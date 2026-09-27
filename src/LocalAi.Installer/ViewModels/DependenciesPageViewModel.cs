using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.ViewModels;

public sealed class DependenciesPageViewModel : ObservableObject
{
    /// <summary>
    /// Only what the installer can act on and the product actually needs.
    ///
    /// The MSVC redistributable used to be listed here. Nothing in LocalAi referenced it —
    /// no catalogue entry, no detection, no code requiring it. The components are
    /// self-contained .NET and Ollama brings its own native dependencies, so it has been
    /// removed rather than left as a line that informs nobody and installs nothing.
    ///
    /// Two are required, and only two. Git is how a repository is identified, scanned and
    /// hooked; Ollama is what computes the embeddings, without which there is no index at
    /// all. Everything else buys precise navigation for one language, and demanding all of
    /// it cost three to four gigabytes and several UAC prompts — winget installs these
    /// machine-wide — from somebody who may only want semantic search over C#. Each optional
    /// line therefore says what skipping it gives up, because "optional" alone invites
    /// skipping everything and discovering the cost at the moment a tool stops answering
    /// precisely.
    /// </summary>
    public ObservableCollection<DependencySelection> Dependencies { get; } =
    [
        new("Git", "Git", true),
        new("Ollama", "Ollama", true),
        new("GitHubCli", "GitHub CLI", false)
        {
            // The repository is public, so releases are read over plain HTTPS with no
            // account at all. Kept for a fork held private, or a network that blocks the
            // release host but not the API.
            ConsequenceWording = (
                "only needed for a private fork, or when the release host is blocked",
                "нужен только для закрытого форка или когда хост релизов заблокирован"),
        },
        new("DotNetSdk", ".NET SDK 10", false)
        {
            ConsequenceWording = (
                "without it, C# definitions and references are answered by text matching "
                + "instead of by the compiler",
                "без него определения и ссылки в C# ищутся текстовым "
                + "совпадением, а не компилятором"),
        },
        new("NodeJs", "Node.js 20", false)
        {
            ConsequenceWording = (
                "needed to install and run SCIP TypeScript and SCIP Python",
                "нужен, чтобы установить и запускать SCIP TypeScript и SCIP Python"),
        },
        new("ScipTypeScript", "SCIP TypeScript", false)
        {
            ConsequenceWording = (
                "without it, TypeScript and JavaScript navigate by text matching",
                "без него навигация по TypeScript и JavaScript идёт текстовым совпадением"),
        },
        new("Python", "Python 3.10+", false)
        {
            ConsequenceWording = (
                "only needed to run the Python indexer",
                "нужен только для индексатора Python"),
        },
        new("ScipPython", "SCIP Python", false)
        {
            ConsequenceWording = (
                "without it, Python navigates by text matching",
                "без него навигация по Python идёт текстовым совпадением"),
        },
    ];

    /// <summary>Every row reads its text again; no consent moves.</summary>
    internal void Relabel()
    {
        foreach (var dependency in Dependencies)
        {
            dependency.AnnounceEverything();
        }

        AnnounceEverything();
    }

    public IReadOnlyList<DependencySelection> SelectedDependencies =>
        [.. Dependencies.Where(dependency => dependency.IsConsented)];

    /// <summary>
    /// A required dependency blocks the wizard only while this installer can install it and it
    /// is neither present nor selected. Nothing is pre-selected: consent is the user's to give.
    /// </summary>
    public bool CanContinue =>
        !Dependencies.Any(dependency =>
            dependency.IsRequired &&
            dependency.IsInstallable &&
            !dependency.IsInstalled &&
            !dependency.IsConsented);

