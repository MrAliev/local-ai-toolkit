using LocalAi.Installer.Core;
using LocalAi.Contracts;

namespace LocalAi.Installer.ViewModels;

public sealed class ReviewApplyPageViewModel : ObservableObject
{
    private bool isConfirmed;
    private bool enableUpdateCheck;

    public bool IsConfirmed
    {
        get => isConfirmed;
        set
        {
            SetProperty(ref isConfirmed, value);
            OnPropertyChanged(nameof(CanApply));
        }
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

    public bool CanApply =>
        IsConfirmed;

}
