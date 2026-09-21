using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class StatusAnnouncementTests
{
    [Theory]
    [InlineData(BrokerModelInstallOutcome.Accepted)]
    [InlineData(BrokerModelInstallOutcome.RejectedResidency)]
    [InlineData(BrokerModelInstallOutcome.Cancelled)]
    public void Model_announcements_wait_for_a_verdict_and_use_the_current_language(BrokerModelInstallOutcome outcome)
    {
        var row = new ModelRunRow("model-a", 2048);
        Assert.Empty(row.Announcement);
        foreach (var phase in new[] { ModelProvisioningPhase.Planned, ModelProvisioningPhase.Checking })
        {
            row.Apply(new("model-a", 2048, phase, 0, 1));
            Assert.Empty(row.Announcement);
        }
        row.Settled(outcome, true, [], "test_code");
        var before = InstallerCulture.Current;
        try
        {
            InstallerCulture.Current = InstallerLanguage.English;
            var english = row.Announcement;
            Assert.Equal("model-a — " + row.Detail, english);
            InstallerCulture.Current = InstallerLanguage.Russian;
            Assert.Equal("model-a — " + row.Detail, row.Announcement);
            Assert.NotEqual(english, row.Announcement);
            row.Apply(new("model-a", 2048, ModelProvisioningPhase.Checking, 0, 1));
            Assert.Equal("model-a — " + row.Detail, row.Announcement);
        }
        finally { InstallerCulture.Current = before; }
    }

    [Fact]
    public void Diagnosis_announces_completion_and_the_blocking_reason_only_after_checking()
    {
        var diagnosis = new DiagnosePageViewModel();
        diagnosis.SetResult(false, "unsupported-system");
        Assert.Empty(diagnosis.Announcement);
        diagnosis.IsChecking = false;
        Assert.Contains("unsupported-system", diagnosis.Announcement, StringComparison.Ordinal);
        diagnosis.IsChecking = true;
        Assert.Empty(diagnosis.Announcement);
        diagnosis.SetResult(true);
        diagnosis.IsChecking = false;
        Assert.NotEmpty(diagnosis.Announcement);
        Assert.DoesNotContain("unsupported-system", diagnosis.Announcement, StringComparison.Ordinal);
    }
}
