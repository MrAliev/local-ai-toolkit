using System.Diagnostics;
using CodeSearch.Core.Indexing;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A generation is immutable and checksum-validated, so a `base.cidx` whose bytes no longer
/// match its manifest makes `ReadManifest` throw `InvalidDataException` — and the sync catches
/// only `DirectoryNotFoundException` and `FileNotFoundException` (#390).
///
/// It surfaces through `Program.cs`'s last-resort handler as `unexpected_failure`, which is the
/// one thing it is not: the state is recoverable by rebuilding the generation. Every sync after
/// it takes the same path — the CLI, the post-commit hook, `index_refresh` — so the repository
/// stops indexing for good, and nothing anywhere says the remedy is to delete a directory.
///
/// This aims at the copy nobody guarded at all: the manifest of the *current* generation, read
/// inside `BuildGenerationAsync` to decide whether its corpus can be reused. That read is outside
/// the try entirely, and it happens before any embedding, so the test needs no backend.
/// </summary>
public sealed class ACorruptGenerationDoesNotJamSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-corrupt-generation-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-corrupt-generation-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_truncated_base_index_does_not_end_the_sync_as_an_unexpected_failure()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "LocalAi Tests");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello\n");
        Git("add", ".");
        Git("commit", "-m", "first");

        var store = new GenerationStore(FsPath.From(RepositoryRuntimeRoot()));
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var manifest = store.PublishIndex(source, Identity());
        store.SetCurrent(manifest);

        // The directory and the manifest are intact, so neither caught exception fires, and only
        // the checksum says anything is wrong.
        File.WriteAllText(store.IndexPath(manifest.Identity.Id), "TRUNCATED");

        var error = await Record.ExceptionAsync(
            () => CodeSearchSyncCommand.ExecuteAsync(
                _root,
                TextWriter.Null,
                cancellationToken: TestContext.Current.CancellationToken,
                runtimeRoot: _runtimeRoot));

        // It may still fail — there is no embedding backend here — but not on the damaged
        // generation, and never as the corruption escaping unhandled.
        Assert.IsNotType<InvalidDataException>(error);
    }

    private string RepositoryRuntimeRoot()
    {
        var commonDirectory = Git("rev-parse", "--path-format=absolute", "--git-common-dir");
        return Path.Combine(
            _runtimeRoot,
            "repositories",
            RepositoryIdentity.FromCommonDirectory(commonDirectory).Id);
    }

    private static GenerationIdentity Identity() => new(
        "repository",
        "commit",
        "tree",
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        CodeIndex.CurrentVersion,
        1,
        0);

    private string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        if (Directory.Exists(_root))
        {
            // Git leaves its object files read-only, and a recursive delete refuses them.
            foreach (var file in Directory.EnumerateFiles(
                         _root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