    public string ReviewText
    {
        get
        {
            var selected = Dependencies
                .Where(dependency => dependency.IsConsented)
                .Select(dependency => string.Format(
                    "{0} ({1})",
                    dependency.Title,
                    dependency.IsInstalled
                        ? InstallerCulture.Pick("reinstall", "переустановка")
                        : InstallerCulture.Pick("install", "установка")))
                .ToArray();
            var text = selected.Length == 0
                ? InstallerCulture.Pick(
                    "Dependencies: nothing selected",
                    "Компоненты: ничего не выбрано")
                : InstallerCulture.Pick("Dependencies: ", "Компоненты: ") +
                    string.Join(", ", selected);
            return StillNeeded.Count == 0
                ? text
                : text + string.Format(
                    InstallerCulture.Pick(
                        "; still needed, install by hand: {0}",
                        "; ещё нужны, установить вручную: {0}"),
                    string.Join(", ", StillNeeded));
        }
    }

    /// <summary>Something missing that this installer cannot install: a manual install is checked for here.</summary>
    public bool NeedsCheckAgain =>
        Dependencies.Any(dependency => !dependency.IsInstalled && !dependency.IsInstallable);

    public PrerequisiteOffer Offer =>
        (Dependencies.Any(dependency => dependency.IsInstallable), NeedsCheckAgain) switch
        {
            (true, false) => PrerequisiteOffer.Choose,
            (true, true) => PrerequisiteOffer.ChooseAndGetTheRestByHand,
            (false, true) => PrerequisiteOffer.AllByHand,
            (false, false) => PrerequisiteOffer.AllInstalled,
        };

    /// <summary>Required, missing, and not installable by this installer.</summary>
    public IReadOnlyList<string> StillNeeded =>
        [.. Dependencies
            .Where(dependency => dependency.IsRequired && !dependency.IsInstalled && !dependency.IsInstallable)
            .Select(dependency => dependency.Title)];

    private bool winGetFound = true;
    private bool npmFound = true;

    /// <summary>
    /// Whether the check found WinGet and npm, which the recipes run. Taken as found until a check
    /// says otherwise.
    /// </summary>
    internal void SetInstallers(bool winGetFound, bool npmFound)
    {
        this.winGetFound = winGetFound;
        this.npmFound = npmFound;
        Notify();
    }

    internal static DependencyDefinition? Definition(string dependencyId) =>
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

    public void SetConsent(string id, bool consent)
    {
        var dependency = Find(id);
        if (!dependency.IsInstallable && consent)
        {
            return;
        }

        dependency.IsConsented = consent;
        Notify();
    }

    /// <summary>
    /// Records what the environment probe found. Detection deliberately does not grant
    /// consent: an already installed dependency must not be reinstalled unless asked.
    /// </summary>
    public void SetInstalled(string id, bool installed)
    {
        Find(id).IsInstalled = installed;
        Notify();
    }

    public void MarkInstalled(string id)
    {
        Find(id).IsInstalled = true;
        Notify();
    }

    private DependencySelection Find(string id) =>
        Dependencies.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown dependency '{id}'.");

    private void Notify()
    {
        // Node.js ticked beside WinGet brings npm: the run installs it first and checks the
        // machine again after every component.
        var nodeComes = winGetFound &&
            Dependencies.Any(dependency => dependency.Id == "NodeJs" && dependency.IsConsented);
        foreach (var dependency in Dependencies)
        {
            var definition = Definition(dependency.Id);
            dependency.SetMissing(
                definition?.InstallerKind switch
                {
                    DependencyInstallerKind.WinGet when !winGetFound => MissingInstaller.WinGet,
                    DependencyInstallerKind.Npm when !npmFound && !nodeComes => MissingInstaller.Npm,
                    _ => MissingInstaller.None,
                },
                definition?.OfficialInstallerUri);
            if (!dependency.IsInstallable)
            {
                dependency.IsConsented = false;
            }

            dependency.AnnounceEverything();
        }

        OnPropertyChanged(nameof(Dependencies));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ReviewText));
        OnPropertyChanged(nameof(NeedsCheckAgain));
        OnPropertyChanged(nameof(StillNeeded));
    }
}

/// <summary>What the prerequisites page can offer, which its subtitle says.</summary>
public enum PrerequisiteOffer
{
    Choose,
    ChooseAndGetTheRestByHand,
    AllByHand,
    AllInstalled,
}
