using System.Diagnostics;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `SemanticNavigationGateway.LoadCurrent` guarded the pointer read above it and, in #422, the two
/// index loads below it — and left `store.ReadManifest` between them outside every try. Everything
/// that call refuses with left the gateway as a raw exception, because the gateway catches only
/// `SemanticNavigationNotReadyException`: no degraded answer, no heuristic locations, no sentence
/// naming what to do.
///
/// The product's own repair path produces the state. `CodeSearchSyncCommand` calls
/// `QuarantineUnreadable`, which moves the generation directory into `staging` — and when HEAD has
/// not moved that is the id `current.json` names. The pointer is not rewritten until the rebuild
/// finishes, so for the length of a rebuild every navigation call throws.
/// </summary>
public sealed class NavigationDegradesWhenTheGenerationIsGoneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-generation-gone-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "localai-generation-gone-runtime-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_generation_the_pointer_names_but_the_disk_lacks_degrades_rather_than_throws()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main", ".");
        Git("config", "user.email", "localai-tests@example.invalid");
        Git("config", "user.name", "Local AI Tests");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello\n");
        Git("add", ".");
        Git("commit", "-m", "first");

        var generationId = PublishThenRemoveTheGeneration();

        var gateway = new SemanticNavigationGateway(runtimeRoot: _runtimeRoot);

        var error = Assert.Throws<SemanticNavigationNotReadyException>(
            () => gateway.GoToDefinition("notes.md", line: 1, utf16Column: 1, root: _root));

        Assert.Equal(
            IndexText.GenerationUnreadableFact(generationId),
            error.Degradation.Fact,
            StringComparer.Ordinal);

        // The command stays in the remedy half: this is the one pair whose commonest occurrence is
        // while a build holds the sync gate, so the fact is the half that usually prints alone.
        Assert.NotNull(error.Degradation.Remedy);
        Assert.DoesNotContain("localai sync", error.Degradation.Fact, StringComparison.Ordinal);
    }

    /// <summary>
    /// Publishes a generation, points at it, then takes the directory away — which is what
    /// `QuarantineUnreadable` does to the generation the pointer names.
    /// </summary>
    private string PublishThenRemoveTheGeneration()
    {
        var repositoryRuntimeRoot = Path.Combine(
            _runtimeRoot,
            "repositories",
            RepositoryIdentity.FromCommonDirectory(
                Git("rev-parse", "--path-format=absolute", "--git-common-dir")).Id);
        var store = new GenerationStore(FsPath.From(repositoryRuntimeRoot));

        var corpus = Path.Combine(_root, "source.cidx");
        File.WriteAllText(corpus, "INDEX");
        var identity = new GenerationIdentity(
            "repository",
            "commit",
            "tree",
            "qwen3-embedding:8b-q8_0",
            4096,
            1,
            CodeIndex.CurrentVersion,
            1,
            1);
        var manifest = store.PublishIndex(corpus, identity);
        store.SetCurrent(manifest);

        Directory.Delete(
            Path.Combine(repositoryRuntimeRoot, "generations", manifest.Identity.Id),
            recursive: true);
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
