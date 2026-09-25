using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ModelValidationResult(
    bool Passed,
    ModelExecutionOutcome Outcome,
    string Code)
{
    public StructuredValidationResult? StructuredValidation { get; init; }

    public static ModelValidationResult Pass(string code) =>
        new(true, ModelExecutionOutcome.Success, code);

    public static ModelValidationResult Fail(
        ModelExecutionOutcome outcome,
        string code)
    {
        if (outcome == ModelExecutionOutcome.Success)
        {
            throw new ArgumentException(
                "Failed validation requires a failure outcome.",
                nameof(outcome));
        }

        return new ModelValidationResult(false, outcome, code);
    }
}

public sealed class ModelExecutionCoordinator
{
    private static readonly TimeSpan WarmProofLifetime = TimeSpan.FromMinutes(1);

    private readonly ModelRouter _router;
    private readonly IModelRuntime _runtime;
    private readonly ExperimentStateStore _experiments;
    private readonly ModelTelemetryStore _telemetry;
    private readonly Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> _execute;
    private readonly Func<LocalJobRequest, ModelSelection, BrokerExecutionResult,
        ModelValidationResult> _validate;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _diagnostic;
    private ModelResidencyProof? _warmProof;

    public ModelExecutionCoordinator(
        ModelRouter router,
        IModelRuntime runtime,
        ExperimentStateStore experiments,
        ModelTelemetryStore telemetry,
        Func<LocalJobRequest, CancellationToken, Task<BrokerExecutionResult>> execute,
        Func<LocalJobRequest, ModelSelection, BrokerExecutionResult, ModelValidationResult>?
            validate = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnostic = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _experiments = experiments ?? throw new ArgumentNullException(nameof(experiments));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        // The request is passed because a check has to see what was asked, not only what came
        // back: the material a code reference is checked against travels in the prompt.
        _validate = validate ?? ((_, _, _) => ModelValidationResult.Pass("none"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnostic = diagnostic;
    }

    public async Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        CancellationToken cancellationToken = default)
    {
        var selection = await SelectAsync(
            request,
            availability,
            cancellationToken);
        return await ExecuteAsync(
            request,
            availability,
            selection,
            cancellationToken);
    }

    public async Task<ModelSelection> SelectAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availability);
        var chat = request.Payload as ChatJobPayload
            ?? throw new ArgumentException(
                "Model execution coordinator currently accepts routed chat jobs.",
                nameof(request));
        var profile = chat.TaskProfile
            ?? throw new ArgumentException("Chat job is not routed.", nameof(request));
        var workload = chat.Workload
            ?? throw new ArgumentException(
                "Routed chat has no workload metadata.",
                nameof(request));
        _router.RequireCatalogVersion(chat.Workflow?.CatalogVersion);
        var context = chat.RequestedContextTokens ?? 2048;
        var experiments = await _experiments.LoadAsync(cancellationToken);
        return _router.Select(
            new ModelRoutingRequest(
                profile,
                context,
                chat.Model,
                workload),
            availability,
            experiments)
            ?? throw new InvalidOperationException(
                $"Task profile '{profile}' is deterministic.");
    }

    public async Task<BrokerExecutionResult> ExecuteAsync(
        LocalJobRequest request,
        ModelAvailability availability,
        ModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(selection);
        if (_warmProof is { } proof && !availability.ResidentModels.Contains(proof.Model))
        {
            InvalidateResidencyProof();
        }
        var chat = request.Payload as ChatJobPayload
            ?? throw new ArgumentException(
                "Model execution coordinator currently accepts routed chat jobs.",
                nameof(request));
        var profile = chat.TaskProfile
            ?? throw new ArgumentException("Chat job is not routed.", nameof(request));
        var workload = chat.Workload
            ?? throw new ArgumentException(
                "Routed chat has no workload metadata.",
                nameof(request));
        _router.RequireCatalogVersion(chat.Workflow?.CatalogVersion);
        _router.RequireCatalogVersion(selection.CatalogVersion);
        var context = chat.RequestedContextTokens ?? 2048;
        if (selection.Profile != profile ||
            selection.ContextTokens != context ||
            selection.Workload != workload)
        {
            throw new ArgumentException(
                "Prepared model selection does not match the routed request.",
                nameof(selection));
        }

        var workflowOwnsExperimentOutcome = chat.Workflow is not null;
        var experiments = await _experiments.LoadAsync(cancellationToken);
        AttemptResult primary;
        try
        {
            primary = await ExecuteAttemptAsync(
                request,
                selection,
                workload,
                cancellationToken);
        }
        catch (ModelPreflightException preflight)
        {
            if (!workflowOwnsExperimentOutcome)
            {
                experiments = RecordIfExperimental(
                    experiments,
                    selection,
                    preflight.Outcome);
                await _experiments.SaveAsync(experiments, cancellationToken);
            }

            ModelSelection preflightFallback;
            try
            {
                preflightFallback = _router.SelectFallback(
                    selection,
                    preflight.Outcome,
                    availability);
            }
            catch (InvalidOperationException)
            {
                throw preflight;
            }

            var recovered = await ExecuteAttemptAsync(
                request,
                preflightFallback,
                workload,
                cancellationToken);
            return AnnotateExperimentalFailure(
                recovered.Result,
                selection,
                preflight.Outcome);
        }
        catch (ModelAttemptExecutionException)
        {
            const ModelExecutionOutcome outcome =
                ModelExecutionOutcome.TechnicalFailure;
            if (!workflowOwnsExperimentOutcome)
            {
                experiments = RecordIfExperimental(
                    experiments,
                    selection,
                    outcome);
                await _experiments.SaveAsync(experiments, cancellationToken);
            }

            var technicalFallback = _router.SelectFallback(
                selection,
                outcome,
                availability);
            var recovered = await ExecuteAttemptAsync(
                request,
                technicalFallback,
                workload,
                cancellationToken);
            return AnnotateExperimentalFailure(
                recovered.Result,
                selection,
                outcome);
        }

        if (!workflowOwnsExperimentOutcome)
        {
            experiments = RecordIfExperimental(
                experiments,
                selection,
                primary.Validation.Outcome);
            await _experiments.SaveAsync(experiments, cancellationToken);
        }

        if (primary.Validation.Passed)
        {
            return primary.Result;
        }

        var validationFallback = _router.SelectFallback(
            selection,
            primary.Validation.Outcome,
            availability);
        var fallbackAttempt = await ExecuteAttemptAsync(
            request,
            validationFallback,
            workload,
            cancellationToken);
        return AnnotateExperimentalFailure(
            fallbackAttempt.Result,
            selection,
            primary.Validation.Outcome);
    }

    /// <summary>A proof describes one model load, and cannot survive an unload.</summary>
    public void InvalidateResidencyProof() => _warmProof = null;

    /// <summary>Discard a cached proof when the backend's current process no longer matches it.</summary>
    public void ObserveResidency(IReadOnlyList<OllamaProcessInfo> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (_warmProof is { } proof && !processes.Any(process =>
                string.Equals(process.Model, proof.Model, StringComparison.Ordinal) &&
                process.ContextTokens == proof.ContextTokens &&
                process.SizeBytes == proof.SizeBytes &&
                process.SizeVramBytes == proof.SizeVramBytes))
        {
            InvalidateResidencyProof();
        }
    }

    private async Task<AttemptResult> ExecuteAttemptAsync(
        LocalJobRequest original,
        ModelSelection selection,
        LocalWorkloadMetadata workload,
        CancellationToken cancellationToken)
    {
        var attemptId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();
        var wasCold = _warmProof is null ||
                      !string.Equals(
                          _warmProof.Model,
                          selection.Model,
                          StringComparison.Ordinal) ||
                      _warmProof.ContextTokens != selection.ContextTokens ||
                      now - _warmProof.VerifiedAtUtc > WarmProofLifetime;
        var loadStarted = now;
        if (wasCold)
        {
            InvalidateResidencyProof();
            _warmProof = await _runtime.EnsureReadyAsync(
                selection.Model,
                selection.ContextTokens,
                cancellationToken);
        }

        var proof = _warmProof;
        var loadCompleted = _timeProvider.GetUtcNow();
        var resolved = LocalJobRequestFactory.ResolveRoutedChat(
            original,
            selection.Model);
        var executionStarted = _timeProvider.GetUtcNow();
        // These legacy numeric fields cannot express unknown. No comparison is credited here:
        // the broker has neither cloud-context provenance nor the final surface payload.
        const long gross = 0;
        const long verification = 0;
        const long net = 0;
        BrokerExecutionResult raw;
        try
        {
            raw = await _execute(resolved, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception executionException)
        {
            var failedAt = _timeProvider.GetUtcNow();
            await TryAppendTelemetryAsync(
                original,
                selection,
                workload,
                wasCold,
                "execution:fail",
                ModelExecutionOutcome.TechnicalFailure,
                loadStarted,
                loadCompleted,
                executionStarted,
                failedAt,
                gross,
                verification,
                net,
                attemptId,
                null,
                proof,
                cancellationToken);

            throw new ModelAttemptExecutionException(executionException);
        }

        var executionCompleted = _timeProvider.GetUtcNow();
        var validation = _validate(original, selection, raw);
        var (shortfall, residentPercent) = proof is null
            ? (ResidencyShortfall.None, (int?)null)
            : proof.Shortfall();
        var routing = new LocalRoutingReceipt(
            selection.Profile,
            selection.Model,
            selection.ContextTokens,
            wasCold,
            selection.UsedFallback,
            $"{validation.Code}:{(validation.Passed ? "pass" : "fail")}",
            gross,
            verification,
            net,
            selection.IsExperimentalAttempt,
            ExperimentalModel: null,
            ExperimentalOutcome: null,
            // Read from the warm proof rather than from this call's EnsureReadyAsync, which only
            // runs on a cold start: taking it from there would mark the first answer of every
            // warm window and leave the rest looking healthy.
            ResidencyShortfall: shortfall,
            VramResidentPercent: residentPercent);
        var result = raw with { Routing = routing };
        await TryAppendTelemetryAsync(
            original,
            selection,
            workload,
            wasCold,
            routing.ValidatorResult!,
            validation.Outcome,
            loadStarted,
            loadCompleted,
            executionStarted,
            executionCompleted,
            gross,
            verification,
            net,
            attemptId,
            raw,
            proof,
            cancellationToken);
        return new AttemptResult(result, validation);
    }

    /// <summary>
    /// Telemetry is measurement, not the answer. A metrics directory that stopped accepting
    /// writes used to turn a finished correct answer into a failed job on the success path,
    /// and on the failure path it was aggregated into the attempt exception — classified as
    /// a technical model failure and answered with a second expensive execution whose real
    /// cause was a disk, not the model (#202). An I/O failure here is now a named diagnostic
    /// and nothing else; cancellation keeps its meaning. Experiment state is unaffected — it
    /// is routing state, recorded elsewhere, and stays mandatory.
    /// </summary>
    private async Task TryAppendTelemetryAsync(
        LocalJobRequest original,
        ModelSelection selection,
        LocalWorkloadMetadata workload,
        bool wasCold,
        string validatorResult,
        ModelExecutionOutcome outcome,
        DateTimeOffset loadStarted,
        DateTimeOffset loadCompleted,
        DateTimeOffset executionStarted,
        DateTimeOffset executionCompleted,
        long gross,
        long verification,
        long net,
        Guid attemptId,
        BrokerExecutionResult? observed,
        ModelResidencyProof? residency,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendTelemetryAsync(
                original,
                selection,
                workload,
                wasCold,
                validatorResult,
                outcome,
                loadStarted,
                loadCompleted,
                executionStarted,
                executionCompleted,
                gross,
                verification,
                net,
                attemptId,
                observed,
                residency,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke(
                $"telemetry record for job {original.JobId:N} was not written " +
                $"({exception.GetType().Name}: {exception.Message}); " +
                "the job outcome is unaffected.");
        }
    }

    private async Task AppendTelemetryAsync(
        LocalJobRequest original,
        ModelSelection selection,
        LocalWorkloadMetadata workload,
        bool wasCold,
        string validatorResult,
        ModelExecutionOutcome outcome,
        DateTimeOffset loadStarted,
        DateTimeOffset loadCompleted,
        DateTimeOffset executionStarted,
        DateTimeOffset executionCompleted,
        long gross,
        long verification,
        long net,
        Guid attemptId,
        BrokerExecutionResult? observed,
        ModelResidencyProof? residency,
        CancellationToken cancellationToken)
    {
        var (shortfall, residentPercent) = residency?.Shortfall() ?? (ResidencyShortfall.None, null);
        var proven = residentPercent is not null;
        await _telemetry.AppendAsync(
            new ModelTelemetryRecord(
                original.JobId,
                selection.Profile,
                selection.Model,
                selection.ContextTokens,
                Bucket(workload.InputCharacters),
                Bucket(workload.ExpectedOutputCharacters),
                wasCold,
                ModelSwitched: wasCold,
                selection.UsedFallback,
                validatorResult,
                outcome,
                NonNegative(loadStarted - original.CreatedAtUtc),
                NonNegative(loadCompleted - loadStarted),
                NonNegative(executionCompleted - executionStarted),
                NonNegative(executionCompleted - original.CreatedAtUtc),
                gross,
                verification,
                net,
                selection.CatalogVersion,
                executionCompleted)
            {
                SchemaVersion = 2,
                AttemptId = attemptId,
                WorkflowId = ((ChatJobPayload)original.Payload).Workflow?.WorkflowId,
                InputCharacters = (long)((ChatJobPayload)original.Payload).Prompt.Length
                    + (((ChatJobPayload)original.Payload).System?.Length ?? 0),
                OutputCharacters = observed?.Body.ValueKind == System.Text.Json.JsonValueKind.Object
                    && observed.Body.TryGetProperty("Content", out var content)
                    && content.ValueKind == System.Text.Json.JsonValueKind.String ? content.GetString()!.Length : null,
                BackendInputTokens = observed?.BackendInputTokens,
                BackendOutputTokens = observed?.BackendOutputTokens,
                CloudTokenSavings = UsageValue.Unknown("surface_comparison_required"),
                Residency = proven ? shortfall : null,
                VramResidentPercent = residentPercent,
                ResidencyVerifiedAtUtc = proven ? residency!.VerifiedAtUtc : null,
            },
            cancellationToken);
    }

    private static BrokerExecutionResult AnnotateExperimentalFailure(
        BrokerExecutionResult result,
        ModelSelection failedSelection,
        ModelExecutionOutcome outcome) =>
        failedSelection.IsExperimentalAttempt && result.Routing is { } routing
            ? result with
            {
                Routing = routing with
                {
                    ExperimentalModel = failedSelection.Model,
                    ExperimentalOutcome = outcome
                }
            }
            : result;

    private static ExperimentSnapshot RecordIfExperimental(
        ExperimentSnapshot state,
        ModelSelection selection,
        ModelExecutionOutcome outcome) =>
        selection.IsExperimentalAttempt
            ? state.Record(selection.Profile, selection.Model, outcome)
            : state;

    private static LocalSizeBucket Bucket(int characters) =>
        characters switch
        {
            0 => LocalSizeBucket.Empty,
            <= 4_000 => LocalSizeBucket.Small,
            <= 16_000 => LocalSizeBucket.Medium,
            _ => LocalSizeBucket.Large
        };

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private sealed record AttemptResult(
        BrokerExecutionResult Result,
        ModelValidationResult Validation);

    private sealed class ModelAttemptExecutionException(Exception innerException)
        : Exception("Model execution failed.", innerException);
}
