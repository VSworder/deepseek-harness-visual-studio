using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DeepSeekHarness.Bridge;
using DeepSeekHarness.Setup;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Answers "is the gate actually armed?" by inspecting the installation rather than
    /// trusting the session.
    /// </summary>
    /// <remarks>
    /// Necessary because the interesting failure is silent: a session whose bridge never
    /// loaded still starts, still runs the agent, and still edits files — it just never
    /// asks. Every check below therefore looks for positive evidence, and the report always
    /// ends with where the log is and what to do next.
    /// </remarks>
    internal sealed class StatusReporter
    {
        private readonly Action<string> _log;

        public StatusReporter(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>Builds the plain-text report shown to the user.</summary>
        public string BuildReport()
        {
            var report = new StringBuilder();

            report.AppendLine("DeepSeek Harness for Visual Studio");
            report.AppendLine(new string('=', 50));
            report.AppendLine();

            var ready = ReportGate(report);
            ReportBridge(report);
            ReportLogTail(report);

            report.AppendLine();
            report.AppendLine(ready
                ? "Result: the gate is armed. Edits wait for the diff window."
                : "Result: the gate is NOT armed. Edits will be written without asking.");

            return report.ToString();
        }

        /// <summary>
        /// Whether a file exists and is non-empty. A zero-byte plugin would load as an empty
        /// module, which reports as installed and gates nothing.
        /// </summary>
        private static bool IsUsableFile(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        /// <summary>Returns true when a gated session could actually be started.</summary>
        private bool ReportGate(StringBuilder report)
        {
            report.AppendLine("Diff gate");

            var tuiCommand = DshLocator.FindTuiCommand();

            var pluginEntry = BridgeInstaller.PluginEntryPath;
            var patch = BridgeInstaller.HookPatchPath;

            var pluginOk = IsUsableFile(pluginEntry);
            var patchOk = File.Exists(patch);

            Line(report, "gate plugin", pluginOk ? pluginEntry : "MISSING");
            Line(report, "bridge patch", patchOk ? patch : "MISSING");
            report.AppendLine();

            Line(report, "tui launcher", tuiCommand ?? "NOT FOUND");
            Line(report, "dsh launcher", DshLocator.FindDshCommand() ?? "NOT FOUND");

            var ready = tuiCommand != null && pluginOk && patchOk;

            if (!ready)
            {
                report.AppendLine();
                report.AppendLine("  A session started now would run WITHOUT the diff gate.");
                report.AppendLine("  Fix what is missing above:");
                report.AppendLine();
                if (!pluginOk)
                    report.AppendLine("      restart Visual Studio to rewrite the gate plugin");
                if (tuiCommand == null)
                    report.AppendLine("      npm install -g @deepseek-harness-tui/dsh-tui");
                report.AppendLine("      then restart Visual Studio");
                report.AppendLine();
            }

            report.AppendLine();
            return ready;
        }

        private void ReportBridge(StringBuilder report)
        {
            report.AppendLine("Bridge endpoint");

            var directory = BridgeLock.DefaultDirectory;
            Line(report, "lock dir", directory);

            var locks = new List<string>();
            try
            {
                if (Directory.Exists(directory))
                    locks.AddRange(Directory.GetFiles(directory, "*.lock"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (locks.Count == 0)
            {
                Line(report, "lock file", "none (no Visual Studio instance is listening)");
            }
            else
            {
                Line(report, "lock file", locks.Count == 1
                    ? Path.GetFileName(locks[0])
                    : locks.Count + " instances: " + string.Join(", ", locks.Select(Path.GetFileName)));
            }

            report.AppendLine();
        }

        private void ReportLogTail(StringBuilder report)
        {
            report.AppendLine("Recent log");

            var path = LogPath;
            Line(report, "file", path);

            try
            {
                if (!File.Exists(path))
                {
                    report.AppendLine("  (no log written yet)");
                    report.AppendLine();
                    return;
                }

                // Read with sharing: the package appends to this file while it runs.
                string[] lines;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var all = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null) all.Add(line);
                    lines = all.ToArray();
                }

                var tail = lines.Length > 20 ? lines.Skip(lines.Length - 20).ToArray() : lines;
                foreach (var line in tail) report.AppendLine("  " + line);
            }
            catch (Exception ex)
            {
                report.AppendLine("  (could not read the log: " + ex.Message + ")");
            }

            report.AppendLine();
        }

        private static void Line(StringBuilder report, string label, string value)
        {
            report.Append("  ");
            report.Append(label.PadRight(16));
            report.Append(value ?? "(null)");
            report.AppendLine();
        }

        /// <summary>Full path of the extension log.</summary>
        internal static string LogPath =>
            Path.Combine(BridgeInstaller.RootDirectory, "vs-extension.log");

        /// <summary>Writes the report to the log, so a bug report carries it.</summary>
        public void LogReport(string report)
        {
            _log("status report requested:");
            foreach (var line in report.Split('\n')) _log("  " + line.TrimEnd('\r'));
        }
    }
}
