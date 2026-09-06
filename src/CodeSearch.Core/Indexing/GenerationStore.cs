using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

public sealed class GenerationStore
{
    private readonly string _generationsRoot;
    private readonly string _currentPath;
    private readonly string _stagingRoot;

    /// <summary>
    /// Takes an <see cref="FsPath"/> so the caller cannot hand over a path it has not
    /// canonicalised. This used to normalise for itself, one of nine places that each spelled
    /// out the same rule — and the directory named here is where a repository's generations
    /// and overlays live, so being wrong about it loses an index rather than a lookup.
    /// </summary>
    public GenerationStore(FsPath repositoryRuntimeRoot)
    {
        _generationsRoot = repositoryRuntimeRoot.Combine("generations").Value;
        _currentPath = repositoryRuntimeRoot.Combine("current.json").Value;
        _stagingRoot = repositoryRuntimeRoot.Combine("staging").Value;
    }

    /// <summary>
    /// Moves a generation that cannot be read out of the way, and answers whether it moved.
    ///
    /// A generation is immutable, so a damaged one is not repairable — it is rebuilt. But
    /// <see cref="PublishIndex"/> returns the existing manifest whenever the directory is there,
    /// which means reading it again and failing the same way, so the rebuild cannot begin until
    /// the damaged copy stops occupying the name (#390).
    ///
    /// Moved rather than deleted, and into <c>staging</c> rather than anywhere new: retention
    /// already collects everything there that no build could still be holding, so the evidence
    /// outlives the incident by a day and no longer. Deleting it outright would destroy the only
    /// copy of whatever went wrong at the moment somebody might want to look.
    /// </summary>
    public bool QuarantineUnreadable(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var directory = Path.Combine(_generationsRoot, generationId);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_stagingRoot);
            Directory.Move(
                directory,
                Path.Combine(
                    _stagingRoot,
                    generationId + ".corrupt-" + DateTime.UtcNow.ToString(
                        "yyyyMMdd-HHmmssfff",
                        CultureInfo.InvariantCulture)));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A build holding the directory open, or an ACL nobody expected. The caller's next
            // step fails on the same generation and says so; refusing to start because the
            // quarantine failed would replace a reported problem with a silent one.
            return false;
        }
    }

    public GenerationManifest PublishIndex(
        string sourceIndexPath,
        GenerationIdentity identity,
        string? sourceSemanticIndexPath = null,
        DateTimeOffset? publishedAtUtc = null,
        IReadOnlyList<SemanticAdapterStatus>? semanticAdapterStatuses = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIndexPath);
        ArgumentNullException.ThrowIfNull(identity);
        Directory.CreateDirectory(_generationsRoot);
        var target = Path.Combine(_generationsRoot, identity.Id);
        if (Directory.Exists(target))
        {
            var existing = ReadManifest(identity.Id);
            if (sourceSemanticIndexPath is not null && existing.SemanticIndexFile is null)
            {
                throw new InvalidOperationException(
                    "The immutable generation already exists without a semantic index.");
            }

            return existing;
        }

        if ((sourceSemanticIndexPath is null) != (identity.SemanticIndexVersion == 0))
        {
            throw new InvalidOperationException(
                "Semantic index input and generation semantic format version must be supplied together.");
        }

        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);
        try
        {
            const string indexFile = "base.cidx";
            var stagedIndex = Path.Combine(staging, indexFile);
            File.Copy(sourceIndexPath, stagedIndex, overwrite: false);
            var checksum = Checksum(stagedIndex);
            string? semanticIndexFile = null;
            string? semanticChecksum = null;
            if (sourceSemanticIndexPath is not null)
            {
                semanticIndexFile = "semantic.sidx";
                var stagedSemanticIndex = Path.Combine(staging, semanticIndexFile);
                File.Copy(sourceSemanticIndexPath, stagedSemanticIndex, overwrite: false);
                semanticChecksum = Checksum(stagedSemanticIndex);
            }

            var manifest = new GenerationManifest(
                identity,
                indexFile,
                checksum,
                publishedAtUtc ?? DateTimeOffset.UtcNow,
                semanticIndexFile,
                semanticChecksum,
                semanticAdapterStatuses);
            AtomicWriteJson(Path.Combine(staging, "manifest.json"), manifest);
            Directory.Move(staging, target);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public GenerationManifest ReadManifest(string generationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        var directory = Path.Combine(_generationsRoot, generationId);
        var manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(Path.Combine(directory, "manifest.json")),
            LocalAiJson.Strict)
            ?? throw new InvalidDataException("Generation manifest is empty.");
        if (manifest.Identity.Id != generationId)
        {
            throw new InvalidDataException("Generation identity does not match its directory.");
        }

        var indexPath = Path.Combine(directory, manifest.IndexFile);
        if (!File.Exists(indexPath) ||
            !string.Equals(
                Checksum(indexPath),
                manifest.IndexChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Generation index checksum does not match.");
        }

        if ((manifest.SemanticIndexFile is null) != (manifest.SemanticIndexChecksum is null) ||
            (manifest.SemanticIndexFile is null) != (manifest.Identity.SemanticIndexVersion == 0))
        {
            throw new InvalidDataException(
                "Generation semantic index metadata is inconsistent with its identity.");
        }

        if (manifest.SemanticIndexFile is not null)
        {
            var semanticPath = Path.Combine(directory, manifest.SemanticIndexFile);
            if (!File.Exists(semanticPath) ||
                !string.Equals(
                    Checksum(semanticPath),
                    manifest.SemanticIndexChecksum,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Generation semantic index checksum does not match.");
            }
        }

        if (manifest.SemanticAdapters is not null &&
            (manifest.SemanticAdapters.Any(status =>
                 string.IsNullOrWhiteSpace(status.Name) ||
                 !Enum.IsDefined(status.State) ||
                 status.DurationMilliseconds < 0) ||
             manifest.SemanticAdapters.Select(status => status.Name)
                 .Distinct(StringComparer.Ordinal).Count() != manifest.SemanticAdapters.Count))
        {
            throw new InvalidDataException("Generation semantic adapter metadata is invalid.");
        }

        return manifest;
    }

    /// <summary>
    /// The generation this repository currently answers from, or null when there is not one —
    /// and null too when the pointer cannot be read, because for everybody who asks this way the
    /// two are the same answer: nothing is known to be current.
    ///
    /// That used to escape as an exception, and it escaped through everybody. Of the eight callers
    /// only <see cref="GenerationRetention"/> guarded itself, and two of the unguarded ones —
    /// <c>RuntimeIndexLayout</c> and the semantic navigation gateway — are on the read path, so a
    /// <c>current.json</c> that does not parse took search and navigation down along with the
    /// sync. The swallow belongs here rather than at seven call sites.
    ///
    /// Retention is the one caller that must not have them collapsed, and it asks through
    /// <see cref="TryReadCurrent(out GenerationPointer?)"/> instead.
    /// </summary>
    public GenerationPointer? ReadCurrent() =>
        TryReadCurrent(out var current) ? current : null;

    /// <summary>
    /// Reads the pointer, telling "there is no pointer" apart from "the pointer cannot be read".
    ///
    /// Only <see cref="GenerationRetention"/> needs the difference, and it needs it badly. With no
    /// pointer nothing is current, so nothing has been superseded and a pass may prune down to the
    /// count; with an unreadable one the generation it names is still the generation this
    /// repository answers from, and it may be the one the pass was about to remove. Deleting it
    /// costs the whole embedding pass again — about nine minutes for this repository, measured — so an unreadable pointer stops the pass instead.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when <c>current.json</c> is there and cannot be opened or parsed.
    /// <see langword="true"/> otherwise, with <paramref name="current"/> null when the file is
    /// simply absent.
    /// </returns>
    public bool TryReadCurrent(out GenerationPointer? current)
    {
        current = null;
        if (!File.Exists(_currentPath))
        {
            return true;
        }

        try
        {
            current = JsonSerializer.Deserialize<GenerationPointer>(
                File.ReadAllText(_currentPath),
                LocalAiJson.Strict);
            // Refused here rather than at the two call sites that turn this into a path, for the
            // reason the swallow above is here: a value that is not a generation id is not one for
            // anybody, and the boundary is where the launcher checks its own pointer too
            // (CurrentPointerSnapshot.ReadPath, before the version becomes a directory name).
            //
            // Unreadable rather than absent, including for a document that parses to nothing at
            // all: the file is there and says something, and what it says cannot be acted on.
            // Retention is the caller that needs the difference, and refusing to prune is the
            // conservative side of it.
            if (!GenerationIdentity.IsWellFormedId(current?.GenerationId))
            {
                current = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
                IOException or
                UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// How long the rename keeps retrying while a reader holds the pointer open.
    ///
    /// <c>TryReadCurrent</c> reads through <c>File.ReadAllText</c>, which takes
    /// <c>FileShare.Read</c> and so denies <c>FILE_SHARE_DELETE</c>: while that handle is open the
    /// publish cannot replace the file. This pointer has more readers than anything else in the
    /// runtime tree — every search, every navigation call, every <c>index_status</c>, every
    /// retention pass — and it was the one with no patience at all, so a lost race threw out of
    /// the sync after the whole build.
    ///
    /// The number is <c>BrokerRuntimeStateStore</c>'s, carried over rather than measured
    /// here, and the two cases differ: a missed heartbeat costs one tick, a refused publish costs
    /// the build that produced it. What the budget has to outlast is the same in both — a reader
    /// parsing a document of a few hundred bytes — but nobody has measured that on a loaded
    /// machine for this file, and this comment should not pretend otherwise.
    /// </summary>
    public static TimeSpan PublishRetryBudget { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Unconditional pointer write. Nothing in <c>src/</c> calls it: of its thirty-one
    /// references, one is this declaration and thirty are test setup. It used to say "and repair
    /// paths" as well, which named <c>GenerationPublisher</c> — a class no path reached, deleted
    /// with the rest of the readiness subsystem (#394).
    ///
    /// A sync publishes through the expectation overload below, and that is the point: an
    /// unconditional write is how an older run used to move the index backwards over a newer one
    /// (#199). If this overload acquires a caller in <c>src/</c>, that caller is the defect —
    /// #199 again, wearing a different name.
    /// </summary>
    public void SetCurrent(GenerationManifest generation) =>
        SetCurrent(generation, PublishRetryBudget);

    /// <param name="retryBudget">
    /// A duration rather than a number of attempts, for the reason
    /// <c>BrokerRuntimeStateStore</c> gives: an attempt count silently means different
    /// amounts of waiting depending on what else the machine is doing.
    /// </param>
    public void SetCurrent(GenerationManifest generation, TimeSpan retryBudget)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryBudget, TimeSpan.Zero);
        ReadManifest(generation.Identity.Id);
        WriteCurrent(generation, retryBudget);
    }

    /// <summary>
    /// Publishes the pointer only while it still is what the caller observed when planning.
    /// The sync gate already serializes runs; this is the second guard, for the gate a
    /// killed process abandoned — the same belt-and-braces the launcher's activation keeps
    /// with --if-current-sha256. A pointer that moved since planning means this run's whole
    /// view is stale, and a stale run must not make itself current.
    ///
    /// An unreadable pointer therefore lets the publish through, deliberately: both reads answer
    /// null, the comparison matches, and the write lands. It should. A pointer nobody can read
    /// names no generation to protect from being overwritten, and publishing over it is how the
    /// repository comes back.
    /// </summary>
    public void SetCurrent(
        GenerationManifest generation,
        GenerationPointer? expectedCurrent)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ReadManifest(generation.Identity.Id);
        var observed = ReadCurrent();
        var matches = observed is null
            ? expectedCurrent is null
            : expectedCurrent is not null &&
              string.Equals(
                  observed.GenerationId,
                  expectedCurrent.GenerationId,
                  StringComparison.Ordinal);
        if (!matches)
        {
            throw new InvalidOperationException(
                "current_pointer_changed: the repository's current generation moved from " +
                $"'{expectedCurrent?.GenerationId ?? "none"}' to " +
                $"'{observed?.GenerationId ?? "none"}' while this sync ran; refusing to " +
                "publish a stale result. Run sync again.");
        }

        WriteCurrent(generation, PublishRetryBudget);
    }

    /// <summary>
    /// Written without <c>FileOptions.WriteThrough</c> and without a flush to disk, unlike
    /// <c>RepositoryIndexProgressStore.Save</c> — which is the difference #392 asks about. The
    /// answer for this file: nothing in a pointer is observed once. It carries a generation id,
    /// the dev tree it was built from, and when that was decided, and the id is a hash of the
    /// repository id, dev commit, dev tree, embedding model, dimension and the format versions —
    /// every one of which the next run derives again from the same repository. What a lost
    /// pointer costs is recomputation, not a fact that existed only here.
    ///
    /// The axis is recoverable or not, rather than important or not: this pointer is as important
    /// as anything in the runtime tree, and still does not need the flush.
    /// </summary>
    private void WriteCurrent(GenerationManifest generation, TimeSpan retryBudget) =>
        AtomicWriteJson(
            _currentPath,
            new GenerationPointer(
                generation.Identity.Id,
                generation.Identity.DevTree,
                DateTimeOffset.UtcNow),
            retryBudget);

    public string IndexPath(string generationId) =>
        Path.Combine(_generationsRoot, generationId, "base.cidx");

    public string SemanticIndexPath(string generationId) =>
        Path.Combine(_generationsRoot, generationId, "semantic.sidx");

    private static string Checksum(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <param name="retryBudget">
    /// How long the rename keeps trying while a reader holds the destination open. Null for the
    /// manifest, which is written into a staging directory nothing has opened yet — only the
    /// pointer has readers to lose a race with.
    /// </param>
    private static void AtomicWriteJson<T>(string path, T value, TimeSpan? retryBudget = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(value, LocalAiJson.Strict));
            if (retryBudget is not { } budget)
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }

            var deadline = DateTime.UtcNow + budget;
            while (true)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (Exception error) when (
                    error is IOException or UnauthorizedAccessException &&
                    DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(25));
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
