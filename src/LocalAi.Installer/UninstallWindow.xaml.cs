using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>
/// The wizard in uninstall mode. Navigation lives in the view model, so this file only
/// forwards the events WPF cannot express as a binding.
/// </summary>
public partial class UninstallWindow : Window, IReturnsToStart
{
    private readonly UninstallWizardViewModel viewModel;

    public UninstallWindow(
        InstallerPresentation presentation,
        RemovalPreset preset = RemovalPreset.FullUninstall,
        bool offersInstallAfterwards = false,
        bool canReturnToStart = false)
    {
        viewModel = new UninstallWizardViewModel(
            preset,
            offersInstallAfterwards,
            canReturnToStart: canReturnToStart,
            presentation: presentation);
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
        viewModel.InstallRequested += (_, _) =>
        {
            // The errand travels: without it the install half of a clean reinstall runs
            // as a plain install, and the two strings written for this path — the window
            // title and the consent line — were unreachable.
            var installer = new MainWindow(viewModel.Presentation, StartChoice.CleanReinstall);
            Application.Current.MainWindow = installer;
            installer.Show();
            Close();
        };
    }

    // async void, so an escaping exception would land on the dispatcher and kill the wizard;
    // it goes into the view model's error state instead, exactly as the install window does.
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

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(viewModel.ReportText); }
        catch (System.Runtime.InteropServices.ExternalException exception) { viewModel.ReportUnexpectedError(exception); }
    }

    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = PageLabels.SaveRemovalReport,
            FileName = "uninstall-report.txt",
            DefaultExt = ".txt",
            Filter = "Text (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) == true) viewModel.SaveReport(dialog.FileName);
    }

    private void OnOpenReportFolder(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasReportPath) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + viewModel.ReportPath + "\"",
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            viewModel.ReportUnexpectedError(exception);
        }
    }

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: RemovalPreset preset })
        {
            viewModel.SelectedPreset = preset;
        }
    }
}
