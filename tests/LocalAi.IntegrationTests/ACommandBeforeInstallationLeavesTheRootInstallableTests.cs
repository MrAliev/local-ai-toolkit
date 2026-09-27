using System.Runtime.Versioning;
using LocalAi.Broker;
using LocalAi.Installer.Core.Activation;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The documented offline path runs the portable CLI on a clean machine before anything is
/// installed, and the installation that follows has to accept whatever that left behind.
/// </summary>
public sealed class ACommandBeforeInstallationLeavesTheRootInstallableTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task The_installer_accepts_the_root_a_command_created_before_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installation layout is Windows-only.");
        using var profile = new TemporaryDirectory("localai-clean-profile");
        var layout = InstallationLayout.FromLocalAppData(profile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await CliUsageReporting.RunAsync("localai", ["offline", "plan"], layout.Root,
            output, error, (_, _) => Task.FromResult(0));

        Assert.Equal(0, exit);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(layout.Root, "telemetry", "usage")));
        using var lease = InstallationLayoutLease.Acquire(layout, requireFreshInstallerTree: true);
        Assert.Equal(layout.Root, lease.Layout.Root);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task The_installer_accepts_the_root_a_command_wrote_into_ahead_of_its_report()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installation layout is Windows-only.");
        using var profile = new TemporaryDirectory("localai-clean-profile");
        var layout = InstallationLayout.FromLocalAppData(profile.Path);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] arguments = ["policy", "set", "--language", "ru"];

        var exit = await CliUsageReporting.RunAsync("localai", arguments, layout.Root, output, error,
            (stdout, stderr) => Task.FromResult(
                LocalAi.Cli.PolicyCommand.Execute(arguments[1..], layout.Root, stdout, stderr)));

        Assert.Equal(0, exit);
        Assert.True(File.Exists(LocalAi.Contracts.Localization.OutputLanguageStore.PathFor(layout.Root)));
        using var lease = InstallationLayoutLease.Acquire(layout, requireFreshInstallerTree: true);
        Assert.Equal(layout.Root, lease.Layout.Root);
    }
}
