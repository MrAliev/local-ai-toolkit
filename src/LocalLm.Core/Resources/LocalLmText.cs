using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalLm.Core.Resources;

/// <summary>
/// Everything the LocalLm tools say, in the language the reader's machine is set to.
///
/// Written by hand rather than generated. A generated accessor gives a property per key and
/// nothing else; these are format strings with holes whose order is part of the contract, and
/// naming the arguments at the one place that fills them is what stops the third argument of a
/// seven-hole sentence from quietly becoming the fourth.
///
/// Formatting is invariant on purpose, in every language. The numbers in these lines are quoted
/// verbatim by agents and parsed by tests, so `2.5` stays `2.5` on a machine that would write
/// `2,5` — only the words move. <see cref="LocalAi.Contracts.Localization.OutputCulture"/> says
/// why at greater length.
/// </summary>
public static class LocalLmText
{
    /// <summary>The strings themselves, and the rules for reading them.</summary>
    public static TextCatalogue Catalogue { get; } = new(
        "LocalLm.Core.Resources.LocalLmText",
        typeof(LocalLmText).Assembly);

    /// <summary>The raw string for a key, for callers that only need the words.</summary>
    public static string Get(string key) => Catalogue.Get(key);

    /// <summary>The keys a language carries, for the parity test.</summary>
    public static IReadOnlyCollection<string> Keys(CultureInfo culture) =>
        Catalogue.Keys(culture);

    /// <summary>The line every local tool returns, so a delegation is never silent.</summary>
    public static string Notice(
        string model,
        string residency,
        string detail,
        string duration,
        string saving) =>
        Format(nameof(Notice), model, residency, detail, duration, saving);

    /// <summary>The mark beside a model that did not fit in video memory.</summary>
    public static string ResidencyPartialOffload(int residentPercent) =>
        Format(nameof(ResidencyPartialOffload), residentPercent);

    public static string ResidencyCpu => Get(nameof(ResidencyCpu));

    /// <summary>Said once per process, not once per answer.</summary>
    public static string ResidencyAdvice => Get(nameof(ResidencyAdvice));

    /// <summary>How long it took, when the wait is worth naming apart from the work.</summary>
    public static string DurationWithQueue(string total, string queued) =>
        Format(nameof(DurationWithQueue), total, queued);

    public static string DurationSeconds(string seconds) =>
        Format(nameof(DurationSeconds), seconds);

    public static string ImagesRead(int count, string described) =>
        Format(nameof(ImagesRead), count, described);

    public static string PdfOnlyInput => Get(nameof(PdfOnlyInput));
    public static string PdfPagesOnly => Get(nameof(PdfPagesOnly));
    public static string PdfOutputOnly => Get(nameof(PdfOutputOnly));
    public static string PdfCheckpointOnly => Get(nameof(PdfCheckpointOnly));
    public static string PdfCheckpointMismatch(string path) => Format(nameof(PdfCheckpointMismatch), path);
    public static string TranslationCheckpointMismatch(string path) => Format(nameof(TranslationCheckpointMismatch), path);
    public static string TranslationCheckpointUnavailable(string path) => Format(nameof(TranslationCheckpointUnavailable), path);
    public static string TranslationCheckpointReturned => Get(nameof(TranslationCheckpointReturned));
    public static string TranslationCheckpointOutputConflict(string path) => Format(nameof(TranslationCheckpointOutputConflict), path);
    public static string PdfCheckpointUnavailable(string path) => Format(nameof(PdfCheckpointUnavailable), path);
    public static string PdfCheckpointReused(int count) => Format(nameof(PdfCheckpointReused), count);
    public static string PdfCheckpointOutputConflict(string path) => Format(nameof(PdfCheckpointOutputConflict), path);
    public static string PdfOutputIsSource(string path) => Format(nameof(PdfOutputIsSource), path);
    public static string PdfOutputIsDirectory(string path) => Format(nameof(PdfOutputIsDirectory), path);
    public static string PdfOutputDirectoryMissing(string path) => Format(nameof(PdfOutputDirectoryMissing), path);
    public static string PdfOutputNotWritten(string path, string reason) => Format(nameof(PdfOutputNotWritten), path, reason);
    public static string PdfOutputWritten(string path, int characters) => Format(nameof(PdfOutputWritten), path, characters);
    public static string PdfPagesInvalid => Get(nameof(PdfPagesInvalid));
    public static string PdfPagesOutsideDocument(int count) => Format(nameof(PdfPagesOutsideDocument), count);
    public static string PdfTogetherUnsupported => Get(nameof(PdfTogetherUnsupported));
    public static string PdfTooLarge(string path, long limitMiB) => Format(nameof(PdfTooLarge), path, limitMiB);
    public static string PdfUnreadable(string path, string reason) => Format(nameof(PdfUnreadable), path, reason);
    public static string PdfPagesRead(string filename, int count) => Format(nameof(PdfPagesRead), filename, count);
    public static string PdfPageHeading(int page, int count) => Format(nameof(PdfPageHeading), page, count);

