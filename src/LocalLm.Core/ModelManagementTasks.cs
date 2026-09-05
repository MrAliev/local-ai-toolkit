using System.Text.Json.Serialization;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalLm.Core;

/// <summary>
/// How much of a synchronisation happened. Neither "succeeded" nor "failed" is true of a run
/// that installed four models out of five, and the three words lead to three different next
/// actions: nothing to do, look at this machine, or wait for the next sync to pick up the rest.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalModelSyncOutcome>))]
public enum LocalModelSyncOutcome
{
    /// <summary>Nothing was left undone - including the run that had nothing to do.</summary>
    Complete,

    /// <summary>Something installed and something did not.</summary>
    Partial,

    /// <summary>Nothing installed, and at least one model was meant to.</summary>
    Failed,
}

/// <summary>One model that did not install, and what was said about it.</summary>
public sealed record LocalModelSyncFailure(string Model, string Reason);

/// <summary>
/// What a synchronisation did, whether or not it finished.
///
/// Every model in the recommended-missing list appears exactly once across the three lists.
/// That is the invariant the shape exists for: a caller can act on this without asking for the
/// status again, which is what it had to do when a partial run threw and took its own account
/// of itself with it.
/// </summary>
public sealed record LocalModelSyncResult(
    LocalModelSyncOutcome Outcome,
    string CatalogVersion,
    IReadOnlyList<string> InstalledModels,
    IReadOnlyList<LocalModelSyncFailure> FailedModels,
    IReadOnlyList<string> NotAttemptedModels);

public sealed class ModelManagementTasks(ILocalModelClient client)
{
    private readonly ILocalModelClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public async Task<LocalModelsStatusOutput> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        (await _client.GetModelsStatusAsync(cancellationToken)).Value;

    public async Task<LocalModelPreflightOutput> PreflightAsync(
        string model,
        int contextTokens,
        string catalogVersion,
        CancellationToken cancellationToken = default) =>
        (await _client.PreflightModelAsync(
            model,
            contextTokens,
            catalogVersion,
            cancellationToken)).Value;

    /// <summary>
    /// Installs the recommended models that are missing, and comes back saying what happened to
    /// each of them.
    ///
    /// It used to accumulate the installed names and return them only on the way out, so a pull
    /// that threw on the third of five took the two already on disk with it: the caller was told
    /// nothing had been said, while the machine had changed. Model pulls are minutes each, so a
    /// partly finished run is the ordinary shape of an interrupted one.
    ///
    /// Which failure stops the run is not a preference. A job the broker refused ran, and the
    /// reason it recorded is about that model - a tag the catalogue does not have, a manifest
    /// that is not there - so the next model is still worth trying. Anything else is about the
    /// machine, and the client waits thirty minutes for a job before giving up, so carrying on
    /// through an unreachable backend would spend that wait once per remaining model inside a
    /// single call.
    /// </summary>
    public async Task<LocalModelSyncResult> SyncRecommendedAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var wanted = status.RecommendedMissingModels;
        var installed = new List<string>(wanted.Count);
        var failed = new List<LocalModelSyncFailure>();
        var notAttempted = new List<string>();

        for (var index = 0; index < wanted.Count; index++)
        {
            var model = wanted[index];
            try
            {
                await _client.PullModelAsync(model, status.CatalogVersion, cancellationToken);
                installed.Add(model);
            }
            // The token, not the type: BrokerJobCancelledException derives from
            // OperationCanceledException, so a job the queue cancelled would otherwise escape as
            // though the caller had asked the server to stop.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (BrokerJobFailedException exception)
            {
                failed.Add(new LocalModelSyncFailure(model, exception.Message));
            }
            catch (Exception exception)
            {
                failed.Add(new LocalModelSyncFailure(model, exception.Message));
                notAttempted.AddRange(wanted.Skip(index + 1));
                break;
            }
        }

        return new LocalModelSyncResult(
            Outcome(installed.Count, failed.Count + notAttempted.Count),
            status.CatalogVersion,
            Array.AsReadOnly(installed.ToArray()),
            Array.AsReadOnly(failed.ToArray()),
            Array.AsReadOnly(notAttempted.ToArray()));
    }

    /// <summary>
    /// Complete covers the run that had nothing to do, which is the commonest one: a machine
    /// whose models are all present asks for none and has left none undone.
    /// </summary>
    private static LocalModelSyncOutcome Outcome(int installed, int missed) =>
        missed == 0
            ? LocalModelSyncOutcome.Complete
            : installed > 0
                ? LocalModelSyncOutcome.Partial
                : LocalModelSyncOutcome.Failed;

    public async Task<LocalExperimentReportOutput> GetExperimentReportAsync(
        LocalTaskProfile profile,
        string model,
        CancellationToken cancellationToken = default) =>
        (await _client.GetExperimentReportAsync(
            profile,
            model,
            cancellationToken)).Value;

    public async Task<LocalModelFeedbackOutput> ApplyFeedbackAsync(
        LocalTaskProfile profile,
        string model,
        ExperimentOwnerAction action,
        CancellationToken cancellationToken = default) =>
        (await _client.ApplyFeedbackAsync(
            profile,
            model,
            action,
            cancellationToken)).Value;
}
