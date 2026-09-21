using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

public sealed class EffectsOverviewTests : IDisposable
{
    private readonly RemovalFixture machine = new();
    public void Dispose() => machine.Dispose();

    [Theory]
    [InlineData(StartChoice.Install)]
    [InlineData(StartChoice.UpdateOrRepair)]
    public void Installation_groups_keep_the_effects_warnings_and_choices_in_both_languages(StartChoice mode)
    {
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(Path.Combine(machine.Root, "preferences")));
        var wizard = new InstallerWizardViewModel(mode, presentation: presentation);
        wizard.Dependencies.SetConsent("Git", true);
        wizard.Models.IsChooseExact = true;
        wizard.Residency.IsAllowCpu = true;
        wizard.Review.EnableUpdateCheck = true;
        wizard.Review.IsConfirmed = true;
        var model = wizard.Models.SelectedModel;
        foreach (var language in new[] { InstallerLanguage.English, InstallerLanguage.Russian })
        {
            presentation.ChooseLanguage(language);
            var groups = wizard.ReviewGroups;
            Assert.True(groups.Count >= 5);
            var text = string.Join('\n', groups.SelectMany(group => group.Lines));
            foreach (var effect in new[] { wizard.Package.ReviewText, wizard.Dependencies.ReviewText,
                         wizard.Models.ReviewText, wizard.Residency.ReviewText, wizard.Agents.ReviewText,
                         wizard.Agents.GateReviewText, wizard.Agents.WorktreeReviewText, wizard.Residency.Warning })
                Assert.Contains(effect.Replace("\r", "", StringComparison.Ordinal), text, StringComparison.Ordinal);
            Assert.Contains("PATH", text, StringComparison.Ordinal);
            Assert.Contains(language == InstallerLanguage.English ? "no release has been verified" : "ни один релиз не проверен", text, StringComparison.Ordinal);
            Assert.Equal(Lines(wizard.ReviewText!), Lines(text));
            Assert.True(wizard.Review.IsConfirmed);
            Assert.True(wizard.Review.EnableUpdateCheck);
            Assert.Equal(model, wizard.Models.SelectedModel);
        }
        wizard.DetachPresentation();
    }

    [Fact]
    public async Task Removal_groups_keep_every_planned_path_hook_configuration_and_retention()
    {
        var plan = await machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.FullUninstall), TestContext.Current.CancellationToken);
        var longPath = Path.Combine(machine.Runtime, new string('x', 190), "long file name.json");
        plan = plan with { Paths = [.. plan.Paths, new(RemovalItem.Binaries, longPath, false)] };
        var groups = ReviewGroup.ForRemoval(plan);
        Assert.Equal(2, groups.Count);
        var changes = string.Join('\n', groups[0].Lines);
        var retained = string.Join('\n', groups[1].Lines);
        foreach (var path in plan.Paths) Assert.Contains(path.Path, changes, StringComparison.Ordinal);
        foreach (var file in plan.AgentConfigurations.SelectMany(configuration => configuration.Files))
            Assert.Contains(file.Path, changes, StringComparison.Ordinal);
        foreach (var hook in plan.Hooks)
        {
            Assert.Contains(hook.CommonDirectory, changes + retained, StringComparison.Ordinal);
            foreach (var target in hook.Dispatchers.Concat(hook.RestoredHooks))
                Assert.Contains(target, changes + retained, StringComparison.Ordinal);
            if (hook.ExcludePatterns.Count > 0)
            {
                Assert.Contains(hook.ExcludePath!, changes + retained, StringComparison.Ordinal);
                foreach (var pattern in hook.ExcludePatterns) Assert.Contains(pattern, changes + retained, StringComparison.Ordinal);
            }
        }
        foreach (var path in plan.RetainedPaths) Assert.Contains(path, retained, StringComparison.Ordinal);
        foreach (var notice in plan.Retained) Assert.Contains(notice.Detail, retained, StringComparison.Ordinal);
        Assert.Equal(Lines(plan.PreviewText), Lines(changes + "\n" + retained));
    }

    [Fact]
    public void Skipped_and_empty_repositories_belong_to_the_kept_group()
    {
        var skipped = Path.Combine(machine.Root, "custom-hooks");
        var empty = Path.Combine(machine.Root, "no-hooks");
        var plan = new UninstallPlan(machine.Runtime, RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            [], [], [], [new("skip", skipped, null, [], [], [], null, "custom hooks"),
                new("empty", empty, null, [], [], [], null, null)], []);
        var groups = ReviewGroup.ForRemoval(plan);
        Assert.Contains(skipped, string.Join('\n', groups[1].Lines), StringComparison.Ordinal);
        Assert.Contains(empty, string.Join('\n', groups[1].Lines), StringComparison.Ordinal);
        Assert.DoesNotContain(skipped, string.Join('\n', groups[0].Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Language_and_theme_keep_the_confirmed_removal_plan_but_back_requests_a_new_one()
    {
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(Path.Combine(machine.Root, "preferences")));
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true, machine.Layout, machine.Home,
            readHooksPath: machine.HooksPathReader, presentation: presentation);
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        var jobs = Path.Combine(machine.Runtime, "jobs");
        Assert.Contains(jobs, string.Join('\n', wizard.ReviewGroups.SelectMany(group => group.Lines)), StringComparison.Ordinal);
        Directory.Delete(jobs, recursive: true);
        presentation.ChooseLanguage(InstallerLanguage.Russian);
        presentation.ChooseTheme(InstallerTheme.Dark);
        Assert.True(wizard.IsConfirmed);
        Assert.Contains("  удалить " + jobs, string.Join('\n', wizard.ReviewGroups.SelectMany(group => group.Lines)), StringComparison.Ordinal);
        Assert.Contains("перепишет все три", wizard.KeepNotice, StringComparison.Ordinal);
        Assert.True(wizard.MovePrevious());
        Assert.Empty(wizard.ReviewGroups);
        Assert.False(wizard.IsConfirmed);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("  удалить " + jobs, string.Join('\n', wizard.ReviewGroups.SelectMany(group => group.Lines)), StringComparison.Ordinal);
        wizard.DetachPresentation();
    }

    private static string[] Lines(string text) => text.Replace("\r", "", StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()).ToArray();
}
