using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The labels written directly into the markup — group headers, radio buttons, the sentence
/// under each option, the navigation buttons. The markup reads them with <c>{vm:Label}</c>, which
/// follows a language changed on any screen; <c>{x:Static}</c> would keep the one the window
/// opened with.
///
/// Everything that varies with what the run is doing lives in <see cref="WizardText"/> or in the
/// page view model that owns it. This class only holds text that is the same on every run.
/// </summary>
public static class PageLabels
{
    public static string ReviewRelease => InstallerCulture.Pick("Release and source", "Релиз и источник");
    public static string ReviewComponents => InstallerCulture.Pick("Components", "Компоненты");
    public static string ReviewModels => InstallerCulture.Pick("Models and memory", "Модели и память");
    public static string ReviewClients => InstallerCulture.Pick("Clients and PATH", "Клиенты и PATH");
    public static string ReviewAdditional => InstallerCulture.Pick("Additional actions", "Дополнительные действия");
    public static string ReviewWarnings => InstallerCulture.Pick("Warnings", "Предупреждения");
    public static string WillBeRemoved => InstallerCulture.Pick("Will be removed", "Будет удалено");
    public static string WillBeKept => InstallerCulture.Pick("Will be kept", "Будет сохранено");
    public static string TechnicalDetails => InstallerCulture.Pick("Technical details", "Технические подробности");

    // --- System check -------------------------------------------------------------------
    public static string Probing => InstallerCulture.Pick(
        "Starting winget, git and ollama…",
        "Запускаю winget, git и ollama…");

    public static string InterruptedRun => InstallerCulture.Pick(
        "Interrupted previous run",
        "Прерванный предыдущий запуск");

    public static string RollBackPreviousRun => InstallerCulture.Pick(
        "Roll back previous run",
        "Откатить предыдущий запуск");

    // --- Prerequisites ------------------------------------------------------------------
    public static string NoAutomatedInstaller => InstallerCulture.Pick(
        "No automated installer is available; install it yourself.",
        "Автоматической установки нет; установите его вручную.");

    // --- LocalAi package ----------------------------------------------------------------
    public static string VersionToInstall => InstallerCulture.Pick(
        "Version to install",
        "Версия для установки");

    public static string VersionHint => InstallerCulture.Pick(
        "Leave this at latest to install whatever is newest when you press the button on " +
        "the last page. Type a version number instead to pin one.",
        "Оставьте latest, чтобы установить самый новый релиз на момент нажатия кнопки на " +
        "последней странице. Введите номер версии, чтобы закрепить конкретную.");

    public static string InstallFromFolder => InstallerCulture.Pick(
        "Install from a folder",
        "Установка из папки");

    public static string ReleaseFolder => InstallerCulture.Pick("Release folder", "Папка релиза");
    public static string ModelToInstall => InstallerCulture.Pick("Model to install", "Модель для установки");
    public static string ModelContextSize => InstallerCulture.Pick("Model context size", "Размер контекста модели");

    public static string FolderHint => InstallerCulture.Pick(
        "A folder holding release-manifest.json, release-manifest.sig and " +
        "localai-package.zip. Leave empty to install from GitHub. The manifest is verified " +
        "against the key built into this installer either way; models still come from the " +
        "Ollama registry.",
        "Папка с файлами release-manifest.json, release-manifest.sig и localai-package.zip. " +
        "Оставьте пустым, чтобы установить с GitHub. Манифест в любом случае проверяется " +
        "ключом, встроенным в этот установщик; модели по-прежнему берутся из реестра Ollama.");

    public static string CheckAgain => InstallerCulture.Pick("Check again", "Проверить снова");

    // --- Models and memory --------------------------------------------------------------
    public static string VideoMemory => InstallerCulture.Pick("Video memory", "Видеопамять");

    public static string RequireFullVram => InstallerCulture.Pick(
        "Require the whole model in video memory (recommended)",
        "Требовать всю модель в видеопамяти (рекомендуется)");

    public static string RequireFullVramHint => InstallerCulture.Pick(
        "Refuses to run a model that does not fit. Fastest, and the only rule that never " +
        "degrades an answer.",
        "Отказывается запускать модель, которая не помещается. Самый быстрый вариант и " +
        "единственное правило, которое никогда не ухудшает ответ.");

    public static string AllowPartialOffload => InstallerCulture.Pick(
        "Allow part of the model in system memory — slower",
        "Разрешить часть модели в системной памяти — медленнее");

