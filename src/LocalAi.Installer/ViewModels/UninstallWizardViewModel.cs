using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The wizard in uninstall mode.
///
/// It is the installer's own executable rather than a CLI verb because everything removal
/// needs is already here — the client adapters, the managed-block machinery, the journal, the
/// review-page pattern — and because it runs from outside the tree it is deleting, which
/// spares it the self-deletion dance a `localai uninstall` would need on Windows.
///
/// The shape follows the installation exactly: choose, review everything on one page, confirm,
/// and only then act. Nothing is removed before the confirmation, the plan shown is the plan
/// performed, and every effect lands in the journal that lives outside the tree being removed.
/// </summary>
public sealed class UninstallWizardViewModel : ObservableObject
{
    /// <summary>
    /// A method rather than a static field: a field reads its strings once per process, so a
    /// language chosen after the first read would leave the rail in the other one.
    /// </summary>
    private static IReadOnlyList<(UninstallPage Page, string Title)> Steps =>
    [
        (UninstallPage.Choose, InstallerCulture.Pick("What to remove", "Что удалить")),
        (UninstallPage.Confirm, InstallerCulture.Pick("Confirm", "Подтверждение")),
        (UninstallPage.Progress, InstallerCulture.Pick("Remove", "Удаление")),
        (UninstallPage.Finish, InstallerCulture.Pick("Finished", "Готово")),
    ];

    private readonly InstallationLayout layout;
    private readonly string homeDirectory;
    private readonly IProcessRunner processRunner;
    private readonly UninstallPlanner planner;

    private UninstallPage currentPage = UninstallPage.Choose;
    private bool isConfirmed;
    private bool isRunning;
    private bool isComplete;
    private bool hasRunError;
    private bool hasLoaded;

    private enum Blocked
    {
        AnotherRun,
        NothingInstalled,
    }

    private Blocked? blocked;
    private Exception? unexpected;
    private int progress;

    /// <summary>A resting state, worded when read; null while a step's own text is showing.</summary>
    private ProgressWord? progressWord = ProgressWord.Ready;
    private string progressText = string.Empty;
    private UninstallPlan? confirmedPlan;
    private CancellationTokenSource? runCancellation;

    private enum RunEnd
    {
        NotRun,
        Finished,
        Cancelled,
        Refused,
        Failed,
    }

    /// <summary>How the run ended and what it left, kept as facts so the finish page can be reworded.</summary>
    private RunEnd runEnd;
    private UninstallOutcome? outcome;
    private string? refusal;
    private string? removalJournalPath;
    private Exception? failure;
    private IReadOnlyList<RemovalReportEntry> reportEntries = [];
    private string? reportPath;
    private string? reportSaveError;
    private string? journalError;

