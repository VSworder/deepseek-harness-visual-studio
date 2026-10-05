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

        /// <summary>Where the launch script, the patch and the plugin live.</summary>
        public static string HookDirectory => Path.Combine(RootDirectory, "vs-bridge");

        /// <summary>
        /// The DeepSeek Harness plugin this extension mounts, written outside the dsh
        /// installation so it travels with the extension instead of the user's profile.
        /// </summary>
        /// <remarks>
        /// An earlier version borrowed <c>@deepseek-ai/dsh-hooks-claude-code</c> and spoke
        /// Claude Code's hook contract to it through a PowerShell script. That package will
        /// not load from outside the dsh installation, and the failure is silent, so the gate
        /// was simply absent while MCP kept working - the exact shape of a bug that costs a
        /// day. Owning the plugin removes the borrowed loader, the format translation, and
        /// the silent failure together.
        /// </remarks>
        public static string PluginDirectory => Path.Combine(RootDirectory, "dsh-plugin");

        /// <summary>Manifest of the installed plugin.</summary>
        public static string PluginManifestPath => Path.Combine(PluginDirectory, "package.json");

        /// <summary>Module the patch names. Package directories cannot be imported, only files.</summary>
        public static string PluginEntryPath => Path.Combine(PluginDirectory, "index.js");

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
        /// <summary>
        /// Writes the plugin, the patch and the launcher. Idempotent: safe on every Visual
        /// Studio start, and it refreshes the plugin after an update.
        /// </summary>
        /// <param name="pluginSource">Contents of the gate plugin's module, embedded in the VSIX.</param>
        /// <param name="pluginManifest">Contents of the plugin's <c>package.json</c>.</param>
        /// <param name="bridgePort">Port the bridge is listening on, or 0 when unknown.</param>
        /// <param name="bridgeToken">Token the bridge requires, or null when unknown.</param>
        public static void Install(string pluginSource, string pluginManifest, int bridgePort = 0, string bridgeToken = null)
        {
            if (string.IsNullOrWhiteSpace(pluginSource))
                throw new ArgumentException("plugin source is required", nameof(pluginSource));

            Directory.CreateDirectory(HookDirectory);
            Directory.CreateDirectory(PluginDirectory);

            // No BOM. Node reads the module as UTF-8 by definition, and a BOM would become
            // part of the first token.
            File.WriteAllText(PluginEntryPath, pluginSource, Utf8NoBom);
            File.WriteAllText(PluginManifestPath, string.IsNullOrWhiteSpace(pluginManifest) ? BuildPluginManifest() : pluginManifest, Utf8NoBom);
            File.WriteAllText(HookPatchPath, BuildPatch(bridgePort, bridgeToken), Utf8NoBom);
            File.WriteAllText(StartScriptPath, BuildStartScript(bridgePort, bridgeToken), Ascii);

            // Remove what the previous design installed. Leaving a PowerShell hook behind
            // would keep a file the harness could still be told to execute, and the user has
            // no reason to know it is there.
            RemoveIfPresent(Path.Combine(HookDirectory, "vs-permission-hook.ps1"));
            RemoveIfPresent(Path.Combine(HookDirectory, "settings.json"));
        }

        private static void RemoveIfPresent(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The plugin manifest, written when the VSIX did not carry one.</summary>
        internal static string BuildPluginManifest()
        {
            return "{\n" +
                   "  \"name\": \"@deepseek-harness-vs/dsh-plugin-vs-gate\",\n" +
                   "  \"version\": \"0.1.0\",\n" +
                   "  \"private\": true,\n" +
                   "  \"type\": \"module\",\n" +
                   "  \"main\": \"index.js\",\n" +
                   "  \"license\": \"MIT\"\n" +
                   "}\n";
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// ASCII, no BOM. cmd.exe treats a UTF-8 BOM as characters on the first line, which
        /// would make the script fail on a machine whose paths are all ASCII anyway.
        /// </summary>
        private static readonly UTF8Encoding Ascii = new UTF8Encoding(false);

        /// <summary>
        /// The script the terminal runs. Three things it must get right:
        ///
        /// 1. <c>dsh-tui</c> is a batch launcher, so it needs a shell 鈥?running it from inside
        ///    another script is what makes that unambiguous.
        /// 2. The bridge patch is attached with <c>--patch</c>, which <c>dsh-tui</c> documents
        ///    as forwarded to DSH, so the session is exactly the one the user would get by
        ///    typing the command.
        /// 3. The bridge's address travels in the environment. The plugin runs inside the
        ///    session and cannot discover a lock file the way an out-of-process hook could;
        ///    handing it the port and token here is also the only place they are known to
        ///    match this Visual Studio instance.
        /// </summary>
        internal static string BuildStartScript(int bridgePort = 0, string bridgeToken = null)
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
                if (bridgePort > 0 && !string.IsNullOrEmpty(bridgeToken))
                {
                    lines.Add("set \"DSH_VS_BRIDGE_PORT=" + bridgePort + "\"");
                    lines.Add("set \"DSH_VS_BRIDGE_TOKEN=" + bridgeToken + "\"");
                }
                lines.Add("\"" + tui + "\" --patch \"%DSH_VS_PATCH%\"");
            }

            // Keep the tab open so a failure is readable instead of a vanishing window.
            lines.Add("echo.");
            lines.Add("echo Session ended. Press any key to close this tab.");
            lines.Add("pause >nul");

            return string.Join("\r\n", lines.ToArray()) + "\r\n";
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
            var gate = BuildPluginSection();
            if (gate != null) sections.Add(gate);
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

        /// <summary>
        /// The plugin mount, or null when the plugin has not been written yet.
        /// </summary>
        /// <remarks>
        /// The URL names the module FILE. A URL naming the package directory cannot be
        /// imported - Node answers ERR_UNSUPPORTED_DIR_IMPORT, because only bare specifiers
        /// get package resolution - and the harness reports that as "entry did not activate"
        /// and carries on. The gate was absent for exactly that reason while the MCP entry in
        /// the same patch kept working.
        /// </remarks>
        private static string BuildPluginSection()
        {
            if (!File.Exists(PluginEntryPath)) return null;

            var url = DshLocator.ToFileUrl(PluginEntryPath);
            if (string.IsNullOrEmpty(url)) return null;

            // No approval override here, deliberately. An earlier version forced
            // `- id: approval / policy: never` on the assumption that a patched session has no
            // interactive answerer. The terminal UI composes its own, so the override disabled
            // a protection the user had turned on: elevation requests were denied with no
            // prompt. This plugin returns a decision from the waterfall instead, so it never
            // needs the approval channel and leaves that policy alone.
            return "- insert:\n" +
                   "    - id: vs-gate\n" +
                   "      name: '" + url + "'\n";
        }
    }
}