    public static string AllowPartialOffloadHint => InstallerCulture.Pick(
        "Needs an adapter that holds at least part of the model.",
        "Нужен адаптер, вмещающий хотя бы часть модели.");

    public static string AllowCpu => InstallerCulture.Pick(
        "Allow running on the processor — much slower",
        "Разрешить работу на процессоре — намного медленнее");

    public static string AllowCpuHint => InstallerCulture.Pick(
        "Works without a usable adapter.",
        "Работает без пригодного адаптера.");

    public static string LocalModels => InstallerCulture.Pick("Local models", "Локальные модели");

    public static string ChooseAutomatically => InstallerCulture.Pick(
        "Choose automatically for this computer",
        "Выбрать автоматически для этого компьютера");

    public static string ChooseExactly => InstallerCulture.Pick("Choose exactly", "Выбрать точно");

    public static string Model => InstallerCulture.Pick("Model", "Модель");

    public static string ContextSize => InstallerCulture.Pick("Context size", "Размер контекста");

    public static string ContextSizeHint => InstallerCulture.Pick(
        "Only the sizes this model permits are offered. A smaller context needs less video " +
        "memory.",
        "Предлагаются только те размеры, которые допускает эта модель. Меньший контекст " +
        "требует меньше видеопамяти.");

    public static string SkipModelSetup => InstallerCulture.Pick(
        "Skip model setup",
        "Пропустить настройку моделей");

    public static string SkipModelSetupHint => InstallerCulture.Pick(
        "Nothing is downloaded now. Models can be set up later.",
        "Сейчас ничего не скачивается. Модели можно настроить позже.");

    // --- Client applications --------------------------------------------------------------

    /// <summary>
    /// The label says the cost rather than the benefit, and names the part a reader would
    /// otherwise discover by being refused: it applies to the commands they type themselves.
    /// </summary>
    public static string RefuseTextSearch => InstallerCulture.Pick(
        "Refuse text searches in Claude Code, including the ones you type",
        "Отклонять текстовый поиск в Claude Code, включая набранный вами");

    /// <summary>
    /// Under the combo rather than beside a checkbox: this is an effect of configuring the
    /// client, not a question. It names what changes and where the choice actually lives — the
    /// folder is per repository, and a machine-wide box could not answer that.
    /// </summary>
    public static string WorktreePlacementHint => InstallerCulture.Pick(
        "A worktree this client opens is created in a worktrees folder beside the repository " +
        "instead of inside it, where anything that walks the tree — indexing included — would " +
        "read it as part of the repository. Two lines in settings.json do that, and " +
        "git config localai.worktreeRoot moves the folder.",
        "Worktree, который открывает этот клиент, создаётся в папке worktrees рядом с " +
        "репозиторием, а не внутри него, где всё, что обходит дерево, — включая индексацию — " +
        "считало бы его частью репозитория. Это делают две строки в settings.json; папку " +
        "переносит git config localai.worktreeRoot.");

    public static string RefuseTextSearchHint => InstallerCulture.Pick(
        "grep, rg, findstr, Select-String and Claude Code's own Grep and Glob are turned down " +
        "before they run, with a message naming the codesearch tools that answer the same " +
        "question. Who is calling is not checked, so the commands you type yourself are " +
        "refused too. git log --grep reads commit messages rather than the tree and still runs.",
        "grep, rg, findstr, Select-String, а также собственные Grep и Glob в Claude Code " +
        "отклоняются до запуска — с сообщением, которое называет инструменты codesearch, " +
        "отвечающие на тот же вопрос. Кто вызывает, не проверяется, поэтому набранные вами " +
        "команды отклоняются тоже. git log --grep читает сообщения коммитов, а не дерево, и " +
        "работает по-прежнему.");

    /// <summary>
    /// Shown instead when the choice above does not register the MCP servers. It has to say both
    /// halves: why the refusal would be useless without them, and that the machine keeps what it
    /// has — a disabled control that silently withdrew a setting would be the worse failure.
    /// </summary>
    public static string RefuseTextSearchNeedsMcp => InstallerCulture.Pick(
        "Available when the choice above registers the MCP servers: the refusal names the " +
        "codesearch tools that answer the same question instead, and without them it would only " +
        "get in the way. This computer keeps the setting it has now — nothing is written to " +
        "settings.json.",
        "Доступно, когда выбор выше регистрирует MCP-серверы: отказ называет инструменты " +
        "codesearch, которые отвечают на тот же вопрос вместо поиска, а без них он только " +
        "мешал бы. На этом компьютере остаётся то, что настроено сейчас, — в settings.json " +
        "ничего не пишется.");

