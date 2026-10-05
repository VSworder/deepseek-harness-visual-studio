using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Presents the proposed change in Visual Studio's own comparison window and collects
    /// the verdict from a small modeless dialog.
    /// </summary>
    /// <remarks>
    /// The comparison window is opened through <see cref="IVsDifferenceService"/> with two
    /// file monikers, which is what makes this the *native* diff (same viewer, same
    /// navigation, same keyboard shortcuts the user already knows). The verdict cannot come
    /// from the diff viewer itself — it returns an <see cref="IVsWindowFrame"/>, not a
    /// result — so a companion dialog carries the Accept/Reject buttons.
    /// </remarks>
    internal sealed class VsDiffPresenter : IDiffPresenter
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Action<string> _log;

        public VsDiffPresenter(IServiceProvider serviceProvider, Action<string> log)
        {
            _serviceProvider = serviceProvider;
            _log = log ?? (_ => { });
        }

        public async Task<DiffOutcome> PresentAsync(string filePath, string newContents)
        {
            if (string.IsNullOrEmpty(filePath))
                return DiffOutcome.CouldNotPresent("no file path");

            var differenceService = _serviceProvider.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (differenceService == null)
                return DiffOutcome.CouldNotPresent("IVsDifferenceService is unavailable");

            // The diff needs both sides on disk. For a file that does not exist yet, stage an
            // empty stand-in instead of creating the real file: an earlier version wrote the
            // target (and its parent directories) before asking anything, so rejecting a
            // proposed new file still left a zero-byte file and a new directory tree in the
            // user's repository. VS never creates a file just because a diff names it, so the
            // moniker does not have to exist on disk.
            ProposedFile stagedEmpty = null;
            var leftMoniker = filePath;
            if (!System.IO.File.Exists(filePath))
            {
                try
                {
                    stagedEmpty = new ProposedFile(filePath, string.Empty);
                    leftMoniker = stagedEmpty.Path;
                }
                catch (Exception ex)
                {
                    return DiffOutcome.CouldNotPresent("cannot stage an empty left side: " + ex.Message);
                }
            }

            try
            {
                using (var proposed = new ProposedFile(filePath, newContents))
                {
                    var caption = System.IO.Path.GetFileName(filePath) + " — DeepSeek Harness proposed change";

                    IVsWindowFrame frame;
                    try
                    {
                        // roles: left = the real file, right = the proposed content. Labels show
                        // up on the two panes so the direction is never ambiguous.
                        frame = differenceService.OpenComparisonWindow2(
                            leftFileMoniker: leftMoniker,
                            rightFileMoniker: proposed.Path,
                            caption: caption,
                            Tooltip: "Accept to write the change, Reject to send it back to the model",
                            leftLabel: "Current (on disk)",
                            rightLabel: "Proposed (DeepSeek Harness)",
                            inlineLabel: null,
                            roles: null,
                            grfDiffOptions: 0);
                    }
                    catch (Exception ex)
                    {
                        _log("OpenComparisonWindow2 failed: " + ex);
                        return DiffOutcome.CouldNotPresent(ex.Message);
                    }

                    if (frame == null)
                        return DiffOutcome.CouldNotPresent("diff window was not created");

                    try { frame.Show(); }
                    catch (Exception ex) { _log("diff frame Show failed: " + ex); }

                    try
                    {
                        var decision = await DecisionDialog.ShowAsync(filePath).ConfigureAwait(true);
                        return decision;
                    }
                    finally
                    {
                        // Leave no stale diff tab behind, whatever the user chose.
                        try { frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
                        catch (Exception ex) { _log("CloseFrame failed: " + ex); }
                    }
                }
            }
            finally
            {
                if (stagedEmpty != null) stagedEmpty.Dispose();
            }
        }
    }
}
