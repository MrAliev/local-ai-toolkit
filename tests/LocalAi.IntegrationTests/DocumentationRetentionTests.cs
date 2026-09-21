using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Documentation;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationRetentionTests
{
    [Fact]
    public async Task Preview_is_read_only_and_execution_keeps_current_previous_and_their_resources()
    {
        using var fixture = new Fixture();
        var reports = await fixture.History(6);
        var before = fixture.Snapshot();
        var plan = fixture.Store.Prune(fixture.Worktree, keepPacks: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(plan.DryRun);
        Assert.True(plan.ReclaimableBytes > 0);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(5, plan.Entries.Count(e => e.Kind == "pack" && e.Delete));
        Assert.Equal(4, plan.Entries.Count(e => e.Kind == "artifact" && e.Delete));
        Assert.Contains(plan.Entries, e => e.Path == reports[^1].PackagePath && e.Reasons.Contains("current"));
        Assert.Contains(plan.Entries, e => e.Kind == "resource" && !e.Delete);
        var applied = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(plan.ReclaimableBytes, applied.DeletedBytes);
        Assert.All(plan.Entries, e => Assert.Equal(!e.Delete, File.Exists(e.Path) || Directory.Exists(e.Path)));
        Assert.Equal(10, fixture.Store.Apply(fixture.Worktree, "snapshot-6").Copied);
        Assert.Equal(0, fixture.Store.Prune(fixture.Worktree, 1, cancellationToken: TestContext.Current.CancellationToken).ReclaimableBytes);
    }

    [Fact]
    public async Task Pinned_old_package_survives_until_explicitly_unpinned()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(5);
        var id = Path.GetFileName(history[0].PackagePath);
        fixture.Store.Pin(fixture.Worktree, id);
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(plan.Entries, e => e.Path == history[0].PackagePath && e.Reasons.Contains("pinned"));
        Assert.True(File.Exists(Path.Combine(history[0].PackagePath, "index.en.html")));
        Assert.True(Directory.Exists(fixture.Artifact(history[0])));
        fixture.Store.Pin(fixture.Worktree, id, pinned: false);
        fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(history[0].PackagePath));
        Assert.False(Directory.Exists(fixture.Artifact(history[0])));
    }

    [Fact]
    public async Task Applied_old_versions_and_hand_edits_survive_new_builds_and_cleanup()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build(1);
        fixture.Store.Apply(fixture.Worktree, "snapshot-1");
        var edited = Directory.GetFiles(fixture.Worktree, "page.en.md", SearchOption.AllDirectories).Single();
        File.WriteAllText(edited, "manual notes");
        for (var revision = 2; revision <= 5; revision++) await fixture.Build(revision);
        fixture.Store.Apply(fixture.Worktree, "snapshot-5");
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(plan.Entries, e => e.Path == first.PackagePath && e.Reasons.Contains("applied"));
        Assert.True(Directory.Exists(fixture.Artifact(first)));
        Assert.Equal("manual notes", File.ReadAllText(edited));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_or_interrupted_apply_without_complete_provenance_preserves_all_history(bool mismatchedHash)
    {
        using var fixture = new Fixture();
        await fixture.Build(1);
        fixture.Store.Apply(fixture.Worktree, "snapshot-1");
        var provenance = Directory.GetFiles(Path.Combine(fixture.Storage, "applied-origins"), "*.json").Single();
        if (!mismatchedHash) File.Delete(provenance);
        else
        {
            var origins = JsonNode.Parse(File.ReadAllText(provenance))!;
            origins["Files"]!.AsObject().First().Value!["Hash"] = new string('f', 64);
            File.WriteAllText(provenance, origins.ToJsonString());
        }
        for (var revision = 2; revision <= 4; revision++) await fixture.Build(revision);
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, plan.DeletedBytes);
        Assert.All(plan.Entries, e => Assert.Contains("untracked_apply", e.Reasons));
        fixture.Store.Apply(fixture.Worktree, "snapshot-4");
        Assert.True(fixture.Store.Prune(fixture.Worktree, 1, cancellationToken: TestContext.Current.CancellationToken).ReclaimableBytes > 0);
    }

    [Fact]
    public async Task Failed_generation_keeps_the_previous_valid_artifact_after_cleanup()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(4);
        var failed = await fixture.Build(5, invalid: true);
        Assert.Equal(1, failed.Failed);
        Assert.True(failed.Units[0].PreviousKept);
        fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(fixture.Artifact(history[^1])));
        Assert.True(Directory.Exists(fixture.Artifact(history[^2])));
        Assert.Equal(10, fixture.Store.Apply(fixture.Worktree, "snapshot-5").Copied);
    }

    [Fact]
    public async Task Unfinished_build_protects_text_and_all_history_until_successful_resume()
    {
        using var fixture = new Fixture();
        await fixture.History(4);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.BuildAsync(fixture.Input(5),
            (_, _) => Task.FromResult(DocumentationValidationTests.Draft), p => { if (p.Phase == "render") cancellation.Cancel(); }, cancellation.Token));
        var before = fixture.Snapshot();
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(plan.Entries, e => Assert.Contains("unfinished_build", e.Reasons));
        Assert.Equal(before, fixture.Snapshot());
        var calls = 0;
        await fixture.Store.BuildAsync(fixture.Input(5), (_, _) => { calls++; return Task.FromResult(DocumentationValidationTests.Draft); }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.True(fixture.Store.Prune(fixture.Worktree, 1, cancellationToken: TestContext.Current.CancellationToken).ReclaimableBytes > 0);
    }

    [Fact]
    public async Task Active_build_excludes_prune_pin_and_apply()
    {
        using var fixture = new Fixture();
        var first = await fixture.Build(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var build = fixture.Store.BuildAsync(fixture.Input(2), async (_, token) =>
        { started.SetResult(); await resume.Task.WaitAsync(token); return DocumentationValidationTests.Draft; }, cancellationToken: TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Contains("build_already_running", Assert.Throws<DocumentationValidationException>(() => fixture.Store.Prune(fixture.Worktree, cancellationToken: TestContext.Current.CancellationToken)).Errors);
            Assert.Contains("build_already_running", Assert.Throws<DocumentationValidationException>(() => fixture.Store.Pin(fixture.Worktree, Path.GetFileName(first.PackagePath))).Errors);
            Assert.Contains("build_already_running", Assert.Throws<DocumentationValidationException>(() => fixture.Store.Apply(fixture.Worktree, "snapshot-1")).Errors);
        }
        finally { resume.TrySetResult(); await build; }
    }

    [Theory]
    [InlineData("current.json")]
    [InlineData("pins/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")]
    public async Task Malformed_protection_metadata_fails_before_any_deletion(string relative)
    {
        using var fixture = new Fixture();
        await fixture.History(4);
        var path = Path.Combine(fixture.Storage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "broken");
        var before = fixture.Snapshot();
        Assert.Contains("retention_metadata_invalid", Assert.Throws<DocumentationValidationException>(() => fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken)).Errors);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task A_new_pin_after_preview_is_respected_by_execution()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(4);
        Assert.Contains(fixture.Store.Prune(fixture.Worktree, 1, cancellationToken: TestContext.Current.CancellationToken).Entries, e => e.Path == history[0].PackagePath && e.Delete);
        fixture.Store.Pin(fixture.Worktree, Path.GetFileName(history[0].PackagePath));
        fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(history[0].PackagePath));
    }

    [Fact]
    public async Task A_partially_deleted_retired_directory_is_reclaimed_on_the_next_run()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(4);
        var retired = Path.Combine(fixture.Storage, "retired", "pack-" + Path.GetFileName(history[0].PackagePath));
        Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
        Directory.Move(history[0].PackagePath, retired);
        File.Delete(Path.Combine(retired, "manifest.json"));
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(plan.Entries, e => e.Path == retired && e.Delete);
        Assert.False(Directory.Exists(retired));
        Assert.True(Directory.Exists(history[^1].PackagePath));
    }

    [Fact]
    public async Task A_pending_first_apply_keeps_its_package_even_without_a_baseline_file()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(4);
        var path = Path.Combine(fixture.Storage, "applied-origins", new string('b', 64) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = 1, Files = new Dictionary<string, object>(), PendingPackId = Path.GetFileName(history[0].PackagePath) }));
        var plan = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(plan.Entries, e => e.Path == history[0].PackagePath && e.Reasons.Contains("apply_in_progress"));
    }

    [Fact]
    public async Task Cancellation_before_execution_and_unknown_entries_never_delete_user_data()
    {
        using var fixture = new Fixture();
        await fixture.History(4);
        var extra = Path.Combine(fixture.Storage, "texts", "notes.txt");
        File.WriteAllText(extra, "user notes");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var before = fixture.Snapshot();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: cancellation.Token));
        Assert.Equal(before, fixture.Snapshot());
        var report = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(report.Entries, e => e.Path == extra && e.Reasons.Contains("unknown_entry"));
        Assert.Equal("user notes", File.ReadAllText(extra));
    }

    [Fact]
    public async Task Orphaned_text_and_css_are_deleted_but_referenced_resources_keep_their_bytes()
    {
        using var fixture = new Fixture();
        await fixture.History(3);
        var text = Path.Combine(fixture.Storage, "texts", new string('b', 64) + ".json");
        var css = Path.Combine(fixture.Storage, "artifacts", "_assets", new string('c', 64) + ".css");
        File.WriteAllText(text, "orphan"); File.WriteAllText(css, "orphan");
        var retainedCss = Directory.GetFiles(Path.GetDirectoryName(css)!, "*.css").Single(p => p != css);
        var bytes = File.ReadAllBytes(retainedCss);
        var report = fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(File.Exists(text)); Assert.False(File.Exists(css));
        Assert.Contains(report.Entries, entry => entry.Path == css && entry.Delete);
        Assert.Equal(bytes, File.ReadAllBytes(retainedCss));
    }

    [Fact]
    public async Task A_linked_candidate_is_refused_before_deleting_anything()
    {
        using var fixture = new Fixture();
        var history = await fixture.History(4);
        var candidate = fixture.Artifact(history[0]);
        var external = Path.Combine(fixture.Runtime, "external");
        Directory.Move(candidate, external);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{candidate}\" \"{external}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        else Directory.CreateSymbolicLink(candidate, external);
        var before = Directory.GetFiles(external).Select(File.ReadAllBytes).Select(Convert.ToHexString).ToArray();
        Assert.Contains("destination_link", Assert.Throws<DocumentationValidationException>(() => fixture.Store.Prune(fixture.Worktree, 1, dryRun: false, cancellationToken: TestContext.Current.CancellationToken)).Errors);
        Assert.Equal(before, Directory.GetFiles(external).Select(File.ReadAllBytes).Select(Convert.ToHexString).ToArray());
        Assert.True(Directory.Exists(history[0].PackagePath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory temp = new("localai-docs-retention");
        internal string Worktree { get; }
        internal string Runtime { get; }
        internal string Storage => Path.Combine(Runtime, "repositories", new string('a', 64), "documentation");
        internal DocumentationPackStore Store { get; }
        internal Fixture()
        {
            Worktree = temp.Combine("worktree"); Runtime = temp.Combine("runtime");
            Directory.CreateDirectory(Worktree);
            Store = new(Runtime, new string('a', 64), "retention-fixture");
        }
        internal DocumentationBuildInput Input(int revision) => new(Worktree, "snapshot-" + revision,
            [new(DocumentationValidationTests.Input with { Snapshot = "snapshot-" + revision }, "source-" + revision, "references", "history")]);
        internal async Task<DocumentationBuildReport> Build(int revision, bool invalid = false)
        {
            var result = await Store.BuildAsync(Input(revision), (_, _) => Task.FromResult(invalid ? DocumentationValidationTests.Draft with { Claims = [] } : DocumentationValidationTests.Draft), cancellationToken: TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(Path.Combine(result.PackagePath, "manifest.json"), new DateTime(2026, 1, 1, 0, 0, revision, DateTimeKind.Utc));
            if (result.Failed == 0) File.SetLastWriteTimeUtc(Path.Combine(Artifact(result), "artifact.json"), new DateTime(2026, 1, 1, 0, 0, revision, DateTimeKind.Utc));
            return result;
        }
        internal async Task<DocumentationBuildReport[]> History(int count)
        {
            var result = new List<DocumentationBuildReport>();
            for (var revision = 1; revision <= count; revision++) result.Add(await Build(revision));
            return result.ToArray();
        }
        internal string Artifact(DocumentationBuildReport report) => Path.Combine(Storage, "artifacts", report.Units[0].ArtifactId!);
        internal string[] Snapshot() => Directory.GetFiles(Storage, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => path + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void Dispose() => temp.Dispose();
    }
}
