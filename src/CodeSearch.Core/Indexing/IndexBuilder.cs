using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

public sealed record BuildResult(
    int FileCount,
    int ChunkCount,
    int FilesReused,
    int FilesEmbedded,
    int ChunksEmbedded,
    TimeSpan Elapsed,
    string IndexPath);

public sealed record BuildPlan(int FileCount, int FilesChanged, int ChunksToEmbed);

public sealed record IndexBuildProgress(
    int ProcessedChunks,
    int TotalChunks,
    double ChunksPerSecond,
    TimeSpan? EstimatedRemaining);

public sealed record IndexBuildContext(
    string Root,
    string GitCommit,
    string GitTree,
    string RepositoryId,
    string GenerationId,
    string? DirtyHash = null);

/// <summary>
/// Builds and incrementally refreshes an index. Incrementality is by file content hash: an
/// unchanged file keeps its existing chunks and vectors verbatim, so a refresh right after a
/// commit only re-embeds what that commit touched.
/// </summary>
public sealed class IndexBuilder(
    IEmbeddingClient embedder,
    Action<string>? log = null,
    Action<IndexBuildProgress>? progress = null,
    SymbolDefinitionCatalog? definitions = null,
    Func<TimeSpan, CancellationToken, Task>? backendWait = null)
{
    /// <summary>
    /// How the build waits for a backend that has gone, in the shape <c>OllamaTransport</c>
    /// already uses for its own retries: a delegate, so a test proves the trying rather than the
    /// speed of the machine it runs on.
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _backendWait =
        backendWait ?? Task.Delay;

    /// <summary>
    /// How long the build waits for a runner that died before giving up for real.
    ///
    /// Ten minutes, chosen by the owner and chosen for the slowest configuration this product
    /// supports rather than for the machine the failure was seen on: on a processor with no
    /// graphics card everything here takes substantially longer. The same figure already anchors
    /// the batch-size reasoning below — the broker's watchdog starts probing a job that has been
    /// silent for ten minutes.
    ///
    /// What it answers: a full build died twice in one day on a runner that had gone, and a third
    /// run started by hand minutes later finished. The difference was time and nothing else
    /// (#412).
    /// </summary>
    public static TimeSpan BackendWaitBudget { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How the tries are spaced: short at first, because a runner that is already coming back
    /// answers in seconds, then settling at half a minute.
    ///
    /// Thirty seconds is this product's own clock for silence — <c>ProgressQueued</c> uses it —
    /// and the embedding line prints about every fifteen seconds, so a wait that says nothing for
    /// longer than that is a silence of the kind #387 was about. The last interval is trimmed to
    /// what is left of the budget: a line promising more time than remains would be a lie in the
    /// one figure it carries.
    /// </summary>
    private static readonly TimeSpan[] BackendWaitLadder =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// One waiter at a time for the whole build. Two batches are in flight — the next is posted
    /// before the current is awaited — so an outage reaches both, and two unserialised waiters
    /// would print two countdowns of one outage and knock twice on a runner that is not there.
    /// The second waiter tries again the moment the first is let through, which is the moment the
    /// backend is known to be back.
    /// </summary>
    private readonly SemaphoreSlim _backendGate = new(1, 1);

    /// <summary>
    /// How long this build has spent waiting, so the embedding rate can be measured against the
    /// time it was embedding. Without this, a ninety-second outage lowers the average for the
    /// rest of the run and the estimate a reader uses to decide whether to wait is inflated by
    /// an event that is already over.
    /// </summary>
    private TimeSpan _backendWaited;

    /// <summary>
    /// Definition bodies for the tree being indexed, from the semantic phase that ran before
    /// this one. Empty for a build with no semantic data — a repository whose adapters are
    /// disabled, a machine without the external indexers — and every file then chunks the way it
    /// did before symbol-aware chunking existed.
    ///
    /// Not empty for an overlay, which this used to say: `localai sync` builds its semantics
    /// first and hands the catalogue to both builds alike. That matters now that reuse compares
    /// which chunker cut a file, because an overlay built without it would disagree with the base
    /// about every windowed file the catalogue names.
    /// </summary>
    private readonly SymbolDefinitionCatalog _definitions = definitions ?? SymbolDefinitionCatalog.Empty;

    /// <summary>
    /// Batches are capped by total characters, not item count. Chunk sizes vary by an order of
    /// magnitude, so a fixed count either starves the GPU on small chunks or sends an oversized
    /// request on large ones.
    /// </summary>
    private const int InitialBatchChars = 48_000;
    private const int MinimumBatchChars = 8_000;
    private const int MaximumBatchChars = 400_000;
    private const int BatchMaxItems = 512;

    /// <summary>
    /// The size is measured, not configured, because the right one differs by an order of
    /// magnitude between machines.
    ///
    /// Every request carries a fixed cost that has nothing to do with the model: assembling the
    /// batch, writing the job to the durable queue, polling for its result, writing the vectors
    /// back. Measured here, that was around 2.4 seconds against 3.6 seconds of actual embedding
    /// — the adapter idle for 40% of the run. Bigger batches amortise it away.
    ///
    /// A larger constant would have paid for that on a fast adapter and charged for it on a slow
    /// one: the same batch that takes 4 seconds on a desktop card takes minutes on an integrated
    /// one, and the broker's watchdog starts probing a job that has been silent for ten minutes.
    /// So the budget follows the observed throughput toward a target duration instead, and a
    /// slow machine simply keeps sending small batches.
    /// </summary>
    /// <summary>
    /// Public because it is also the longest any part of a sync may stay silent: a step with no
    /// counter of its own says it is still there on this cadence, so nothing anywhere in a run
    /// goes quiet for longer than one embedding batch (#419). Referenced rather than repeated, so
    /// the two cannot drift apart.
    /// </summary>
    public static TimeSpan TargetBatchDuration { get; } = TimeSpan.FromSeconds(15);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly Action<IndexBuildProgress> _progress = progress ?? (_ => { });

    /// <summary>
    /// Which chunker this build would use for a file, as it will be recorded beside the file's
    /// chunks. Reuse compares it, so the recording and the comparison come from one place.
    /// </summary>
    private string ChunkerIdentity(string relPath) =>
        ChunkerFactory.IdentityOf(relPath, _definitions);

    public async Task<BuildResult> BuildAsync(
        string root,
        string indexPath,
        bool force = false,
        CancellationToken ct = default,
        IndexBuildContext? context = null,
        string? embeddingCheckpointPath = null,
        int? expectedEmbeddingDimension = null)
    {
        var stopwatch = Stopwatch.StartNew();
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        var files = FileScanner.Enumerate(root);
        _log(IndexText.BuildScanned(root, files.Count));

        var existing = LoadReusable(indexPath, force);
        var previousByPath = BuildLookup(existing);

        var (reused, changed) = Partition(
            root,
            files,
            previousByPath,
            out var hashes,
            ChunkerIdentity);
        _log(IndexText.BuildPartitioned(reused.Count, changed.Count));

        var freshChunks = ChunkFiles(root, changed, ct);
        var totalToEmbed = freshChunks.Values.Sum(c => c.Count);
        _log(IndexText.BuildChunked(changed.Count, totalToEmbed));

        var plan = Plan(files, previousByPath, existing, freshChunks, hashes, ChunkerIdentity);
        RefuseAVectorBlockThatCannotBeAllocated(
            plan.TotalChunks,
            existing?.Dim ?? expectedEmbeddingDimension ?? 0);

        _progress(new IndexBuildProgress(0, totalToEmbed, 0, null));

        var checkpoint = embeddingCheckpointPath is null
            ? null
            : new EmbeddingCheckpointStore(
                embeddingCheckpointPath,
                embedder.Model,
                expectedEmbeddingDimension,
                _log);
        var vectorsByPath = await EmbedAsync(freshChunks, totalToEmbed, checkpoint, ct);

        var assembled = Assemble(plan, existing, vectorsByPath, out var dim);

        if (dim == 0)
        {
            throw new InvalidOperationException(
                "Nothing was embedded and no previous index exists, so vector width is unknown.");
        }

        var index = new CodeIndex
        {
            Dim = dim,
            Model = embedder.Model,
            Root = context?.Root ?? root,
            GitCommit = context?.GitCommit ?? RepoLocator.GitCommit(root),
            GitTree = context?.GitTree ??
                      RepoLocator.GitOutput(root, "rev-parse HEAD^{tree}") ??
                      string.Empty,
            RepositoryId = context?.RepositoryId ?? string.Empty,
            GenerationId = context?.GenerationId ?? string.Empty,
            DirtyHash = context?.DirtyHash,
            IndexedAtUtc = DateTime.UtcNow,
            Files = assembled.Files,
            Chunks = assembled.Chunks,
            Vectors = assembled.Vectors,
        };

        index.Save(indexPath);
        stopwatch.Stop();

        _log(IndexText.BuildSaved(
            indexPath,
            assembled.Chunks.Count,
            assembled.Files.Count,
            stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)));

        return new BuildResult(
            assembled.Files.Count,
            assembled.Chunks.Count,
            reused.Count,
            changed.Count,
            totalToEmbed,
            stopwatch.Elapsed,
            indexPath);
    }

    /// <summary>
    /// Builds this working tree's overlay against a base index: only files whose content differs
    /// from the base get embedded, plus a list of files the base has and this tree does not.
    ///
    /// The base is loaded WITHOUT vectors - only its per-file hashes are needed to decide what
    /// differs, so building an overlay never pays the ~700MB read of the base vector block.
    /// </summary>
    public async Task<BuildResult> BuildOverlayAsync(
        string workingRoot,
        string baseIndexPath,
        string overlayPath,
        CancellationToken ct = default,
        IndexBuildContext? context = null,
        string? embeddingCheckpointPath = null,
        int? expectedEmbeddingDimension = null)
    {
        var stopwatch = Stopwatch.StartNew();
        workingRoot = Path.GetFullPath(workingRoot).TrimEnd(Path.DirectorySeparatorChar);

        var baseIndex = CodeIndex.Load(baseIndexPath, withVectors: false);
        if (!string.Equals(baseIndex.Model, embedder.Model, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Base index uses '{baseIndex.Model}' but this run uses '{embedder.Model}'. " +
                "An overlay must be embedded with the base's model or the vectors cannot be compared.");
        }

        var files = FileScanner.Enumerate(workingRoot);
        var baseByPath = baseIndex.Files.ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase);
        _log(IndexText.BuildOverlayScanned(workingRoot, files.Count, baseByPath.Count));

        var (changed, hashes) = SelectDivergent(workingRoot, files, baseByPath);
        var deleted = baseByPath.Keys
            .Where(path => !hashes.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log(IndexText.BuildOverlayDivergence(changed.Count, deleted.Count));

        var existingOverlay = LoadReusable(NewestOverlayBeside(overlayPath), force: false);
        var overlayByPath = BuildLookup(existingOverlay);

        var toChunk = changed
            .Where(path => !(overlayByPath.TryGetValue(path, out var prior) &&
                             prior.Hash.AsSpan().SequenceEqual(hashes[path]) &&
                             string.Equals(
                                 prior.Chunker,
                                 ChunkerIdentity(path),
                                 StringComparison.Ordinal)))
            .ToList();

        _log(IndexText.BuildOverlayReuse(changed.Count - toChunk.Count, toChunk.Count));

        var freshChunks = ChunkFiles(workingRoot, toChunk, ct);
        var totalToEmbed = freshChunks.Values.Sum(c => c.Count);

        var plan = Plan(changed, overlayByPath, existingOverlay, freshChunks, hashes, ChunkerIdentity);
        RefuseAVectorBlockThatCannotBeAllocated(
            plan.TotalChunks,
            expectedEmbeddingDimension ?? baseIndex.Dim);

        _progress(new IndexBuildProgress(0, totalToEmbed, 0, null));
        var checkpoint = embeddingCheckpointPath is null
            ? null
            : new EmbeddingCheckpointStore(
                embeddingCheckpointPath,
                embedder.Model,
                expectedEmbeddingDimension ?? baseIndex.Dim,
                _log);
        var vectorsByPath = await EmbedAsync(freshChunks, totalToEmbed, checkpoint, ct);

        var assembled = Assemble(plan, existingOverlay, vectorsByPath, out var dim);

        // A file that diverges from the base and cuts into nothing has to be listed anyway.
        //
        // An overlay shadows a base entry by naming the file or by naming it deleted, and such a
        // file is in neither list: it is still on disk, so it is not deleted, and it contributes
        // no chunks, so `Assemble` leaves it out. The base's chunks for it then went on being
        // served — a symbol, a signature and a line range for text the file no longer holds,
        // taking a result slot, under a snippet marker telling the reader to run sync again when
        // sync had just run. Whitespace-only content, a NUL byte through a text extension and an
        // `<auto-generated/>` header all reach it.
        var shadowed = assembled.Files
            .Select(file => file.RelPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in changed.Where(path => !shadowed.Contains(path)))
        {
            assembled.Files.Add(new IndexedFile
            {
                RelPath = path,
                Hash = hashes[path],
                ChunkStart = assembled.Chunks.Count,
                ChunkCount = 0,
                Chunker = ChunkerIdentity(path),
            });
        }

        if (dim == 0)
        {
            // Nothing diverges at all: still write the overlay so deletions are recorded and the
            // search path does not keep falling back to "no overlay exists".
            dim = baseIndex.Dim;
        }

        var overlay = new CodeIndex
        {
            Dim = dim,
            Model = embedder.Model,
            Root = context?.Root ?? workingRoot,
            GitCommit = context?.GitCommit ?? RepoLocator.GitCommit(workingRoot),
            GitTree = context?.GitTree ??
                      RepoLocator.GitOutput(workingRoot, "rev-parse HEAD^{tree}") ??
                      string.Empty,
            RepositoryId = context?.RepositoryId ?? baseIndex.RepositoryId,
            GenerationId = context?.GenerationId ?? baseIndex.GenerationId,
            DirtyHash = context?.DirtyHash,
            IndexedAtUtc = DateTime.UtcNow,
            Files = assembled.Files,
            Chunks = assembled.Chunks,
            Vectors = assembled.Vectors,
            BaseCommit = baseIndex.GitCommit,
            DeletedPaths = deleted,
        };

        overlay.Save(overlayPath);
        stopwatch.Stop();

        _log(IndexText.BuildOverlaySaved(
            overlayPath,
            assembled.Chunks.Count,
            assembled.Files.Count,
            deleted.Count,
            stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)));

        return new BuildResult(
            assembled.Files.Count,
            assembled.Chunks.Count,
            changed.Count - toChunk.Count,
            toChunk.Count,
            totalToEmbed,
            stopwatch.Elapsed,
            overlayPath);
    }

    /// <summary>
    /// Files whose content is not byte-identical to the base. Everything else is covered by the
    /// base's own vectors, so the overlay must not duplicate it.
    /// </summary>
    private static (List<string> Changed, Dictionary<string, byte[]> Hashes) SelectDivergent(
        string root, List<string> files, Dictionary<string, IndexedFile> baseByPath)
    {
        var computed = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(files, relPath =>
        {
            var hash = HashFile(Path.Combine(root, relPath));
            if (hash is not null)
            {
                computed[relPath] = hash;
            }
        });

        var changed = new List<string>();
        foreach (var relPath in files)
        {
            if (!computed.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (!baseByPath.TryGetValue(relPath, out var inBase) ||
                !inBase.Hash.AsSpan().SequenceEqual(hash))
            {
                changed.Add(relPath);
            }
        }

        return (changed, new Dictionary<string, byte[]>(computed, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Costs out a refresh without embedding anything. The MCP server uses this to refuse a cold
    /// build inline (which would take many minutes and time the tool call out) and hand back a
    /// CLI command instead.
    /// </summary>
    public BuildPlan Plan(string root, string indexPath, bool force = false)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var files = FileScanner.Enumerate(root);
        var existing = LoadReusable(indexPath, force);
        var previousByPath = BuildLookup(existing);
        var (_, changed) = Partition(root, files, previousByPath, out _);

        // Chunk counting is a Roslyn parse per changed file - cheap next to embedding, and it
        // makes the estimate exact rather than a guess from file sizes.
        var chunks = ChunkFiles(root, changed, CancellationToken.None).Values.Sum(c => c.Count);
        return new BuildPlan(files.Count, changed.Count, chunks);
    }

    /// <summary>
    /// The overlay to reuse chunks from: the most recently written one in the directory this
    /// build is about to write into, which is normally the previous build of the same branch.
    ///
    /// Not <paramref name="overlayPath"/> itself, which was the whole defect. An overlay's file
    /// name carries the hash of every dirty file, so editing anything names a file that does not
    /// exist yet, and the reuse branch could only fire on a rebuild where nothing had changed —
    /// the one case with nothing to reuse. Two files out of 715 edited, and the build reported
    /// nought reused and two to embed, every time (#388).
    ///
    /// Choosing wrongly here cannot produce a wrong index, only a slower build — but only because
    /// <see cref="Reusable"/> stands on the branch that keeps a previous entry. That was written
    /// as a claim here before it was true anywhere: the filter this sentence meant lives in the
    /// caller, and a file that was re-chunked into nothing reached the keeping branch without
    /// passing it. The generation, worktree, head tree and model are guaranteed by the directory
    /// the path already names, and by the model check inside <see cref="LoadReusable"/>.
    /// </summary>
    private static string NewestOverlayBeside(string overlayPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(overlayPath));
        if (directory is null || !Directory.Exists(directory))
        {
            return overlayPath;
        }

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*.cidx")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName ?? overlayPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return overlayPath;
        }
    }

    private CodeIndex? LoadReusable(string indexPath, bool force)
    {
        if (force || !File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            var index = CodeIndex.Load(indexPath);
            // These two stay English literals rather than joining IndexText, for the reason
            // that class's comment gives: neither can be reached by an entry point that
            // follows the reader. Under `localai sync` a previous index reaches this path
            // only after CanReuseCorpusFrom agreed, and that compares the embedding model and
            // the index format version - so a mismatch in either was already refused upstream.
            // The one face that does reach them, `codesearch index`, pins English for all of
            // its output.
            if (index.FormatVersion < CodeIndex.CurrentVersion)
            {
                _log($"Existing index is format v{index.FormatVersion}; v{CodeIndex.CurrentVersion} " +
                     $"{CodeIndex.CurrentVersionReason} - rebuilding once.");
                return null;
            }

            if (!string.Equals(index.Model, embedder.Model, StringComparison.OrdinalIgnoreCase))
            {
                // Vectors from different models are not comparable, so a model switch is a full
                // rebuild whether the files changed or not.
                _log($"Existing index was built with '{index.Model}', now using '{embedder.Model}' - rebuilding from scratch.");
                return null;
            }

            _log(IndexText.BuildReusingIndex(index.Chunks.Count, index.Files.Count));
            return index;
        }
        catch (Exception ex)
        {
            _log(IndexText.BuildIndexNotReusable(ex.Message));
            return null;
        }
    }

    private static Dictionary<string, IndexedFile> BuildLookup(CodeIndex? index) =>
        index is null
            ? new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase)
            : index.Files.ToDictionary(f => f.RelPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many files a build would have to re-read, without doing any of it.
    ///
    /// This is the only estimate available before the semantic phase, and the semantic phase is
    /// the expensive thing to avoid: Roslyn loads the whole solution there. Chunk counts cannot
    /// be had this early — C# is cut on the definitions that phase produces — so a caller that
    /// wants to decline large work has to decide in files.
    ///
    /// Scanning and hashing is what it costs: seconds on a repository of a few thousand files,
    /// and no model, no Roslyn, no subprocess.
    /// </summary>
    public static int CountChangedFiles(string root, string? againstIndexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var files = FileScanner.Enumerate(root);
        var previous = againstIndexPath is not null && File.Exists(againstIndexPath)
            ? BuildLookup(CodeIndex.Load(againstIndexPath, withVectors: false))
            : [];
        var (_, changed) = Partition(root, files, previous, out _);
        return changed.Count;
    }

    /// <param name="identityOf">
    /// What this build would cut the file with, compared against what the previous index recorded.
    /// Null asks for the content check alone, which is what <see cref="CountChangedFiles"/> wants:
    /// it runs before the semantic phase, so it has no catalogue to answer the question with and
    /// says so rather than answering it wrongly.
    /// </param>
    private static (List<string> Reused, List<string> Changed) Partition(
        string root,
        List<string> files,
        Dictionary<string, IndexedFile> previous,
        out Dictionary<string, byte[]> hashes,
        Func<string, string>? identityOf = null)
    {
        var reused = new List<string>();
        var changed = new List<string>();
        var computed = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(files, relPath =>
        {
            var hash = HashFile(Path.Combine(root, relPath));
            if (hash is not null)
            {
                computed[relPath] = hash;
            }
        });

        foreach (var relPath in files)
        {
            if (!computed.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (previous.TryGetValue(relPath, out var prior) &&
                prior.Hash.AsSpan().SequenceEqual(hash) &&
                (identityOf is null ||
                 string.Equals(prior.Chunker, identityOf(relPath), StringComparison.Ordinal)))
            {
                reused.Add(relPath);
            }
            else
            {
                changed.Add(relPath);
            }
        }

        hashes = new Dictionary<string, byte[]>(computed, StringComparer.OrdinalIgnoreCase);
        return (reused, changed);
    }

    private static byte[]? HashFile(string path)
    {
        try
        {
            return CanonicalIndexText.Hash(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Dictionary<string, List<Chunk>> ChunkFiles(
        string root, List<string> relPaths, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, List<Chunk>>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(relPaths, new ParallelOptions { CancellationToken = ct }, relPath =>
        {
            var chunker = ChunkerFactory.Resolve(relPath, _definitions);
            if (chunker is null)
            {
                return;
            }

            try
            {
                var content = CanonicalIndexText.Read(Path.Combine(root, relPath));
                var chunks = chunker.Split(relPath, content).ToList();
                if (chunks.Count > 0)
                {
                    result[relPath] = chunks;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or is locked mid-scan simply doesn't get indexed this run.
            }
        });

        return new Dictionary<string, List<Chunk>>(result, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, float[][]>> EmbedAsync(
        Dictionary<string, List<Chunk>> chunksByFile,
        int total,
        EmbeddingCheckpointStore? checkpoint,
        CancellationToken ct)
    {
        var vectors = chunksByFile.ToDictionary(
            kv => kv.Key,
            kv => new float[kv.Value.Count][],
            StringComparer.OrdinalIgnoreCase);

        if (total == 0)
        {
            return vectors;
        }

        var queue = new List<(string RelPath, int Slot, string Text)>(total);
        var restored = 0;
        foreach (var (relPath, chunks) in chunksByFile)
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var text = CanonicalIndexText.Normalize(chunks[i].EmbedText);
                if (checkpoint?.TryGet(text, out var vector) == true)
                {
                    vectors[relPath][i] = vector;
                    restored++;
                }
                else
                {
                    queue.Add((relPath, i, text));
                }
            }
        }

        if (restored > 0)
        {
            _log(IndexText.BuildCheckpointRestored(restored, total));
            _progress(new IndexBuildProgress(
                restored,
                total,
                0,
                restored == total ? TimeSpan.Zero : null));
        }

        var stopwatch = Stopwatch.StartNew();
        var done = restored;
        var embeddedThisRun = 0;

        // A build that is waiting for a backend still has to look alive. Progress is what says
        // so: `Sync:` prints how long ago the record was written precisely so that a build whose
        // process died reads as stale, and a ten-minute pause with no write is indistinguishable
        // from that. The counters are the ones already reached; the rate is zero and the estimate
        // unknown, which is the shape a checkpoint restore already reports, so the reader is told
        // "calculating" rather than an estimate nobody is earning.
        void Pausing() => _progress(new IndexBuildProgress(done, total, 0, null));
        var position = 0;
        var budget = InitialBatchChars;

        // One batch stays on the queue while the one ahead of it is consumed. The broker keeps
        // a model resident only while the queue holds work for it, and a build that posted the
        // next batch after the previous one had answered left the queue empty between every
        // two — so every batch began by loading the model again, about a third of a sync's
        // wall time (#350). One in flight is enough to keep the queue from running dry; more
        // would only hold texts and vectors in memory for longer.
        InFlightBatch? current = null;
        InFlightBatch? next = null;
        var lastCompletedAt = stopwatch.Elapsed;
        try
        {
            while (position < queue.Count || current is not null)
            {
                ct.ThrowIfCancellationRequested();

                // Nothing is posted behind a batch that has already failed: the failure is
                // about to be reported, and a batch posted now would be work the broker does
                // for nobody — and, on a resumed build, work done twice, because nothing that
                // was never consumed reaches the checkpoint.
                var aheadHasFailed = current is not null &&
                                     current.Embeddings.IsCompleted &&
                                     !current.Embeddings.IsCompletedSuccessfully;
                next = null;
                if (position < queue.Count && !aheadHasFailed)
                {
                    var batch = new List<(string RelPath, int Slot, string Text)>();
                    var chars = 0;
                    while (position < queue.Count && batch.Count < BatchMaxItems &&
                           (batch.Count == 0 || chars + queue[position].Text.Length <= budget))
                    {
                        chars += queue[position].Text.Length;
                        batch.Add(queue[position]);
                        position++;
                    }

                    next = new InFlightBatch(
                        batch,
                        chars,
                        stopwatch.Elapsed,
                        EmbedBatchAsync(batch, checkpoint, Pausing, ct));
                }

                if (current is not null)
                {
                    var embeddings = await current.Embeddings;
                    checkpoint?.SaveBatch(
                        current.Items.Select(item => item.Text).ToArray(),
                        embeddings);
                    // However early it was posted, the batch was worked on only after the one
                    // ahead of it finished, so its cost runs from that moment — from its own
                    // post only when nothing was ahead of it.
                    var startedAt = current.PostedAt > lastCompletedAt
                        ? current.PostedAt
                        : lastCompletedAt;
                    lastCompletedAt = stopwatch.Elapsed;
                    budget = NextBudget(budget, current.Chars, lastCompletedAt - startedAt);
                    for (var i = 0; i < current.Items.Count; i++)
                    {
                        vectors[current.Items[i].RelPath][current.Items[i].Slot] = embeddings[i];
                    }

                    done += current.Items.Count;
                    embeddedThisRun += current.Items.Count;
                    // Time spent embedding, not wall clock: an outage the build waited out is
                    // over, and leaving it in the denominator lowers the average for the whole
                    // rest of the run. The estimate below is what a reader uses to decide whether
                    // to wait, so it must not be inflated by an event that has finished.
                    var embedding = stopwatch.Elapsed - _backendWaited;
                    var rate = embeddedThisRun / Math.Max(0.001, embedding.TotalSeconds);
                    // Against everything this build has to produce, not against what is left to
                    // embed. `done` starts at the number the checkpoint restored, so counting it
                    // against the queue reports 21 075 of 20 980 on a resumed build, with a
                    // negative estimate to match — and the progress store rejects both, which
                    // killed the build after every chunk of it had already been embedded.
                    var remaining = TimeSpan.FromSeconds((total - done) / Math.Max(0.001, rate));
                    _log(IndexText.BuildEmbedded(
                        done,
                        total,
                        rate.ToString("F1", CultureInfo.InvariantCulture),
                        remaining.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)));
                    _progress(new IndexBuildProgress(done, total, rate, remaining));
                }

                current = next;
            }
        }
        // The figures live only here, so the sentence that carries them is composed here. The
        // layer that raises the refusal knows the model and the backend's own words and nothing
        // about how far the build got - and how far it got is what decides whether the reader
        // starts again or gives up (#389). Figures first: the cause ends in a quoted response
        // body of unbounded shape, so nothing can safely follow it.
        catch (EmbeddingUnavailableException exception)
        {
            Observe(current?.Embeddings);
            Observe(next?.Embeddings);
            throw new EmbeddingUnavailableException(
                StoppedAt(done, total, checkpoint is not null) + " " + exception.Message,
                exception);
        }
        catch
        {
            // The batch behind the one that failed is already on the queue and cannot be
            // recalled. Its fault, if it ends in one, is read here so that it cannot surface
            // later as a second, unowned report of a build that has already said how it ended.
            Observe(current?.Embeddings);
            Observe(next?.Embeddings);
            throw;
        }

        return vectors;
    }

    /// <summary>
    /// How far the embedding got, and whether that is worth anything to the reader.
    ///
    /// With a checkpoint the count is a promise: everything counted is on disk, and running the
    /// build again continues from there. The promise is understated by construction - a batch is
    /// saved before <c>done</c> is incremented, and the halving path saves more - so it can be
    /// short of the truth but never ahead of it.
    ///
    /// Without one there is no promise to make, and the sentence says so instead of implying the
    /// work survived. That branch is reachable only from <c>codesearch</c>, which pins its whole
    /// output to English, so it stays a literal rather than a translated string no reader can
    /// ever be shown.
    /// </summary>
    private static string StoppedAt(int done, int total, bool checkpointed) =>
        done == 0
            ? IndexText.BuildEmbeddingStoppedAtStart(total)
            : checkpointed
                ? IndexText.BuildEmbeddingStopped(done, total)
                : $"Embedding stopped at {done} of {total} chunks. This run keeps no embedding " +
                  "checkpoint, so the next run embeds them again.";

    private sealed record InFlightBatch(
        IReadOnlyList<(string RelPath, int Slot, string Text)> Items,
        int Chars,
        TimeSpan PostedAt,
        Task<float[][]> Embeddings);

    /// <summary>
    /// Reads a fault nobody is going to await, and only a fault: a task that ends cancelled or
    /// successfully has nothing to report, and reading <c>Exception</c> is what marks a faulted
    /// one handled.
    /// </summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// The next character budget, from what the last batch actually cost.
    ///
    /// Moves by at most a factor of two per step. A single slow batch — a cold model, another
    /// client's job ahead in the queue — should nudge the size, not halve or double it, and the
    /// clamp keeps a machine that is briefly starved from collapsing to one chunk per request.
    /// </summary>
    internal static int NextBudget(int budget, int chars, TimeSpan elapsed)
    {
        if (chars <= 0 || elapsed <= TimeSpan.Zero)
        {
            return budget;
        }

        var perSecond = chars / elapsed.TotalSeconds;
        var target = perSecond * TargetBatchDuration.TotalSeconds;
        var bounded = Math.Clamp(target, budget / 2d, budget * 2d);
        return (int)Math.Clamp(bounded, MinimumBatchChars, MaximumBatchChars);
    }

    /// <summary>
    /// One failing chunk used to end a multi-hour run: the broker exception propagated straight
    /// out of the build and every embedding computed so far was discarded, because a generation
    /// is published atomically and nothing is checkpointed. Halving the batch on failure isolates
    /// the offender - a transient fault clears on the smaller retry, and a deterministic one is
    /// reported against the exact file and chunk instead of an opaque broker job id.
    ///
    /// The checkpoint comes down here for the sake of the half that succeeds.
    /// <see cref="EmbedAsync"/> saves a batch only once the whole of it comes back, so a failure
    /// in the second half threw the first half away - up to 511 chunks of finished work, redone
    /// on the next run, and a deterministic failure means on every run after it as well.
    /// </summary>
    /// <summary>
    /// One batch, waiting out a backend that has gone rather than ending the build with it.
    ///
    /// Here rather than above the build, because here the work already done is kept: the caller
    /// checkpoints each batch, and a build that resumes from the batch it was on loses nothing.
    /// The exception is the one <see cref="EmbedBatchAsync"/> deliberately refuses to halve on —
    /// halving cannot make a missing runner answer (#369) — so this is the only place that can
    /// treat it as a pause instead of an end.
    ///
    /// Cancellation is not caught: the wait is interruptible, and a build a person stopped must
    /// stop.
    /// </summary>
    private async Task<float[][]> EmbedWaitingForABackendAsync(
        IReadOnlyList<string> inputs,
        Action pausing,
        CancellationToken ct)
    {
        var deduplicationKey = EmbeddingDeduplicationKey(inputs);
        try
        {
            return await embedder.EmbedAsync(
                inputs,
                LocalJobPriority.Background,
                deduplicationKey,
                ct);
        }
        catch (EmbeddingUnavailableException)
        {
            // Down to the patient path. Taking the gate only now keeps the ordinary case
            // concurrent: batches are meant to overlap, and serialising them all to guard a
            // failure that has not happened would cost every build the throughput.
        }

        await _backendGate.WaitAsync(ct);
        try
        {
            return await WaitForABackendAsync(inputs, deduplicationKey, pausing, ct);
        }
        finally
        {
            _backendGate.Release();
        }
    }

    /// <summary>
    /// The patient path, entered holding the gate. Its first attempt is silent and immediate: for
    /// the batch that arrived second the backend is usually already back, and a countdown printed
    /// before finding that out would be a line about nothing.
    /// </summary>
    private async Task<float[][]> WaitForABackendAsync(
        IReadOnlyList<string> inputs,
        string deduplicationKey,
        Action pausing,
        CancellationToken ct)
    {
        var spent = TimeSpan.Zero;
        var outage = Stopwatch.StartNew();
        var waited = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var embeddings = await embedder.EmbedAsync(
                    inputs,
                    LocalJobPriority.Background,
                    deduplicationKey,
                    ct);
                if (waited)
                {
                    _log(IndexText.BuildBackendAnswered(
                        outage.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)));
                    _backendWaited += outage.Elapsed;
                }

                return embeddings;
            }
            catch (EmbeddingUnavailableException) when (spent < BackendWaitBudget)
            {
                // Trimmed to what is left, so the figure the line prints is one the build will
                // honour rather than one it rounds up to.
                var interval = BackendWaitLadder[Math.Min(attempt, BackendWaitLadder.Length - 1)];
                var remaining = BackendWaitBudget - spent;
                if (interval > remaining)
                {
                    interval = remaining;
                }

                _log(IndexText.BuildBackendWaiting(
                    remaining.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)));
                pausing();
                await _backendWait(interval, ct);
                spent += interval;
                waited = true;
            }
        }
    }

    private async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<(string RelPath, int Slot, string Text)> batch,
        EmbeddingCheckpointStore? checkpoint,
        Action pausing,
        CancellationToken ct)
    {
        var inputs = batch.Select(b => b.Text).ToList();
        try
        {
            return await EmbedWaitingForABackendAsync(inputs, pausing, ct);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
                not EmbeddingUnavailableException &&
            batch.Count > 1)
        {
            _log(IndexText.BuildBatchHalved(batch.Count, exception.Message));
            var half = batch.Count / 2;
            var headItems = batch.Take(half).ToArray();
            var head = await EmbedBatchAsync(headItems, checkpoint, pausing, ct);
            try
            {
                var tail = await EmbedBatchAsync(
                    [.. batch.Skip(half)],
                    checkpoint,
                    pausing,
                    ct);
                return [.. head, .. tail];
            }
            catch
            {
                // On the way out only. On the way through, the caller saves the whole batch, and
                // saving here as well would write the same vectors to a second file on disk.
                //
                // Every level of the recursion keeps its own half, so a failure deep inside still
                // leaves each half above it saved rather than only the innermost one.
                checkpoint?.SaveBatch([.. headItems.Select(item => item.Text)], head);
                throw;
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
                not EmbeddingUnavailableException and
                not EmbeddingChunkException)
        {
            var chunk = batch[0];
            throw new EmbeddingChunkException(
                $"Chunk {chunk.Slot} of '{chunk.RelPath}' ({chunk.Text.Length} characters) " +
                $"could not be embedded: {exception.Message}",
                exception);
        }
    }

    private string EmbeddingDeduplicationKey(IReadOnlyList<string> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(embedder.Model));
        foreach (var input in inputs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(input));
            hash.AppendData([0]);
        }

        return "codesearch:index:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// What the finished index will hold: every file that contributes a chunk, and whether those
    /// chunks are the freshly cut ones or the previous index's kept verbatim.
    ///
    /// Counted once and used at two moments that can be hours apart — the refusal below, before
    /// any embedding starts, and the single vector block allocated after it all. Counting twice
    /// would be counting by two rules free to drift, and a ceiling checked against a different
    /// total from the one allocated is worse than no ceiling at all.
    /// </summary>
    private sealed record ChunkPlan(
        List<Contribution> Contributions,
        int TotalChunks);

    /// <summary>
    /// One file's place in the finished index: its content hash, its chunks — freshly cut or the
    /// previous index's kept verbatim — and the chunker they came from, which is written beside
    /// them so a later build can tell whether they are still reusable.
    /// </summary>
    private readonly record struct Contribution(
        string RelPath,
        byte[] Hash,
        string Chunker,
        List<Chunk>? Fresh,
        IndexedFile? Prior);

    private static ChunkPlan Plan(
        List<string> files,
        Dictionary<string, IndexedFile> previous,
        CodeIndex? existing,
        Dictionary<string, List<Chunk>> freshChunks,
        Dictionary<string, byte[]> hashes,
        Func<string, string> identityOf)
    {
        var contributions = new List<Contribution>(files.Count);
        var totalChunks = 0;

        foreach (var relPath in files)
        {
            if (!hashes.TryGetValue(relPath, out var hash))
            {
                continue;
            }

            if (freshChunks.TryGetValue(relPath, out var chunks))
            {
                contributions.Add(new(relPath, hash, identityOf(relPath), chunks, null));
                totalChunks += chunks.Count;
            }
            else if (existing is not null &&
                     previous.TryGetValue(relPath, out var prior) &&
                     Reusable(prior, hash, relPath, identityOf))
            {
                // The prior identity rather than this build's: they are the same by the check
                // above, and the prior one is what actually cut these chunks.
                contributions.Add(new(relPath, hash, prior.Chunker, null, prior));
                totalChunks += prior.ChunkCount;
            }
        }

        return new ChunkPlan(contributions, totalChunks);
    }

    /// <summary>
    /// Whether a previous index's entry still describes this file, asked here rather than assumed
    /// from the caller having asked it already.
    ///
    /// The callers do filter — <see cref="Partition"/> and the overlay's own `toChunk` — but a
    /// file can reach the fallback branch without having passed either: one that was re-chunked
    /// and produced nothing. Whitespace-only content, a NUL byte from a build tool writing binary
    /// through a text extension, and a file locked between the hash and the read all do that, and
    /// the entry then arrives carrying the file's new hash over the old file's chunks and vectors.
    ///
    /// Search would answer from text the file no longer holds, and the next build would see a
    /// matching hash and a matching chunker and keep it. So the predicate lives beside the branch
    /// that acts on it, where it cannot be separated from it by a later edit.
    /// </summary>
    private static bool Reusable(
        IndexedFile prior,
        byte[] hash,
        string relPath,
        Func<string, string> identityOf) =>
        prior.Hash.AsSpan().SequenceEqual(hash) &&
        string.Equals(prior.Chunker, identityOf(relPath), StringComparison.Ordinal);

    /// <summary>
    /// The one ceiling that no machine can be bought out of: every vector of a finished index
    /// lives in a single <c>float[]</c>, and an array holds at most <c>Array.MaxLength</c>
    /// elements — about 524 000 chunks at 4096 dimensions. Past that the allocation in
    /// <see cref="Assemble"/> throws, and it is the last step of a build that may have run for
    /// hours, so the whole of it is lost to a number that was knowable before it started (#397).
    ///
    /// Which is why this stands before the embedding rather than beside the allocation. It is an
    /// upper bound and nothing more: a build well under it can still exhaust the machine it runs
    /// on, and refusing on that would be a guess about the machine rather than a fact about the
    /// index.
    ///
    /// A width of zero means nobody has said how wide a vector is — no previous index and no
    /// expected dimension — and the ceiling is a quotient, so there is nothing to check. That is
    /// only reachable from <c>codesearch index</c>: <c>localai sync</c> always carries the
    /// generation's dimension.
    /// </summary>
    private static void RefuseAVectorBlockThatCannotBeAllocated(int totalChunks, int dimension)
    {
        if (dimension <= 0 || (long)totalChunks * dimension <= Array.MaxLength)
        {
            return;
        }

        throw new InvalidOperationException(
            IndexText.BuildTooManyChunks(totalChunks, Array.MaxLength / dimension, dimension));
    }

    private static (List<IndexedFile> Files, List<ChunkMeta> Chunks, float[] Vectors) Assemble(
        ChunkPlan plan,
        CodeIndex? existing,
        Dictionary<string, float[][]> freshVectors,
        out int dim)
    {
        dim = existing?.Dim ?? 0;
        if (dim == 0)
        {
            foreach (var vectors in freshVectors.Values)
            {
                if (vectors.Length > 0 && vectors[0] is { Length: > 0 })
                {
                    dim = vectors[0].Length;
                    break;
                }
            }
        }

        // The plan counted the chunks before the embedding began, so the vector block is allocated
        // exactly once. Growing a List<float> to half a gigabyte instead would spike to ~1.5GB
        // mid-resize.
        var outFiles = new List<IndexedFile>(plan.Contributions.Count);
        var outChunks = new List<ChunkMeta>(plan.TotalChunks);
        var outVectors = new float[(long)plan.TotalChunks * dim];

        foreach (var (relPath, hash, chunker, fresh, prior) in plan.Contributions)
        {
            var start = outChunks.Count;
            var fileIndex = outFiles.Count;

            if (fresh is not null)
            {
                var vectors = freshVectors[relPath];
                for (var i = 0; i < fresh.Count; i++)
                {
                    var vector = vectors[i]
                        ?? throw new InvalidOperationException($"Chunk {i} of '{relPath}' was never embedded.");

                    outChunks.Add(ToMeta(fresh[i], fileIndex));
                    vector.AsSpan().CopyTo(outVectors.AsSpan((outChunks.Count - 1) * dim, dim));
                }
            }
            else if (prior is not null && existing is not null)
            {
                for (var i = 0; i < prior.ChunkCount; i++)
                {
                    var source = existing.Chunks[prior.ChunkStart + i];
                    outChunks.Add(new ChunkMeta
                    {
                        FileIndex = fileIndex,
                        Kind = source.Kind,
                        Symbol = source.Symbol,
                        Signature = source.Signature,
                        Namespace = source.Namespace,
                        LexicalText = source.LexicalText,
                        StartLine = source.StartLine,
                        EndLine = source.EndLine,
                    });

                    existing.VectorAt(prior.ChunkStart + i)
                        .CopyTo(outVectors.AsSpan((outChunks.Count - 1) * dim, dim));
                }
            }

            if (outChunks.Count == start)
            {
                continue;
            }

            outFiles.Add(new IndexedFile
            {
                RelPath = relPath,
                Hash = hash,
                ChunkStart = start,
                ChunkCount = outChunks.Count - start,
                Chunker = chunker,
            });
        }

        return (outFiles, outChunks, outVectors);
    }

    private static ChunkMeta ToMeta(Chunk chunk, int fileIndex) => new()
    {
        FileIndex = fileIndex,
        Kind = chunk.Kind,
        Symbol = chunk.Symbol,
        Signature = chunk.Signature,
        Namespace = chunk.Namespace,
        LexicalText = CanonicalIndexText.Normalize(chunk.EmbedText),
        StartLine = chunk.StartLine,
        EndLine = chunk.EndLine,
    };
}
