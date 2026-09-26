using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

public sealed class InstallerWizardViewModel : ObservableObject
{
    private static readonly TimeSpan DependencyInstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Matches the activation timeout the installation itself runs under.</summary>
    private static readonly TimeSpan RollbackActivationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What the start screen was asked for. Until now the wizard could not tell an install
    /// from an update — StartWindow opened the same MainWindow for both — so it introduced
    /// itself as an installation, asked every question an installation asks, and left the
    /// person who had chosen "Update or repair" to work out that it had heard them (#257).
    /// </summary>
    public StartChoice Mode { get; }

    /// <summary>
    /// One row per model, in the order the signed manifest planned them. Empty until a run
    /// reaches the models, and empty for good on a run that was told to skip them — a heading
    /// over no rows is a question about an absence.
    /// </summary>
    public ObservableCollection<ModelRunRow> ModelRuns { get; } = [];

    public bool HasModelRuns => ModelRuns.Count > 0;

    /// <summary>
    /// The pages this run actually visits.
    ///
    /// An update re-asked four questions it had the answers to — prerequisites that are
    /// already installed, models that are already pulled, a residency policy now read from
    /// disk, client registrations that already exist — and then told the reader it was on
    /// "Step 3 of 9" of a nine-page install (#257). The settings pages are folded away and
    /// their values still appear on the review page, which is what keeps every effect listed
    /// before it is applied.
    ///
    /// One button on the review page brings them back, for the run where the answer really
    /// has changed. Nothing is skipped irrevocably.
    /// </summary>
    private IReadOnlyList<(InstallerPage Page, string Title)> Steps =>
        Mode == StartChoice.Install
            ? WizardText.Steps()
            : [.. WizardText.Steps()
                .Where(step =>
                (releaseRevealed || !FoldedRelease.Contains(step.Page)) &&
                (settingsRevealed || !FoldedSettings.Contains(step.Page)))];

    /// <summary>
    /// What an update does not ask again. Diagnose stays: it is where an interrupted run is
    /// offered back and where an unsupported machine is stopped. Package stays for now — it
    /// still owns resolving the release, and moving that is its own change.
    /// </summary>
    /// <summary>
    /// Which release to install. Its own group, and its own button on the review page: the
    /// release and the settings are different questions, and one control for both is the
    /// confusion this redesign exists to remove.
    /// </summary>
    private static readonly IReadOnlySet<InstallerPage> FoldedRelease =
        new HashSet<InstallerPage> { InstallerPage.Package };

    private static readonly IReadOnlySet<InstallerPage> FoldedSettings =
        new HashSet<InstallerPage>
        {
            InstallerPage.Dependencies,
            InstallerPage.Models,
            InstallerPage.Agents,
        };

    /// <summary>Set by the review page's two buttons; never unset.</summary>
    private bool settingsRevealed;

    private bool releaseRevealed;

    public bool AreSettingsFolded =>
        Mode != StartChoice.Install && !settingsRevealed;

    public bool IsReleaseFolded =>
        Mode != StartChoice.Install && !releaseRevealed;

    /// <summary>
    /// Brings the folded pages back and moves to the first of them. Offered on the review
    /// page, because that is where somebody discovers that a carried-forward answer is not
    /// the one they want this time.
    /// </summary>
    public void RevealSettings()
    {
        if (!AreSettingsFolded)
        {
            return;
        }

        settingsRevealed = true;
        CurrentPage = InstallerPage.Dependencies;
    }

    /// <summary>
    /// Brings back the release page alone. Back from it returns to the review rather than to
    /// the page before it in the rail: it was opened from there, for one answer.
    /// </summary>
    public void RevealRelease()
    {
        if (!IsReleaseFolded)
        {
            return;
        }

        releaseRevealed = true;
        returnFromReleaseToConfirm = true;
        CurrentPage = InstallerPage.Package;
    }

    private bool returnFromReleaseToConfirm;

    private readonly DiagnosePageViewModel diagnose = new();
    private readonly DependenciesPageViewModel dependencies = new();
    private readonly IInstalledApplicationProbe installedApplications;
    private readonly PackagePageViewModel package = new();

    /// <summary>
    /// Kept for the whole run so the executable approved once is the one revalidated before each
    /// later install, rather than a fresh answer from a search path that may have changed.
    /// </summary>
    private readonly TrustedWingetSource wingetSource =
        new(new WindowsWingetExecutableTrust());
    private readonly ModelsPageViewModel models = new();
    private readonly ResidencyPageViewModel residency = new();

    /// <summary>What the machine had before this run, so the review can say what changes.</summary>
    private ModelResidencyPolicy? storedResidency;

    private bool? storedUpdateCheck;

    /// <summary>
    /// What was installed when this window opened. Held rather than re-read: null is a real
    /// answer, so the flag beside it is what makes "read once" possible.
    /// </summary>
    private string? heldInstalledVersion;

    private bool hasReadInstalledVersion;

    /// <summary>
    /// Which resolve is the current one. The question alone cannot tell two resolves of the
    /// same question apart — the automatic one and the "Check release" button ask identically
    /// — and the older one finishing last would otherwise overwrite the newer answer.
    /// </summary>
    private int resolveGeneration;


    private readonly Func<string?> readInstalledVersion;
    private readonly AgentIntegrationPageViewModel agents = new();
    private readonly ReviewApplyPageViewModel review = new();
    private readonly FinishPageViewModel finish = new();
    private readonly Func<CancellationToken, Task<EnvironmentDiagnosis>> detectEnvironment;
    private readonly IProcessRunner processRunner;

    private readonly AnonymousReleaseFeed anonymousFeed = new();

    /// <summary>
    /// Supplied instead of the real feed by tests. The behaviours that hang off this — that a
    /// verified release survives a re-check which fails, that a resolve runs behind the first
    /// page without anybody pressing a button, and that a resolve overtaken by a newer
    /// question does not answer it — cannot be observed without one, and the first of those
    /// shipped broken because there was nothing to observe it with.
    /// </summary>
    private readonly IReleaseFeed? feedForTests;
    private CancellationTokenSource? runCancellation;
    private InstallerPage currentPage = InstallerPage.Diagnose;
    private bool isRunning;
    private bool isComplete;
    private bool hasRunError;

    /// <summary>The clients this run actually wrote to, which is what has to be restarted.</summary>
    private readonly List<string> configuredAgents = [];
    private readonly List<ClientRunOutcome> clientOutcomes = [];
    private ClientRunOutcome? hooksOutcome;
    private RegistrationOutcome? registrationOutcome;
    private string? reportSaveFailure;

    private RunMark pathMark = RunMark.None;

    private PathOutcome? pathOutcome;
    private bool wasCancelled;
    private bool hasInitialized;
    private int progress;

    /// <summary>A resting state, worded when read; null while a step's own text is showing.</summary>
    private ProgressWord? progressWord = ProgressWord.Ready;
    private string progressText = string.Empty;
    private string? runLogMessage;
    private Exception? unexpected;
    private EnvironmentDiagnosis? environmentDiagnosis;
    private CatalogRecommendation lastRecommendation = CatalogRecommendation.Empty;
    private string? resolvedTag;
    private PackageOutcome? packageOutcome;
    private bool packageInstalled;
    private InstallerRunJournal? runJournal;
    private InstallerRunJournal? interruptedJournal;

    /// <summary>
    /// The interrupted run as it was found. The notice is worded from this rather than from the
    /// journal, which a rollback rewrites step by step while it runs.
    /// </summary>
    private InstallerRunJournalSnapshot? interruptedAsFound;

    /// <summary>What rolling back the interrupted run reported, shown in the notice's place.</summary>
    private string? rollbackNotice;

    /// <summary>The counts the finish rows were built from, so they can be built again.</summary>
    private (int Requested, int Installed, int Failed)? finished;

