using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;
using LocalAi.Repository;

namespace CodeSearch.Core.Semantics;

public sealed record SemanticNavigationContext(
    SemanticNavigationService Service,
    SemanticSnapshotIdentity Snapshot);

/// <summary>
/// Why navigation could not answer precisely, in words meant for the reader.
///
/// The inner exception is the one this replaced in the sentence. None of the twenty ways a
/// semantic index can be unreadable changes what the reader does — the answer is a re-sync in all
/// of them — and "Semantic index ended inside a document hash." is not a sentence to put in front
/// of anybody. It is kept here because a debugger still wants it.
/// </summary>
public sealed class SemanticNavigationNotReadyException : InvalidOperationException
{
    public SemanticNavigationNotReadyException(string message)
        : this(new SemanticDegradation(message, null))
    {
    }

    public SemanticNavigationNotReadyException(string message, Exception innerException)
        : this(new SemanticDegradation(message, null), innerException)
    {
    }

    /// <summary>
    /// The two halves kept apart, so a caller that must not print the command can drop it. The
    /// message stays the whole sentence, because that is what every face printing
    /// <c>Message</c> has always printed.
    /// </summary>
    public SemanticNavigationNotReadyException(
        SemanticDegradation degradation,
        Exception? innerException = null)
        : base(degradation.Sentence, innerException)
    {
        Degradation = degradation;
    }

    public SemanticDegradation Degradation { get; }
}

/// <summary>
/// Why an answer is not the precise one, in the two halves a caller may need apart: what is true,
/// and the command that repairs it where there is one.
///
/// One reason has no remedy — the index loaded and answered nothing at that position — and it is
/// the reason that needs none: nothing about it changes while a build runs. The other three end in
/// <c>localai sync</c>, which is refused while a build holds the repository's gate, so the caller
/// that knows about the gate prints the fact alone and names the running build instead.
/// </summary>
public sealed record SemanticDegradation(string Fact, string? Remedy)
{
    /// <summary>The sentence every face has always printed.</summary>
    public string Sentence => Remedy is null ? Fact : Fact + " " + Remedy;

    public override string ToString() => Sentence;
}

/// <summary>
/// A navigation answer together with the reason it is not the precise one, when it is not.
///
/// The gateway already knew why: a generation published before semantic indexing existed has no
/// <c>semantic.sidx</c>, and loading it raises an exception naming the generation and saying to
/// rebuild it. That exception was then swallowed to reach the text fallback, so a repository that
/// had simply not been re-synced answered <c>find_references</c> with README and docs matches and
/// no indication that anything was missing. Every hit was honestly tagged Heuristic; nothing said
/// why, or what to do about it.
/// </summary>
public sealed record SemanticNavigationOutcome(
    IReadOnlyList<SemanticLocation> Locations,
    SemanticDegradation? Degradation)
{
    public static SemanticNavigationOutcome Precise(
        IReadOnlyList<SemanticLocation> locations) =>
        new(locations, null);
}

/// <summary>Loads the exact current semantic generation and executes snapshot-bound queries.</summary>
public sealed class SemanticNavigationGateway
{
    private readonly Func<string?, SemanticNavigationContext> _contextFactory;
    private readonly ILiveSemanticNavigation? _liveNavigation;
    private readonly IHeuristicSemanticNavigation? _heuristicNavigation;

    /// <param name="runtimeRoot">
    /// The installation whose published generation is loaded. Null means the machine's own, which
    /// is the only value production uses; a caller names one to read a generation that is not the
    /// current user's. Ignored when <paramref name="contextFactory"/> is supplied, because the
    /// factory has then already decided where the context comes from.
    /// </param>
    public SemanticNavigationGateway(
        Func<string?, SemanticNavigationContext>? contextFactory = null,
        ILiveSemanticNavigation? liveNavigation = null,
        IHeuristicSemanticNavigation? heuristicNavigation = null,
        string? runtimeRoot = null)
    {
        _contextFactory = contextFactory ?? (root => LoadCurrent(root, runtimeRoot));
        _liveNavigation = liveNavigation;
        _heuristicNavigation = heuristicNavigation;
    }

    public IReadOnlyList<SemanticLocation> GoToDefinition(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null)
    {
        return GoToDefinitionAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<SemanticLocation>> GoToDefinitionAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default) =>
        (await ResolveDefinitionAsync(
            documentPath,
            line,
            utf16Column,
            root,
            cancellationToken)).Locations;

    public SemanticNavigationOutcome ResolveDefinition(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null) =>
        ResolveDefinitionAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();

