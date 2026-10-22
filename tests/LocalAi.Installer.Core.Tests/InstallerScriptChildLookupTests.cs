using System.Reflection;
using LocalAi.Installer.Core.Abstractions;
using Xunit;

namespace LocalAi.Installer.Core.Tests;

[CollectionDefinition("Installer script child lookup", DisableParallelization = true)]
public sealed class InstallerScriptChildLookupCollection;

[Collection("Installer script child lookup")]
public sealed class InstallerScriptChildLookupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Script_dependencies_use_configured_PATH_or_explicit_relative_commands(
        bool trustedPath, bool explicitRelative)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows command-script lookup is required.");
        const string restriction = "NoDefaultCurrentDirectoryInExePath";
        var previousRestriction = Environment.GetEnvironmentVariable(restriction);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousInterpreter = Environment.GetEnvironmentVariable("ComSpec");
        var previousDirectory = Environment.CurrentDirectory;
        var scratch = Directory.CreateTempSubdirectory("localai-script-child-boundary-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(scratch.FullName, "workspace")).FullName;
            var trusted = Directory.CreateDirectory(Path.Combine(scratch.FullName, "trusted")).FullName;
            var fixture = typeof(InstallerScriptChildLookupTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ProcessFixtureExecutable").Value!;
            Assert.True(File.Exists(fixture), $"The process fixture was not built at '{fixture}'.");
            foreach (var directory in new[] { workspace, trusted })
            {
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!))
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                File.Copy(fixture, Path.Combine(directory, "localai-script-dependency.exe"));
            }

            var script = Path.Combine(trusted, "trusted.cmd");
            File.WriteAllText(script, "@echo off\r\n" +
                (explicitRelative ? ".\\" : "") + "localai-script-dependency.exe process-path\r\n");
            var configuredPath = (trustedPath ? trusted + Path.PathSeparator : "") +
                Environment.SystemDirectory + Path.PathSeparator +
                Directory.GetParent(Environment.SystemDirectory)!.FullName;
            Environment.SetEnvironmentVariable("PATH", configuredPath);
            Environment.SetEnvironmentVariable("ComSpec", Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            Environment.SetEnvironmentVariable(restriction, null);
            Assert.Null(Environment.GetEnvironmentVariable(restriction));

            var result = await new SystemProcessRunner().RunInDirectoryAsync(
                script, [], workspace, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Null(Environment.GetEnvironmentVariable(restriction));
            Assert.Equal(configuredPath, Environment.GetEnvironmentVariable("PATH"));
            Assert.Equal(previousDirectory, Environment.CurrentDirectory);
            if (!trustedPath && !explicitRelative)
            {
                Assert.NotEqual(0, result.ExitCode);
                Assert.Empty(result.StandardOutput);
            }
            else
            {
                Assert.Equal(0, result.ExitCode);
                var expected = Path.Combine(explicitRelative ? workspace : trusted, "localai-script-dependency.exe");
                Assert.True(string.Equals(expected, result.StandardOutput.Trim(), StringComparison.OrdinalIgnoreCase),
                    $"Expected '{expected}', actual '{result.StandardOutput.Trim()}'.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(restriction, previousRestriction);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("ComSpec", previousInterpreter);
            Environment.CurrentDirectory = previousDirectory;
            scratch.Delete(recursive: true);
        }
    }
}
