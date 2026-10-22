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
        var root = Directory.CreateTempSubdirectory("semantic-tool-boundary-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
            var neutral = Directory.CreateDirectory(Path.Combine(root, "neutral")).FullName;
            var marker = Path.Combine(root, "marker.txt");
            var command = "missing-semantic-tool-" + Guid.NewGuid().ToString("N");
            var plantedExecutable = Path.Combine(project, command + ".exe");
            var parentCwd = Environment.CurrentDirectory;

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

            var outcome = await RunProbeAsync(start);
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

    [Theory]
    [InlineData("scip", "workspace-only")]
    [InlineData("lsp", "workspace-only")]
    [InlineData("scip", "trusted-path")]
    [InlineData("lsp", "trusted-path")]
    [InlineData("scip", "explicit-relative")]
    [InlineData("lsp", "explicit-relative")]
    public async Task Command_script_limits_implicit_dependency_lookup(string launcher, string scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Regression for Windows command-script lookup.");
        var fixture = typeof(SemanticToolExecutableBoundaryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SemanticToolProcessFixtureExecutable").Value!;
        var root = Directory.CreateTempSubdirectory("semantic-script-boundary-").FullName;
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var trusted = Directory.CreateDirectory(Path.Combine(root, "trusted-tools")).FullName;
            var marker = Path.Combine(root, "selected.txt");
            var dependency = "semantic-dependency-" + Guid.NewGuid().ToString("N") + ".exe";
            foreach (var directory in new[] { workspace, trusted })
            {
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }
            }

            // Keep a workspace decoy even when a trusted PATH dependency is available.
            var workspaceDependency = Path.Combine(workspace, dependency);
            File.Copy(fixture, workspaceDependency);
            var trustedDependency = Path.Combine(trusted, dependency);
            if (scenario == "trusted-path")
            {
                File.Copy(fixture, trustedDependency);
            }

            var script = Path.Combine(root, "trusted-adapter.cmd");
            var command = scenario == "explicit-relative" ? ".\\" + dependency : dependency;
            await File.WriteAllTextAsync(script,
                $"@echo off\r\n{command} %*\r\n> \"%~2.done\" echo finished\r\nexit /b 23\r\n",
                TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(fixture)
            {
                UseShellExecute = false,
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Only this isolated host loses the inherited flag; production must set it again.
            start.Environment.Remove("NoDefaultCurrentDirectoryInExePath");
            if (scenario == "trusted-path")
            {
                start.Environment["PATH"] = trusted + Path.PathSeparator + start.Environment["PATH"];
            }

            foreach (var argument in new[] { "probe-script", launcher, script, workspace, marker, scenario, dependency })
            {
                start.ArgumentList.Add(argument);
            }

            var outcome = await RunProbeAsync(start);
            Assert.SkipWhen(outcome == "ConfiguredLookup",
                "A configured PATH entry deliberately resolves the workspace dependency.");
            Assert.Equal(launcher == "scip" ? "Failed" : "Started", outcome);
            Assert.True(File.Exists(marker + ".done"), "The command script did not finish.");
            if (scenario == "workspace-only")
            {
                Assert.False(File.Exists(marker));
            }
            else
            {
                Assert.Equal(scenario == "trusted-path" ? trustedDependency : workspaceDependency,
                    await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken),
                    ignoreCase: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("scip", "missing")]
    [InlineData("lsp", "missing")]
    [InlineData("scip", "explicit-system")]
    [InlineData("lsp", "explicit-system")]
    [InlineData("scip", "explicit-fixture")]
    [InlineData("lsp", "explicit-fixture")]
    [InlineData("scip", "bare")]
    [InlineData("lsp", "bare")]
    [InlineData("scip", "relative")]
    [InlineData("lsp", "relative")]
    [InlineData("scip", "drive-relative")]
    [InlineData("lsp", "drive-relative")]
    [InlineData("scip", "root-relative")]
    [InlineData("lsp", "root-relative")]
    [InlineData("scip", "explicit-missing")]
    [InlineData("lsp", "explicit-missing")]
    public async Task Interpreter_selection_requires_an_explicit_full_path(string launcher, string scenario)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Regression for Windows interpreter lookup.");
        var fixture = typeof(SemanticToolExecutableBoundaryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SemanticToolProcessFixtureExecutable").Value!;
        var root = Directory.CreateTempSubdirectory("semantic-interpreter-boundary-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "snapshot")).FullName;
            var marker = Path.Combine(project, "interpreter-selected.txt");
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!))
            {
                File.Copy(file, Path.Combine(project, Path.GetFileName(file)));
            }

            var decoy = Path.Combine(project, "cmd.exe");
            File.Copy(fixture, decoy);
            var script = Path.Combine(root, "trusted-adapter.cmd");
            await File.WriteAllTextAsync(script,
                "@echo off\r\n> \"%~2.done\" echo finished\r\nexit /b 23\r\n",
                TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(fixture)
            {
                UseShellExecute = false,
                WorkingDirectory = project,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Change only this isolated host; the test runner keeps its environment and CWD.
            if (scenario == "missing")
            {
                start.Environment.Remove("ComSpec");
            }
            else
            {
                if (scenario == "drive-relative")
                {
                    Assert.SkipUnless(Path.GetPathRoot(project) is { Length: >= 2 } drive && drive[1] == ':',
                        "Drive-relative lookup requires a DOS drive fixture.");
                }

                start.Environment["ComSpec"] = scenario switch
                {
                    "explicit-system" => Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "explicit-fixture" => decoy,
                    "explicit-missing" => Path.Combine(root, "absent-interpreter.exe"),
                    "bare" => "cmd.exe",
                    "relative" => ".\\cmd.exe",
                    "drive-relative" => Path.GetPathRoot(project)![..2] + "cmd.exe",
                    "root-relative" => "\\cmd.exe",
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }

            foreach (var argument in new[] { "probe-interpreter", launcher, script, workspace, marker })
            {
                start.ArgumentList.Add(argument);
            }

            var expectedOutcome = scenario == "explicit-missing"
                ? launcher == "scip" ? "Skipped" : "Missing"
                : launcher == "scip" ? "Failed" : "Started";
            Assert.Equal(expectedOutcome, await RunProbeAsync(start));
            var fixtureSelected = scenario == "explicit-fixture";
            Assert.Equal(fixtureSelected, File.Exists(marker));
            Assert.Equal(scenario is not ("explicit-fixture" or "explicit-missing"), File.Exists(marker + ".done"));
            if (fixtureSelected)
            {
                Assert.Equal(decoy,
                    await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken),
                    ignoreCase: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> RunProbeAsync(ProcessStartInfo start)
    {
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
        return (await stdout).Trim();
    }
}