    public async Task<SemanticNavigationOutcome> ResolveDefinitionAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var workingRoot = ResolveLiveRoot(root);
            var live = await _liveNavigation.GoToDefinitionAsync(
                workingRoot,
                documentPath,
                line,
                utf16Column,
                cancellationToken);
            if (live.Handled)
            {
                return SemanticNavigationOutcome.Precise(live.Locations);
            }
        }

        SemanticDegradation degradation;
        try
        {
            var context = _contextFactory(root);
            var precise = context.Service.GoToDefinition(
                documentPath,
                line,
                utf16Column,
                context.Snapshot);
            if (precise.Count > 0 || _heuristicNavigation is null)
            {
                return SemanticNavigationOutcome.Precise(precise);
            }

            degradation = EmptyPreciseResult;
        }
        catch (SemanticNavigationNotReadyException exception) when (
            _heuristicNavigation is not null)
        {
            degradation = exception.Degradation;
        }

        return new SemanticNavigationOutcome(
            await _heuristicNavigation!.GoToDefinitionAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                cancellationToken),
            degradation);
    }

    public IReadOnlyList<SemanticLocation> FindReferences(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null)
    {
        return FindReferencesAsync(
            documentPath,
            line,
            utf16Column,
            includeDefinition,
            root).GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<SemanticLocation>> FindReferencesAsync(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null,
        CancellationToken cancellationToken = default) =>
        (await ResolveReferencesAsync(
            documentPath,
            line,
            utf16Column,
            includeDefinition,
            root,
            cancellationToken)).Locations;

    public SemanticNavigationOutcome ResolveReferences(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null) =>
        ResolveReferencesAsync(documentPath, line, utf16Column, includeDefinition, root)
            .GetAwaiter().GetResult();

    public async Task<SemanticNavigationOutcome> ResolveReferencesAsync(
        string documentPath,
        int line,
        int utf16Column,
        bool includeDefinition,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var workingRoot = ResolveLiveRoot(root);
            var live = await _liveNavigation.FindReferencesAsync(
                workingRoot,
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                cancellationToken);
            if (live.Handled)
            {
                return SemanticNavigationOutcome.Precise(live.Locations);
            }
        }

        SemanticDegradation degradation;
        try
        {
            var context = _contextFactory(root);
            var precise = context.Service.FindReferences(
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                context.Snapshot);
            if (precise.Count > 0 || _heuristicNavigation is null)
            {
                return SemanticNavigationOutcome.Precise(precise);
            }

            degradation = EmptyPreciseResult;
        }
        catch (SemanticNavigationNotReadyException exception) when (
            _heuristicNavigation is not null)
        {
            degradation = exception.Degradation;
        }

        return new SemanticNavigationOutcome(
            await _heuristicNavigation!.FindReferencesAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                includeDefinition,
                cancellationToken),
            degradation);
    }

    public IReadOnlyList<SemanticLocation> FindImplementations(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null) =>
        FindImplementationsAsync(documentPath, line, utf16Column, root)
            .GetAwaiter().GetResult();

    public async Task<IReadOnlyList<SemanticLocation>> FindImplementationsAsync(
        string documentPath,
        int line,
        int utf16Column,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (_liveNavigation is not null)
        {
            var live = await _liveNavigation.FindImplementationsAsync(
                ResolveLiveRoot(root),
                documentPath,
                line,
                utf16Column,
                cancellationToken);
            if (live.Handled)
            {
                return live.Locations;
            }
        }

        var context = _contextFactory(root);
        return context.Service.FindImplementations(
            documentPath,
            line,
            utf16Column,
            context.Snapshot);
    }

    public IReadOnlyList<SemanticRelatedLocation> FindRelationships(
        string documentPath,
        int line,
        int utf16Column,
        SemanticRelationshipDirection direction,
        SemanticRelationshipKind? kind = null,
        string? root = null)
    {
        var context = _contextFactory(root);
        return context.Service.FindRelationships(
            documentPath,
            line,
            utf16Column,
            direction,
            kind,
            context.Snapshot);
    }

    /// <summary>
    /// The other way a caller ends up on text matches: the semantic index is present and current,
    /// and simply has nothing at that position. Worth distinguishing from a missing index, because
    /// only one of the two is fixed by re-syncing.
    /// </summary>
    /// <summary>
    /// The one reason with no command in it: the index loaded and answered nothing here.
    /// Nothing about it changes while a build runs, which is why it keeps the shape it has.
    /// </summary>
    private static SemanticDegradation EmptyPreciseResult =>
        new(IndexText.NoSymbolAtPosition, null);

    private static string ResolveLiveRoot(string? root) =>
        root is null
            ? RepoLocator.ResolveWorkingRoot(null).Value
            : Path.GetFullPath(root);

    private static SemanticNavigationContext LoadCurrent(string? root, string? runtimeRoot)
    {
        var workingRoot = RepoLocator.ResolveWorkingRoot(root).Value;
        var identity = RuntimeIndexLayout.Inspect(workingRoot, runtimeRoot);
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        var current = store.ReadCurrent()
            ?? throw new SemanticNavigationNotReadyException(new SemanticDegradation(
                IndexText.GenerationNotPublishedFact,
                IndexText.GenerationNotPublishedRemedy(workingRoot)));
        var manifest = store.ReadManifest(current.GenerationId);
        if (manifest.SemanticIndexFile is null)
        {
            throw new SemanticNavigationNotReadyException(new SemanticDegradation(
                IndexText.GenerationWithoutSemanticIndexFact(current.GenerationId),
                IndexText.GenerationWithoutSemanticIndexRemedy(workingRoot)));
        }

        var usesBaseSnapshot =
            string.Equals(identity.HeadTree, manifest.Identity.DevTree, StringComparison.Ordinal) &&
            identity.DirtyHash is null;
        SemanticIndex semanticIndex;
        try
        {
            var baseIndex = SemanticIndex.Load(store.SemanticIndexPath(current.GenerationId));
            semanticIndex = usesBaseSnapshot
                ? baseIndex
                : SemanticIndexOverlay.Load(
                    RuntimeIndexLayout.SemanticOverlayPath(identity, current.GenerationId))
                    .Materialize(baseIndex);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new SemanticNavigationNotReadyException(
                new SemanticDegradation(
                    IndexText.SemanticIndexUnreadableFact(current.GenerationId),
                    IndexText.SemanticIndexUnreadableRemedy(workingRoot)),
                exception);
        }

        return new SemanticNavigationContext(
            new SemanticNavigationService(semanticIndex),
            new SemanticSnapshotIdentity(
                identity.RepositoryId,
                current.GenerationId,
                identity.HeadTree,
                identity.DirtyHash));
    }
}
