using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The automatic list against what setup installs: the planner takes only the models that fit
/// the video memory, under any rule, and weighs the release's models itself when the registry
/// gave no sizes. So a relaxed rule does not widen the list, and a model without a size is named
/// with the condition it is installed under.
/// </summary>
public sealed class TheModelListIsWhatSetupInstallsTests
{
    [Fact]
    public void A_relaxed_rule_lists_only_the_models_that_fit()
    {
        var page = new ModelsPageViewModel();
        var tags = Tags(page);

        page.ApplyRecommendation(Weighed(tags, fitting: 1), residencyRequiresVideoMemory: false);

        Assert.Equal([$"{tags[0]} — 0.9 GB, context 8192"], page.AutomaticSelection);
        Assert.Equal(
            $"Relaxed video memory rule — 1 of {tags.Length} catalogue models fit",
            page.RuleSummary);
    }

    [Fact]
    public void Nothing_fitting_says_nothing_will_be_set_up()
    {
        var page = new ModelsPageViewModel();

        page.ApplyRecommendation(Weighed(Tags(page), fitting: 0), residencyRequiresVideoMemory: false);

        Assert.Empty(page.AutomaticSelection);
        Assert.Equal(
            "No catalogue model fits the detected video memory, so none will be set up.",
            page.AutomaticSummary);
        Assert.Equal(
            "Models: none fit this computer's video memory, so none will be set up",
            page.ReviewText);
    }

    /// <summary>
    /// Without sizes the page cannot weigh anything, but setup still does, against the signed
    /// manifest; "every catalogue model will be offered" described a run that does not happen.
    /// </summary>
    [Fact]
    public void Without_sizes_each_model_is_named_with_the_condition_it_is_set_up_under()
    {
        var page = new ModelsPageViewModel();
        var tags = Tags(page);

        page.ApplyRecommendation(CatalogRecommendation.Empty, residencyRequiresVideoMemory: true);

        Assert.Equal(
            "Model sizes could not be retrieved, so this page cannot say which models fit. " +
            "Setup weighs the release's models against this computer's video memory and " +
            "installs only those that fit.",
            page.AutomaticSummary);
        Assert.Equal(
            tags.Select(tag => $"{tag} — size unknown; set up only if it fits"),
            page.AutomaticSelection);
        Assert.StartsWith(
            $"Models: {tags[0]} — size unknown; set up only if it fits",
            page.ReviewText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The registry can answer for some models and not others. The ones it did not answer for
    /// were counted as not fitting, while setup may still install them.
    /// </summary>
    [Fact]
    public void A_model_the_registry_did_not_size_is_counted_and_listed_apart()
    {
        var page = new ModelsPageViewModel();
        var tags = Tags(page);

        page.ApplyRecommendation(Weighed(tags[..1], fitting: 1), residencyRequiresVideoMemory: true);

        Assert.Equal(
            $"Whole model in video memory — 1 of {tags.Length} catalogue models fit; size " +
            $"unknown: {tags.Length - 1}",
            page.RuleSummary);
        Assert.Equal(
            [$"{tags[0]} — 0.9 GB, context 8192",
                .. tags[1..].Select(tag => $"{tag} — size unknown; set up only if it fits")],
            page.AutomaticSelection);
    }

    private static string[] Tags(ModelsPageViewModel page)
    {
        var tags = page.CatalogModels.Select(model => model.Tag).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(tags.Length >= 2, "these tests need a catalogue of at least two models");
        return tags;
    }

    private static CatalogRecommendation Weighed(IReadOnlyList<string> tags, int fitting) =>
        new(
            [.. tags.Select((tag, index) =>
                new CatalogModelFit(tag, 8192, index < fitting, 1_000_000_000, "weighed"))],
            "weighed",
            SizesKnown: true);
}