    /// <summary>
    /// <paramml name="preset"/> is where the page opens: a clean reinstall arrives here on the
    /// reinstall-friendly row, a removal on the full one. The rest exist so tests can point
    /// the wizard at a machine of their own making; left alone it works on this computer.
    /// <paramref name="readHooksPath"/> is how a repository's <c>core.hooksPath</c> is
    /// discovered — Git, unless a test says otherwise.
    /// </summary>
    public UninstallWizardViewModel(
        RemovalPreset preset = RemovalPreset.FullUninstall,
        bool offersInstallAfterwards = false,
        InstallationLayout? layout = null,
        string? homeDirectory = null,
        IProcessRunner? processRunner = null,
        Func<string, CancellationToken, Task<string?>>? readHooksPath = null,
        bool canReturnToStart = false,
        InstallerPresentation? presentation = null)
    {
        Presentation = presentation ?? new InstallerPresentation();
        Presentation.LanguageChanged += OnLanguageChanged;
        OffersInstallAfterwards = offersInstallAfterwards;
        this.canReturnToStart = canReturnToStart;
        this.layout = layout ?? InstallationLayout.CreateDefault();
        this.homeDirectory = homeDirectory ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this.processRunner = processRunner ?? new SystemProcessRunner();
        planner = new UninstallPlanner(this.layout, this.homeDirectory, readHooksPath);

        Choices = new RemovalChoicesPageViewModel(preset)
        {
            SigningKeysDirectory = Path.Combine(
                this.layout.Root,
                RemovalMatrix.SigningKeyDirectoryName),
        };
        // A change on the page invalidates what the wizard says about it: the buttons, the
        // step status, and any plan already built from the old answers.
        Choices.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedPreset));
            OnPropertyChanged(nameof(RemoveSigningKeys));
            RefreshAll();
        };
        ContinueToInstallCommand = new RelayCommand(
            () => InstallRequested?.Invoke(this, EventArgs.Empty),
            () => CanContinueToInstall);
        BackCommand = new RelayCommand(() => MovePrevious(), () => CanMovePrevious);
        NextCommand = new AsyncRelayCommand(
            () => MoveNextAsync(),
            () => CanMoveNext,
            ReportUnexpectedError);
        UninstallCommand = new AsyncRelayCommand(
            () => RunAsync(),
            () => CanRun,
            ReportUnexpectedError);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        visited.Add(currentPage);
    }

    public event EventHandler? CloseRequested;

    public InstallerPresentation Presentation { get; }

    /// <summary>For the window to call once closed; the shared settings outlive it.</summary>
    public void DetachPresentation() => Presentation.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Words only: the confirmed plan and the consent stay as they are, and nothing is planned
    /// again. What the planner and the runner worded themselves keeps the language it came in.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildSteps();
        AnnounceEverything();
        Choices.AnnounceLanguage();
    }

    /// <summary>
    /// Raised when the person asks to carry on into an installation after a clean reinstall's
    /// removal half. Two deliberate wizards rather than one chained run: the install has its
    /// own prerequisites, its own release choice and its own review page, and starting it
    /// automatically would apply all of that without anybody confirming it.
    /// </summary>
    public event EventHandler? InstallRequested;

    /// <summary>
    /// Where the journal is written: outside the runtime root, so the record of what this run
    /// removed survives the removal. Settable so tests point it at their own directory.
    /// </summary>
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        RemovalMatrix.JournalDirectoryName);

    /// <summary>
    /// The matrix page. Shared with the installer wizard, which needs the same page for the
    /// removal half of a clean reinstall — the rows and the presets are the contract with the
    /// planner, and two copies of it are two that drift.
    /// </summary>
    public RemovalChoicesPageViewModel Choices { get; }

    public ObservableCollection<RemovalRow> Rows => Choices.Rows;

    public ObservableCollection<RepositoryRow> Repositories => Choices.Repositories;

    public ObservableCollection<WizardStep> StepList { get; } = [];

    public IReadOnlyList<UninstallPresetOption> Presets => Choices.Presets;

    public RelayCommand BackCommand { get; }

    public AsyncRelayCommand NextCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ContinueToInstallCommand { get; }

    /// <summary>Whether this run is the removal half of a clean reinstall.</summary>
    public bool OffersInstallAfterwards { get; }

    /// <summary>
    /// The install half is offered once the removal half has actually finished, and only when
    /// it succeeded: inviting somebody to install over a removal that failed halfway is how a
    /// machine ends up in a state neither wizard describes.
    /// </summary>
    public bool CanContinueToInstall =>
        OffersInstallAfterwards && IsFinishPage && !isRunning && !hasRunError;

    public UninstallPage CurrentPage
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
    private readonly HashSet<UninstallPage> visited = [];

    public RemovalPreset SelectedPreset
    {
        get => Choices.SelectedPreset;
        set => Choices.SelectedPreset = value;
    }

    /// <summary>
    /// The separate confirmation the key directory needs. Kept off the matrix rows on purpose:
    /// with it ticked the offline backup becomes the only copy that exists anywhere.
    /// </summary>
    public bool RemoveSigningKeys
    {
        get => Choices.RemoveSigningKeys;
        set => Choices.RemoveSigningKeys = value;
    }

    public string SigningKeysTitle => Choices.SigningKeysTitle;

    public string SigningKeysWarning => Choices.SigningKeysWarning;

    public bool IsConfirmed
    {
        get => isConfirmed;
        set
        {
            SetProperty(ref isConfirmed, value);
            RefreshAll();
        }
    }

    /// <summary>
    /// Why this wizard will not do anything: another run holds the live lock, or there is no
    /// installation here to remove. Both are answers, not errors.
    /// </summary>
    public string? BlockingNotice => blocked switch
    {
        Blocked.AnotherRun => InstallerCulture.Pick(
            "Another LocalAi installer or uninstaller is running on this computer. " +
            "Finish it first, then start this one again.",
            "На этом компьютере уже работает установщик или деинсталлятор " +
            "LocalAi. Сначала завершите его, потом запустите этот заново."),
        Blocked.NothingInstalled => string.Format(
            InstallerCulture.Pick(
                "There is no LocalAi installation at {0} to remove. Client registrations " +
                "and Git hooks, if any are left, are listed by the documentation.",
                "В {0} нет установки LocalAi, которую можно удалить. Регистрации " +
                "клиентов и хуки Git, если что-то осталось, перечислены в документации."),
            layout.Root),
        _ => null,
    };

    public bool IsBlocked => blocked is not null;

    public string? UnexpectedError => unexpected is null
        ? null
        : string.Format(
            InstallerCulture.Pick(
                "Unexpected error: {0}: {1}",
                "Непредвиденная ошибка: {0}: {1}"),
            unexpected.GetType().Name,
            unexpected.Message);

    public bool HasUnexpectedError => unexpected is not null;

    public string PreviewText => confirmedPlan?.PreviewText ?? string.Empty;

    /// <summary>
    /// What this run deliberately leaves alone, said before the list rather than inside it.
    ///
    /// The reinstall-friendly preset pins the client registrations and the hook dispatchers to
    /// kept, because the installation that follows rewrites all three and asking twice invites
    /// two different answers. Read from the confirmed plan rather than from the preset: the
    /// rows stay tickable after a preset fills them in, so somebody who arrived here by
    /// choosing a clean reinstall can still tick one, and this sentence must not then be a
    /// lie. Empty when it does not apply.
    /// </summary>
    public string KeepNotice => confirmedPlan is { } plan
        ? BuildKeepNotice(plan.Selection)
        : string.Empty;

    public bool HasKeepNotice => KeepNotice.Length > 0;

    public string Summary => runEnd switch
    {
        RunEnd.Finished => BuildSummary(outcome!),
        RunEnd.Cancelled => InstallerCulture.Pick(
            "Removal stopped. Data already removed has not been restored. Check the journal before closing this window and running the installer again. Journal: ",
            "Удаление остановлено. Уже удалённые данные не восстановлены. Проверьте журнал перед закрытием окна и повторным запуском установщика. Журнал: ") + removalJournalPath,
        RunEnd.Refused => InstallerCulture.Pick(
            "Nothing was removed. Removal was refused before any changes. Address the reported cause, close this window and run the installer again. Technical details: ",
            "Ничего не удалено. Удаление отклонено до внесения изменений. Устраните указанную причину, закройте это окно и снова запустите установщик. Технические сведения: ") + refusal,
        RunEnd.Failed => string.Format(
            InstallerCulture.Pick(
                "Removal failed; some changes may already have been made. Deleted data has not been restored. Keep the technical details and check the journal before retrying. Technical details: {0}. Journal: {1}",
                "Удаление завершилось ошибкой; часть изменений могла быть выполнена. Удалённые данные не восстановлены. Сохраните технические сведения и проверьте журнал перед повторной попыткой. Технические сведения: {0}. Журнал: {1}"),
            failure?.Message, removalJournalPath ?? InstallerCulture.Pick("unavailable", "недоступен")),
        _ => string.Empty,
    };

    public IReadOnlyList<RunResultRow> Results => reportEntries.Select(RemovalReportPresentation.Row).ToArray();

    public string Report =>
        (runEnd == RunEnd.Refused ? InstallerCulture.Pick("Nothing was removed.\n", "Ничего не удалено.\n") : string.Empty) +
        string.Join(Environment.NewLine, reportEntries.Select(RemovalReportPresentation.Line));

    public string? ReportPath => reportPath;
    public bool HasReportPath => reportPath is not null && File.Exists(reportPath);
    public bool HasReport => runEnd != RunEnd.NotRun;
    public bool HasReportSaveError => reportSaveError is not null;
    public string ReportSaveError => reportSaveError is null ? string.Empty : string.Format(
        InstallerCulture.Pick("Could not save the report: {0}. Copy the details below.",
            "Не удалось сохранить отчёт: {0}. Скопируйте сведения ниже."), reportSaveError);
    public string ReportText => Summary + Environment.NewLine + Environment.NewLine + Report +
        (removalJournalPath is null ? string.Empty : Environment.NewLine + "Journal: " + removalJournalPath) +
        (journalError is null ? string.Empty : Environment.NewLine + "Journal error: " + journalError);

    public int Progress => progress;

    public string ProgressText => progressWord is { } word ? ProgressWords.Text(word) : progressText;

    public bool IsRunning => isRunning;

    public bool IsComplete => isComplete;

    public bool HasRunError => hasRunError;

    public bool IsProgressPage => CurrentPage == UninstallPage.Progress;

    public bool IsFinishPage => CurrentPage == UninstallPage.Finish;

    public string StepStatus => string.Format(
        InstallerCulture.Pick("Step {0} of {1}", "Шаг {0} из {1}"),
        StepIndex(CurrentPage) + 1,
        Steps.Count);

    public string StepTitle => CurrentPage switch
    {
        UninstallPage.Choose => InstallerCulture.Pick("What to remove", "Что удалить"),
        UninstallPage.Confirm => InstallerCulture.Pick("Ready to remove", "Готово к удалению"),
        UninstallPage.Progress => InstallerCulture.Pick("Removing", "Удаление"),
        _ => hasRunError
            ? InstallerCulture.Pick("Removal not completed", "Удаление не завершено")
            : InstallerCulture.Pick("Removal complete", "Удаление завершено"),
    };

    public string StepDescription => CurrentPage switch
    {
        UninstallPage.Choose => InstallerCulture.Pick(
            "Start from one of the three presets and change any row you like. Nothing is " +
            "removed until you confirm it on the next page.",
            "Начните с одного из трёх наборов и меняйте любые строки. " +
            "Ничего не будет удалено, пока вы не подтвердите это на следующей " +
            "странице."),
        UninstallPage.Confirm => InstallerCulture.Pick(
            "Everything this will remove, and everything it will leave alone. To change " +
            "anything click Back; to apply it click Uninstall.",
            "Всё, что будет удалено, и всё, что останется. Чтобы что-то " +
            "изменить, нажмите «Назад»; чтобы применить — «Удалить»."),
        UninstallPage.Progress => InstallerCulture.Pick(
            "Applying the selected removals.",
            "Применяю выбранные удаления."),
        _ => hasRunError
            ? InstallerCulture.Pick(
                "Check the outcome below before retrying. Removal does not restore deleted data.",
                "Перед повторной попыткой проверьте результат ниже. Удаление не восстанавливает удалённые данные.")
            : OffersInstallAfterwards
                // Not "or close: either way". With the hook dispatchers now kept, stopping
                // here leaves every connected repository failing its hooks until a launcher
                // is back. Close is still on screen; this line stops calling it equivalent.
                ? InstallerCulture.Pick(
                    "Everything selected was removed. Continue to install to put LocalAi " +
                    "back: the indexes and settings you kept are waiting, and the Git hooks " +
                    "in your repositories will not work again until the launcher is back.",
                    "Всё выбранное удалено. Нажмите «Перейти к установке», чтобы " +
                    "вернуть LocalAi: сохранённые индексы и настройки на месте, а хуки " +
                    "Git в ваших репозиториях не заработают, пока launcher не вернётся.")
                : InstallerCulture.Pick(
                    "Everything selected was removed.",
                    "Всё выбранное удалено."),
    };

    private readonly bool canReturnToStart;

    /// <summary>
    /// Raised when Back is pressed on the first page, which leaves the wizard rather than
    /// moving inside it.
    /// </summary>
    public event EventHandler? ReturnToStartRequested;

    public bool CanMovePrevious =>
        !isRunning &&
        (CurrentPage == UninstallPage.Confirm ||
            (CurrentPage == UninstallPage.Choose && canReturnToStart));

    public bool CanMoveNext =>
        !isRunning && !IsBlocked && CurrentPage == UninstallPage.Choose;

    public bool CanRun =>
        CurrentPage == UninstallPage.Confirm &&
        !isRunning &&
        isConfirmed &&
        confirmedPlan is { HasWork: true };

    public bool CanCancel => !isRunning || !IsFinishPage;

    public bool IsNextVisible => CurrentPage == UninstallPage.Choose;

    public bool IsUninstallVisible => CurrentPage == UninstallPage.Confirm;

    public string CancelButtonText => IsFinishPage
        ? InstallerCulture.Pick("Close", "Закрыть")
        : InstallerCulture.Pick("Cancel", "Отмена");

    public int CurrentPageIndex => StepIndex(CurrentPage);

    /// <summary>
    /// Reads the machine once: whether another run is happening, whether there is anything
    /// installed at all, and which repositories carry the dispatchers.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        if (InstallerRunJournal.IsRunActive(LogDirectory))
        {
            // The two must not meet in the middle: one wizard writing the runtime while
            // another removes it leaves a tree neither of them describes.
            blocked = Blocked.AnotherRun;
            RefreshAll();
            return;
        }

        if (!Directory.Exists(layout.Root))
        {
            blocked = Blocked.NothingInstalled;
            RefreshAll();
            return;
        }

        // Planned as a full uninstall purely to inventory the connected repositories: the
        // page has to list them before anybody has chosen anything.
        var inventory = await planner.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            cancellationToken);
        Choices.ListRepositories(inventory.Hooks.Select(hook => new RepositoryRow(
            hook.RepositoryId,
            hook.CommonDirectory,
            hook.Dispatchers.Count,
            hook.SkipReason)));

        RefreshAll();
    }

    public async Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMoveNext)
        {
            return false;
        }

        // The preview is built from the current choices at the moment the person asks to see
        // it, and the plan they then confirm is the object apply is handed — so what the page
        // listed is what runs, not a second planning pass that could differ.
        confirmedPlan = await planner.PlanAsync(
            Selection(),
            cancellationToken,
            installationFollows: OffersInstallAfterwards);
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(KeepNotice));
        OnPropertyChanged(nameof(HasKeepNotice));
        CurrentPage = UninstallPage.Confirm;
        RefreshAll();
        return true;
    }

    public bool MovePrevious()
    {
        if (!CanMovePrevious)
        {
            return false;
        }

        // The first page has no page before it, so Back leaves the wizard. Only the window
        // knows what is behind it, which is why this is asked rather than done.
        if (CurrentPage == UninstallPage.Choose)
        {
            ReturnToStartRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        // Going back invalidates the plan: the next Next builds a fresh one from whatever the
        // page says then.
        confirmedPlan = null;
        isConfirmed = false;
        OnPropertyChanged(nameof(IsConfirmed));
        CurrentPage = UninstallPage.Choose;
        return true;
    }

    public void Cancel()
    {
        if (isRunning)
        {
            runCancellation?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RunAsync(
        CancellationToken cancellationToken = default,
        IProgress<UninstallProgress>? progressObserver = null)
    {
        if (!CanRun || confirmedPlan is not { } plan)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation = linked;
        isRunning = true;
        isComplete = false;
        hasRunError = false;
        reportEntries = [];
        reportPath = null;
        reportSaveError = null;
        journalError = null;
        removalJournalPath = null;
        failure = null;
        refusal = null;
        outcome = null;
        var acceptingProgress = true;
        CurrentPage = UninstallPage.Progress;
        SetProgress(
            0,
            InstallerCulture.Pick("Asking LocalAi to stop…", "Прошу LocalAi остановиться…"));

        InstallerRunJournal? journal = null;
        try
        {
            ValidateReportDirectory(LogDirectory);
            journal = InstallerRunJournal.Start(LogDirectory);
            removalJournalPath = journal.JournalPath;
            var result = await new UninstallRunner(layout, processRunner)
                .ApplyAsync(
                    plan,
                    journal,
                    linked.Token,
                    new ObservedProgress(
                        new Progress<UninstallProgress>(step =>
                        {
                            if (acceptingProgress) SetProgress(step.Percent, step.Text);
                        }),
                        progressObserver));
            hasRunError = !result.Succeeded;
            isComplete = result.Succeeded;
            FinishJournal(journal, result.Succeeded
                ? InstallerRunOutcome.Completed
                : InstallerRunOutcome.Failed);
            outcome = result;
            runEnd = RunEnd.Finished;
            SetProgress(100, result.Succeeded ? ProgressWord.Completed : ProgressWord.FinishedWithProblems);
            CurrentPage = UninstallPage.Finish;
            return result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            FinishJournal(journal, InstallerRunOutcome.Cancelled);
            hasRunError = true;
            runEnd = RunEnd.Cancelled;
            SetProgress(progress, ProgressWord.Cancelled);
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        catch (UninstallRefusedException refused)
        {
            // The run stopped before touching anything, which is the one failure worth
            // stating as its own outcome: the machine is exactly as it was.
            FinishJournal(journal, InstallerRunOutcome.Failed);
            hasRunError = true;
            runEnd = RunEnd.Refused;
            refusal = refused.Message;
            SetProgress(100, ProgressWord.Refused);
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        catch (Exception exception)
        {
            FinishJournal(journal, InstallerRunOutcome.Failed);
            hasRunError = true;
            runEnd = RunEnd.Failed;
            failure = exception;
            SetProgress(100, ProgressWord.Failed);
            CurrentPage = UninstallPage.Finish;
            return false;
        }
        finally
        {
            acceptingProgress = false;
            reportEntries = UninstallReport.Capture(plan, journal?.Snapshot, layout);
            if (outcome is { } completed)
            {
                var additional = completed.Failures.Where(item => !reportEntries.Any(entry =>
                    entry.Target == item.Path && entry.State == RemovalReportState.Failed))
                    .Select(item => new RemovalReportEntry(item.Path, RemovalReportKind.Other,
                        RemovalReportState.Failed, item.Reason));
                reportEntries = [.. reportEntries, .. additional];
            }

            SaveReport(Path.Combine(LogDirectory, $"uninstall-{journal?.Snapshot.RunId ?? Guid.NewGuid():N}.txt"));
            journal?.Dispose();
            isRunning = false;
            runCancellation = null;
            RefreshAll();
        }
    }

    public void ReportUnexpectedError(Exception exception)
    {
        unexpected = exception;
        OnPropertyChanged(nameof(UnexpectedError));
        OnPropertyChanged(nameof(HasUnexpectedError));
    }

    private void FinishJournal(InstallerRunJournal? journal, InstallerRunOutcome result)
    {
        try
        {
            journal?.Finish(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            journalError = exception.Message;
        }
    }

    public bool SaveReport(string destination)
    {
        try
        {
            var fullPath = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(fullPath)!;
            ValidateReportDirectory(directory);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".uninstall-report-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, ReportText, new UTF8Encoding(false));
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            reportPath = fullPath;
            reportSaveError = null;
            RefreshAll();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            reportSaveError = exception.Message;
            RefreshAll();
            return false;
        }
    }

    private void ValidateReportDirectory(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.Root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(InstallerCulture.Pick(
                "Choose a report folder outside the LocalAi runtime being removed.",
                "Выберите папку отчётов вне удаляемого runtime LocalAi."));
        }

        for (var ancestor = new DirectoryInfo(full); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(InstallerCulture.Pick(
                    "Choose a report folder without directory links.", "Выберите папку отчётов без ссылок на каталоги."));
            }
        }
    }

    private sealed class ObservedProgress(
        IProgress<UninstallProgress> display,
        IProgress<UninstallProgress>? observer) : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value)
        {
            display.Report(value);
            observer?.Report(value);
        }
    }

    /// <summary>The choices as the core understands them.</summary>
    /// <summary>
    /// Two sentences for two arrivals. On a reinstall the point is that the gap closes by
    /// itself. On a hand-picked reinstall-friendly uninstall nothing follows, and pinning the
    /// rows to kept took away the "your choice" hint that used to say the decision was
    /// theirs — so that path is told where the decision went instead.
    /// </summary>
    private string BuildKeepNotice(RemovalSelection selection)
    {
        var keepsClaude = !selection.Includes(RemovalItem.ClaudeIntegration);
        var keepsCodex = !selection.Includes(RemovalItem.CodexIntegration);
        var keepsHooks = !selection.Includes(RemovalItem.GitHooks);

        // The one thing on this page that is kept by the Claude row and taken by another. The
        // gate is a command line pointing at the launcher, so the row that deletes the binaries
        // takes it whatever the Claude row says — and without this clause the notice tells the
        // reader their Claude setup is untouched while the run edits a file in their home
        // directory. The preview box names the file; this names the reason.
        var gateGoesAnyway = keepsClaude && selection.Includes(RemovalItem.Binaries);

        if (OffersInstallAfterwards)
        {
            // Silent the moment one of them is ticked: the override was deliberate, and the
            // preview box below lists what it costs.
            return keepsClaude && keepsCodex && keepsHooks
                ? InstallerCulture.Pick(
                    "The Claude and Codex registrations and the Git hook dispatchers are " +
                    "left exactly as they are — the installation that follows this removal " +
                    "rewrites all three.",
                    "Регистрации Claude и Codex и диспетчеры хуков Git остаются ровно " +
                    "как есть — установка, которая пойдёт следом, перепишет все три.") +
                  (gateGoesAnyway
                      // "asks about it again" rather than "puts it back": the installation
                      // seeds that checkbox by reading the machine, and by then the entry is
                      // gone — so a promise to restore it would be one this run cannot keep.
                      ? InstallerCulture.Pick(
                          " The hook that refuses text searches is not one of them: it points " +
                          "at the launcher, and the installation that follows asks about it " +
                          "again.",
                          " Хук, который отклоняет текстовый поиск, — не из их числа: он " +
                          "указывает на launcher, и установка, которая пойдёт следом, спросит " +
                          "о нём заново.")
                      : string.Empty)
                : string.Empty;
        }

        return keepsClaude && keepsCodex
            ? InstallerCulture.Pick(
                "The Claude and Codex registrations are left exactly as they are: this " +
                "preset disconnects no clients. To remove them as well, go back and tick " +
                "their rows.",
                "Регистрации Claude и Codex остаются ровно как есть: этот набор не " +
                "отключает ни одного клиента. Чтобы удалить и их, вернитесь назад " +
                "и отметьте их строки.") +
              (gateGoesAnyway
                  // Nothing follows this run, so the sentence states the fact and promises
                  // nothing about what comes after it.
                  ? InstallerCulture.Pick(
                      " The hook that refuses text searches still goes: it points at the " +
                      "launcher, which this run removes.",
                      " Хук, который отклоняет текстовый поиск, всё равно уходит: он " +
                      "указывает на launcher, который этот запуск удаляет.")
                  : string.Empty)
            : string.Empty;
    }

    public RemovalSelection Selection() => Choices.Selection();

    private string BuildSummary(UninstallOutcome outcome)
    {
        if (!outcome.Succeeded)
        {
            // "1 thing(s)" was never good English either; counting after the label works
            // in both languages and drops the plural hack.
            return string.Format(
                InstallerCulture.Pick(
                    "Could not be removed: {0}; removed: {1}. Nothing else was touched.",
                    "Не удалось удалить: {0}; удалено: {1}. Больше ничего не тронуто."),
                outcome.Failures.Count,
                outcome.RemovedPaths.Count);
        }

        var summary = new StringBuilder();
        summary.Append(outcome.RuntimeRootRemoved
            ? InstallerCulture.Pick(
                "LocalAi was removed from this computer.",
                "LocalAi удалён с этого компьютера.")
            : string.Format(
                InstallerCulture.Pick(
                    "Paths removed from {1}: {0}.",
                    "Удалено путей из {1}: {0}."),
                outcome.RemovedPaths.Count,
                layout.Root));
        if (outcome.RewrittenConfigurations.Count > 0)
        {
            summary.Append(' ').Append(string.Format(
                InstallerCulture.Pick(
                    "Client configurations updated: {0}.",
                    "Обновлено конфигураций клиентов: {0}."),
                outcome.RewrittenConfigurations.Count));
        }

        if (outcome.RemovedHooks.Count > 0)
        {
            summary.Append(' ').Append(string.Format(
                InstallerCulture.Pick(
                    "Git hook dispatchers removed: {0}.",
                    "Удалено диспетчеров хуков Git: {0}."),
                outcome.RemovedHooks.Count));
        }

        return summary.ToString();
    }


    private void SetProgress(int value, string text)
    {
        progress = value;
        progressWord = null;
        progressText = text;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    private void SetProgress(int value, ProgressWord word)
    {
        progress = value;
        progressWord = word;
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    private static int StepIndex(UninstallPage page)
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
        foreach (var name in new[]
                 {
                     nameof(CurrentPage), nameof(CurrentPageIndex), nameof(StepTitle),
                     nameof(StepDescription), nameof(StepStatus), nameof(IsProgressPage),
                     nameof(IsFinishPage), nameof(IsRunning), nameof(IsComplete),
                     nameof(HasRunError), nameof(CanMoveNext), nameof(CanMovePrevious),
                     nameof(CanRun), nameof(CanCancel), nameof(IsNextVisible),
                     nameof(IsUninstallVisible), nameof(CancelButtonText), nameof(Summary),
                     nameof(Report), nameof(BlockingNotice), nameof(IsBlocked),
                     nameof(CanContinueToInstall), nameof(KeepNotice),
                     nameof(HasKeepNotice), nameof(PreviewText),
                     nameof(Results), nameof(ReportPath), nameof(HasReportPath), nameof(HasReport),
                     nameof(ReportSaveError), nameof(HasReportSaveError), nameof(ReportText),
                 })
        {
            OnPropertyChanged(name);
        }

        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ContinueToInstallCommand.RaiseCanExecuteChanged();
    }
}
