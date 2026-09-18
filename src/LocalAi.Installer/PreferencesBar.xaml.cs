using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer;

/// <summary>Language and theme, on every screen of every window. Bound to the shared <see cref="InstallerPresentation"/>.</summary>
public partial class PreferencesBar : UserControl
{
    public PreferencesBar() => InitializeComponent();

    /// <summary>
    /// The link pressed turns into plain text, so focus goes to the other one rather than to nowhere.
    /// </summary>
    private void OnChooseLanguage(object sender, RoutedEventArgs e)
    {
        if (DataContext is InstallerPresentation presentation &&
            sender is Button { Tag: string name } link &&
            Enum.TryParse<InstallerLanguage>(name, out var language))
        {
            var hadFocus = link.IsKeyboardFocused;
            presentation.ChooseLanguage(language);
            if (hadFocus)
            {
                var other = ReferenceEquals(link, ToRussian) ? ToEnglish : ToRussian;
                Dispatcher.BeginInvoke(() => other.Focus(), DispatcherPriority.Input);
            }
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
