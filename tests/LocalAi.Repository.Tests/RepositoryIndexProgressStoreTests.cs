using LocalAi.Contracts;
using LocalAi.Repository;

namespace LocalAi.Repository.Tests;

public sealed class RepositoryIndexProgressStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-progress-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_is_atomic_and_round_trips_progress_and_eta()
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var expected = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.EmbeddingBase,
            @"C:\repo",
            120,
            300,
            2.5,
            TimeSpan.FromSeconds(72),
            DateTimeOffset.UtcNow);

        store.Save(expected);

        var actual = Assert.IsType<RepositoryIndexProgress>(store.Read());
        Assert.Equal(expected, actual);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    public void Save_rejects_invalid_chunk_counters(int processed, int total)
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var progress = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.EmbeddingBase,
            @"C:\repo",
            processed,
            total,
            1,
            TimeSpan.Zero,
            DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => store.Save(progress));
    }

    /// <summary>
    /// The step's counters are held to what the chunk pair beside them is held to, and for the
    /// same reason: the pair is printed as a fraction, and `analysed 30/26 projects` is a sentence
    /// no reader can do anything with. Refusing the write is what keeps a renderer from having to
    /// defend itself against its own record.
    /// </summary>
    [Theory]
    [InlineData(-1, 26)]
    [InlineData(30, 26)]
    [InlineData(0, -1)]
    public void Save_rejects_invalid_step_counters(int done, int total)
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var progress = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.SemanticBase,
            @"C:\repo",
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            Pause: null,
            Step: IndexBuildStep.AnalysingProjects,
            StepDone: done,
            StepTotal: total);

        Assert.Throws<InvalidDataException>(() => store.Save(progress));
    }

    /// <summary>
    /// And a step that counts nothing is not a step with broken counters: most of them have no
    /// figure at all, and zero of zero is how that is written.
    /// </summary>
    [Fact]
    public void Save_round_trips_a_step_that_counts_nothing()
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));
        var expected = new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.SemanticBase,
            @"C:\repo",
            0,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            Pause: null,
            Step: IndexBuildStep.RestoringDependencies);

        store.Save(expected);

        Assert.Equal(expected, store.Read());
    }

    /// <summary>
    /// The terminal record of a build from before the figure existed: the reader of today still
    /// has to take it, and says the figure is not there rather than that it is zero.
    /// </summary>
    [Fact]
    public void A_record_written_before_the_embedded_figure_existed_still_reads()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "progress.json"),
            """
            {"SchemaVersion":1,"Progress":{"RepositoryId":"repository","Phase":"Completed",
            "WorkingRoot":"C:\\repo","ProcessedChunks":0,"TotalChunks":0,"ChunksPerSecond":0,
            "EstimatedRemaining":"00:00:00","UpdatedAtUtc":"2026-09-27T10:00:00+00:00",
            "Pause":null,"Step":"SwitchingOver","StepDone":0,"StepTotal":0}}
            """);

        var progress = new RepositoryIndexProgressStore(FsPath.From(_root)).Read();

        Assert.NotNull(progress);
        Assert.Equal(RepositoryIndexProgressPhase.Completed, progress.Phase);
        Assert.Null(progress.ChunksEmbedded);
    }

    /// <summary>
    /// A reader built before the figure refuses a member it does not know, and an MCP server
    /// started before an update keeps reading this file until it is restarted. Left out where
    /// there is none, the figure changes the shape of the terminal record only, and every record
    /// of a build in progress stays one that reader can take.
    /// </summary>
    [Fact]
    public void A_record_without_the_embedded_figure_is_written_without_it()
    {
        var store = new RepositoryIndexProgressStore(FsPath.From(_root));

        store.Save(new RepositoryIndexProgress(
            "repository",
            RepositoryIndexProgressPhase.EmbeddingBase,
            @"C:\repo",
            120,
            300,
            2.5,
            TimeSpan.FromSeconds(72),
            DateTimeOffset.UtcNow));

        Assert.DoesNotContain(
            "ChunksEmbedded",
            File.ReadAllText(Path.Combine(_root, "progress.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_reports_zero_filled_progress_as_invalid_data()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "progress.json"), new byte[337]);

        var error = Assert.Throws<InvalidDataException>(
            () => new RepositoryIndexProgressStore(FsPath.From(_root)).Read());

        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
