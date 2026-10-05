using System;
using System.IO;

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
        /// <param name="pluginSource">Contents of the gate plugin's module, embedded in the VSIX.</param>
        /// <param name="pluginManifest">Contents of the plugin's <c>package.json</c>, or null to generate one.</param>
        /// <param name="bridgePort">Port the bridge is listening on, or 0 when unknown.</param>
        /// <param name="bridgeToken">Token the bridge requires, or null when unknown.</param>
        public static InstallResult Install(string pluginSource, string pluginManifest = null,
                                            int bridgePort = 0, string bridgeToken = null)
        {
            BridgeInstaller.Install(pluginSource, pluginManifest, bridgePort, bridgeToken);

            return new InstallResult
            {
                PluginEntryPath = BridgeInstaller.PluginEntryPath,
                PatchPath = BridgeInstaller.HookPatchPath,

                // The plugin ships inside the VSIX, so unlike the Claude Code hook package it
                // replaced, nothing outside this extension has to be present for the gate to
                // exist. The check is now "did our own file land on disk", which is the
                // failure that has actually been observed.
                PluginInstalled = IsUsableFile(BridgeInstaller.PluginEntryPath),
                TuiCommandFound = DshLocator.FindTuiCommand() != null,
                DshCommandFound = DshLocator.FindDshCommand() != null
            };
        }

        /// <summary>
        /// Whether a file exists and is non-empty. A zero-byte module would load as an empty
        /// plugin, which looks installed and gates nothing.
        /// </summary>
        private static bool IsUsableFile(string path)
        {
            try
            {
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>What the extension needs to know after laying out the bridge.</summary>
    public sealed class InstallResult
    {
        /// <summary>Module the patch mounts.</summary>
        public string PluginEntryPath { get; set; }

        /// <summary>Patch passed to <c>dsh-tui --patch</c> when a session starts.</summary>
        public string PatchPath { get; set; }

        /// <summary>False when the gate plugin could not be written.</summary>
        public bool PluginInstalled { get; set; }

        /// <summary>
        /// False when <c>dsh-tui</c> is missing. The extension attaches to the profile the
        /// user runs through that launcher, so without it there is nothing to start.
        /// </summary>
        public bool TuiCommandFound { get; set; }

        /// <summary>False when <c>dsh</c> itself was not found.</summary>
        public bool DshCommandFound { get; set; }

        /// <summary>True when a session can be started with the gate attached.</summary>
        public bool CanStartSession => PluginInstalled && TuiCommandFound;
    }
}
