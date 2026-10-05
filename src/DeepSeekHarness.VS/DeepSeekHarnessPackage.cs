using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarness.Bridge;
using DeepSeekHarness.Setup;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Visual Studio entry point. Starts the loopback bridge, publishes its lock file,
    /// installs the DeepSeek Harness hook beside it, and keeps a session terminal within
    /// reach.
    /// </summary>
    /// <remarks>
    /// Autoloads on solution load rather than shell start: the bridge advertises the
    /// solution folder as its workspace, so there is nothing useful to publish before one
    /// is open, and keeping devenv's startup path clear matters.
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class DeepSeekHarnessPackage : AsyncPackage
    {
        public const string PackageGuidString = "8f2c1d94-7b3e-4a51-9d6f-2c4b8e0a1f37";

        /// <summary>Command set GUID; must match the VSCT symbols.</summary>
        public const string CommandSetGuidString = "5c1f4d20-9a83-4e77-b6d2-1f0e8a3c7b94";

        private const int CmdStartSession = 0x0100;
        private const int CmdStartSessionContext = 0x0101;
        private const int CmdStatus = 0x0102;
        private const int CmdOpenLog = 0x0103;
        private const int CmdRemove = 0x0104;

        private static readonly string LogPath =
            Path.Combine(BridgeInstaller.RootDirectory, "vs-extension.log");

        private BridgeServer _server;
        private BridgeLock _lock;
        private IWorkspaceMatcher _matcher;
        private InstallResult _install;

        protected override async Task InitializeAsync(CancellationToken cancellationToken,
                                                     IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // AsyncPackage.Initialize() is sealed, so commands are wired here instead.
            try
            {
                InitializeCommands();
            }
            catch (Exception ex)
            {
                Log("could not register commands: " + ex);
            }

            try
            {
                StartBridge();
            }
            catch (Exception ex)
            {
                // Never take devenv down: a broken bridge simply means no gating.
                Log("failed to start bridge: " + ex);
            }
        }

        private void StartBridge()
        {
            Log("starting");

            // Drop lock files left behind by a crashed Visual Studio before publishing ours.
            BridgeLock.ReapStale(BridgeLock.DefaultDirectory);

            _matcher = new PathWorkspaceMatcher(GetWorkspaceFolders());

            _server = new BridgeServer(_matcher, Log);
            _server.PermissionHandler = OnPermissionRequestedAsync;
            _server.ToolProvider = BuildIdeTools;
            _server.ToolInvoker = InvokeIdeTool;
            _server.Start();

            _lock = new BridgeLock
            {
                Port = _server.Port,
                AuthToken = _server.AuthToken,
                Pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                WorkspaceFolders = new List<string>(_matcher.WorkspaceFolders)
            };
            _lock.WriteTo(BridgeLock.DefaultDirectory);
            Log("lock written: " + BridgeLock.PathFor(BridgeLock.DefaultDirectory, _server.Port));

            InstallBridge();
        }

        /// <summary>
        /// Writes the hook, its DSH config, the DSH profile and the profile patch, so a
        /// session started from Visual Studio has the gate active without the user
        /// configuring anything.
        /// </summary>
        private void InstallBridge()
        {
            try
            {
                _install = ProfileWriter.Install(ReadEmbeddedPlugin(), ReadEmbeddedPluginManifest(),
                                                 _server.Port, _server.AuthToken);

                Log("gate plugin  : " + _install.PluginEntryPath);
                Log("bridge patch : " + _install.PatchPath);

                if (!_install.DshCommandFound)
                    Log("WARNING: dsh was not found on PATH; the extension cannot start a session.");

                // Positive evidence only. The failure this replaces was invisible: the session
                // started, the gate was not mounted, and edits were written without asking
                // while every log line looked healthy. Same reasoning as before, one check
                // instead of two, because the plugin now ships inside the VSIX rather than
                // being borrowed from the dsh installation.
                if (!_install.PluginInstalled)
                    Log("GATE OFF: the gate plugin was not written to disk");

                if (!_install.TuiCommandFound)
                    Log("GATE OFF: dsh-tui was not found; install it with: " +
                        "npm install -g @deepseek-harness-tui/dsh-tui");

                Log(_install.CanStartSession
                    ? "ready: a gated session can be started"
                    : "NOT READY: starting a session would run without the diff gate");
            }
            catch (Exception ex)
            {
                Log("bridge install failed: " + ex);
            }
        }

        /// <summary>
        /// Creates the profile's node_modules by delegating to the dsh launcher, which
        /// already owns profile package management. Runs off the UI thread because a cold
        /// install downloads packages.
        /// </summary>
        /// <summary>
        /// The Visual Studio state exposed to the agent. Built per request so tools reflect
        /// the solution that is open now.
        /// </summary>
        private IIdeTool[] BuildIdeTools()
        {
            return new IIdeTool[]
            {
                new EnvironmentTool(this, _matcher),
                new SelectionTool(this),
                new OpenFilesTool(this)
            };
        }

        /// <summary>
        /// Runs one tool on the UI thread. The MCP endpoint is reached over HTTP from the
        /// agent's process, so without this every tool would touch editor state off-thread.
        /// </summary>
        private string InvokeIdeTool(IIdeTool tool, System.Collections.Generic.IReadOnlyDictionary<string, string> arguments)
        {
            string result = null;
            Exception failure = null;

            // The MCP endpoint is reached over HTTP from the agent's process, so the call
            // arrives on a thread-pool thread; every IDE tool needs the UI thread.
            var joinable = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                result = tool.Invoke(arguments);
            });

            try
            {
                joinable.Join();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure != null) throw failure;
            return result;
        }
        /// <summary>
        /// Reads the gate plugin embedded in this assembly, so the plugin version always
        /// travels with the extension that installed it.
        /// </summary>
        private static string ReadEmbeddedPlugin()
        {
            return ReadEmbeddedText("vs-gate-plugin.js",
                   "the gate plugin is missing from the VSIX; the diff gate cannot be armed");
        }

        /// <summary>
        /// Reads the plugin's manifest, or returns null to let the installer generate one.
        /// </summary>
        private static string ReadEmbeddedPluginManifest()
        {
            try
            {
                return ReadEmbeddedText("vs-gate-plugin.package.json", null);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a text resource by the suffix of its manifest name. The suffix is enough to
        /// identify these files and keeps the lookup working if the folder layout changes.
        /// </summary>
        private static string ReadEmbeddedText(string nameSuffix, string missingMessage)
        {
            var assembly = Assembly.GetExecutingAssembly();

            string name = null;
            foreach (var candidate in assembly.GetManifestResourceNames())
            {
                if (candidate.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = candidate;
                    break;
                }
            }

            if (name == null)
            {
                if (missingMessage == null) throw new InvalidOperationException(nameSuffix + " not found");
                throw new InvalidOperationException(missingMessage);
            }

            using (var stream = assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private Task<PermissionDecision> OnPermissionRequestedAsync(PermissionRequest request)
        {
            // The HTTP handler runs on a thread-pool thread and may stay alive for hours,
            // but the diff must be presented on the UI thread. Bridge the two with a
            // completion source rather than blocking either one.
            var completion = new TaskCompletionSource<PermissionDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    var presenter = new VsDiffPresenter(this, Log);
                    var outcome = await presenter.PresentAsync(request.FilePath, request.CurrentContents, request.NewContents);

                    switch (outcome.Verdict)
                    {
                        case DiffVerdict.Accept:
                            Log("accepted: " + request.FilePath);
                            completion.TrySetResult(PermissionDecision.Accepted());
                            break;

                        case DiffVerdict.Reject:
                            Log("rejected: " + request.FilePath +
                                (string.IsNullOrWhiteSpace(outcome.Reason) ? "" : " (" + outcome.Reason + ")"));
                            completion.TrySetResult(PermissionDecision.Rejected(outcome.Reason));
                            break;

                        default:
                            // Could not show a diff: decline rather than block. DSH then runs
                            // its own permission flow, which is the safe direction.
                            Log("could not present diff for " + request.FilePath + ": " + outcome.Error);
                            completion.TrySetResult(PermissionDecision.DeclineToGate(outcome.Error));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log("diff presentation failed: " + ex);
                    completion.TrySetResult(PermissionDecision.DeclineToGate("diff presentation failed"));
                }
            });

            return completion.Task;
        }

        /// <summary>
        /// Starts a DeepSeek Harness session in a Visual Studio terminal tab. Called from
        /// the toolbar command the package registers.
        /// </summary>
        internal async Task LaunchSessionAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                // Never start a session that would run ungated: it would look healthy while
                // every write went straight to disk.
                if (_install == null || !_install.CanStartSession)
                {
                    ShowGateUnavailable(_install == null
                        ? "the bridge was not installed"
                        : DescribeMissingPieces());
                    return;
                }

                string workingDirectory = null;
                foreach (var folder in GetWorkspaceFolders()) { workingDirectory = folder; break; }

                var launcher = new VsTerminalLauncher(this, Log);
                var id = await launcher.LaunchAsync(workingDirectory ?? Directory.GetCurrentDirectory(),
                                                   CancellationToken.None);

                if (id == null)
                    Log("session launch failed; see the messages above");
            }
            catch (Exception ex)
            {
                Log("session launch threw: " + ex);
            }
        }

        /// <summary>
        /// Routes the menu and context-menu commands declared in DeepSeekHarness.vsct.
        /// </summary>
        private void InitializeCommands()
        {
            var menuCommandService = GetService(typeof(System.ComponentModel.Design.IMenuCommandService))
                as System.ComponentModel.Design.IMenuCommandService;
            if (menuCommandService == null)
            {
                // The menu entries come from the VSCT and stay visible either way, so without
                // this line the user clicks a command that does nothing and the log - the one
                // artifact a bug report carries - says nothing about why.
                Log("IMenuCommandService unavailable; no commands were registered");
                return;
            }

            var commandSet = new Guid(CommandSetGuidString);

            menuCommandService.AddCommand(new MenuCommand(
                (_, __) => _ = LaunchSessionAsync(),
                new CommandID(commandSet, CmdStartSession)));

            menuCommandService.AddCommand(new MenuCommand(
                (_, __) => _ = LaunchSessionAsync(),
                new CommandID(commandSet, CmdStartSessionContext)));

            menuCommandService.AddCommand(new MenuCommand(
                (_, __) => ShowStatus(),
                new CommandID(commandSet, CmdStatus)));

            menuCommandService.AddCommand(new MenuCommand(
                (_, __) => OpenLog(),
                new CommandID(commandSet, CmdOpenLog)));

            menuCommandService.AddCommand(new MenuCommand(
                (_, __) => RemoveEverything(),
                new CommandID(commandSet, CmdRemove)));
        }

        /// <summary>
        /// Deletes everything this extension installed outside the VSIX.
        /// </summary>
        /// <remarks>
        /// VSIX uninstall cannot run extension code, so the hook script, its DeepSeek Harness
        /// configuration, the launcher and the log all survive it - and the hook script is
        /// executed by DeepSeek Harness with the user's own rights. Confirming first, because
        /// this is the one irreversible thing the extension does.
        /// </remarks>
        private void RemoveEverything()
        {
            try
            {
                var root = BridgeInstaller.RootDirectory;
                if (!System.IO.Directory.Exists(root))
                {
                    VsShellUtilities.ShowMessageBox(this,
                        "Nothing to remove. The extension's folder does not exist:\n\n" + root,
                        "DeepSeek Harness", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    return;
                }

                var answer = VsShellUtilities.ShowMessageBox(this,
                    "Delete the files this extension installed?\n\n" + root +
                    "\n\nThis removes the permission hook, its DeepSeek Harness configuration and the launcher.\n" +
                    "Sessions started from that launcher stop being gated. Uninstalling the VSIX does NOT do this.",
                    "DeepSeek Harness", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

                if (answer != 6) // IDYES
                {
                    Log("remove everything: cancelled by the user");
                    return;
                }

                // The bridge is writing a lock file into this tree, so stop it first.
                try { _server?.Dispose(); } catch { }
                try { _lock?.Delete(BridgeLock.DefaultDirectory); } catch { }

                System.IO.Directory.Delete(root, true);
                Log("removed " + root);
                VsShellUtilities.ShowMessageBox(this,
                    "Removed:\n\n" + root +
                    "\n\nUninstall the extension itself from Extensions > Manage Extensions when you want it gone.",
                    "DeepSeek Harness", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch (Exception ex)
            {
                Log("remove everything failed: " + ex);
                VsShellUtilities.ShowMessageBox(this,
                    "Could not remove the folder:\n\n" + ex.Message + "\n\nSee the log for details.",
                    "DeepSeek Harness", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        /// <summary>
        /// Reports whether the gate is armed. The report is written to the log first, so a
        /// bug report carries it even if the user only reads the dialog.
        /// </summary>
        private void ShowStatus()
        {
            try
            {
                var reporter = new StatusReporter(Log);
                var report = reporter.BuildReport();
                reporter.LogReport(report);
                StatusWindow.Show(report);
            }
            catch (Exception ex)
            {
                Log("status report failed: " + ex);
            }
        }

        /// <summary>Opens the extension log in the editor.</summary>
        private void OpenLog()
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    Log("log opened before anything was written");
                    Log("this line exists so the file has something in it");
                }

                VsShellUtilities.OpenDocument(this, LogPath);
            }
            catch (Exception ex)
            {
                Log("could not open the log: " + ex);
            }
        }
        /// <summary>
        /// Tells the user, in the IDE, that the gate could not be turned on. Silence here
        /// would be the worst outcome: the session would look healthy while every write
        /// went straight to disk.
        /// </summary>
        /// <summary>
        /// Explains, in the terms the user can act on, which piece is missing.
        /// </summary>
        private string DescribeMissingPieces()
        {
            var parts = new System.Collections.Generic.List<string>();

            if (!_install.PluginInstalled)
                parts.Add("the gate plugin was not written to disk " +
                          "(" + BridgeInstaller.PluginEntryPath + ")");

            if (!_install.TuiCommandFound)
                parts.Add("dsh-tui was not found on PATH " +
                          "(install it: npm install -g @deepseek-harness-tui/dsh-tui)");

            if (parts.Count == 0) parts.Add("the bridge is not ready");

            return string.Join("\n  - ", parts.ToArray());
        }
        private void ShowGateUnavailable(string reason)
        {
            Log("REFUSING TO START: " + reason);

            try
            {
                VsShellUtilities.ShowMessageBox(
                    this,
                    "DeepSeek Harness could not start a session because its profile is not ready.\n\n" +
                    reason + "\n\n" +
                    "The extension installs the profile automatically. If this is the first run, " +
                    "check that the machine can reach the npm registry, or run this by hand to see the error:\n\n" +
                    "    dsh-tui --patch \"" + BridgeInstaller.HookPatchPath + "\"\n\n" +
                    "Log: " + LogPath,
                    "DeepSeek Harness",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch (Exception ex)
            {
                Log("could not show the readiness message box: " + ex.Message);
            }
        }
        private IEnumerable<string> GetWorkspaceFolders()
        {
            var folders = new List<string>();

            try
            {
                var solution = GetService(typeof(SVsSolution)) as IVsSolution;
                string solutionDirectory;
                string solutionFile;
                string userOptionsFile;
                if (solution != null &&
                    solution.GetSolutionInfo(out solutionDirectory, out solutionFile, out userOptionsFile) >= 0 &&
                    !string.IsNullOrEmpty(solutionDirectory))
                {
                    folders.Add(solutionDirectory.TrimEnd('\\'));
                }
            }
            catch (Exception ex)
            {
                Log("could not read solution directory: " + ex.Message);
            }

            if (folders.Count == 0)
            {
                // No solution. Do not fall back to Directory.GetCurrentDirectory(): for a
                // devenv-hosted extension that is Visual Studio's own install folder (or
                // wherever the shortcut started it), so the bridge would scope the agent's
                // workspace to Program Files and open the session tab there. Use a directory
                // that belongs to the user, and say which one was chosen.
                var folder = SafeUserFolder();
                folders.Add(folder);
                Log("no solution or workspace folder; using " + folder + " as the workspace");
            }

            return folders;
        }

        /// <summary>
        /// A directory that belongs to the user, for when the IDE reports no workspace.
        /// </summary>
        private static string SafeUserFolder()
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile) && Directory.Exists(profile))
                    return profile.TrimEnd('\\');
            }
            catch (Exception) { }

            // Last resort, and still not the IDE's own directory.
            return Path.GetTempPath().TrimEnd('\\');
        }

        /// <summary>
        /// Appends to a log under %LOCALAPPDATA%\DeepSeekHarness\. Every failure path in
        /// this extension is silent by design (fail-open), so this file is the only way to
        /// find out that the gate stopped working.
        /// </summary>
        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(BridgeInstaller.RootDirectory);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;

                // Keep the log from growing without bound across sessions.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024)
                    File.Delete(LogPath);

                File.AppendAllText(LogPath, line, new System.Text.UTF8Encoding(false));
            }
            catch { /* logging must never throw */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _lock?.Delete(BridgeLock.DefaultDirectory); } catch { }
                try { _server?.Dispose(); } catch { }
                Log("bridge stopped");
            }

            base.Dispose(disposing);
        }
    }
}