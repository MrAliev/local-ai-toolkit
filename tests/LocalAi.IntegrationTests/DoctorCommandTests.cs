using System.Globalization;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The value of a diagnostic is what it says when something is wrong, so these break an
/// installation one way at a time and assert the report names that way and no other.
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_complete_installation_reports_no_problems()
    {
        Install("v1");

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(DoctorStatus.Ok, Check(report, "version").Status);
        Assert.Equal(DoctorStatus.Ok, Check(report, "launcher").Status);
        Assert.Contains("v1", Check(report, "version").Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Which rows appear is what carries the state, so the row a reader scans for must not be the
    /// one that is missing. Run without --root, doctor used to drop the repository check entirely
    /// and still close with "no problems": eight green rows about a repository it never looked at.
    ///
    /// Standing somewhere that is not a repository is not a fault, so this is ok rather than a
    /// warning and cannot move the exit code. What the caller names is checked and may fail; where
    /// the caller happens to be standing is reported and cannot.
    /// </summary>
    [Fact]
    public void A_directory_that_is_no_repository_is_still_reported_as_a_row()
    {
        Install("v1");
        var elsewhere = Path.Combine(_root, "not-a-repository");
        Directory.CreateDirectory(elsewhere);

        var report = DoctorCommand.Inspect(_root, repositoryRoot: null, workingDirectory: elsewhere);

        var repository = Check(report, "repository");
        Assert.Equal(DoctorStatus.Ok, repository.Status);
        Assert.Contains(elsewhere, repository.Detail, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// A <c>.git</c> that git will not answer about — an empty one here, a checkout with no commit
    /// yet in life. The row says the check did not run, and stays a warning: the reader named no
    /// repository, so nothing they asserted has failed, and doctor must not begin exiting 1 for
    /// standing somewhere ordinary.
    /// </summary>
    [Fact]
    public void A_repository_git_will_not_answer_about_is_noted_rather_than_failed()
    {
        Install("v1");
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));

        var report = DoctorCommand.Inspect(_root, repositoryRoot: null, workingDirectory: checkout);

        Assert.Equal(DoctorStatus.Warning, Check(report, "repository").Status);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void An_empty_runtime_root_fails_rather_than_reporting_nothing()
    {
        Directory.CreateDirectory(_root);

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(DoctorStatus.Failed, Check(report, "version").Status);
    }

    /// <summary>
    /// The shape a half-finished install leaves behind: the pointer is confident and the version
    /// it names is incomplete. Every tool then fails one at a time, each blaming itself.
    /// </summary>
    [Fact]
    public void A_version_missing_binaries_is_named_along_with_what_is_missing()
    {
        Install("v1");
        File.Delete(Path.Combine(_root, "bin", "versions", "v1", "codesearch-mcp.exe"));

        var check = Check(DoctorCommand.Inspect(_root), "version");

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Contains("codesearch-mcp.exe", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pointer_naming_an_absent_version_is_a_failure()
    {
        Install("v1");
        Directory.Delete(Path.Combine(_root, "bin", "versions", "v1"), recursive: true);

        var check = Check(DoctorCommand.Inspect(_root), "version");

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Contains("v1", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stopped broker is not a fault — it starts on demand — so it must not make the exit code
    /// non-zero. A diagnostic that fails for ordinary states gets its exit code ignored.
    /// </summary>
    [Fact]
    public void A_broker_that_is_not_running_is_worth_reading_but_not_a_failure()
    {
        Install("v1");

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(DoctorStatus.Warning, Check(report, "broker").Status);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public void A_broker_silent_for_too_long_is_distinguished_from_a_live_one()
    {
        Install("v1");
        var host = WriteHost(DateTimeOffset.UtcNow - TimeSpan.FromHours(2));

        var stale = Check(DoctorCommand.Inspect(_root, processStartedAt: Running(host)), "broker");
        host = WriteHost(DateTimeOffset.UtcNow);
        var live = Check(DoctorCommand.Inspect(_root, processStartedAt: Running(host)), "broker");

        Assert.Equal(DoctorStatus.Warning, stale.Status);
        Assert.Contains("4242", stale.Detail, StringComparison.Ordinal);
        Assert.Equal(DoctorStatus.Ok, live.Status);
    }

    /// <summary>
    /// Run 20260927-233709: the broker's process was gone, host.json was left from 103 s earlier,
    /// and doctor printed "ok".
    /// </summary>
    [Fact]
    public void A_broker_whose_process_is_gone_is_not_reported_as_running()
    {
        Install("v1");
        WriteHost(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(103));

        var check = Check(DoctorCommand.Inspect(_root, processStartedAt: _ => null), "broker");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(CliText.BrokerNotRunning, check.Detail);
    }

    [Fact]
    public void A_process_that_took_the_brokers_id_is_not_taken_for_the_broker()
    {
        Install("v1");
        var host = WriteHost(DateTimeOffset.UtcNow);

        var check = Check(
            DoctorCommand.Inspect(
                _root,
                processStartedAt: id => id == host.ProcessId ? host.StartedAtUtc.AddSeconds(1) : null),
            "broker");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(CliText.BrokerNotRunning, check.Detail);
    }

    private static Func<int, DateTimeOffset?> Running(BrokerProcessState host) =>
        id => id == host.ProcessId ? host.StartedAtUtc : null;

    /// <summary>
    /// Quarantined jobs are never retried and nothing else mentions them, so they accumulate
    /// unseen — two sat in this runtime for six weeks before anyone looked.
    /// </summary>
    [Fact]
    public void Quarantined_jobs_are_surfaced()
    {
        Install("v1");
        Directory.CreateDirectory(Path.Combine(_root, "quarantine", "stuck-job"));

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Contains("quarantined", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A queue that has stopped looks exactly like a busy one to a count of directories, and that
    /// is what this check was: `4 queued` and `No problems`, printed for two hours while a Git
    /// hook's embedding job waited behind a job nothing would ever pick (#335). The broker was
    /// healthy by every measure it reports — a heartbeat one second old, the backend reachable —
    /// because it was the queue that was not moving.
    ///
    /// The evidence was already on disk: each job's state file carries when it was created and
    /// how many times it has been attempted. Never attempted, and older than the age at which the
    /// scheduler itself force-includes a starved job, is not a queue anybody should call healthy.
    /// </summary>
    [Fact]
    public void A_queue_that_has_not_moved_is_not_reported_as_healthy()
    {
        Install("v1");
        Queued("stalled-job", DateTimeOffset.UtcNow.AddHours(-2), attempts: 0);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Warning, check.Status);
    }

    /// <summary>
    /// A job merely waiting its turn is not a problem. Something is always last in a queue, and a
    /// check that warned about that would be noise on every busy machine.
    /// </summary>
    [Fact]
    public void A_job_waiting_its_turn_is_not_a_problem()
    {
        Install("v1");
        Queued("fresh-job", DateTimeOffset.UtcNow, attempts: 0);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Ok, check.Status);
    }

    /// <summary>
    /// A job that has been attempted is being served, however long it has been there: a long
    /// inference is work, not a stall.
    /// </summary>
    [Fact]
    public void A_long_job_that_is_being_attempted_is_not_a_stall()
    {
        Install("v1");
        Queued("running-job", DateTimeOffset.UtcNow.AddHours(-2), attempts: 1);

        var check = Check(DoctorCommand.Inspect(_root), "queue");

        Assert.Equal(DoctorStatus.Ok, check.Status);
    }

    /// <summary>One job directory, shaped the way the durable queue writes them.</summary>
    private void Queued(string name, DateTimeOffset createdAtUtc, int attempts)
    {
        var directory = Path.Combine(_root, "jobs", name);
        Directory.CreateDirectory(directory);
        var moment = createdAtUtc.ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(
            Path.Combine(directory, "state.json"),
            $$"""
            {"SchemaVersion":1,"JobId":"{{Guid.NewGuid()}}","Sequence":1,
             "Priority":"Foreground","State":"Queued",
             "CreatedAtUtc":"{{moment}}","UpdatedAtUtc":"{{moment}}",
             "WorkerId":null,"LeaseId":null,"LeaseExpiresAtUtc":null,"HeartbeatAtUtc":null,
             "AttemptCount":{{attempts}},"RecoveryCount":0,"FailureCode":null}
            """);
    }

    /// <summary>
    /// A policy file holding exactly the default values is configured, not broken. The first
    /// version of this check inferred "malformed" from "reads back as the defaults" and cried
    /// wolf on the first real installation it saw.
    /// </summary>
    [Fact]
    public void A_policy_file_that_matches_the_defaults_is_not_reported_as_a_problem()
    {
        Install("v1");
        new ModelResidencyPolicyStore(_root).Write(BrokerPolicy.Default);

        var report = DoctorCommand.Inspect(_root);

        Assert.Equal(DoctorStatus.Ok, Check(report, "policy.models").Status);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The case the policy checks existed for and did not catch. A store answers a document it
    /// cannot use with its defaults, so the file stays on disk looking configured while doing
    /// nothing; this check used to print those defaults as though the file had produced them.
    /// </summary>
    [Fact]
    public void A_policy_file_that_stopped_parsing_is_not_reported_as_configured()
    {
        Install("v1");
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(
            Path.Combine(_root, "settings", BrokerPolicy.FileName),
            "{ this is not json");

        var check = Check(DoctorCommand.Inspect(_root), "policy.models");

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Contains(
            Path.Combine(_root, "settings", BrokerPolicy.FileName),
            check.Detail,
            StringComparison.Ordinal);
        // A warning, not a failure: every store falls back towards its safe value, so an ignored
        // file can only make the machine stricter than asked, never looser.
        Assert.Equal(0, DoctorCommand.Inspect(_root).ExitCode);
    }

    /// <summary>
    /// "No file" and "a file that does nothing" are different things to whoever wrote one, so a
    /// program can tell them apart without reading the sentence.
    /// </summary>
    [Fact]
    public void The_machine_face_tells_a_missing_file_from_one_that_is_ignored()
    {
        Install("v1");
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(
            Path.Combine(_root, "settings", BrokerPolicy.FileName),
            "{ this is not json");

        using var document = JsonDocument.Parse(
            MachineOutput.Answer("doctor", DoctorCommand.Describe(DoctorCommand.Inspect(_root))));
        var checks = document.RootElement.GetProperty("data").GetProperty("checks");
        var models = checks.EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "policy.models");
        var retention = checks.EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "policy.retention");

        Assert.True(models.GetProperty("fileFound").GetBoolean());
        Assert.False(models.GetProperty("fileUsed").GetBoolean());
        Assert.False(retention.GetProperty("fileFound").GetBoolean());
        Assert.False(retention.GetProperty("fileUsed").GetBoolean());
    }

    private static DoctorCheck Check(DoctorReport report, string name) =>
        report.Checks.Single(check => check.Name == name);

    private BrokerProcessState WriteHost(DateTimeOffset heartbeat)
    {
        var host = new BrokerProcessState(
            4242,
            heartbeat - TimeSpan.FromMinutes(1),
            heartbeat,
            1,
            Path.Combine(_root, "bin", "versions", "v1", "LocalAi.Broker.exe"));
        File.WriteAllText(
            Path.Combine(_root, "host.json"),
            JsonSerializer.Serialize(host, LocalAiJson.Strict));
        return host;
    }

    private void Install(string version)
    {
        var versionDirectory = Path.Combine(_root, "bin", "versions", version);
        Directory.CreateDirectory(versionDirectory);
        foreach (var file in LocalAiPackageLayout.VersionRequiredFiles)
        {
            File.WriteAllText(Path.Combine(versionDirectory, file), file);
        }

        var launcherDirectory = Path.Combine(_root, "bin", "launcher");
        Directory.CreateDirectory(launcherDirectory);
        File.WriteAllText(
            Path.Combine(launcherDirectory, LocalAiPackageLayout.StableLauncherFile),
            "launcher");
        File.WriteAllText(
            Path.Combine(_root, "bin", "current.json"),
            $$"""{"schemaVersion":1,"version":"{{version}}"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
