using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Documentation;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationWorkspaceTests
{
    [Fact]
    public void Capture_uses_the_current_index_and_real_scoped_commit_history()
    {
        using var repo = new DocumentationRepositoryFixture();
        var input = DocumentationWorkspace.Capture(repo.Root, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken);
        var unit = Assert.Single(input.Units);
        Assert.Equal(repo.Identity.HeadCommit + ":" + repo.Identity.HeadTree, input.Snapshot);
        Assert.Contains(unit.Content.History, h => h.Commit == repo.Identity.HeadCommit && h.Message.Contains("initial public API", StringComparison.Ordinal));
    }

    [Fact]
    public void Changed_source_without_an_overlay_is_refused()
    {
        using var repo = new DocumentationRepositoryFixture();
        File.AppendAllText(Path.Combine(repo.Root, "src", "Types.cs"), " public class B {}");
        var error = Assert.Throws<DocumentationValidationException>(() => DocumentationWorkspace.Capture(repo.Root, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("index_outdated", error.Errors);
    }

    [Fact]
    public void A_missing_index_is_refused()
    {
        using var repo = new DocumentationRepositoryFixture(publish: false);
        Assert.Throws<DocumentationValidationException>(() => DocumentationWorkspace.Capture(repo.Root, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_prose_only_dirty_file_does_not_require_a_semantic_overlay()
    {
        using var repo = new DocumentationRepositoryFixture();
        File.WriteAllText(Path.Combine(repo.Root, "README.md"), "documentation edit");
        Assert.Single(DocumentationWorkspace.Capture(repo.Root, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken).Units);
    }

    [Fact]
    public void A_worktree_has_the_same_repository_identity_and_can_use_the_same_base_index()
    {
        using var repo = new DocumentationRepositoryFixture();
        var other = repo.Temp.Combine("other");
        repo.Git("worktree", "add", "-b", "other", other);
        Assert.Equal(repo.Identity.RepositoryId, RuntimeIndexLayout.Inspect(other, repo.Runtime).RepositoryId);
        Assert.Single(DocumentationWorkspace.Capture(other, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken).Units);
    }
}

internal sealed class DocumentationRepositoryFixture : IDisposable
{
    internal TemporaryDirectory Temp { get; } = new("localai-docs-repository");
    internal string Root { get; }
    internal string Runtime { get; }
    internal WorkingIndexIdentity Identity { get; }
    internal DocumentationRepositoryFixture(bool publish = true)
    {
        Root = Temp.Combine("repository"); Runtime = Temp.Combine("runtime");
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        File.WriteAllText(Path.Combine(Root, "src", "Types.cs"), "public class A {}");
        Git("init", "-b", "main"); Git("config", "user.name", "Fixture"); Git("config", "user.email", "fixture@example.invalid");
        Git("add", "."); Git("-c", "core.hooksPath=", "commit", "-m", "initial public API");
        Identity = RuntimeIndexLayout.Inspect(Root, Runtime);
        if (!publish) return;
        var generation = new GenerationIdentity(Identity.RepositoryId, Identity.HeadCommit, Identity.HeadTree, "fixture", 2, 1, 1, 1, 1, SemanticIndex.CurrentVersion);
        var index = DocumentationInputsTests.Index("public class A {}") with
        { RepositoryId = Identity.RepositoryId, GenerationId = generation.Id, GitTree = Identity.HeadTree, BaseCommit = Identity.HeadCommit };
        var semantic = Temp.Combine("semantic.sidx"); index.Save(semantic);
        var corpus = Temp.Combine("base.cidx"); File.WriteAllText(corpus, "unused corpus fixture");
        var store = new GenerationStore(Identity.RepositoryRuntimeRoot);
        store.SetCurrent(store.PublishIndex(corpus, generation, semantic));
    }
    internal void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(output, error);
        Assert.True(process.ExitCode == 0, output.Result + error.Result);
    }
    public void Dispose() => Temp.Dispose();
}
