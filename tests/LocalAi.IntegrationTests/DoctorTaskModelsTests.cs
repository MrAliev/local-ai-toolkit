using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The `task-models` line in `localai doctor`: whether each task has a model installed, read from
/// what the broker last recorded. A valid catalogue used to be the whole of doctor's answer about
/// models, and it read as healthy while Ollama pointed at an empty model directory (#633).
/// </summary>
public sealed class DoctorTaskModelsTests : IDisposable
{
    private const string Name = "task-models";

    private static readonly DateTimeOffset Seen = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = Seen.AddMinutes(10);

    private static readonly string[] EveryModel =
    [
        "qwen2.5-coder:14b",
        "qwen3-vl:8b-instruct-q8_0",
        "qwen3-embedding:8b-q8_0",
        "gpt-oss:20b",
        "qwen3.5:9b",
        "translategemma:12b",
        "glm-ocr:bf16",
    ];

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-doctor-task-models-" + Guid.NewGuid().ToString("N"));

    public DoctorTaskModelsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Doctor starts nothing, so it names the command that would record them, and what that starts.</summary>
    [Fact]
    public void Nothing_recorded_yet_is_unknown_rather_than_ok()
    {
        var check = Check();

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("never_checked", Result());
        Assert.Equal(
            "unknown; the broker has not recorded the installed models yet. `localai model status` records them now, starting the broker if it is not running",
            check.Detail);
    }

    /// <summary>
    /// A running broker records them once a pass; older than two, nobody is recording, and what
    /// is on disk is not an answer.
    /// </summary>
    [Fact]
    public void A_record_more_than_two_passes_old_is_unknown_and_carries_no_answer()
    {
        Installed(EveryModel);
        var now = Seen + InstalledModelsSnapshot.StaleAfter + TimeSpan.FromSeconds(1);

        var check = Check(now);

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("stale", Result(now));
        Assert.Equal(
            "unknown; the broker last recorded the installed models at 2026-09-25 12:00 UTC, more than 30 min ago. `localai model status` records them now, starting the broker if it is not running",
            check.Detail);
        var line = Line(now);
        Assert.Equal("2026-09-25T12:00:00Z", line["checkedAtUtc"]!.GetValue<string>());
        Assert.Null(line["tasksWithoutModel"]);
        Assert.Null(line["installedModels"]);
    }

    [Fact]
    public void A_record_two_passes_old_still_answers()
    {
        Installed(EveryModel);

        Assert.Equal("all_covered", Result(Seen + InstalledModelsSnapshot.StaleAfter));
    }

    [Fact]
    public void Every_task_with_a_model_installed_is_ok()
    {
        Installed(EveryModel);

        var check = Check();

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Equal("all_covered", Result());
        Assert.Equal("every task that needs a model has one installed (checked 2026-09-25 12:00 UTC)", check.Detail);
        Assert.Empty(TasksWithoutModel());
        Assert.Equal(14, Line()["tasksChecked"]!.GetValue<int>());
        Assert.Equal(EveryModel, Strings(Line()["installedModels"]));
    }

    /// <summary>
    /// A model left out on purpose leaves its tasks without one, which is worth reading and not a
    /// fault: the installer lets a person choose.
    /// </summary>
    [Fact]
    public void A_task_with_no_model_installed_is_named_with_the_models_that_would_serve_it()
    {
        Installed(["qwen3.5:9b"]);

        var check = Check();

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal("some_uncovered", Result());
        Assert.Equal(["VectorEmbedding"], TasksWithoutModel());
        Assert.Equal(
            "tasks with no model installed: 1 of 14 (checked 2026-09-25 12:00 UTC) — VectorEmbedding (qwen3-embedding:8b-q8_0)",
            check.Detail);
    }

    /// <summary>Any one of them would do, in the order the router would try them.</summary>
    [Fact]
    public void Several_models_that_would_serve_a_task_are_offered_as_alternatives()
    {
        Installed(["qwen3-embedding:8b-q8_0", "qwen3-vl:8b-instruct-q8_0", "translategemma:12b", "glm-ocr:bf16"]);

        Assert.Equal(
            "tasks with no model installed: 8 of 14 (checked 2026-09-25 12:00 UTC) — " +
            "CodeAnalysis (gpt-oss:20b, qwen2.5-coder:14b or qwen3.5:9b); " +
            "CodeReview (gpt-oss:20b, qwen2.5-coder:14b or qwen3.5:9b); " +
            "LogTriage (qwen2.5-coder:14b or qwen3.5:9b); " +
            "Extraction (qwen3.5:9b or qwen2.5-coder:14b); " +
            "Classification (qwen2.5-coder:14b or qwen3.5:9b); " +
            "ShortSummary (qwen3.5:9b or qwen2.5-coder:14b); " +
            "MultiFileSynthesis (gpt-oss:20b, qwen3.5:9b or qwen2.5-coder:14b); " +
            "Planning (gpt-oss:20b, qwen3.5:9b or qwen2.5-coder:14b)",
            Check().Detail);
    }

