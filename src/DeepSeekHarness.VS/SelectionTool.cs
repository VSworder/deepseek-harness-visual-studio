using System;
using System.Collections.Generic;
using System.Text;
using DeepSeekHarness.Bridge;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Returns the text currently selected in the editor.
    /// </summary>
    /// <remarks>
    /// Saves the user from describing what they are looking at, and reads the editor buffer
    /// so unsaved edits are included: what is on screen is what the model gets.
    ///
    /// Built on the classic text-manager contracts (<see cref="IVsTextView"/> and
    /// <see cref="IVsTextLines"/>) rather than the newer editor API, which lives in NuGet
    /// packages this extension deliberately does not take.
    /// </remarks>
    internal sealed class SelectionTool : IIdeTool
    {
        /// <summary>Upper bound on returned text, so one selection cannot flood the context.</summary>
        private const int MaxCharacters = 8000;

        private readonly IServiceProvider _serviceProvider;

        public SelectionTool(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public string Name { get { return "get_current_selection"; } }

        public string Description
        {
            get
            {
                return "Return the text currently selected in the active editor, with its file path and " +
                       "line range. Returns a message instead when nothing is selected. Prefer this over " +
                       "asking the user to paste code.";
            }
        }

        public string InputSchemaJson { get { return ToolSchema.None(); } }

        public string Invoke(IReadOnlyDictionary<string, string> arguments)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string filePath;
            var view = ActiveEditor.Find(_serviceProvider, out filePath);

            if (view == null)
                return "No active text editor: " + (ActiveEditor.LastFailure ?? "unknown reason");

            var file = filePath ?? "(unsaved document)";

            int anchorLine, anchorColumn, endLine, endColumn;
            if (view.GetSelection(out anchorLine, out anchorColumn, out endLine, out endColumn) != 0)
                return DescribeCaret(view, file);

            // A zero-width span is a caret, not a selection. Falling back to the caret's line
            // is more useful than an empty answer: the agent gets somewhere to start, and the
            // user does not have to re-select just to ask about the line they are on.
            if (anchorLine == endLine && anchorColumn == endColumn)
                return DescribeCaret(view, file);

            var text = ReadSelection(view, anchorLine, anchorColumn, endLine, endColumn);

            if (string.IsNullOrEmpty(text))
                return "Nothing is selected in " + (filePath ?? "the active editor") + ".";

            var truncated = string.Empty;
            if (text.Length > MaxCharacters)
            {
                text = text.Substring(0, MaxCharacters);
                truncated = "\n\n[truncated at " + MaxCharacters + " characters]";
            }

            // The view reports 0-based lines; the transcript reads better 1-based.
            return "Selected from " + (filePath ?? "(unsaved document)") +
                   ", lines " + (anchorLine + 1) + "-" + (endLine + 1) +
                   " (" + (endLine - anchorLine + 1) + " line(s)):\n\n" +
                   text + truncated;
        }

        /// <summary>
        /// Reports the caret's line when nothing is selected. The agent still needs to know
        /// where the user is looking, and asking them to select text first is friction this
        /// tool exists to remove.
        /// </summary>
        private static string DescribeCaret(IVsTextView view, string file)
        {
            int line, column;
            if (view.GetCaretPos(out line, out column) != 0)
                return "Nothing is selected in " + file + ", and the caret position is unavailable.";

            IVsTextLines buffer;
            if (view.GetBuffer(out buffer) != 0 || buffer == null)
                return "Nothing is selected in " + file + " (caret at line " + (line + 1) + ").";

            // Read the caret's whole line: a bare caret offset is not worth a round trip.
            string text;
            if (buffer.GetLineText(line, 0, line, -1, out text) != 0 || text == null)
                return "Nothing is selected in " + file + " (caret at line " + (line + 1) + ").";

            return "Nothing is selected. The caret is on " + file + ", line " + (line + 1) +
                   ":\n\n" + text.TrimEnd('\r', '\n');
        }
        /// <summary>
        /// Reads the selected span. <see cref="IVsTextView.GetSelection"/> yields coordinates
        /// only, so the text comes from the underlying buffer.
        /// </summary>
        private static string ReadSelection(IVsTextView view, int startLine, int startColumn, int endLine, int endColumn)
        {
            IVsTextLines buffer;
            if (view.GetBuffer(out buffer) != 0 || buffer == null) return null;

            string text;
            if (buffer.GetLineText(startLine, startColumn, endLine, endColumn, out text) == 0)
                return text;

            return null;
        }
    }
}