    /// <summary>
    /// <paramref name="readInstalledVersion"/> is how the rail learns which version is on this
    /// computer. Injectable because the property that matters — that the answer stops changing
    /// once the run starts — cannot be observed without moving the disk underneath it.
    /// <paramref name="detectEnvironment"/> lets completion paths be exercised with a
    /// diagnostic failure or cancellation without probing or changing the host machine.
    /// </summary>
    public InstallerWizardViewModel(
        StartChoice mode = StartChoice.Install,
        Func<string?>? readInstalledVersion = null,
        IReleaseFeed? releaseFeed = null,
        bool canReturnToStart = false,
        Func<CancellationToken, Task<EnvironmentDiagnosis>>? detectEnvironment = null,
        InstallerPresentation? presentation = null)
    {
        Presentation = presentation ?? new InstallerPresentation();
        Presentation.LanguageChanged += OnLanguageChanged;
        this.canReturnToStart = canReturnToStart;
        feedForTests = releaseFeed;
        this.readInstalledVersion = readInstalledVersion ?? (() => InstalledVersionReader
            .Read(ModelResidencyPolicyStore.DefaultRuntimeRoot)
            .DisplayName);
        Mode = mode;
        var runner = new SystemProcessRunner();
        processRunner = runner;
        // Kept, not just handed to the detector: the detector's answer may have come from a
        // plain PATH lookup, and only the validated one is worth recording as launchable.
        installedApplications = new WindowsInstalledApplicationProbe();
        var environmentDetector = new WindowsEnvironmentDetector(
            new SystemEnvironmentProbe(),
            new SystemFileSystemProbe(),
            runner,
            installedApplications,
            new SystemDiskProbe(),
            new SystemNetworkProbe(),
            new WindowsGpuProbe(new DxgiNativeGpuAdapterEnumerator()));
        this.detectEnvironment = detectEnvironment ?? environmentDetector.DetectAsync;

        BackCommand = new RelayCommand(() => MovePrevious(), () => CanMovePrevious);
        NextCommand = new RelayCommand(() => MoveNext(), () => CanMoveNext);
        InstallCommand = new AsyncRelayCommand(
            () => RunAsync(),
            () => CanRun,
            ReportUnexpectedError);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        RevealSettingsCommand = new RelayCommand(RevealSettings, () => AreSettingsFolded);
        RevealReleaseCommand = new RelayCommand(RevealRelease, () => IsReleaseFolded);
        RollbackCommand = new AsyncRelayCommand(
            () => RollbackThisRunAsync(),
            () => CanRollback,
            ReportUnexpectedError);
        RollbackPreviousRunCommand = new AsyncRelayCommand(
            () => RollbackPreviousRunAsync(),
            () => HasInterruptedRun && !isRunning,
            ReportUnexpectedError);

        // Relaxing the residency policy immediately widens what the models page can offer,
        // so the two pages stay consistent instead of contradicting each other.
        residency.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ResidencyPageViewModel.Policy))
            {
                OnResidencyChanged();
            }
        };

        // The folder the installer was started from, when someone has already put a release
        // beside it. AppContext.BaseDirectory rather than the current directory: a wizard is
        // usually launched from Explorer, whose current directory is not where the file is.
        package.OfferLocalFolder(AppContext.BaseDirectory);
        visited.Add(CurrentPage);
        RebuildSteps();
    }

    public event EventHandler? CloseRequested;

    public InstallerPresentation Presentation { get; }

    /// <summary>For the window to call once closed; the shared settings outlive it.</summary>
    public void DetachPresentation() => Presentation.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Words only: no check, release lookup or detection runs again, and no page, answer or consent
    /// moves; the review reads the client files and PATH as on any refresh. The wizard announces
    /// itself last, because its review is built out of the pages and a binding reads it at once.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildSteps();
        diagnose.Relabel();
        dependencies.Relabel();
        package.AnnounceEverything();
        models.Relabel();
        residency.AnnounceEverything();
        agents.Relabel();
        review.AnnounceEverything();
        foreach (var row in ModelRuns)
        {
            row.Relabel();
        }

        if (finished is { } counts)
        {
            BuildFinishResults(counts.Requested, counts.Installed, counts.Failed);
        }

        finish.AnnounceEverything();
        AnnounceEverything();
    }

    public bool EnableDependencyActions { get; set; }

    /// <summary>
    /// Where the run report and the run journal are written. Outside the LocalAi root on
    /// purpose: that tree is validated against an exact name list on every install, so a
    /// stray file inside it would refuse the next installation. Settable so tests can point
    /// the journal at their own directory instead of the machine's.
    /// </summary>
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAi-installer-logs");

    /// <summary>
    /// The runtime root the stored settings are read from and written into, and whose existence
    /// the review asks about. One value, so the review and the writes cannot disagree.
    /// </summary>
    internal string RuntimeRoot { get; init; } = ModelResidencyPolicyStore.DefaultRuntimeRoot;

    public RelayCommand BackCommand { get; }

    public RelayCommand NextCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Brings back the four settings pages an update folds away.</summary>
    public RelayCommand RevealSettingsCommand { get; }

    /// <summary>Brings back the release page alone.</summary>
    public RelayCommand RevealReleaseCommand { get; }

    public AsyncRelayCommand RollbackCommand { get; }

    public AsyncRelayCommand RollbackPreviousRunCommand { get; }

    public DiagnosePageViewModel Diagnose => diagnose;
    public DependenciesPageViewModel Dependencies => dependencies;
    public PackagePageViewModel Package => package;
    public ModelsPageViewModel Models => models;
    public ResidencyPageViewModel Residency => residency;
    public AgentIntegrationPageViewModel Agents => agents;
    public ReviewApplyPageViewModel Review => review;
    public FinishPageViewModel Finish => finish;

    public ObservableCollection<WizardStep> StepList { get; } = [];

    public InstallerPage CurrentPage
    {
        get => currentPage;
        private set
        {
            if (currentPage == value)
            {
                return;
            }

            currentPage = value;
            // Added to, never removed from. Going back does not un-visit a page.
            visited.Add(value);
            RefreshAll();
        }
    }

    /// <summary>
    /// The pages this run actually reached. Pages rather than an index: a reveal button adds
    /// steps to the rail, so an index recorded before it was pressed would afterwards name a
    /// different step.
    /// </summary>
    private readonly HashSet<InstallerPage> visited = [];

    /// <summary>
    /// The consent, in the words of the run it consents to. One tick over one list: two boxes
    /// on one page train people to tick boxes. A clean reinstall reaches this page after its
    /// removal has run, so the list holds no removal to consent to.
    /// </summary>
    public string ConsentText => Mode switch
    {
        StartChoice.UpdateOrRepair or StartChoice.CleanReinstall => InstallerCulture.Pick(
            "I have read what will change and want to continue.",
            "Я понимаю, что изменится, и хочу продолжить."),
        _ => InstallerCulture.Pick(
            "I have read what will be installed and want to continue.",
            "Я понимаю, что будет установлено, и хочу продолжить."),
    };

    /// <summary>
    /// What this run is, in the title bar. An update that calls itself "LocalAi Setup" is the
    /// same wizard as an install, which is exactly the impression that has to stop.
    /// </summary>
    public string WindowTitle => Mode switch
    {
        StartChoice.UpdateOrRepair => InstallerCulture.Pick(
            "LocalAi — Update or repair",
            "LocalAi — обновление или восстановление"),
        StartChoice.CleanReinstall => InstallerCulture.Pick(
            "LocalAi — Reinstall",
            "LocalAi — переустановка"),
        _ => InstallerCulture.Pick("LocalAi Setup", "Установка LocalAi"),
    };

    /// <summary>
    /// The version line under the product name in the step rail, on every page.
    ///
    /// It is the one place a version is a question rather than a fact, which is the point:
    /// the wizard used to ask for it again on a page of its own, two pages after the start
    /// screen had already settled what the run was for.
    /// </summary>
    /// <summary>
    /// What this run is doing, on every page. Two halves under different rules: what was on
    /// this computer before the run, and what the run is putting there.
    /// </summary>
    public string VersionContext => BuildVersionContext();

    private string BuildVersionContext()
    {
        // The left half is read once and never again. An installation writes the version
        // pointer, so a later read answers a different question than the one this half asks —
        // which is how the finish page turned "0.1.50 → 0.1.51" into "0.1.51 → 0.1.51
        // (repair)" at the moment somebody was reading the outcome.
        if (!hasReadInstalledVersion)
        {
            heldInstalledVersion = readInstalledVersion();
            hasReadInstalledVersion = true;
        }

        // The right half is not history. It is the answer to what this run is doing, and on
        // an installation it is not known when the window opens: the release resolves behind
        // the first page, and a request left at "latest" is checked once more immediately
        // before installing. So it moves until the run settles it.
        var resolved = package.Resolved?.Manifest.ReleaseVersion
            ?? (package.IsResolving
                ? InstallerCulture.Pick("checking…", "проверяю…")
                : InstallerCulture.Pick("no release", "релиз не выбран"));

        if (heldInstalledVersion is null)
        {
            return resolved;
        }

        return string.Equals(heldInstalledVersion, resolved, StringComparison.OrdinalIgnoreCase)
            ? heldInstalledVersion + " → " + resolved +
                InstallerCulture.Pick(" (repair)", " (восстановление)")
            : heldInstalledVersion + " → " + resolved;
    }

    /// <summary>
    /// The label on the control that carries the run out. It is the most-read thing on that
    /// page, and "Install" on a run somebody started as "Update or repair" is exactly the "did
    /// it hear me?" this wording work is about (#257).
    /// </summary>
    public string ActionText => IsUpdate
        ? InstallerCulture.Pick("Update", "Обновить")
        : InstallerCulture.Pick("Install", "Установить");

    /// <summary>
    /// True for the errands that change an existing installation rather than create one. Both
    /// read as an update to the person running them, whatever the removal matrix does first.
    /// </summary>
    private bool IsUpdate => Mode is StartChoice.UpdateOrRepair or StartChoice.CleanReinstall;

    public string StepTitle => WizardText.Title(CurrentPage, IsUpdate, hasRunError);

    public string StepDescription =>
        WizardText.Description(
            CurrentPage,
            IsUpdate,
            ActionText,
            hasRunError,
            isChecking: diagnose.IsChecking,
            isReleaseFolded: IsReleaseFolded,
            areSettingsFolded: AreSettingsFolded);

    public string StepStatus => string.Format(
        InstallerCulture.Pick("Step {0} of {1}", "Шаг {0} из {1}"),
        StepIndex(CurrentPage) + 1,
        Steps.Count);

    public bool IsFinishPage => CurrentPage == InstallerPage.Finish;

    public bool IsProgressPage => CurrentPage == InstallerPage.Progress;

    public bool IsRunning => isRunning;

    public bool IsComplete => isComplete;

    public bool HasRunError => hasRunError;

    public bool IsCanceled => wasCancelled;

    public int Progress => progress;

    public string ProgressText => progressWord is { } word ? ProgressWords.Text(word) : progressText;

    public string? RunLog => runLogMessage;

    /// <summary>
    /// Whether the last run left reversible effects a rollback could take back. False for a
    /// successful run on purpose: rollback is a recovery from a run that went wrong, never
    /// an uninstaller for one that went right.
    /// </summary>
    public bool CanRollback =>
        IsFinishPage &&
        !isRunning &&
        runJournal is { } journal &&
        journal.Snapshot.Outcome is InstallerRunOutcome.Failed or InstallerRunOutcome.Cancelled &&
        journal.Snapshot.HasReversibleWork;

    public bool HasInterruptedRun => interruptedJournal is not null;

    public string? UnexpectedError => unexpected is null
        ? null
        : string.Format(
            InstallerCulture.Pick(
                "Unexpected error: {0}: {1}",
                "Непредвиденная ошибка: {0}: {1}"),
            unexpected.GetType().Name,
            unexpected.Message);

    public bool HasUnexpectedError => unexpected is not null;

    /// <summary>
    /// The sink for exceptions that would otherwise escape an async void entry point — a
    /// command's Execute, or a window event handler — and kill the installer with no
    /// explanation (#209/m4). The wizard stays alive and shows what happened instead; each
    /// new report replaces the previous one.
    /// </summary>
    public void ReportUnexpectedError(Exception exception)
    {
        unexpected = exception;
        OnPropertyChanged(nameof(UnexpectedError));
        OnPropertyChanged(nameof(HasUnexpectedError));
    }

    public string? InterruptedRunNotice => interruptedJournal is not null && interruptedAsFound is { } found
        ? InstallerRunMessages.InterruptedRunNotice(found)
        : rollbackNotice;

    public bool HasInterruptedRunNotice => !string.IsNullOrWhiteSpace(InterruptedRunNotice);

    public int CurrentPageIndex => StepIndex(CurrentPage);

    /// <summary>
    /// Back and Next stay on screen for the whole wizard and only change availability.
    /// Buttons that vanish make the panel jump and hide where the user is.
    /// </summary>
    private readonly bool canReturnToStart;

    /// <summary>
    /// Raised when Back is pressed on the first page: there is no page before it, so leaving
    /// the wizard is the answer, and only the window knows how.
    /// </summary>
    public event EventHandler? ReturnToStartRequested;

    /// <summary>
    /// Every choice can be revisited until the run starts. The first page is included when
    /// there is a screen behind it — the one that asked which errand this is. Reached from
    /// Apps and features there is none, and a Back that opens something the person never saw
    /// is not going back.
    /// </summary>
    public bool CanMovePrevious =>
        !isRunning &&
        !IsFinishPage &&
        CurrentPage != InstallerPage.Progress &&
        (CurrentPage != InstallerPage.Diagnose || canReturnToStart);

    public bool CanMoveNext => !isRunning && CurrentPage switch
    {
        InstallerPage.Diagnose => diagnose.CanContinue,
        InstallerPage.Dependencies => dependencies.CanContinue,
        InstallerPage.Package => package.CanContinue,
        InstallerPage.Models => models.CanContinue,
        InstallerPage.Agents => agents.CanContinue,
        _ => false,
    };

    public bool CanRun =>
        CurrentPage == InstallerPage.Confirm && !isRunning && review.CanApply;

    public bool CanCancel => !IsFinishPage || !isRunning;

    public bool IsNextVisible => CurrentPage is not (
        InstallerPage.Confirm or InstallerPage.Progress or InstallerPage.Finish);

    public bool IsInstallVisible => CurrentPage == InstallerPage.Confirm;

    public string CancelButtonText => IsFinishPage
        ? InstallerCulture.Pick("Close", "Закрыть")
        : InstallerCulture.Pick("Cancel", "Отмена");

    public string? ReviewText => BuildReview();

    public IReadOnlyList<ReviewGroup> ReviewGroups => BuildReviewGroups();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasInitialized)
        {
            return;
        }

        LoadInterruptedRunJournal();
        SeedSettingsFromInstallation();
        SeedTextSearchGate();
        await RefreshEnvironmentDiagnosticsAsync(cancellationToken);

        // Behind the check already running, on every errand. A page whose one job is to name
        // the release should open with the answer rather than with a button; and on the
        // install path the run made this same call anyway, too late for the rail to say so.
        await ResolvePackageAsync(cancellationToken);

        hasInitialized = true;
    }

    /// <summary>
    /// Starts the two settings pages from what this machine already has, rather than from the
    /// defaults a blank machine would get.
    ///
    /// The wizard used to write both policies from its own defaults at the end of every run
    /// without ever reading them, so an update silently turned a relaxed residency back to
    /// strict and an enabled update check back off (#256). The review page listed the value it
    /// was about to write, which reads as "what will be configured" rather than "what is being
    /// replaced" — the letter of informed consent without its spirit.
    ///
    /// Only a file that parses seeds anything. A corrupt update-check policy still means off:
    /// that rule is what stops an unreadable file from becoming permission to use the network.
    /// </summary>
    private void SeedSettingsFromInstallation()
    {
        try
        {
            var runtimeRoot = RuntimeRoot;
            if (!Directory.Exists(runtimeRoot))
            {
                return;
            }

            storedResidency = new ModelResidencyPolicyStore(runtimeRoot).Read().ModelResidency;
            residency.Policy = storedResidency.Value;

            storedUpdateCheck = new UpdateCheckPolicyStore(runtimeRoot).Read().Enabled;
            review.EnableUpdateCheck = storedUpdateCheck.Value;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable installation seeds nothing and the pages keep their safe defaults.
        }
    }

    /// <summary>
    /// Starts the text-search gate's checkbox from what is in <c>~/.claude/settings.json</c> right
    /// now, which is the whole of how a decline survives an update: nothing is remembered, so an
    /// update proposes what is already there rather than restoring something that was turned off,
    /// and somebody who took the entry out by hand is shown that instead of their old answer.
    ///
    /// Separate from <see cref="SeedSettingsFromInstallation"/> deliberately. That one gives up
    /// when the runtime root is missing, and this setting does not live there — a machine with no
    /// LocalAi runtime can still carry an entry from an installation that was removed.
    ///
    /// A clean reinstall therefore comes up unticked, and that is the decision rather than an
    /// oversight. The removal half takes the entry out with the binaries it points at, this half
    /// then reads a file that no longer carries it, and the box shows what is actually there. The
    /// alternative — carrying the previous answer across the handover — would make this the one
    /// setting the installer remembers, and "read the machine, remember nothing" is the property
    /// that makes a decline survive an update at all.
    /// </summary>
    private void SeedTextSearchGate()
    {
        var adapter = new ClaudeConfigurationAdapter(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            InstallationLayout.CreateDefault().BinRoot);
        var gate = false;
        var worktree = false;
        try
        {
            gate = adapter.IsGateRegistered();
            worktree = adapter.IsWorktreeRegistered();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A file that cannot be read holds nothing this run will claim to have found. The
            // path is still seeded, so the review line names it rather than an empty string.
        }

        agents.ApplyHookState(gate, worktree, adapter.SettingsPath);
        agents.UseConfigurationPreview((agent, choice) => PreviewFiles(agent, choice));
    }

    /// <summary>
    /// The paths a choice would write, taken from the adapter that writes them so the review
    /// page and the run cannot disagree. Null where the machine could not be read: the page
    /// then names nothing rather than reporting an empty plan it never obtained.
    /// </summary>
    private static IReadOnlyList<string>? PreviewFiles(string agent, AgentChoice choice)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binRoot = InstallationLayout.CreateDefault().BinRoot;
        try
        {
            var plan = string.Equals(agent, "claude", StringComparison.OrdinalIgnoreCase)
                ? new ClaudeConfigurationAdapter(home, binRoot).Preview(choice.ToCore())
                : new CodexConfigurationAdapter(home, binRoot).Preview(choice.ToCore());
            return [.. plan.Files.Select(file => file.Path)];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks for a journal from a run that never wrote its outcome — a wizard that was
    /// killed, or a machine that lost power, mid-installation. Its effects are real and
    /// undocumented anywhere else, so the first page says what they were and offers to
    /// undo the reversible ones before the user commits to a fresh run on top of them.
    /// </summary>
    public void LoadInterruptedRunJournal()
    {
        try
        {
            interruptedJournal = InstallerRunJournal.FindInterrupted(LogDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            interruptedJournal = null;
        }

        interruptedAsFound = interruptedJournal?.Snapshot;
        RefreshAll();
    }

    public void SetReviewConfirmed(bool confirmed)
    {
        review.IsConfirmed = confirmed;
        RefreshAll();
    }

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        // Moving past the first page with the interrupted-run notice on screen is the
        // explicit "leave it in place" choice the notice offers. Recording it stops every
        // later wizard start from asking about the same abandoned run again.
        if (CurrentPage == InstallerPage.Diagnose && interruptedJournal is { } abandoned)
        {
            try
            {
                abandoned.Finish(InstallerRunOutcome.Abandoned);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // An unwritable journal must not trap the user on the first page.
            }

            interruptedJournal = null;
            rollbackNotice = null;
        }

        CurrentPage = Adjacent(CurrentPage, forward: true);
        return true;
    }

    /// <summary>
    /// The next or previous page this run visits. Reading it off the step list is what keeps
    /// the rail, the counter and the buttons agreeing with each other when pages are folded.
    /// </summary>
    private InstallerPage Adjacent(InstallerPage page, bool forward)
    {
        var steps = Steps;
        var index = -1;
        for (var candidate = 0; candidate < steps.Count; candidate++)
        {
            if (steps[candidate].Page == page)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            return page;
        }

        var target = forward ? index + 1 : index - 1;
        return target >= 0 && target < steps.Count ? steps[target].Page : page;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        // The first page has no page before it, so Back leaves the wizard. Only the window
        // knows what is behind it, which is why this is asked rather than done.
        if (CurrentPage == InstallerPage.Diagnose)
        {
            ReturnToStartRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (CurrentPage == InstallerPage.Package && returnFromReleaseToConfirm)
        {
            returnFromReleaseToConfirm = false;
            CurrentPage = InstallerPage.Confirm;
            return true;
        }

        CurrentPage = Adjacent(CurrentPage, forward: false);
        return true;
    }

    /// <summary>
    /// Cancels a running installation, or closes the wizard when nothing is running. Cancel
    /// is never disabled while work is in flight: it is the only way out of a long install.
    /// </summary>
    public void Cancel()
    {
        if (isRunning)
        {
            runCancellation?.Cancel();
            return;
        }

        wasCancelled = !IsFinishPage;
        OnPropertyChanged(nameof(IsCanceled));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRun)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation = linked;
        var token = linked.Token;

        isRunning = true;
        isComplete = false;
        hasRunError = false;
        wasCancelled = false;
        // A second attempt after a failure reports on itself, not on the one before it.
        configuredAgents.Clear();
        clientOutcomes.Clear();
        if (EnableDependencyActions)
        {
            clientOutcomes.AddRange(agents.Agents
                .Where(agent => agent.Choice != AgentChoice.NoChange)
                .Select(agent => new ClientRunOutcome(agent.DisplayName, ClientRunState.NotAttempted)));
        }

        registrationOutcome = null;
        hooksOutcome = EnableDependencyActions && agents.SettingsChanges
            ? new("Claude Code", ClientRunState.NotAttempted)
            : null;
        reportSaveFailure = null;
        packageOutcome = null;
        packageInstalled = false;
        finish.ReportPath = null;
        finish.RollbackReport = null;
        ModelRuns.Clear();
        OnPropertyChanged(nameof(HasModelRuns));
        finish.Results.Clear();
        finished = null;
        pathMark = RunMark.None;
        pathOutcome = null;
        CurrentPage = InstallerPage.Progress;
        RefreshAll();

        var report = new StringBuilder();
        var successfulActions = 0;
        var failedActions = 0;
        var skippedActions = 0;
        var requested = 0;

        try
        {
            if (!EnableDependencyActions)
            {
                AppendLog(report, "Dry run completed. Nothing was installed.");
                AppendLog(report, RunResults.Tally(requested, successfulActions, skippedActions, failedActions));
                finish.Progress = report.ToString().Trim();
                // The same rows as a real run, all of them saying nothing was attempted — which
                // is what a dry run did.
                BuildFinishResults(requested, successfulActions, failedActions);
                SetProgress(100, ProgressWord.Completed);
                isComplete = true;
                CurrentPage = InstallerPage.Finish;
                return true;
            }

            // Before the first effect, so a process killed at any later point still leaves
            // a record. A journal that cannot be written costs the run its rollback, never
            // the installation itself - but it has to say so, because a user who believes
            // a rollback exists is worse off than one who knows it does not.
            try
            {
                runJournal = InstallerRunJournal.Start(LogDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    NotSupportedException or ArgumentException)
            {
                runJournal = null;
                AppendLog(
                    report,
                    $"Run journal could not be created ({exception.Message}). The run " +
                    "continues, but rollback will not be available for it.");
            }

            await RefreshEnvironmentDiagnosticsAsync(token);

            var selected = dependencies.Dependencies
                .Where(dependency => dependency.IsConsented && dependency.IsInstallable)
                .ToArray();
            requested = selected.Length;
            AppendLog(report, selected.Length == 0
                ? "No prerequisites were selected."
                : $"Prerequisites selected: {selected.Length}.");

            var step = 0;
            foreach (var dependency in selected)
            {
                token.ThrowIfCancellationRequested();
                SetProgress(
                    10 + (80 * step++ / Math.Max(selected.Length, 1)),
                    string.Format(
                        InstallerCulture.Pick("Installing {0}...", "Устанавливаю {0}…"),
                        dependency.Title));

                var definition = ResolveDependencyDefinition(dependency.Id);
                if (definition is null)
                {
                    AppendLog(report, $"{dependency.Title}: no automated installer available.");
                    skippedActions++;
                    continue;
                }

                AppendLog(report, $"{dependency.Title}: installing...");
                var dependencyStep = JournalBegin(
                    InstallerRunEffectKind.DependencyInstall,
                    $"Prerequisite {dependency.Title} ({definition.PackageId})");
                var installed = definition.InstallerKind switch
                {
                    DependencyInstallerKind.WinGet =>
                        await TryInstallDependencyWithWingetAsync(
                            definition,
                            dependency.Title,
                            reinstall: dependency.IsInstalled,
                            token),
                    DependencyInstallerKind.Npm =>
                        await TryInstallDependencyWithNpmAsync(
                            definition,
                            dependency.Title,
                            token),
                    _ => false,
                };
                AppendLog(report, installed
                    ? $"{dependency.Title}: done."
                    : $"{dependency.Title}: failed.");
                if (installed)
                {
                    successfulActions++;
                    JournalComplete(
                        dependencyStep,
                        "Installed machine-wide. This installer does not uninstall shared " +
                        "software: other programs may already depend on it.",
                        isReversible: false);
                }
                else
                {
                    failedActions++;
                    hasRunError = true;
                    JournalFail(dependencyStep, "The install command did not succeed.");
                }

                await RefreshEnvironmentDiagnosticsAsync(token);
            }

            // The package goes last: prerequisites must be in place first, and a failure
            // here must not leave half-installed dependencies unexplained.
            await InstallPackageAsync(report, token);

            // Deliberately after the package, never before it, and never at all when there is
            // no installation to write into. See ResidencyPolicyWriter for why this write is
            // the one that used to poison every later installation on a clean machine.
            ApplyResidencyPolicy(report);

            // Beside it, under the same rule and for the same reason.
            ApplyUpdateCheckPolicy(report);

            // Beside the residency policy and for the same reason: after the package, so the
            // runtime directory exists with the permissions an installation gives it.
            await RecordOllamaLaunchPathAsync(report, token);

            // After the package, for the same reason the models are: the registration points
            // at the launcher this run installed, so writing it before the launcher exists
            // would hand every client a path to nothing.
            await ApplyAgentConfigurationAsync(report, token);

            SetProgress(95, InstallerCulture.Pick("Finalising...", "Завершаю…"));
            AppendLog(report, "Finalising.");
            // The tally goes to the log, where a tally belongs; the page gets a row per
            // thing the run touched.
            AppendLog(report, RunResults.Tally(
                requested,
                successfulActions,
                skippedActions,
                failedActions));
            BuildFinishResults(requested, successfulActions, failedActions);
            SetProgress(100, hasRunError ? ProgressWord.Failed : ProgressWord.Completed);
            isComplete = !hasRunError;
            JournalFinish(hasRunError
                ? InstallerRunOutcome.Failed
                : InstallerRunOutcome.Completed);
            AppendRollbackAvailability(report);
            finish.Progress = report.ToString().Trim();
            SetRunLog(report.ToString());
            CurrentPage = InstallerPage.Finish;
            return !hasRunError;
        }
        catch (OperationCanceledException)
        {
            AppendLog(report, "Cancelled. Actions already applied were left in place.");
            JournalFinish(InstallerRunOutcome.Cancelled);
            AppendRollbackAvailability(report);
            wasCancelled = true;
            hasRunError = true;
            isComplete = false;
            SetProgress(progress, ProgressWord.Cancelled);
            AppendLog(report, RunResults.Tally(
                requested,
                successfulActions,
                skippedActions,
                failedActions));
            BuildFinishResults(requested, successfulActions, failedActions);
            finish.Progress = report.ToString().Trim();
            CurrentPage = InstallerPage.Finish;
            return false;
        }
        catch (Exception exception)
        {
            AppendLog(report, $"Failed: {exception.Message}");
            JournalFinish(InstallerRunOutcome.Failed);
            AppendRollbackAvailability(report);
            hasRunError = true;
            isComplete = false;
            SetProgress(100, ProgressWord.Failed);
            AppendLog(report, RunResults.Tally(
                requested,
                successfulActions,
                skippedActions,
                failedActions));
            BuildFinishResults(requested, successfulActions, failedActions);
            finish.Progress = report.ToString().Trim();
            SetRunLog(report.ToString());
            CurrentPage = InstallerPage.Finish;
            return false;
        }
        finally
        {
            isRunning = false;
            runCancellation = null;
            PersistReport(report);
            BuildFinishResults(requested, successfulActions, failedActions);
            RefreshAll();
        }
    }

    /// <summary>
    /// The run report only ever existed inside this window, so a refused installation explained
    /// itself exactly once and lost the explanation the moment the wizard was closed - which is
    /// precisely when someone needs it. Persisting it turns "it did not install" into a reason.
    ///
    /// Written outside the LocalAi root deliberately. That tree is validated against an exact
    /// name list on every install, so a log directory inside it would refuse the next
    /// installation rather than help diagnose the last one.
    /// </summary>
    private void PersistReport(StringBuilder report)
    {
        if (report is null || report.Length == 0)
        {
            return;
        }

        try
        {
            var directory = LogDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, report.ToString());
            // Its own field. Appended to the summary it was the last line of a box 110 high, and
            // the screenshot that started this work has it clipped mid-path.
            finish.ReportPath = path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            // A missing log must never turn a successful install into a failed one.
            reportSaveFailure = exception.Message;
        }
    }

    /// <summary>
    /// Journal writes never fail the installation they describe: a run that applied its
    /// effects but could not write about them is still a run that applied its effects.
    /// A write failure silently drops the journal, and with it the rollback offer, so the
    /// finish page can only promise what the record can actually deliver.
    /// </summary>
    private string? JournalBegin(InstallerRunEffectKind kind, string description)
    {
        if (runJournal is not { } journal)
        {
            return null;
        }

        try
        {
            return journal.BeginStep(kind, description);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            journal.Dispose();
            runJournal = null;
            return null;
        }
    }

    private void JournalComplete(
        string? stepId,
        string detail,
        bool isReversible,
        InstallerRunUndoData? undo = null)
    {
        if (stepId is null || runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.CompleteStep(stepId, detail, isReversible, undo);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            journal.Dispose();
            runJournal = null;
        }
    }

    private void JournalFail(string? stepId, string detail)
    {
        if (stepId is null || runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.FailStep(stepId, detail);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            journal.Dispose();
            runJournal = null;
        }
    }

    private void JournalFinish(InstallerRunOutcome outcome)
    {
        if (runJournal is not { } journal)
        {
            return;
        }

        try
        {
            journal.Finish(outcome);
        }
        catch (Exception exception) when (IsJournalWriteFailure(exception))
        {
            journal.Dispose();
            runJournal = null;
        }
    }

    private static bool IsJournalWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or InvalidOperationException;

    /// <summary>
    /// One line at the end of a failed or cancelled run saying whether anything can be
    /// taken back. Without it "Cancelled. Actions already applied were left in place." was
    /// the entire recovery story, and the Roll back button below the log had no explanation.
    /// </summary>
    private void AppendRollbackAvailability(StringBuilder report)
    {
        if (runJournal is not { } journal || journal.Snapshot.Outcome is
            not (InstallerRunOutcome.Failed or InstallerRunOutcome.Cancelled))
        {
            return;
        }

        AppendLog(
            report,
            journal.Snapshot.HasReversibleWork
                // The log is English, but this sentence points at a button, and the button
                // carries whatever label the window is showing. An instruction naming a
                // control that is not on screen cannot be followed.
                ? "Some of the applied actions are reversible. Use \"" +
                    PageLabels.RollBackChanges + "\" below to undo them; the journal at " +
                    journal.JournalPath + " records exactly what was done."
                : "Nothing this run applied is reversible by the installer. The journal " +
                    "at " + journal.JournalPath + " records exactly what was done.");
    }

    public async Task RollbackThisRunAsync()
    {
        if (!CanRollback || runJournal is not { } journal)
        {
            return;
        }

        var rollbackReport = await ExecuteRollbackAsync(journal);
        finish.RollbackReport = rollbackReport;
        if (finished is { } counts)
        {
            BuildFinishResults(counts.Requested, counts.Installed, counts.Failed);
        }
        RefreshAll();
    }

    public async Task RollbackPreviousRunAsync()
    {
        if (interruptedJournal is not { } journal || isRunning)
        {
            return;
        }

        var rollbackReport = await ExecuteRollbackAsync(journal);
        // The journal now carries a rollback outcome, so it is no longer "interrupted" and
        // the next start will not ask about it. The notice space shows the result instead.
        interruptedJournal = null;
        rollbackNotice = rollbackReport;
        RefreshAll();
    }

    private async Task<string> ExecuteRollbackAsync(InstallerRunJournal journal)
    {
        isRunning = true;
        RefreshAll();
        try
        {
            var rollback = new InstallerRunRollback(
                processRunner,
                InstallationLayout.CreateDefault(),
                RollbackActivationTimeout);
            return InstallerRunMessages.RollbackReport(
                await rollback.RollbackAsync(journal, CancellationToken.None));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Rollback failed: {exception.Message} The journal at " +
                $"{journal.JournalPath} still records what the run did.";
        }
        finally
        {
            isRunning = false;
            RefreshAll();
        }
    }

    public void SetProgress(int value, string message)
    {
        progress = Math.Clamp(value, 0, 100);
        progressWord = null;
        progressText = message;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void SetProgress(int value, ProgressWord word)
    {
        progress = Math.Clamp(value, 0, 100);
        progressWord = word;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    public void SetRunLog(string message)
    {
        runLogMessage = message;
        finish.RunLog = message;
        OnPropertyChanged(nameof(RunLog));
    }

    public void RefreshNavigationState() => RefreshAll();

    /// <summary>
    /// Registers the MCP servers and writes the managed instruction block for every client
    /// the user chose to configure.
    ///
    /// This step existed only as a page for a while: the wizard collected the choice, printed
    /// it on the review screen, and then never acted on it, so a finished installation left
    /// Claude and Codex unable to reach anything it had installed. Each client is applied
    /// independently — a malformed config in one must not cost the other its integration.
    /// </summary>
    private async Task ApplyAgentConfigurationAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        await ApplyClientIntegrationAsync(report, cancellationToken);
        await ApplyClaudeHooksAsync(report, cancellationToken);
    }

    /// <summary>
    /// The hooks this product writes into Claude Code's settings, applied as one step with one
    /// plan and its own failure.
    ///
    /// That separation is the point rather than tidiness. <c>settings.json</c> is a file the
    /// person edits by hand, so a misplaced comma in it is an ordinary event — and inside the
    /// loop above, that comma would cost the MCP registrations, which are the expensive half of
    /// the installation. Here it costs only the gate, and the line above still reads true (#423).
    /// </summary>
    private async Task ApplyClaudeHooksAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        if (!agents.SettingsChanges)
        {
            // Includes the case the consent turns on: an unticked box over a machine with
            // nothing registered must not open the file at all, because an empty `hooks: {}` is
            // an effect nobody agreed to and it would reformat their document on the way in.
            AppendLog(report, "Claude Code hooks: left as they were.");
            return;
        }

        if (packageOutcome is null || !packageInstalled)
        {
            AppendLog(
                report,
                "Claude Code hooks: skipped, because the LocalAi package was not installed " +
                "and this run did not get far enough to change it.");
            hasRunError = true;
            return;
        }

        var registering = agents.RefusesTextSearch;
        hooksOutcome = new("Claude Code", ClientRunState.Configuring);
        var placingWorktrees = agents.RegistersWorktrees;
        string? step = null;
        try
        {
            var adapter = new ClaudeConfigurationAdapter(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                InstallationLayout.CreateDefault().BinRoot);
            var plan = adapter.PreviewSettings(registering, placingWorktrees);
            if (!plan.HasChanges)
            {
                hooksOutcome = new("Claude Code", ClientRunState.AlreadyConfigured);
                AppendLog(report, "Claude Code hooks: already as requested, nothing to change.");
                return;
            }

            step = JournalBegin(
                InstallerRunEffectKind.AgentConfiguration,
                "Claude text-search gate");
            await adapter.ApplyAsync(plan, cancellationToken);
            hooksOutcome = new("Claude Code", ClientRunState.Applied);
            if (!configuredAgents.Contains("Claude Code"))
            {
                configuredAgents.Add("Claude Code");
            }
            JournalComplete(
                step,
                "Applied to " + string.Join(", ", plan.Files.Select(file => file.Path)) + ".",
                isReversible: true,
                new InstallerRunUndoData(
                    Files: plan.Files.Select(BuildAgentFileUndo).ToArray()));
            // Built from the effects that actually moved rather than from a fixed sentence
            // naming both: this step writes one file for two independent changes, and a line
            // claiming a registration that did not happen is worse than a line that is terse.
            var effects = new List<string>();
            if (agents.GateChanges)
            {
                effects.Add(registering
                    ? "text-search refusal registered"
                    : "text-search refusal removed");
            }

            if (agents.WorktreeChanges)
            {
                effects.Add(placingWorktrees
                    ? "worktree placement registered"
                    : "worktree placement removed");
            }

            AppendLog(
                report,
                "Claude Code hooks: " + string.Join("; ", effects) + " — in " +
                string.Join(", ", plan.Files.Select(file => file.Path)) +
                ". Restart Claude Code to pick this up.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            hooksOutcome = new("Claude Code", ClientRunState.Failed, exception.Message);
            JournalFail(step, exception.Message);
            AppendLog(
                report,
                $"Claude Code hooks: not changed — {exception.Message} " +
                "The rest of the installation is unaffected.");
            hasRunError = true;
        }
    }

    private async Task ApplyClientIntegrationAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        var requested = agents.Agents
            .Where(agent => agent.Choice != AgentChoice.NoChange)
            .ToArray();
        if (requested.Length == 0)
        {
            AppendLog(report, "Client applications: left unchanged.");
            return;
        }

        if (packageOutcome is null || !packageInstalled)
        {
            AppendLog(
                report,
                "Client applications: skipped, because the LocalAi package was not installed " +
                "and the registration would point at a launcher that is not there.");
            hasRunError = true;
            return;
        }

        SetProgress(94, InstallerCulture.Pick("Configuring client applications...", "Настраиваю клиентские приложения…"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binRoot = InstallationLayout.CreateDefault().BinRoot;
        foreach (var agent in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomeIndex = clientOutcomes.FindIndex(outcome => outcome.Name == agent.DisplayName);
            clientOutcomes[outcomeIndex] = new(agent.DisplayName, ClientRunState.Configuring);
            var isClaude = string.Equals(agent.Agent, "claude", StringComparison.OrdinalIgnoreCase);
            string? agentStep = null;
            try
            {
                var claude = isClaude ? new ClaudeConfigurationAdapter(home, binRoot) : null;
                var codex = isClaude ? null : new CodexConfigurationAdapter(home, binRoot);
                var choice = agent.Choice.ToCore();
                var plan = claude is not null
                    ? claude.Preview(choice)
                    : codex!.Preview(choice);
                if (!plan.HasChanges)
                {
                    clientOutcomes[outcomeIndex] = new(agent.DisplayName, ClientRunState.AlreadyConfigured);
                    AppendLog(report, $"{plan.AgentName}: already configured, nothing to change.");
                    continue;
                }

                agentStep = JournalBegin(
                    InstallerRunEffectKind.AgentConfiguration,
                    $"{plan.AgentName} client configuration");
                if (claude is not null)
                {
                    await claude.ApplyAsync(plan, cancellationToken);
                }
                else
                {
                    await codex!.ApplyAsync(plan, cancellationToken);
                }

                JournalComplete(
                    agentStep,
                    "Applied to " +
                    string.Join(", ", plan.Files.Select(file => file.Path)) + ".",
                    isReversible: true,
                    new InstallerRunUndoData(
                        Files: plan.Files.Select(BuildAgentFileUndo).ToArray()));
                configuredAgents.Add(agent.DisplayName);
                clientOutcomes[outcomeIndex] = new(agent.DisplayName, ClientRunState.Applied);
                AppendLog(
                    report,
                    $"{plan.AgentName}: {agent.Choice.EnglishTitle()} applied to " +
                    string.Join(", ", plan.Files.Select(file => file.Path)) +
                    ". Restart the client to pick it up.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A hand-edited config that the adapter refuses to rewrite is the common case
                // here, and it is recoverable by hand, so it must not fail the installation.
                // The adapter also restores its own partial writes before throwing, which is
                // why a failed step leaves nothing for rollback to undo.
                JournalFail(agentStep, exception.Message);
                clientOutcomes[outcomeIndex] = new(agent.DisplayName, ClientRunState.Failed, exception.Message);
                AppendLog(
                    report,
                    $"{agent.DisplayName}: not configured — {exception.Message} " +
                    "The rest of the installation is unaffected.");
                hasRunError = true;
            }
        }
    }

    /// <summary>
    /// The restore source is the .bak file the adapter already writes beside the config; a
    /// backup exists exactly when the file existed before the run, so its presence after a
    /// successful apply doubles as the existed-before record. The journal keeps hashes for
    /// both sides so rollback can prove the backup is the pre-install content and that the
    /// file has not been hand-edited since the run.
    /// </summary>
    private static InstallerRunFileUndo BuildAgentFileUndo(AgentConfigurationFilePlan file)
    {
        var backupExists = File.Exists(file.BackupPath);
        return new(
            file.Path,
            backupExists,
            file.ExpectedSha256,
            null,
            backupExists ? file.BackupPath : null,
            file.AfterSha256);
    }

    /// <summary>
    /// Records the Ollama the installer verified, so the broker can start it when a model call
    /// finds it down instead of leaving the work waiting for somebody to notice.
    ///
    /// Only the validated answer is recorded. The detector falls back to a plain PATH lookup
    /// when the uninstall entry does not match, and a path resolved that way is exactly what a
    /// background process must never launch unattended -- the search path is writable by the
    /// user. No record is better than a record of the wrong file.
    /// </summary>
    private async Task RecordOllamaLaunchPathAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        string? recordStep = null;
        try
        {
            var installed = await installedApplications.FindOllamaAsync(cancellationToken);
            if (installed?.ExecutablePath is not { Length: > 0 } path)
            {
                AppendLog(
                    report,
                    "Ollama start-on-demand: not recorded, because no verified installation of "
                    + "Ollama was found. A model call will say Ollama is not answering rather "
                    + "than starting it.");
                return;
            }

            var recordPath = Path.Combine(
                RuntimeRoot,
                OllamaLaunchRecord.FileName);
            var priorRecord = CaptureFileState(recordPath);
            recordStep = JournalBegin(
                InstallerRunEffectKind.OllamaLaunchRecord,
                "Ollama start-on-demand record");
            new OllamaLaunchRecordStore(RuntimeRoot)
                .Save(path, installed.DetectedVersion);
            JournalComplete(
                recordStep,
                $"Wrote {recordPath}.",
                isReversible: true,
                new InstallerRunUndoData(Files: [BuildFileUndo(recordPath, priorRecord)]));
            AppendLog(report, $"Ollama start-on-demand: recorded {path}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            JournalFail(recordStep, exception.Message);
            AppendLog(
                report,
                $"Ollama start-on-demand: not recorded ({exception.Message}).");
        }
    }

    private void ApplyResidencyPolicy(StringBuilder report)
    {
        string? residencyStep = null;
        try
        {
            var runtimeRoot = RuntimeRoot;
            var policyPath = RuntimeDirectories.SettingsFileForWriting(
                runtimeRoot,
                BrokerPolicy.FileName);
            // Captured before the write, journalled only when the writer will actually
            // write: mirroring its own installation check keeps the journal free of steps
            // for effects that never happened.
            var priorPolicy = Directory.Exists(runtimeRoot)
                ? CaptureFileState(policyPath)
                : null;
            if (priorPolicy is not null)
            {
                residencyStep = JournalBegin(
                    InstallerRunEffectKind.ResidencyPolicy,
                    $"Model residency policy ({residency.Policy})");
            }

            var outcome = ResidencyPolicyWriter.Apply(runtimeRoot, residency.Policy);
            if (outcome == ResidencyPolicyOutcome.SkippedWithoutInstallation)
            {
                JournalFail(residencyStep, "Nothing was written: LocalAi is not installed.");
                AppendLog(
                    report,
                    "Model residency policy: not stored, because LocalAi is not installed on " +
                    "this computer. Storing it would create the LocalAi directory without the " +
                    "permissions an installation requires, and every later installation would " +
                    "then refuse it. The next run asks for this setting again.");
                return;
            }

            JournalComplete(
                residencyStep,
                $"Wrote {policyPath}.",
                isReversible: true,
                priorPolicy is { } captured
                    ? new InstallerRunUndoData(Files: [BuildFileUndo(policyPath, captured)])
                    : null);
            AppendLog(report, $"Model residency policy: {residency.Policy}.");
            if (residency.Policy != ModelResidencyPolicy.RequireFullVram)
            {
                AppendLog(
                    report,
                    "Warning: residency is relaxed, so models may be slower than a fully " +
                    "resident load. Degraded answers will say so.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            JournalFail(residencyStep, exception.Message);
            AppendLog(report, $"Could not store the residency policy: {exception.Message}");
            hasRunError = true;
        }
    }

    /// <summary>
    /// Stores the answer to the update-check question, beside the residency policy and for the
    /// same reasons: after the package, and only into an installation that exists.
    ///
    /// A failure here never fails the run. Not knowing whether a newer release exists is a
    /// smaller problem than an installation reported as failed, and the setting can be changed
    /// afterwards with one command — which the log names.
    /// </summary>
    private void ApplyUpdateCheckPolicy(StringBuilder report)
    {
        string? step = null;
        try
        {
            var runtimeRoot = RuntimeRoot;
            var policyPath = RuntimeDirectories.SettingsFileForWriting(
                runtimeRoot,
                UpdateCheckPolicy.FileName);
            var prior = Directory.Exists(runtimeRoot) ? CaptureFileState(policyPath) : null;
            if (prior is not null)
            {
                step = JournalBegin(
                    InstallerRunEffectKind.ResidencyPolicy,
                    "Update check policy (" + (review.EnableUpdateCheck ? "on" : "off") + ")");
            }

            var outcome = UpdateCheckPolicyWriter.Apply(runtimeRoot, review.EnableUpdateCheck);
            if (outcome == ResidencyPolicyOutcome.SkippedWithoutInstallation)
            {
                AppendLog(
                    report,
                    "Update check: not stored, because LocalAi is not installed on this " +
                    "computer. Nothing will be looked up. Set it later with " +
                    "`localai policy set --update-check on`.");
                return;
            }

            JournalComplete(
                step,
                $"Wrote {policyPath}.",
                isReversible: true,
                prior is { } captured
                    ? new InstallerRunUndoData(Files: [BuildFileUndo(policyPath, captured)])
                    : null);
            AppendLog(
                report,
                review.EnableUpdateCheck
                    ? "Update check: on. " + UpdateCheckPolicy.Disclosure
                    : "Update check: off. Nothing about releases is looked up; turn it on " +
                        "later with `localai policy set --update-check on`.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            JournalFail(step, exception.Message);
            AppendLog(
                report,
                $"Could not store the update check setting: {exception.Message}. The " +
                "installation is unaffected; set it with `localai policy set --update-check " +
                "on` if you want it.");
        }
    }

    /// <summary>The pre-effect content of a file the run is about to change, or its absence.</summary>
    private sealed record CapturedFileState(bool Existed, byte[] Bytes);

    private static CapturedFileState CaptureFileState(string path) =>
        File.Exists(path)
            ? new(true, File.ReadAllBytes(path))
            : new(false, []);

    /// <summary>
    /// Pairs the captured pre-effect content with what is on disk now. Small files travel
    /// inline in the journal; anything above the cap keeps only its hash, which rollback
    /// reports as unrecoverable instead of guessing.
    /// </summary>
    private static InstallerRunFileUndo BuildFileUndo(string path, CapturedFileState before)
    {
        var after = File.Exists(path) ? File.ReadAllBytes(path) : [];
        return new(
            path,
            before.Existed,
            InstallerRunJournal.Sha256Hex(before.Bytes),
            before.Existed && before.Bytes.Length <= InstallerRunJournal.MaximumInlineContentBytes
                ? Convert.ToBase64String(before.Bytes)
                : null,
            null,
            InstallerRunJournal.Sha256Hex(after));
    }

    private int StepIndex(InstallerPage page)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Page == page)
            {
                return index;
            }
        }

        return 0;
    }

    /// <summary>Where the run wrote its report, said on the finish page.</summary>
    private static string SavedTo => InstallerCulture.Pick(
        "Report saved to {0}.",
        "Отчёт сохранён в {0}.");

    /// <summary>
    /// Says when this run replaces a setting the machine already had.
    ///
    /// "Model residency: RequireFullVram" reads as what will be configured. On a machine that
    /// chose AllowCpu it is also a silent reversal, and the page that promises to list every
    /// effect has to say which of the two it is (#256).
    /// </summary>
    private string StoredResidencyNote() =>
        storedResidency is { } stored && stored != residency.Policy
            ? string.Format(
                InstallerCulture.Pick(
                    " (currently {0} — this run changes it)",
                    " (сейчас {0} — этот запуск это меняет)"),
                ResidencyPageViewModel.Name(stored))
            : string.Empty;

    /// <summary>
    /// The update check is a stored setting too, so a run that turns it off says so rather
    /// than leaving the change to be discovered later.
    /// </summary>
    private string UpdateCheckReviewLine()
    {
        var wanted = Switch(review.EnableUpdateCheck);
        if (storedUpdateCheck is { } stored && stored != review.EnableUpdateCheck)
        {
            return string.Format(
                InstallerCulture.Pick(
                    "Update check: {0} (currently {1} — this run changes it)",
                    "Проверка обновлений: {0} (сейчас {1} — этот запуск это меняет)"),
                wanted,
                Switch(stored));
        }

        return string.Format(
            InstallerCulture.Pick("Update check: {0}", "Проверка обновлений: {0}"),
            wanted);

        static string Switch(bool on) => on
            ? InstallerCulture.Pick("on", "включена")
            : InstallerCulture.Pick("off", "выключена");
    }

    private string BuildReview() => string.Join(Environment.NewLine, ReviewGroups.SelectMany(group => group.Lines)).Trim();

    private IReadOnlyList<ReviewGroup> BuildReviewGroups()
    {
        package.ActionText = ActionText;
        var groups = new List<ReviewGroup>
        {
            new(PageLabels.ReviewRelease, ReviewGroup.SplitLines(package.ReviewText)),
            new(PageLabels.ReviewComponents, ReviewGroup.SplitLines(dependencies.ReviewText)),
            new(PageLabels.ReviewModels, ReviewGroup.SplitLines(models.ReviewText, residency.ReviewText + StoredResidencyNote())),
            new(PageLabels.ReviewClients, ReviewGroup.SplitLines(agents.ReviewText, agents.GateReviewText,
                agents.WorktreeReviewText, LauncherPathReviewLine())),
            new(PageLabels.ReviewAdditional, ReviewGroup.SplitLines(UpdateCheckReviewLine())),
        };
        var builder = new StringBuilder();
        if (residency.HasWarning)
        {
            builder.AppendLine();
            builder.AppendLine(
                InstallerCulture.Pick("Warning: ", "Внимание: ") + residency.Warning);
        }

        // The package is the point of the run, and its absence is the one line on this page a
        // reader skims past — "not resolved" sits in a list of five neutral statements. An
        // installation that goes ahead without it leaves the clients unconfigured too, so it
        // is worth saying twice, in the register the rest of the page reserves for warnings.
        if (!package.HasPackage)
        {
            builder.AppendLine();
            // What the run will still do (#264). The settings are written whenever the runtime
            // root exists, by the same Directory.Exists the two writers ask.
            var prerequisites = dependencies.Dependencies
                .Any(dependency => dependency.IsConsented && dependency.IsInstallable);
            var remaining = (prerequisites, Directory.Exists(RuntimeRoot)) switch
            {
                (true, true) => InstallerCulture.Pick(
                    "The prerequisites above will still be installed, and the video memory and " +
                    "update check settings stored.",
                    "Компоненты, перечисленные выше, всё равно будут установлены, а настройки " +
                    "видеопамяти и проверки обновлений — сохранены."),
                (true, false) => InstallerCulture.Pick(
                    "Only the prerequisites above will be applied.",
                    "Будут применены только компоненты, перечисленные выше."),
                (false, true) => InstallerCulture.Pick(
                    "The video memory and update check settings will still be stored.",
                    "Настройки видеопамяти и проверки обновлений всё равно будут сохранены."),
                (false, false) => InstallerCulture.Pick(
                    "Nothing will be applied.",
                    "Ничего применено не будет."),
            };
            // The button under the list exists only while the release step is folded; anywhere
            // else that step is in the rail before this one.
            var route = IsReleaseFolded
                ? InstallerCulture.Pick(
                    "Choose a release below, or point the installer at a folder holding one, " +
                    "and verify it before continuing.",
                    "Выберите релиз ниже или укажите папку с релизом и проверьте его, прежде " +
                    "чем продолжать.")
                : string.Format(
                    InstallerCulture.Pick(
                        "Go back to the {0} step to choose a release or a folder holding one.",
                        "Вернитесь к шагу «{0}», чтобы выбрать релиз или папку с ним."),
                    WizardText.Steps().First(step => step.Page == InstallerPage.Package).Title);
            builder.AppendLine(string.Format(
                InstallerCulture.Pick(
                    "Warning: no release has been verified, so LocalAi itself will not be " +
                    "installed, and no models, client applications or PATH entry will be set " +
                    "up. {0} {1}",
                    "Внимание: ни один релиз не проверен, поэтому сам LocalAi установлен не " +
                    "будет, а модели, клиентские приложения и запись в PATH настроены не " +
                    "будут. {0} {1}"),
                remaining,
                route));
        }

        if (builder.Length > 0)
        {
            groups.Add(new(PageLabels.ReviewWarnings, ReviewGroup.SplitLines(builder.ToString())));
        }
        return groups;
    }

    /// <summary>
    /// A step is done when it was actually reached, not when it happens to sit left of where
    /// the reader is now.
    ///
    /// Position is not history. The reveal buttons jump backwards to a folded page, and a
    /// finish page can be reached without the pages before it having run — so "everything to
    /// my left happened" marks work that never took place, which is the one thing a step rail
    /// must not do.
    /// </summary>
    private void RebuildSteps()
    {
        StepList.Clear();
        var current = StepIndex(CurrentPage);
        for (var index = 0; index < Steps.Count; index++)
        {
            StepList.Add(new WizardStep(
                Steps[index].Title,
                index == current,
                index != current && visited.Contains(Steps[index].Page)));
        }
    }

    private void RefreshAll()
    {
        RebuildSteps();
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepDescription));
        OnPropertyChanged(nameof(StepStatus));
        OnPropertyChanged(nameof(VersionContext));
        OnPropertyChanged(nameof(AreSettingsFolded));
        OnPropertyChanged(nameof(IsReleaseFolded));
        OnPropertyChanged(nameof(StepList));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CanMovePrevious));
        OnPropertyChanged(nameof(CanMoveNext));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsNextVisible));
        OnPropertyChanged(nameof(IsInstallVisible));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(IsFinishPage));
        OnPropertyChanged(nameof(IsProgressPage));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(HasRunError));
        OnPropertyChanged(nameof(ReviewText));
        OnPropertyChanged(nameof(ReviewGroups));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(HasInterruptedRun));
        OnPropertyChanged(nameof(InterruptedRunNotice));
        OnPropertyChanged(nameof(HasInterruptedRunNotice));
        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RollbackCommand.RaiseCanExecuteChanged();
        RollbackPreviousRunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Appends a line and publishes it immediately.
    ///
    /// The log used to be assigned only after the run finished, so the progress page stayed
    /// blank for the whole installation — including the minutes spent downloading — and gave
    /// no sign that anything was happening.
    /// </summary>
    private void AppendLog(StringBuilder report, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        report.AppendLine(message);
        finish.Progress = report.ToString().TrimEnd();
    }

    /// <summary>
    /// What the page a run ends on opens with. Every row is a thing the run touched, marked with
    /// the same vocabulary the model rows use, and the one on the end answers the only question
    /// somebody has there.
    /// </summary>
    private void BuildFinishResults(int requested, int installed, int failed)
    {
        finished = (requested, installed, failed);
        SettleModelRuns(wasCancelled);
        finish.Results.Clear();
        finish.Results.Add(RunResults.Package(
            packageOutcome is { } outcome ? RunResults.PackageDetail(outcome, packageInstalled) : null,
            packageInstalled));
        finish.Results.Add(RunResults.Prerequisites(requested, installed, failed));
        finish.Results.Add(RunResults.Models(ModelRuns));
        foreach (var model in ModelRuns)
        {
            finish.Results.Add(new RunResultRow(model.Mark, model.Tag, model.Detail));
        }

        if (clientOutcomes.Count == 0)
        {
            finish.Results.Add(RunResults.Clients(configuredAgents));
        }
        else
        {
            foreach (var client in clientOutcomes)
            {
                finish.Results.Add(RunResults.Client(client));
            }
        }

        if (registrationOutcome is { } registration)
        {
            finish.Results.Add(RunResults.Registration(registration));
        }

        if (hooksOutcome is { } hooks)
        {
            finish.Results.Add(RunResults.Client(hooks with
            {
                Name = InstallerCulture.Pick("Claude Code hooks", "Хуки Claude Code"),
            }));
        }
        if (pathMark != RunMark.None || pathOutcome is not null)
        {
            finish.Results.Add(RunResults.PathEntry(
                pathMark,
                pathOutcome is { } path ? RunResults.PathDetail(path) : PageLabels.ResultNotAttempted));
        }

        finish.NextSteps = InstallerRunMessages.NextSteps(new InstallerRunMessageFacts(
            WasCancelled: wasCancelled,
            HasRunError: hasRunError,
            NoValidatedModels: ModelRuns.Count > 0 &&
                !ModelRuns.Any(model => model.State is ModelRunState.Ready or ModelRunState.ReadyWithLimitations),
            HasRollbackReport: finish.HasRollbackReport,
            CanRollback: CanRollback,
            ReportSaveFailure: reportSaveFailure,
            ClientNextSteps: finish.HasRollbackReport ? null : RunResults.NextSteps(configuredAgents),
            PathAdded: pathOutcome == PathOutcome.Added));
    }

    private async Task RefreshEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        diagnose.IsChecking = true;
        RefreshAll();
        try
        {
            await RunEnvironmentDiagnosticsAsync(cancellationToken);
        }
        finally
        {
            diagnose.IsChecking = false;
            RefreshAll();
        }
    }

    private async Task RunEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var diagnosis = await detectEnvironment(cancellationToken);
        environmentDiagnosis = diagnosis;

        diagnose.Load(diagnosis);
        residency.HasUsableAdapter = diagnose.HasUsableAdapter;
        // The card the rule is about, named the way the machine reports it. The one the
        // recommendation weighs against is the one with the most dedicated video memory, so
        // that is the one the group names.
        residency.WeighedAdapter = diagnosis.Gpu.Adapters
            .Where(adapter => !adapter.IsSoftware)
            .OrderByDescending(adapter => adapter.DedicatedLocalBytes)
            .FirstOrDefault();
        agents.ApplyDetection(diagnosis.Agents);
        await RefreshRecommendationAsync(diagnosis, cancellationToken);

        // Detection records presence only; it must not grant consent on the user's behalf.
        dependencies.SetInstalled("Git", diagnosis.Git.State == DependencyState.Detected);
        dependencies.SetInstalled("Ollama", diagnosis.Ollama.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "GitHubCli",
            diagnosis.GitHubCli.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "DotNetSdk",
            diagnosis.DotNetSdk.State == DependencyState.Detected);
        dependencies.SetInstalled("NodeJs", diagnosis.NodeJs.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "ScipTypeScript",
            diagnosis.ScipTypeScript.State == DependencyState.Detected);
        dependencies.SetInstalled("Python", diagnosis.Python.State == DependencyState.Detected);
        dependencies.SetInstalled(
            "ScipPython",
            diagnosis.ScipPython.State == DependencyState.Detected);

        OnPropertyChanged(nameof(Diagnose));
        OnPropertyChanged(nameof(Dependencies));
        RefreshAll();
    }

    /// <summary>
    /// Recomputes which catalogue models fit this machine. Sizes come from the public model
    /// registry, so this is a network call: it must never prevent the wizard from running,
    /// and an offline machine simply gets the catalogue without sizes.
    /// </summary>
    private async Task RefreshRecommendationAsync(
        EnvironmentDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        try
        {
            using var registry = new OllamaRegistryClient();
            var recommender = new CatalogModelRecommender(registry);
            lastRecommendation = await recommender.RecommendAsync(
                diagnosis.Gpu,
                models.CatalogModels,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lastRecommendation = CatalogRecommendation.Empty;
        }

        models.ApplyRecommendation(
            lastRecommendation,
            residency.Policy == ModelResidencyPolicy.RequireFullVram);
    }

    /// <summary>
    /// Re-applies the last recommendation under the current residency choice, so relaxing
    /// the policy immediately widens what the models page offers.
    /// </summary>
    public void OnResidencyChanged()
    {
        models.ApplyRecommendation(
            lastRecommendation,
            residency.Policy == ModelResidencyPolicy.RequireFullVram);
        RefreshAll();
    }

    /// <summary>
    /// The GitHub CLI exactly where the environment check found it, or null if it found nothing.
    ///
    /// Resolving it once and reusing the answer is the point. The feed defaults to the bare name
    /// "gh" and leaves the lookup to CreateProcess, which searches only the PATH this process
    /// inherited at startup — so on a machine where the CLI was installed during the session the
    /// check reported it present, from the registry, and the very next step still failed with
    /// "The GitHub CLI could not be started". Two lookups with different rules cannot disagree
    /// if there is only one.
    /// </summary>
    private string? GitHubCliPath =>
        environmentDiagnosis?.GitHubCli is
            { State: DependencyState.Detected, ExecutablePath: { } path }
            ? path
            : null;

    /// <summary>
    /// The release feed for this machine: anonymous HTTPS first, the GitHub CLI behind it.
    ///
    /// The repository is public, so an installation must not begin by demanding an account —
    /// that used to be three obstacles (an account, an invitation, `gh auth login`) in front
    /// of a download anyone is allowed to make. The CLI is still offered to the chain when it
    /// is installed and signed in, because a fork kept private stays installable that way,
    /// and because a network that blocks the release host may not block the API.
    ///
    /// Neither path is trusted for being itself: the manifest is checked against the embedded
    /// key and the package against the hash in that manifest, whichever one fetched them.
    /// </summary>
    /// <remarks>
    /// A chosen folder replaces the chain rather than joining it. Falling back to GitHub after a
    /// folder failed would be the opposite of what asking for a folder means — on an air-gapped
    /// machine it turns one clear error into a long timeout, and on any machine it would install
    /// something other than what the operator pointed at.
    /// </remarks>
    private IReleaseFeed CreateFeed() =>
        feedForTests
        ?? (string.IsNullOrWhiteSpace(package.SourceFolder)
            ? new FallbackReleaseFeed(
                anonymousFeed,
                GitHubCliPath is null
                    ? null
                    : new GitHubReleaseFeed(processRunner, gitHubCliPath: GitHubCliPath))
            : new DirectoryReleaseFeed(package.SourceFolder));

    private static DependencyDefinition? ResolveDependencyDefinition(string dependencyId) =>
        dependencyId switch
        {
            "Git" => DependencyCatalog.Git,
            "Ollama" => DependencyCatalog.Ollama,
            "GitHubCli" => DependencyCatalog.GitHubCli,
            "DotNetSdk" => DependencyCatalog.DotNetSdk,
            "NodeJs" => DependencyCatalog.NodeJs,
            "ScipTypeScript" => DependencyCatalog.ScipTypeScript,
            "Python" => DependencyCatalog.Python,
            "ScipPython" => DependencyCatalog.ScipPython,
            _ => null,
        };

    /// <summary>
    /// Scratch space for downloaded release assets.
    ///
    /// Deliberately outside the installation layout. It first lived in
    /// %LOCALAPPDATA%\LocalAi\installer\downloads, which looked tidy and broke every
    /// install: that directory belongs to the installer and must contain exactly "backups"
    /// and "transaction.lock", so an extra folder made the layout unrecognisable to the
    /// installer's own validation — after the package had already been downloaded.
    /// </summary>
    private static string WorkingDirectory => Path.Combine(
        Path.GetTempPath(),
        "LocalAi-installer");

    /// <summary>
    /// Resolves and verifies the requested release. Failures are shown on the package page
    /// rather than thrown: an unresolved package does not stop the rest of the installation.
    /// </summary>
    public async Task ResolvePackageAsync(CancellationToken cancellationToken = default)
    {
        // The question this resolve is answering, captured whole. The package page is
        // visible on an installation and its tag box updates on every keystroke, so the
        // question can change while a resolve is in flight — by another resolve starting, or
        // simply by the tag or the folder being edited. Comparing the question itself covers
        // both; a counter only covered the first.
        var asked = (package.ReleaseVersion, package.SourceFolder);
        var mine = ++resolveGeneration;
        package.BeginResolving();
        RefreshAll();
        try
        {
            var feed = CreateFeed();
            package.InstalledVersionDirectory = InstalledVersionDirectory();
            // "latest" is not a tag GitHub knows; resolve it to the newest published one so
            // the field can keep its convenient default.
            var tag = await feed.ResolveTagAsync(package.ReleaseVersion, cancellationToken);
            var release = await feed.ResolveAsync(tag, WorkingDirectory, cancellationToken);
            if (mine != resolveGeneration ||
                asked != (package.ReleaseVersion, package.SourceFolder))
            {
                // The question changed while this was in flight — the person typed a
                // different tag, or chose a folder. Answering the old one here would put a
                // release on screen that nobody asked for and install it.
                return;
            }

            resolvedTag = tag;
            package.SelectResolvedRelease(release, tag);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The token, not the exception type: a black-holed connection times out inside
            // HttpClient and arrives as TaskCanceledException, which is an
            // OperationCanceledException and is not a cancellation at all. Filtering by type
            // let it escape as "cancelled by the user", abandoning the run and leaving the
            // rail saying "checking…" for the rest of the session.
            //
            // Reported even when the question has moved on: a stale answer must be silent,
            // but a stale failure that escapes becomes an unexpected-error banner, which is
            // louder than the fresh one it is not.
            var stale = mine != resolveGeneration ||
                asked != (package.ReleaseVersion, package.SourceFolder);
            if (!stale)
            {
                package.ReportUnavailable(
                    exception is ReleaseResolutionException
                        ? exception.Message
                        : $"The release could not be checked: {exception.Message}");
                // The box that installs from a folder lives on the page an update folds away,
                // so a machine that cannot reach the feed had no route to it at all. A failed
                // resolve is exactly the run that needs one: the page joins the rail and the
                // reader walks through it, instead of being told where to look.
                //
                // Not RevealRelease(): that also arms the return to the confirm page, which is
                // the button's business and would send Back to the wrong place from here.
                releaseRevealed = true;
            }
            else
            {
                // Nothing to say about a question nobody is asking, but the flag it set has
                // to come down or the rail keeps claiming a check is running.
                package.EndResolving();
            }
        }

        RefreshAll();
    }

    /// <summary>
    /// The version directory currently active, or null when nothing is installed or the
    /// pointer cannot be read. Only used to tell the user a release is already installed, so a
    /// failure here costs a warning, not the run.
    /// </summary>
    private static string? InstalledVersionDirectory()
    {
        try
        {
            var snapshot = new ExistingLocalAiInspector(new SystemFileSystemProbe())
                .Inspect(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData));
            return snapshot.Version;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-resolves a request left at "latest" immediately before installing it.
    ///
    /// The package page checks the release when it is opened, and a wizard can sit on the
    /// confirmation step for a long time — long enough for a release to be published in
    /// between. Installing what was newest when the window opened, under a field that still
    /// says "latest", is the one outcome that word rules out. A specific tag is left alone: it
    /// was asked for by name. So is a run with no verified release: its review said LocalAi will
    /// not be installed, and a re-check must not turn that into an installation.
    /// </summary>
    /// <summary>
    /// Internal rather than private so a test can drive it: reaching it through RunAsync means
    /// enabling the real dependency installation, and the behaviour it holds — that a verified
    /// release survives a re-check the feed refuses — shipped broken for want of a test.
    /// </summary>
    internal async Task RefreshLatestBeforeInstallAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        if (!package.WantsLatest || !package.HasPackage)
        {
            return;
        }

        // Deliberately not BeginResolving(): that clears the verified release, and this
        // method's whole contract is that a feed which cannot be reached right now is a
        // reason to install what was already checked rather than to abandon the run. Nobody
        // is looking at the package page during a run, so there is nothing here to say
        // "checking…" to — and the rail is better off naming the version being installed.
        try
        {
            var feed = CreateFeed();
            var newest = await feed.ResolveTagAsync(
                PackagePageViewModel.LatestTag,
                cancellationToken);
            if (string.Equals(newest, package.ResolvedTag, StringComparison.Ordinal))
            {
                return;
            }

            AppendLog(
                report,
                $"LocalAi package: {newest} was published after this release was checked; " +
                "resolving it instead.");
            // Resolved first, then recorded: assigning the tag before this await left the
            // wizard pointing at a release whose manifest had failed to verify, so the run
            // downloaded that asset and checked it against the previous manifest's hash.
            var release = await feed.ResolveAsync(newest, WorkingDirectory, cancellationToken);
            resolvedTag = newest;
            package.SelectResolvedRelease(release, newest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Whatever was already verified stays selected: a feed that cannot be reached right
            // now is a reason to install what was checked, not to abandon the run.
            AppendLog(
                report,
                "LocalAi package: the newest release could not be re-checked " +
                $"({exception.Message}); continuing with {package.ResolvedTag}.");
        }
    }

    /// <summary>
    /// Registers this installation in Apps &amp; features, and parks the copy of the installer
    /// the entry points at inside the runtime root.
    ///
    /// Right after the package, because both the version it reports and the size it estimates
    /// are read from what was just installed. An upgrade rewrites the same entry, so
    /// DisplayVersion follows the installation instead of naming whichever release first
    /// created it.
    ///
    /// A failure here does not fail the installation: LocalAi works perfectly well without a
    /// line in a list. It is said out loud, though, together with how to remove it by hand,
    /// because an uninstaller nobody can find is the problem this whole feature exists for.
    /// </summary>
    private void RegisterUninstallEntry(StringBuilder report, string version)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var step = JournalBegin(
            InstallerRunEffectKind.UninstallRegistration,
            "Apps & features entry for LocalAi " + version);
        try
        {
            var registration = new UninstallRegistration(InstallationLayout.CreateDefault());
            registration.Register(
                version,
                Environment.ProcessPath ??
                    throw new InvalidOperationException(
                        "The running installer has no path to copy."));
            // Read back, not recalled: the line used to be composed from the values just handed
            // to the registry, so it agreed with itself whatever the registry did.
            var written = registration.Read();
            registrationOutcome = written is null ? RegistrationOutcome.Missing : RegistrationOutcome.Registered;
            AppendLog(
                report,
                RunResults.AppsAndFeatures(written, registration.UninstallerPath));
            JournalComplete(
                step,
                written is null
                    ? "Written under HKCU and not found when read back."
                    : "Registered under HKCU; removed again by the uninstaller.",
                isReversible: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidOperationException or
                InstallationLayoutException)
        {
            registrationOutcome = RegistrationOutcome.Failed;
            JournalFail(step, exception.Message);
            AppendLog(
                report,
                "Apps & features: could not register this installation " +
                $"({exception.Message}). Package installation completed; model and client results are reported separately. To remove it later, " +
                "run this installer again and choose Remove.");
        }
    }

    /// <summary>
    /// What the review page says about the PATH, which is an effect on the account rather than
    /// inside the runtime root and therefore one a person agrees to before it happens.
    ///
    /// It says nothing when the entry is already there: a line about a change that is not going
    /// to be made is the same defect as a missing line about one that is.
    /// </summary>
    private string LauncherPathReviewLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        // The entry is added only after the package installs.
        if (!package.HasPackage)
        {
            return InstallerCulture.Pick(
                "PATH: unchanged, because LocalAi will not be installed.",
                "PATH: не меняется, потому что LocalAi не будет установлен.");
        }

        var directory = Path.Combine(InstallationLayout.CreateDefault().BinRoot, "launcher");
        return LauncherPathRegistration.Contains(LauncherPathRegistration.Current(), directory)
            ? string.Format(
                InstallerCulture.Pick(
                    "PATH: {0} is already there; nothing will be changed.",
                    "PATH: {0} уже добавлен; ничего не изменится."),
                directory)
            : string.Format(
                InstallerCulture.Pick(
                    "PATH: {0} will be added for this account, so the commands the instructions " +
                    "give can be typed. A console already open keeps the path it started with.",
                    "PATH: {0} будет добавлен для этой учётной записи, чтобы команды из " +
                    "инструкций можно было набирать. Уже открытая консоль сохраняет тот путь, " +
                    "с которым запускалась."),
                directory);
    }

    /// <summary>
    /// Puts the launcher where a command line can find it.
    ///
    /// Every command the installed instructions offer begins `localai-launcher.exe`, and none of
    /// them ran before this: the launcher lives under LOCALAPPDATA and was on nobody's PATH. The
    /// half of the toolkit those commands reach is the documented way through a dead MCP server,
    /// so it was the fallback that did not work.
    /// </summary>
    private void RegisterLauncherPath(StringBuilder report)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(
            InstallationLayout.CreateDefault().BinRoot,
            "launcher");
        var step = JournalBegin(InstallerRunEffectKind.LauncherPath, "PATH entry for " + directory);
        try
        {
            var added = LauncherPathRegistration.Register(directory);
            pathMark = RunMark.Done;
            pathOutcome = added ? PathOutcome.Added : PathOutcome.AlreadyThere;
            AppendLog(
                report,
                added
                    ? $"PATH: {directory} added for this account. A console already open keeps " +
                        "the path it started with; a new one finds localai-launcher.exe."
                    : $"PATH: {directory} was already there, nothing changed.");
            JournalComplete(
                step,
                added ? "Added to the user PATH." : "Already present; nothing written.",
                isReversible: true);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or InvalidOperationException or
                InstallationLayoutException)
        {
            JournalFail(step, exception.Message);
            pathMark = RunMark.Failed;
            pathOutcome = PathOutcome.Failed;
            AppendLog(
                report,
                $"PATH: could not be updated ({exception.Message}). The command line needs the launcher's full path until this is " +
                "fixed by hand.");
        }
    }

    private async Task InstallPackageAsync(
        StringBuilder report,
        CancellationToken cancellationToken)
    {
        await RefreshLatestBeforeInstallAsync(report, cancellationToken);
        // The version becomes known here on the install path, and the rail says what the run
        // is doing — so it has to reach the progress page rather than waiting for the finish.
        RefreshAll();
        if (package.Resolved is not { } resolved)
        {
            // Not a skip: installing the package is the entire point of this wizard. Returning
            // quietly here let the run finish as "Installation complete" while nothing had been
            // installed at all, which is the one outcome worse than a visible failure.
            packageOutcome = new(PackageOutcomeKind.NoRelease);
            AppendLog(
                report,
                "LocalAi package: no verified release was selected, so nothing was installed. " +
                "Return to the package step and check the release before continuing.");
            hasRunError = true;
            return;
        }

        AppendLog(
            report,
            $"LocalAi package {resolved.Manifest.ReleaseVersion}: downloading and verifying...");

        // Real byte progress: the expected size comes from the verified manifest, and the
        // downloaded bytes are observed as the file grows.
        var total = resolved.Manifest.PackageSize;
        // Progress callbacks are posted, not called, so the last download report can land after
        // the model phase has already begun. Without this the bar sat at "downloading 208 of
        // 208 MB" for the whole of a two-minute preflight, which reads as a hung installer with
        // nothing to press but Cancel.
        var downloadFinished = false;
        var downloadProgress = new Progress<long>(bytes =>
        {
            if (downloadFinished)
            {
                return;
            }

            SetProgress(
                40 + (int)(50 * Math.Clamp(bytes, 0, total) / Math.Max(total, 1)),
                string.Format(
                    InstallerCulture.Pick(
                        "Downloading the LocalAi package: {0:N0} of {1:N0} MB",
                        "Скачиваю пакет LocalAi: {0:N0} из {1:N0} МБ"),
                    bytes / (1024d * 1024),
                    total / (1024d * 1024)));
        });
        SetProgress(40, InstallerCulture.Pick("Downloading the LocalAi package...", "Скачиваю пакет LocalAi…"));

        var service = new ReleaseInstallService(
            CreateFeed(),
            processRunner,
            new SystemFileSystemProbe());
        var planned = new List<ModelProvisioningProgress>();
        using var modelProgress = new ModelRunProgress(step =>
        {
            downloadFinished = true;
            ApplyModelStep(step);
            if (step.Phase == ModelProvisioningPhase.Planned)
            {
                // Said once, when the last of them has arrived: the plan is one fact about the
                // run, not six, and it is the first thing worth reading in the log.
                planned.Add(step);
                if (planned.Count == step.Total)
                {
                    AppendLog(
                        report,
                        "Models: " +
                        string.Join(
                            ", ",
                            planned.Select(model =>
                                $"{model.Model} at {model.ContextTokens} tokens")) +
                        ". Anything already installed is left alone; the rest is downloaded now.");
                }

                return;
            }

            // 90 to 95: the package is in, the models are the tail of the run. The count is
            // what makes a long wait legible — "3 of 6" is a queue, a frozen bar is a fault.
            var share = step.Total <= 0 ? 0 : 5 * step.Completed / step.Total;
            var said = ModelStepWording.ForTheRail(step);
            SetProgress(
                90 + share,
                step.Total > 0
                    ? string.Format(
                        InstallerCulture.Pick(
                            "Local models: {0} of {1} — {2}",
                            "Локальные модели: {0} из {1} — {2}"),
                        step.Completed,
                        step.Total,
                        said)
                    : said);
            // The rail moves with every report; the log keeps the ones worth reading afterwards.
            if (ModelStepWording.LogLine(step) is { } line)
            {
                AppendLog(report, line);
            }
        });
        var selection = models.BuildProvisioningSelection();
        // The intent covers the download too, but the download only touches the temp
        // directory; the effect the journal exists for is the activation. The callback
        // below closes this step at the exact moment activation finishes, so a process
        // killed during the model pulls that follow leaves the activation recorded as done
        // rather than as "state unknown".
        var packageStep = JournalBegin(
            InstallerRunEffectKind.PackageActivation,
            $"LocalAi package {resolved.Manifest.ReleaseVersion}");
        string? modelsStep = null;
        ReleaseInstallResult result;
        try
        {
            result = await service.InstallAsync(
                resolved,
                WorkingDirectory,
                resolvedTag ?? resolved.Manifest.ReleaseVersion,
                downloadProgress,
                selection,
                environmentDiagnosis?.Gpu,
                modelProgress,
                activation =>
                {
                    ApplyPackageResult(activation);
                    JournalPackageActivation(packageStep, activation);
                    if (activation.Installed && selection.Mode != ModelProvisioningMode.None)
                    {
                        modelsStep = JournalBegin(
                            InstallerRunEffectKind.ModelInstall,
                            "Local models through the broker");
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            JournalFail(modelsStep ?? packageStep, "Cancelled before it could finish.");
            throw;
        }
        catch (LocalAiPackageInstallationException exception)
        {
            JournalFail(packageStep, exception.Message);
            // A refused layout is the one installation failure a user can actually fix, and
            // the refusal alone never says how. Letting it reach the outer handler produced
            // "Failed: The LocalAi installation layout is unsafe (check: ValidateAcl)" and
            // nothing else — accurate, unactionable, and indistinguishable from a product
            // defect. It is caught here so the advice can name the directory and say what is
            // in it, and so the run still finishes its own reporting instead of aborting.
            packageInstalled = false;
            packageOutcome = new(PackageOutcomeKind.Refused, Reason: exception.Message);
            AppendLog(report, $"LocalAi package: {exception.Message}");
            var layout = InstallationLayout.CreateDefault();
            var advice = InstallationFailureAdvice.ForLayoutFailure(
                exception.Message,
                layout.Root,
                HoldsInstalledVersions(layout));
            if (advice is not null)
            {
                AppendLog(report, advice);
            }

            hasRunError = true;
            return;
        }

        finally
        {
            // Flush reported model events before the batch verdict, and close both streams
            // before this run can finish or another attempt can start.
            downloadFinished = true;
            modelProgress.Dispose();
        }

        SetProgress(90, InstallerCulture.Pick("Installing the LocalAi package...", "Устанавливаю пакет LocalAi…"));

        ApplyPackageResult(result);
        // result.Status is an enum name. It used to be printed straight into the summary a
        // person reads — "ImmutableConflict" — which is the same defect the review page fixed
        // for the residency policy, one page further on.
        AppendLog(
            report,
            result.Installed
                ? $"LocalAi package: {result.Status}, version {result.Version} at {result.VersionPath}."
                : $"LocalAi package: {result.Status}. {result.Reason}".Trim());
        if (!result.Installed)
        {
            hasRunError = true;
        }

        if (result.Installed)
        {
            // The release version, not the version directory: Apps & features shows this
            // to a person, and "467ed5f0f9bf" is a commit id rather than a version (#255).
            RegisterUninstallEntry(report, resolved.Manifest.ReleaseVersion);
            RegisterLauncherPath(report);
        }

        ReportModelOutcome(report, result.Models, selection);
        if (modelsStep is not null)
        {
            var pulled = result.Models?.Batch?.Models
                .Count(model => model.PullCompleted) ?? 0;
            JournalComplete(
                modelsStep,
                $"Models newly downloaded: {pulled}. Pulled models live in Ollama's own " +
                "store, shared with everything else that uses it, and are not removed by " +
                "this installer.",
                isReversible: false);
        }
    }

    /// <summary>
    /// Retain activation before model provisioning can fail or be cancelled.
    /// </summary>
    public void ApplyPackageResult(ReleaseInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        packageInstalled = result.Installed;
        packageOutcome = new(PackageOutcomeKind.Result, result.Status, result.Version, result.Reason);
    }

    /// <summary>
    /// What activation left behind decides what rollback can promise. Only an upgrade is
    /// reversible: the launcher can reactivate the version that was current before. A first
    /// installation has nothing to return to, and its root starts holding runtime data —
    /// indexes included — the moment the broker runs, so "undo" would mean deleting things
    /// this run did not create.
    /// </summary>
    private void JournalPackageActivation(string? packageStep, ReleaseInstallResult activation)
    {
        if (!activation.Installed)
        {
            // The package installer recovers its own failures: by the time it reports
            // anything but success, the prior state is back (or the reason says why not).
            JournalFail(
                packageStep,
                $"{activation.Status}. {activation.Reason}".Trim());
            return;
        }

        var isUpgrade = activation.PriorVersion is { Length: > 0 } prior &&
            !string.Equals(prior, activation.Version, StringComparison.Ordinal);
        JournalComplete(
            packageStep,
            isUpgrade
                ? $"Activated {activation.Version}; {activation.PriorVersion} was active " +
                    "before and can be reactivated."
                : activation.PriorVersion is null
                    ? $"First installation of {activation.Version}: there is no previous " +
                        "version to return to, and the LocalAi directory starts holding " +
                        "runtime data as soon as it is used."
                    : $"Version {activation.Version} was already active.",
            isReversible: isUpgrade,
            isUpgrade
                ? new InstallerRunUndoData(activation.Version, activation.PriorVersion)
                : null);
    }

    /// <summary>
    /// Whether the LocalAi root holds an installed version. Only used to choose which advice
    /// to print after a refused layout, so an unreadable directory answers "assume it does":
    /// advising someone to delete a tree that might hold their indexes has to be the answer
    /// this is sure about, never the one it guesses.
    /// </summary>
    private static bool HoldsInstalledVersions(InstallationLayout layout)
    {
        try
        {
            return Directory.Exists(layout.VersionsRoot) &&
                Directory.EnumerateFileSystemEntries(layout.VersionsRoot).Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// One step of a model run, onto the row it names. <see cref="ModelProvisioningPhase.Planned"/>
    /// is what creates the rows: the plan comes from the signed manifest and arrives in full
    /// before the first download starts, so the queue's length is visible when it matters.
    /// </summary>
    public void ApplyModelStep(ModelProvisioningProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Phase == ModelProvisioningPhase.Planned)
        {
            if (Row(step.Model, step.ContextTokens) is null)
            {
                ModelRuns.Add(new ModelRunRow(step.Model, step.ContextTokens));
                OnPropertyChanged(nameof(HasModelRuns));
            }

            return;
        }

        Row(step.Model, step.ContextTokens)?.Apply(step);
    }

    /// <summary>
    /// Closes every row the run never answered for. A batch stops at the first failure, so the
    /// models after it are neither done nor running, and they would otherwise sit spinning on a
    /// page that has finished.
    /// </summary>
    public void SettleModelRuns(bool cancelled = false)
    {
        foreach (var row in ModelRuns)
        {
            row.FinishWithoutVerdict(cancelled);
        }
    }

    private ModelRunRow? Row(string model, int contextTokens) =>
        ModelRuns.FirstOrDefault(row =>
            string.Equals(row.Tag, model, StringComparison.Ordinal) &&
            row.ContextTokens == contextTokens);

    /// <summary>
    /// A model that was asked for and did not arrive has to be visible here. The broker
    /// answers per model, and a refusal carries the smaller context sizes that would have
    /// fitted, so both are printed rather than collapsed into "models: failed".
    /// </summary>
    private void ReportModelOutcome(
        StringBuilder report,
        ReleaseModelInstallReport? models,
        ModelProvisioningSelection selection)
    {
        if (models?.Batch is not { } batch)
        {
            // No batch means the planner produced no request at all. When models were asked
            // for, that is a failure of the run, not an absence of news — and it used to be
            // reported as neither. A release signed without a model list, or a machine no
            // catalogue model fits, ended as "Installation complete" with one explanatory
            // line buried in the log, and the first sign of trouble was CodeSearch having no
            // embedding model days later.
            if (selection.Mode == ModelProvisioningMode.None || models is null)
            {
                return;
            }

            var reasons = models.Excluded.Count == 0
                ? "The installer produced no model request."
                : string.Join(" ", models.Excluded);
            AppendLog(
                report,
                "Local models: none were installed. " + reasons +
                " Nothing else in this installation is affected, but local model work — " +
                "CodeSearch indexing included — cannot run until at least one model is " +
                "present. Ask the client to run the 'local_models_sync' tool, or install " +
                "one from a release whose manifest carries the model list.");
            hasRunError = true;
            return;
        }

        // Printed whether or not a batch ran. A model the planner left out used to reach the log
        // only when nothing at all was installed, so on a run where five of six models went in,
        // the sixth vanished without a line.
        foreach (var excluded in models.Excluded)
        {
            AppendLog(report, excluded);
        }

        foreach (var model in batch.Models)
        {
            Row(model.Model, model.ContextTokens)?.Settled(
                model.Outcome,
                model.PullCompleted,
                model.FallbackSuggestions,
                model.Code,
                model.ResidencyDegraded);
        }

        SettleModelRuns();

        foreach (var model in batch.Models)
        {
            var pulled = model.PullCompleted
                ? "downloaded"
                : model.PullAttempted
                    ? "download did not finish"
                    : "already present";
            if (model.Outcome == BrokerModelInstallOutcome.Accepted)
            {
                AppendLog(
                    report,
                    $"Model {model.Model} at {model.ContextTokens} tokens: {pulled}, " +
                    (model.ResidencyDegraded
                        ? "ready with limitations under the selected memory policy."
                        : "fully resident."));
                continue;
            }

            var suggestion = model.FallbackSuggestions.Count == 0
                ? string.Empty
                : " Smaller options that fit: " +
                    string.Join(
                        ", ",
                        model.FallbackSuggestions.Select(fallback =>
                            $"{fallback.Model} at {fallback.ContextTokens}")) + ".";
            AppendLog(
                report,
                $"Model {model.Model} at {model.ContextTokens} tokens: {model.Outcome} " +
                $"({model.Code}), {pulled}.{suggestion}");
            hasRunError = true;
        }

        if (batch.StopReason != BrokerModelBatchStopReason.None)
        {
            AppendLog(
                report,
                $"Model setup stopped early: {batch.StopReason} ({batch.Code}).");
            hasRunError = true;
        }
    }

    private async Task<bool> TryInstallDependencyWithWingetAsync(
        DependencyDefinition dependency,
        string displayName,
        bool reinstall,
        CancellationToken cancellationToken)
    {
        if (environmentDiagnosis?.WinGet is not
            { State: DependencyState.Detected, ExecutablePath: { } wingetPath })
        {
            SetRunLog(
                $"WinGet is unavailable; install {displayName} manually from " +
                $"{dependency.OfficialInstallerUri}.");
            return false;
        }

        // Checked here rather than at detection, and before every install rather than once: the
        // detector reports the first file named winget.exe on a search path the user can write
        // to, and it reported it several pages ago. What runs is the path this check resolves.
        var winget = wingetSource.Authorize(wingetPath);
        if (!winget.Allowed)
        {
            SetRunLog(
                $"{displayName} was not installed. {winget.Message} You can install it " +
                $"yourself from {dependency.OfficialInstallerUri}.");
            return false;
        }

        var arguments = new List<string>
        {
            "install",
            "--id",
            dependency.PackageId,
            "--exact",
            "--source",
            "winget",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
        };

        // --force only when the user explicitly asked to reinstall something already
        // present. Passing it unconditionally reinstalled working software on every run.
        if (reinstall)
        {
            arguments.Add("--force");
        }

        var result = await processRunner.RunAsync(
            winget.ExecutablePath,
            [.. arguments],
            DependencyInstallTimeout,
            cancellationToken);
        if (result.ExitCode is not 0)
        {
            SetRunLog(
                $"{displayName} installation failed with exit code {result.ExitCode}.");
            return false;
        }

        return true;
    }

    private async Task<bool> TryInstallDependencyWithNpmAsync(
        DependencyDefinition dependency,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (dependency.PackageVersion is not { Length: > 0 } packageVersion)
        {
            SetRunLog($"{displayName} has no pinned package version.");
            return false;
        }

        if (environmentDiagnosis?.Npm is not
            { State: DependencyState.Detected, ExecutablePath: { } npmPath })
        {
            SetRunLog(
                $"npm is unavailable; install Node.js 20 before {displayName}.");
            return false;
        }

        var result = await processRunner.RunAsync(
            npmPath,
            ["install", "--global", $"{dependency.PackageId}@{packageVersion}"],
            DependencyInstallTimeout,
            cancellationToken);
        if (result.ExitCode is not 0)
        {
            SetRunLog(
                $"{displayName} installation failed with exit code {result.ExitCode}.");
            return false;
        }

        if (string.Equals(
                dependency.PackageId,
                DependencyCatalog.ScipPython.PackageId,
                StringComparison.Ordinal))
        {
            var npmRootResult = await processRunner.RunAsync(
                npmPath,
                ["root", "--global"],
                DependencyInstallTimeout,
                cancellationToken);
            var npmGlobalRoot = npmRootResult.StandardOutput.Trim();
            if (npmRootResult.ExitCode is not 0 ||
                string.IsNullOrWhiteSpace(npmGlobalRoot) ||
                npmGlobalRoot.Contains('\n', StringComparison.Ordinal) ||
                npmGlobalRoot.Contains('\r', StringComparison.Ordinal))
            {
                SetRunLog(
                    "SCIP Python was installed, but npm did not return a valid global " +
                    "package directory.");
                return false;
            }

            try
            {
                _ = new ScipPythonWindowsCompatibilityPatch().Apply(
                    npmGlobalRoot,
                    packageVersion);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or JsonException)
            {
                SetRunLog(
                    $"SCIP Python Windows compatibility patch failed: {exception.Message}");
                return false;
            }

            var globalPrefix = Directory.GetParent(npmGlobalRoot)?.FullName;
            var scipPythonPath = globalPrefix is null
                ? null
                : Path.Combine(globalPrefix, "scip-python.cmd");
            if (scipPythonPath is null || !File.Exists(scipPythonPath))
            {
                SetRunLog(
                    "SCIP Python was patched, but its npm command shim was not found.");
                return false;
            }

            var verification = await processRunner.RunAsync(
                scipPythonPath,
                ["--version"],
                DependencyInstallTimeout,
                cancellationToken);
            var versionOutput = verification.StandardOutput + verification.StandardError;
            if (verification.ExitCode is not 0 ||
                !versionOutput.Contains(packageVersion, StringComparison.Ordinal))
            {
                SetRunLog(
                    "SCIP Python was patched, but its executable verification failed.");
                return false;
            }
        }

        return true;
    }
}
