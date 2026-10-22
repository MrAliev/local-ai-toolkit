using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeSearch.Core.Indexing;
using LocalAi.Repository;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

public sealed class SnapshotGitDiscoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exported_snapshot_stops_implicit_git_discovery(bool nestedTemp, bool descendant)
    {
        var root = Directory.CreateTempSubdirectory("localai-snapshot-discovery-").FullName;
        try
        {
            var fixture = Fixture();
            var git = GitExecutableLocator.Resolve(root);
            var source = Path.Combine(root, "source");
            var ancestor = Path.Combine(root, "ancestor");
            var marker = Path.Combine(root, "fsmonitor.marker");
            var revision = await CreateRepository(git, source, "source revision");
            var ancestorRevision = await CreateRepository(git, ancestor, "unrelated ancestor");
            Assert.NotEqual(revision, ancestorRevision);
            await ConfigureMonitor(git, ancestor, fixture, marker);
            await Git(git, ancestor, "status", "--porcelain");
            Assert.True(File.Exists(marker)); // The harmless helper really works before testing absence.
            File.Delete(marker);

            var start = Start(fixture, root);
            foreach (var argument in new[] { "probe-snapshot", source, revision, descendant ? "sub" : ".", "-" })
                start.ArgumentList.Add(argument);
            if (nestedTemp)
            {
                var temp = Path.Combine(ancestor, "temporary");
                Directory.CreateDirectory(temp);
                start.Environment["TEMP"] = temp;
                start.Environment["TMP"] = temp;
                start.Environment["TMPDIR"] = temp;
            }

            using var proof = JsonDocument.Parse(await Run(start));
            var value = proof.RootElement;
            if (nestedTemp)
            {
                Assert.Equal(Path.Combine(ancestor, "temporary"),
                    Path.GetDirectoryName(Path.GetDirectoryName(value.GetProperty("SnapshotRoot").GetString()!)),
                    ignoreCase: OperatingSystem.IsWindows());
            }
            Assert.False(File.Exists(marker));
            Assert.Equal(new[] { "Root.cs", "sub/Nested.cs" },
                value.GetProperty("Files").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal("source revision", value.GetProperty("Content").GetString());
            Assert.Equal(string.Empty, value.GetProperty("Commit").GetString());
            Assert.Equal(JsonValueKind.Null, value.GetProperty("CommonDirectory").ValueKind);
            Assert.False(value.GetProperty("Listed").GetBoolean());
            Assert.False(value.GetProperty("Diffed").GetBoolean());
            Assert.False(Directory.Exists(value.GetProperty("SnapshotRoot").GetString()));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Ordinary_repository_subdirectory_worktree_and_explicit_override_keep_their_identity()
    {
        var root = Directory.CreateTempSubdirectory("localai-snapshot-controls-").FullName;
        try
        {
            var git = GitExecutableLocator.Resolve(root);
            var repository = Path.Combine(root, "repository");
            var revision = await CreateRepository(git, repository, "source revision");
            Assert.Equal(revision, RepoLocator.GitCommit(repository));
            Assert.Equal(revision, RepoLocator.GitCommit(Path.Combine(repository, "sub")));
            var worktree = Path.Combine(root, "worktree");
            await Git(git, repository, "worktree", "add", "--detach", worktree, revision);
            Assert.Equal(revision, RepoLocator.GitCommit(worktree));
            Assert.Equal(new[] { "Root.cs", "sub/Nested.cs" },
                FileScanner.Enumerate(worktree).Select(path => path.Replace('\\', '/')).ToArray());

            var start = Start(Fixture(), root);
            foreach (var argument in new[] { "probe-snapshot", repository, revision, "sub", Path.Combine(repository, ".git") })
                start.ArgumentList.Add(argument);
            using var proof = JsonDocument.Parse(await Run(start));
            Assert.Equal(revision, proof.RootElement.GetProperty("Commit").GetString());
            Assert.True(proof.RootElement.GetProperty("Listed").GetBoolean());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private static string Fixture()
    {
        var path = typeof(SnapshotGitDiscoveryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SemanticToolProcessFixtureExecutable").Value!;
        if (!OperatingSystem.IsWindows()) path = Path.ChangeExtension(path, null);
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return Path.GetFullPath(path);
    }

    private static async Task<string> CreateRepository(string git, string directory, string content)
    {
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        await Git(git, directory, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(directory, "Root.cs"), content);
        await File.WriteAllTextAsync(Path.Combine(directory, "sub", "Nested.cs"), "nested source");
        await Git(git, directory, "add", "--", "Root.cs", "sub/Nested.cs");
        await Git(git, directory, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "-m", "fixture");
        return (await Git(git, directory, "rev-parse", "HEAD")).Trim();
    }

    private static Task<string> ConfigureMonitor(string git, string directory, string fixture, string marker) =>
        Git(git, directory, "config", "core.fsmonitor",
            $"\"{fixture.Replace('\\', '/')}\" git-fsmonitor \"{marker.Replace('\\', '/')}\"");

    private static Task<string> Git(string git, string directory, params string[] arguments)
    {
        var start = Start(git, directory);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Run(start);
    }

    private static ProcessStartInfo Start(string executable, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, "absent-global-config");
        return start;
    }

    private static async Task<string> Run(ProcessStartInfo start)
    {
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
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
        return await stdout;
    }
}