    // --- Confirm ------------------------------------------------------------------------
    /// <summary>
    /// Names both routes. It said only "release", and somebody whose machine cannot reach
    /// GitHub is not looking for a release — they are looking for a folder, and the box that
    /// takes one lives behind this button.
    /// </summary>
    public static string ChooseARelease => InstallerCulture.Pick(
        "Choose a release or folder…",
        "Выбрать релиз или папку…");

    public static string BrowseForFolder => InstallerCulture.Pick(
        "Browse…",
        "Обзор…");

    public static string BrowseForFolderTitle => InstallerCulture.Pick(
        "The folder holding release-manifest.json, release-manifest.sig and localai-package.zip",
        "Папка с release-manifest.json, release-manifest.sig и localai-package.zip");

    public static string ChangeSettings => InstallerCulture.Pick(
        "Change settings…",
        "Изменить настройки…");

    public static string AfterThisInstallation => InstallerCulture.Pick(
        "After this installation",
        "После этой установки");

    public static string EnableUpdateCheck => InstallerCulture.Pick(
        "After it is installed, let LocalAi look up whether a newer release exists",
        "После установки разрешить LocalAi проверять, вышел ли более новый релиз");

    public static string UpdateCheckHint => InstallerCulture.Pick(
        "A newer version is then mentioned in localai doctor and in the index status your " +
        "assistant reads. Nothing pops up, and nothing installs itself.",
        "Тогда о более новой версии сообщат localai doctor и статус индекса, который " +
        "читает ваш ассистент. Ничего не всплывает и ничего не устанавливается само.");

    // --- Progress, finish, navigation ---------------------------------------------------
    public static string Log => InstallerCulture.Pick("Log", "Журнал");

    public static string RollBackChanges => InstallerCulture.Pick(
        "Roll back changes",
        "Откатить изменения");

    public static string Rollback => InstallerCulture.Pick("Rollback", "Откат");

    public static string Back => InstallerCulture.Pick("Back", "Назад");

    public static string Next => InstallerCulture.Pick("Next", "Далее");

    /// <summary>The group of errands, for a screen reader. Never rendered.</summary>
    public static string ChooseWhatToDo => InstallerCulture.Pick(
        "Choose what to do",
        "Выберите, что сделать");

    // --- Language and theme, on every screen -------------------------------------------
    public static string ThemeLabel => InstallerCulture.Pick("Theme", "Тема");

    public static string ThemeSystem => InstallerCulture.Pick("System", "Системная");

    public static string ThemeLight => InstallerCulture.Pick("Light", "Светлая");

    public static string ThemeDark => InstallerCulture.Pick("Dark", "Тёмная");

    public static string PreferencesNotSaved => InstallerCulture.Pick(
        "Could not save this choice. It applies only until the installer closes.",
        "Не удалось сохранить выбор. Он действует только до закрытия установщика.");

    /// <summary><c>{0}</c> is the exception's own message, untranslated.</summary>
    public static string PreferencesNotSavedReason => InstallerCulture.Pick("Reason: {0}", "Причина: {0}");

    // --- The removal wizard -------------------------------------------------------------
    public static string RemoveLocalAi => InstallerCulture.Pick(
        "Remove LocalAi",
        "Удаление LocalAi");

    public static string StartFrom => InstallerCulture.Pick("Start from", "С чего начать");

    public static string WhatGoes => InstallerCulture.Pick("What goes", "Что удаляется");

    public static string ConnectedRepositories => InstallerCulture.Pick(
        "Connected repositories",
        "Подключённые репозитории");

    public static string RemovalConsent => InstallerCulture.Pick(
        "I have read what will be removed and want to continue.",
        "Я понимаю, что будет удалено, и хочу продолжить.");

    public static string Report => InstallerCulture.Pick("Report", "Отчёт");

    public static string ContinueToInstall => InstallerCulture.Pick(
        "Continue to install",
        "Перейти к установке");

    public static string Uninstall => InstallerCulture.Pick("Uninstall", "Удалить");

    public static string ModelWaiting => InstallerCulture.Pick("waiting", "ожидает");

    /// <summary>
    /// Which of the two it was is the difference between a run that spent twenty minutes on this
    /// model and one that spent none.
    /// </summary>
    public static string ModelReadyDownloaded => InstallerCulture.Pick(
        "ready — downloaded",
        "готова — скачана");

