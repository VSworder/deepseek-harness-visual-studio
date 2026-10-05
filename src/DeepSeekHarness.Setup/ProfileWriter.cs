using System;

namespace DeepSeekHarness.Setup
{
    /// <summary>
    /// Writes the bridge files and reports what the extension needs to act on.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="BridgeInstaller"/> so the packaging side and the
    /// runtime side do not have to agree on one large method.
    /// </remarks>
    public static class ProfileWriter
    {
        /// <summary>Installs the bridge and reports the state of everything it depends on.</summary>
        public static InstallResult Install(string hookScript, int bridgePort = 0, string bridgeToken = null)
        {
            BridgeInstaller.Install(hookScript, bridgePort, bridgeToken);

            return new InstallResult
            {
                HookScriptPath = BridgeInstaller.HookScriptPath,
                HookSettingsPath = BridgeInstaller.HookSettingsPath,
                PatchPath = BridgeInstaller.HookPatchPath,
                HooksPackageFound = DshLocator.FindHooksPackage() != null,
                TuiCommandFound = DshLocator.FindTuiCommand() != null,
                DshCommandFound = DshLocator.FindDshCommand() != null
            };
        }
    }

    /// <summary>What the extension needs to know after laying out the bridge.</summary>
    public sealed class InstallResult
    {
        public string HookScriptPath { get; set; }
        public string HookSettingsPath { get; set; }

        /// <summary>Patch passed to <c>dsh-tui --patch</c> when a session starts.</summary>
        public string PatchPath { get; set; }

        /// <summary>False when the hook bridge package could not be located in the dsh install.</summary>
        public bool HooksPackageFound { get; set; }

        /// <summary>
        /// False when <c>dsh-tui</c> is missing. The extension attaches to the profile the
        /// user runs through that launcher, so without it there is nothing to start.
        /// </summary>
        public bool TuiCommandFound { get; set; }

        /// <summary>False when <c>dsh</c> itself was not found.</summary>
        public bool DshCommandFound { get; set; }

        /// <summary>True when a session can be started with the gate attached.</summary>
        public bool CanStartSession => HooksPackageFound && TuiCommandFound;
    }
}
