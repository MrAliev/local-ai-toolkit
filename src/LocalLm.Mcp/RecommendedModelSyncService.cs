using LocalLm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalLm.Mcp;

public sealed class RecommendedModelSyncService(
    ModelManagementTasks tasks,
    ILogger<RecommendedModelSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await tasks.SyncRecommendedAsync(stoppingToken);
            switch (result.Outcome)
            {
                case LocalModelSyncOutcome.Complete when result.InstalledModels.Count > 0:
                    logger.LogInformation(
                        "Queued and completed recommended model installation: {Models}",
                        string.Join(", ", result.InstalledModels));
                    break;

                // A partial run is worth a warning and not a failure: some models work now, and
                // the next sync retries the rest because the missing list is recomputed from
                // status each time.
                case LocalModelSyncOutcome.Partial:
                    logger.LogWarning(
                        "Recommended model installation is partial. Installed: {Installed}. " +
                        "Not installed: {NotInstalled}. MCP startup continues and the next sync " +
                        "retries what is missing.",
                        string.Join(", ", result.InstalledModels),
                        NotInstalled(result));
                    break;

                case LocalModelSyncOutcome.Failed:
                    logger.LogWarning(
                        "No recommended model was installed. Not installed: {NotInstalled}. " +
                        "MCP startup continues and the next sync retries them.",
                        NotInstalled(result));
                    break;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Reached only when the run never started - a status that could not be read. A run
            // that started reports itself, however little of it happened.
            logger.LogWarning(
                exception,
                "Recommended model synchronization failed; MCP startup continues.");
        }
    }

    /// <summary>
    /// Every model that did not install, each with why. One placeholder rather than two, so no
    /// line ever ends in an empty list; the reasons are already cut to a readable length by the
    /// broker client that recorded them.
    /// </summary>
    private static string NotInstalled(LocalModelSyncResult result) =>
        string.Join(
            "; ",
            result.FailedModels
                .Select(failure => failure.Model + " — " + failure.Reason)
                .Concat(result.NotAttemptedModels
                    .Select(model => model + " — not attempted")));
}
