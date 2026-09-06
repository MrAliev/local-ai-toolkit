using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A heuristic navigation answer, and the build that explains it.
///
/// The line above such an answer names one of four reasons, and three of them end in
/// `localai sync --root …`. While a build holds the repository's sync gate that command exits 75
/// sync_busy - so on a machine building its first index, the tools that are supposed to be usable
/// during a build spend the whole build naming the one command that cannot run.
///
/// The fourth reason - no symbol at that position - names no command. It is a fact about a
/// position, it is true whatever the gate is doing, and nothing about it changes here. That is why
/// the shape follows the reason rather than the tool.
/// </summary>
public sealed class DegradedNavigationAndARunningBuildTests : IDisposable
{
    private const string Repository = "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966a";
    private const string Generation = "ff114b066164";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-degraded-" + Guid.NewGuid().ToString("N"));

    public DegradedNavigationAndARunningBuildTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// The defect. The reason is true and worth printing; the command at the end of it is the one
    /// thing the reader must not do, and it is the only part of the answer that tells them to act.
    /// </summary>
    [Fact]
    public void A_reason_that_names_a_sync_does_not_name_it_while_one_is_running()
    {
        var answer = CodeSearchTools.GoToDefinition(
            WithoutASemanticIndex(),
            Watching(Running()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains("semantic_navigation_degraded:", answer, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.BuildAlreadyRunning, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("localai sync --root", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it stops claiming something that was never true on this path. `GenerationNotPublished`
    /// means no generation has ever been published, and the sentence above the answer said
    /// "answers come from the last index it published" - there is no last index.
    /// </summary>
    [Fact]
    public void A_reason_that_names_a_sync_does_not_promise_a_published_index()
    {
        var answer = CodeSearchTools.FindReferences(
            WithNoGenerationAtAll(),
            Watching(Running()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.SyncNotSearchableYet,
            answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason with no command in it is untouched: it is a fact about a position, it is true
    /// whatever the gate is doing, and the answer under it really does come from the index that
    /// was published.
    /// </summary>
    [Fact]
    public void A_reason_that_names_no_command_keeps_the_line_it_has()
    {
        var answer = CodeSearchTools.GoToDefinition(
            WithNothingAtThePosition(),
            Watching(Running()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains(CodeSearchText.SyncNotSearchableYet, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.BuildAlreadyRunning,
            answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// With nothing running the sentence is the one this product has always printed, command and
    /// all. That is the whole of what keeps this a fix rather than a rewrite.
    /// </summary>
    [Fact]
    public void With_nothing_running_the_reason_is_the_sentence_it_has_always_been()
    {
        var answer = CodeSearchTools.GoToDefinition(
            WithoutASemanticIndex(),
            NoBuildRunning.Notice,
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains(
            IndexText.GenerationWithoutSemanticIndex(Generation, _root),
            answer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Sync:", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sync that died leaves the command in place - the gate is open and it will work - and adds
    /// where it stopped, because a reader about to run it needs to know the last attempt is dead
    /// rather than still working.
    /// </summary>
    [Fact]
    public void A_build_that_died_leaves_the_command_and_says_where_it_stopped()
    {
        var answer = CodeSearchTools.GoToDefinition(
            WithoutASemanticIndex(),
            Watching(Died()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains("localai sync --root", answer, StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.ProgressChunks(3182, 26480, 23298),
            answer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ETA", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one place in this product where the block is not the first thing in the answer. It is
    /// not a header here - it is the completion of a sentence whose command was taken away, so it
    /// goes on the line under it, and the blank line the answer already had stays where it was.
    /// </summary>
    [Fact]
    public void The_block_follows_the_reason_rather_than_heading_the_answer()
    {
        var answer = CodeSearchTools.GoToDefinition(
            WithoutASemanticIndex(),
            Watching(Running()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.StartsWith("semantic_navigation_degraded: ", answer, StringComparison.Ordinal);
        var lines = answer.Split('\n');
        Assert.StartsWith("Sync: ", lines[1], StringComparison.Ordinal);
        Assert.Equal(CodeSearchText.BuildAlreadyRunning, lines[2]);
        Assert.Equal(string.Empty, lines[3]);
    }

    /// <summary>
    /// The same defect two tools over. find_implementations and find_relationships have no text
    /// fallback, so the same three sentences reach the reader as a refusal rather than as a
    /// caveat - and a refusal whose only route is a command that cannot run is worse, not better.
    /// </summary>
    [Fact]
    public void A_refusal_that_names_a_sync_does_not_name_it_while_one_is_running()
    {
        var answer = CodeSearchTools.FindImplementations(
            WithoutASemanticIndex(),
            Watching(Running()),
            "Use.cs",
            line: 1,
            root: _root);

        Assert.Contains("semantic_navigation_not_ready:", answer, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.BuildAlreadyRunning, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("localai sync --root", answer, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A generation published before semantic indexing existed: a reason with a command.
    ///
    /// Built from the two halves the gateway builds it from, not from the composed sentence — a
    /// fixture that composes it differently hides which half a caller is allowed to drop, which is
    /// the whole of what these tests are about.
    /// </summary>
    private SemanticNavigationGateway WithoutASemanticIndex() =>
        new(
            _ => throw new SemanticNavigationNotReadyException(new SemanticDegradation(
                IndexText.GenerationWithoutSemanticIndexFact(Generation),
                IndexText.GenerationWithoutSemanticIndexRemedy(_root))),
            heuristicNavigation: new OneHeuristicLocation());

    /// <summary>A repository nothing has ever been published for: also a reason with a command.</summary>
    private SemanticNavigationGateway WithNoGenerationAtAll() =>
        new(
            _ => throw new SemanticNavigationNotReadyException(new SemanticDegradation(
                IndexText.GenerationNotPublishedFact,
                IndexText.GenerationNotPublishedRemedy(_root))),
            heuristicNavigation: new OneHeuristicLocation());

    /// <summary>An index that loaded and answered nothing here: a reason with no command.</summary>
    private static SemanticNavigationGateway WithNothingAtThePosition()
    {
        var index = new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents =
            [
                new SemanticDocument { RelPath = "Use.cs", Hash = new byte[32] },
            ],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };
        return new SemanticNavigationGateway(
            _ => new SemanticNavigationContext(
                new SemanticNavigationService(index),
                new SemanticSnapshotIdentity("repository", "generation", "tree", null)),
            heuristicNavigation: new OneHeuristicLocation());
    }

    private static SyncNotice Watching(SyncSighting sighting) => new(_ => sighting);

    private static SyncSighting Running() =>
        new(Repository, BuildHoldsTheGate: true, Progress(DateTimeOffset.UtcNow));

    private static SyncSighting Died() =>
        new(
            Repository,
            BuildHoldsTheGate: false,
            Progress(DateTimeOffset.UtcNow - TimeSpan.FromHours(3.2)));

    private static RepositoryIndexProgress Progress(DateTimeOffset updatedAtUtc) =>
        new(
            Repository,
            RepositoryIndexProgressPhase.EmbeddingBase,
            "R:\\IntelWash",
            3182,
            26480,
            10.2,
            TimeSpan.FromMinutes(38.1),
            updatedAtUtc);

    private sealed class OneHeuristicLocation : IHeuristicSemanticNavigation
    {
        private static readonly SemanticLocation Location = new(
            "README.md",
            new SourceRange(0, 0, 0, 3),
            "heuristic",
            SemanticOccurrenceRoles.Reference,
            NavigationPrecision.Heuristic);

        public Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
            string repositoryRoot,
            string documentPath,
            int line,
            int utf16Column,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SemanticLocation>>([Location]);

        public Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
            string repositoryRoot,
            string documentPath,
            int line,
            int utf16Column,
            bool includeDefinition,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SemanticLocation>>([Location]);
    }
}
