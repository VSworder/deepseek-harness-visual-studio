using System;
using System.Collections.Generic;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// Body of <c>POST /permission</c>: the complete proposed content of one file the
    /// model wants to change.
    /// </summary>
    /// <remarks>
    /// The gate plugin reconstructs <see cref="NewContents"/> from the tool call (full file
    /// contents for a write, or by applying old/new pairs for an edit, insert or create) so
    /// the bridge never has to know which tool produced the change.
    /// </remarks>
    public sealed class PermissionRequest
    {
        /// <summary>Absolute path of the file the model wants to change.</summary>
        public string FilePath { get; set; }

        /// <summary>
        /// The file's content as the plugin read it, when it supplied one.
        /// </summary>
        /// <remarks>
        /// Preferred over reading the file here: the plugin runs inside the session that is
        /// about to write, so its read is the one the edit was computed against. A read on
        /// this side could see a different revision and show the reviewer a diff against
        /// something the tool never saw.
        /// </remarks>
        public string CurrentContents { get; set; }

        /// <summary>The complete proposed content, not a patch.</summary>
        public string NewContents { get; set; }

        /// <summary>The agent session's working directory, used for workspace matching.</summary>
        public string Cwd { get; set; }

        /// <summary>DSH's permission mode for the session; empty when not supplied.</summary>
        public string PermissionMode { get; set; }

        /// <summary>Transcript path if DSH supplied one; empty otherwise.</summary>
        public string TranscriptPath { get; set; }

        /// <summary>The hook process id, so the bridge can tell sessions apart.</summary>
        public int Pid { get; set; }

        /// <summary>
        /// The harness call this proposal belongs to, when the plugin supplied one.
        /// </summary>
        /// <remarks>
        /// Used to recognise the same call arriving twice. That happens when the gate is
        /// mounted more than once - the extension mounts it, and the user may also have
        /// installed it into their profile - and without this the reviewer would be shown
        /// two diffs for one edit.
        /// </remarks>
        public string CallId { get; set; }
    }

    /// <summary>What the user decided in the diff window, or that the bridge declined to gate.</summary>
    public sealed class PermissionDecision
    {
        /// <summary>True when the user accepted the proposed content.</summary>
        public bool Accept { get; private set; }

        /// <summary>Why the user rejected; surfaced to the model as the tool error text.</summary>
        public string Reason { get; private set; }

        /// <summary>
        /// True when this bridge declines to gate the session. The hook then emits no
        /// decision at all, so DSH's own permission flow runs instead — the gate must
        /// never be stricter than the user's configuration.
        /// </summary>
        public bool Ask { get; private set; }

        private PermissionDecision() { }

        public static PermissionDecision Accepted()
        {
            return new PermissionDecision { Accept = true };
        }

        public static PermissionDecision Rejected(string reason)
        {
            return new PermissionDecision
            {
                Accept = false,
                Reason = string.IsNullOrWhiteSpace(reason) ? "Rejected in the Visual Studio diff" : reason
            };
        }

        public static PermissionDecision DeclineToGate(string reason)
        {
            return new PermissionDecision { Ask = true, Reason = reason };
        }

        /// <summary>Renders the JSON body the hook expects.</summary>
        public string ToJson()
        {
            if (Ask) return "{\"ask\":true}";
            if (Accept) return "{\"accept\":true}";
            return "{\"accept\":false,\"reason\":" + JsonEncode(Reason) + "}";
        }

        internal static string JsonEncode(string value)
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

    /// <summary>
    /// Decides whether this bridge owns the session the request came from.
    /// </summary>
    /// <remarks>
    /// Ownership matters because one machine can run several Visual Studio instances and
    /// several agent sessions. A bridge answers <c>ask</c> (declines to gate) rather than
    /// opening a diff that does not belong to it, so two IDEs never fight over one session
    /// and a user's own pre-approval mode is never overridden by a forced prompt.
    /// </remarks>
    public interface IWorkspaceMatcher
    {
        /// <summary>
        /// Returns true when <paramref name="cwd"/> belongs to this bridge's workspace.
        /// </summary>
        bool Owns(string cwd);

        /// <summary>The workspace folders this bridge advertises in its lock file.</summary>
        IEnumerable<string> WorkspaceFolders { get; }
    }

    /// <summary>
    /// Default ownership rule: exact match, session inside workspace, or workspace inside
    /// session, compared with separators folded and (on Windows) case ignored.
    /// </summary>
    public sealed class PathWorkspaceMatcher : IWorkspaceMatcher
    {
        private readonly List<string> _folders = new List<string>();

        public PathWorkspaceMatcher(IEnumerable<string> workspaceFolders)
        {
            if (workspaceFolders == null) return;
            foreach (var folder in workspaceFolders)
            {
                var normalized = Normalize(folder);
                if (normalized.Length > 0) _folders.Add(normalized);
            }
        }

        public IEnumerable<string> WorkspaceFolders => _folders;

        public bool Owns(string cwd)
        {
            var normalized = Normalize(cwd);
            if (normalized.Length == 0) return false;

            foreach (var folder in _folders)
            {
                if (normalized.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
                if (normalized.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                if (folder.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return path.Replace('/', '\\').TrimEnd('\\');
        }
    }
}