    public static string ModelReadyAlreadyHere => InstallerCulture.Pick(
        "ready — already on this computer",
        "готова — уже есть на этом компьютере");

    public static string ModelDoesNotFit => InstallerCulture.Pick(
        "does not fit, so it is not used.",
        "не помещается, поэтому не используется.");

    /// <summary>
    /// <c>{0}</c> is a list of «model / context», which are identifiers and stay as they are.
    /// </summary>
    public static string ModelSmallerOptions => InstallerCulture.Pick(
        "Smaller options that fit: {0}",
        "Помещаются варианты меньше: {0}");

    /// <summary><c>{0}</c> is a status token from the run, never translated.</summary>
    public static string ModelFailed => InstallerCulture.Pick(
        "failed ({0})",
        "не удалось ({0})");

    public static string ModelNotReached => InstallerCulture.Pick(
        "not reached — model setup stopped before this one",
        "не дошло — настройка моделей остановилась раньше");

    public static string ModelCancelled => InstallerCulture.Pick(
        "validation cancelled",
        "проверка отменена");

    public static string ModelLimitedDownloaded => InstallerCulture.Pick(
        "ready with limitations — downloaded",
        "готова с ограничениями — скачана");

    public static string ModelLimitedAlreadyHere => InstallerCulture.Pick(
        "ready with limitations — already on this computer",
        "готова с ограничениями — уже на компьютере");

    public static string ModelResultUnknown => InstallerCulture.Pick(
        "validation result unknown",
        "результат проверки неизвестен");

    public static string ResultPackage => InstallerCulture.Pick(
        "LocalAi package",
        "Пакет LocalAi");

    public static string ResultPrerequisites => InstallerCulture.Pick(
        "Prerequisites",
        "Необходимые компоненты");

    public static string ResultClients => InstallerCulture.Pick(
        "Client applications",
        "Клиентские приложения");

    public static string ResultPath => "PATH";

    public static string ResultNotAttempted => InstallerCulture.Pick(
        "not attempted",
        "не выполнялось");

    public static string ResultLeftUnchanged => InstallerCulture.Pick(
        "left unchanged",
        "не менялись");

    /// <summary>
    /// Said instead of counting to zero four times, which is what this page did on the ordinary
    /// run — the one where the machine already has everything.
    /// </summary>
    public static string ResultNothingSelected => InstallerCulture.Pick(
        "nothing was selected",
        "ничего не выбрано");

    public static string ResultInstalledCount => InstallerCulture.Pick(
        "installed {0} of {1}",
        "установлено {0} из {1}");

    public static string ResultInstalledAndFailed => InstallerCulture.Pick(
        "installed {0} of {1}, failed {2}",
        "установлено {0} из {1}, не удалось {2}");

    public static string ResultModelsReady => InstallerCulture.Pick(
        "{0} of {1} ready",
        "готовы {0} из {1}");

    public static string ResultModelsRejected => InstallerCulture.Pick("{0} rejected", "отказ: {0}");

    public static string ResultModelsLimited => InstallerCulture.Pick(
        "with limitations: {0}", "с ограничениями: {0}");

    public static string ResultModelsFailed => InstallerCulture.Pick("{0} failed", "ошибки: {0}");

    public static string ResultModelsCancelled => InstallerCulture.Pick(
        "validation cancelled: {0}", "проверка отменена: {0}");

    public static string ResultModelsNotValidated => InstallerCulture.Pick(
        "{0} not validated", "не проверялись: {0}");

    public static string ResultModelsUnknown => InstallerCulture.Pick(
        "result unknown: {0}", "результат неизвестен: {0}");

    public static string ResultModelsWorking => InstallerCulture.Pick(
        "{0} checks in progress", "проверяются: {0}");

    public static string WhatToDoNext => InstallerCulture.Pick(
        "What to do next",
        "Что делать дальше");

    /// <summary><c>{0}</c> is a list of client names.</summary>
    public static string RestartClients => InstallerCulture.Pick(
        "Restart {0} so the new setup is picked up.",
        "Перезапустите {0}, чтобы новая настройка заработала.");

    public static string OpenReportFolder => InstallerCulture.Pick(
        "Open folder",
        "Открыть папку");

    public static string CopyRemovalReport => InstallerCulture.Pick("Copy details", "Скопировать сведения");
    public static string SaveRemovalReport => InstallerCulture.Pick("Save report as…", "Сохранить отчёт как…");
}
