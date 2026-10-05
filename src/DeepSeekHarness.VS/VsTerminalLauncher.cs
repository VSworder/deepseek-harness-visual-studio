using System;
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
    /// exposed through <see cref="IVsTerminalService"/>, so the agent runs in a real
    /// terminal tab instead of an emulator we would have to write. The session UI is
    /// therefore DeepSeek Harness's own TUI: this extension never renders a conversation.
    ///
    /// The session is started through <c>cmd.exe</c> running <c>dsh-tui</c> with the bridge
    /// patch. Two things that were learned the hard way:
    ///
    /// - The location must be an executable. Handing the terminal a <c>.cmd</c> path
    ///   directly opens a tab that never runs anything, which is what happened before this
    ///   went through cmd.exe.
    /// - The bridge is attached with <c>--patch</c> rather than by owning a DSH profile,
    ///   so the session is exactly the one the user would get by typing the command.
    /// </remarks>
    internal sealed class VsTerminalLauncher
    {
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
        public async Task<Guid?> LaunchAsync(string workingDirectory, System.Threading.CancellationToken cancellationToken)
        {
            var terminal = GetTerminalService();
            if (terminal == null)
            {
                _log("terminal service unavailable; cannot start a session inside Visual Studio");
                return null;
            }

            var tui = DshLocator.FindTuiCommand();
            if (tui == null)
            {
                _log("dsh-tui was not found; install it with: npm install -g @deepseek-harness-tui/dsh-tui");
                return null;
            }

            var arguments = DshLocator.BuildTuiArguments();
            if (string.IsNullOrEmpty(arguments))
            {
                _log("could not build the session command line");
                return null;
            }

            // cmd.exe is the executable; the TUI follows through the generated script.
            //
            // isDefault must be TRUE. With it false, the terminal service ignores this
            // profile entirely and opens the user's default shell instead - observed as a
            // tab named "DeepSeek Harness" running Developer PowerShell with nothing
            // started. The name was honoured, the command was not.
            var profile = new ProfileConfig(
                displayName: "DeepSeek Harness",
                location: "cmd.exe",
                arguments: arguments,
                isDefault: true);

            try
            {
                var id = await terminal.CreateTerminalAsync(
                    cancellationToken,
                    name: "DeepSeek Harness",
                    profile: profile,
                    workingDirectory: workingDirectory).ConfigureAwait(true);

                _log("launched session in VS terminal " + id + " (cmd.exe " + arguments + ")");
                return id;
            }
            catch (Exception ex)
            {
                // Most likely shape drift in the VS terminal contract across updates.
                _log("could not start the terminal: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Resolves <see cref="ITerminalService"/> from the shell. When the VS edition or
        /// update does not expose it, this returns null and the caller degrades gracefully.
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
