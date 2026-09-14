using LocalAi.Documentation;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationPackStoreTests
{
    [Fact]
    public async Task Build_writes_outside_the_worktree_and_offers_both_review_languages()
    {
        using var fixture = new Fixture();
        var report = await fixture.Build();
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(report.PackagePath, "index.en.html")));
        Assert.True(File.Exists(Path.Combine(report.PackagePath, "index.ru.html")));
        Assert.Single(report.Units);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Unchanged_input_reuses_the_exact_artifact_without_a_model_call()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        var second = await fixture.Build();
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, second.Reused);
        Assert.Equal(first.Units[0].ArtifactId, second.Units[0].ArtifactId);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("references")]
    [InlineData("history")]
    public async Task Each_changed_input_is_reported_with_its_reason(string reason)
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var original = Fixture.Unit;
        var changed = reason switch { "source" => original with { SourceHash = "changed" }, "references" => original with { ReferencesHash = "changed" }, _ => original with { HistoryHash = "changed" } };
        var report = await fixture.Build([changed]);
        Assert.Equal(2, fixture.Calls);
        Assert.Contains(reason, Assert.Single(report.Units).Reasons);
    }

    [Fact]
    public async Task Failed_validation_keeps_the_previous_document_and_reports_the_failure()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        fixture.Invalid = true;
        var report = await fixture.Build([Fixture.Unit with { SourceHash = "changed" }]);
        var unit = Assert.Single(report.Units);
        Assert.Equal(1, report.Failed);
        Assert.True(unit.PreviousKept);
        Assert.Equal(first.Units[0].ArtifactId, unit.ArtifactId);
        Assert.Contains("missing_claims", unit.Error!);
    }

    [Fact]
    public async Task A_failed_first_document_is_reported_without_an_artifact()
    {
        using var fixture = new Fixture { Invalid = true };
        var unit = Assert.Single((await fixture.Build()).Units);
        Assert.NotNull(unit.Error);
        Assert.False(unit.PreviousKept);
        Assert.Null(unit.ArtifactId);
    }

    [Fact]
    public async Task Completed_units_survive_interruption_without_replacing_the_published_pack()
    {
        using var fixture = new Fixture();
        var other = Fixture.Unit with { Content = Fixture.Unit.Content with { SymbolId = "type:B", Name = "Example.B" } };
        var input = new DocumentationBuildInput(fixture.Worktree, "tree", [Fixture.Unit, other]);
        var calls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.BuildAsync(input, (_, _) =>
        {
            if (++calls == 2) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.FromResult(DocumentationValidationTests.Draft);
        }, cancellationToken: cancellation.Token));
        Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        var resumed = await fixture.Build([Fixture.Unit, other]);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(1, resumed.Reused);
    }

    [Fact]
    public async Task Modified_cache_bytes_are_not_reused()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        var page = Assert.Single(Directory.GetFiles(fixture.Runtime, "page.en.md", SearchOption.AllDirectories));
        File.AppendAllText(page, "modified");
        var next = await fixture.Build();
        Assert.Equal(2, fixture.Calls);
        Assert.Equal(0, next.Reused);
        Assert.NotEqual(first.Units[0].ArtifactId, next.Units[0].ArtifactId);
    }

    [Fact]
    public async Task Apply_copies_once_and_preserves_hand_edits_on_the_next_apply()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var applied = fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Equal(9, applied.Copied);
        Assert.Equal(1, fixture.Calls);
        var page = Assert.Single(Directory.GetFiles(fixture.Worktree, "page.en.md", SearchOption.AllDirectories));
        File.WriteAllText(page, "manual edit");
        var again = fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Single(again.Skipped);
        Assert.Equal(0, again.Copied);
        Assert.Equal(8, again.Unchanged);
        Assert.Equal("manual edit", File.ReadAllText(page));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Apply_refuses_a_pack_for_different_source_state()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "different-tree"));
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Runtime_inside_the_repository_is_refused_before_writing()
    {
        using var fixture = new Fixture();
        var store = new DocumentationPackStore(Path.Combine(fixture.Worktree, "runtime"), new string('a', 64));
        await Assert.ThrowsAsync<DocumentationValidationException>(() => store.BuildAsync(new(fixture.Worktree, "tree", [Fixture.Unit]),
            (_, _) => Task.FromResult(DocumentationValidationTests.Draft), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Reused_documents_remain_in_the_contents_but_not_in_the_changes_report()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var reused = await fixture.Build();
        var html = File.ReadAllText(Path.Combine(reused.PackagePath, "index.en.html"));
        Assert.Contains("No documents changed.", html);
        Assert.Contains("Documents in this package", html);
        Assert.Contains("/page.en.pdf", html);
        Assert.DoesNotContain("<h3>", html);
    }

    [Fact]
    public async Task A_generator_version_change_rebuilds_the_document()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var store = new DocumentationPackStore(fixture.Runtime, new string('a', 64), "fixture-2");
        var result = await store.BuildAsync(new(fixture.Worktree, "tree", [Fixture.Unit]), (_, _) => Task.FromResult(DocumentationValidationTests.Draft), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("generator", Assert.Single(result.Units).Reasons);
        Assert.False(result.Units[0].Reused);
    }

    [Fact]
    public async Task Apply_reports_the_exact_symbolic_link_and_does_not_write_through_it()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var external = Path.Combine(fixture.Runtime, "external"); Directory.CreateDirectory(external);
        var docs = Path.Combine(fixture.Worktree, "docs");
        if (OperatingSystem.IsWindows())
        {
            // Directory junctions exercise the same reparse-point guard without the
            // elevated privilege needed to create a Windows symbolic link.
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{docs}\" \"{external}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var failure = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await failure);
        }
        else Directory.CreateSymbolicLink(docs, external);
        var error = Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        Assert.Equal(docs, error.Data["path"]);
        Assert.Empty(Directory.GetFiles(external));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory temp = new("localai-documentation-pack");
        internal static DocumentationUnitInput Unit => new(DocumentationValidationTests.Input, "source", "references", "history");
        internal string Worktree { get; }
        internal string Runtime { get; }
        internal DocumentationPackStore Store { get; }
        internal int Calls;
        internal bool Invalid;
        internal Fixture()
        {
            Worktree = temp.Combine("repository"); Runtime = temp.Combine("runtime");
            Directory.CreateDirectory(Worktree);
            Store = new(Runtime, new string('a', 64), "fixture-1");
        }
        internal Task<DocumentationBuildReport> Build(DocumentationUnitInput[]? units = null) => Store.BuildAsync(
            new(Worktree, "tree", units ?? [Unit]), (_, _) => { Calls++; return Task.FromResult(Invalid ? DocumentationValidationTests.Draft with { Claims = [] } : DocumentationValidationTests.Draft); },
            cancellationToken: TestContext.Current.CancellationToken);
        public void Dispose() => temp.Dispose();
    }
}
