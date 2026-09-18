using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>
/// Navigation lives in the view model and is bound through commands, so this file only
/// forwards the few control events that WPF cannot express as a two-way binding.
/// </summary>
public partial class MainWindow : Window, IReturnsToStart
{
    private readonly InstallerWizardViewModel viewModel;

    public MainWindow(StartChoice mode = StartChoice.Install, bool canReturnToStart = false)
    {
        viewModel = new InstallerWizardViewModel(mode, canReturnToStart: canReturnToStart)
        {
            EnableDependencyActions = true,
        };
        InitializeComponent();
        // The caption is the desktop manager's, not the palette's.
        DarkCaption.Follow(this);
        // Back on the first page leaves the wizard; the start window is what knows
        // where to.
        viewModel.ReturnToStartRequested += (_, _) =>
        {
            ReturnToStart?.Invoke(this, EventArgs.Empty);
            Close();
        };
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.DetachPresentation();
    }

    // Both async void handlers route unexpected exceptions into the view model's error
    // state: escaping ones would land on the dispatcher and kill the installer (#209/m4).
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedError(exception);
        }
    }

    public event EventHandler? ReturnToStart;

    private void OnDependencyConsentChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string dependencyId })
        {
            return;
        }

        viewModel.Dependencies.SetConsent(dependencyId, ((CheckBox)sender).IsChecked == true);
        viewModel.RefreshNavigationState();
    }

    /// <summary>
    /// A log that appends below the fold is a log nobody reads while it matters. It kept the
    /// first screenful in view for the whole run.
    /// </summary>
    private void OnLogTextChanged(object sender, TextChangedEventArgs e) =>
        ((TextBox)sender).ScrollToEnd();

    /// <summary>
    /// The folder, not the file: Explorer is where somebody attaches a log from, and a run that
    /// failed twice has two of them.
    /// </summary>
    private void OnOpenReportFolder(object sender, RoutedEventArgs e)
    {
        if (viewModel.Finish.ReportPath is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The path is on screen beside the button; a shell that will not start is not a
            // reason to take the page down.
        }
    }

    /// <summary>
    /// The folder, chosen rather than typed. Typing the path by hand was the only way to use
    /// this box, and a release folder is somewhere a person navigates to, not something they
    /// remember.
    /// </summary>
    private void OnBrowseForReleaseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = PageLabels.BrowseForFolderTitle,
            InitialDirectory = viewModel.Package.SourceFolder,
        };
        if (dialog.ShowDialog(this) == true)
        {
            // Setting it invalidates whatever the last check resolved and resets the status
            // line, which already says that "Проверить снова" is the next step.
            viewModel.Package.SourceFolder = dialog.FolderName;
        }
    }

    private async void OnPackageCheckRelease(object sender, RoutedEventArgs e)
    {
        try
        {
            await viewModel.ResolvePackageAsync();
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedError(exception);
        }
    }

    private void OnAgentChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string agent } comboBox ||
            comboBox.SelectedItem is not AgentChoiceOption { Choice: var choice })
        {
            return;
        }

        viewModel.Agents.SetChoice(agent, choice);
        viewModel.RefreshNavigationState();
    }

    private void OnReviewConfirmedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            viewModel.SetReviewConfirmed(checkBox.IsChecked == true);
        }
    }
}
