using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallerLogDirectoryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("relative-user-data")]
    [InlineData("C:relative")]
    [InlineData("\\rooted-without-drive")]
    public void An_unavailable_or_relative_profile_cannot_select_the_working_directory(string profile)
    {
        Assert.Throws<InvalidOperationException>(() => InstallerLogDirectory.FromLocalApplicationData(profile));
    }

    [Fact]
    public void An_absolute_profile_is_preserved()
    {
        var profile = Path.Combine(Path.GetTempPath(), "LocalAi.LogDirectory.Tests", Guid.NewGuid().ToString("N"));

        var actual = InstallerLogDirectory.FromLocalApplicationData(profile);

        Assert.Equal(Path.Combine(profile, "LocalAi-installer-logs"), actual);
        Assert.True(Path.IsPathFullyQualified(actual));
    }
}
