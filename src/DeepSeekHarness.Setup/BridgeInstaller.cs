using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DeepSeekHarness.Setup
{
    /// <summary>
    /// Lays out everything the extension needs on disk under
    /// <c>%LOCALAPPDATA%\DeepSeekHarness\</c>.
    /// </summary>
    /// <remarks>
    /// Two problems shape this layout, both found by testing DeepSeek Harness against a
    /// repository that happened to be called <c>Move&amp;Jump</c>:
    ///
    /// 1. DSH runs command hooks through Git Bash on Windows, and <c>&amp;</c> in a path is a
    ///    command separator 閳?a hook installed inside the user's repository would silently
    ///    never run. Installing under the user profile avoids every such character, and it
    ///    also means the extension can refresh the hook on update without touching the
    ///    user's project.
    /// 2. Nothing here creates a DSH profile. The extension attaches to whatever profile the
    ///    user already runs by handing <c>dsh-tui --patch</c> a patch file, because
    ///    <c>dsh-tui</c> documents that DSH options are forwarded. That is one argument
    ///    instead of a profile to provision, a package install to supervise, and an app name
    ///    to guess.
    /// </remarks>
    public static class BridgeInstaller
    {
        /// <summary>Root for everything this extension writes outside its own install folder.</summary>
        public static string RootDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "DeepSeekHarness");

        /// <summary>Where the hook script, its settings and the patch live.</summary>
        public static string HookDirectory => Path.Combine(RootDirectory, "vs-bridge");

        /// <summary>Full path of the installed permission hook.</summary>
        public static string HookScriptPath => Path.Combine(HookDirectory, "vs-permission-hook.ps1");

        /// <summary>Full path of the generated DSH hook config.</summary>
        public static string HookSettingsPath => Path.Combine(HookDirectory, "settings.json");

        /// <summary>
        /// Patch file handed to <c>dsh-tui --patch</c>. It mounts the hook bridge onto the
        /// profile the user already runs.
        /// </summary>
        public static string HookPatchPath => Path.Combine(HookDirectory, "dsh-patch.yml");

        /// <summary>
        /// Launcher script the terminal actually runs. It lives under the user profile, so
        /// its path contains no spaces and no '&' 鈥?which is what lets the terminal be given
        /// a single, quoting-free argument.
        /// </summary>
        public static string StartScriptPath => Path.Combine(HookDirectory, "start.cmd");

        /// <summary>
        /// Writes the hook script, its settings and the patch. Idempotent: safe on every
        /// Visual Studio start, and it refreshes the hook after an update.
        /// </summary>
        /// <param name="hookScript">Contents of <c>vs-permission-hook.ps1</c>, embedded in the VSIX.</param>
        /// <summary>
        /// Writes the hook, its settings and the patch. The patch also registers the MCP
        /// endpoint, so the agent can call Visual Studio tools in the same session that has
        /// the diff gate armed.
        /// </summary>
        /// <param name="hookScript">Contents of <c>vs-permission-hook.ps1</c>.</param>
        /// <param name="bridgePort">Port the bridge is listening on, or 0 when unknown.</param>
        /// <param name="bridgeToken">Token the bridge requires, or null when unknown.</param>
        public static void Install(string hookScript, int bridgePort = 0, string bridgeToken = null)
        {
            if (string.IsNullOrWhiteSpace(hookScript))
                throw new ArgumentException("hook script content is required", nameof(hookScript));

            Directory.CreateDirectory(HookDirectory);

            // UTF-8 with BOM on purpose. Windows PowerShell 5.1 閳?which DSH invokes 閳?reads a
            // BOM-less file as ANSI and mangles any non-ASCII, so the BOM is what makes the
            // script portable rather than a stylistic choice.
            File.WriteAllText(HookScriptPath, hookScript, new UTF8Encoding(true));
            File.WriteAllText(HookSettingsPath, BuildSettingsJson(HookScriptPath), new UTF8Encoding(false));
            File.WriteAllText(HookPatchPath, BuildPatch(bridgePort, bridgeToken), new UTF8Encoding(false));
            File.WriteAllText(StartScriptPath, BuildStartScript(), Ascii);
        }

        /// <summary>
        /// ASCII, no BOM. cmd.exe treats a UTF-8 BOM as characters on the first line, which
        /// would make the script fail on a machine whose paths are all ASCII anyway.
        /// </summary>
        private static readonly UTF8Encoding Ascii = new UTF8Encoding(false);

        /// <summary>
        /// The script the terminal runs. Two things it must get right:
        ///
        /// 1. <c>dsh-tui</c> is a batch launcher, so it needs a shell 鈥?running it from inside
        ///    another script is what makes that unambiguous.
        /// 2. The bridge patch is attached with <c>--patch</c>, which <c>dsh-tui</c> documents
        ///    as forwarded to DSH, so the session is exactly the one the user would get by
        ///    typing the command.
        /// </summary>
        internal static string BuildStartScript()
        {
            var tui = DshLocator.FindTuiCommand();
            var patch = HookPatchPath;

            var lines = new List<string>
            {
                "@echo off",
                "rem Generated by DeepSeek Harness for Visual Studio. Do not edit by hand.",
                "rem Started in a Visual Studio terminal tab; the bridge patch arms the diff gate.",
                "title DeepSeek Harness",
                "echo DeepSeek Harness - starting a gated session...",
                "echo."
            };

            if (tui == null)
            {
                lines.Add("echo ERROR: dsh-tui was not found on PATH.");
                lines.Add("echo Install it with:  npm install -g @deepseek-harness-tui/dsh-tui");
            }
            else
            {
                lines.Add("set \"DSH_VS_PATCH=" + patch + "\"");
                lines.Add("\"" + tui + "\" --patch \"%DSH_VS_PATCH%\"");
            }

            // Keep the tab open so a failure is readable instead of a vanishing window.
            lines.Add("echo.");
            lines.Add("echo Session ended. Press any key to close this tab.");
            lines.Add("pause >nul");

            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        /// <summary>
        /// The DSH-side hook config. Only <c>PreToolUse</c> is registered: the diff gate is
        /// the one interception point this extension owns.
        /// </summary>
        internal static string BuildSettingsJson(string hookScriptPath)
        {
            // Forward slashes: the command string is executed by a shell on Windows, and a
            // backslash inside the double-quoted path is an escape character there.
            var commandPath = hookScriptPath.Replace('\\', '/');

            // Tool names are LOWERCASE in DeepSeek Harness (write/edit), unlike Claude Code's
            // Write/Edit. A matcher that does not match means the hook never runs 閳?and since
            // a missing hook is indistinguishable from an allowed edit, that failure is silent.
            return "{\n" +
                   "  \"hooks\": {\n" +
                   "    \"PreToolUse\": [\n" +
                   "      {\n" +
                   "        \"matcher\": \"write|edit|str_replace_editor\",\n" +
                   "        \"hooks\": [\n" +
                   "          {\n" +
                   "            \"type\": \"command\",\n" +
                   "            \"command\": \"powershell -NoProfile -ExecutionPolicy Bypass -File \\\"" + commandPath + "\\\"\",\n" +
                   "            \"timeout\": " + HookTimeoutSeconds + "\n" +
                   "          }\n" +
                   "        ]\n" +
                   "      }\n" +
                   "    ]\n" +
                   "  }\n" +
                   "}\n";
        }

        /// <summary>
        /// The patch that mounts the hook bridge. The bridge is addressed by absolute
        /// <c>file:///</c> URL because it ships inside the user's global <c>dsh</c>
        /// installation and the user's profile does not declare it as a dependency.
        /// </summary>
        internal static string BuildPatch(int bridgePort = 0, string bridgeToken = null)
        {
            // Each section contributes at most one patch entry, or nothing. Concatenating a
            // section that emitted its own empty document (`[]`) with one that emits a block
            // sequence produced two root nodes, and DSH refuses to parse that at all - so a
            // machine missing the hook package lost the MCP registration too, instead of
            // degrading to "the gate is off".
            var sections = new List<string>();
            var hooks = BuildHookSection();
            if (hooks != null) sections.Add(hooks);
            var mcp = BuildMcpSection(bridgePort, bridgeToken);
            if (mcp != null) sections.Add(mcp);

            if (sections.Count == 0) return Header() + "\n[]\n";
            return Header() + "\n" + string.Join("\n", sections.ToArray());
        }

        private static string Header()
        {
            return "# Generated by DeepSeek Harness for Visual Studio. Do not edit by hand:\n" +
                   "# the extension rewrites this file on every Visual Studio start.\n" +
                   "# Passed to dsh-tui with --patch when the extension starts a session.\n";
        }

        /// <summary>
        /// The MCP registration. Written with the live port and token because the client
        /// needs a URL up front; the extension rewrites this file on every start, so a
        /// changed port is picked up on the next session.
        /// </summary>
        private static string BuildMcpSection(int port, string token)
        {
            if (port <= 0 || string.IsNullOrEmpty(token))
            {
                // No MCP entry to contribute; the caller decides what the file looks like.
                return null;
            }

            return "# Visual Studio tools for the agent. The diff gate above answers edits; this\n" +
                   "# answers questions about the IDE: selection, open files, environment.\n" +
                   "- insert:\n" +
                   "    - id: mcp-vs\n" +
                   "      name: '@deepseek-ai/dsh-mcp-client'\n" +
                   "      config:\n" +
                   "        serverName: vs\n" +
                   "        transport: streamable-http\n" +
                   "        url: http://127.0.0.1:" + port + "/mcp\n" +
                   "        headers:\n" +
                   "          x-dsh-vs-authorization: '" + YamlSingleQuoted(token) + "'\n" +
                   "        failOnStartupError: false\n";
        }

        /// <summary>
        /// Escapes a value for a YAML single-quoted scalar. Paths reach this with the account
        /// name inside them, so one apostrophe in %LOCALAPPDATA% would otherwise end the
        /// scalar early and make the whole overlay unparseable - which DSH treats as fatal.
        /// </summary>
        private static string YamlSingleQuoted(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        /// <summary>The hook mount, or null when the hook package is not installed.</summary>
        private static string BuildHookSection()
        {
            var hooksPackage = DshLocator.FindHooksPackage();

            if (hooksPackage == null)
            {
                // Contributing nothing is the degradation. The previous version emitted a
                // whole `[]` document here, which combined with the MCP section into two root
                // nodes - an unparseable file on exactly the machine least able to debug it.
                return null;
            }

            // The config path is written as a single-quoted scalar, so apostrophes in the user
            // profile have to be doubled or the path (and the file) ends early.
            var configPath = YamlSingleQuoted(HookSettingsPath.Replace('\\', '/'));

            // An earlier version forced `- id: approval / policy: never` here, on the
            // assumption that a patched session has no interactive answerer and would
            // therefore fail closed. That assumption was wrong: the terminal UI composes its
            // own approval answerer (ApprovalPanel, with the on-screen hint "approval channel
            // mounted: commands requesting sandbox_permissions raise an approval bar"). The
            // override did not protect anything - it silently disabled a protection the user
            // had explicitly turned on, so elevation requests were denied with no prompt.
            //
            // The extension now leaves the approval policy alone. That is also the right
            // default for a plugin: the hook answers file edits, and everything else keeps
            // whatever policy the user configured.
            return "- insert:\n" +
                   "    - id: hooks-vs-bridge\n" +
                   "      name: '" + (DshLocator.ToPackageEntryUrl(hooksPackage) ?? DshLocator.ToFileUrl(hooksPackage)) + "'\n" +
                   "      config:\n" +
                   "        configPath: '" + configPath + "'\n";
        }

        /// <summary>
        /// 24 hours. DSH defaults a hook with no timeout to 10 minutes
        /// (<c>DEFAULT_HOOK_TIMEOUT_MS = 600000</c>) and treats a killed hook as *allow*,
        /// so a short timeout silently disables the gate while the user is still reading.
        /// </summary>
        public const int HookTimeoutSeconds = 86400;
    }
}
