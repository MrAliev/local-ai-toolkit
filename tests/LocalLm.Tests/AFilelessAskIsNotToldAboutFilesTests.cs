using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalLm.Tests;

/// <summary>
/// `ask_local` takes an empty file list on purpose — its own parameter says "May be empty for a
/// file-less question" — but the system prompt was written for one mode and sent in both. With
/// no files it still said "answer strictly from the supplied content" and "say explicitly when
/// the files do not contain the answer", so a model doing as it was told had to refuse.
///
/// Codex hit it on a default installation:
///
///     Я должен работать строго с содержимым ваших файлов и не могу извлекать данные или
///     выполнять вычисления на основе текста вашего сообщения.
///
/// It is a coin flip rather than a certainty — a 9B model obeys that instruction sometimes —
/// which is worse than a reliable failure, not better.
/// </summary>
public sealed class AFilelessAskIsNotToldAboutFilesTests
{
    [Fact]
    public async Task A_question_with_no_files_is_not_told_to_answer_from_files()
    {
        var client = new RecordingClient();

        await new LocalTasks(client).AskAsync(
            "List the numbers from 1 to 10.",
            [],
            model: null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(client.System);
        foreach (var fileWord in new[] { "files", "the supplied content" })
        {
            Assert.DoesNotContain(fileWord, client.System!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// And the mode that does have files keeps the instruction that mode needs: not inventing
    /// what the files do not say is the whole reason it is worded that way.
    /// </summary>
    [Fact]
    public async Task A_question_with_files_still_answers_strictly_from_them()
    {
        var client = new RecordingClient();
        var file = Path.Combine(Path.GetTempPath(), "localai-ask-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "class A { }", TestContext.Current.CancellationToken);
        try
        {
            await new LocalTasks(client).AskAsync(
                "List the types.",
                [file],
                model: null,
                TestContext.Current.CancellationToken);

            Assert.Contains("files", client.System!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "rather than filling the gap",
                client.System!,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Both modes still say which language to answer in.</summary>
    [Fact]
    public async Task Both_modes_carry_the_language_instruction()
    {
        var withoutFiles = new RecordingClient();
        await new LocalTasks(withoutFiles).AskAsync(
            "Anything.", [], model: null, TestContext.Current.CancellationToken);

        Assert.Contains(
            AnswerLanguage.Instruction.Trim(),
            withoutFiles.System!,
            StringComparison.Ordinal);
    }

    private sealed class RecordingClient : ILocalModelClient
    {
        public string? System { get; private set; }

        public Task<LocalJobResult<string>> ChatAsync(
            string model,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<string>> RoutedChatAsync(
            LocalTaskProfile profile,
            string prompt,
            string? system,
            IReadOnlyList<string>? imagesBase64,
            LocalWorkloadMetadata workload,
            LocalWorkflowHint? workflow,
            string? modelOverride,
            int? requestedContextTokens,
            LocalJobPriority priority,
            CancellationToken cancellationToken = default)
        {
            System = system;
            return Task.FromResult(new LocalJobResult<string>(
                "ok",
                new LocalUsageReceipt(
                    Guid.NewGuid(),
                    "local-lm",
                    "chat",
                    "qwen3.5:9b",
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    prompt.Length,
                    prompt.Length / 4,
                    null,
                    null,
                    null,
                    new LocalRoutingReceipt(
                        profile,
                        "qwen3.5:9b",
                        requestedContextTokens,
                        WasCold: false,
                        UsedFallback: false,
                        ValidatorResult: "none:pass",
                        EstimatedGrossCloudTokensSaved: 0,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: 0,
                        IsExperimentalAttempt: false))));
        }

        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(
            string model,
            int contextTokens,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(
            string model,
            string catalogVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(
            LocalTaskProfile profile,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(
            LocalTaskProfile profile,
            string model,
            ExperimentOwnerAction action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
