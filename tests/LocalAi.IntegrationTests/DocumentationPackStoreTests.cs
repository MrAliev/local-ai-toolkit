using LocalAi.Documentation;
using LocalAi.Tests.Shared;
using LocalAi.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationPackStoreTests
{
    [Fact]
    public async Task Changing_html_mode_rerenders_without_generation_and_keeps_the_old_pack_readable()
    {
        using var fixture = new Fixture();
        var shared = await fixture.Build();
        var standalone = new DocumentationPackStore(fixture.Runtime, new string('a', 64), "fixture-1", htmlMode: DocumentationHtmlMode.Standalone);
        var changed = await fixture.Build(store: standalone);
        Assert.Equal(1, fixture.Calls);
        Assert.Contains("renderer", changed.Units[0].Reasons);
        Assert.NotEqual(shared.Units[0].ArtifactId, changed.Units[0].ArtifactId);
        Assert.Equal(1, (await fixture.Build(store: standalone)).Reused);
        Assert.Equal(9, standalone.Apply(fixture.Worktree, "tree").Copied);
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*.css", SearchOption.AllDirectories));
        var html = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Worktree, "page.en.html", SearchOption.AllDirectories)));
        Assert.Contains("data:font/ttf;base64,", html);
        Assert.Contains("data:image/png;base64,", html);
        var restored = await fixture.Build();
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(0, restored.Failed);
        Assert.Single(Directory.GetFiles(fixture.Runtime, "*.css", SearchOption.AllDirectories));
        var originalPage = Path.Combine(shared.PackagePath, "..", "..", "artifacts", shared.Units[0].ArtifactId!, "page.en.html");
        foreach (System.Text.RegularExpressions.Match font in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(originalPage), @"href=""(\.\./_assets/[a-f0-9]{64}\.css)"""))
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(originalPage)!, font.Groups[1].Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_shared_font_refuses_apply_before_writes_and_build_repairs_without_generation(bool missing)
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var font = Directory.GetFiles(fixture.Runtime, "*.css", SearchOption.AllDirectories)[0];
        var original = File.ReadAllBytes(font);
        if (missing) File.Delete(font); else File.WriteAllText(font, "corrupt");
        var error = Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        Assert.Contains("artifact_corrupt", error.Errors);
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
        Assert.Equal(0, (await fixture.Build()).Failed);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(original, File.ReadAllBytes(font));
        Assert.Equal(10, fixture.Store.Apply(fixture.Worktree, "tree").Copied);
    }

    [Fact]
    public async Task Apply_preserves_a_hand_edited_shared_font_and_reports_its_path()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        fixture.Store.Apply(fixture.Worktree, "tree");
        var font = Directory.GetFiles(fixture.Worktree, "*.css", SearchOption.AllDirectories)[0];
        File.WriteAllText(font, "manual edit");
        var report = fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Equal(font, Assert.Single(report.Skipped));
        Assert.Equal("manual edit", File.ReadAllText(font));
        Assert.Equal(0, report.Copied);
        Assert.Equal(9, report.Unchanged);
    }

    [Theory]
    [InlineData("../outside.css")]
    [InlineData("/outside.css")]
    [InlineData("wrong.css")]
    public async Task Resource_manifest_cannot_supply_paths_outside_the_content_addressed_directory(string name)
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var path = Assert.Single(Directory.GetFiles(fixture.Runtime, "artifact.json", SearchOption.AllDirectories));
        var artifact = JsonNode.Parse(File.ReadAllText(path))!;
        var resources = artifact["Resources"]!.AsObject();
        var first = resources.First();
        resources.Remove(first.Key);
        resources[name] = first.Value;
        File.WriteAllText(path, artifact.ToJsonString());
        Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Shared_artifact_with_missing_resource_metadata_is_not_applied()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var path = Assert.Single(Directory.GetFiles(fixture.Runtime, "artifact.json", SearchOption.AllDirectories));
        var artifact = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        artifact.Remove("Resources");
        File.WriteAllText(path, artifact.ToJsonString());
        Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Package_pages_share_font_files_and_apply_preserves_their_relative_links()
    {
        using var fixture = new Fixture();
        var other = Fixture.Unit with { Content = Fixture.Unit.Content with { SymbolId = "type:B", Name = "Example.B" } };
        var report = await fixture.Build([Fixture.Unit, other]);
        Assert.Equal(0, report.Failed);
        var pages = Directory.GetFiles(fixture.Runtime, "page.*.html", SearchOption.AllDirectories);
        Assert.Equal(4, pages.Length);
        foreach (var page in pages)
        {
            var html = File.ReadAllText(page);
            Assert.DoesNotContain("data:font/ttf;base64,", html);
            var fonts = System.Text.RegularExpressions.Regex.Matches(html, @"href=""(\.\./_assets/[a-f0-9]{64}\.css)""");
            Assert.Single(fonts.Cast<System.Text.RegularExpressions.Match>());
            foreach (System.Text.RegularExpressions.Match font in fonts)
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(page)!, font.Groups[1].Value)));
        }
        var stylesheet = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Runtime, "*.css", SearchOption.AllDirectories)));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(stylesheet, "data:font/ttf;base64,").Count);
        Assert.DoesNotContain("url(../", stylesheet);
        fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Single(Directory.GetFiles(fixture.Worktree, "*.css", SearchOption.AllDirectories));
        foreach (var page in Directory.GetFiles(fixture.Worktree, "page.*.html", SearchOption.AllDirectories))
        foreach (System.Text.RegularExpressions.Match font in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(page), @"href=""(\.\./_assets/[a-f0-9]{64}\.css)"""))
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(page)!, font.Groups[1].Value)));
    }

    [Theory]
    [InlineData("prompt", "generator", 2)]
    [InlineData("validator", "generator", 2)]
    [InlineData("route", "generator", 2)]
    [InlineData("renderer", "renderer", 1)]
    public async Task Cache_versions_distinguish_text_regeneration_from_rendering(string changed, string reason, int calls)
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        var versions = DocumentationCacheVersions.ForRuntime(fixture.Runtime);
        versions = changed switch
        {
            "prompt" => versions with { Prompt = "prompt-next" },
            "validator" => versions with { Validator = "validator-next" },
            "route" => versions with { Route = "route-next" },
            _ => versions with { Renderer = "renderer-next" }
        };
        var store = new DocumentationPackStore(fixture.Runtime, new string('a', 64), "fixture-1", versions);

        var second = await fixture.Build(store: store);
        var third = await fixture.Build(store: store);

        Assert.Equal(calls, fixture.Calls);
        Assert.Contains(reason, Assert.Single(second.Units).Reasons);
        Assert.NotEqual(first.Units[0].ArtifactId, second.Units[0].ArtifactId);
        Assert.Equal(second.Units[0].ArtifactId, third.Units[0].ArtifactId);
        Assert.Equal(1, third.Reused);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_or_invalid_cached_text_is_regenerated_before_publication(bool validChecksum)
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var path = Assert.Single(Directory.GetFiles(fixture.Texts, "*.json"));
        if (!validChecksum) File.AppendAllText(path, "broken");
        else
        {
            var checkpoint = JsonNode.Parse(File.ReadAllText(path))!;
            var invalid = DocumentationValidationTests.Draft with { Claims = [] };
            checkpoint["Draft"] = JsonSerializer.SerializeToNode(invalid);
            checkpoint["DraftHash"] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(invalid)));
            File.WriteAllText(path, checkpoint.ToJsonString());
        }

        var changed = Fixture.Unit with { Content = Fixture.Unit.Content with { Snapshot = "new-tree" } };
        var report = await fixture.Build([changed]);

        Assert.Equal(2, fixture.Calls);
        Assert.Equal(0, report.Failed);
        Assert.False(report.Units[0].PreviousKept);
    }

    [Fact]
    public async Task Missing_text_does_not_discard_a_valid_unchanged_artifact_but_is_regenerated_for_rerendering()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        File.Delete(Assert.Single(Directory.GetFiles(fixture.Texts, "*.json")));

        var unchanged = await fixture.Build();
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(first.Units[0].ArtifactId, unchanged.Units[0].ArtifactId);
        var changed = Fixture.Unit with { Content = Fixture.Unit.Content with { Snapshot = "new-tree" } };
        var report = await fixture.Build([changed]);

        Assert.Equal(2, fixture.Calls);
        Assert.Equal(0, report.Failed);
        Assert.NotEqual(first.Units[0].ArtifactId, report.Units[0].ArtifactId);
    }

    [Fact]
    public async Task Legacy_render_only_metadata_rebuilds_once_without_making_old_documents_unreadable()
    {
        using var fixture = new Fixture();
        await fixture.Build(store: new DocumentationPackStore(fixture.Runtime, new string('a', 64), "fixture-1", htmlMode: DocumentationHtmlMode.Standalone));
        var path = Assert.Single(Directory.GetFiles(fixture.Runtime, "artifact.json", SearchOption.AllDirectories));
        var artifact = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        artifact["Version"] = "documentation-1";
        foreach (var name in new[] { "TextKey", "PresentationHash", "RendererVersion" }) artifact.Remove(name);
        File.WriteAllText(path, artifact.ToJsonString());
        File.Delete(Assert.Single(Directory.GetFiles(fixture.Texts, "*.json")));
        Assert.Equal(9, fixture.Store.Apply(fixture.Worktree, "tree").Copied);

        var rebuilt = await fixture.Build();
        Assert.Equal(2, fixture.Calls);
        Assert.Contains("generator", rebuilt.Units[0].Reasons);
        Assert.Equal(1, (await fixture.Build()).Reused);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task An_indexed_dependency_signature_change_invalidates_cached_text()
    {
        using var fixture = new Fixture { Draft = DocumentationValidationTests.Draft with { DiagramSvg = DocumentationValidationTests.Svg.Replace("Example.", "", StringComparison.Ordinal) } };
        const string source = "public class A : B {} public class B {}";
        var index = DocumentationInputsTests.Index(source, relationship: true);
        var sources = new Dictionary<string, string> { ["src/Types.cs"] = source };
        var first = Assert.Single(DocumentationInputs.Create(index, sources, _ => [], 1));
        var changed = index with { Symbols = index.Symbols.Select(s => s.Id == "B" ? s with { Signature = "public class B<T>" } : s).ToList() };
        var second = Assert.Single(DocumentationInputs.Create(changed, sources, _ => [], 1));
        Assert.Equal(first.SourceHash, second.SourceHash);
        Assert.NotEqual(first.ReferencesHash, second.ReferencesHash);

        Assert.Equal(0, (await fixture.Build([first])).Failed);
        var report = await fixture.Build([second]);

        Assert.Equal(2, fixture.Calls);
        Assert.Equal(0, report.Failed);
        Assert.Contains("references", report.Units[0].Reasons);
    }

    [Fact]
    public async Task Route_content_changes_invalidate_text_even_when_catalog_version_is_unchanged()
    {
        using var fixture = new Fixture();
        var catalog = ModelRoutingCatalogStore.LoadEmbedded();
        fixture.WriteCatalog(catalog);
        await fixture.Build();
        var changed = catalog with { Routes = catalog.Routes.Select(route => route.Profile == LocalTaskProfile.PlainTranslation ?
            route with { Fallbacks = route.Fallbacks.Concat(route.Fallbacks.Take(1)).ToArray() } : route).ToArray() };
        Assert.Equal(catalog.CatalogVersion, changed.CatalogVersion);
        fixture.WriteCatalog(changed);

        var report = await fixture.Build();

        Assert.Equal(2, fixture.Calls);
        Assert.Equal(0, report.Failed);
        Assert.Contains("generator", report.Units[0].Reasons);
    }

    [Fact]
    public async Task A_route_change_during_generation_cannot_publish_text_under_the_old_route_key()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        fixture.OnGenerate = () => fixture.WriteCatalog(ModelRoutingCatalogStore.LoadEmbedded() with { CatalogVersion = "new-version" });

        var report = await fixture.Build([Fixture.Unit with { SourceHash = "changed" }]);

        Assert.Equal("documentation_route_changed", report.Units[0].Error);
        Assert.True(report.Units[0].PreviousKept);
        Assert.Single(Directory.GetFiles(fixture.Texts, "*.json"));
    }

    [Fact]
    public async Task Validated_text_survives_cancellation_before_rendering_and_resumes_without_generation()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.BuildAsync(new(fixture.Worktree, "tree", [Fixture.Unit]),
            (_, _) => Task.FromResult(DocumentationValidationTests.Draft), progress =>
            {
                if (progress.Phase == "render") cancellation.Cancel();
            }, cancellation.Token));
        Assert.Single(Directory.GetFiles(fixture.Texts, "*.json"));
        Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));

        var report = await fixture.Build();

        Assert.Equal(0, fixture.Calls);
        Assert.Equal(0, report.Failed);
        Assert.Equal(10, fixture.Store.Apply(fixture.Worktree, "tree").Copied);
    }

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
    public async Task Modified_rendered_bytes_are_rebuilt_from_verified_text_without_a_model_call()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        var page = Assert.Single(Directory.GetFiles(fixture.Runtime, "page.en.md", SearchOption.AllDirectories));
        File.AppendAllText(page, "modified");
        var next = await fixture.Build();
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(0, next.Reused);
        Assert.NotEqual(first.Units[0].ArtifactId, next.Units[0].ArtifactId);
    }

    [Fact]
    public async Task New_coordinates_and_snapshot_rerender_verified_text_and_preserve_manual_edits()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build();
        fixture.Store.Apply(fixture.Worktree, "tree");
        var manual = Assert.Single(Directory.GetFiles(fixture.Worktree, "page.en.md", SearchOption.AllDirectories));
        File.WriteAllText(manual, "manual edit");
        var moved = Fixture.Unit with { Content = Fixture.Unit.Content with { SourceLine = 47, Snapshot = "new-tree" } };

        var second = await fixture.Build([moved], "new-tree");

        Assert.NotEqual(first.Units[0].ArtifactId, second.Units[0].ArtifactId);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(0, second.Reused);
        Assert.Contains("presentation", second.Units[0].Reasons);
        var page = Path.Combine(fixture.Runtime, "repositories", new string('a', 64), "documentation", "artifacts", second.Units[0].ArtifactId!, "page.en.md");
        Assert.Contains("src/A.cs:47", File.ReadAllText(page));
        Assert.Contains("new-tree", File.ReadAllText(page));
        var applied = fixture.Store.Apply(fixture.Worktree, "new-tree");
        Assert.Contains(manual, applied.Skipped);
        Assert.Equal("manual edit", File.ReadAllText(manual));
    }

    [Fact]
    public async Task Apply_copies_once_and_preserves_hand_edits_on_the_next_apply()
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var applied = fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Equal(10, applied.Copied);
        Assert.Equal(1, fixture.Calls);
        var page = Assert.Single(Directory.GetFiles(fixture.Worktree, "page.en.md", SearchOption.AllDirectories));
        File.WriteAllText(page, "manual edit");
        var again = fixture.Store.Apply(fixture.Worktree, "tree");
        Assert.Single(again.Skipped);
        Assert.Equal(0, again.Copied);
        Assert.Equal(9, again.Unchanged);
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

    [Theory]
    [InlineData("docs")]
    [InlineData("docs/generated/_assets")]
    public async Task Apply_reports_the_exact_symbolic_link_and_does_not_write_through_it(string relative)
    {
        using var fixture = new Fixture();
        await fixture.Build();
        var external = Path.Combine(fixture.Runtime, "external"); Directory.CreateDirectory(external);
        var docs = Path.GetFullPath(Path.Combine(fixture.Worktree, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(docs)!);
        await CreateDirectoryLink(docs, external);
        var error = Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
        Assert.Equal(docs, error.Data["path"]);
        Assert.Empty(Directory.GetFiles(external));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_refuses_a_linked_baseline_directory_without_creating_or_overwriting_external_files(bool existingFile)
    {
        using var fixture = new Fixture();
        await fixture.Build(units: []);
        fixture.Store.Apply(fixture.Worktree, "tree");
        var applied = Path.Combine(fixture.Runtime, "repositories", new string('a', 64), "documentation", "applied");
        var baseline = Assert.Single(Directory.GetFiles(applied));
        // Obtain the real baseline name without duplicating its path hashing algorithm.
        File.Move(baseline, Path.Combine(fixture.Runtime, "original-baseline.json"));
        Directory.Delete(applied);
        var external = Path.Combine(fixture.Runtime, "external");
        Directory.CreateDirectory(external);
        var outsideBaseline = Path.Combine(external, Path.GetFileName(baseline));
        const string original = "unrelated fixture data";
        if (existingFile) File.WriteAllText(outsideBaseline, original);
        await CreateDirectoryLink(applied, external);
        try
        {
            var error = Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "tree"));
            Assert.Equal(applied, error.Data["path"]);
            if (existingFile) Assert.Equal(original, File.ReadAllText(outsideBaseline));
            else Assert.Empty(Directory.GetFiles(external));
            Assert.Empty(Directory.GetFiles(fixture.Worktree, "*", SearchOption.AllDirectories));
        }
        finally
        {
            // Remove only the junction/link, leaving its external target untouched.
            Directory.Delete(applied);
        }
    }

    private static Task CreateDirectoryLink(string path, string target) =>
        DirectoryLinkFixture.CreateAsync(path, target, TestContext.Current.CancellationToken);

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory temp = new("localai-documentation-pack");
        internal static DocumentationUnitInput Unit => new(DocumentationValidationTests.Input, "source", "references", "history");
        internal string Worktree { get; }
        internal string Runtime { get; }
        internal string Texts => Path.Combine(Runtime, "repositories", new string('a', 64), "documentation", "texts");
        internal DocumentationPackStore Store { get; }
        internal int Calls;
        internal bool Invalid;
        internal DocumentationDraft Draft = DocumentationValidationTests.Draft;
        internal Action? OnGenerate;
        internal Fixture()
        {
            Worktree = temp.Combine("repository"); Runtime = temp.Combine("runtime");
            Directory.CreateDirectory(Worktree);
            Store = new(Runtime, new string('a', 64), "fixture-1");
        }
        internal Task<DocumentationBuildReport> Build(DocumentationUnitInput[]? units = null, string snapshot = "tree", DocumentationPackStore? store = null) => (store ?? Store).BuildAsync(
            new(Worktree, snapshot, units ?? [Unit]), (_, _) => { Calls++; OnGenerate?.Invoke(); return Task.FromResult(Invalid ? Draft with { Claims = [] } : Draft); },
            cancellationToken: TestContext.Current.CancellationToken);
        internal void WriteCatalog(ModelRoutingCatalogDocument catalog)
        {
            var path = new ModelRoutingCatalogStore(Runtime).FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(catalog, LocalAiJson.Strict));
        }
        public void Dispose() => temp.Dispose();
    }
}
