using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Documentation;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationWorkspaceTests
{
    private const string MultilineSource = "public class A\n{\n    public int Value => 42;\n}\n";

    [Theory]
    [InlineData("\n", "\r\n")]
    [InlineData("\r\n", "\n")]
    [InlineData("\r\n", "\r\n")]
    public void Clean_line_ending_variants_preserve_documentation_inputs(string checkoutEnding, string indexedEnding)
    {
        using var baseline = new DocumentationRepositoryFixture(source: MultilineSource);
        var expected = Assert.Single(DocumentationWorkspace.Capture(baseline.Root, baseline.Runtime,
            cancellationToken: TestContext.Current.CancellationToken).Units);
        var source = MultilineSource.Replace("\n", checkoutEnding, StringComparison.Ordinal);
        using var repo = new DocumentationRepositoryFixture(source: source,
            indexedSource: MultilineSource.Replace("\n", indexedEnding, StringComparison.Ordinal));
        Assert.Null(RuntimeIndexLayout.Inspect(repo.Root, repo.Runtime).SemanticDirtyHash);

        var actual = Assert.Single(DocumentationWorkspace.Capture(repo.Root, repo.Runtime,
            cancellationToken: TestContext.Current.CancellationToken).Units);

        Assert.Equal(expected.SourceHash, actual.SourceHash);
        Assert.Equal(expected.ReferencesHash, actual.ReferencesHash);
        Assert.Equal("A", actual.Content.SymbolId);
        Assert.Equal(source, File.ReadAllText(Path.Combine(repo.Root, "src", "Types.cs")));
    }

    [Fact]
    public void Line_ending_tolerance_does_not_accept_a_different_source_body()
    {
        using var repo = new DocumentationRepositoryFixture(
            source: MultilineSource.Replace("42", "43", StringComparison.Ordinal),
            indexedSource: MultilineSource.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Null(RuntimeIndexLayout.Inspect(repo.Root, repo.Runtime).SemanticDirtyHash);

        var error = Assert.Throws<DocumentationValidationException>(() => DocumentationWorkspace.Capture(
            repo.Root, repo.Runtime, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("index_outdated", error.Errors);
    }

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
    public void A_limited_capture_does_not_collect_an_unrelated_indexed_file()
    {
        using var repo = new DocumentationRepositoryFixture(extraSources: new Dictionary<string, string>
        {
            ["src/ZUnused.cs"] = "public class ZUnused {}"
        }, extraIndexedSources: new Dictionary<string, string> { ["src/ZUnused.cs"] = "public class ZUnused { int Value => 1; }" });

        var input = DocumentationWorkspace.Capture(repo.Root, repo.Runtime, limit: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("A", Assert.Single(input.Units).Content.SymbolId);
        var allError = Assert.Throws<DocumentationValidationException>(() => DocumentationWorkspace.Capture(
            repo.Root, repo.Runtime, limit: int.MaxValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("index_outdated", allError.Errors);
    }

    [Fact]
    public void A_limited_capture_still_validates_a_required_dependency_file()
    {
        using var repo = new DocumentationRepositoryFixture(source: "public class A : B {}",
            extraSources: new Dictionary<string, string> { ["src/ZDependency.cs"] = "public class B {}" },
            relationship: true, extraIndexedSources: new Dictionary<string, string> { ["src/ZDependency.cs"] = "public class B { int Value => 1; }" });

        var error = Assert.Throws<DocumentationValidationException>(() => DocumentationWorkspace.Capture(
            repo.Root, repo.Runtime, limit: 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("index_outdated", error.Errors);
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
    internal DocumentationRepositoryFixture(bool publish = true, string source = "public class A {}", string? indexedSource = null,
        IReadOnlyDictionary<string, string>? extraSources = null, bool relationship = false,
        IReadOnlyDictionary<string, string>? extraIndexedSources = null)
    {
        Root = Temp.Combine("repository"); Runtime = Temp.Combine("runtime");
        Directory.CreateDirectory(Path.Combine(Root, "src"));
        File.WriteAllText(Path.Combine(Root, "src", "Types.cs"), source);
        foreach (var (path, content) in extraSources ?? new Dictionary<string, string>())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, path))!);
            File.WriteAllText(Path.Combine(Root, path), content);
        }
        Git("init", "-b", "main"); Git("config", "user.name", "Fixture"); Git("config", "user.email", "fixture@example.invalid");
        Git("config", "core.autocrlf", "true"); Git("config", "core.safecrlf", "false");
        Git("add", "."); Git("-c", "core.hooksPath=", "commit", "-m", "initial public API");
        Identity = RuntimeIndexLayout.Inspect(Root, Runtime);
        if (!publish) return;
        var generation = new GenerationIdentity(Identity.RepositoryId, Identity.HeadCommit, Identity.HeadTree, "fixture", 2, 1, 1, 1, 1, SemanticIndex.CurrentVersion);
        var index = DocumentationInputsTests.Index(indexedSource ?? source, relationship) with
        { RepositoryId = Identity.RepositoryId, GenerationId = generation.Id, GitTree = Identity.HeadTree, BaseCommit = Identity.HeadCommit };
        foreach (var (path, content) in extraSources ?? new Dictionary<string, string>())
        {
            var extra = DocumentationInputsTests.Index(extraIndexedSources?.GetValueOrDefault(path) ?? content);
            index.Documents.AddRange(extra.Documents.Select(document => document with { RelPath = path }));
            index.Symbols.AddRange(extra.Symbols);
            index.Occurrences.AddRange(extra.Occurrences.Select(occurrence => occurrence with { DocumentPath = path }));
        }
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
