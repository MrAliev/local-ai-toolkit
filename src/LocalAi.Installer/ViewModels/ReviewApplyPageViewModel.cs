using LocalAi.Installer.Core;
using LocalAi.Contracts;

namespace LocalAi.Installer.ViewModels;

/// <summary>A check whose answer can still change the review's list while it runs.</summary>
public enum ReviewCheck
{
    Release,
    FolderPackage,
    ThisComputer,
}

/// <summary>A group of the review's list, in the order the list shows them.</summary>
public enum ReviewSection
{
    Release,
    Components,
    Models,
    Clients,
    Additional,
}

public sealed class ReviewApplyPageViewModel : ObservableObject
{
    private bool isConfirmed;
    private bool enableUpdateCheck;
    private Func<IReadOnlyList<ReviewCheck>> pending = () => [];

    /// <summary>The groups that changed after the last tick, until the box is ticked again.</summary>
    private IReadOnlyList<ReviewSection> changed = [];

    public bool IsConfirmed
    {
        get => isConfirmed;
        set
        {
            SetProperty(ref isConfirmed, value);
            if (value && changed.Count > 0)
            {
                changed = [];
                AnnounceChanged();
            }

            OnPropertyChanged(nameof(CanApply));
        }
    }

    /// <summary>Clears the box whose list is no longer the one it was given to, and says which groups moved.</summary>
    internal void Withdraw(IReadOnlyList<ReviewSection> sections)
    {
        changed = sections;
        IsConfirmed = false;
        AnnounceChanged();
    }

    /// <summary>The line under a box the wizard cleared; empty once it is ticked again.</summary>
    public string ChangedNotice => changed.Count == 0
        ? string.Empty
        : string.Format(
            InstallerCulture.Pick(
                "The list above changed after you confirmed it ({0}). Read it again, then confirm.",
                "Список выше изменился после вашего подтверждения ({0}). Прочитайте его снова и " +
                "подтвердите."),
            string.Join(", ", changed.Select(Title)));

    public bool HasChangedNotice => changed.Count > 0;

    /// <summary>The group's heading, as the list shows it.</summary>
    internal static string Title(ReviewSection section) => section switch
    {
        ReviewSection.Release => PageLabels.ReviewRelease,
        ReviewSection.Components => PageLabels.ReviewComponents,
        ReviewSection.Models => PageLabels.ReviewModels,
        ReviewSection.Clients => PageLabels.ReviewClients,
        _ => PageLabels.ReviewAdditional,
    };

    private void AnnounceChanged()
    {
        OnPropertyChanged(nameof(ChangedNotice));
        OnPropertyChanged(nameof(HasChangedNotice));
    }

    /// <summary>
    /// Whether this installation may look up whether a newer release exists.
    ///
    /// Off unless the box is ticked, and asked here rather than on a page of its own because
    /// this is where a person is already reading what the run will do. It changes nothing
    /// about whether the installation proceeds — an unanswered question is a "no", not a
    /// blocked wizard.
    /// </summary>
    public bool EnableUpdateCheck
    {
        get => enableUpdateCheck;
        set => SetProperty(ref enableUpdateCheck, value);
    }

    /// <summary>
    /// The same sentence `localai policy set --update-check on` prints, held in the contract so
    /// the two cannot drift into describing the same request differently.
    /// </summary>
    public string UpdateCheckDisclosure => InstallerCulture.Pick(
        UpdateCheckPolicy.Disclosure,
        UpdateCheckPolicy.DisclosureRussian);

    /// <summary>Where the checks still running come from; the wizard runs them.</summary>
    internal void WaitFor(Func<IReadOnlyList<ReviewCheck>> checks) => pending = checks;

    /// <summary>
    /// Whether the box can be ticked. Not while a check the list is built from is running: the
    /// tick would be given to a list that is about to change.
    /// </summary>
    public bool CanConfirm => pending().Count == 0;

    public bool IsStillChecking => !CanConfirm;

    /// <summary>The line under the box, and the box's help while it waits; empty otherwise.</summary>
    public string StillChecking
    {
        get
        {
            var checks = pending();
            return checks.Count == 0
                ? string.Empty
                : string.Format(
                    InstallerCulture.Pick(
                        "Still checking {0}. The list above may change, so it can be confirmed once " +
                        "the check is done.",
                        "Ещё идёт проверка {0}. Список выше может измениться, поэтому подтвердить его " +
                        "можно после её окончания."),
                    string.Join(InstallerCulture.Pick(" and ", " и "), checks.Select(Name)));
        }
    }

    private static string Name(ReviewCheck check) => check switch
    {
        ReviewCheck.Release => InstallerCulture.Pick("the release", "релиза"),
        ReviewCheck.FolderPackage => InstallerCulture.Pick("the package in the folder", "пакета в папке"),
        _ => InstallerCulture.Pick("this computer", "этого компьютера"),
    };

    public bool CanApply =>
        IsConfirmed && CanConfirm;

    /// <summary>For the wizard, whenever a check starts or ends.</summary>
    internal void AnnounceChecks()
    {
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(IsStillChecking));
        OnPropertyChanged(nameof(StillChecking));
        OnPropertyChanged(nameof(CanApply));
    }
}
