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
    /// shell's default 闂?the tab takes the requested name and runs PowerShell. And the profile
    /// has to be reachable under the overload taking an <see cref="ITerminalProfile"/>, which is
    /// why <c>AddCachedProfile</c> comes first.
    /// </remarks>
    internal sealed class VsTerminalLauncher
    {
        private const string ProfileId = "DeepSeekHarness.VS.Terminal";
        private const string ProfileName = "DeepSeek Harness";

        /// <summary>Full path to the command processor, as Visual Studio's own profile uses.</summary>
        private static readonly string CommandProcessorPath =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

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

            // Diagnose before touching anything: what the service already knows decides which
            // launch path can possibly work, and guessing at that has been expensive.
            await DumpProfilesAsync(terminal, "before");

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

            await DumpProfilesAsync(terminal, "after-register");

            // Handing the profile to this call is what selects it. The service resolves a
            // profile by id against its own list rather than trusting the object, which is
            // why AddCachedProfile above is a prerequisite and not a nicety.
            try
            {
                var id = await terminal.CreateTerminalAsync(
                    cancellationToken,
                    name: ProfileName,
                    profile: (ITerminalProfile)profile,
                    workingDirectory: workingDirectory).ConfigureAwait(true);

                _log("started terminal " + id + " with profile '" + ProfileName + "'");
                WithdrawLater(terminal, profile, id);
                return id;
            }
            catch (Exception ex)
            {
                _log("CreateTerminalAsync with a profile failed: " + ex);
            }

            // Last resort: a plain terminal, with the profile still registered so the user can
            // pick it from the dropdown. Better than nothing, and the log says what to do.
            try
            {
                var id = await terminal.CreateTerminalWindowAsync(cancellationToken, null).ConfigureAwait(true);
                _log("opened a plain terminal window (" + id + "); pick the '" + ProfileName +
                     "' profile from the terminal dropdown to run the session");
                return id;
            }
            catch (Exception ex)
            {
                _log("could not start a terminal at all: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Drops the cached profile once the terminal has settled.
        /// </summary>
        /// <remarks>
        /// Deferred rather than immediate: the launch call returns before the terminal has
        /// finished resolving its profile, and withdrawing too early pulled the profile out
        /// from under it - which looked exactly like selection silently falling back to
        /// PowerShell. The delay is a compromise, not a guarantee: it keeps the extension
        /// from leaving a permanent default behind, without racing the terminal's startup.
        /// </remarks>
        private void WithdrawLater(ITerminalService terminal, ProfileConfig profile, Guid id)
        {
            System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(delegate
            {
                try
                {
                    terminal.RemoveCachedProfile(profile);
                    _log("withdrew the cached profile after terminal " + id + " started");
                }
                catch (Exception ex)
                {
                    _log("could not withdraw the cached profile: " + ex.Message);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
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
            // Two details are copied from the command prompt profile Visual Studio ships
            // (loc='C:\Windows\system32\cmd.exe', args='/k ""%VSAPPIDDIR%\..\VsDevCmd.bat"'):
            // the location is a full path, and the command after /k is double-quoted.
            //
            // Getting these wrong is not a cosmetic problem. A bare 'cmd.exe' with a
            // single-quoted command registered, listed, resolved by id and launched without
            // error - and ran the default shell anyway. The profile shape, not the
            // registration, was what the terminal service rejected.
            var config = new ProfileConfig(
                displayName: ProfileName,
                location: CommandProcessorPath,
                arguments: "/k \"\"" + scriptPath + "\"\"",
                isDefault: false);
            config.Id = ProfileId;
            return config;
        }

        /// <summary>
        /// Logs every profile the service knows and which one it calls the default.
        /// </summary>
        /// <remarks>
        /// Added after several launches reported success while running the wrong command.
        /// The service resolves a profile against its own collection, so "did my profile get
        /// in, and what does the service consider default" is the fact that decides which
        /// launch path can work. Guessing at it cost several rounds.
        /// </remarks>
        private async Task DumpProfilesAsync(ITerminalService terminal, string stage)
        {
            try
            {
                var profiles = await terminal.GetProfilesAsync(CancellationToken.None).ConfigureAwait(true);
                var count = 0;
                foreach (var p in profiles)
                {
                    count++;
                    _log("profiles[" + stage + "] " + count + ": name='" + p.DisplayName +
                         "' id='" + p.Id + "' default=" + p.IsDefault +
                         " pty=" + p.CreatePTY + " loc='" + p.Location + "' args='" + p.Arguments + "'");
                }
                _log("profiles[" + stage + "]: " + count + " total");
            }
            catch (Exception ex)
            {
                _log("profiles[" + stage + "]: listing failed: " + ex.Message);
            }

            try
            {
                var def = await terminal.GetDefaultProfileAsync(CancellationToken.None).ConfigureAwait(true);
                _log("default[" + stage + "]: " + (def == null ? "(null)" : "name='" + def.DisplayName + "' id='" + def.Id + "'"));
            }
            catch (Exception ex)
            {
                _log("default[" + stage + "]: lookup failed: " + ex.Message);
            }
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
