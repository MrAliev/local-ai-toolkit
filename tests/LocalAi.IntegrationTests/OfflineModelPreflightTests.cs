using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.IntegrationTests;

public sealed class OfflineModelPreflightTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_explains_preflight_scope_and_its_remaining_requirements()
    {
        using var fixture = new Fixture(); var bundle = await fixture.Source.Build();
        var requestPath = Path.Combine(fixture.Source.Materials.Source, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(fixture.Request, OfflineEnvironmentBundle.Json));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await OfflineExecuteCommand.ExecuteAsync(["--bundle", fixture.Bundle, "--manifest-sha256", bundle.ManifestSha256,
            "--request", requestPath, "--out", fixture.Source.Run], new(output, error), true, Token,
            fixture.Source.Materials.Verifier, fixture, fixture.Source.Materials.Catalog, fixture.Source.Layout);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains("readyForUse=false", json.RootElement.GetProperty("data").GetProperty("preflightNotice").GetString());
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task Preview_lists_exact_model_contexts_without_loading_or_installing()
    {
        using var fixture = new Fixture(); var bundle = await fixture.Source.Build();
        var plan = await fixture.Application.PlanExecutionAsync(fixture.Bundle, bundle.ManifestSha256, fixture.Request,
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token);
        Assert.Equal(fixture.Request.Preflight, plan.Preflight);
        Assert.Equal(fixture.CatalogVersion, plan.CatalogVersion);
        Assert.Contains("ollama-model-source-and-directory-unverified", plan.RemainingRequirements);
        Assert.Empty(fixture.Calls); Assert.False(Directory.Exists(fixture.Source.Layout.Root));
        Assert.False(Directory.Exists(fixture.Source.Run));
    }

    [Theory]
    [InlineData("valid", "applied", null)]
    [InlineData("missing", "failed", "model-not-installed")]
    [InlineData("pending-pull", "failed", "model-pull-pending")]
    [InlineData("catalog", "failed", "preflight-response-invalid")]
    [InlineData("model", "failed", "preflight-response-invalid")]
    [InlineData("context", "failed", "preflight-response-invalid")]
    [InlineData("future", "failed", "preflight-response-invalid")]
    [InlineData("malformed", "failed", "preflight-response-invalid")]
    [InlineData("truncated", "failed", "preflight-response-invalid")]
    [InlineData("rejected", "failed", "residency-rejected")]
    [InlineData("trial-load-failed", "failed", "trial-load-failed")]
    [InlineData("policy", "failed", "residency-rejected")]
    [InlineData("policy-changed", "failed", "residency-policy-changed")]
    [InlineData("launcher-changed", "failed", "preflight-trust-or-process-failed")]
    [InlineData("cancelled", "cancelled", "preflight-interrupted")]
    [InlineData("timeout", "timed-out", "preflight-interrupted")]
    [InlineData("termination", "termination-unconfirmed", "preflight-termination-unconfirmed")]
    public async Task Apply_records_proof_or_stops_without_downloads(string mode, string state, string? detail)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture { Mode = mode }; var bundle = await fixture.Source.Build();
        var result = await fixture.Apply(bundle);
        Assert.Equal(state, result.State); Assert.True(result.RecordingComplete); Assert.False(result.ReadyForUse);
        Assert.DoesNotContain(fixture.Calls, call => call.Contains("pull"));
        var step = result.Steps.Last(); Assert.Equal("preflight", step.Kind);
        Assert.Equal(detail ?? "model-residency-verified;source-and-directory-unverified", step.Detail);
        if (state == "applied")
        {
            Assert.NotNull(step.Proof); Assert.Equal(fixture.Model.Model, step.Proof.Model);
            Assert.Equal(fixture.Model.ContextTokens, step.Proof.ContextTokens); Assert.Equal(123, step.Proof.SizeBytes);
            Assert.Equal(ModelResidencyPolicy.RequireFullVram, step.ResidencyPolicy); Assert.Empty(result.PendingSteps);
        }
        else { Assert.Null(step.Proof); Assert.Contains("preflight:" + fixture.Model.Model + ":" + fixture.Model.ContextTokens, result.PendingSteps); }
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        if (mode == "termination")
        { Assert.Null(journal.Snapshot.Outcome); Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps.Last().Status); }
        else Assert.Equal(state == "applied" ? InstallerRunOutcome.Completed : state == "cancelled" ? InstallerRunOutcome.Cancelled : InstallerRunOutcome.Failed, journal.Snapshot.Outcome);
        Assert.True(Directory.Exists(fixture.Source.Models));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("runtime")]
    [InlineData("setup")]
    [InlineData("unknown")]
    [InlineData("context")]
    [InlineData("duplicate")]
    [InlineData("unsigned")]
    public async Task Invalid_selections_are_refused_before_any_effect(string invalid)
    {
        using var fixture = new Fixture(invalid != "unsigned"); var bundle = await fixture.Source.Build();
        var request = invalid switch
        {
            "schema" => fixture.Request with { SchemaVersion = 4 },
            "runtime" => fixture.Request with { InstallRuntime = false },
            "setup" => fixture.Request with { ConfigureModels = false },
            "unknown" => fixture.Request with { Preflight = [new("absent:latest", 2048)] },
            "context" => fixture.Request with { Preflight = [fixture.Model with { ContextTokens = 17 }] },
            "duplicate" => fixture.Request with { Preflight = [fixture.Model, fixture.Model] },
            _ => fixture.Request,
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Application.ExecuteAsync(fixture.Bundle, bundle.ManifestSha256,
            request, fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token));
        Assert.Empty(fixture.Calls); Assert.False(Directory.Exists(fixture.Source.Run)); Assert.False(Directory.Exists(fixture.Source.Layout.Root));
    }

    [Fact]
    public async Task Selected_target_policy_is_respected_without_rewriting_it()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture { Mode = "policy", Policy = ModelResidencyPolicy.AllowCpu }; var bundle = await fixture.Source.Build();
        var result = await fixture.Apply(bundle);
        Assert.Equal("applied", result.State);
        Assert.Equal(ModelResidencyPolicy.AllowCpu, result.Steps.Last().ResidencyPolicy);
        Assert.Equal(0, result.Steps.Last().Proof!.SizeVramBytes);
        Assert.Equal(ModelResidencyPolicy.AllowCpu, new ModelResidencyPolicyStore(fixture.Source.Layout.Root).Read().ModelResidency);
    }

    [Fact]
    public async Task Refusal_keeps_later_contexts_pending_and_never_loads_them()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture { Mode = "missing" }; var bundle = await fixture.Source.Build();
        var other = fixture.Model with { ContextTokens = fixture.Source.Materials.Catalog.Models.Single(model => model.Tag == fixture.Model.Model)
            .ContextTokens.First(context => context != fixture.Model.ContextTokens) };
        var result = await fixture.Application.ExecuteAsync(fixture.Bundle, bundle.ManifestSha256, fixture.Request with { Preflight = [fixture.Model, other] },
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token);
        Assert.Equal("failed", result.State); Assert.Equal(2, result.PendingSteps.Length);
        Assert.Single(fixture.Calls, call => call[0] == "run");
    }

    private sealed class Fixture : IProcessRunner, IInstalledApplicationProbe, IDisposable
    {
        public OfflineExecutionTests.Fixture Source { get; }
        public string Bundle => Source.Materials.Output;
        public string CatalogVersion => Source.Materials.Catalog.CatalogVersion;
        public ModelContextRef Model { get; }
        public List<string[]> Calls { get; } = [];
        public string Mode { get; set; } = "valid";
        public ModelResidencyPolicy Policy { get; set; } = ModelResidencyPolicy.RequireFullVram;
        public OfflineExecutionRequest Request => Source.Request with
            { SchemaVersion = 5, Dependencies = [], ConfigureModels = true, Preflight = [Model] };
        public OfflineBundleApplication Application => new(Source.Materials.Verifier, this, installedApplications: this);
        public Fixture(bool signed = true)
        {
            Source = new(signModels: signed);
            var entry = Source.Materials.Catalog.Models.First(model => model.Lifecycle != LocalModelLifecycle.Disabled && model.InstallPolicy != LocalModelInstallPolicy.Never);
            Model = new(entry.Tag, entry.ContextTokens[0]);
        }
        public Task<OfflineExecutionResult> Apply(OfflineBundleResult bundle) => Application.ExecuteAsync(Bundle, bundle.ManifestSha256,
            Request, Source.Run, Source.Materials.Catalog, Source.Layout, Token);
        public Task<InstalledApplicationMetadata?> FindOllamaAsync(CancellationToken token)
        {
            if (Mode == "launcher-changed") File.WriteAllText(Source.Layout.LauncherPath, "changed");
            return Task.FromResult<InstalledApplicationMetadata?>(
                new("Ollama", "fixture", Source.Materials.Source, Path.Combine(Source.Materials.Source, "ollama.exe"), "fixture", "fixture"));
        }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray()); Assert.Equal(Source.Layout.LauncherPath, executable);
            if (arguments[0] == "activate")
            {
                File.WriteAllText(Source.Layout.CurrentPointerPath, "{\"schemaVersion\":1,\"version\":\"abcdef123456\"}");
                if (Policy != ModelResidencyPolicy.RequireFullVram) new ModelResidencyPolicyStore(Source.Layout.Root).Write(BrokerPolicy.Default with { ModelResidency = Policy });
                return Task.FromResult(new ProcessResult(0, "", "", false, false));
            }
            Assert.Equal("--runtime-root", arguments[^2]); Assert.Equal(Source.Layout.Root, arguments[^1]);
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(Source.Run, "journal-*.json")));
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps.Last().Status);
            Assert.StartsWith("preflight:", journal.Snapshot.Steps.Last().Description);
            Assert.True(Directory.Exists(Source.Models));
            if (arguments[3] == "status") return Task.FromResult(Answer(new ModelStatusCommandSuccess(1, "status", true,
                Mode == "catalog" ? "other" : CatalogVersion, Mode == "missing" ? [] : [Model.Model], Mode == "pending-pull" ? [Model.Model] : [])));
            Assert.Equal("preflight", arguments[3]);
            if (Mode == "policy-changed") new ModelResidencyPolicyStore(Source.Layout.Root).Write(BrokerPolicy.Default with { ModelResidency = ModelResidencyPolicy.AllowCpu });
            if (Mode == "termination") throw new ProcessTerminationException(1, ProcessTerminationCause.Timeout, "fixture");
            if (Mode is "cancelled" or "timeout") return Task.FromResult(new ProcessResult(null, "", "", Mode == "timeout", Mode == "cancelled"));
            if (Mode is "rejected" or "trial-load-failed") return Task.FromResult(Answer(new ModelPreflightCommandRejected(1, "preflight", false, Model.Model,
                Model.ContextTokens, CatalogVersion, Mode == "rejected" ? "residency_rejected" : "trial_load_failed"), 3));
            var proof = new ModelPreflightCommandSuccess(1, "preflight", true, Mode == "model" ? "other:latest" : Model.Model,
                Mode == "context" ? 17 : Model.ContextTokens, CatalogVersion, 123, Mode == "policy" ? 0 : 123, Mode != "policy",
                Mode == "future" ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow);
            return Task.FromResult(Mode == "malformed" ? new(0, "{", "", false, false) : Answer(proof) with { StandardOutputTruncated = Mode == "truncated" });
        }
        private static ProcessResult Answer<T>(T value, int exit = 0) => new(exit, JsonSerializer.Serialize(value, LocalAiJson.Strict), "", false, false);
        public void Dispose() => Source.Dispose();
    }
}
