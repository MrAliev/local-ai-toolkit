using System.Diagnostics;
using System.IO.Compression;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

public sealed class CommitSnapshot : IDisposable
{
    private readonly string _temporaryRoot;

    private CommitSnapshot(string temporaryRoot, string root)
    {
        _temporaryRoot = temporaryRoot;
        Root = root;
    }

    public string Root { get; }

    public static CommitSnapshot Create(string repositoryRoot, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "localai-snapshot-" + Guid.NewGuid().ToString("N"));
        var contentRoot = Path.Combine(temporaryRoot, "content");
        var archivePath = Path.Combine(temporaryRoot, "snapshot.zip");
        Directory.CreateDirectory(contentRoot);
        try
        {
            using var process = Process.Start(ArchiveStartInfo(repositoryRoot, archivePath, revision))
                ?? throw new InvalidOperationException("Could not start git archive.");
            // Closed at once. Redirecting it is what stops git inheriting the caller's; closing
            // it is what turns a read into an immediate end of file instead of a wait.
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ArchiveTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    $"git archive did not finish within {ArchiveTimeoutMs / 1000} seconds.");
            }

            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git archive exited with {process.ExitCode}: {stderr.Result.Trim()}");
            }

            ZipFile.ExtractToDirectory(archivePath, contentRoot);
            File.Delete(archivePath);
            return new CommitSnapshot(temporaryRoot, contentRoot);
        }
        catch
        {
            Directory.Delete(temporaryRoot, recursive: true);
            throw;
        }
    }


    /// <summary>
    /// A runaway guard, not an expectation, in the sense <c>RepoLocator.GitTimeoutMs</c> gives
    /// the phrase. Ten minutes rather than that method's two, because this git walks the whole
    /// tree where that one answers about a single ref — and either number turns "for ever" into
    /// a failure somebody can read, which is the property that matters.
    /// </summary>
    private const int ArchiveTimeoutMs = 600_000;

    /// <summary>
    /// How the archive is asked for. Separate so a test can read it: the streams are the whole
    /// point, and the one that matters is standard input.
    /// </summary>
    internal static ProcessStartInfo ArchiveStartInfo(
        string repositoryRoot,
        string archivePath,
        string revision)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
            // Redirected so git is handed a stream of ours rather than the caller's. Under an
            // MCP server the caller's is the client's JSON-RPC pipe, which never closes and
            // never delivers, and a child that reads it waits for ever — observed as a thirty
            // minute wedge in `index_refresh` holding the repository's sync gate.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Git writes UTF-8 on every platform, so this says so rather than inheriting
            // whatever the parent chose for printing its own output.
            StandardOutputEncoding = ChildProcessText.Utf8,
            StandardErrorEncoding = ChildProcessText.Utf8,
        };
        start.ArgumentList.Add("archive");
        start.ArgumentList.Add("--format=zip");
        start.ArgumentList.Add($"--output={archivePath}");
        start.ArgumentList.Add(revision);
        return start;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}
