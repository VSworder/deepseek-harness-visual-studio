using System;
using System.IO;
using System.Threading.Tasks;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Opens the native Visual Studio diff window for a proposed file change and waits
    /// for the user's verdict.
    /// </summary>
    /// <remarks>
    /// The right-hand side is a temporary file holding the proposed content, because
    /// <c>IVsDifferenceService.OpenComparisonWindow2</c> addresses both sides by file
    /// moniker. The temp file keeps the original extension so the diff viewer applies
    /// the same syntax colouring and language service as the real file.
    /// </remarks>
    public interface IDiffPresenter
    {
        /// <summary>
        /// Shows <paramref name="newContents"/> against the current file on disk and
        /// returns the user's decision. Must not throw for user-driven outcomes;
        /// a failure to present should yield <see cref="DiffOutcome.CouldNotPresent"/>.
        /// </summary>
        Task<DiffOutcome> PresentAsync(string filePath, string newContents);
    }

    /// <summary>What the user did with a presented diff.</summary>
    public sealed class DiffOutcome
    {
        public DiffVerdict Verdict { get; private set; }

        /// <summary>Optional explanation the user typed when rejecting.</summary>
        public string Reason { get; private set; }

        /// <summary>Set when the diff could not be shown at all; callers should decline to gate.</summary>
        public string Error { get; private set; }

        public static DiffOutcome Accept() => new DiffOutcome { Verdict = DiffVerdict.Accept };

        public static DiffOutcome Reject(string reason) =>
            new DiffOutcome { Verdict = DiffVerdict.Reject, Reason = reason };

        public static DiffOutcome CouldNotPresent(string error) =>
            new DiffOutcome { Verdict = DiffVerdict.Unavailable, Error = error };
    }

    public enum DiffVerdict
    {
        Accept,
        Reject,
        /// <summary>The diff could not be shown; the caller must not treat this as consent.</summary>
        Unavailable
    }

    /// <summary>
    /// Writes the proposed content to a temp file whose name mirrors the target, so the
    /// diff viewer picks the right language. Disposing deletes the temp file.
    /// </summary>
    internal sealed class ProposedFile : IDisposable
    {
        public string Path { get; }

        public ProposedFile(string targetPath, string contents)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeepSeekHarness", "proposed");
            Directory.CreateDirectory(directory);

            var name = System.IO.Path.GetFileName(targetPath);
            if (string.IsNullOrEmpty(name)) name = "proposed.txt";

            // Unique per call: two concurrent reviews of the same file must not collide.
            Path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + name);
            File.WriteAllText(Path, contents ?? string.Empty, new System.Text.UTF8Encoding(false));
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path)) File.Delete(Path);
            }
            catch (IOException) { /* a leftover temp file is harmless */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
