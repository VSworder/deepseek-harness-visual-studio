using System;
using System.IO;
using System.Text;

namespace DeepSeekHarness.Setup
{
    /// <summary>
    /// Finds the user's DeepSeek Harness installation and builds the command line that
    /// starts a session with the bridge attached.
    /// </summary>
    /// <remarks>
    /// The extension attaches to the profile the user already runs rather than owning one.
    /// <c>dsh-tui</c> forwards DSH options such as <c>--patch</c> to the underlying
    /// launcher, so a single extra argument mounts the bridge on a working setup. That
    /// removes three failure modes at once: no profile of our own to create, no package
    /// install to babysit, and no guessing which app name the launcher resolves.
    /// </remarks>
    public static class DshLocator
    {
        /// <summary>File name of the plain CLI launcher.</summary>
        public const string DshCommandName = "dsh.cmd";

        /// <summary>
        /// The TUI launcher. Its help documents that options such as <c>--patch</c> are
        /// forwarded to DSH, which is what this extension relies on.
        /// </summary>
        public const string TuiCommandName = "dsh-tui.cmd";

        /// <summary>Absolute path of the <c>dsh-tui</c> launcher, or null when absent.</summary>
        public static string FindTuiCommand()
        {
            return FindOnPathOrRoots(TuiCommandName);
        }

        /// <summary>Absolute path of the plain <c>dsh</c> launcher, or null when absent.</summary>
        public static string FindDshCommand()
        {
            return FindOnPathOrRoots(DshCommandName);
        }

        private static string FindOnPathOrRoots(string fileName)
        {
            foreach (var root in CandidateRoots())
            {
                try
                {
                    var path = Path.Combine(root, fileName);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { /* malformed root */ }
            }

            // Fall back to a PATH lookup so non-npm installs still work.
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }

            return null;
        }

        /// <summary>
        /// Absolute path of the hook bridge package inside the global dsh installation,
        /// or null when it cannot be found.
        /// </summary>
        public static string FindHooksPackage()
        {
            var dshPackage = FindDshPackageDirectory();
            if (dshPackage == null) return null;

            var path = Path.Combine(dshPackage, "node_modules", "@deepseek-ai", "dsh-hooks-claude-code");
            return Directory.Exists(path) && File.Exists(Path.Combine(path, "package.json")) ? path : null;
        }

        /// <summary>
        /// Directory of the installed <c>@deepseek-ai/dsh</c> package, or null when the CLI
        /// is not installed. Package versions are read from here so the extension never
        /// refers to a version the local installation does not have.
        /// </summary>
        public static string FindDshPackageDirectory()
        {
            foreach (var root in CandidateRoots())
            {
                try
                {
                    var path = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh");
                    if (Directory.Exists(path) && File.Exists(Path.Combine(path, "package.json")))
                        return path;
                }
                catch (ArgumentException) { }
            }

            return null;
        }

        /// <summary>
        /// Full path of the <c>dsh-tui</c> launcher to embed in a terminal profile, or null
        /// when it is not installed.
        /// </summary>
        public static string TuiCommandPath => FindTuiCommand();

        /// <summary>
        /// Arguments for the terminal profile: run the TUI through cmd.exe with the bridge
        /// patch attached.
        /// </summary>
        /// <remarks>
        /// cmd.exe is required because <c>dsh-tui.cmd</c> is a batch launcher, not an
        /// executable: handing the .cmd path straight to the terminal starts a process that
        /// produces no session, which is exactly what was observed before this change.
        /// <c>/k</c> keeps the window open so a failed start can be read instead of the tab
        /// vanishing.
        ///
        /// Quoting follows cmd's rule that the whole command line after /k is wrapped in one
        /// extra pair of quotes when the first token is itself quoted.
        /// </remarks>
        public static string BuildTuiArguments()
        {
            var script = BridgeInstaller.StartScriptPath;

            // One argument, no quoting games. The terminal hands Location and Arguments to
            // CreateProcess, which does no shell parsing, so anything more elaborate than a
            // single token was previously swallowed: the tab opened and cmd.exe sat idle.
            // The script lives under the user profile, so this path has no spaces.
            return "/k \"" + script + "\"";
        }

        /// <summary>
        /// npm global roots to search, most likely first. npm puts launchers in
        /// %APPDATA%\npm on Windows and packages under the same prefix.
        /// </summary>
        private static string[] CandidateRoots()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            return new[]
            {
                Path.Combine(roaming, "npm"),
                Path.Combine(local, "npm"),
                Path.Combine(programFiles, "nodejs"),
                Path.Combine(roaming, "nvm"),
            };
        }

        /// <summary>
        /// Converts a Windows path into the <c>file:///</c> URL form DSH requires when an
        /// entry names a package by location instead of by name.
        /// </summary>
        /// <remarks>
        /// Needed for the bridge: the patch runs against the user's own profile, which does
        /// not declare the bridge as a dependency, so it is addressed by absolute URL.
        /// Nested <c>@scope</c> directories and spaces both survive the encoding.
        /// </remarks>
        public static string ToFileUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var full = Path.GetFullPath(path).Replace('\\', '/');
            var encoded = new StringBuilder("file:///");

            foreach (var c in full)
            {
                // Keep the separators and the characters npm paths actually use; percent
                // encode the rest so a space or a '#' cannot truncate the URL.
                if (char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '_' || c == '.' || c == '~' || c == '@')
                    encoded.Append(c);
                else
                    encoded.Append('%').Append(((int)c).ToString("X2"));
            }

            return encoded.ToString();
        }
    }
}
