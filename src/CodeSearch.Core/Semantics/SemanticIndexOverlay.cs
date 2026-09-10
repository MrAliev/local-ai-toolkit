using System.Text;
using CodeSearch.Core.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// Snapshot-bound replacement slices for documents whose text or resolved semantics changed.
/// Deleted documents are represented explicitly as tombstones.
/// </summary>
public sealed record SemanticIndexOverlay
{
    public const int CurrentVersion = 1;

    private const string Magic = "SOVL";
    private const int MaximumTombstones = 10_000_000;

    public required string RepositoryId { get; init; }
    public required string GenerationId { get; init; }
    public required string BaseGitTree { get; init; }
    public required string GitTree { get; init; }
    public string? DirtyHash { get; init; }
    public required List<string> DeletedDocuments { get; init; }
    public required SemanticIndex Changed { get; init; }

    public static SemanticIndexOverlay Create(
        SemanticIndex baseIndex,
        SemanticIndex currentIndex,
        IEnumerable<string> dirtyPaths)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(currentIndex);
        ArgumentNullException.ThrowIfNull(dirtyPaths);
        if (!string.Equals(baseIndex.RepositoryId, currentIndex.RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GenerationId, currentIndex.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic overlay indexes belong to different generations.");
        }

        var baseDocuments = baseIndex.Documents.ToDictionary(
            document => NormalizePath(document.RelPath), StringComparer.Ordinal);
        var currentDocuments = currentIndex.Documents.ToDictionary(
            document => NormalizePath(document.RelPath), StringComparer.Ordinal);
        var allPaths = baseDocuments.Keys.Concat(currentDocuments.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var affected = dirtyPaths.Select(NormalizePath)
            .Where(allPaths.Contains)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in allPaths)
        {
            if (!SameDocument(baseDocuments.GetValueOrDefault(path), currentDocuments.GetValueOrDefault(path)) ||
                !SameOccurrences(baseIndex, currentIndex, path))
            {
                affected.Add(path);
            }
        }

        return From(baseIndex, currentIndex, baseDocuments, currentDocuments, affected);
    }

