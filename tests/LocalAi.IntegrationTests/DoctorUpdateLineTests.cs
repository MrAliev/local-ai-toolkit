using System.Text;
using System.Text.Json.Nodes;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The `update` line in `localai doctor`.
///
/// Two rules it exists to keep. It never reaches the network — `doctor` answers about this
/// machine, and a diagnostic that quietly called GitHub would be a second, unthrottled caller
/// of the thing the policy rations. And a newer release is a warning, never a failure: an
/// installation one version behind works perfectly well, and a diagnostic that exits non-zero
/// over it teaches whoever wired it into a script to ignore the exit code.
/// </summary>
public sealed class DoctorUpdateLineTests : IDisposable
{
    private const string Directory50 = "be08af033a2a";
    private const string Directory51 = "467ed5f0f9bf";

    private static readonly DateTimeOffset Checked =
        new(2026, 8, 31, 9, 30, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-update-" + Guid.NewGuid().ToString("N"));

    public DoctorUpdateLineTests() => Install(Directory50, "0.1.50");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_machine_that_never_opted_in_is_told_how_to()
    {
        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Equal("disabled", Result());
        Assert.Contains("check disabled", check.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "localai policy set --update-check on",
            check.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_enabled_check_that_has_not_run_is_unknown_rather_than_ok()
    {
        Enable();

        var check = Check();

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("never_checked", Result());
        Assert.Contains("nothing has been checked yet", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Being_up_to_date_is_reported_as_ok()
    {
        Enable();
        Learned("0.1.50", Directory50);

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Equal("up_to_date", Result());
        Assert.Contains("up to date at 0.1.50", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Up_to_date_information_older_than_two_intervals_is_stale_rather_than_ok()
    {
        Enable();
        Learned("0.1.50", Directory50);
        var now = Checked.AddHours(49);

        var check = Check(now);
        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("stale", Result(now));
        Assert.Equal("Unknown", Update(now)["availability"]!.GetValue<string>());
        Assert.Equal(
            "unknown; up to date at 0.1.50 as of 2026-08-31 09:30 UTC, but more than two check intervals have passed since",
            check.Detail);
    }

    [Fact]
    public void A_newer_release_is_a_warning_and_names_where_to_read_about_it()
    {
        Enable();
        Learned("0.1.51", Directory51);

        var report = DoctorCommand.Inspect(root, now: Checked.AddHours(1));
        var check = report.Checks.Single(c => c.Name == "update");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal("update_available", Result());
        Assert.Contains("0.1.51 is available", check.Detail, StringComparison.Ordinal);
        Assert.Contains("this installation is 0.1.50", check.Detail, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid", check.Detail, StringComparison.Ordinal);
        // A version behind is not a fault: the exit code is what scripts read.
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_release_that_cannot_be_compared_is_unknown_rather_than_ok()
    {
        Enable();
        Learned("latest", null);

        Assert.Equal(DoctorStatus.Unknown, Check().Status);
        Assert.Equal("incomparable", Result());
    }

    [Fact]
    public void A_check_that_found_nothing_to_believe_is_unknown_and_not_an_error()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, null, null));

        var report = DoctorCommand.Inspect(root, now: Checked.AddHours(1));
        var check = report.Checks.Single(c => c.Name == "update");

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("unavailable", Result());
        Assert.Equal("not_recorded", Update()["reason"]!.GetValue<string>());
        Assert.Equal(
            "unknown; the last check produced nothing to believe (tried 2026-08-31 09:30 UTC, reason: not_recorded)",
            check.Detail);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_check_that_found_nothing_to_believe_says_why_and_when()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, null, null)
            {
                FailureReason = "verification_failed",
            });

        var update = Update();

        Assert.Equal("Unknown", update["status"]!.GetValue<string>());
        Assert.Equal("unavailable", update["result"]!.GetValue<string>());
        Assert.Equal("verification_failed", update["reason"]!.GetValue<string>());
        Assert.Equal("2026-08-31T09:30:00Z", update["checkedAtUtc"]!.GetValue<string>());
        Assert.EndsWith("(tried 2026-08-31 09:30 UTC, reason: verification_failed)",
            update["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_the_product_never_writes_is_not_repeated()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, null, null)
            {
                FailureReason = "see http://evil.invalid now",
            });

        var update = Update();

        Assert.Equal("unrecognized", update["reason"]!.GetValue<string>());
        Assert.DoesNotContain("evil", update["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An unverified answer is not information, however new the version inside it looks — the
    /// same rule the state file enforces, checked here because this is where a person reads it.
    /// </summary>
    [Fact]
    public void An_unverified_version_never_becomes_a_warning()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Unavailable,
            Checked,
            "9.9.9",
            "https://example.invalid/releases/tag/v9.9.9"));

        var check = Check();
        var update = Update();

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.DoesNotContain("9.9.9", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("9.9.9", update.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(update["releaseUrl"]);
    }

    [Fact]
    public void An_unknown_update_is_counted_but_moves_neither_the_verdict_nor_the_exit_code()
    {
        var known = DoctorCommand.Inspect(root, now: Checked.AddHours(1));
        Enable();
        var unknown = DoctorCommand.Inspect(root, now: Checked.AddHours(1));

        Assert.Equal(known.Verdict, unknown.Verdict);
        Assert.Equal(known.ExitCode, unknown.ExitCode);
        var before = DoctorCommand.Describe(known);
        var after = DoctorCommand.Describe(unknown);
        // Relative: this installation has no broker record of its models either, and task-models
        // counts as unanswered for that on both sides.
        Assert.Equal(before["unknown"]!.GetValue<int>() + 1, after["unknown"]!.GetValue<int>());
        Assert.Equal(before["warned"]!.GetValue<int>(), after["warned"]!.GetValue<int>());
        Assert.Equal(before["failed"]!.GetValue<int>(), after["failed"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(DoctorStatus.Ok)]
    [InlineData(DoctorStatus.Warning)]
    [InlineData(DoctorStatus.Failed)]
    public void An_unknown_check_leaves_the_verdict_and_the_exit_code_to_the_others(DoctorStatus other)
    {
        var alone = new DoctorReport([new DoctorCheck("other", other, "detail")]);
        var withUnknown = new DoctorReport(
        [
            new DoctorCheck("other", other, "detail"),
            new DoctorCheck("update", DoctorStatus.Unknown, "detail"),
        ]);

        Assert.Equal(other, withUnknown.Verdict);
        Assert.Equal(alone.ExitCode, withUnknown.ExitCode);
    }

    [Fact]
    public void Reading_the_update_line_changes_nothing_on_disk()
    {
        Enable();
        new UpdateCheckStateStore(root).Write(
            new UpdateCheckState(1, UpdateCheckStatus.Unavailable, Checked, null, null) { FailureReason = "network" });
        var state = File.ReadAllBytes(new UpdateCheckStateStore(root).FilePath);

        _ = DoctorCommand.Inspect(root, now: Checked.AddDays(30));

        Assert.Equal(state, File.ReadAllBytes(new UpdateCheckStateStore(root).FilePath));
    }

    private DoctorCheck Check(DateTimeOffset? now = null) =>
        DoctorCommand.Inspect(root, now: now ?? Checked.AddHours(1)).Checks.Single(check => check.Name == "update");

    private JsonObject Update(DateTimeOffset? now = null) =>
        DoctorCommand.Describe(DoctorCommand.Inspect(root, now: now ?? Checked.AddHours(1)))["checks"]!.AsArray()
            .Single(check => check!["name"]!.GetValue<string>() == "update")!.AsObject();

    private string Result(DateTimeOffset? now = null) => Update(now)["result"]!.GetValue<string>();

    private void Enable() =>
        new UpdateCheckPolicyStore(root).Write(
            UpdateCheckPolicy.Default with { Enabled = true });

    private void Learned(string version, string? versionDirectory) =>
        new UpdateCheckStateStore(root).Write(new UpdateCheckState(
            1,
            UpdateCheckStatus.Verified,
            Checked,
            version,
            "https://example.invalid/releases/tag/v" + version,
            versionDirectory));

    /// <summary>
    /// An installation as LocalAiPackageInstaller leaves one: the pointer names the version
    /// *directory*, and a separate record says which release that directory came from. The
    /// earlier fixture put the release version in the pointer, which is why every one of these
    /// tests passed while the product reported an available update as "up to date" (#255).
    /// </summary>
    private void Install(string directory, string release)
    {
        var versionDirectory = Path.Combine(root, "bin", "versions", directory);
        Directory.CreateDirectory(versionDirectory);
        foreach (var file in LocalAiPackageLayout.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionDirectory, file), "binary");
        }

        Directory.CreateDirectory(Path.Combine(root, "bin", "launcher"));
        File.WriteAllText(
            Path.Combine(root, "bin", "launcher", LocalAiPackageLayout.StableLauncherFile),
            "binary");
        File.WriteAllBytes(
            Path.Combine(root, "bin", "current.json"),
            CurrentPointerSnapshot.CreateCanonicalBytes(directory));
        new InstalledReleaseStore(Path.Combine(root, "bin")).Write(directory, release);
    }
}
