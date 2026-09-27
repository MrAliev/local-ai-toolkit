using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The consent on the review page is given to the list above it (#612). While a check that can
/// still change that list is running, the box cannot be ticked, and the list says what it is
/// waiting for rather than that the package will not be installed. A list that changes after the
/// tick takes the tick back and names the groups that changed.
/// </summary>
public sealed class TheReviewWaitsForItsChecksTests : IDisposable
{
    private const string Waiting =
        " The list above may change, so it can be confirmed once the check is done.";

    private const string WaitingRussian =
        " Список выше может измениться, поэтому подтвердить его можно после её окончания.";

    private readonly TemporaryDirectory scratch = new("LocalAi-review-waits");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task While_the_release_is_checked_the_box_waits_and_the_list_says_so()
    {
        var feed = new Feed { Manifest = new Gate() };
        var wizard = Wizard(feed);
        WalkToTheReview(wizard);

        var resolving = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, resolving);

        Assert.False(wizard.Review.CanConfirm);
        Assert.True(wizard.Review.IsStillChecking);
        Assert.Equal("Still checking the release." + Waiting, wizard.Review.StillChecking);
        Assert.Contains("LocalAi package: checking the release…", wizard.ReviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("not resolved", wizard.ReviewText, StringComparison.Ordinal);

        feed.Manifest.Open();
        await resolving;

        Assert.True(wizard.Review.CanConfirm);
        Assert.False(wizard.Review.IsStillChecking);
        Assert.Equal(string.Empty, wizard.Review.StillChecking);
        Assert.Contains("LocalAi package: 0.1.51", wizard.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second half of a folder's check is the hash of its package, which on a slow medium takes
    /// minutes. The line names that half once it starts, and the page is told when it does.
    /// </summary>
    [Fact]
    public async Task While_the_folder_package_is_hashed_the_list_names_the_hash()
    {
        var feed = new FolderFeed();
        var wizard = Wizard(feed);
        wizard.Package.SourceFolder = scratch.Path;
        WalkToTheReview(wizard);
        var announced = new List<string>();
        wizard.Review.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReviewApplyPageViewModel.StillChecking))
            {
                announced.Add(wizard.Review.StillChecking);
            }
        };

        var resolving = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Hash, resolving);

        var line = "Still checking the package in the folder." + Waiting;
        Assert.False(wizard.Review.CanConfirm);
        Assert.Equal(line, wizard.Review.StillChecking);
        Assert.Contains(line, announced);
        Assert.Contains(
            "LocalAi package: checking the package in the folder…",
            wizard.ReviewText,
            StringComparison.Ordinal);

        feed.Hash.Open();
        await resolving;

        Assert.True(wizard.Review.CanConfirm);
    }

    /// <summary>The components page can check the machine again, and the list is built from it.</summary>
    [Fact]
    public async Task While_this_computer_is_checked_again_the_box_waits()
    {
        var detect = new Detect();
        var wizard = Wizard(new Feed(), detect);
        WalkToTheReview(wizard);

        detect.Next = new Gate();
        var check = wizard.CheckPrerequisitesAgainAsync();
        await Reached(detect.Next, check);

        Assert.False(wizard.Review.CanConfirm);
        Assert.Equal("Still checking this computer." + Waiting, wizard.Review.StillChecking);

        detect.Next.Open();
        await check;

        Assert.True(wizard.Review.CanConfirm);
        Assert.Equal(string.Empty, wizard.Review.StillChecking);
    }

    [Theory]
    [InlineData(InstallerLanguage.English, "Still checking the release and this computer." + Waiting)]
    [InlineData(InstallerLanguage.Russian, "Ещё идёт проверка релиза и этого компьютера." + WaitingRussian)]
    public async Task Two_checks_at_once_are_named_together(InstallerLanguage language, string expected)
    {
        InstallerCulture.Current = language;
        var feed = new Feed { Manifest = new Gate() };
        var detect = new Detect();
        var wizard = Wizard(feed, detect);
        WalkToTheReview(wizard);

        var resolving = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, resolving);
        detect.Next = new Gate();
        var check = wizard.CheckPrerequisitesAgainAsync();
        await Reached(detect.Next, check);

        Assert.Equal(expected, wizard.Review.StillChecking);

        feed.Manifest.Open();
        detect.Next.Open();
        await Task.WhenAll(resolving, check);
    }

    [Fact]
    public async Task The_package_line_waits_in_Russian_too()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;
        var feed = new FolderFeed { Manifest = new Gate() };
        var wizard = Wizard(feed);
        wizard.Package.SourceFolder = scratch.Path;
        WalkToTheReview(wizard);

        var resolving = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, resolving);
        Assert.Contains("Пакет LocalAi: проверяю релиз…", wizard.ReviewText, StringComparison.Ordinal);

        feed.Manifest.Open();
        await Reached(feed.Hash, resolving);
        Assert.Contains("Пакет LocalAi: проверяю пакет в папке…", wizard.ReviewText, StringComparison.Ordinal);
        Assert.Equal("Ещё идёт проверка пакета в папке." + WaitingRussian, wizard.Review.StillChecking);

        feed.Hash.Open();
        await resolving;
    }

    /// <summary>
    /// The tick survived going back and changing a page: only the box's own handler ever cleared
    /// it. It is given to a list, and that list is no longer the one on the page.
    /// </summary>
    [Fact]
    public void A_page_changed_after_the_tick_takes_it_back_and_names_its_group()
    {
        var wizard = Wizard(new Feed());
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        Assert.True(wizard.MovePrevious());
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.RefreshNavigationState();
        Assert.True(wizard.MoveNext());

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        Assert.False(wizard.Review.IsConfirmed);
        Assert.False(wizard.CanRun);
        Assert.True(wizard.Review.HasChangedNotice);
        Assert.Equal(
            "The list above changed after you confirmed it (Clients and PATH). Read it again, then confirm.",
            wizard.Review.ChangedNotice);

        wizard.SetReviewConfirmed(true);

        Assert.True(wizard.CanRun);
        Assert.False(wizard.Review.HasChangedNotice);
        Assert.Equal(string.Empty, wizard.Review.ChangedNotice);
    }

    /// <summary>
    /// The client files are read again every time the list is built, so something else writing
    /// one while the page is open changes the list without anybody touching the wizard.
    /// </summary>
    [Fact]
    public void A_file_the_list_reads_again_takes_the_tick_back_when_it_changed()
    {
        var files = new List<string> { @"C:\Users\x\.codex\config.toml" };
        var wizard = Wizard(new Feed());
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.Agents.UseConfigurationPreview((agent, _) => agent == "codex" ? [.. files] : []);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);
        wizard.RefreshNavigationState();
        Assert.True(wizard.Review.IsConfirmed);

        files.Clear();
        wizard.RefreshNavigationState();

        Assert.False(wizard.Review.IsConfirmed);
        Assert.Equal(
            "The list above changed after you confirmed it (Clients and PATH). Read it again, then confirm.",
            wizard.Review.ChangedNotice);
    }

    /// <summary>A check started after the tick holds the box, and its different answer takes the tick back.</summary>
    [Fact]
    public async Task A_check_that_answers_differently_takes_the_tick_back()
    {
        var feed = new Feed();
        var wizard = Wizard(feed);
        await wizard.ResolvePackageAsync(Token);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        feed.Answer = Release("0.1.52");
        feed.Manifest = new Gate();
        var again = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, again);
        Assert.False(wizard.CanRun);
        feed.Manifest.Open();
        await again;

        Assert.False(wizard.Review.IsConfirmed);
        Assert.Equal(
            "The list above changed after you confirmed it (Release and source). Read it again, then confirm.",
            wizard.Review.ChangedNotice);
    }

    /// <summary>
    /// While the check runs the list says so, and that is not the change: the tick is held with the
    /// box and stays when the answer is the one it was given to.
    /// </summary>
    [Fact]
    public async Task A_check_that_answers_the_same_leaves_the_tick()
    {
        var feed = new Feed();
        var wizard = Wizard(feed);
        await wizard.ResolvePackageAsync(Token);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        feed.Manifest = new Gate();
        var again = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, again);
        Assert.False(wizard.CanRun);
        feed.Manifest.Open();
        await again;

        Assert.True(wizard.Review.IsConfirmed);
        Assert.True(wizard.CanRun);
        Assert.False(wizard.Review.HasChangedNotice);
    }

    /// <summary>
    /// A switch of language rewords every line of the list and changes none of its facts, and a
    /// theme does not reach the list at all. Neither is a change the tick is taken back for.
    /// </summary>
    [Fact]
    public void A_language_or_theme_change_leaves_the_tick()
    {
        var presentation = new InstallerPresentation();
        var wizard = Wizard(new Feed(), presentation: presentation);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        presentation.ChooseTheme(InstallerTheme.Dark);
        presentation.ChooseLanguage(InstallerLanguage.Russian);
        wizard.RefreshNavigationState();

        Assert.True(wizard.Review.IsConfirmed);
        Assert.True(wizard.CanRun);
        Assert.False(wizard.Review.HasChangedNotice);

        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.RefreshNavigationState();

        Assert.Equal(
            "Список выше изменился после вашего подтверждения (Клиенты и PATH). " +
            "Прочитайте его снова и подтвердите.",
            wizard.Review.ChangedNotice);
    }

    /// <summary>
    /// The update check is answered on the review page itself, above the box, in view of the list:
    /// the answer is taken into the tick rather than taking it away. The rest of the list is still
    /// held to what was ticked.
    /// </summary>
    [Fact]
    public void The_update_check_answered_above_the_box_is_taken_into_the_tick()
    {
        var wizard = Wizard(new Feed());
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);
        var announced = new List<string?>();
        wizard.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        wizard.Review.EnableUpdateCheck = true;

        Assert.Contains(nameof(InstallerWizardViewModel.ReviewGroups), announced);
        Assert.Contains("Update check: on", wizard.ReviewText, StringComparison.Ordinal);

        wizard.RefreshNavigationState();

        Assert.True(wizard.Review.IsConfirmed);
        Assert.True(wizard.CanRun);

        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.RefreshNavigationState();

        Assert.Equal(
            "The list above changed after you confirmed it (Clients and PATH). Read it again, then confirm.",
            wizard.Review.ChangedNotice);
    }

    [Fact]
    public void Every_group_that_changed_is_named_in_the_readers_language()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;
        var wizard = Wizard(new Feed());
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.Dependencies.SetConsent("Git", true);
        wizard.RefreshNavigationState();

        Assert.False(wizard.Review.IsConfirmed);
        Assert.Equal(
            "Список выше изменился после вашего подтверждения (Компоненты, Клиенты и PATH). " +
            "Прочитайте его снова и подтвердите.",
            wizard.Review.ChangedNotice);
    }

    /// <summary>
    /// Nothing may have refreshed the page since the file changed, so the button reads the list
    /// once more before it starts anything.
    /// </summary>
    [Fact]
    public async Task Pressing_the_button_reads_the_list_once_more()
    {
        var files = new List<string> { @"C:\Users\x\.codex\config.toml" };
        var wizard = Wizard(new Feed());
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.Agents.UseConfigurationPreview((agent, _) => agent == "codex" ? [.. files] : []);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);

        files.Clear();

        Assert.False(await wizard.RunAsync(Token));
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        Assert.False(wizard.Review.IsConfirmed);
        Assert.True(wizard.Review.HasChangedNotice);
    }

    /// <summary>
    /// While the release is checked its absence is not an outcome yet: the warning that nothing
    /// will be installed goes until the check ends, taking the group with it when it was the only
    /// warning, and the PATH line says what it waits for instead of that it will not change.
    /// </summary>
    [Theory]
    [InlineData(
        InstallerLanguage.English,
        "Warning: no release has been verified",
        "PATH: unchanged, because LocalAi will not be installed.",
        "PATH: depends on the release, still being checked.")]
    [InlineData(
        InstallerLanguage.Russian,
        "Внимание: ни один релиз не проверен",
        "PATH: не меняется, потому что LocalAi не будет установлен.",
        "PATH: зависит от релиза, который ещё проверяется.")]
    public async Task While_the_release_is_checked_its_absence_is_not_warned_of_yet(
        InstallerLanguage language,
        string noRelease,
        string unchanged,
        string waiting)
    {
        InstallerCulture.Current = language;
        var feed = new Feed { Manifest = new Gate(), Failure = new IOException("no feed in this test") };
        var wizard = Wizard(feed);
        WalkToTheReview(wizard);
        Assert.StartsWith(noRelease, Assert.Single(Group(wizard, PageLabels.ReviewWarnings)), StringComparison.Ordinal);
        Assert.Contains(unchanged, Group(wizard, PageLabels.ReviewClients));

        var resolving = wizard.ResolvePackageAsync(Token);
        await Reached(feed.Manifest, resolving);

        Assert.DoesNotContain(wizard.ReviewGroups, group => group.Title == PageLabels.ReviewWarnings);
        Assert.Contains(waiting, Group(wizard, PageLabels.ReviewClients));
        Assert.DoesNotContain(unchanged, Group(wizard, PageLabels.ReviewClients));

        feed.Manifest.Open();
        await resolving;

        Assert.StartsWith(noRelease, Assert.Single(Group(wizard, PageLabels.ReviewWarnings)), StringComparison.Ordinal);
        Assert.Contains(unchanged, Group(wizard, PageLabels.ReviewClients));
        Assert.DoesNotContain(waiting, Group(wizard, PageLabels.ReviewClients));
    }

    /// <summary>
    /// One line under the box at a time. "Read it again, then confirm" cannot be done while a check
    /// holds the box, so the line saying the list changed waits for the check to end, and then
    /// names every group changed since the tick, those the check itself changed among them.
    /// </summary>
    [Fact]
    public async Task The_changed_list_is_named_after_the_check_with_what_the_check_changed()
    {
        var detect = new Detect();
        var wizard = Wizard(new Feed(), detect);
        WalkToTheReview(wizard);
        await wizard.CheckPrerequisitesAgainAsync();
        wizard.Dependencies.SetConsent("Git", true);
        wizard.RefreshNavigationState();
        wizard.SetReviewConfirmed(true);

        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        wizard.RefreshNavigationState();
        Assert.Equal(
            "The list above changed after you confirmed it (Clients and PATH). Read it again, then confirm.",
            wizard.Review.ChangedNotice);

        // Git gone since: the components line now installs it rather than reinstalling it.
        detect.Answer = HermeticDiagnosis.Supported(
            git: new DependencySnapshot("git", DependencyState.NotFound, null, null, "not in this test"));
        detect.Next = new Gate();
        var check = wizard.CheckPrerequisitesAgainAsync();
        await Reached(detect.Next, check);

        Assert.True(wizard.Review.IsStillChecking);
        Assert.False(wizard.Review.HasChangedNotice);
        Assert.Equal(string.Empty, wizard.Review.ChangedNotice);

        detect.Next.Open();
        await check;

        Assert.False(wizard.Review.IsConfirmed);
        Assert.False(wizard.Review.IsStillChecking);
        Assert.True(wizard.Review.HasChangedNotice);
        Assert.Equal(
            "The list above changed after you confirmed it (Components, Clients and PATH). " +
            "Read it again, then confirm.",
            wizard.Review.ChangedNotice);
    }

    /// <summary>
    /// The line names the union of the groups changed since the tick: a group it already named stays
    /// named when another changes, even if the first has since gone back to what was ticked.
    /// </summary>
    [Fact]
    public void A_group_already_named_stays_named_when_another_changes()
    {
        var wizard = Wizard(new Feed());
        WalkToTheReview(wizard);
        var codex = wizard.Agents.Agents.Single(row => row.Agent == "codex").Choice;
        wizard.SetReviewConfirmed(true);

        wizard.Agents.SetChoice("codex", codex == AgentChoice.McpOnly ? AgentChoice.NoChange : AgentChoice.McpOnly);
        wizard.RefreshNavigationState();
        wizard.Agents.SetChoice("codex", codex);
        wizard.Dependencies.SetConsent("Git", true);
        wizard.RefreshNavigationState();

        Assert.Equal(
            "The list above changed after you confirmed it (Components, Clients and PATH). " +
            "Read it again, then confirm.",
            wizard.Review.ChangedNotice);
    }

    private static IReadOnlyList<string> Group(InstallerWizardViewModel wizard, string title) =>
        wizard.ReviewGroups.Single(group => group.Title == title).Lines;

    private InstallerWizardViewModel Wizard(
        IReleaseFeed feed,
        Detect? detect = null,
        InstallerPresentation? presentation = null) =>
        new(
            StartChoice.Install,
            () => null,
            feed,
            detectEnvironment: (detect ?? new Detect()).Run,
            presentation: presentation)
        {
            ModelSizeSource = new OfflineSizesComeFromTheReleaseTests.FixedRegistry(null),
            LogDirectory = scratch.Combine("logs"),
            RuntimeRoot = scratch.Combine("no-runtime-root"),
            ScratchRoot = scratch.Combine("scratch"),
        };

    private static void WalkToTheReview(InstallerWizardViewModel wizard)
    {
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }
    }

    private static async Task Reached(Gate gate, Task task)
    {
        var first = await Task.WhenAny(gate.Reached, task).WaitAsync(Token);
        Assert.True(first == gate.Reached, "The check finished without reaching its gate.");
    }

    /// <summary>Held shut until the test opens it; says when something has arrived at it.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource opened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => arrived.Task;

        public async Task Pass()
        {
            arrived.TrySetResult();
            await opened.Task;
        }

        public void Open() => opened.TrySetResult();
    }

    /// <summary>The check of this computer, answered at once unless a gate is set for the next one.</summary>
    private sealed class Detect
    {
        public Gate? Next { get; set; }

        public EnvironmentDiagnosis Answer { get; set; } = HermeticDiagnosis.Supported();

        public async Task<EnvironmentDiagnosis> Run(CancellationToken cancellationToken)
        {
            var answer = Answer;
            if (Next is { } gate)
            {
                await gate.Pass();
            }

            return answer;
        }
    }

    /// <summary>
    /// A feed answering <see cref="Answer"/>, or throwing <see cref="Failure"/> when one is set, its
    /// manifest held at a gate when one is set.
    /// </summary>
    private class Feed : IReleaseFeed
    {
        public ResolvedRelease Answer { get; set; } = Release("0.1.51");

        public Exception? Failure { get; set; }

        public Gate? Manifest { get; set; }

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken cancellationToken) =>
            Task.FromResult(Answer.Manifest.ReleaseVersion);

        public async Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            var answer = Answer;
            if (Manifest is { } gate)
            {
                await gate.Pass();
            }

            return Failure is { } failure ? throw failure : answer;
        }

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken cancellationToken) =>
            throw new IOException("these tests do not install");
    }

    /// <summary>A folder whose package hash waits at its gate.</summary>
    private sealed class FolderFeed : Feed, IReleasePackageCheck
    {
        public Gate Hash { get; } = new();

        public async Task<bool> PackageMatchesAsync(
            ReleaseManifest manifest,
            CancellationToken cancellationToken)
        {
            await Hash.Pass();
            return true;
        }
    }

    private static ResolvedRelease Release(string version) => new(
        new ReleaseManifest(
            schemaVersion: 1,
            releaseVersion: version,
            versionDirectory: "0123456789ab",
            modelCatalogVersion: "1",
            protocolVersion: 1,
            buildCompatibilityId: "test",
            packageUri: new Uri($"https://example.invalid/{version}/localai-package.zip"),
            packageSize: 200L * 1024 * 1024,
            packageSha256: new string('a', 64),
            requiresAuthenticode: false,
            models: []),
        [1],
        [2]);
}
