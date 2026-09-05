using CodeSearch.Mcp;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Tests;

/// <summary>
/// How an answer finds the build that is running behind it.
///
/// index_status could afford RuntimeIndexLayout.Inspect - five git processes and a hash of every
/// dirty file - because a person asks for a status now and then. Every search and every navigation
/// answer cannot: the figures are worth a line above an answer only if reading them costs about
/// nothing. So this asks git once per working root, keeps the answer, and after that reads a small
/// file and takes one momentary look at a named mutex.
///
/// It reads both because neither answers alone. The record says how far a build got; only the gate
/// says whether anything is running, because nothing writes an end for a process that was killed.
///
/// Everything it cannot answer is silence. A directory that is not a repository, a runtime that
/// has never recorded a build, a progress file half-written by a sync that is writing it right now
/// - none of those are worth a word above an answer the reader did ask for.
/// </summary>
public sealed class RepositoryProgressReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-progress-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-progress-runtime-" + Guid.NewGuid().ToString("N"));

    private readonly string _commonDirectory;

    public RepositoryProgressReaderTests()
    {
        _commonDirectory = Path.Combine(_root, ".git");
        Directory.CreateDirectory(_commonDirectory);
    }

    [Fact]
    public void A_recorded_build_is_read_back()
    {
        Record(Progress(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

        var sighting = Reader().Look(_root);

        Assert.NotNull(sighting);
        Assert.Equal(RepositoryId, sighting.RepositoryId);
        Assert.NotNull(sighting.Progress);
        Assert.Equal(RepositoryIndexProgressPhase.EmbeddingBase, sighting.Progress.Phase);
        Assert.Equal(948, sighting.Progress.ProcessedChunks);
        Assert.Equal(1511, sighting.Progress.TotalChunks);
    }

    /// <summary>
    /// The half the record cannot answer, and the reason this is not just a file read: whether a
    /// sync holds the gate right now decides whether an answer may tell the reader to start one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_gate_is_reported_as_it_is_found(bool held)
    {
        Record(Progress(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

        var sighting = Reader(gateHeld: _ => held).Look(_root);

        Assert.NotNull(sighting);
        Assert.Equal(held, sighting.BuildHoldsTheGate);
    }

    /// <summary>
    /// A build seen before it has written anything is still a build. The gate is taken before the
    /// first byte of shared state, so this window exists on every sync and is the whole of it on
    /// a machine building its first index.
    /// </summary>
    [Fact]
    public void A_gate_held_with_nothing_recorded_is_still_a_sighting()
    {
        var sighting = Reader(gateHeld: _ => true).Look(_root);

        Assert.NotNull(sighting);
        Assert.True(sighting.BuildHoldsTheGate);
        Assert.Null(sighting.Progress);
    }

    /// <summary>
    /// The reason this class exists rather than a call to RuntimeIndexLayout.Inspect. A build of
    /// this repository embeds about seven and a half thousand chunks, and a session asks dozens of
    /// questions while it runs; a git process per answer would be paid for the whole build.
    /// </summary>
    [Fact]
    public void The_repository_is_identified_once_however_many_answers_carry_the_line()
    {
        Record(Progress(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));
        var asked = 0;
        var reader = Reader(() =>
        {
            asked++;
            return _commonDirectory;
        });

        reader.Look(_root);
        reader.Look(_root);
        reader.Look(_root);

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// And a failure to identify it is not remembered as one. A directory asked about before
    /// `git init` has run answers nothing; asked again after, it answers - so the negative cannot
    /// be the thing that is cached, or one badly-timed question would silence this repository's
    /// figures for the life of the server.
    /// </summary>
    [Fact]
    public void A_directory_that_is_not_a_repository_yet_is_asked_about_again()
    {
        Record(Progress(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));
        string? answer = null;
        var reader = Reader(() => answer);

        var before = reader.Look(_root);
        answer = _commonDirectory;
        var after = reader.Look(_root);

        Assert.Null(before);
        Assert.NotNull(after);
    }

    [Fact]
    public void A_runtime_that_has_recorded_nothing_reports_no_progress()
    {
        var sighting = Reader().Look(_root);

        Assert.NotNull(sighting);
        Assert.Null(sighting.Progress);
    }

    /// <summary>
    /// A sync writes progress.json by rename, so a torn read should not happen - but an installation
    /// carries files older than the schema that reads them, and the alternative to silence here is
    /// an exception thrown out of a search that had an answer ready.
    /// </summary>
    [Fact]
    public void A_progress_file_it_cannot_read_reports_no_progress()
    {
        var directory = Path.Combine(_runtimeRoot, "repositories", RepositoryId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "progress.json"), "{ not json");

        var sighting = Reader().Look(_root);

        Assert.NotNull(sighting);
        Assert.Null(sighting.Progress);
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_runtimeRoot);
    }

    private string RepositoryId =>
        RepositoryIdentity.FromCommonDirectory(_commonDirectory).Id;

    private RepositoryProgressReader Reader(
        Func<string?>? commonDirectory = null,
        Func<string, bool>? gateHeld = null) =>
        new(
            _runtimeRoot,
            _ => commonDirectory is null ? _commonDirectory : commonDirectory(),
            gateHeld ?? (_ => false));

    private void Record(RepositoryIndexProgress progress) =>
        new RepositoryIndexProgressStore(
                FsPath.From(_runtimeRoot).Combine("repositories", RepositoryId))
            .Save(progress);

    private RepositoryIndexProgress Progress(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total) =>
        new(
            RepositoryId,
            phase,
            _root,
            processed,
            total,
            10.2,
            TimeSpan.FromMinutes(0.9),
            DateTimeOffset.UtcNow);

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
