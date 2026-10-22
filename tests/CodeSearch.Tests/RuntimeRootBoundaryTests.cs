using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class RuntimeRootBoundaryTests
{
    [Fact]
    public void Default_runtime_root_preserves_the_user_data_location_on_this_host()
    {
        var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(userData) || !Path.IsPathFullyQualified(userData))
        {
            Assert.Throws<InvalidOperationException>(() => ModelResidencyPolicyStore.DefaultRuntimeRoot);
            return;
        }

        Assert.Equal(Path.Combine(userData, "LocalAi"), ModelResidencyPolicyStore.DefaultRuntimeRoot);
        Assert.True(Path.IsPathFullyQualified(ModelResidencyPolicyStore.DefaultRuntimeRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_runtime_root_stays_in_the_chosen_linux_user_data_directory(bool createDataDirectory)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Regression for Linux user-data directory selection.");
        var fixture = typeof(RuntimeRootBoundaryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SemanticToolProcessFixtureExecutable").Value!;
        fixture = Path.ChangeExtension(fixture.Replace('\\', Path.DirectorySeparatorChar), null);
        var root = Directory.CreateTempSubdirectory("runtime-root-boundary-").FullName;
        try
        {
            var data = Path.Combine(root, "data");
            if (createDataDirectory)
            {
                Directory.CreateDirectory(data);
            }

            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var start = new ProcessStartInfo(fixture)
            {
                UseShellExecute = false,
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("probe-runtime-root");
            start.ArgumentList.Add(data);
            start.Environment["HOME"] = home;
            start.Environment["XDG_DATA_HOME"] = data;
            using var process = Process.Start(start)!;
            // Drain even after cancellation so the owned child can exit before cleanup.
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(output, error);
                throw;
            }

            var text = await output;
            Assert.True(process.ExitCode == 0, await error);
            using var result = JsonDocument.Parse(text);
            var expectedRoot = Path.Combine(data, "LocalAi");
            var settingsFile = RuntimeDirectories.SettingsFileForWriting(expectedRoot, BrokerPolicy.FileName);
            Assert.Equal(expectedRoot, result.RootElement.GetProperty("RuntimeRoot").GetString());
            Assert.Equal(createDataDirectory, result.RootElement.GetProperty("DataExistsAfterLookup").GetBoolean());
            Assert.Equal(settingsFile, result.RootElement.GetProperty("SettingsFile").GetString());
            Assert.True(result.RootElement.GetProperty("RoundTrip").GetBoolean());
            Assert.True(File.Exists(settingsFile));
            Assert.False(Directory.Exists(Path.Combine(workspace, "LocalAi")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
