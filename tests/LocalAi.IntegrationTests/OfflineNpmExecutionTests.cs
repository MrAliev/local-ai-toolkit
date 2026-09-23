using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.IntegrationTests;

public sealed class OfflineNpmExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mixed_selection_runs_in_catalog_order_and_records_intent_before_npm()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build(includeExecutables: true);
        var runner = new Runner(fixture, new(0, "", "", false, false));
        var application = new OfflineBundleApplication(fixture.Materials.Materials.Verifier, runner);
        var plan = await application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Run,
            fixture.Catalog, fixture.Layout, Token);
        Assert.Equal(["dependency.ollama", fixture.Materials.Component, "dependency.python-3-12"], plan.DependencyOrder);
        Assert.Single(plan.Npm!); Assert.Empty(runner.Calls); Assert.False(Directory.Exists(fixture.Run));
        var result = await application.ExecuteAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Catalog, fixture.Layout, Token);
        Assert.Equal("applied", result.State); Assert.Equal(["ollama.exe", "npm", "python.exe"], runner.Calls);
        Assert.Equal(plan.DependencyOrder, result.Steps.Select(step => step.Id));
        Assert.True(result.RecordingComplete); Assert.Empty(result.PendingSteps); Assert.False(result.ReadyForUse);
        Assert.True(Directory.Exists(fixture.Materials.Output));
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunOutcome.Completed, journal.Snapshot.Outcome);
        Assert.All(journal.Snapshot.Steps, step => Assert.Equal(InstallerRunStepStatus.Completed, step.Status));
    }

    [Theory]
    [InlineData(1, false, false, false, "failed")]
    [InlineData(null, true, false, false, "timed-out")]
    [InlineData(null, false, true, false, "cancelled")]
    [InlineData(null, false, false, true, "termination-unconfirmed")]
    public async Task Npm_failure_or_uncertainty_stops_later_dependencies_and_keeps_pending_steps(int? exit, bool timeout, bool cancelled, bool uncertain, string state)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build(includeExecutables: true);
        var runner = new Runner(fixture, new(exit, "", "", timeout, cancelled)) { Uncertain = uncertain };
        var result = await new OfflineBundleApplication(fixture.Materials.Materials.Verifier, runner)
            .ExecuteAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Catalog, fixture.Layout, Token);
        Assert.Equal(state, result.State); Assert.Equal(["ollama.exe", "npm"], runner.Calls);
        Assert.Equal(["dependency:" + fixture.Materials.Component, "dependency:dependency.python-3-12"], result.PendingSteps);
        Assert.NotNull(result.Steps[^1].RetainedDirectory); Assert.False(Directory.Exists(fixture.Materials.Output));
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        if (uncertain) { Assert.Null(journal.Snapshot.Outcome); Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status); }
        else Assert.Equal(InstallerRunStepStatus.Failed, journal.Snapshot.Steps[^1].Status);
    }

    [Fact]
    public async Task Npm_only_requests_are_supported_in_schema_two()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var result = await new OfflineBundleApplication(fixture.Materials.Materials.Verifier, new Runner(fixture, new(0, "", "", false, false)))
            .ExecuteAsync(fixture.Bundle, built.ManifestSha256, fixture.Request with { Dependencies = [] }, fixture.Run, fixture.Catalog, fixture.Layout, Token);
        Assert.Equal("applied", result.State); Assert.Equal(fixture.Materials.Component, Assert.Single(result.Steps).Id);
    }

    [Theory]
    [InlineData("legacy-schema")]
    [InlineData("duplicate")]
    [InlineData("output-overlap")]
    public async Task Invalid_npm_selections_are_refused_before_any_effect(string damage)
    {
        using var fixture = new Fixture(); var built = await fixture.Materials.Build(includeExecutables: true);
        var request = damage switch
        {
            "legacy-schema" => fixture.Request with { SchemaVersion = 1 },
            "duplicate" => fixture.Request with { Npm = [fixture.Request.Npm![0], fixture.Request.Npm![0]] },
            _ => fixture.Request with { Npm = [fixture.Request.Npm![0] with { Output = fixture.Run }] },
        };
        var runner = new Runner(fixture, new(0, "", "", false, false));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Materials.Materials.Verifier, runner)
            .PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, request, fixture.Run, fixture.Catalog, fixture.Layout, Token));
        Assert.Empty(runner.Calls); Assert.False(Directory.Exists(fixture.Run)); Assert.False(Directory.Exists(fixture.Materials.Output));
    }

    [Fact]
    public async Task Cli_preserves_an_explicit_npm_selection_in_its_default_plan()
    {
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var request = Path.Combine(Path.GetDirectoryName(fixture.Run)!, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(fixture.Request with { Dependencies = [] }, OfflineEnvironmentBundle.Json));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await OfflineExecuteCommand.ExecuteAsync(["--bundle", fixture.Bundle, "--manifest-sha256", built.ManifestSha256, "--request", request,
            "--out", fixture.Run], new(output, error), true, Token, fixture.Materials.Materials.Verifier,
            new Runner(fixture, new(0, "", "", false, false)), fixture.Catalog, fixture.Layout);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(fixture.Materials.Component, data.GetProperty("result").GetProperty("npm")[0].GetProperty("component").GetString());
        Assert.False(Directory.Exists(fixture.Run));
    }

    private sealed class Runner(Fixture fixture, ProcessResult result) : IProcessRunner, IProcessWorkingDirectoryRunner
    {
        public bool Uncertain { get; init; }
        public List<string> Calls { get; } = [];
        private void AssertIntent()
        {
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(fixture.Run, "journal-*.json")));
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status);
        }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        { AssertIntent(); Calls.Add(Path.GetFileName(executable)); return Task.FromResult(new ProcessResult(0, "", "", false, false)); }
        public Task<ProcessResult> RunInDirectoryAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            AssertIntent(); Calls.Add("npm");
            if (Uncertain) throw new ProcessTerminationException(1, ProcessTerminationCause.Timeout, "fixture");
            if (result.ExitCode == 0)
            {
                var package = Path.Combine(workingDirectory, "node_modules", "@sourcegraph", "scip-typescript"); Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@sourcegraph/scip-typescript\",\"version\":\"0.4.0\"}");
            }
            return Task.FromResult(result);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public OfflineNpmTests.Fixture Materials { get; } = new();
        public string Run => Materials.Output + "-run";
        public string Bundle => Materials.Materials.Output;
        public ModelRoutingCatalogDocument Catalog => Materials.Materials.Catalog;
        public InstallationLayout Layout { get; }
        public OfflineExecutionRequest Request => new(2, ["dependency.python-3-12", "dependency.ollama"], false, null,
            [new(Materials.Component, Materials.Node, Materials.NpmCli, Materials.Output)]);
        public Fixture()
        {
            var profile = Path.Combine(Path.GetDirectoryName(Run)!, "profile"); Directory.CreateDirectory(profile);
            Layout = InstallationLayout.FromLocalAppData(profile);
        }
        public void Dispose() => Materials.Dispose();
    }
}
