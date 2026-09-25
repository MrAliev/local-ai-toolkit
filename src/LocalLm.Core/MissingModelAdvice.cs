using LocalAi.Contracts;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// What to tell someone whose task has no model to run on.
///
/// The broker records a failure code and nothing else — a raw exception message is not something
/// that boundary hands out — so <c>read_image</c> on a machine with no vision model reported
/// "failed with 'InvalidOperationException'". True, and useless: it named neither the cause nor
/// anything to do about it.
///
/// The routing catalog already knows which models a profile can run on, and it is readable
/// without depending on the broker, which is what <see cref="ModelRoutingCatalogStore"/>
/// exists for. So the answer is assembled here, where the tool that failed can say it.
/// </summary>
public static class MissingModelAdvice
{
    /// <summary>
    /// Named for the profile, listing the models that would satisfy it and the one command that
    /// installs one. The pull goes through the launcher and the broker, which is the only route
    /// models are ever installed by, so it names only a model the broker's allowlist admits.
    /// </summary>
    public static string ForProfile(LocalTaskProfile profile, string? runtimeRoot = null)
    {
        if (Read(runtimeRoot) is not { } document)
        {
            return LocalLmText.NoModelCatalogUnreadable(profile);
        }

        var models = ModelsFor(profile, document);
        if (models.Count == 0)
        {
            return LocalLmText.NoModelAndCatalogNamesNone(profile);
        }

        var named = string.Join(", ", models);
        var installable = models.FirstOrDefault(model =>
            document.MaintenanceAllowlist.Contains(model, StringComparer.Ordinal));
        return installable is null
            ? LocalLmText.NoModelInstalledNotDownloadable(profile, named)
            : LocalLmText.NoModelInstalledWithInstall(profile, named, installable, document.CatalogVersion);
    }

    /// <summary>
    /// The opposite case: a model for this task is installed, but none of them can take this
    /// particular request. Telling somebody to install what they already have would waste an hour
    /// and several gigabytes, so this names the two limits that actually decide eligibility.
    /// </summary>
    public static string ForIneligibleRequest(LocalTaskProfile profile, string? runtimeRoot = null)
    {
        var models = Read(runtimeRoot) is { } document ? ModelsFor(profile, document) : [];
        return models.Count == 0
            ? LocalLmText.Ineligible(profile)
            : LocalLmText.IneligibleWithModels(profile, string.Join(", ", models));
    }

    private static ModelRoutingCatalogDocument? Read(string? runtimeRoot)
    {
        try
        {
            return new ModelRoutingCatalogStore(runtimeRoot).Read();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or
                System.Text.Json.JsonException)
        {
            // Advice is not worth failing over; the caller already has a failure to report.
            return null;
        }
    }

    private static IReadOnlyList<string> ModelsFor(LocalTaskProfile profile, ModelRoutingCatalogDocument document)
    {
        var route = document.Routes.FirstOrDefault(entry => entry.Profile == profile);
        if (route is null)
        {
            return [];
        }

        // Candidates first, then fallbacks: that is the order the router would have tried
        // them, so it is the order worth installing them in.
        return
        [
            .. route.Candidates
                .Concat(route.Fallbacks)
                .Distinct(StringComparer.Ordinal)
        ];
    }
}
