using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarness.Setup;
using Microsoft.VisualStudio.Terminal;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Starts a DeepSeek Harness session in Visual Studio's own terminal.
    /// </summary>
    /// <remarks>
    /// Visual Studio ships a terminal host (ConPTY behind <c>Microsoft.Terminal.Wpf</c>)
    /// exposed through <see cref="IVsTerminalService"/>, so the agent runs in a real terminal
    /// tab instead of an emulator we would have to write. The session UI is therefore DeepSeek
    /// Harness's own TUI: this extension never renders a conversation.
    ///
    /// The launcher registers its profile through <see cref="ITerminalService.AddCachedProfile"/>
    /// rather than writing <c>environment.terminal.profiles</c> into the user's settings. That
    /// distinction is the whole point: a cached profile is additive and disappears when Visual
    /// Studio restarts, so the extension never edits configuration the user owns and never
    /// changes what their terminal button does.
    ///
    /// Two wrong turns worth recording. The <c>CreateTerminalAsync</c> overload taking a
    /// <c>ProfileConfig</c> ignores a profile the service has not seen and falls back to the
    /// shell's default 閳?the tab takes the requested name and runs PowerShell. And the profile
    /// has to be reachable under the overload taking an <see cref="ITerminalProfile"/>, which is
    /// why <c>AddCachedProfile</c> comes first.
    /// </remarks>
    internal sealed class VsTerminalLauncher
    {
        private const string ProfileId = "DeepSeekHarness.VS.Terminal";
        private const string ProfileName = "DeepSeek Harness";

        private readonly IServiceProvider _serviceProvider;
        private readonly Action<string> _log;

        public VsTerminalLauncher(IServiceProvider serviceProvider, Action<string> log)
        {
            _serviceProvider = serviceProvider;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Opens a terminal tab running the gated TUI. Returns the terminal id, or null when
        /// the session could not be started.
        /// </summary>
        public async Task<Guid?> LaunchAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            var terminal = GetTerminalService();
            if (terminal == null)
            {
                _log("terminal service unavailable; cannot start a session inside Visual Studio");
                return null;
            }

            var script = BridgeInstaller.StartScriptPath;
            if (!System.IO.File.Exists(script))
            {
                _log("the launch script is missing: " + script);
                return null;
            }

            var profile = BuildProfile(script);

            try
            {
                // Register first: the profile overload only honours a profile the service knows.
                terminal.AddCachedProfile(profile);
                _log("registered terminal profile '" + ProfileName + "'");
            }
            catch (Exception ex)
            {
                // Not fatal on its own; the launch below reports the real outcome.
                _log("could not register the terminal profile: " + ex.Message);
            }

            // The window entry point is what honours the default profile; the overload taking
            // a profile object is accepted but ignored, measured, so it is the fallback.
            try
            {
                var id = await terminal.CreateTerminalWindowAsync(cancellationToken, null).ConfigureAwait(true);
                _log("opened terminal window " + id);

                // The profile only had to exist for that one selection. Withdrawing it keeps
                // the promise that this extension adds nothing lasting: otherwise the user's
                // own "new terminal" would keep opening DeepSeek Harness until a restart.
                // The running terminal owns its pty already, so removing the cached entry
                // does not disturb it.
                try
                {
                    terminal.RemoveCachedProfile(profile);
                    _log("withdrew the cached profile; the running session is unaffected");
                }
                catch (Exception ex)
                {
                    _log("could not withdraw the cached profile: " + ex.Message);
                }

                return id;
            }
            catch (Exception ex)
            {
                _log("CreateTerminalWindowAsync failed: " + ex);
            }

            try
            {
                var id = await terminal.CreateTerminalAsync(
                    cancellationToken,
                    name: ProfileName,
                    profile: (ITerminalProfile)profile,
                    workingDirectory: workingDirectory).ConfigureAwait(true);

                _log("fell back to CreateTerminalAsync; opened terminal " + id);
                return id;
            }
            catch (Exception ex)
            {
                _log("could not start a terminal at all: " + ex);
                return null;
            }
        }

        /// <summary>
        /// The profile the terminal runs: cmd.exe executing the generated launch script.
        /// </summary>
        /// <remarks>
        /// cmd.exe is the executable because <c>dsh-tui.cmd</c> is a batch launcher, not an
        /// executable; handing the terminal a .cmd path directly opens a tab that produces no
        /// session. The script lives under the user profile, so its path has no spaces and
        /// needs no quoting.
        /// </remarks>
        private static ProfileConfig BuildProfile(string scriptPath)
        {
            var config = new ProfileConfig(
                displayName: ProfileName,
                location: "cmd.exe",
                arguments: "/k \"" + scriptPath + "\"",
                // isDefault must be true. A non-default profile is exactly what the
                // terminal service refuses to select on its own - which is why the first
                // attempt opened a tab named "DeepSeek Harness" running PowerShell while
                // picking the same profile from the dropdown worked. The cache is
                // process-lifetime only, so this changes nothing after Visual Studio
                // restarts, and it is the price of the service choosing our profile.
                isDefault: true);

            config.Id = ProfileId;
            return config;
        }

        /// <summary>
        /// Everything the user could pick from, for the log. Turns a "my profile is missing"
        /// report into something actionable.
        /// </summary>
        public async Task<string> DescribeProfilesAsync(CancellationToken cancellationToken)
        {
            var terminal = GetTerminalService();
            if (terminal == null) return "(no terminal service)";

            try
            {
                var profiles = await terminal.GetProfilesAsync(cancellationToken).ConfigureAwait(true);
                var names = new List<string>();
                foreach (var profile in profiles) names.Add(profile.DisplayName + " [" + profile.Id + "]");
                return names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray());
            }
            catch (Exception ex)
            {
                return "(could not list profiles: " + ex.Message + ")";
            }
        }

        /// <summary>
        /// Resolves <see cref="ITerminalService"/> from the shell. When the VS edition or update
        /// does not expose it, this returns null and the caller degrades gracefully.
        /// </summary>
        private ITerminalService GetTerminalService()
        {
            try
            {
                var factory = _serviceProvider.GetService(typeof(SVsTerminalService)) as IVsTerminalService;
                if (factory == null)
                {
                    _log("SVsTerminalService not available in this Visual Studio edition");
                    return null;
                }

                return factory.CreateTerminalService();
            }
            catch (Exception ex)
            {
                _log("could not create the terminal service: " + ex);
                return null;
            }
        }
    }
}
