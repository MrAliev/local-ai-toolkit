using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The labels written directly into the markup — group headers, radio buttons, the sentence
/// under each option, the navigation buttons.
///
/// Markup cannot ask which language was chosen, so the choice is made here and the markup binds
/// with <c>{x:Static}</c>. That resolves once, when the window is loaded, which is exactly when
/// it should: the language is settled on the start screen before either wizard window exists.
///
/// Everything that varies with what the run is doing lives in <see cref="WizardText"/> or in the
/// page view model that owns it. This class only holds text that is the same on every run.
/// </summary>
public static class PageLabels
{
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
    public static string ChooseARelease => InstallerCulture.Pick(
        "Choose a release…",
        "Выбрать релиз…");

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

    // --- The theme control, on the start screen beside the language ------------------
    public static string ThemeSystem => InstallerCulture.Pick("System", "Системная");

    public static string ThemeLight => InstallerCulture.Pick("Light", "Светлая");

    public static string ThemeDark => InstallerCulture.Pick("Dark", "Тёмная");

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
}
