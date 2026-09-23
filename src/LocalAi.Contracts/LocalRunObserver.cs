namespace LocalAi.Contracts;

/// <summary>
/// Told what a long run is doing while it is still doing it.
///
/// The steps carry facts and no words: the console owns the sentences, because it is the only
/// face that prints them. An MCP server passes no observer and stays silent — progress on a
/// stdio server's standard error lands in the host's log for no reader's benefit.
///
/// Deliberately not <c>IProgress&lt;T&gt;</c>. With no synchronization context that posts to the
/// thread pool, and two lines can then arrive in the wrong order — which in a transcript is
/// indistinguishable from the run doing something else.
/// </summary>
public interface ILocalRunObserver
{
    void Report(LocalRunStep step);
}

/// <summary>One thing that happened during a run, named rather than worded.</summary>
public abstract record LocalRunStep;

/// <summary>
/// The job is in the broker and has not finished. Reported on every poll of the queue — ten
/// times a second — so the console can tell waiting apart from working without a clock of its
/// own down here. Whether any of them becomes a line is the console's decision.
/// </summary>
public sealed record BrokerJobPending(bool Running) : LocalRunStep;

/// <summary>
/// How far a download has got, as the broker wrote it down.
///
/// Two figures and no percent: <paramref name="Total"/> is the sum of the layer sizes the
/// backend has named so far, so it grows as layers appear, and a percent against a growing
/// denominator goes backwards. <paramref name="Phase"/> is one of ours - preparing, downloading,
/// verifying, storing, other - and <paramref name="Detail"/> carries the backend's own word only
/// when the phase is other.
/// </summary>
public sealed record ModelDownloadProgress(
    string Phase,
    string? Detail,
    long Completed,
    long Total) : LocalRunStep;

/// <summary>
/// About to translate one fragment of <paramref name="Total"/>. Reported before the call rather
/// than after it: the total is known before the first one, and it is the fact that decides
/// whether the reader waits.
/// </summary>
public sealed record TranslatingFragment(int Index, int Total) : LocalRunStep;

/// <summary>
/// Translation checks failed, so the document is retried with validated checkpoint reuse and
/// another model. Without this the counter restarting at 1 reads as a defect.
/// </summary>
public sealed record TranslationRetrying(string Detail, string Model) : LocalRunStep;

/// <summary>
/// Before the capacity probe, which loads a model to find out whether it fits. Minutes of
/// silence otherwise, before anything else about the run can be said.
/// </summary>
public sealed record TriageChoosingModel : LocalRunStep;

/// <summary>
/// About to analyse one fragment, and how much of the source is behind it.
///
/// Still no total of fragments: they are streamed off the log, and that count is not known until
/// it ends. <paramref name="Consumed"/> against <paramref name="Total"/> is the denominator that
/// does exist — the position reached in the source against its length — and unlike a download's
/// total this one does not grow, which is why a percent is right here and refused in
/// <see cref="ModelDownloadProgress"/>. Both are in the source's own unit: bytes for a file,
/// characters for text piped in. Only their ratio is ever printed, so the two units never meet on
/// screen. Consumed covers what the fragments already analysed took, so it is 0 on the first and
/// never reaches Total: the fact that the reading finished is <see cref="TriageLogRead"/>.
/// </summary>
public sealed record TriagingFragment(int Index, long Consumed, long Total) : LocalRunStep;

/// <summary>
/// Every image in one call, which is what the together form asks for and what a single image gets.
///
/// Not a line of its own: there is nothing to count inside one call, so the console keeps the
/// number and names the work in the heartbeat it was going to print anyway, and a run that answers
/// in three seconds still says nothing. Kept when the counted form arrived rather than replaced by
/// it — a question that spans the images cannot be answered from readings merged afterwards.
/// </summary>
public sealed record ReadingImages(int Count) : LocalRunStep;

/// <summary>
/// About to read image <paramref name="Index"/> of <paramref name="Total"/>, one image per call.
///
/// The counter is honest here in a way it would not have been over a single call carrying all of
/// them: each call finishes, so the figure moves at the model's pace rather than at the encoder's.
/// A run over one image never reports this — one image is one call, and it takes
/// <see cref="ReadingImages"/> like the together form.
/// </summary>
public sealed record ReadingImage(int Index, int Total) : LocalRunStep;

/// <summary>About to render and read one PDF page, with its original page number.</summary>
public sealed record ReadingPdfPage(int Index, int Total) : LocalRunStep
{
    public int? SelectedIndex { get; init; }
    public int? SelectedTotal { get; init; }
}

/// <summary>The same fact for a question asked over files, which is also one call.</summary>
public sealed record AskingOverFiles(int Count) : LocalRunStep;

/// <summary>The total, at the first moment it is true.</summary>
public sealed record TriageLogRead(int Fragments) : LocalRunStep;

/// <summary>
/// Merging partial answers. Also happens mid-stream when a level overflows its budget, so this
/// must not be read as "everything has been read".
///
/// Named for the reducer rather than for triage, which is where it started: reading images one at
/// a time ends in the same merge, and "partial findings" is a log's word for what a screenshot
/// contains.
/// </summary>
public sealed record MergingPartials(int Parts, int Level) : LocalRunStep;