    /// <summary>
    /// The case #633 was filed for: Ollama answered from a model directory with nothing in it, and
    /// every task was left without a model while the catalogue read as healthy.
    /// </summary>
    [Fact]
    public void No_task_with_a_model_is_a_failure()
    {
        Installed([]);

        var check = Check();

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Equal("none_covered", Result());
        Assert.Equal(14, TasksWithoutModel().Length);
        Assert.DoesNotContain("ExactSearch", TasksWithoutModel());
        Assert.Equal(
            "no task has a model installed: Ollama returned an empty list of installed models (checked 2026-09-25 12:00 UTC)",
            check.Detail);
    }

    /// <summary>A list that is not empty points somewhere else than an empty model directory.</summary>
    [Fact]
    public void Models_no_task_uses_leave_every_task_without_one()
    {
        Installed(["llama3:8b", "mistral:7b"]);

        var check = Check();

        Assert.Equal(DoctorStatus.Failed, check.Status);
        Assert.Equal("none_covered", Result());
        Assert.Equal(
            "no task has a model installed: none of the models Ollama lists (2) is used by any task (checked 2026-09-25 12:00 UTC)",
            check.Detail);
    }

    /// <summary>The router never picks a disabled model, so it serves no task here either.</summary>
    [Fact]
    public void A_disabled_model_serves_no_task()
    {
        Catalogue(document => document with
        {
            Models =
            [
                .. document.Models.Select(model => model.Tag == "qwen3-embedding:8b-q8_0"
                    ? model with { Lifecycle = LocalModelLifecycle.Disabled }
                    : model),
            ],
        });
        Installed(EveryModel);

        var check = Check();

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(["VectorEmbedding"], TasksWithoutModel());
        Assert.EndsWith("— VectorEmbedding (no model enabled in the catalog)", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>An image task sends images, and the router refuses a model that cannot read them.</summary>
    [Fact]
    public void An_image_task_needs_a_model_that_reads_images()
    {
        Catalogue(document => document with
        {
            Routes =
            [
                .. document.Routes.Select(route => route.Profile == LocalTaskProfile.VisualAnalysis
                    ? route with { Candidates = ["qwen2.5-coder:14b"], Fallbacks = ["qwen2.5-coder:14b"] }
                    : route),
            ],
        });
        Installed(EveryModel);

        var check = Check();

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal(["VisualAnalysis"], TasksWithoutModel());
        Assert.EndsWith(
            "— VisualAnalysis (no model enabled in the catalog reads images)",
            check.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_Russian_reader_gets_the_same_answer_in_Russian()
    {
        using var reading = TestCulture.Reading("ru");
        Installed(["qwen3-embedding:8b-q8_0", "qwen3-vl:8b-instruct-q8_0", "translategemma:12b", "gpt-oss:20b"]);

        var detail = Check().Detail;

        Assert.StartsWith(
            "задач, для которых не установлено ни одной модели: 4 из 14 (проверено 2026-09-25 12:00 UTC) — ",
            detail,
            StringComparison.Ordinal);
        Assert.Contains("LogTriage (qwen2.5-coder:14b или qwen3.5:9b)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Russian_reader_is_told_the_record_is_too_old_in_Russian()
    {
        using var reading = TestCulture.Reading("ru");
        Installed(EveryModel);

        Assert.Equal(
            "неизвестно — брокер в последний раз записал установленные модели 2026-09-25 12:00 UTC, больше 30 мин назад. Записать сейчас (брокер запустится, если не запущен): localai model status",
            Check(Seen + InstalledModelsSnapshot.StaleAfter + TimeSpan.FromMinutes(1)).Detail);
    }

    /// <summary>The `model-catalog` line fails for this; this one cannot answer without it.</summary>
    [Fact]
    public void An_unreadable_catalogue_leaves_the_question_unanswered()
    {
        var store = new ModelRoutingCatalogStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ not json");
        Installed(EveryModel);

        var check = Check();

        Assert.Equal(DoctorStatus.Unknown, check.Status);
        Assert.Equal("catalog_unreadable", Result());
        Assert.Equal(
            "unknown; without a readable model catalog there is no list of tasks to check — see model-catalog",
            check.Detail);
    }

    private void Installed(IReadOnlyList<string> models) =>
        new InstalledModelsSnapshotStore(root).Write(models, Seen);

    private void Catalogue(Func<ModelRoutingCatalogDocument, ModelRoutingCatalogDocument> change)
    {
        var store = new ModelRoutingCatalogStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllBytes(
            store.FilePath,
            JsonSerializer.SerializeToUtf8Bytes(
                change(ModelRoutingCatalogStore.LoadEmbedded()),
                LocalAiJson.Strict));
    }

    private DoctorCheck Check(DateTimeOffset? now = null) =>
        DoctorCommand.Inspect(root, now: now ?? Now).Checks.Single(check => check.Name == Name);

    private JsonObject Line(DateTimeOffset? now = null) =>
        DoctorCommand.Describe(DoctorCommand.Inspect(root, now: now ?? Now))["checks"]!.AsArray()
            .Single(check => check!["name"]!.GetValue<string>() == Name)!.AsObject();

    private string Result(DateTimeOffset? now = null) => Line(now)["result"]!.GetValue<string>();

    private string[] TasksWithoutModel() => Strings(Line()["tasksWithoutModel"]);

    private static string[] Strings(JsonNode? array) =>
        [.. array!.AsArray().Select(item => item!.GetValue<string>())];
}
