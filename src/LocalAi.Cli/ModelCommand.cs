using System.Globalization;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

public static class ModelCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int InvalidArgumentsExitCode = 2;
    public const int RejectedExitCode = 3;
    public const int CancelledExitCode = 4;

    private const int SchemaVersion = 1;
    private const int MaximumStatusModels = 256;

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> arguments,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(arguments, out var request))
        {
            return await WriteErrorAsync(
                output, "invalid", "invalid_arguments", InvalidArgumentsExitCode);
        }

        return await ExecuteParsedAsync(
            request,
            client,
            output,
            TextWriter.Null,
            silentBackend: null,
            cancellationToken);
    }

    /// <summary>
    /// A download is given the same twelve hours the installer allows the whole command.
    /// The thirty-minute default belonged to a call to a model; on a download it made the
    /// console announce a cancellation nobody had asked for while the broker was still
    /// pulling, and the installer reported that as a failed model.
    /// </summary>
    private static readonly TimeSpan PullDeadline = TimeSpan.FromHours(12);

    public static Task<int> ExecuteProductionAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        CancellationToken cancellationToken, TextWriter? error = null) =>
        ExecuteProductionAsync(
            arguments,
            static (observer, deadline, runtimeRoot, token) =>
            {
                token.ThrowIfCancellationRequested();
                return new BrokerLocalModelClient(
                    BrokerClientFactory.CreateDefault(
                        runtimeRoot: runtimeRoot, observer: observer,
                        deadline: deadline));
            },
            output,
            cancellationToken, error,
            BrokerBackendProbe.SilentEndpoint);

    internal static Task<int> ExecuteProductionAsync(
        IReadOnlyList<string> arguments,
        Func<ILocalRunObserver?, TimeSpan?, CancellationToken, ILocalModelClient>
            clientFactory,
        TextWriter output,
        CancellationToken cancellationToken, TextWriter? error = null) =>
        ExecuteProductionAsync(arguments, (observer, deadline, _, token) => clientFactory(observer, deadline, token),
            output, cancellationToken, error);

    internal static async Task<int> ExecuteProductionAsync(
        IReadOnlyList<string> arguments,
        Func<ILocalRunObserver?, TimeSpan?, string?, CancellationToken, ILocalModelClient> clientFactory,
        TextWriter output,
        CancellationToken cancellationToken, TextWriter? error = null,
        Func<string, string?>? silentBackend = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(arguments, out var request))
        {
            return await WriteErrorAsync(
                output, "invalid", "invalid_arguments", InvalidArgumentsExitCode);
        }

        // Only a pull is long enough to have anything to say, and only a pull needs the
        // long guard. Everything else here answers in a moment.
        var isPull = string.Equals(request.Operation, "pull", StringComparison.Ordinal);
        error ??= Console.Error;
        var observer = isPull ? Watching(request, error) : null;

        ILocalModelClient client;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            client = clientFactory(
                    observer,
                    isPull ? PullDeadline : null,
                    request.RuntimeRoot,
                    cancellationToken) ??
                throw new InvalidOperationException();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return await WriteErrorAsync(
                output,
                request.Operation,
                "cancelled",
                CancelledExitCode);
        }
        catch (Exception exception)
        {
            return await BrokerFailureAsync(output, error, request, exception, silentBackend);
        }

        return await ExecuteParsedAsync(
            request,
            client,
            output,
            error,
            silentBackend,
            cancellationToken);
    }

    private static async Task<int> ExecuteParsedAsync(
        Request request,
        ILocalModelClient client,
        TextWriter output,
        TextWriter error,
        Func<string, string?>? silentBackend,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Operation switch
            {
                "status" => await StatusAsync(client, output, cancellationToken),
                "pull" => await PullAsync(request, client, output, cancellationToken),
                "preflight" => await PreflightAsync(request, client, output, cancellationToken),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (BrokerJobFailedException exception) when (
            request.Operation == "preflight" &&
            string.Equals(
                exception.FailureCode,
                "ModelPreflightException",
                StringComparison.Ordinal))
        {
            await WriteAsync(
                output,
                new ModelPreflightCommandRejected(
                    SchemaVersion,
                    "preflight",
                    false,
                    request.Model!,
                    request.ContextTokens,
                    request.CatalogVersion!,
                    // A broker too old to record an outcome proves no misfit either.
                    exception.FailureOutcome == ModelExecutionOutcome.CpuOffload
                        ? "residency_rejected"
                        : "trial_load_failed"));
            return RejectedExitCode;
        }
        catch (OperationCanceledException)
        {
            return await WriteErrorAsync(
                output,
                request.Operation,
                "cancelled",
                CancelledExitCode);
        }
        catch (Exception exception)
        {
            return await BrokerFailureAsync(output, error, request, exception, silentBackend);
        }
    }

    /// <summary>
    /// The code stays one token for a program; the reason goes to standard error for a person,
    /// because the code alone left the cause in the broker's diagnostics log (#612).
    /// <c>--progress-json</c> promised a program nothing but objects there, so it gets none.
    /// </summary>
    private static async Task<int> BrokerFailureAsync(
        TextWriter output,
        TextWriter error,
        Request request,
        Exception exception,
        Func<string, string?>? silentBackend)
    {
        var exit = await WriteErrorAsync(
            output,
            request.Operation,
            "broker_failure",
            FailureExitCode);
        if (!request.ProgressJson)
        {
            await error.WriteLineAsync("localai: " + Explain(
                exception,
                request.RuntimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot,
                silentBackend));
        }

        return exit;
    }

    /// <summary>
    /// Ollama is named only when nothing answers where the broker reaches for it: an HTTP failure
    /// is also every status a running server can send.
    /// </summary>
    private static string Explain(
        Exception exception,
        string runtimeRoot,
        Func<string, string?>? silentBackend) =>
        exception switch
        {
            BrokerBackendUnreachableException unreachable =>
                OllamaNotAnswering(unreachable.Endpoint, runtimeRoot),
            BrokerJobFailedException { FailureCode: "HttpRequestException" } when
                silentBackend?.Invoke(runtimeRoot) is { } endpoint =>
                OllamaNotAnswering(endpoint, runtimeRoot),
            BrokerJobFailedException failed => failed.Message,
            BrokerBootstrapException bootstrap => CliText.ModelBrokerNotStarted(bootstrap.Message),
            _ => UnexpectedFailure.Describe(exception),
        };

    /// <summary>
    /// The broker starts Ollama only from the installer's launch record, with its models folder
    /// when one is named, so the advice reads the same record.
    /// </summary>
    private static string OllamaNotAnswering(string endpoint, string runtimeRoot) =>
        new OllamaLaunchRecordStore(runtimeRoot).Read() switch
        {
            null => CliText.ModelOllamaNotAnswering(endpoint),
            { ModelsDirectory: { } models } =>
                CliText.ModelOllamaNotAnsweringModelsFolder(endpoint, models),
            _ => CliText.ModelOllamaNotAnsweringStartsItself(endpoint),
        };

    private static async Task<int> StatusAsync(
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await client.GetModelsStatusAsync(cancellationToken);
        var status = result.Value ?? throw new InvalidOperationException();
        var installed = ValidateAndSortModels(status.InstalledModels);
        var pending = ValidateAndSortModels(status.PendingPullModels ?? []);
        if (!IsSafeCatalogVersion(status.CatalogVersion))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelStatusCommandSuccess(
                SchemaVersion,
                "status",
                true,
                status.CatalogVersion,
                installed,
                pending));
        return SuccessExitCode;
    }

    private static async Task<int> PullAsync(
        Request request,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await client.PullModelAsync(
            request.Model!,
            request.CatalogVersion!,
            cancellationToken);
        if (!string.Equals(result.Value?.Status, "success", StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelPullCommandSuccess(
                SchemaVersion,
                "pull",
                true,
                request.Model!,
                request.CatalogVersion!,
                "success"));
        return SuccessExitCode;
    }

    private static async Task<int> PreflightAsync(
        Request request,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var proof = await new ModelManagementTasks(client).PreflightAsync(
            request.Model!,
            request.ContextTokens,
            request.CatalogVersion!,
            cancellationToken) ?? throw new InvalidOperationException();
        if (!IsSafeModel(proof.Model) ||
            !string.Equals(proof.Model, request.Model, StringComparison.Ordinal) ||
            proof.ContextTokens != request.ContextTokens ||
            !IsSafeCatalogVersion(proof.CatalogVersion) ||
            !string.Equals(
                proof.CatalogVersion,
                request.CatalogVersion,
                StringComparison.Ordinal) ||
            proof.SizeBytes <= 0 ||
            !IsResidencyAcceptable(proof, request.RuntimeRoot) ||
            proof.VerifiedAtUtc == default ||
            proof.VerifiedAtUtc.Offset != TimeSpan.Zero ||
            proof.VerifiedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelPreflightCommandSuccess(
                SchemaVersion,
                "preflight",
                true,
                proof.Model,
                proof.ContextTokens,
                proof.CatalogVersion,
                proof.SizeBytes,
                proof.SizeVramBytes,
                proof.FullyResident,
                proof.VerifiedAtUtc));
        return SuccessExitCode;
    }

    /// <summary>
    /// Checks the proof against the configured residency policy rather than assuming full
    /// residency. This validation guards against a broker reporting more than it delivered,
    /// so it has to know which outcomes the installation actually permits — otherwise a
    /// machine deliberately configured for integrated graphics would look like a liar.
    /// </summary>
    private static bool IsResidencyAcceptable(LocalModelPreflightOutput proof, string? runtimeRoot) =>
        new ModelResidencyPolicyStore(runtimeRoot ?? ModelResidencyPolicyStore.DefaultRuntimeRoot).Read().ModelResidency switch
        {
            ModelResidencyPolicy.RequireFullVram =>
                proof.FullyResident && proof.SizeVramBytes >= proof.SizeBytes,
            ModelResidencyPolicy.AllowPartialOffload => proof.SizeVramBytes > 0,
            ModelResidencyPolicy.AllowCpu => true,
            _ => false,
        };

    private static string[] ValidateAndSortModels(IReadOnlyList<string>? models)
    {
        if (models is null || models.Count > MaximumStatusModels ||
            models.Any(model => !IsSafeModel(model)) ||
            models.Distinct(StringComparer.Ordinal).Count() != models.Count)
        {
            throw new InvalidOperationException();
        }

        return models.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out Request request)
    {
        request = new Request("invalid", null, null, 0);
        // Only observation/preflight can select a target installation. This does not
        // authorize downloads, change process-wide environment or relax its policy.
        if (arguments.Count >= 3 && arguments[^2] == "--runtime-root")
        {
            var root = arguments[^1];
            if (arguments[0] is not ("status" or "preflight") || !Path.IsPathFullyQualified(root) || !HardwarePlanCommand.IsLocalPath(root)) return false;
            try
            {
                root = Path.GetFullPath(root);
                for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                    if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (!Directory.Exists(root) || !TryParse(arguments.Take(arguments.Count - 2).ToArray(), out var parsed) ||
                    parsed.RuntimeRoot is not null) return false;
                request = parsed with { RuntimeRoot = root };
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return false; }
        }
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], "status", StringComparison.Ordinal))
        {
            request = new Request("status", null, null, 0);
            return true;
        }

        // The one place this parser is not strictly positional: the flag is optional and
        // last. It picks the progress channel's form, and nothing else about the run.
        if (arguments.Count is 5 or 6 &&
            string.Equals(arguments[0], "pull", StringComparison.Ordinal) &&
            string.Equals(arguments[1], "--model", StringComparison.Ordinal) &&
            string.Equals(arguments[3], "--catalog-version", StringComparison.Ordinal) &&
            IsSafeModel(arguments[2]) &&
            IsSafeCatalogVersion(arguments[4]) &&
            (arguments.Count == 5 ||
             string.Equals(arguments[5], "--progress-json", StringComparison.Ordinal)))
        {
            request = new Request(
                "pull",
                arguments[2],
                arguments[4],
                0,
                ProgressJson: arguments.Count == 6);
            return true;
        }

        if (arguments.Count == 7 &&
            string.Equals(arguments[0], "preflight", StringComparison.Ordinal) &&
            string.Equals(arguments[1], "--model", StringComparison.Ordinal) &&
            string.Equals(arguments[3], "--context", StringComparison.Ordinal) &&
            string.Equals(arguments[5], "--catalog-version", StringComparison.Ordinal) &&
            IsSafeModel(arguments[2]) &&
            IsSafeCatalogVersion(arguments[6]) &&
            int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var contextTokens) &&
            LocalContextTiers.IsSupported(contextTokens))
        {
            request = new Request("preflight", arguments[2], arguments[6], contextTokens);
            return true;
        }

        return false;
    }

    internal static bool IsSafeModel(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200 ||
            !IsAsciiAlphaNumeric(value[0]) ||
            !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character =>
                IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-' or '/' or ':') &&
            !value.Contains("..", StringComparison.Ordinal) &&
            !value.Contains("//", StringComparison.Ordinal) &&
            !value.Contains("::", StringComparison.Ordinal) &&
            !value.Contains("/:", StringComparison.Ordinal) &&
            !value.Contains(":/", StringComparison.Ordinal);
    }

    internal static bool IsSafeCatalogVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 ||
            !IsAsciiAlphaNumeric(value[0]) ||
            !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static Task WriteAsync<T>(TextWriter output, T response) =>
        output.WriteLineAsync(JsonSerializer.Serialize(response, LocalAiJson.Strict));

    private static async Task<int> WriteErrorAsync(
        TextWriter output,
        string operation,
        string code,
        int exitCode)
    {
        if (exitCode == CancelledExitCode) UsageCall.Current?.Cancelled();
        else if (exitCode is InvalidArgumentsExitCode or RejectedExitCode) UsageCall.Current?.Refused(code);
        else UsageCall.Current?.Failed(code);
        await WriteAsync(
            output,
            new ModelCommandError(SchemaVersion, operation, false, code));
        return exitCode;
    }

    /// <summary>
    /// One face at a time: numbers for a program, sentences for a person. Both go to
    /// standard error, which leaves the single JSON object on standard output exactly as
    /// it was — that is the answer, and an answer is not a progress report.
    /// </summary>
    private static ILocalRunObserver Watching(Request request, TextWriter error) =>
        request.ProgressJson
            ? new ModelPullProgressJson(error, request.Model ?? string.Empty)
            : new LocalRunProgress(
                error,
                static () => DateTimeOffset.UtcNow,
                request.Model);

    private sealed record Request(
        string Operation,
        string? Model,
        string? CatalogVersion,
        int ContextTokens,
        bool ProgressJson = false,
        string? RuntimeRoot = null);
}