    /// <summary>
    /// The counted form's detail, and separate from <see cref="ImagesRead"/> on purpose: one look
    /// at five images can compare them and five merged readings cannot, so the two sentences are
    /// different claims about what the answer supports.
    /// </summary>
    public static string ImagesReadOneAtATime(int count, string described) =>
        Format(nameof(ImagesReadOneAtATime), count, described);

    public static string FilesProcessed(int count, string names, string more) =>
        Format(nameof(FilesProcessed), count, names, more);

    public static string PromptOnly => Get(nameof(PromptOnly));

    public static string InputTruncated(int characterBudget) =>
        Format(nameof(InputTruncated), characterBudget);

    public static string FilesSkipped(int omitted) => Format(nameof(FilesSkipped), omitted);

    public static string LogTextRead(int characters, long fragments, int contextTokens) =>
        Format(nameof(LogTextRead), characters, fragments, contextTokens);

    public static string LogFileRead(
        string fileName,
        long kilobytes,
        long fragments,
        int contextTokens) =>
        Format(nameof(LogFileRead), fileName, kilobytes, fragments, contextTokens);

    public static string TranslationNotice(
        string model,
        string residency,
        string validation,
        string duration,
        string processedLocally,
        string generationSaved,
        string contextSaved) =>
        Format(
            nameof(TranslationNotice),
            model,
            residency,
            validation,
            duration,
            processedLocally,
            generationSaved,
            contextSaved);

    public static string TranslationNoTranslatableText => Get(nameof(TranslationNoTranslatableText));

    public static string TranslationNotRequired => Get(nameof(TranslationNotRequired));

    public static string SavingUnderHalfK => Get(nameof(SavingUnderHalfK));

    public static string SavedNothing => Get(nameof(SavedNothing));

    public static string SavedNegligible => Get(nameof(SavedNegligible));

    public static string SavedAbout(string band) => Format(nameof(SavedAbout), band);

    public static string TranslationFailed(string reason) =>
        Format(nameof(TranslationFailed), reason);

    public static string FileNotFound(string path) => Format(nameof(FileNotFound), path);

    public static string InvalidRequest(string reason) => Format(nameof(InvalidRequest), reason);

    public static string LocalModelFailed(string reason) =>
        Format(nameof(LocalModelFailed), reason);

    /// <summary>
    /// What a model-management tool says when it has nothing to return.
    ///
    /// Not "failed": the commonest case is the product working - the experiment gate
    /// declining feedback before ten completed tasks, so nobody judges a model on a first
    /// impression. Not "refused" either, because the code cannot tell that apart from a
    /// fault: the broker records the exception type name as the failure code, and the gate
    /// throws a plain InvalidOperationException. No answer is what is true of every branch,
    /// and the reason that follows is written by whatever knows which one it was.
    /// </summary>
    public static string NoAnswerFromTool(string tool, string reason) =>
        Format(nameof(NoAnswerFromTool), tool, reason);