    /// <summary>
    /// An overlay from a rebuild that only looked at part of the tree.
    ///
    /// <see cref="Create"/> works out what changed by diffing the base against a current
    /// index of everything, and reads a document present in the base but absent from the
    /// current one as deleted. Hand that an index built from three projects out of four
    /// hundred and it tombstones the rest of the repository.
    ///
    /// So this is told which paths were recomputed rather than inferring it. Everything
    /// outside that set is left exactly as the base has it -- sound because a file the rebuild
    /// did not look at cannot bind differently unless something it references changed, and
    /// deciding that is the caller's job before it gets here (#519).
    /// </summary>
    public static SemanticIndexOverlay CreateScoped(
        SemanticIndex baseIndex,
        SemanticIndex recomputedIndex,
        IEnumerable<string> recomputedPaths)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(recomputedIndex);
        ArgumentNullException.ThrowIfNull(recomputedPaths);
        if (!string.Equals(
                baseIndex.RepositoryId,
                recomputedIndex.RepositoryId,
                StringComparison.Ordinal) ||
            !string.Equals(
                baseIndex.GenerationId,
                recomputedIndex.GenerationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Semantic overlay indexes belong to different generations.");
        }

        return From(
            baseIndex,
            recomputedIndex,
            baseIndex.Documents.ToDictionary(
                document => NormalizePath(document.RelPath), StringComparer.Ordinal),
            recomputedIndex.Documents.ToDictionary(
                document => NormalizePath(document.RelPath), StringComparer.Ordinal),
            recomputedPaths.Select(NormalizePath).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// A save, laid over the overlay of the commit it was made against.
    ///
    /// A worktree's overlay says two things at once: what the branch committed since the base
    /// generation, and what is saved but not committed. Saving a file moves only the second, so
    /// the first is taken from the overlay that already holds it -- the `clean` overlay for the
    /// same commit -- and only the saved files are recomputed. That is what the corpus layer has
    /// done since #388, and why one saved file embedded one chunk while the semantic layer
    /// reparsed the branch's other hundred (#519).
    ///
    /// <paramref name="committed"/> is the committed overlay and nothing else. Layering on
    /// another dirty overlay would be layering on a state whose files are no longer on disk:
    /// nothing could then say whether one of them changed a declaration, and if one had, every
    /// record carried across would be stale in a way no later sync notices.
    /// </summary>
    /// <param name="recomputedPaths">
    /// What the rebuild spoke for, including a file it found deleted -- which produces no document
    /// and would otherwise go on being answered from the committed layer.
    /// </param>
    public static SemanticIndexOverlay Layer(
        SemanticIndexOverlay committed,
        SemanticIndex baseIndex,
        SemanticIndex recomputedIndex,
        IEnumerable<string> recomputedPaths)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(baseIndex);
        ArgumentNullException.ThrowIfNull(recomputedIndex);
        ArgumentNullException.ThrowIfNull(recomputedPaths);
        if (committed.DirtyHash is not null)
        {
            throw new InvalidDataException(
                "A semantic overlay is layered on the overlay of its commit, not on another.");
        }

        if (!string.Equals(
                committed.RepositoryId,
                recomputedIndex.RepositoryId,
                StringComparison.Ordinal) ||
            !string.Equals(
                committed.GenerationId,
                recomputedIndex.GenerationId,
                StringComparison.Ordinal) ||
            !string.Equals(committed.GitTree, recomputedIndex.GitTree, StringComparison.Ordinal) ||
            !string.Equals(committed.BaseGitTree, baseIndex.GitTree, StringComparison.Ordinal) ||
            !string.Equals(
                baseIndex.GenerationId,
                recomputedIndex.GenerationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Semantic overlay layers do not describe the same tree.");
        }

        var replaced = recomputedPaths.Select(NormalizePath)
            .Concat(recomputedIndex.Documents.Select(document => NormalizePath(document.RelPath)))
            .ToHashSet(StringComparer.Ordinal);
        var merged = Merge(committed.Changed, baseIndex, recomputedIndex, replaced);
        var affected = replaced
            .Concat(committed.DeletedDocuments.Select(NormalizePath))
            .Concat(committed.Changed.Documents.Select(document => NormalizePath(document.RelPath)))
            .ToHashSet(StringComparer.Ordinal);
        return From(
            baseIndex,
            merged,
            baseIndex.Documents.ToDictionary(
                document => NormalizePath(document.RelPath), StringComparer.Ordinal),
            merged.Documents.ToDictionary(
                document => NormalizePath(document.RelPath), StringComparer.Ordinal),
            affected);
    }

    /// <summary>
    /// The committed layer with the recomputed files' records swapped in.
    ///
    /// Relationships are held against the symbol they were read from -- the indexer walks a
    /// declaration and writes down what it implements or overrides -- so the committed layer's
    /// relationships survive except where their source is declared in a file this rebuild looked
    /// at again. Keeping those would keep an interface a save has just removed.
    ///
    /// Where a symbol is declared is a fact about the whole index and not about the layer, which
    /// is why the base is consulted as well. The indexer writes a relationship down for every
    /// symbol it meets, references included, so the committed layer holds `Bar -> IService`
    /// merely because a file the branch changed *mentions* `Bar`, with no declaration of `Bar`
    /// anywhere in that layer -- and asking the layer alone would find nothing to drop.
    /// </summary>
    private static SemanticIndex Merge(
        SemanticIndex committed,
        SemanticIndex baseIndex,
        SemanticIndex recomputed,
        HashSet<string> replaced)
    {
        var recomputedSources = baseIndex.Occurrences
            .Concat(committed.Occurrences)
            .Concat(recomputed.Occurrences)
            .Where(occurrence =>
                occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
                replaced.Contains(NormalizePath(occurrence.DocumentPath)))
            .Select(occurrence => occurrence.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var symbols = committed.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        foreach (var symbol in recomputed.Symbols)
        {
            symbols[symbol.Id] = symbol;
        }

        return new SemanticIndex
        {
            RepositoryId = recomputed.RepositoryId,
            GenerationId = recomputed.GenerationId,
            GitTree = recomputed.GitTree,
            DirtyHash = recomputed.DirtyHash,
            BaseCommit = recomputed.BaseCommit,
            IndexedAtUtc = recomputed.IndexedAtUtc,
            Documents = committed.Documents
                .Where(document => !replaced.Contains(NormalizePath(document.RelPath)))
                .Concat(recomputed.Documents)
                .ToList(),
            Symbols = symbols.Values.ToList(),
            Occurrences = committed.Occurrences
                .Where(occurrence => !replaced.Contains(NormalizePath(occurrence.DocumentPath)))
                .Concat(recomputed.Occurrences)
                .ToList(),
            Relationships = committed.Relationships
                .Where(relationship => !recomputedSources.Contains(relationship.SourceSymbolId))
                .Concat(recomputed.Relationships)
                .Distinct()
                .ToList(),
        }.NormalizeForUse();
    }

    /// <summary>
    /// The overlay itself, once the affected paths are settled. Every constructor ends here:
    /// they differ only in how that set is arrived at, and two copies of this would be two
    /// answers to what an overlay contains.
    /// </summary>
    private static SemanticIndexOverlay From(
        SemanticIndex baseIndex,
        SemanticIndex currentIndex,
        IReadOnlyDictionary<string, SemanticDocument> baseDocuments,
        IReadOnlyDictionary<string, SemanticDocument> currentDocuments,
        HashSet<string> affected)
    {
        var deleted = affected.Where(path =>
                baseDocuments.ContainsKey(path) && !currentDocuments.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var changedDocuments = affected.Where(currentDocuments.ContainsKey)
            .Select(path => currentDocuments[path])
            .OrderBy(document => document.RelPath, StringComparer.Ordinal)
            .ToList();
        var changedOccurrences = currentIndex.Occurrences
            .Where(occurrence => affected.Contains(NormalizePath(occurrence.DocumentPath)))
            .ToList();
        var impactedSymbols = baseIndex.Occurrences.Concat(currentIndex.Occurrences)
            .Where(occurrence => affected.Contains(NormalizePath(occurrence.DocumentPath)))
            .Select(occurrence => occurrence.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var relationships = currentIndex.Relationships.Where(relationship =>
                impactedSymbols.Contains(relationship.SourceSymbolId) ||
                impactedSymbols.Contains(relationship.TargetSymbolId))
            .ToList();
        var requiredSymbols = changedOccurrences.Select(occurrence => occurrence.SymbolId)
            .Concat(relationships.SelectMany(relationship =>
                new[] { relationship.SourceSymbolId, relationship.TargetSymbolId }))
            .ToHashSet(StringComparer.Ordinal);
        var currentSymbols = currentIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var baseSymbols = baseIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var symbols = requiredSymbols.Select(id =>
                currentSymbols.GetValueOrDefault(id) ?? baseSymbols.GetValueOrDefault(id) ??
                throw new InvalidDataException($"Semantic overlay symbol '{id}' is unavailable."))
            .ToList();
        var changed = new SemanticIndex
        {
            RepositoryId = currentIndex.RepositoryId,
            GenerationId = currentIndex.GenerationId,
            GitTree = currentIndex.GitTree,
            DirtyHash = currentIndex.DirtyHash,
            BaseCommit = currentIndex.BaseCommit,
            IndexedAtUtc = currentIndex.IndexedAtUtc,
            Documents = changedDocuments,
            Symbols = symbols,
            Occurrences = changedOccurrences,
            Relationships = relationships,
        }.NormalizeForUse();

        return new SemanticIndexOverlay
        {
            RepositoryId = currentIndex.RepositoryId,
            GenerationId = currentIndex.GenerationId,
            BaseGitTree = baseIndex.GitTree,
            GitTree = currentIndex.GitTree,
            DirtyHash = currentIndex.DirtyHash,
            DeletedDocuments = deleted,
            Changed = changed,
        }.Validate();
    }


    public SemanticIndex Materialize(SemanticIndex baseIndex)
    {
        ArgumentNullException.ThrowIfNull(baseIndex);
        var overlay = Validate();
        if (!string.Equals(baseIndex.RepositoryId, RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GenerationId, GenerationId, StringComparison.Ordinal) ||
            !string.Equals(baseIndex.GitTree, BaseGitTree, StringComparison.Ordinal))
        {
            throw new SemanticSnapshotMismatchException(IndexText.OverlayBaseMismatch);
        }

        var touched = DeletedDocuments.Concat(Changed.Documents.Select(document => document.RelPath))
            .ToHashSet(StringComparer.Ordinal);
        // A relationship is read off the declaration it belongs to -- the indexer walks
        // `class Bar : IService` and writes `Bar -> IService` down against `Bar` -- so whether the
        // base's copy still holds is a question about the file `Bar` is declared in. A save that
        // touches `IService` says nothing about it.
        //
        // Dropping on either endpoint was harmless while every overlay carried a rebuild of the
        // whole tree: whatever went came straight back in `Changed`. Since #521 a rebuild covers
        // the projects it opened, and those are the ones the changed file references, never the
        // ones that reference it -- so a doc comment on an interface, the narrowest plan there
        // is, emptied `find_implementations` for it until the next commit.
        var redeclared = baseIndex.Occurrences.Concat(Changed.Occurrences)
            .Where(occurrence =>
                occurrence.Roles.HasFlag(SemanticOccurrenceRoles.Definition) &&
                touched.Contains(occurrence.DocumentPath))
            .Select(occurrence => occurrence.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var documents = baseIndex.Documents.Where(document => !touched.Contains(document.RelPath))
            .Concat(Changed.Documents)
            .ToList();
        var occurrences = baseIndex.Occurrences
            .Where(occurrence => !touched.Contains(occurrence.DocumentPath))
            .Concat(Changed.Occurrences)
            .ToList();
        var relationships = baseIndex.Relationships
            .Where(relationship => !redeclared.Contains(relationship.SourceSymbolId))
            .Concat(Changed.Relationships)
            .Distinct()
            .ToList();
        var symbols = baseIndex.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        foreach (var symbol in Changed.Symbols)
        {
            symbols[symbol.Id] = symbol;
        }

        var requiredSymbols = occurrences.Select(occurrence => occurrence.SymbolId)
            .Concat(relationships.SelectMany(relationship =>
                new[] { relationship.SourceSymbolId, relationship.TargetSymbolId }))
            .ToHashSet(StringComparer.Ordinal);
        return new SemanticIndex
        {
            RepositoryId = RepositoryId,
            GenerationId = GenerationId,
            GitTree = GitTree,
            DirtyHash = DirtyHash,
            BaseCommit = Changed.BaseCommit,
            IndexedAtUtc = Changed.IndexedAtUtc,
            Documents = documents,
            Symbols = symbols.Values.Where(symbol => requiredSymbols.Contains(symbol.Id)).ToList(),
            Occurrences = occurrences,
            Relationships = relationships,
        }.NormalizeForUse();
    }

    /// <summary>
    /// Recomputable, and the most rebuilt file in the runtime tree: an overlay is made again
    /// whenever the branch moves or a file changes.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var overlay = Validate();
        AtomicFile.Write(
            path,
            stream =>
            {
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(Encoding.ASCII.GetBytes(Magic));
                writer.Write(CurrentVersion);
                writer.Write(overlay.RepositoryId);
                writer.Write(overlay.GenerationId);
                writer.Write(overlay.BaseGitTree);
                writer.Write(overlay.GitTree);
                writer.Write(overlay.DirtyHash is not null);
                if (overlay.DirtyHash is not null) writer.Write(overlay.DirtyHash);
                writer.Write(overlay.DeletedDocuments.Count);
                foreach (var deleted in overlay.DeletedDocuments) writer.Write(deleted);
                overlay.Changed.WriteTo(writer);
            },
            AtomicWrite.Recomputable);
    }

    public static SemanticIndexOverlay Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{path}' is not a semantic overlay.");
        }

        var version = reader.ReadInt32();
        if (version != CurrentVersion)
        {
            throw new InvalidDataException($"Semantic overlay '{path}' has unsupported version {version}.");
        }

        var repositoryId = reader.ReadString();
        var generationId = reader.ReadString();
        var baseGitTree = reader.ReadString();
        var gitTree = reader.ReadString();
        var dirtyHash = reader.ReadBoolean() ? reader.ReadString() : null;
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumTombstones)
        {
            throw new InvalidDataException("Semantic overlay tombstone count is invalid.");
        }

        var deleted = new List<string>(count);
        for (var index = 0; index < count; index++) deleted.Add(reader.ReadString());
        var changed = SemanticIndex.ReadFrom(reader, path, requireEnd: false);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Semantic overlay has trailing data.");
        }

        return new SemanticIndexOverlay
        {
            RepositoryId = repositoryId,
            GenerationId = generationId,
            BaseGitTree = baseGitTree,
            GitTree = gitTree,
            DirtyHash = dirtyHash,
            DeletedDocuments = deleted,
            Changed = changed,
        }.Validate();
    }

    private SemanticIndexOverlay Validate()
    {
        if (string.IsNullOrWhiteSpace(RepositoryId) || string.IsNullOrWhiteSpace(GenerationId) ||
            string.IsNullOrWhiteSpace(BaseGitTree) || string.IsNullOrWhiteSpace(GitTree))
        {
            throw new InvalidDataException("Semantic overlay identity is incomplete.");
        }

        var deleted = DeletedDocuments.Select(NormalizePath)
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (deleted.Distinct(StringComparer.Ordinal).Count() != deleted.Count ||
            deleted.Intersect(Changed.Documents.Select(document => document.RelPath), StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Semantic overlay document replacements are inconsistent.");
        }

        if (!string.Equals(Changed.RepositoryId, RepositoryId, StringComparison.Ordinal) ||
            !string.Equals(Changed.GenerationId, GenerationId, StringComparison.Ordinal) ||
            !string.Equals(Changed.GitTree, GitTree, StringComparison.Ordinal) ||
            !string.Equals(Changed.DirtyHash, DirtyHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic overlay payload identity is inconsistent.");
        }

        return this with { DeletedDocuments = deleted, Changed = Changed.NormalizeForUse() };
    }

    private static bool SameDocument(SemanticDocument? left, SemanticDocument? right) =>
        left is null && right is null ||
        left is not null && right is not null && left.Hash.AsSpan().SequenceEqual(right.Hash);

    private static bool SameOccurrences(SemanticIndex left, SemanticIndex right, string path)
    {
        var leftSet = left.Occurrences.Where(value => value.DocumentPath == path).ToHashSet();
        var rightSet = right.Occurrences.Where(value => value.DocumentPath == path).ToHashSet();
        return leftSet.SetEquals(rightSet);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Semantic overlay path '{path}' is invalid.");
        }

        return normalized;
    }
}
