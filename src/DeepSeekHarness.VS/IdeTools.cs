using System;
using System.Collections.Generic;
using System.Text;
using DeepSeekHarness.Bridge;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Reports which IDE, workspace and process the bridge is attached to.
    /// </summary>
    /// <remarks>
    /// The "am I wired up?" tool. The agent cannot otherwise tell whether its edits are
    /// landing in the project the user is looking at, and describing the far end makes an
    /// integration much easier to debug.
    /// </remarks>
    internal sealed class EnvironmentTool : IIdeTool
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IWorkspaceMatcher _matcher;

        public EnvironmentTool(IServiceProvider serviceProvider, IWorkspaceMatcher matcher)
        {
            _serviceProvider = serviceProvider;
            _matcher = matcher;
        }

        public string Name { get { return "get_environment"; } }

        public string Description
        {
            get
            {
                return "Report the Visual Studio instance and workspace this session is attached to: " +
                       "solution path, workspace folder, process id. Use it to confirm that edits are " +
                       "landing in the project you are working on.";
            }
        }

        public string InputSchemaJson { get { return ToolSchema.None(); } }

        public string Invoke(IReadOnlyDictionary<string, string> arguments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var report = new StringBuilder();
            report.AppendLine("Visual Studio");

            try
            {
                var solution = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution;
                string directory, file, userOptions;
                if (solution != null &&
                    solution.GetSolutionInfo(out directory, out file, out userOptions) >= 0)
                {
                    report.AppendLine("  solution : " + (string.IsNullOrEmpty(file) ? "(none open)" : file));
                    report.AppendLine("  folder   : " + (directory ?? "(unknown)"));
                }
                else
                {
                    report.AppendLine("  solution : (unavailable)");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("  solution : (error: " + ex.Message + ")");
            }

            report.AppendLine("  process  : " + System.Diagnostics.Process.GetCurrentProcess().Id);

            var folders = new List<string>(_matcher.WorkspaceFolders);
            report.AppendLine("  workspace: " + (folders.Count > 0 ? string.Join("; ", folders.ToArray()) : "(none)"));

            return report.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Lists the files open in the editor, flagging the one the user is looking at.
    /// </summary>
    /// <remarks>
    /// Cheap orientation in a large solution: "what is the user working on right now",
    /// without walking directories or guessing from recent edits.
    ///
    /// The active document is identified through the selection container, which reports the
    /// hierarchy and item id of the focused window; the running document table then maps
    /// that back to a file. That avoids the newer editor API, which lives in NuGet packages
    /// this extension deliberately does not take.
    /// </remarks>
    internal sealed class OpenFilesTool : IIdeTool
    {
        private readonly IServiceProvider _serviceProvider;

        public OpenFilesTool(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public string Name { get { return "get_open_files"; } }

        public string Description
        {
            get
            {
                return "List the files currently open in the Visual Studio editor, marking the active one. " +
                       "Use it to orient yourself in a large solution without listing directories.";
            }
        }

        public string InputSchemaJson { get { return ToolSchema.None(); } }

        public string Invoke(IReadOnlyDictionary<string, string> arguments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var activePath = FindActiveDocumentPath();
            var rdt = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;

            if (rdt == null)
                return activePath == null ? "No documents are open." : "Active: " + activePath;

            IEnumRunningDocuments enumerator;
            if (rdt.GetRunningDocumentsEnum(out enumerator) != 0 || enumerator == null)
                return activePath == null ? "No documents are open." : "Active: " + activePath;

            var open = new List<string>();
            var cookies = new uint[16];
            uint fetched;

            while (enumerator.Next((uint)cookies.Length, cookies, out fetched) == 0 && fetched > 0)
            {
                for (var i = 0u; i < fetched; i++)
                {
                    uint flags, readLocks, editLocks;
                    string moniker;
                    IVsHierarchy hierarchy;
                    uint itemId;
                    IntPtr docData;
                    if (rdt.GetDocumentInfo(cookies[i], out flags, out readLocks, out editLocks,
                                            out moniker, out hierarchy, out itemId, out docData) != 0)
                        continue;

                    if (string.IsNullOrEmpty(moniker)) continue;

                    var line = (string.Equals(moniker, activePath, StringComparison.OrdinalIgnoreCase) ? "* " : "  ") + moniker;
                    if (!open.Contains(line)) open.Add(line);
                }

                if (fetched < cookies.Length) break;
            }

            if (open.Count == 0) return "No documents are open.";

            var report = new StringBuilder();
            report.AppendLine(open.Count + " open document(s) (* = active):");
            foreach (var line in open) report.AppendLine(line);
            return report.ToString().TrimEnd();
        }

        /// <summary>
        /// File path of the focused document, or null. Read from the selection container's
        /// hierarchy and item id, then resolved to a moniker.
        /// </summary>
        private string FindActiveDocumentPath()
        {
            try
            {
                var monitorSelection = _serviceProvider.GetService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
                if (monitorSelection == null) return null;

                IntPtr hierarchyPointer;
                uint itemId;
                IVsMultiItemSelect multiSelect;
                IntPtr selectionContainer;

                if (monitorSelection.GetCurrentSelection(out hierarchyPointer, out itemId, out multiSelect, out selectionContainer) != 0)
                    return null;

                if (hierarchyPointer == IntPtr.Zero) return null;

                try
                {
                    var hierarchy = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(hierarchyPointer) as IVsHierarchy;
                    if (hierarchy == null) return null;

                    string name;
                    if (hierarchy.GetCanonicalName(itemId, out name) != 0) return null;

                    return string.IsNullOrEmpty(name) ? null : name;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.Release(hierarchyPointer);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
