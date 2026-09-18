using System.Windows;
using System.Windows.Controls;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>Language and theme, on every screen of every window. Bound to the shared <see cref="InstallerPresentation"/>.</summary>
public partial class PreferencesBar : UserControl
{
    public PreferencesBar() => InitializeComponent();

    private void OnChooseLanguage(object sender, RoutedEventArgs e)
    {
        if (DataContext is InstallerPresentation presentation &&
            sender is Button { Tag: string name } &&
            Enum.TryParse<InstallerLanguage>(name, out var language))
        {
            presentation.ChooseLanguage(language);
        }
    }

    /// <summary>
    /// Checked also fires while bindings settle, here and in the hidden start window's own panel;
    /// the presentation ignores the theme it already has.
    /// </summary>
    private void OnChooseTheme(object sender, RoutedEventArgs e)
    {
        if (DataContext is InstallerPresentation presentation &&
            sender is RadioButton { Tag: string name } &&
            Enum.TryParse<InstallerTheme>(name, out var theme))
        {
            presentation.ChooseTheme(theme);
        }
    }
}