    /// <summary>
    /// What a tool says when the request itself cannot be run. These reach the caller as the
    /// tool's answer rather than as a protocol error, so they are read by the same person as
    /// the notice line and belong in the same language.
    /// </summary>
    public static string ImageProfileUnsupported => Get(nameof(ImageProfileUnsupported));

    public static string NoImagePaths => Get(nameof(NoImagePaths));

    public static string TooManyImages(int given, int limit) =>
        Format(nameof(TooManyImages), given, limit);

    public static string NotAnImage(string path) => Format(nameof(NotAnImage), path);

    public static string ImageTooLarge(string path, long megabytes, long limit) =>
        Format(nameof(ImageTooLarge), path, megabytes, limit);

    public static string ImagesTooLargeTogether(long limitMegabytes) =>
        Format(nameof(ImagesTooLargeTogether), limitMegabytes);

    public static string ImagesTooManyPixels(long limit) =>
        Format(nameof(ImagesTooManyPixels), limit);

    public static string NotATextChatProfile(LocalTaskProfile profile) =>
        Format(nameof(NotATextChatProfile), profile);

    /// <summary>
    /// The guard both surfaces cross, and the only one <c>ask_local</c> has. Separate from
    /// <see cref="NotATextChatProfile"/>, which stays for the seven profiles that really are not
    /// text-chat ones: naming a cause that is false is the mistake its sibling was corrected for.
    /// </summary>
    public static string ProfileHasItsOwnPipeline(LocalTaskProfile profile) =>
        Format(nameof(ProfileHasItsOwnPipeline), profile);

    public static string TooManyFiles(int given, int limit) =>
        Format(nameof(TooManyFiles), given, limit);

    public static string TranslationNoReceipt => Get(nameof(TranslationNoReceipt));

    public static string TranslationValidationFailed(string detail) =>
        Format(nameof(TranslationValidationFailed), detail);

    /// <summary>
    /// What reading a document to translate can refuse with, and what writing one can say.
    ///
    /// These live here rather than in the console's catalogue because both faces read files
    /// now: <c>localai translate --in</c> and <c>translate_local</c>'s <c>path</c> go through
    /// one reader, <see cref="LocalLm.Core.TranslateSource"/>, and a second copy of these
    /// sentences would drift exactly where drift matters — the wording of a refusal. The
    /// console keeps the sentences that name a console thing: an option, standard input, a
    /// usage line.
    /// </summary>
    public static string TranslateFileMissing(string path) =>
        Format(nameof(TranslateFileMissing), path);

    public static string TranslateNotAFile(string path) =>
        Format(nameof(TranslateNotAFile), path);

    public static string TranslateFileEmpty(string path) =>
        Format(nameof(TranslateFileEmpty), path);

    public static string TranslateFileNotText(string path) =>
        Format(nameof(TranslateFileNotText), path);

    public static string TranslateFileNotRead(string path, string reason) =>
        Format(nameof(TranslateFileNotRead), path, reason);

    /// <summary>
    /// The two names are the caller's own: the console passes <c>--in</c> and <c>--out</c>,
    /// <c>translate_local</c> passes <c>path</c> and <c>outPath</c>. Naming a parameter the
    /// reader's face does not have is the mistake <c>ParseProfile</c> already carries a comment
    /// about.
    /// </summary>
    public static string TranslateOutputIsSource(string input, string output, string path) =>
        Format(nameof(TranslateOutputIsSource), input, output, path);

    public static string TranslateSourceAmbiguous => Get(nameof(TranslateSourceAmbiguous));

    public static string TranslateSourceMissing => Get(nameof(TranslateSourceMissing));

    public static string TranslateOutputIsDirectory(string path) =>
        Format(nameof(TranslateOutputIsDirectory), path);

    public static string TranslateOutputDirectoryMissing(string path) =>
        Format(nameof(TranslateOutputDirectoryMissing), path);

    public static string TranslateNeedsOutPath(string path, int characters, int limit) =>
        Format(nameof(TranslateNeedsOutPath), path, characters, limit);

    public static string TranslateMarkdownOn(string path) =>
        Format(nameof(TranslateMarkdownOn), path);

