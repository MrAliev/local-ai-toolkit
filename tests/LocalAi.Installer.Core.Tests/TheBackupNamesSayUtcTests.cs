using System.Globalization;
using System.Text;
using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// A client file's backup is named for the moment in UTC, marked Z, like the run's log and
/// journal: the report puts them side by side, and a local time would make one run read as two,
/// or two runs an hour apart read as one when the clocks go back. The digits are Gregorian
/// whatever calendar the machine formats dates in (#614).
/// </summary>
public sealed class TheBackupNamesSayUtcTests
{
    private const string Settings = "C:\\home\\.claude\\settings.json";

    // 19:58:56 UTC, read on a machine three hours east of it.
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 27, 22, 58, 56, TimeSpan.FromHours(3));

    private static readonly byte[] Before = Encoding.UTF8.GetBytes("{}");

    [Fact]
    public void A_rewritten_file_is_backed_up_under_the_utc_moment()
    {
        var plan = AgentConfigurationFileOperations.FilePlan(Settings, Before, "{ }", Moment);

        Assert.Equal(Settings + ".20260927-195856Z.bak", plan.BackupPath);
    }

    [Fact]
    public void A_deleted_file_is_backed_up_under_the_utc_moment()
    {
        var plan = AgentConfigurationFileOperations.DeletePlan(Settings, Before, Moment);

        Assert.Equal(Settings + ".20260927-195856Z.bak", plan.BackupPath);
    }

    [Fact]
    public void The_backup_keeps_the_gregorian_year_on_a_thai_machine()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            // The premise: this culture really counts years another way here.
            Assert.IsType<ThaiBuddhistCalendar>(CultureInfo.CurrentCulture.Calendar);
            Assert.Equal(
                Settings + ".20260927-195856Z.bak",
                AgentConfigurationFileOperations.FilePlan(Settings, Before, "{ }", Moment).BackupPath);
            Assert.Equal(
                Settings + ".20260927-195856Z.bak",
                AgentConfigurationFileOperations.DeletePlan(Settings, Before, Moment).BackupPath);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
