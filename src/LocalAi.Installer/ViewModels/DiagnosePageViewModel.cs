using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Shows what was actually found on the machine. The detector already collects the operating
/// system, disk, network, WinGet, Git, Ollama and every graphics adapter, so the page reports
/// all of it rather than reducing the environment to a single boolean.
/// </summary>
public sealed class DiagnosePageViewModel : ObservableObject
{
    private bool isSupported;
    private string? unsupportedReason;
    private bool hasUsableAdapter;
    private bool hasGitHubSignIn;
    private bool isChecking = true;
    private EnvironmentDiagnosis? loaded;
    private bool registryAnswered;

    /// <summary>The same findings in the language chosen now; nothing is probed again.</summary>
    internal void Relabel()
    {
        if (loaded is { } diagnosis)
        {
            Load(diagnosis, registryAnswered);
        }

        AnnounceEverything();
    }

    public ObservableCollection<EnvironmentCheck> Checks { get; } = [];

    /// <summary>
    /// True while the environment is being probed; without this the first page sat empty and
    /// looked frozen.
    /// </summary>
    public bool IsChecking
    {
        get => isChecking;
        set
        {
            SetProperty(ref isChecking, value);
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(Announcement));
        }
    }

    public bool HasResults => !IsChecking;

    public string Announcement => IsChecking ? string.Empty : IsSupported
        ? InstallerCulture.Pick("System check complete. Review the results below.", "Проверка системы завершена. Результаты приведены ниже.")
        : (InstallerCulture.Pick("System check complete. Installation is unavailable. ", "Проверка системы завершена. Установка недоступна. ") + UnsupportedReason).TrimEnd();

    public bool IsSupported
    {
        get => isSupported;
        set
        {
            SetProperty(ref isSupported, value);
            OnPropertyChanged(nameof(CanContinue));
            OnPropertyChanged(nameof(Announcement));
        }
    }

    public string? UnsupportedReason
    {
        get => unsupportedReason;
        set
        {
            SetProperty(ref unsupportedReason, value);
            OnPropertyChanged(nameof(Announcement));
        }
    }

    /// <summary>
    /// Drives the hint on the residency page. A missing adapter never blocks installation —
    /// it only means the strict residency policy would refuse to load any model.
    /// </summary>
    public bool HasUsableAdapter
    {
        get => hasUsableAdapter;
        private set => SetProperty(ref hasUsableAdapter, value);
    }

    /// <summary>
    /// Whether the GitHub CLI on this machine is signed in. Reported, not required: releases
    /// are public and are read over plain HTTPS, so this only says whether the fallback path
    /// is available — for a network that blocks the release host, or a fork kept private.
    /// </summary>
    public bool HasGitHubSignIn
    {
        get => hasGitHubSignIn;
        private set => SetProperty(ref hasGitHubSignIn, value);
    }

    /// <summary>
    /// Blocked while the check runs: continuing on results that are not in yet would show
    /// the next pages an environment nobody has looked at.
    /// </summary>
    public bool CanContinue => IsSupported && !IsChecking;

    public void SetResult(bool supported, string? reason = null)
    {
        IsSupported = supported;
        UnsupportedReason = supported ? null : reason;
        OnPropertyChanged(nameof(Announcement));
    }

    /// <param name="registryAnswered">
    /// Whether the Ollama registry gave any answer. The network row is worded from it first: an
    /// adapter that is up says nothing about the internet.
    /// </param>
    public void Load(EnvironmentDiagnosis diagnosis, bool registryAnswered)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);

        loaded = diagnosis;
        this.registryAnswered = registryAnswered;
        var rows = new List<EnvironmentCheck>();
        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Operating system", "Операционная система"),
            diagnosis.OperatingSystem.OperatingSystemSupport == SupportStatus.Supported &&
            diagnosis.OperatingSystem.ArchitectureSupport == SupportStatus.Supported
                ? CheckStatus.Ok
                : CheckStatus.Blocking,
            $"{diagnosis.OperatingSystem.ProductName} ({diagnosis.OperatingSystem.Architecture})"));

        rows.Add(Describe(
            "WinGet",
            diagnosis.WinGet,
            InstallerCulture.Pick(
                "Needed to install Git and Ollama automatically.",
                "Нужен, чтобы установить Git и Ollama автоматически.")));
        rows.Add(Describe(
            DependencyCatalog.Git.DisplayName,
            diagnosis.Git,
            InstallerCulture.Pick(
                "Needed to index repositories.",
                "Нужен, чтобы индексировать репозитории.")));
        rows.Add(Describe(
            DependencyCatalog.GitHubCli.DisplayName,
            diagnosis.GitHubCli,
            InstallerCulture.Pick(
                "Optional. Releases are downloaded over plain HTTPS; this is only used as "
                + "a fallback, or for a repository kept private.",
                "Необязателен. Релизы скачиваются по обычному HTTPS; он "
                + "нужен только как запасной путь или для закрытого репозитория.")));

        // Reported, never demanded. Signing in changes nothing for a public release, so
        // this line exists to answer "is my gh usable" for the fallback path and for a
        // private fork — not to make anyone feel unprepared for an ordinary install.
        HasGitHubSignIn = diagnosis.GitHubSignIn.State == DependencyState.Detected;
        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("GitHub sign-in", "Вход в GitHub"),
            CheckStatus.Ok,
            HasGitHubSignIn
                ? diagnosis.GitHubSignIn.Version ??
                    InstallerCulture.Pick("signed in", "выполнен")
                : InstallerCulture.Pick(
                    "not signed in — not required, releases are public",
                    "вход не выполнен — не требуется, релизы публичные")));

        var usableAdapters = diagnosis.Gpu.Adapters
            .Where(adapter => !adapter.IsSoftware)
            .ToArray();
        HasUsableAdapter = usableAdapters.Any(adapter => adapter.DedicatedLocalBytes > 0);
        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Graphics adapters", "Видеоадаптеры"),
            HasUsableAdapter ? CheckStatus.Ok : CheckStatus.Warning,
            diagnosis.Gpu.Adapters.Count > 0
                ? string.Join("; ", diagnosis.Gpu.Adapters.Select(Describe))
                : diagnosis.Gpu.Reason ??
                    InstallerCulture.Pick("No adapter reported.", "Адаптеры не обнаружены.")));

        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Free disk space", "Свободное место на диске"),
            diagnosis.Disk.AvailableBytes is > 0 and var free && free >= 8L * 1024 * 1024 * 1024
                ? CheckStatus.Ok
                : CheckStatus.Warning,
            diagnosis.Disk.AvailableBytes is { } bytes
                ? string.Format(
                    InstallerCulture.Pick("{0:N1} GB available", "{0:N1} ГБ свободно"),
                    bytes / (1024d * 1024 * 1024))
                : diagnosis.Disk.Reason ?? InstallerCulture.Pick("unknown", "неизвестно")));

        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Network", "Сеть"),
            registryAnswered ? CheckStatus.Ok : CheckStatus.Warning,
            registryAnswered
                ? InstallerCulture.Pick(
                    "online: the Ollama registry answers",
                    "есть доступ в интернет: реестр Ollama отвечает")
                : diagnosis.Network.State switch
                {
                    ObservationState.Available => InstallerCulture.Pick(
                        "connected to a network, but the Ollama registry does not answer — models " +
                        "can come only from this computer",
                        "подключение к сети есть, но реестр Ollama не отвечает — модели можно взять " +
                        "только с этого компьютера"),
                    ObservationState.Unavailable => InstallerCulture.Pick(
                        "no network connection", "подключения к сети нет"),
                    _ => string.Format(
                        InstallerCulture.Pick(
                            "the Ollama registry does not answer, and the network adapters could " +
                            "not be read: {0}",
                            "реестр Ollama не отвечает, а сетевые адаптеры прочитать не удалось: {0}"),
                        diagnosis.Network.Reason ?? InstallerCulture.Pick("unknown", "неизвестно")),
                }));

        rows.Add(new EnvironmentCheck(
            InstallerCulture.Pick("Existing LocalAi", "Установленный LocalAi"),
            diagnosis.ExistingLocalAi.State switch
            {
                var state when state.NothingInstalled() => CheckStatus.Ok,
                ExistingLocalAiState.Compatible => CheckStatus.Ok,
                _ => CheckStatus.Warning,
            },
            diagnosis.ExistingLocalAi.State switch
            {
                // What a stopped removal left is not an installation: this still is a first one.
                var state when state.NothingInstalled() => InstallerCulture.Pick(
                    "none — this will be a first installation",
                    "нет — это будет первая установка"),
                ExistingLocalAiState.Compatible => string.Format(
                    InstallerCulture.Pick("version {0} at {1}", "версия {0} в {1}"),
                    diagnosis.ExistingLocalAi.Version,
                    diagnosis.ExistingLocalAi.VersionPath),
                _ => diagnosis.ExistingLocalAi.Reason ?? InstallerCulture.Pick(
                    "present but not recognised",
                    "есть, но не распознан"),
            }));

        // In place when the table has the same shape, which it always has after the first load:
        // the rows keep their place on screen and in the automation tree.
        if (Checks.Count == rows.Count)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                Checks[index].Become(rows[index]);
            }
        }
        else
        {
            Checks.Clear();
            foreach (var row in rows)
            {
                Checks.Add(row);
            }
        }

        SetResult(
            diagnosis.IsSupported,
            diagnosis.UnsupportedReasons.Count == 0
                ? null
                : string.Join("; ", diagnosis.UnsupportedReasons));

        OnPropertyChanged(nameof(Checks));
    }

    private static string Describe(GpuAdapterSnapshot adapter) =>
        GpuAdapterDisplay.Describe(adapter);

    /// <summary>
    /// Failed is a command found on disk whose version probe did not answer; the detector always
    /// gives its path. It blocks nothing, so it is a warning rather than "not found". The title is
    /// the tool's display name: the snapshot's name is the detector's identifier.
    /// </summary>
    private static EnvironmentCheck Describe(string title, DependencySnapshot snapshot, string purpose) =>
        snapshot.State switch
        {
            DependencyState.Detected => new(
                title,
                CheckStatus.Ok,
                string.Join(
                    " — ",
                    new[] { snapshot.Version, snapshot.ExecutablePath }
                        .Where(part => !string.IsNullOrWhiteSpace(part)))),
            DependencyState.Failed => new(
                title,
                CheckStatus.Warning,
                string.Format(
                    InstallerCulture.Pick(
                        "Found at {0}, but it did not report a version ({1}). {2}",
                        "Найден в {0}, но не сообщил версию ({1}). {2}"),
                    snapshot.ExecutablePath,
                    snapshot.Reason,
                    purpose)),
            _ => new(
                title,
                CheckStatus.Missing,
                snapshot.Reason is { Length: > 0 } reason
                    ? $"{purpose} ({reason})"
                    : purpose),
        };
}
