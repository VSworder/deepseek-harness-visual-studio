using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// Loopback HTTP endpoint the DeepSeek Harness permission hook talks to.
    /// Binds to 127.0.0.1 on the first free port and validates a bearer token on
    /// every request, so no other local process can drive the Visual Studio diff UI.
    /// </summary>
    /// <remarks>
    /// Hand-rolled over <see cref="HttpListener"/> on purpose: the assembly must ship
    /// inside a VSIX with zero NuGet dependencies and load in the devenv process.
    /// </remarks>
    public sealed class BridgeServer : IDisposable
    {
        /// <summary>Header the hook uses to present the lock file's auth token.</summary>
        public const string AuthHeader = "x-dsh-vs-authorization";

        private readonly HttpListener _listener = new HttpListener();
        private readonly string _authToken;
        private readonly IWorkspaceMatcher _workspaceMatcher;
        private readonly Action<string> _log;
        private readonly McpEndpoint _mcp;
        private bool _disposed;

        /// <summary>
        /// Handles one gated change. Implementations show the diff UI and return the
        /// user's decision; they must not throw for user-driven outcomes.
        /// </summary>
        public Func<PermissionRequest, Task<PermissionDecision>> PermissionHandler { get; set; }

        /// <summary>
        /// Supplies the IDE tools. Called per request so tools can appear and disappear with
        /// the solution; implementations run on the UI thread <see cref="ToolInvoker"/> provides.
        /// </summary>
        public Func<IIdeTool[]> ToolProvider { get; set; }

        /// <summary>
        /// Runs one tool invocation on the UI thread. Without it, tool calls are refused
        /// rather than run off-thread, because every tool touches Visual Studio state.
        /// </summary>
        public Func<IIdeTool, IReadOnlyDictionary<string, string>, string> ToolInvoker { get; set; }

        /// <summary>Port actually bound. Valid only after <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>Token a hook must present. Valid only after construction.</summary>
        public string AuthToken => _authToken;

        public BridgeServer(IWorkspaceMatcher workspaceMatcher, Action<string> log = null, string authToken = null)
        {
            _workspaceMatcher = workspaceMatcher ?? throw new ArgumentNullException(nameof(workspaceMatcher));
            _log = log ?? (_ => { });
            _authToken = string.IsNullOrEmpty(authToken) ? Guid.NewGuid().ToString("D") : authToken;

            // Tools are resolved lazily: the package builds them after the solution loads.
            _mcp = new McpEndpoint(() => ToolProvider?.Invoke() ?? new IIdeTool[0], _log);
            _mcp.ToolInvoker = (tool, args) => ToolInvoker?.Invoke(tool, args)
                ?? throw new InvalidOperationException("this Visual Studio instance serves no tools");
        }

        /// <summary>
        /// Binds the first free loopback port and starts serving. Throws when no port
        /// could be bound; callers should treat that as "the gate is unavailable".
        /// </summary>
        public void Start(int preferredPort = 0)
        {
            Port = preferredPort > 0 ? preferredPort : FindFreePort();

            // HttpListener needs the trailing slash and an explicit host. Loopback only:
            // a wildcard prefix would trigger a Windows Firewall prompt and widen exposure.
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Start();
            _listener.BeginGetContext(OnContext, null);
            _log("bridge listening on http://127.0.0.1:" + Port + "/");
        }

        /// <summary>Probes a port by binding it, so two instances never race for one lock file name.</summary>
        public static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private void OnContext(IAsyncResult ar)
        {
            if (_disposed) return;

            HttpListenerContext context = null;
            try
            {
                context = _listener.EndGetContext(ar);
            }
            catch (HttpListenerException) { return; }   // listener stopped
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }

            // Keep accepting while this request is handled.
            try { _listener.BeginGetContext(OnContext, null); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }

            // Handle off the accept thread: the diff decision can take hours.
            Task.Run(() => HandleAsync(context));
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var path = (context.Request.Url?.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";

                if (!IsAuthorized(context.Request))
                {
                    _log("rejected unauthorized request to " + path);
                    await WriteJsonAsync(context, 401, "{\"error\":\"unauthorized\"}").ConfigureAwait(false);
                    return;
                }

                switch (path)
                {
                    case "/health":
                        await WriteJsonAsync(context, 200, "{\"ok\":true,\"port\":" + Port + "}").ConfigureAwait(false);
                        return;

                    case "/permission":
                        await HandlePermissionAsync(context).ConfigureAwait(false);
                        return;

                    case "/mcp":
                        await HandleMcpAsync(context).ConfigureAwait(false);
                        return;

                    default:
                        await WriteJsonAsync(context, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception ex)
            {
                _log("request failed: " + ex);
                try { await WriteJsonAsync(context, 500, "{\"error\":\"internal\"}").ConfigureAwait(false); }
                catch { /* client already gone */ }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// Serves the Model Context Protocol surface. One JSON-RPC message per POST; the
        /// reply is always JSON, which the client accepts alongside SSE.
        /// </summary>
        private async Task HandleMcpAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var result = _mcp.Handle(body);

            if (result.Body == null)
            {
                context.Response.StatusCode = result.Status;
                context.Response.ContentLength64 = 0;
                return;
            }

            await WriteJsonAsync(context, result.Status, result.Body).ConfigureAwait(false);
        }

        private async Task HandlePermissionAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            PermissionRequest request;
            try
            {
                request = ParsePermissionRequest(body);
            }
            catch (Exception ex)
            {
                _log("malformed /permission body: " + ex.Message);
                await WriteJsonAsync(context, 400, "{\"error\":\"malformed request\"}").ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(request.FilePath))
            {
                await WriteJsonAsync(context, 400, "{\"error\":\"filePath is required\"}").ConfigureAwait(false);
                return;
            }

            // Never gate a session that does not belong to this Visual Studio.
            if (!_workspaceMatcher.Owns(request.Cwd))
            {
                _log("declining to gate session in " + request.Cwd + " (not this workspace)");
                await WriteJsonAsync(context, 200, PermissionDecision.DeclineToGate("not this workspace").ToJson())
                    .ConfigureAwait(false);
                return;
            }

            var handler = PermissionHandler;
            if (handler == null)
            {
                await WriteJsonAsync(context, 503, PermissionDecision.DeclineToGate("no handler").ToJson())
                    .ConfigureAwait(false);
                return;
            }

            PermissionDecision decision;
            try
            {
                decision = await handler(request).ConfigureAwait(false) ?? PermissionDecision.DeclineToGate("no decision");
            }
            catch (Exception ex)
            {
                // A crashed UI must not block the agent: decline, so DSH's own flow runs.
                _log("permission handler threw: " + ex);
                decision = PermissionDecision.DeclineToGate("handler failed");
            }

            await WriteJsonAsync(context, 200, decision.ToJson()).ConfigureAwait(false);
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var presented = request.Headers[AuthHeader];
            if (string.IsNullOrEmpty(presented) || presented.Length != _authToken.Length) return false;

            // Fixed-time compare: the token is loopback-only, but there is no reason to leak
            // its length or prefix through timing.
            var diff = 0;
            for (var i = 0; i < presented.Length; i++) diff |= presented[i] ^ _authToken[i];
            return diff == 0;
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Minimal JSON reader for the known request shape. Kept dependency-free; a
        /// malformed body throws so the endpoint can answer 400 instead of guessing.
        /// </summary>
        internal static PermissionRequest ParsePermissionRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("empty body");
            if (json.TrimStart()[0] != '{') throw new FormatException("body is not a JSON object");

            return new PermissionRequest
            {
                FilePath = ReadString(json, "filePath"),
                CurrentContents = ReadString(json, "currentContents"),
                NewContents = ReadString(json, "newContents"),
                Cwd = ReadString(json, "cwd"),
                PermissionMode = ReadString(json, "permissionMode"),
                TranscriptPath = ReadString(json, "transcriptPath"),
                Pid = ReadInt(json, "pid")
            };
        }

        internal static string ReadString(string json, string name)
        {
            var key = "\"" + name + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;

            at = json.IndexOf(':', at + key.Length);
            if (at < 0) return null;
            at++;

            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at >= json.Length) return null;
            if (json[at] == 'n') return null;          // null literal
            if (json[at] != '"') throw new FormatException("field '" + name + "' is not a string");

            var sb = new StringBuilder();
            for (var i = at + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '\\')
                {
                    if (++i >= json.Length) break;
                    var esc = json[i];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            throw new FormatException("unterminated string in field '" + name + "'");
        }

        internal static int ReadInt(string json, string name)
        {
            var key = "\"" + name + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return 0;

            at = json.IndexOf(':', at + key.Length);
            if (at < 0) return 0;
            at++;

            var start = at;
            while (at < json.Length && (char.IsDigit(json[at]) || json[at] == '-')) at++;
            if (at == start) return 0;

            int value;
            return int.TryParse(json.Substring(start, at - start), out value) ? value : 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
