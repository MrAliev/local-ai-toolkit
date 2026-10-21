using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

public sealed class DoctorWorktreeDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-doctor-worktree-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repository_diagnostics_identify_the_requested_linked_worktree(bool explicitRoot)
    {
        var (_, worktree) = CreateRepository();

        var report = explicitRoot
            ? DoctorCommand.Inspect(Path.Combine(root, "runtime"), repositoryRoot: worktree)
            : DoctorCommand.Inspect(Path.Combine(root, "runtime"), workingDirectory: worktree);

        var check = Assert.Single(report.Checks, check => check.Name == "repository");
        Assert.Equal(Path.GetFullPath(worktree), Assert.Single(check.Facts!, fact => fact.Name == "worktreeRoot").Value);
    }

    [Fact]
    public void A_published_worktree_is_distinguished_from_interrupted_shared_sync_without_mutating_either()
    {
        var (repository, worktree) = CreateRepository();
        var now = DateTimeOffset.UtcNow;
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        store.SetCurrent(Publish(identity, "previous-model"));
        var expectedCurrent = store.ReadCurrent();
        var published = Publish(identity, "ready-model");
        Assert.True(WorktreeGenerationPublications.Publish(identity, published, expectedCurrent));
        var manifestStore = new RepositoryManifestStore(identity.RepositoryRuntimeRoot);
        var manifest = new RepositoryManifest(identity.RepositoryId,
            Git(repository, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim(), "refs/heads/main",
            null, null, "ready-model", 2, 1, CodeIndex.CurrentVersion, RepositoryIndexState.Initializing,
            [], now - TimeSpan.FromMinutes(20));
        manifestStore.Save(manifest);
        var progressStore = new RepositoryIndexProgressStore(identity.RepositoryRuntimeRoot);
        var progress = new RepositoryIndexProgress(identity.RepositoryId, RepositoryIndexProgressPhase.SemanticOverlay,
            repository, 0, 0, 0, null, now - TimeSpan.FromMinutes(10),
            Step: IndexBuildStep.AnalysingProjects, StepDone: 10, StepTotal: 28);
        progressStore.Save(progress);

        var check = RepositoryCheck(runtime, worktree, now);

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal("Initializing", Fact(check, "state"));
        Assert.DoesNotContain(check.Facts!, fact => fact.Name == "generationId");
        Assert.Equal("Current", Fact(check, "worktreeState"));
        Assert.Equal(true, Fact(check, "worktreeUsable"));
        Assert.Equal(published.Identity.Id, Fact(check, "worktreeGenerationId"));
        Assert.Equal("Interrupted", Fact(check, "syncStatus"));
        Assert.Equal(repository, Fact(check, "syncWorkingRoot"));
        Assert.Equal("SemanticOverlay", Fact(check, "syncPhase"));
        Assert.Equal(10, Fact(check, "syncStepDone"));
        Assert.Equal(28, Fact(check, "syncStepTotal"));
        Assert.Contains(worktree, check.Detail, StringComparison.Ordinal);
        Assert.Contains(repository, check.Detail, StringComparison.Ordinal);
        Assert.Equal(expectedCurrent, store.ReadCurrent());
        Assert.Equal(progress, progressStore.Read());
        Assert.Equal(manifest.UpdatedAtUtc, manifestStore.Read()!.UpdatedAtUtc);
    }

    [Fact]
    public void A_worktree_publication_cannot_override_a_new_repository_pointer_epoch()
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        store.SetCurrent(Publish(identity, "previous-model"));
        var published = Publish(identity, "published-model");
        Assert.True(WorktreeGenerationPublications.Publish(identity, published, store.ReadCurrent()));
        var replacement = Publish(identity, "replacement-model");
        store.SetCurrent(replacement);
        SaveInitializingManifest(identity);

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal(replacement.Identity.Id, Fact(check, "worktreeGenerationId"));
        Assert.NotEqual(published.Identity.Id, Fact(check, "worktreeGenerationId"));
    }

    [Fact]
    public void Dirty_changes_without_a_matching_overlay_are_not_reported_as_current()
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        store.SetCurrent(Publish(identity, "ready-model"));
        SaveInitializingManifest(identity);
        File.WriteAllText(Path.Combine(worktree, "Changed.cs"), "class Changed {}\n");

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal("NeedsOverlay", Fact(check, "worktreeState"));
        Assert.Equal(false, Fact(check, "worktreeUsable"));
    }

    [Theory]
    [InlineData(RepositoryIndexProgressPhase.SemanticOverlay, 1, "Recent")]
    [InlineData(RepositoryIndexProgressPhase.Completed, 10, "Completed")]
    [InlineData(RepositoryIndexProgressPhase.Failed, 1, "Failed")]
    public void Recent_and_terminal_progress_remain_separate_from_shared_repository_readiness(
        RepositoryIndexProgressPhase phase, int ageMinutes, string expectedStatus)
    {
        var (repository, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var now = DateTimeOffset.UtcNow;
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        new GenerationStore(identity.RepositoryRuntimeRoot).SetCurrent(Publish(identity, "ready-model"));
        SaveInitializingManifest(identity);
        new RepositoryIndexProgressStore(identity.RepositoryRuntimeRoot).Save(new RepositoryIndexProgress(
            identity.RepositoryId, phase, repository, 0, 0, 0, null, now - TimeSpan.FromMinutes(ageMinutes)));

        var check = RepositoryCheck(runtime, worktree, now);

        Assert.Equal(expectedStatus, Fact(check, "syncStatus"));
        Assert.Equal("Current", Fact(check, "worktreeState"));
        Assert.Equal("Initializing", Fact(check, "state"));
        Assert.Equal(DoctorStatus.Warning, check.Status);
    }

    private (string Repository, string Worktree) CreateRepository()
    {
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-b", "main");
        Git(repository, "-c", "user.name=LocalAI Tests", "-c", "user.email=localai@example.invalid", "commit", "--allow-empty", "-m", "Initial");
        var worktree = Path.Combine(root, "linked");
        Git(repository, "worktree", "add", "-b", "topic", worktree);
        return (repository, worktree);
    }

    private GenerationManifest Publish(WorkingIndexIdentity identity, string model)
    {
        var generation = new GenerationIdentity(identity.RepositoryId, identity.HeadCommit, identity.HeadTree,
            model, 2, 1, CodeIndex.CurrentVersion, 3, 1);
        var source = Path.Combine(root, generation.Id + ".cidx");
        new CodeIndex
        {
            Root = identity.WorkingRoot.Value, Model = model, Dim = 2, GitCommit = identity.HeadCommit,
            GitTree = identity.HeadTree, RepositoryId = identity.RepositoryId, GenerationId = generation.Id,
            IndexedAtUtc = DateTime.UtcNow, Files = [], Chunks = [], Vectors = [],
        }.Save(source);
        return new GenerationStore(identity.RepositoryRuntimeRoot).PublishIndex(source, generation);
    }

    private static void SaveInitializingManifest(WorkingIndexIdentity identity) =>
        new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Save(new RepositoryManifest(identity.RepositoryId,
            identity.RepositoryRoot.Value, "refs/heads/main", null, null, "test-model", 2, 1, CodeIndex.CurrentVersion,
            RepositoryIndexState.Initializing, [], DateTimeOffset.UtcNow));

    private static DoctorCheck RepositoryCheck(string runtime, string worktree, DateTimeOffset now) =>
        Assert.Single(DoctorCommand.Inspect(runtime, repositoryRoot: worktree, now: now).Checks, check => check.Name == "repository");

    private static object? Fact(DoctorCheck check, string name) =>
        Assert.Single(check.Facts!, fact => fact.Name == name).Value;

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        // Git deliberately makes its own object files read-only on Windows.
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }
}
