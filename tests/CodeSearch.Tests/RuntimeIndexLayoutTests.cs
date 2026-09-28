using LocalAi.Contracts;
using System.Diagnostics;
using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class RuntimeIndexLayoutTests
{
    /// <summary>
    /// The guarantee the runtime-root parameter exists for, asserted rather than assumed: a
    /// caller that names an installation gets every path inside it, and nothing lands in the
    /// machine's own. Without this, an injected path could quietly go back to resolving
    /// %LOCALAPPDATA%\LocalAi and the only symptom would be tests failing when something else
    /// happens to be indexing.
    /// </summary>
    [Fact]
    public void A_named_runtime_root_holds_every_path_that_belongs_to_it()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "codesearch-layout-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-layout-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A {}\r\n");
            Git(repository, "init", "-b", "main");
            Git(repository, "config", "user.email", "tests@local.invalid");
            Git(repository, "config", "user.name", "Local AI Tests");
            Git(repository, "add", "A.cs");
            Git(repository, "commit", "-m", "Initial");

            var named = RuntimeIndexLayout.Inspect(repository, runtimeRoot);
            var machine = RuntimeIndexLayout.Inspect(repository);

            Assert.StartsWith(
                runtimeRoot,
                named.RepositoryRuntimeRoot.Value,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RuntimeIndexLayout.OverlayPath(named, "generation"),
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RepoLocator.LegacyIndexPathFor(repository, runtimeRoot),
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                runtimeRoot,
                RepoLocator.IndexPathFor(repository, runtimeRoot),
                StringComparison.OrdinalIgnoreCase);

            // Same repository, same identity - only the installation differs. Otherwise the
            // parameter would be redirecting more than it claims to.
            Assert.Equal(machine.RepositoryId, named.RepositoryId);
            Assert.Equal(machine.HeadTree, named.HeadTree);
            Assert.StartsWith(
                RuntimeIndexLayout.DefaultRuntimeRoot,
                machine.RepositoryRuntimeRoot.Value,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTree(runtimeRoot);
            DeleteTree(repository);
        }
    }

    /// <summary>
    /// The overlay directory name is a shipped wire format: it is what a released build wrote on
    /// disk, and what retention matches against to decide an overlay is unreachable. Changing
    /// what this produces orphans every overlay on every machine — and, worse, makes the ones
    /// already there look collectable.
    ///
    /// The expected value is read off a real overlay directory an earlier release built, not off
    /// this code.
    /// </summary>
    [Fact]
    public void The_key_of_a_known_worktree_is_the_directory_an_earlier_release_built()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The recorded directory was built by a Windows installation.");

        Assert.Equal(
            "6d16c7330624ccef59b9ed6e17f7c587ee312a1d78501f4926510ecf0ab87a1e",
            RuntimeIndexLayout.WorktreeKey(
                FsPath.From(@"R:\LocalAi")));
    }

    /// <summary>
    /// However the caller spelled it. This is the pairing that deleted live overlays twice: the
    /// writer had the path from Inspect, the reader had it from `git worktree list`, and the two
    /// print different separators for the same directory.
    /// </summary>
    [Theory]
    [InlineData(@"R:\LocalAi")]
    [InlineData("R:/LocalAi")]
    [InlineData(@"R:\LocalAi\")]
    [InlineData(@"r:\LOCALAI")]
    public void Every_spelling_of_one_worktree_gives_one_key(string spelling)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters and case folding are Windows path rules.");

        Assert.Equal(
            "6d16c7330624ccef59b9ed6e17f7c587ee312a1d78501f4926510ecf0ab87a1e",
            RuntimeIndexLayout.WorktreeKey(spelling));
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public void Overlay_path_is_exact_for_generation_tree_and_dirty_hash()
    {
        var identity = Identity(dirtyHash: "dirty", semanticDirtyHash: "semantic");

        var path = RuntimeIndexLayout.OverlayPath(identity, "generation");

        Assert.Contains(Path.Combine("overlays", "generation"), path);
        Assert.Contains(Path.Combine("tree", "dirty.cidx"), path);
    }

    [Fact]
    public void Clean_and_dirty_overlays_never_share_a_path()
    {
        var clean = Identity(dirtyHash: null, semanticDirtyHash: null);
        var dirty = clean with { DirtyHash = "dirty" };

        Assert.NotEqual(
            RuntimeIndexLayout.OverlayPath(clean, "generation"),
            RuntimeIndexLayout.OverlayPath(dirty, "generation"));
    }

    /// <summary>
    /// The two overlays are keyed by two different hashes, and that is the whole of #385. A saved
    /// `.md` changes what the corpus contains and nothing the semantic layer knows, so the vector
    /// overlay has to be rebuilt and the semantic one does not — and it will not be, because it is
    /// still sitting under the name this sync is about to look for.
    /// </summary>
    [Fact]
    public void An_edit_the_semantic_layer_cannot_see_leaves_its_overlay_where_it_was()
    {
        var before = Identity(dirtyHash: "corpus-1", semanticDirtyHash: "semantic-1");
        var afterEditingProse = before with { DirtyHash = "corpus-2" };

        Assert.NotEqual(
            RuntimeIndexLayout.OverlayPath(before, "generation"),
            RuntimeIndexLayout.OverlayPath(afterEditingProse, "generation"));
        Assert.Equal(
            RuntimeIndexLayout.SemanticOverlayPath(before, "generation"),
            RuntimeIndexLayout.SemanticOverlayPath(afterEditingProse, "generation"));
    }

    [Fact]
    public void An_edit_it_can_see_moves_the_semantic_overlay_too()
    {
        var before = Identity(dirtyHash: "corpus-1", semanticDirtyHash: "semantic-1");
        var afterEditingCode = before with
        {
            DirtyHash = "corpus-2",
            SemanticDirtyHash = "semantic-2",
        };

        Assert.NotEqual(
            RuntimeIndexLayout.SemanticOverlayPath(before, "generation"),
            RuntimeIndexLayout.SemanticOverlayPath(afterEditingCode, "generation"));
    }

    /// <summary>
    /// A pristine worktree and one whose only edits are prose share their semantic overlay, and
    /// that sharing is the saving rather than a collision: the two trees differ from the base
    /// generation in exactly the same way as far as the semantic layer is concerned, so one file
    /// is the true answer for both. The corpus overlays beside them still differ, because the
    /// prose is in the corpus.
    ///
    /// The rest of the identity is not shared away: generation, worktree and head tree are all in
    /// the directory above, so this only ever joins two states of one worktree at one commit.
    /// </summary>
    [Fact]
    public void Two_states_that_agree_semantically_share_one_semantic_overlay()
    {
        var pristine = Identity(dirtyHash: null, semanticDirtyHash: null);
        var proseOnly = Identity(dirtyHash: "corpus-1", semanticDirtyHash: null);

        Assert.Equal(
            RuntimeIndexLayout.SemanticOverlayPath(pristine, "generation"),
            RuntimeIndexLayout.SemanticOverlayPath(proseOnly, "generation"));
        Assert.NotEqual(
            RuntimeIndexLayout.OverlayPath(pristine, "generation"),
            RuntimeIndexLayout.OverlayPath(proseOnly, "generation"));
    }

    /// <summary>
    /// The measurement behind #385: editing two files of 715 cost 49.67 seconds, 36.72 of them
    /// opening a solution of 26 projects and running the adapters, where a sync with nothing to do
    /// costs 1.33. This is the property that gets those 36 seconds back for the commonest edit
    /// there is — and it is asserted through real Git rather than through a constructed identity,
    /// because what has to hold is that <c>Inspect</c> draws the line, not that a record can carry
    /// two fields.
    /// </summary>
    [Fact]
    public void Prose_moves_the_corpus_and_leaves_the_semantic_state_where_it_was()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "codesearch-semantic-hash-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-semantic-hash-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A {}\r\n");
            File.WriteAllText(Path.Combine(repository, "README.md"), "# Notes\r\n");
            Git(repository, "init", "-b", "main");
            Git(repository, "config", "user.email", "tests@local.invalid");
            Git(repository, "config", "user.name", "Local AI Tests");
            Git(repository, "add", ".");
            Git(repository, "commit", "-m", "Initial");

            var committed = RuntimeIndexLayout.Inspect(repository, runtimeRoot);
            Assert.Null(committed.DirtyHash);
            Assert.Null(committed.SemanticDirtyHash);

            File.WriteAllText(Path.Combine(repository, "README.md"), "# Notes\r\nAnd more.\r\n");
            var afterProse = RuntimeIndexLayout.Inspect(repository, runtimeRoot);

            // The corpus holds the prose and has to be rebuilt; the semantic layer never read it.
            Assert.NotNull(afterProse.DirtyHash);
            Assert.Null(afterProse.SemanticDirtyHash);

            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A { void B() {} }\r\n");
            var afterCode = RuntimeIndexLayout.Inspect(repository, runtimeRoot);

            Assert.NotNull(afterCode.SemanticDirtyHash);
            Assert.NotEqual(afterProse.DirtyHash, afterCode.DirtyHash);
        }
        finally
        {
            DeleteTree(repository);
        }
    }

    /// <summary>
    /// And the semantic hash answers for the semantic files only — editing prose again after the
    /// code has changed must leave it exactly where the code left it, or the phase runs a second
    /// time for a `.md`.
    /// </summary>
    [Fact]
    public void Prose_on_top_of_a_code_change_still_moves_nothing_semantic()
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "codesearch-semantic-hash-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-semantic-hash-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        try
        {
            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A {}\r\n");
            File.WriteAllText(Path.Combine(repository, "README.md"), "# Notes\r\n");
            Git(repository, "init", "-b", "main");
            Git(repository, "config", "user.email", "tests@local.invalid");
            Git(repository, "config", "user.name", "Local AI Tests");
            Git(repository, "add", ".");
            Git(repository, "commit", "-m", "Initial");

            File.WriteAllText(Path.Combine(repository, "A.cs"), "class A { void B() {} }\r\n");
            var afterCode = RuntimeIndexLayout.Inspect(repository, runtimeRoot);

            File.WriteAllText(Path.Combine(repository, "README.md"), "# Notes\r\nAnd more.\r\n");
            var afterBoth = RuntimeIndexLayout.Inspect(repository, runtimeRoot);

            Assert.Equal(afterCode.SemanticDirtyHash, afterBoth.SemanticDirtyHash);
            Assert.NotEqual(afterCode.DirtyHash, afterBoth.DirtyHash);
        }
        finally
        {
            DeleteTree(repository);
        }
    }

    private static WorkingIndexIdentity Identity(
        string? dirtyHash,
        string? semanticDirtyHash) =>
        new(
            FsPath.From(@"C:\repo-worktree"),
            FsPath.From(@"C:\repo"),
            "repository",
            FsPath.From(@"C:\runtime\repository"),
            "commit",
            "tree",
            dirtyHash,
            semanticDirtyHash);
}
