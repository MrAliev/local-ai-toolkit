using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Core.Resources;
using LocalLm.Mcp;

namespace LocalLm.Tests;

/// <summary>
/// `translate_local` reading and writing files itself.
///
/// The tool existed to keep tokens out of a caller's context and could only be given text,
/// so translating a document meant reading it into that context first - spending exactly what
/// the tool is for. `path` and `outPath` close that: with both, a 200 KB document is read,
/// translated and written without any part of it passing through the conversation.
///
/// The refusals are asserted against the catalogue rather than against English words, because
/// they are printed in the reader's language and these tests have to hold in both.
/// </summary>
public sealed class TranslateLocalReadsAFileTests : IDisposable
{
    private const string English = "English version";
    private const string Russian = "Английская версия";

    // Resolved once, so every path asserted below is already the form the refusals print: they
    // name the resolved path, never the path as it was typed.
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "localai-translate-tool-" + Guid.NewGuid().ToString("N")));

    public TranslateLocalReadsAFileTests() => Directory.CreateDirectory(_root);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// The point of the whole change: the caller names a file and never holds its content.
    /// The origin marker names the document, so a relayed answer still says where it came from.
    /// </summary>
    [Fact]
    public async Task A_named_file_is_read_by_the_tool_and_not_by_the_caller()
    {
        var document = Write("notes.txt", English);
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Russian, answer, StringComparison.Ordinal);
        Assert.Contains(
            "origin=\"translate_local:" + document + "\"",
            answer,
            StringComparison.Ordinal);
        Assert.Equal(LocalTaskProfile.PlainTranslation, Assert.Single(client.Profiles));
    }

    /// <summary>
    /// Not `ExactlyOneSource`, which names `path` and `text`: this tool has no `text`, and
    /// naming a parameter it does not have sends the caller to fix the wrong thing.
    /// </summary>
    [Fact]
    public async Task Text_and_a_file_together_are_refused_before_anything_runs()
    {
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            source: English,
            path: Write("notes.txt", English),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LocalLmText.TranslateSourceAmbiguous, answer, StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    [Fact]
    public async Task Neither_text_nor_a_file_is_refused()
    {
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LocalLmText.TranslateSourceMissing, answer, StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    /// <summary>
    /// The console warns before the run because a person can stop one; here the run is already
    /// over by the time anybody reads a word, so the answer says what was assumed instead.
    /// </summary>
    [Fact]
    public async Task A_markdown_file_turns_the_flag_on_and_the_answer_says_it_did()
    {
        var document = Write("readme.md", "# Title\r\n\r\nText with `Code()`.");
        var client = new RecordingClient("# Заголовок\r\n\r\nТекст с `Code()`.");

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LocalTaskProfile.TechnicalTranslation, Assert.Single(client.Profiles));
        Assert.Contains(
            LocalLmText.TranslateMarkdownOn(document),
            answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch, and the reason it prints nothing: an explicit `false` assumed nothing,
    /// so there is nothing to report. MCP has no standard input, so this flag is the only way
    /// to translate a Markdown file as plain text.
    /// </summary>
    [Fact]
    public async Task Markdown_false_overrides_the_extension_and_says_nothing()
    {
        var document = Write("notes.md", English);
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            markdown: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LocalTaskProfile.PlainTranslation, Assert.Single(client.Profiles));
        Assert.DoesNotContain(
            LocalLmText.TranslateMarkdownOn(document),
            answer,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The limit is on what may land in a caller's context, not on what may be translated, so
    /// `outPath` lifts it. Refused before the model runs: minutes of GPU work followed by a
    /// refusal would be the worst of both.
    /// </summary>
    [Fact]
    public async Task A_document_too_large_to_hand_back_is_refused_before_the_model_runs()
    {
        var content = new string('a', LocalLmTools.MaximumReturnedCharacters + 1);
        var document = Write("big.txt", content);
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            LocalLmText.TranslateNeedsOutPath(
                document,
                content.Length,
                LocalLmTools.MaximumReturnedCharacters),
            answer,
            StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    /// <summary>
    /// With `outPath` the answer carries no content-derived text at all, so it carries no
    /// untrusted markers either: wrapping nothing teaches a caller to skim them.
    /// </summary>
    [Fact]
    public async Task With_an_output_file_the_document_goes_to_disk_and_not_into_the_answer()
    {
        var document = Write("notes.txt", English);
        var target = Path.Combine(_root, "notes.ru.txt");
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains(Russian, written, StringComparison.Ordinal);
        Assert.DoesNotContain("<untrusted-content", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(Russian, answer, StringComparison.Ordinal);
        Assert.Contains(
            LocalLmText.TranslateWritten(
                target,
                new FileInfo(target).Length / 1024,
                written.Length),
            answer,
            StringComparison.Ordinal);

        // Nothing was assumed and nothing was replaced, so the answer stops there.
        Assert.DoesNotContain(LocalLmText.TranslateReplaced, answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Overwriting is allowed - a `name.md` and its `name.ru.md` both exist already, which is
    /// the main use - so the answer says it happened rather than the tool refusing and inviting
    /// a caller to delete the target first, which loses more when a run dies halfway.
    /// </summary>
    [Fact]
    public async Task An_existing_translation_is_replaced_and_the_answer_says_so()
    {
        var document = Write("notes.txt", English);
        var target = Write("notes.ru.txt", "Прошлый перевод");

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(new RecordingClient(Russian)),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(LocalLmText.TranslateReplaced, answer, StringComparison.Ordinal);
        Assert.Contains(
            Russian,
            await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Checked before the run, because a missing directory found after twenty minutes of GPU
    /// work is the one failure with nothing left to recover.
    /// </summary>
    [Fact]
    public async Task An_output_directory_that_is_not_there_is_refused_before_the_run()
    {
        var document = Write("notes.txt", English);
        var target = Path.Combine(_root, "no-such-directory", "notes.ru.txt");
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            LocalLmText.TranslateOutputDirectoryMissing(target),
            answer,
            StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    [Fact]
    public async Task An_output_path_that_names_a_directory_is_refused()
    {
        var document = Write("notes.txt", English);
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            LocalLmText.TranslateOutputIsDirectory(_root),
            answer,
            StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    /// <summary>
    /// The console's refusal, in this face's words: the sentence takes the two names from
    /// whoever refuses, so a caller here is told to fix `path` and `outPath` rather than two
    /// options it does not have. Translating in place destroys the original and there is no undo.
    /// </summary>
    [Fact]
    public async Task A_translation_may_not_overwrite_the_document_it_came_from()
    {
        var document = Write("notes.md", English);
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: Path.Combine(_root, "..", Path.GetFileName(_root), "notes.md"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            LocalLmText.TranslateOutputIsSource("path", "outPath", document),
            answer,
            StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    /// <summary>
    /// The reader's own sentences reach this face unchanged - it is the same reader the console
    /// uses - and they arrive as the answer rather than as a protocol error, so a mistyped path
    /// does not look like a broken tool.
    /// </summary>
    [Fact]
    public async Task A_path_with_no_file_at_it_is_refused_in_the_readers_words()
    {
        var missing = Path.Combine(_root, "no-such.md");
        var client = new RecordingClient(Russian);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(client),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: missing,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(LocalLmText.TranslateFileMissing(missing), answer, StringComparer.Ordinal);
        Assert.Empty(client.Profiles);
    }

    /// <summary>
    /// A write that fails after the run does not fall back to returning the document: dumping a
    /// document the caller asked to be written into its context is the opposite of what it asked
    /// for. So the sentence has to say plainly that the translation is gone.
    /// </summary>
    [Fact]
    public async Task A_write_that_fails_says_the_translation_was_not_kept()
    {
        var document = Write("notes.txt", English);
        var target = Write("notes.ru.txt", "занято");

        using var exclusive = new FileStream(
            target, FileMode.Open, FileAccess.Read, FileShare.None);

        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(new RecordingClient(Russian)),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            path: document,
            outPath: target,
            cancellationToken: TestContext.Current.CancellationToken);

        // The reason is the framework's own message, so the sentence is asserted up to the
        // hole that carries it - which is what makes this hold in either language.
        var sentence = LocalLmText.TranslateNotWritten(target, "<reason>");
        Assert.Contains(
            sentence[..sentence.IndexOf("<reason>", StringComparison.Ordinal)],
            answer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Russian, answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text in hand keeps every word of its behaviour: same parameter, same wrapped answer, same
    /// origin. A caller written against the tool as it was sees no difference at all.
    /// </summary>
    [Fact]
    public async Task Text_in_hand_still_travels_the_route_it_always_did()
    {
        var answer = await LocalLmTools.TranslateLocal(
            new LocalTasks(new RecordingClient(Russian)),
            sourceLanguage: "English",
            targetLanguage: "Russian",
            source: English,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("origin=\"translate_local\"", answer, StringComparison.Ordinal);
        Assert.Contains(Russian, answer, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a test left open on a machine that has not released it yet is not a failure
            // of the thing under test.
        }
    }

    private sealed class RecordingClient(string answer) : ILocalModelClient
    {
        public List<LocalTaskProfile> Profiles { get; } = [];

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
            Profiles.Add(profile);
            return Task.FromResult(new LocalJobResult<string>(
                answer,
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
                        EstimatedGrossCloudTokensSaved: prompt.Length / 4,
                        EstimatedVerificationTokens: 0,
                        EstimatedNetCloudTokensSaved: prompt.Length / 4,
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
