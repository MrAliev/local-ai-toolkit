using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalAi.Installer;

/// <summary>
/// The front door. One executable installs, updates, repairs, reinstalls and removes, so the
/// first question it asks is which of those this is — answered against what is actually on
/// the machine rather than by offering all five and failing on four of them.
/// </summary>
public partial class StartWindow : Window
{
    private readonly InstallerStartViewModel viewModel;

    public StartWindow(InstallerPresentation presentation)
    {
        viewModel = new InstallerStartViewModel(presentation: presentation);
        InitializeComponent();
        // The caption is the desktop manager's, not the palette's.
        DarkCaption.Follow(this);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.DetachPresentation();
    }

    private void OnChooseErrand(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: StartChoice choice })
        {
            viewModel.Select(choice);
        }
    }

    /// <summary>
    /// Stock WPF moves focus with the arrow keys without moving the selection, so a group of
    /// radio buttons would leave the person's choice behind their focus. Disabled rows are not
    /// focusable, so an unavailable errand is skipped without being named here.
    /// </summary>
    private void OnErrandKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Down or Key.Right => 1,
            Key.Up or Key.Left => -1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        var reachable = viewModel.Actions.Where(option => option.IsAvailable).ToArray();
        if (reachable.Length == 0)
        {
            return;
        }

        var current = Array.FindIndex(reachable, option => option.IsSelected);
        var next = current < 0
            ? (step > 0 ? 0 : reachable.Length - 1)
            : ((current + step) % reachable.Length + reachable.Length) % reachable.Length;
        viewModel.Select(reachable[next].Choice);
        FocusChosenErrand();
        e.Handled = true;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (viewModel.Selected is not { } choice)
        {
            return;
        }

        Open(choice switch
        {
            // The errand travels with the window: an update that asks every question an
            // install asks is a wizard that did not hear the answer already given (#257).
            StartChoice.Install or StartChoice.UpdateOrRepair =>
                new MainWindow(viewModel.Presentation, choice, canReturnToStart: true),
            // The removal half opens on the reinstall-friendly row and offers the install half
            // when it finishes; two deliberate wizards, because the install has prerequisites
            // and a release choice of its own to confirm.
            StartChoice.CleanReinstall => new UninstallWindow(
                viewModel.Presentation,
                InstallerStartViewModel.PresetFor(choice),
                offersInstallAfterwards: true,
                canReturnToStart: true),
            _ => new UninstallWindow(
                viewModel.Presentation,
                InstallerStartViewModel.PresetFor(choice),
                canReturnToStart: true),
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Hidden rather than closed, so Back from the wizard's first page finds the errand still
    /// chosen, in whatever language and theme the wizard was left in. Rebuilding it instead
    /// would mean carrying that state out and back through a constructor, and losing whatever
    /// was forgotten.
    ///
    /// A wizard closed any other way — the caption's X, Cancel, a finished run — takes this
    /// screen with it, or the process would linger behind an invisible window.
    /// </summary>
    private void Open(Window window)
    {
        var returning = false;
        if (window is IReturnsToStart wizard)
        {
            wizard.ReturnToStart += (_, _) => returning = true;
        }

        window.Closed += (_, _) =>
        {
            if (returning)
            {
                Application.Current.MainWindow = this;
                Show();
                Activate();
                // A language chosen in the wizard rebuilt the rows, and the one focus was on went
                // with them.
                Dispatcher.BeginInvoke(FocusChosenErrand, DispatcherPriority.Input);
            }
            else
            {
                Close();
            }
        };

        Application.Current.MainWindow = window;
        window.Show();
        Hide();
    }

    private void FocusChosenErrand()
    {
        if (viewModel.Actions.FirstOrDefault(option => option.IsSelected) is { } chosen &&
            Errands.ItemContainerGenerator.ContainerFromItem(chosen) is DependencyObject container &&
            VisualTreeHelper.GetChildrenCount(container) > 0 &&
            VisualTreeHelper.GetChild(container, 0) is RadioButton row)
        {
            row.Focus();
        }
    }
}
