using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The run's log sits beside its journal, and the journal is named in UTC. A log named in local
/// time made one run at UTC+3 read as two runs on different days.
/// </summary>
public sealed class TheLogIsNamedOnTheJournalsClockTests
{
    [Fact]
    public async Task The_run_log_is_named_in_utc()
    {
        Assert.Equal("install-20260926-221355Z.log", await LogNameAsync());
    }

    /// <summary>
    /// The journal beside it keeps Gregorian digits, and the two are matched by their time: a Thai
    /// culture counts 2569 where the others count 2026.
    /// </summary>
    [Fact]
    public async Task The_run_log_keeps_the_gregorian_year_on_a_thai_machine()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            // The premise: this culture really counts years another way here.
            Assert.IsType<ThaiBuddhistCalendar>(CultureInfo.CurrentCulture.Calendar);

            Assert.Equal("install-20260926-221355Z.log", await LogNameAsync());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static async Task<string?> LogNameAsync()
    {
        using var logs = new TemporaryDirectory("LocalAi-log-clock");
        var presentation = new InstallerPresentation(
            new InstallerPreferencesStore(logs.Combine("preferences")));
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ => Task.FromResult(HermeticDiagnosis.Supported()),
            presentation: presentation)
        {
            LogDirectory = logs.Combine("logs"),
            RuntimeRoot = logs.Combine("no-runtime-root"),
            Clock = new ThreeHoursEastOfUtc(new DateTimeOffset(2026, 9, 26, 22, 13, 55, TimeSpan.Zero)),
        };
        try
        {
            wizard.Diagnose.IsChecking = false;
            wizard.Diagnose.SetResult(supported: true);
            Assert.True(wizard.MoveNext());
            wizard.SetReviewConfirmed(true);

            Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

            return Path.GetFileName(wizard.Finish.ReportPath);
        }
        finally
        {
            wizard.DetachPresentation();
        }
    }

    private sealed class ThreeHoursEastOfUtc(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+03", TimeSpan.FromHours(3), "UTC+03", "UTC+03");
    }
}