    public static string TranslateWritten(string path, long kilobytes, int characters) =>
        Format(nameof(TranslateWritten), path, kilobytes, characters);

    public static string TranslateReplaced => Get(nameof(TranslateReplaced));

    public static string TranslateNotWritten(string path, string reason) =>
        Format(nameof(TranslateNotWritten), path, reason);

    /// <summary>What to tell somebody whose task has no model to run on.</summary>
    public static string NoModelAndCatalogNamesNone(LocalTaskProfile profile) =>
        Format(nameof(NoModelAndCatalogNamesNone), profile);

    public static string NoModelInstalledWithInstall(
        LocalTaskProfile profile,
        string models,
        string firstModel,
        string catalogVersion) =>
        Format(
            nameof(NoModelInstalledWithInstall),
            profile,
            models,
            firstModel,
            catalogVersion);

    public static string NoModelInstalled(LocalTaskProfile profile, string models) =>
        Format(nameof(NoModelInstalled), profile, models);

    public static string IneligibleWithModels(LocalTaskProfile profile, string models) =>
        Format(nameof(IneligibleWithModels), profile, models);

    public static string Ineligible(LocalTaskProfile profile) =>
        Format(nameof(Ineligible), profile);

    public static string QuestionDoesNotFitContext => Get(nameof(QuestionDoesNotFitContext));

    public static string LogTriageNoReceipt => Get(nameof(LogTriageNoReceipt));

    public static string CatalogMismatch(string client, string broker) =>
        Format(nameof(CatalogMismatch), client, broker);

    public static string CatalogMismatchRecovery => Get(nameof(CatalogMismatchRecovery));

    public static string ModelNotConfiguredForTriage(string model) =>
        Format(nameof(ModelNotConfiguredForTriage), model);

    public static string NoTriageModelNoneTried => Get(nameof(NoTriageModelNoneTried));

    public static string NoTriageModel(string attempted) =>
        Format(nameof(NoTriageModel), attempted);

    public static string ExactlyOneSource => Get(nameof(ExactlyOneSource));

    /// <summary>Not a failure: the answer triage_log gives for a log with nothing in it.</summary>
    public static string EmptyLog => Get(nameof(EmptyLog));

    public static string UnknownValue(string parameter, string value) =>
        Format(nameof(UnknownValue), parameter, value);

    /// <summary>
    /// What the translation validator found, lowercase because each one lands after a colon
    /// inside a sentence the notice line already started. The successful ones reach every
    /// translated answer, which is how a Russian notice line came to end in an English clause.
    /// </summary>
    public static string ValidatorEmptyTranslation => Get(nameof(ValidatorEmptyTranslation));

    public static string ValidatorUnexpectedFence => Get(nameof(ValidatorUnexpectedFence));

    public static string ValidatorPromptLeak => Get(nameof(ValidatorPromptLeak));

    public static string ValidatorExpanded(int from, int to) =>
        Format(nameof(ValidatorExpanded), from, to);

    public static string ValidatorPlausible => Get(nameof(ValidatorPlausible));

    public static string ValidatorStructurePreserved => Get(nameof(ValidatorStructurePreserved));

    public static string ValidatorCountMismatch(string what, int expected, int actual) =>
        Format(nameof(ValidatorCountMismatch), what, expected, actual);

    public static string ValidatorProtectedTokensChanged(string what) =>
        Format(nameof(ValidatorProtectedTokensChanged), what);

    public static string ValidatorHeadings => Get(nameof(ValidatorHeadings));

    public static string ValidatorFenceMarkers => Get(nameof(ValidatorFenceMarkers));

    public static string ValidatorListMarkers => Get(nameof(ValidatorListMarkers));

    public static string ValidatorFencedCode => Get(nameof(ValidatorFencedCode));

    public static string ValidatorInlineCode => Get(nameof(ValidatorInlineCode));

    public static string ValidatorUrls => Get(nameof(ValidatorUrls));

    public static string ValidatorPlaceholders => Get(nameof(ValidatorPlaceholders));

    private static string Format(string key, params object?[] arguments) =>
        Catalogue.Format(key, arguments);
}
