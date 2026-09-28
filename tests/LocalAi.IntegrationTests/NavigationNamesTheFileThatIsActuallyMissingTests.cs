using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The behavioural half of #409, through the gateway rather than through the resource file.
///
/// A published generation whose semantic index reads perfectly, and a worktree whose head tree is
/// not the one that generation was built from — so navigation needs an overlay, and none has been
/// built. That is the ordinary state of any repository between syncs, and it was announced as the
/// generation's semantic index being unreadable: a different file, at a different path, that had
/// just loaded successfully one line earlier.
/// </summary>
public sealed class NavigationNamesTheFileThatIsActuallyMissingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-overlay-absent-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-overlay-absent-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_overlay_that_was_never_built_is_not_reported_as_a_corrupt_generation()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "Local AI Tests");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello\n");
        Git("add", ".");
        Git("commit", "-m", "first");

        var generationId = PublishAGenerationBuiltFromAnotherTree();

        var gateway = new SemanticNavigationGateway(runtimeRoot: _runtimeRoot);

        var error = Assert.Throws<SemanticNavigationNotReadyException>(
            () => gateway.GoToDefinition("notes.md", line: 1, utf16Column: 1, root: _root));

        Assert.Equal(
            IndexText.SemanticOverlayNotBuiltFact(generationId),
            error.Degradation.Fact,
            StringComparer.Ordinal);

        // The command stays in the remedy half. The MCP tools print the fact alone while a build
        // holds the sync gate, because a second `localai sync` exits 75 sync_busy (#383).
        Assert.NotNull(error.Degradation.Remedy);
        Assert.Contains("localai sync", error.Degradation.Remedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Its semantic index is real and readable — the point of the test is that the sentence used
    /// to blame it. Its DevTree is deliberately not this worktree's head tree, which is what makes
    /// the gateway reach for an overlay.
    /// </summary>
    private string PublishAGenerationBuiltFromAnotherTree()
    {
        var repositoryRuntimeRoot = Path.Combine(
            _runtimeRoot,
            "repositories",
            RepositoryIdentity.FromCommonDirectory(
                Git("rev-parse", "--path-format=absolute", "--git-common-dir")).Id);
        var store = new GenerationStore(FsPath.From(repositoryRuntimeRoot));

        var corpus = Path.Combine(_root, "source.cidx");
        File.WriteAllText(corpus, "INDEX");
        var semantic = Path.Combine(_root, "source.sidx");
        // SemanticIndexVersion is set through `with` rather than positionally: the nine
        // positional parameters stop at RankingVersion, and PublishIndex refuses a semantic index
        // whose identity says the generation carries none.
        var identity = new GenerationIdentity(
            "repository",
            "commit",
            "a-tree-this-worktree-is-not-on",
            "qwen3-embedding:8b-q8_0",
            4096,
            1,
            CodeIndex.CurrentVersion,
            1,
            1) with
        {
            SemanticIndexVersion = 1,
        };
        new SemanticIndex
        {
            RepositoryId = identity.RepositoryId,
            GenerationId = identity.Id,
            GitTree = identity.DevTree,
            DirtyHash = null,
            BaseCommit = identity.DevCommit,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        }.Save(semantic);

        var manifest = store.PublishIndex(corpus, identity, sourceSemanticIndexPath: semantic);
        store.SetCurrent(manifest);
        return manifest.Identity.Id;
    }

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
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
