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
/// nothing. So this asks git once per working root, keeps the answer, and reads a small file after
/// that.
///
/// Everything it cannot do is silence. A directory that is not a repository, a runtime that has
/// never recorded a build, a progress file half-written by a sync that is writing it right now -
/// none of those are worth a word above an answer the reader did ask for.
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

        var progress = Reader().Read(_root);

        Assert.NotNull(progress);
        Assert.Equal(RepositoryIndexProgressPhase.EmbeddingBase, progress.Phase);
        Assert.Equal(948, progress.ProcessedChunks);
        Assert.Equal(1511, progress.TotalChunks);
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

        reader.Read(_root);
        reader.Read(_root);
        reader.Read(_root);

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

        var before = reader.Read(_root);
        answer = _commonDirectory;
        var after = reader.Read(_root);

        Assert.Null(before);
        Assert.NotNull(after);
    }

    [Fact]
    public void A_runtime_that_has_recorded_nothing_says_nothing()
    {
        Assert.Null(Reader().Read(_root));
    }

    /// <summary>
    /// A sync writes progress.json by rename, so a torn read should not happen - but an installation
    /// carries files older than the schema that reads them, and the alternative to silence here is
    /// an exception thrown out of a search that had an answer ready.
    /// </summary>
    [Fact]
    public void A_progress_file_it_cannot_read_says_nothing()
    {
        var directory = Path.Combine(
            _runtimeRoot,
            "repositories",
            RepositoryIdentity.FromCommonDirectory(_commonDirectory).Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "progress.json"), "{ not json");

        Assert.Null(Reader().Read(_root));
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_runtimeRoot);
    }

    private RepositoryProgressReader Reader(Func<string?>? commonDirectory = null) =>
        new(_runtimeRoot, _ => commonDirectory is null ? _commonDirectory : commonDirectory());

    private void Record(RepositoryIndexProgress progress) =>
        new RepositoryIndexProgressStore(
                FsPath.From(_runtimeRoot).Combine(
                    "repositories",
                    RepositoryIdentity.FromCommonDirectory(_commonDirectory).Id))
            .Save(progress);

    private RepositoryIndexProgress Progress(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total) =>
        new(
            RepositoryIdentity.FromCommonDirectory(_commonDirectory).Id,
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
