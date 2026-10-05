using System;
using System.Collections.Generic;
using System.Text;
using DeepSeekHarness.Bridge;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

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
                return activePath == null
                    ? "No active document: " + (ActiveEditor.LastFailure ?? "unknown reason")
                    : "Active: " + activePath;

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

    /// <summary>
    /// Shared lookup for "the editor the user is looking at", used by more than one tool.
    /// </summary>
    internal static class ActiveEditor
    {
        /// <summary>
        /// Resolves the active text view and its file path. The path comes from the window
        /// frame, so a buffer that was never saved still reports where it belongs.
        /// </summary>
        public static IVsTextView Find(IServiceProvider serviceProvider, out string filePath)
        {
            filePath = null;

            try
            {
                var textManager = serviceProvider.GetService(typeof(SVsTextManager)) as IVsTextManager;
                if (textManager == null)
                {
                    LastFailure = "SVsTextManager is unavailable to this package";
                    return null;
                }

                // fMustHaveFocus = 0 is the whole point. The agent is told about a selection
                // while the user is typing in the terminal, so the editor never has focus at
                // the moment this runs. Asking for "the last active view" instead of "the
                // focused view" is what makes the tool work at all; asking for focus returns
                // the terminal's frame and no text view.
                IVsTextView view;
                var hr = textManager.GetActiveView(0, null, out view);
                if (hr != 0 || view == null)
                {
                    LastFailure = "no active text view (0x" + hr.ToString("x8") +
                                  "); open a file in the editor and click in it once";
                    return null;
                }

                filePath = ResolvePath(view);
                LastFailure = null;
                return view;
            }
            catch (Exception ex)
            {
                LastFailure = "resolution threw: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// File path behind a text view, or null. The view exposes its buffer, and the running
        /// document table maps that buffer back to a moniker.
        /// </summary>
        private static string ResolvePath(IVsTextView view)
        {
            try
            {
                IVsTextLines buffer;
                if (view.GetBuffer(out buffer) != 0 || buffer == null) return null;

                var provider = buffer as IVsUserData;
                if (provider == null) return null;

                var key = typeof(IVsUserData).GUID;
                object moniker;
                if (provider.GetData(ref key, out moniker) != 0) return null;

                return moniker as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
        /// <summary>
        /// Why the last <see cref="Find"/> returned nothing, for the tool to report.
        /// </summary>
        /// <remarks>
        /// Added after both editor tools returned the same unhelpful "no active editor" text.
        /// The resolution has four failure points and they need different fixes, so the tool
        /// now says which one fired instead of collapsing them into one message.
        /// </remarks>
        public static string LastFailure { get; private set; }    }
}