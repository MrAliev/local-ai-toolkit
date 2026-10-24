using System.Diagnostics;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class SyncOperationTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "localai-sync-operation-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_area, "repo");
    private string Other => Path.Combine(_area, "other");
    private string Runtime => Path.Combine(_area, "runtime");

    [Fact]
    public async Task Admission_counts_the_base_and_every_explicit_linked_pass_before_any_embedding()
    {
        Prepare(linked: true, extraFiles: 100);
        File.AppendAllText(Path.Combine(Root, "notes.md"), "\nrequested edit");
        File.AppendAllText(Path.Combine(Other, "notes.md"), "\nother edit");
        var embedder = new FakeEmbedder();
        using var output = new StringWriter();
        var scoped = await CodeSearchSyncCommand.ExecuteAsync(Root, output, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken,
            refuseInlineOverFiles: 0, embedder: embedder);
        var maintenance = await CodeSearchSyncCommand.ExecuteAsync(Root, output, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken,
            refuseInlineOverFiles: 200, embedder: embedder, allWorktrees: true);

        Assert.Equal(202, scoped.RefusedFiles);
        Assert.Equal(303, maintenance.RefusedFiles);
        Assert.Equal(0, embedder.Calls);
        Assert.Contains("estimate=upper-bound files=303 limit=200 scope=all-worktrees", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFiles(Runtime, "*.sidx", SearchOption.AllDirectories).Any());
        Assert.Null(new RepositoryIndexProgressStore(Identity().RepositoryRuntimeRoot).Read());
        Assert.Equal("Refused", new RepositorySyncOperationStore(Identity().RepositoryRuntimeRoot).Read()!.State);
    }

    [Fact]
    public async Task A_new_model_generation_is_budgeted_even_when_file_contents_are_unchanged()
    {
        Prepare();
        var embedder = new FakeEmbedder();
        await CodeSearchSyncCommand.ExecuteAsync(Root, TextWriter.Null, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken, embedder: embedder);
        var calls = embedder.Calls;
        var result = await CodeSearchSyncCommand.ExecuteAsync(Root, TextWriter.Null, model: "different-model",
            runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken, refuseInlineOverFiles: 0, embedder: embedder);
        Assert.Equal(1, result.RefusedFiles);
        Assert.Equal(calls, embedder.Calls);
    }

    [Fact]
    public async Task Whole_operation_metadata_keeps_the_origin_and_passes_separate_from_phase_figures()
    {
        Prepare(linked: true);
        File.AppendAllText(Path.Combine(Root, "notes.md"), "\nrequested edit");
        var id = Guid.NewGuid();
        using var output = new StringWriter();
        await CodeSearchSyncCommand.ExecuteAsync(Root, output, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken, embedder: new FakeEmbedder(),
            allWorktrees: true, operationId: id);
        var operation = new RepositorySyncOperationStore(Identity().RepositoryRuntimeRoot).Read();
        Assert.NotNull(operation);
        Assert.Equal(id, operation.OperationId);
        Assert.Equal(Path.GetFullPath(Root), operation.RequestedRoot);
        Assert.Equal(3, operation.TotalPasses);
        Assert.Equal(3, operation.CompletedPasses);
        Assert.Equal("Completed", operation.State);
        Assert.Contains("pass=2/3", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("counters=current-pass whole_chunks=UNKNOWN whole_rate=UNKNOWN whole_eta=UNKNOWN", output.ToString(), StringComparison.Ordinal);
        // The original strict progress contract remains independently readable.
        Assert.Equal(RepositoryIndexProgressPhase.Completed,
            new RepositoryIndexProgressStore(Identity().RepositoryRuntimeRoot).Read()!.Phase);
    }

    [Fact]
    public async Task Cancellation_targets_the_exact_originating_operation_and_reaches_the_embedder_token()
    {
        Prepare();
        var embedder = new FakeEmbedder(wait: true);
        var id = Guid.NewGuid();
        var sync = CodeSearchSyncCommand.ExecuteAsync(Root, TextWriter.Null, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken,
            embedder: embedder, operationId: id);
        await embedder.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var store = new RepositorySyncOperationStore(Identity().RepositoryRuntimeRoot);
        Assert.False(store.RequestCancellation(Guid.NewGuid()));
        Assert.False(sync.IsCompleted);
        Assert.True(store.RequestCancellation(id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled", store.Read()!.State);
        Assert.False(store.RequestCancellation(id));
        Assert.True(embedder.TokenCancelled);
    }

    [Fact]
    public async Task An_embedding_failure_records_a_terminal_operation_without_claiming_cancellation()
    {
        Prepare();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => CodeSearchSyncCommand.ExecuteAsync(Root,
            TextWriter.Null, runtimeRoot: Runtime, cancellationToken: TestContext.Current.CancellationToken,
            embedder: new FakeEmbedder(fail: true)));
        Assert.Contains("fake_embedding_failure", failure.ToString(), StringComparison.Ordinal);
        var operation = new RepositorySyncOperationStore(Identity().RepositoryRuntimeRoot).Read();
        Assert.Equal("Failed", operation!.State);
    }

    private WorkingIndexIdentity Identity() => RuntimeIndexLayout.Inspect(Root, Runtime);

    private void Prepare(bool linked = false, int extraFiles = 0)
    {
        Directory.CreateDirectory(Root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "Local AI Tests");
        File.WriteAllText(Path.Combine(Root, "notes.md"), "Initial repository notes.");
        for (var i = 0; i < extraFiles; i++) File.WriteAllText(Path.Combine(Root, $"notes-{i}.md"), "Notes.");
        Git("add", ".");
        Git("commit", "-m", "initial");
        if (linked) Git("worktree", "add", "-b", "other", Other);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error + output);
    }

    public void Dispose()
    {
        // The only recursive target is this test's freshly allocated temporary directory.
        var target = Path.GetFullPath(_area);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test cleanup target.");
        if (!Directory.Exists(target)) return;
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(target, recursive: true);
    }

    private sealed class FakeEmbedder(bool wait = false, bool fail = false) : IEmbeddingClient
    {
        public string Model => CodeSearchSyncCommand.DefaultModel;
        public int Calls { get; private set; }
        public bool TokenCancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, LocalJobPriority priority,
            string deduplicationKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            if (fail) throw new InvalidOperationException("fake_embedding_failure");
            if (wait)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { TokenCancelled = true; throw; }
            }
            return inputs.Select(_ =>
            {
                var vector = new float[CodeSearchSyncCommand.DefaultDimension];
                vector[0] = 1;
                return vector;
            }).ToArray();
        }
    }
}
