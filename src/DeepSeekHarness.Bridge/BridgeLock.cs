using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// One JSON document per Visual Studio instance, written to
    /// <c>%LOCALAPPDATA%\DeepSeekHarness\vs-bridge\&lt;port&gt;.lock</c>.
    /// The hook reads these to discover a live bridge.
    /// </summary>
    /// <remarks>
    /// The file name carries the port, and <see cref="Pid"/> lets the hook skip locks
    /// whose owning process is gone. Serialization is hand-rolled to keep this assembly
    /// dependency-free (it must load inside devenv with nothing else shipped).
    /// </remarks>
    public sealed class BridgeLock
    {
        public int Port { get; set; }
        public string AuthToken { get; set; }
        public int Pid { get; set; }
        public IList<string> WorkspaceFolders { get; set; } = new List<string>();

        /// <summary>Directory the hook scans. Created on demand by <see cref="WriteTo"/>.</summary>
        public static string DefaultDirectory
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "DeepSeekHarness", "vs-bridge");
            }
        }

        /// <summary>Full path of the lock file for <see cref="Port"/> inside <paramref name="directory"/>.</summary>
        public static string PathFor(string directory, int port)
        {
            return Path.Combine(directory, port.ToString() + ".lock");
        }

        /// <summary>Writes (or overwrites) this lock into <paramref name="directory"/>.</summary>
        public void WriteTo(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(PathFor(directory, Port), ToJson(), Utf8NoBom);
        }

        /// <summary>Deletes the lock file, ignoring the case where it is already gone.</summary>
        public void Delete(string directory)
        {
            try
            {
                var path = PathFor(directory, Port);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* a leftover lock is harmless: the hook probes liveness */ }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Removes lock files whose <c>pid</c> no longer exists. Called at startup so a
        /// crashed Visual Studio does not leave locks that keep winning discovery.
        /// </summary>
        public static void ReapStale(string directory)
        {
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.GetFiles(directory, "*.lock"))
            {
                // Catch-all on purpose. This runs as the first statement of bridge startup, so
                // anything escaping it means no listener, no lock and no refreshed hook - the
                // extension silently does nothing while the user believes the gate is armed.
                // One unreadable lock file is not worth that: a lock that cannot be examined
                // is left alone and simply loses discovery on the liveness probe.
                try
                {
                    var pid = ReadPid(File.ReadAllText(file));
                    if (pid > 0 && !ProcessExists(pid)) File.Delete(file);
                }
                catch (Exception) { }
            }
        }

        internal static bool ProcessExists(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }        // no such process
            catch (InvalidOperationException) { return false; }
            // HasExited throws Win32Exception ("Access is denied") for a process this one
            // cannot open, such as a service. Treating that as "gone" would delete a live
            // lock, and letting it escape would abort startup, so an unreadable process
            // counts as alive: the liveness probe decides.
            catch (System.ComponentModel.Win32Exception) { return true; }
            catch (Exception) { return true; }
        }

        internal static int ReadPid(string json)
        {
            var m = Regex.Match(json ?? string.Empty, "\"pid\"\\s*:\\s*(\\d+)");
            if (!m.Success) return -1;
            int pid;
            return int.TryParse(m.Groups[1].Value, out pid) ? pid : -1;
        }

        internal static readonly System.Text.UTF8Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        internal string ToJson()
        {
            var folders = (WorkspaceFolders ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(Escape)
                .ToArray();

            return "{" +
                   "\"port\":" + Port + "," +
                   "\"authToken\":" + Escape(AuthToken) + "," +
                   "\"pid\":" + Pid + "," +
                   "\"workspaceFolders\":[" + string.Join(",", folders) + "]" +
                   "}";
        }

        private static string Escape(string value)
        {
            if (value == null) return "null";

            var sb = new System.Text.StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
