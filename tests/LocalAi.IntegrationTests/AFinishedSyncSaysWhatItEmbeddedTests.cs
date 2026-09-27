using System.Diagnostics;
using System.Globalization;
using CodeSearch.Core.Embedding;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `repo status --follow` reads the record once an interval, and a phase that starts and ends
/// between two readings never reaches it: the records after embedding carry no chunk counts. Two
/// base syncs of this repository reported 413 and 144 chunks after embedding 1226 and 1181 (#774).
/// The sync itself sees every chunk it embeds, so the figure belongs in its terminal record.
/// </summary>
public sealed class AFinishedSyncSaysWhatItEmbeddedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-embedded-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-sync-embedded-runtime-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A base and an overlay, because the figure is their sum: the committed file is embedded into
    /// the base, and the uncommitted edit to it into this worktree's overlay. The loop is then
    /// handed the terminal record alone, as a loop whose readings caught none of the embedding.
    /// </summary>
    [Fact]
    public async Task The_terminal_record_carries_every_chunk_the_sync_embedded()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "LocalAi Tests");
        var notes = Path.Combine(_root, "notes.md");
        File.WriteAllLines(notes, Lines("committed", 150));
        Git("add", ".");
        Git("commit", "-m", "notes");
        File.AppendAllLines(notes, Lines("edited", 40));
        var embedder = new CountingEmbedder();
        var output = new StringWriter();

        var result = await CodeSearchSyncCommand.ExecuteAsync(
            _root,
            output,
            cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: _runtimeRoot,
            embedder: embedder);

        Assert.True(result.OverlaysBuilt == 1, output.ToString());
        var recorded = new RepositoryIndexProgressStore(FsPath.From(Path.Combine(
                _runtimeRoot,
                "repositories",
                result.RepositoryId)))
            .Read();
        Assert.NotNull(recorded);
        Assert.Equal(RepositoryIndexProgressPhase.Completed, recorded.Phase);
        Assert.Equal(embedder.Embedded, recorded.ChunksEmbedded);

        var step = RepoFollowCommand.Step(recorded, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        Assert.Equal(0, step.ExitCode);
        Assert.Contains(
            embedder.Embedded.ToString(CultureInfo.InvariantCulture),
            step.Line,
            StringComparison.Ordinal);
    }

    private static IEnumerable<string> Lines(string prefix, int count) =>
        Enumerable.Range(1, count).Select(line => $"{prefix} line {line} of the notes.");

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            // Git leaves its object files read-only, and a recursive delete refuses them.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git exited with {process.ExitCode}: {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Every text it was asked to embed, counted: what the sync embedded, told by the side that
    /// did the work rather than by the code under test. The model is the sync's own, because an
    /// overlay refuses a base embedded with another one.
    /// </summary>
    private sealed class CountingEmbedder : IEmbeddingClient
    {
        private int _embedded;

        public string Model => CodeSearchSyncCommand.DefaultModel;

        public int Embedded => Volatile.Read(ref _embedded);

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _embedded, inputs.Count);
            return Task.FromResult(inputs
                .Select(_ =>
                {
                    var vector = new float[CodeSearchSyncCommand.DefaultDimension];
                    vector[0] = 1f;
                    return vector;
                })
                .ToArray());
        }
    }
}
