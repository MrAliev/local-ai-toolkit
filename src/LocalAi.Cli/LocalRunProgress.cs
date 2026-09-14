using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>
/// Turns the steps of a long run into console lines, and — most of what it does — decides which
/// steps do not become one.
///
/// One object owns the clock because the two cadence rules share it: a step prints when it
/// happens, and a heartbeat prints only into silence. Two independent timers would double-print,
/// and a reader cannot tell a doubled line from a repeated step.
///
/// Every line is written whole. There is no carriage return anywhere in this product's output,
/// and this is not the place to introduce one: a self-rewriting line looks tidy on a terminal and
/// fills a redirected log with a hundred copies of itself, and this console is driven by hooks
/// and agents at least as often as by people.
/// </summary>
public sealed class LocalRunProgress : ILocalRunObserver
{
    /// <summary>
    /// Below ten seconds a person is still waiting rather than wondering, and a log gains nothing
    /// from a line the answer immediately follows.
    /// </summary>
    private static readonly TimeSpan FirstSilence = TimeSpan.FromSeconds(10);

    /// <summary>Once a run has spoken, silence has to be longer before it is worth breaking.</summary>
    private static readonly TimeSpan LaterSilence = TimeSpan.FromSeconds(30);

    private readonly TextWriter writer;
    private readonly Func<DateTimeOffset> clock;
    private readonly DateTimeOffset startedAt;

    /// <summary>
    /// What the run is about, when a line needs to name it. Only a download does: every
    /// other line here is about the run the reader started a moment ago and can see.
    /// </summary>
    private readonly string subject;

    private string? downloadPhase;
    private string? downloadDetail;

    private DateTimeOffset? lastLineAt;
    private bool? running;
    private DateTimeOffset stateSince;
    private DateTimeOffset? firstUnitAt;

    /// <summary>
    /// What the one call carries, when the run said so. Kept rather than printed: naming it is
    /// not an event a reader waits for, and a run that answers in three seconds should stay
    /// silent. It is spent on the heartbeat the silence clock was going to print anyway.
    /// </summary>
    private int? workingImages;
    private int? workingFiles;

    private DateTimeOffset? triageFirstAt;
    private long? triageBaseline;

    public LocalRunProgress(
        TextWriter writer,
        Func<DateTimeOffset> clock,
        string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.writer = writer;
        this.clock = clock;
        this.subject = subject ?? string.Empty;
        startedAt = clock();
        stateSince = startedAt;
    }

    public void Report(LocalRunStep step)
    {
        var now = clock();
        switch (step)
        {
            case BrokerJobPending pending:
                ReportPending(pending, now);
                break;

            case ModelDownloadProgress download:
                ReportDownload(download, now);
                break;

            case TranslatingFragment fragment:
                ReportCounted(
                    fragment.Index,
                    fragment.Total,
                    CliText.ProgressTranslatingFragment,
                    now);
                break;

            case ReadingImage image:
                ReportCounted(
                    image.Index,
                    image.Total,
                    CliText.ProgressReadingImage,
                    now);
                break;

            case ReadingPdfPage page:
                ReportCounted(page.Index, page.Total, CliText.ProgressReadingPdfPage, now);
                break;

            case TranslationRetrying retry:
                // The pass starts over, so the mean the estimate is built from has to as well:
                // carrying the old one across would understate a run that has just doubled.
                firstUnitAt = null;
                Write(CliText.ProgressTranslationRetry(retry.Detail, retry.Model), now);
                break;

            case TriageChoosingModel:
                Write(CliText.ProgressTriageChoosingModel, now);
                break;

            case TriagingFragment triaged:
                ReportTriage(triaged, now);
                break;

            case ReadingImages images:
                workingImages = images.Count;
                break;

            case AskingOverFiles files:
                workingFiles = files.Count;
                break;

            case TriageLogRead read:
                Write(CliText.ProgressTriageLogRead(read.Fragments), now);
                break;

            case MergingPartials merging:
                Write(CliText.ProgressMerging(merging.Parts, merging.Level), now);
                break;
        }
    }

    /// <summary>
    /// Reported ten times a second, so almost every one of these is silence. The state is read
    /// rather than guessed: a line saying the model is working while the job has not left the
    /// queue is the false reassurance the pair exists to refuse.
    /// </summary>
    private void ReportPending(BrokerJobPending pending, DateTimeOffset now)
    {
        // The first observation happens as the job is enqueued, so the state it reports began
        // when the run did. Only a later change restarts the clock this line reads.
        if (running is not null && running != pending.Running)
        {
            stateSince = now;
        }

        running = pending.Running;
        if (!SilenceEarnsALine(now))
        {
            return;
        }

        var inState = WholeSeconds(now - stateSince);
        // Queued is queued whatever the job carries: naming the cargo there would say nothing
        // about the wait. Working is where the size of what was sent is the fact that tells a
        // reader whether forty seconds is ordinary.
        Write(
            pending.Running
                ? workingImages is { } images
                    ? CliText.ProgressRunningImages(images, inState)
                    : workingFiles is { } files
                        ? CliText.ProgressRunningFiles(files, inState)
                        : CliText.ProgressRunning(inState)
                : CliText.ProgressQueued(inState),
            now);
    }

