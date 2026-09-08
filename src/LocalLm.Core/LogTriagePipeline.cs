using System.Runtime.CompilerServices;
using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// Streams an arbitrarily long log through one VRAM-admissible model/context pair. Source
/// fragments and reduction groups are bounded independently, so neither a large file nor a large
/// number of partial findings has to be retained in memory.
/// </summary>
internal sealed class LogTriagePipeline(
    ILocalModelClient client,
    LogTriagePolicy policy,
    ILocalRunObserver? observer = null)
{
    private const string DefaultQuestion =
        "What failed, and why? Give the exact file and line if the log names one.";
    private const string TriageInstruction =
        "You are triaging a build or test log for an engineer who will not read it themselves. " +
        "Report only failures the supplied material actually contains - never invent a failure " +
        "that is not there, and say plainly if nothing failed. Preserve exact file paths, line " +
        "numbers, timestamps, identifiers and error codes, and preserve the original language of " +
        "quoted messages.";

    /// <summary>
    /// No longer a constant, because the last sentence names the reader's language and that is
    /// known only at run time. The budget below measures this rather than the fixed half: a
    /// prompt allowance computed from a shorter string than the one actually sent is how a
    /// context limit is discovered by overrunning it.
    /// </summary>
    private static string SystemPrompt =>
        TriageInstruction + " " + AnswerLanguage.Instruction;

    public async Task<LocalResult> RunAsync(
        string? path,
        string? text,
        string? question,
        string? model,
        CancellationToken cancellationToken)
    {
        var source = ValidateSource(path, text);
        var focus = string.IsNullOrWhiteSpace(question) ? DefaultQuestion : question.Trim();
        // Before the probe, which loads a model to find out whether it fits: it unloads
        // others, preflights at a context size and steps down on failure. Minutes of
        // silence otherwise, before anything else about this run can be said.
        observer?.Report(new TriageChoosingModel());
        var capacity = await SelectCapacityAsync(model, cancellationToken);
        var promptBudget = InputCharacterBudget(capacity.ContextTokens);
        var availableCharacters =
            promptBudget - focus.Length - SystemPrompt.Length - policy.PromptOverheadCharacters;
        if (availableCharacters < 128)
        {
            throw new ArgumentException(
                LocalLmText.QuestionDoesNotFitContext,
                nameof(question));
        }

        var fragmentBudget = Math.Min(
            policy.MaximumFragmentCharacters,
            availableCharacters);
        var overlapCharacters = Math.Min(
            policy.MaximumOverlapCharacters,
            Math.Max(0, fragmentBudget / 16));
        var reductionBudget = Math.Min(
            policy.MaximumFragmentCharacters,
            availableCharacters);
        var maximumSummaryCharacters = MaximumSummaryCharacters(capacity.ContextTokens);
        var reducer = new HierarchicalReducer(
            reductionBudget,
            (summaries, level, ct) => ReduceAsync(
                summaries,
                level,
                focus,
                maximumSummaryCharacters,
                capacity,
                ct));

        long fragmentCount = 0;
        long originalTokens = 0;
        LocalUsageReceipt? lastReceipt = null;
        // Accumulated, not overwritten. A triage is one model call per fragment plus the
        // reduction rounds, and reporting the last one as the whole run understated a
        // hundred-second job as three seconds -- in a line whose purpose is to make the cost
        // of a local call visible. The saving beside it was accumulated all along.
        var queued = TimeSpan.Zero;
        var executed = TimeSpan.Zero;
        await foreach (var fragment in ReadFragmentsAsync(
                           source,
                           fragmentBudget,
                           overlapCharacters,
                           cancellationToken))
        {
            fragmentCount++;
            observer?.Report(new TriagingFragment(
                (int)fragmentCount,
                fragment.ConsumedBefore,
                fragment.Total));
            originalTokens = Math.Min(
                int.MaxValue,
                originalTokens + TokenEstimator.ForText(fragment.UniqueText));
            var result = await AnalyzeFragmentAsync(
                fragment.Content,
                fragmentCount,
                focus,
                maximumSummaryCharacters,
                capacity,
                cancellationToken);
            lastReceipt = result.Receipt;
            queued += result.Receipt.QueueDuration;
            executed += result.Receipt.ExecutionDuration;
            await reducer.AddAsync(result.Value, cancellationToken);
        }

        // The total, at the first moment it is true.
        observer?.Report(new TriageLogRead((int)fragmentCount));
        var reduced = await reducer.CompleteAsync(cancellationToken);
        if (reduced.Receipt is not null)
        {
            lastReceipt = reduced.Receipt;
            queued += reduced.Receipt.QueueDuration;
            executed += reduced.Receipt.ExecutionDuration;
        }

        var answer = reduced.Text;
        var receipt = (lastReceipt
            ?? throw new InvalidOperationException(LocalLmText.LogTriageNoReceipt))
            with { QueueDuration = queued, ExecutionDuration = executed };
        var detail = source.Path is null
            ? LocalLmText.LogTextRead(
                source.Text!.Length,
                fragmentCount,
                capacity.ContextTokens)
            : LocalLmText.LogFileRead(
                Path.GetFileName(source.Path),
                new FileInfo(source.Path).Length / 1024,
                fragmentCount,
                capacity.ContextTokens);
        return new LocalResult(
            answer,
            TokenEstimator.Saved((int)originalTokens, answer),
            capacity.Model,
            detail,
            receipt);
    }

    private async Task<Capacity> SelectCapacityAsync(
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        var status = (await client.GetModelsStatusAsync(cancellationToken)).Value;
        var catalog = ModelRoutingCatalogResource.LoadDocument();
        if (!string.Equals(
                status.CatalogVersion,
                catalog.CatalogVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                LocalLmText.CatalogMismatch(
                    catalog.CatalogVersion,
                    status.CatalogVersion));
        }

        var route = catalog.Routes.Single(candidate =>
            candidate.Profile == LocalTaskProfile.LogTriage);
        var permitted = route.Candidates
            .Concat(route.Fallbacks)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (modelOverride is not null &&
            !permitted.Contains(modelOverride, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                LocalLmText.ModelNotConfiguredForTriage(modelOverride),
                nameof(modelOverride));
        }

        var installed = status.InstalledModels.ToHashSet(StringComparer.Ordinal);
        var disabled = (status.DisabledContexts ?? [])
            .ToHashSet();
        var orderedModels = modelOverride is null
            ? OrderRouteModels(route, catalog, status)
            : [modelOverride];
        var attempted = new List<string>();
        foreach (var model in orderedModels)
        {
            var entry = catalog.Models.Single(candidate =>
                string.Equals(candidate.Tag, model, StringComparison.Ordinal));
            if (!installed.Contains(model) ||
                entry.Lifecycle == LocalModelLifecycle.Disabled ||
                !entry.Capabilities.Contains(LocalModelCapability.Text))
            {
                continue;
            }

            foreach (var contextTokens in entry.ContextTokens
                         .Where(value => value <= policy.MaximumContextTokens)
                         .OrderDescending())
            {
                if (disabled.Contains(new ModelContextRef(model, contextTokens)))
                {
                    continue;
                }

                attempted.Add($"{model}/{contextTokens}");
                try
                {
                    var proof = (await client.PreflightModelAsync(
                        model,
                        contextTokens,
                        catalog.CatalogVersion,
                        cancellationToken)).Value;
                    return new Capacity(proof.Model, proof.ContextTokens);
                }
                catch (BrokerJobFailedException exception) when (
                    exception.FailureCode is
                        "ModelPreflightException" or
                        nameof(InvalidOperationException))
                {
                    // ModelRuntime disables only this failed model/context pair. Trying the next
                    // smaller tier is the actual capacity probe, not a guessed hardware profile.
                }
            }
        }

        throw new InvalidOperationException(
            attempted.Count == 0
                ? LocalLmText.NoTriageModelNoneTried
                : LocalLmText.NoTriageModel(string.Join(", ", attempted)));
    }

    private static IReadOnlyList<string> OrderRouteModels(
        TaskRouteEntry route,
        ModelRoutingCatalogDocument catalog,
        LocalModelsStatusOutput status)
    {
        var experimentByModel = (status.Experiments ?? [])
            .Where(experiment => experiment.Profile == LocalTaskProfile.LogTriage)
            .ToDictionary(experiment => experiment.Model, StringComparer.Ordinal);
        var entries = catalog.Models.ToDictionary(model => model.Tag, StringComparer.Ordinal);
        var activeExperimental = route.Candidates.Where(tag =>
        {
            var entry = entries[tag];
            if (entry.Lifecycle != LocalModelLifecycle.Experimental)
            {
                return false;
            }

            return !experimentByModel.TryGetValue(tag, out var experiment) ||
                   (!experiment.IsPaused &&
                    !experiment.IsCircuitOpen &&
                    experiment.OwnerAction is not (
                        ExperimentOwnerAction.FallbackOnly or
                        ExperimentOwnerAction.Disable));
        });
        var established = route.Candidates.Where(tag =>
            entries[tag].Lifecycle != LocalModelLifecycle.Experimental ||
            experimentByModel.TryGetValue(tag, out var experiment) &&
            experiment.IsPromoted);
        return activeExperimental
            .Concat(established)
            .Concat(route.Fallbacks)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<LocalJobResult<string>> AnalyzeFragmentAsync(
        string content,
        long fragmentIndex,
        string question,
        int maximumSummaryCharacters,
        Capacity capacity,
        CancellationToken cancellationToken) =>
        await client.RoutedChatAsync(
            LocalTaskProfile.LogTriage,
            $"""
             {question}

             Analyze log fragment {fragmentIndex}. Return only relevant findings from this
             fragment. Keep the response under {maximumSummaryCharacters} characters.
             Preserve exact evidence. If the fragment contains no relevant failure, say so briefly.

             --- BEGIN LOG FRAGMENT {fragmentIndex} ---
             {content}
             --- END LOG FRAGMENT {fragmentIndex} ---
             """,
            SystemPrompt,
            null,
            new LocalWorkloadMetadata(
                content.Length,
                maximumSummaryCharacters,
                1,
                0,
                0,
                LocalDurationClass.Short),
            workflow: null,
            modelOverride: capacity.Model,
            requestedContextTokens: capacity.ContextTokens,
            LocalJobPriority.Foreground,
            cancellationToken);

    private async Task<ReducedSummary> ReduceAsync(
        IReadOnlyList<string> summaries,
        int level,
        string question,
        int maximumSummaryCharacters,
        Capacity capacity,
        CancellationToken cancellationToken)
    {
        // Merges also happen mid-stream when a level overflows its budget, so the line
        // this becomes must not read as "the log has been read".
        observer?.Report(new MergingPartials(summaries.Count, level));
        var body = new StringBuilder();
        for (var index = 0; index < summaries.Count; index++)
        {
            body.AppendLine($"--- PARTIAL FINDING {index + 1} ---")
                .AppendLine(summaries[index]);
        }

        var result = await client.RoutedChatAsync(
            LocalTaskProfile.LogTriage,
            $"""
             {question}

             Merge these ordered partial log findings (reduction level {level}). Deduplicate repeated
             overlap evidence, preserve every distinct failure and its exact evidence, and do not
             introduce claims absent from the partial findings. Return one concise diagnosis under
             {maximumSummaryCharacters} characters.

             {body}
             """,
            SystemPrompt,
            null,
            new LocalWorkloadMetadata(
                body.Length,
                maximumSummaryCharacters,
                summaries.Count,
                0,
                0,
                LocalDurationClass.Short),
            workflow: null,
            modelOverride: capacity.Model,
            requestedContextTokens: capacity.ContextTokens,
            LocalJobPriority.Foreground,
            cancellationToken);
        return new ReducedSummary(result.Value, result.Receipt);
    }

    private static async IAsyncEnumerable<LogFragment> ReadFragmentsAsync(
        LogSource source,
        int fragmentCharacters,
        int overlapCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (source.Text is not null)
        {
            var offset = 0;
            var overlap = string.Empty;
            if (source.Text.Length == 0)
            {
                yield return new LogFragment(string.Empty, string.Empty, 0, 0);
                yield break;
            }

            while (offset < source.Text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var freshLength = Math.Min(
                    Math.Max(1, fragmentCharacters - overlap.Length),
                    source.Text.Length - offset);
                freshLength = SurrogateSafe.Extend(
                    source.Text,
                    offset,
                    freshLength);
                var fresh = source.Text.Substring(offset, freshLength);
                var content = overlap + fresh;
                yield return new LogFragment(content, fresh, offset, source.Text.Length);
                offset += freshLength;
                overlap = Tail(content, overlapCharacters);
            }

            yield break;
        }

        await using var stream = new FileStream(
            source.Path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: false);
        var fileOverlap = string.Empty;
        var yielded = false;
        // Counted off the text handed out rather than read from the stream: both the FileStream
        // and the StreamReader buffer 64 KB, so the stream's position runs up to 128 KB ahead of
        // what has actually been analysed — on a 200 KB log the first fragment would report the
        // whole file. CurrentEncoding reflects a byte order mark the reader detected, so this sum
        // falls short of the file's length by the preamble at most: pessimistic, never optimistic.
        var consumed = 0L;
        var total = new FileInfo(source.Path!).Length;
        while (true)
        {
            var freshBudget = Math.Max(1, fragmentCharacters - fileOverlap.Length);
            var fresh = await ReadAtMostAsync(reader, freshBudget, cancellationToken);
            if (fresh.Length == 0)
            {
                if (!yielded)
                {
                    yield return new LogFragment(string.Empty, string.Empty, 0, total);
                }

                yield break;
            }

            yielded = true;
            var content = fileOverlap + fresh;
            yield return new LogFragment(content, fresh, consumed, total);
            consumed += reader.CurrentEncoding.GetByteCount(fresh);
            fileOverlap = Tail(content, overlapCharacters);
        }
    }

    private static async Task<string> ReadAtMostAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[maximumCharacters];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await reader.ReadAsync(
                buffer.AsMemory(read, buffer.Length - read),
                cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read > 0 && char.IsHighSurrogate(buffer[read - 1]))
        {
            var next = new char[1];
            if (await reader.ReadAsync(next, cancellationToken) == 1)
            {
                return new string(buffer, 0, read) + next[0];
            }
        }

        return new string(buffer, 0, read);
    }

    private static string Tail(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        var start = value.Length - maximumCharacters;
        if (start > 0 &&
            char.IsLowSurrogate(value[start]) &&
            char.IsHighSurrogate(value[start - 1]))
        {
            start--;
        }

        return value[start..];
    }

    private int InputCharacterBudget(int contextTokens)
    {
        var reservedTokens = ReservedContextTokens(contextTokens);
        return Math.Max(
            256,
            checked((int)Math.Floor(
                (contextTokens - reservedTokens) * policy.CharactersPerToken)));
    }

    private int MaximumSummaryCharacters(int contextTokens) => Math.Min(
        policy.MaximumPartialSummaryCharacters,
        Math.Max(
            256,
            checked((int)Math.Floor(
                ReservedContextTokens(contextTokens) * policy.CharactersPerToken))));

    private int ReservedContextTokens(int contextTokens) => Math.Min(
        policy.ReservedContextTokens,
        Math.Max(256, contextTokens / 3));

    private static LogSource ValidateSource(string? path, string? text)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasText = text is not null;
        if (hasPath == hasText)
        {
            throw new ArgumentException(LocalLmText.ExactlyOneSource);
        }

        if (!hasPath)
        {
            return new LogSource(null, text);
        }

        var full = Path.GetFullPath(path!);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"No such file: {full}", full);
        }

        return new LogSource(full, null);
    }


    private sealed record LogSource(string? Path, string? Text);

    /// <param name="ConsumedBefore">
    /// How much of the source the fragments before this one covered, in the source's own unit —
    /// characters for text, bytes for a file. Before rather than including, so the first fragment
    /// reports nothing consumed and no fragment reports the whole source: that the reading
    /// finished is a separate fact, and the line that says it is the one that says it.
    /// </param>
    private sealed record LogFragment(
        string Content,
        string UniqueText,
        long ConsumedBefore,
        long Total);

    private sealed record Capacity(string Model, int ContextTokens);

}
