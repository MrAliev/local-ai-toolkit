using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;

using LocalAi.TestSupport;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemEnvironmentProbeTests
{
    [Fact]
    public void Windows_command_candidates_prefer_executable_shims_over_extensionless_scripts()
    {
        var candidates = SystemEnvironmentProbe.CandidateNames(
            "scip-typescript",
            isWindows: true,
            ".COM;.EXE;.BAT;.CMD");

        Assert.Equal(
            [
                "scip-typescript.COM",
                "scip-typescript.EXE",
                "scip-typescript.BAT",
                "scip-typescript.CMD",
                "scip-typescript",
            ],
            candidates);
    }

    [Fact]
    public void Explicit_extensions_and_non_windows_commands_are_unchanged()
    {
        Assert.Equal(
            ["npm.cmd"],
            SystemEnvironmentProbe.CandidateNames(
                "npm.cmd",
                isWindows: true,
                ".COM;.EXE;.BAT;.CMD"));
        Assert.Equal(
            ["python3"],
            SystemEnvironmentProbe.CandidateNames(
                "python3",
                isWindows: false,
                null));
    }

    /// <summary>
    /// Windows 11 left the registry's ProductName at "Windows 10 …", so the system check named a
    /// Windows 11 24H2 machine "Windows 10 Enterprise". Only the build tells the two apart.
    /// </summary>
    [Theory]
    [InlineData("Windows 10 Enterprise", 26100, "Windows 11 Enterprise")]
    [InlineData("Windows 10 Pro", 22000, "Windows 11 Pro")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows Server 2025 Datacenter", 26100, "Windows Server 2025 Datacenter")]
    public void Windows_is_named_by_its_build_rather_than_by_the_registry_alone(
        string registryProductName,
        int build,
        string expected)
    {
        Assert.Equal(
            expected,
            SystemEnvironmentProbe.WindowsProductName(
                registryProductName,
                new Version(10, 0, build)));
    }

    [Fact]
    public void Installed_npm_scip_commands_resolve_to_windows_shims()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm Windows shim precedence is Windows-specific.");
        }

        var npmDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");
        var commands = new[] { "scip-typescript", "scip-python" };
        if (commands.Any(command =>
                !File.Exists(Path.Combine(npmDirectory, command)) ||
                !File.Exists(Path.Combine(npmDirectory, command + ".cmd"))))
        {
            Assert.Skip("Install both SCIP npm packages to run the real shim test.");
        }

        var probe = new SystemEnvironmentProbe();
        Assert.All(commands, command =>
            Assert.EndsWith(
                command + ".cmd",
                probe.ResolveExecutable(command),
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("scip-typescript", "0.4.0")]
    [InlineData("scip-python", "0.6.6")]
    public async Task Installed_npm_scip_shims_execute_the_same_version_probes_as_the_installer(
        string command, string expectedVersion)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm Windows shim execution is Windows-specific.");
        }

        var probe = new SystemEnvironmentProbe();
        var runner = new SystemProcessRunner();
        var executable = command == "scip-python"
            ? Environment.GetEnvironmentVariable("LOCALAI_SCIP_PYTHON_EXECUTABLE") ?? probe.ResolveExecutable(command)
            : probe.ResolveExecutable(command);
        if (executable is null)
        {
            Assert.Skip($"Install {command} to run the real version probe.");
        }

        // CI passes the prepared private Python shim explicitly, just as the semantic
        // fixtures do. Its other npm shims need not shadow unrelated tools on PATH.
        // An installed tool that cannot start is a failure unless explicitly excused.
        await ScipVersionProbe.VerifyAsync(
            command, executable, expectedVersion, runner, FixturePrerequisite.Excused,
            probe.ResolveExecutable, TestContext.Current.CancellationToken);
    }
}