    /// <summary>
    /// The running heartbeat for the job it belongs to, which is why nothing else prints
    /// one beside it: "the model is working" would be false of a download.
    ///
    /// A change of phase does not wait for the clock — it answers a different question
    /// from a byte count. The first phase still does, because below ten seconds a reader
    /// is waiting rather than wondering, and a line the answer follows says nothing.
    /// </summary>
    private void ReportDownload(ModelDownloadProgress download, DateTimeOffset now)
    {
        var moved = !string.Equals(downloadPhase, download.Phase, StringComparison.Ordinal) ||
                    !string.Equals(downloadDetail, download.Detail, StringComparison.Ordinal);
        downloadPhase = download.Phase;
        downloadDetail = download.Detail;
        if (!(moved && lastLineAt is not null) && !SilenceEarnsALine(now))
        {
            return;
        }

        var elapsed = WholeSeconds(now - startedAt);
        Write(
            download.Phase switch
            {
                "downloading" => CliText.ProgressPullDownloading(
                    subject,
                    Gigabytes(download.Completed),
                    Gigabytes(download.Total)),
                "preparing" => CliText.ProgressPullPreparing(subject, elapsed),
                "verifying" => CliText.ProgressPullVerifying(elapsed),
                "storing" => CliText.ProgressPullStoring(elapsed),
                _ => CliText.ProgressPullStatus(download.Detail ?? string.Empty, elapsed),
            },
            now);
    }

    /// <summary>
    /// Ungated, like every other step line: it is the only proof the stream is advancing, and
    /// under the silence clock a run whose fragments are quick and one that has stalled would
    /// look the same.
    /// </summary>
    private void ReportTriage(TriagingFragment fragment, DateTimeOffset now)
    {
        var left = string.Empty;
        if (triageFirstAt is { } first && triageBaseline is { } baseline &&
            fragment.Consumed > baseline && fragment.Total > fragment.Consumed)
        {
            // A mean over the log actually analysed, so the merges that happened between
            // fragments are already inside it. The cascade after the last one is not, which is
            // the other reason the sentence says about.
            var mean = (now - first) / (fragment.Consumed - baseline);
            var remaining = mean * (fragment.Total - fragment.Consumed);
            left = CliText.ProgressMinutesLeft(
                remaining.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture));
        }

        triageFirstAt ??= now;
        triageBaseline ??= fragment.Consumed;
        Write(
            CliText.ProgressTriageFragment(
                fragment.Index,
                Percent(fragment.Consumed, fragment.Total),
                left),
            now);
    }

    /// <summary>
    /// Integer division, so it rounds down and a run with one fragment left says 99 rather than
    /// 100. Zero when there is nothing to divide by: an empty log yields one empty fragment.
    /// </summary>
    private static int Percent(long consumed, long total) =>
        total <= 0 ? 0 : (int)(consumed * 100 / total);

    /// <summary>
    /// Gibibytes to one decimal, invariant. The same unit the operating system shows for a
    /// file, so the figure matches what the reader will find on disk.
    /// </summary>
    private static string Gigabytes(long bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>
    /// A unit of a run whose total is known before the first one: a translation fragment, an
    /// image, and — when it arrives — a page. Ungated, like every other step line: it is the proof
    /// the stream is advancing, and under the silence clock a run whose units are quick and one
    /// that has stalled look the same.
    ///
    /// The estimate is a mean over the units that have finished, so the merge that follows the
    /// last one is outside it — the other reason the clause says about. The total on screen and
    /// the denominator of the estimate are deliberately the same number: one that counted the
    /// merge would disagree with the line carrying it.
    /// </summary>
    private void ReportCounted(
        int index,
        int total,
        Func<int, int, string, string> sentence,
        DateTimeOffset now)
    {
        var left = string.Empty;
        if (index > 1 && firstUnitAt is { } first && index <= total)
        {
            // A mean over what has finished, not a promise: the units are whole model calls and
            // their durations differ, which is why the sentence says "about".
            var mean = (now - first) / (index - 1);
            var remaining = mean * (total - index + 1);
            left = CliText.ProgressMinutesLeft(
                remaining.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture));
        }

        if (index == 1)
        {
            firstUnitAt = now;
        }

        // The position answers "what is it working on" now, so a remembered cargo count would be a
        // second answer to the same question — and a false one: this call carries one of them.
        workingImages = null;
        workingFiles = null;
        Write(sentence(index, total, left), now);
    }

    private bool SilenceEarnsALine(DateTimeOffset now) =>
        lastLineAt is { } last
            ? now - last >= LaterSilence
            : now - startedAt >= FirstSilence;

    private void Write(string line, DateTimeOffset now)
    {
        writer.WriteLine(line);
        lastLineAt = now;
    }

    /// <summary>
    /// Whole seconds, invariant. These lines are quoted verbatim out of redirected logs, and the
    /// notice beside them already prints whole seconds — two roundings would read as two clocks.
    /// </summary>
    private static int WholeSeconds(TimeSpan elapsed) =>
        (int)Math.Max(0, elapsed.TotalSeconds);
}
