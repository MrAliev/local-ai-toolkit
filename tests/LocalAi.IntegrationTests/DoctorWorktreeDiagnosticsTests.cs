using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_semantic_headers_cannot_claim_precise_navigation(bool unsupportedVersion)
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var generation = Publish(identity, "ready-model", withSemantics: true);
        store.SetCurrent(generation);
        SaveCurrentManifest(identity, generation);
        Assert.Equal("HeaderOnly", Fact(RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow), "worktreeNavigation"));
        var path = store.SemanticIndexPath(generation.Identity.Id);
        if (unsupportedVersion)
        {
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("SIDX"));
            writer.Write(SemanticIndex.CurrentVersion + 1);
        }
        else File.WriteAllText(path, "not a semantic index");

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal("Unknown", Fact(check, "worktreeNavigation"));
        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(true, Fact(check, "worktreeUsable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_corrupt_semantic_overlay_does_not_become_precise_merely_by_existing(bool nestedDirtyMismatch)
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var generation = Publish(identity, "ready-model", withSemantics: true);
        store.SetCurrent(generation);
        SaveCurrentManifest(identity, generation);
        File.WriteAllText(Path.Combine(worktree, "Changed.cs"), "class Changed {}\n");
        var dirty = RuntimeIndexLayout.Inspect(worktree, runtime);
        var overlayPath = RuntimeIndexLayout.OverlayPath(dirty, generation.Identity.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        new CodeIndex
        {
            Root = worktree, Model = "ready-model", Dim = 2, GitCommit = dirty.HeadCommit,
            GitTree = dirty.HeadTree, RepositoryId = dirty.RepositoryId, GenerationId = generation.Identity.Id,
            DirtyHash = dirty.DirtyHash, BaseCommit = identity.HeadCommit,
            IndexedAtUtc = DateTime.UtcNow, Files = [], Chunks = [], Vectors = [],
        }.Save(overlayPath);
        var semanticPath = RuntimeIndexLayout.SemanticOverlayPath(dirty, generation.Identity.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(semanticPath)!);
        if (!nestedDirtyMismatch) File.WriteAllText(semanticPath, "broken semantic overlay");
        else
        {
            new SemanticIndexOverlay
            {
                RepositoryId = dirty.RepositoryId, GenerationId = generation.Identity.Id,
                BaseGitTree = identity.HeadTree, GitTree = dirty.HeadTree, DirtyHash = dirty.DirtyHash,
                DeletedDocuments = [],
                Changed = new SemanticIndex
                {
                    RepositoryId = dirty.RepositoryId, GenerationId = generation.Identity.Id,
                    GitTree = dirty.HeadTree, DirtyHash = dirty.DirtyHash, BaseCommit = dirty.HeadCommit,
                    IndexedAtUtc = DateTime.UtcNow,
                    Documents = [new SemanticDocument { RelPath = "Changed.cs", Hash = new byte[32] }],
                    Symbols = [], Occurrences = [], Relationships = [],
                },
            }.Save(semanticPath);
            using (var stream = File.Open(semanticPath, FileMode.Open, FileAccess.ReadWrite))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                reader.ReadBytes(4);
                reader.ReadInt32();
                for (var index = 0; index < 4; index++) reader.ReadString();
                if (reader.ReadBoolean()) reader.ReadString();
                var deleted = reader.ReadInt32();
                for (var index = 0; index < deleted; index++) reader.ReadString();
                reader.ReadBytes(4);
                reader.ReadInt32();
                for (var index = 0; index < 3; index++) reader.ReadString();
                Assert.True(reader.ReadBoolean());
                var length = reader.Read7BitEncodedInt();
                Assert.Equal(dirty.DirtyHash!.Length, length);
                var different = (dirty.DirtyHash[0] == 'a' ? "b" : "a") + dirty.DirtyHash[1..];
                stream.Write(System.Text.Encoding.UTF8.GetBytes(different));
            }
            Assert.Throws<InvalidDataException>(() => SemanticIndexOverlay.Load(semanticPath));
        }

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal("Unknown", Fact(check, "worktreeNavigation"));
        Assert.Equal(DoctorStatus.Warning, check.Status);
    }

    [Fact]
    public void A_valid_header_with_truncated_semantic_body_is_only_header_evidence_not_readiness()
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var generation = Publish(identity, "ready-model", withSemantics: true);
        store.SetCurrent(generation);
        SaveCurrentManifest(identity, generation);
        var path = store.SemanticIndexPath(generation.Identity.Id);
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            reader.ReadBytes(4);
            reader.ReadInt32();
            for (var index = 0; index < 3; index++) reader.ReadString();
            if (reader.ReadBoolean()) reader.ReadString();
            reader.ReadString();
            reader.ReadInt64();
            reader.ReadInt32();
            stream.SetLength(stream.Position);
        }
        Assert.Equal(1, SemanticIndex.TryReadDocumentCount(path));
        Assert.ThrowsAny<IOException>(() => SemanticIndex.Load(path));

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal("HeaderOnly", Fact(check, "worktreeNavigation"));
        Assert.Equal("Current", Fact(check, "worktreeState"));
        Assert.Equal(DoctorStatus.Unknown, check.Status);
    }

    [Theory]
    [InlineData("{ invalid JSON")]
    [InlineData("{\"SchemaVersion\":42,\"Progress\":null}")]
    public void Unreadable_optional_progress_preserves_known_worktree_facts(string content)
    {
        var (_, worktree) = CreateRepository();
        var runtime = Path.Combine(root, "runtime");
        var identity = RuntimeIndexLayout.Inspect(worktree, runtime);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var generation = Publish(identity, "ready-model");
        store.SetCurrent(generation);
        SaveCurrentManifest(identity, generation);
        File.WriteAllText(Path.Combine(identity.RepositoryRuntimeRoot.Value, "progress.json"), content);

        var check = RepositoryCheck(runtime, worktree, DateTimeOffset.UtcNow);

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal("Unknown", Fact(check, "syncStatus"));
        Assert.Equal("Current", Fact(check, "worktreeState"));
        Assert.Equal(generation.Identity.Id, Fact(check, "worktreeGenerationId"));
        Assert.Equal(worktree, Fact(check, "worktreeRoot"));
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

    private GenerationManifest Publish(WorkingIndexIdentity identity, string model, bool withSemantics = false)
    {
        var generation = new GenerationIdentity(identity.RepositoryId, identity.HeadCommit, identity.HeadTree,
            model, 2, 1, CodeIndex.CurrentVersion, 3, 1, withSemantics ? SemanticIndex.CurrentVersion : 0);
        var source = Path.Combine(root, generation.Id + ".cidx");
        new CodeIndex
        {
            Root = identity.WorkingRoot.Value, Model = model, Dim = 2, GitCommit = identity.HeadCommit,
            GitTree = identity.HeadTree, RepositoryId = identity.RepositoryId, GenerationId = generation.Id,
            IndexedAtUtc = DateTime.UtcNow, Files = [], Chunks = [], Vectors = [],
        }.Save(source);
        string? semanticPath = null;
        if (withSemantics)
        {
            semanticPath = Path.Combine(root, generation.Id + ".sidx");
            new SemanticIndex
            {
                RepositoryId = identity.RepositoryId, GenerationId = generation.Id, GitTree = identity.HeadTree,
                BaseCommit = identity.HeadCommit, IndexedAtUtc = DateTime.UtcNow,
                Documents = [new SemanticDocument { RelPath = "Known.cs", Hash = new byte[32] }],
                Symbols = [], Occurrences = [], Relationships = [],
            }.Save(semanticPath);
        }
        return new GenerationStore(identity.RepositoryRuntimeRoot).PublishIndex(source, generation, semanticPath);
    }

    private static void SaveInitializingManifest(WorkingIndexIdentity identity) =>
        new RepositoryManifestStore(identity.RepositoryRuntimeRoot).Save(new RepositoryManifest(identity.RepositoryId,
            Git(identity.WorkingRoot.Value, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim(),
            "refs/heads/main", null, null, "test-model", 2, 1, CodeIndex.CurrentVersion,
            RepositoryIndexState.Initializing, [], DateTimeOffset.UtcNow));

    private static void SaveCurrentManifest(WorkingIndexIdentity identity, GenerationManifest generation)
    {
        SaveInitializingManifest(identity);
        var store = new RepositoryManifestStore(identity.RepositoryRuntimeRoot);
        store.Save(store.Read()! with { State = RepositoryIndexState.Current,
            CurrentGenerationId = generation.Identity.Id, PublishedGitTree = identity.HeadTree });
    }

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
