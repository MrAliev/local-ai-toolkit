using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelResidencyPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.ResidencyPolicyTests",
        Guid.NewGuid().ToString("N"));

    public ModelResidencyPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_policy_file_is_strict()
    {
        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
        Assert.Equal(
            BrokerPolicyDefaults.IdleModelKeepAliveSeconds,
            policy.IdleModelKeepAliveSeconds);
    }

    [Fact]
    public void Malformed_policy_file_does_not_silently_relax_the_check()
    {
        File.WriteAllText(Path.Combine(_root, BrokerPolicy.FileName), "{ not json");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Fact]
    public void Unknown_policy_value_falls_back_to_strict()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"AllowAnythingPlease"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Fact]
    public void Future_schema_version_falls_back_to_strict()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":99,"ModelResidency":"AllowCpu"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Theory]
    [InlineData(ModelResidencyPolicy.AllowPartialOffload)]
    [InlineData(ModelResidencyPolicy.AllowCpu)]
    public void Written_policy_round_trips(ModelResidencyPolicy residency)
    {
        var store = new ModelResidencyPolicyStore(_root);
        store.Write(new BrokerPolicy(1, residency, IdleModelKeepAliveSeconds: 45));

        Assert.Equal(residency, store.Read().ModelResidency);
        Assert.Equal(45, store.Read().IdleModelKeepAliveSeconds);
    }

    /// <summary>
    /// Ten seconds, and the number is the point rather than a preference.
    ///
    /// Zero turned every gap between two pieces of work into a full model load. An index build
    /// posts one batch while the previous one is still being worked on, so the queue is empty
    /// only for the instant between them — and at zero that instant is enough for the idle sweep
    /// to unload eight gigabytes the next batch immediately loads again. Measured on the machine
    /// this was found on: fifty-seven embedding batches, fifty-seven unloads, and a median
    /// request of ten seconds with two to four of them spent loading the model inside it.
    ///
    /// It does not delay a model switch, which is the thing zero was protecting. Work for another
    /// model unloads the resident one on the execution path, in
    /// <c>ModelRuntime.EnsureReadyAsync</c>, before its own preflight; the idle sweep only runs
    /// when nothing is queued at all.
    /// </summary>
    [Fact]
    public void An_installation_that_configured_nothing_holds_the_model_for_ten_seconds()
    {
        Assert.Equal(10, BrokerPolicy.Default.IdleModelKeepAliveSeconds);
    }

    /// <summary>
    /// A policy file written before the field existed is an installation that never chose, so it
    /// gets the same hold a fresh one gets. The absence of the field used to mean zero, which is
    /// the one value nobody would pick on purpose.
    /// </summary>
    [Fact]
    public void Existing_policy_without_idle_keep_alive_holds_the_model_for_ten_seconds()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"RequireFullVram"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(10, policy.IdleModelKeepAliveSeconds);
    }

    /// <summary>
    /// A file that stopped parsing is not the same as no file, and the store is the only thing
    /// that knows which it read. It answers a malformed document with its defaults — right at
    /// runtime, and invisible to whoever wrote the file: it is still on disk, still looks
    /// configured, and no longer does anything.
    /// </summary>
    [Fact]
    public void A_file_that_did_not_parse_is_reported_as_found_and_unused()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            "{ this is not json");

        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.False(read.FileUsed);
        Assert.Equal(BrokerPolicy.Default, read.Policy);
    }

    [Fact]
    public void A_file_the_store_could_use_is_reported_as_used()
    {
        new ModelResidencyPolicyStore(_root).Write(
            BrokerPolicy.Default with { IdleModelKeepAliveSeconds = 45 });

        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.True(read.FileUsed);
        Assert.Equal(45, read.Policy.IdleModelKeepAliveSeconds);
    }

    [Fact]
    public void No_file_at_all_is_neither_found_nor_used()
    {
        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.False(read.FileFound);
        Assert.False(read.FileUsed);
    }

    /// <summary>
    /// A schema this build does not know is refused the same way malformed JSON is: the values
    /// are not applied, so the file is found and unused rather than found and honoured.
    /// </summary>
    [Fact]
    public void A_retention_file_from_another_schema_is_found_and_unused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(
            Path.Combine(_root, "settings", RuntimeRetentionPolicy.FileName),
            """{"SchemaVersion":99,"GenerationsPerRepository":7}""");

        var read = new RuntimeRetentionPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.False(read.FileUsed);
        Assert.Equal(
            RuntimeRetentionPolicy.Default.GenerationsPerRepository,
            read.Policy.GenerationsPerRepository);
    }

    [Fact]
    public void Negative_idle_keep_alive_falls_back_to_default_policy()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"AllowCpu","IdleModelKeepAliveSeconds":-1}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(BrokerPolicy.Default, policy);
    }

    [Fact]
    public void Fully_resident_load_carries_no_warning()
    {
        Assert.Null(
            ModelResidencyPolicy.AllowCpu.DescribeDegradation(
                sizeBytes: 1000,
                sizeVramBytes: 1000));
    }

    [Fact]
    public void Cpu_load_is_reported_as_running_on_cpu()
    {
        var warning = ModelResidencyPolicy.AllowCpu.DescribeDegradation(
            sizeBytes: 1000,
            sizeVramBytes: 0);

        Assert.NotNull(warning);
        Assert.Contains("CPU", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sliver in video memory is the processor running the model, and has to read as that.
    ///
    /// The CPU branch triggered on "nothing at all in video memory", so 40 MB of a 20 GB model
    /// fell through to the partial one and printed "only 0% of the model is in video memory, so
    /// responses are slower" — which says "slower" about the case that is slowest.
    /// `ResidencyShortfall.Shortfall()` had the rule right; the sentence a reader sees did not.
    /// </summary>
    [Fact]
    public void A_sliver_in_video_memory_reads_as_the_processor_running_it()
    {
        var warning = ModelResidencyPolicy.AllowCpu.DescribeDegradation(
            sizeBytes: 20_000_000_000,
            sizeVramBytes: 40_000_000);

        Assert.NotNull(warning);
        Assert.Contains("CPU", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("0%", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_load_reports_the_resident_share()
    {
        var warning = ModelResidencyPolicy.AllowPartialOffload.DescribeDegradation(
            sizeBytes: 1000,
            sizeVramBytes: 400);

        Assert.NotNull(warning);
        Assert.Contains("40%", warning, StringComparison.Ordinal);
    }
}
