using System.Diagnostics;
using System.Reflection;

namespace CodeSearch.Tests;

public sealed class SemanticToolExecutableBoundaryTests
{
    [Theory]
    [InlineData("scip", "parent-cwd", "Skipped", false)]
    [InlineData("lsp", "parent-cwd", "Missing", false)]
    [InlineData("scip", "child-cwd", "Skipped", false)]
    [InlineData("lsp", "child-cwd", "Missing", false)]
    [InlineData("scip", "explicit", "Failed", true)]
    [InlineData("lsp", "explicit", "Started", true)]
    public async Task Launch_uses_only_the_resolved_tool(
        string launcher, string scenario, string expectedOutcome, bool expectedMarker)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Regression for Windows executable lookup.");
        var fixture = typeof(SemanticToolExecutableBoundaryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SemanticToolProcessFixtureExecutable").Value!;
        var root = Path.Combine(Path.GetTempPath(), "semantic-tool-boundary-" + Guid.NewGuid().ToString("N"));
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
        var neutral = Directory.CreateDirectory(Path.Combine(root, "neutral")).FullName;
        var marker = Path.Combine(root, "marker.txt");
        var command = "missing-semantic-tool-" + Guid.NewGuid().ToString("N");
        var plantedExecutable = Path.Combine(project, command + ".exe");
        var parentCwd = Environment.CurrentDirectory;
        try
        {
            // The copied apphost keeps its original DLL/runtimeconfig alongside it.
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!))
            {
                File.Copy(file, Path.Combine(project, Path.GetFileName(file)));
            }

            File.Copy(fixture, plantedExecutable);
            var start = new ProcessStartInfo(fixture)
            {
                UseShellExecute = false,
                WorkingDirectory = scenario == "child-cwd" ? neutral : project,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "probe", launcher,
                         scenario == "explicit" ? plantedExecutable : command,
                         scenario == "child-cwd" ? project : workspace, marker })
            {
                start.ArgumentList.Add(argument);
            }

            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            Assert.True(child.ExitCode == 0, await stderr);
            var outcome = (await stdout).Trim();
            Assert.SkipWhen(outcome == "ConfiguredLookup",
                "A configured search directory already resolves the fixture command; OS fallback is not isolated.");
            Assert.Equal(expectedOutcome, outcome);
            Assert.Equal(expectedMarker, File.Exists(marker));
            Assert.Equal(parentCwd, Environment.CurrentDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
