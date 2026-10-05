using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// The Model Context Protocol surface the agent talks to: it exposes Visual Studio
    /// state as MCP tools.
    /// </summary>
    /// <remarks>
    /// Protocol shape verified against the client DeepSeek Harness uses
    /// (<c>@modelcontextprotocol/client</c> 2.0.0, the same SDK generation as the MCP
    /// 2025-06-18 revision):
    ///
    /// - Every message is a POST of one JSON-RPC object to the endpoint.
    /// - The client advertises <c>accept: application/json, text/event-stream</c>, so a
    ///   plain JSON response is a valid reply; SSE is optional and not implemented here.
    /// - Messages without an <c>id</c> are notifications and expect no body, so they get 202.
    /// - The session header is optional: leaving it out means stateless, which is all this
    ///   server needs.
    ///
    /// Tool names are the raw names; DeepSeek Harness publishes them to the model as
    /// <c>mcp__&lt;serverName&gt;__&lt;rawName&gt;</c>.
    /// </remarks>
    public sealed class McpEndpoint
    {
        /// <summary>Revision reported during initialization.</summary>
        private const string ProtocolVersion = "2025-06-18";

        private readonly Func<IIdeTool[]> _toolProvider;
        private readonly Action<string> _log;

        /// <summary>
        /// Runs a tool on the thread that owns Visual Studio state. Set by the host; when it
        /// is missing, tool calls are refused instead of run on the wrong thread.
        /// </summary>
        public Func<IIdeTool, IReadOnlyDictionary<string, string>, string> ToolInvoker { get; set; }

        public McpEndpoint(Func<IIdeTool[]> toolProvider, Action<string> log)
        {
            _toolProvider = toolProvider ?? (() => new IIdeTool[0]);
            _log = log ?? (_ => { });
        }

        /// <summary>What to send back for one request body.</summary>
        public sealed class Result
        {
            public int Status { get; set; }
            public string Body { get; set; }
        }

        /// <summary>
        /// Handles one JSON-RPC message. Never throws: a malformed message becomes a JSON-RPC
        /// error so the client sees a protocol answer rather than a broken connection.
        /// </summary>
        public Result Handle(string body)
        {
            string id = null;
            var isNotification = false;

            try
            {
                var method = JsonRpc.ReadString(body, "method");
                if (method == null)
                    return Error(null, -32600, "invalid request: no method");

                // A message without an id is a notification: answer with no content.
                isNotification = !JsonRpc.HasMember(body, "id");
                if (!isNotification) id = JsonRpc.ReadRawMember(body, "id");

                switch (method)
                {
                    case "initialize":
                        return Ok(id, InitializeResult());

                    // Notifications carry no id and expect no body.
                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return Accepted();

                    // The 2026-era client asks the server to describe itself. Answering with
                    // the same capabilities as initialize is enough: the client fell back
                    // gracefully without it, but leaving it unsupported would log noise on
                    // every connection.
                    case "server/discover":
                        return Ok(id, InitializeResult());

                    case "ping":
                        return Ok(id, "{}");

                    case "tools/list":
                        return Ok(id, ToolsListResult());

                    case "tools/call":
                        return Ok(id, ToolsCallResult(JsonRpc.ReadRawMember(body, "params")));

                    default:
                        _log("mcp: unsupported method " + method);
                        return Error(id, -32601, "method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                _log("mcp: request failed: " + ex);
                return Error(isNotification ? null : id, -32603, ex.Message);
            }
        }

        private string InitializeResult()
        {
            return "{\"protocolVersion\":" + JsonRpc.Quote(ProtocolVersion) + "," +
                   "\"capabilities\":{\"tools\":{}}," +
                   "\"serverInfo\":{\"name\":\"visualstudio\",\"version\":\"1.0.0\"}}";
        }

        private string ToolsListResult()
        {
            var tools = _toolProvider() ?? new IIdeTool[0];

            var json = new StringBuilder("{\"tools\":[");
            for (var i = 0; i < tools.Length; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"name\":").Append(JsonRpc.Quote(tools[i].Name))
                    .Append(",\"description\":").Append(JsonRpc.Quote(tools[i].Description))
                    .Append(",\"inputSchema\":").Append(tools[i].InputSchemaJson)
                    .Append('}');
            }
            json.Append("]}");

            _log("mcp: listed " + tools.Length + " tool(s)");
            return json.ToString();
        }

        private string ToolsCallResult(string paramsJson)
        {
            var name = JsonRpc.ReadString(paramsJson, "name");
            if (string.IsNullOrEmpty(name))
                return ErrorResult("tools/call requires a name");

            var tool = (_toolProvider() ?? new IIdeTool[0])
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

            if (tool == null)
                return ErrorResult("unknown tool: " + name);

            var arguments = JsonRpc.ReadArguments(JsonRpc.ReadRawMember(paramsJson, "arguments"));

            try
            {
                var invoker = ToolInvoker;
                if (invoker == null)
                    return ErrorResult("this Visual Studio instance serves no tools");

                var text = invoker(tool, arguments) ?? string.Empty;
                _log("mcp: " + name + " returned " + text.Length + " chars");
                return TextResult(text, isError: false);
            }
            catch (Exception ex)
            {
                // A failing tool is reported to the model, not to the transport.
                _log("mcp: " + name + " failed: " + ex.Message);
                return TextResult(ex.Message, isError: true);
            }
        }

        private static string TextResult(string text, bool isError)
        {
            return "{\"content\":[{\"type\":\"text\",\"text\":" + JsonRpc.Quote(text) + "}]" +
                   (isError ? ",\"isError\":true" : string.Empty) + "}";
        }

        private static string ErrorResult(string message)
        {
            return TextResult(message, isError: true);
        }

        private static Result Ok(string id, string resultJson)
        {
            return new Result
            {
                Status = 200,
                Body = "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"result\":" + resultJson + "}"
            };
        }

        private static Result Error(string id, int code, string message)
        {
            return new Result
            {
                Status = 200,
                Body = "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"error\":{\"code\":" + code +
                       ",\"message\":" + JsonRpc.Quote(message) + "}}"
            };
        }

        /// <summary>Notifications get no body, per the transport's expectations.</summary>
        private static Result Accepted()
        {
            return new Result { Status = 202, Body = null };
        }
    }
}
