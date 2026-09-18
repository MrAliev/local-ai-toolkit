using System.Globalization;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The language and theme every installer window shows, changed from any of them. The language
/// itself stays in <see cref="InstallerCulture"/>, which every string already reads.
/// </summary>
public sealed class InstallerPresentation(
    InstallerPreferencesStore? store = null,
    Action<InstallerTheme>? applyTheme = null) : ObservableObject
{
    /// <summary>Null while every choice so far reached the disk; the reason when the last did not.</summary>
    private string? writeFailure;

    /// <summary>Raised after the new language is in effect, for text that is not a label.</summary>
    public event EventHandler? LanguageChanged;

    public bool IsEnglish => InstallerCulture.Current == InstallerLanguage.English;

    public bool IsRussian => InstallerCulture.Current == InstallerLanguage.Russian;

    public InstallerTheme Theme { get; private set; } = store?.ReadTheme() ?? InstallerTheme.System;

    public bool IsSystemTheme => Theme == InstallerTheme.System;

    public bool IsLightTheme => Theme == InstallerTheme.Light;

    public bool IsDarkTheme => Theme == InstallerTheme.Dark;

    public bool HasWriteFailure => writeFailure is not null;

    public string? WriteFailureNotice => writeFailure is null ? null : PageLabels.PreferencesNotSaved;

    public bool HasWriteFailureReason => !string.IsNullOrWhiteSpace(writeFailure);

    public string? WriteFailureReason => HasWriteFailureReason
        ? string.Format(PageLabels.PreferencesNotSavedReason, writeFailure)
        : null;

    public void ChooseLanguage(InstallerLanguage language)
    {
        if (language == InstallerCulture.Current)
        {
            return;
        }

        InstallerCulture.Current = language;
        if (store is not null)
        {
            Remember(store.WriteLanguage(language), store);
        }

        LiveLabels.Current.Announce();
        OnPropertyChanged(string.Empty);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ignores the theme already shown: a hidden start window keeps its panel bound, and the binding
    /// settling there reports the choice back as a click.
    /// </summary>
    public void ChooseTheme(InstallerTheme theme)
    {
        if (theme == Theme)
        {
            return;
        }

        Theme = theme;
        if (store is not null)
        {
            Remember(store.WriteTheme(theme), store);
        }

        applyTheme?.Invoke(theme);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// The notice says the next launch will not show this choice, so it rests on what the next
    /// launch would read, not on whether this write failed.
    /// </summary>
    private void Remember(bool written, InstallerPreferencesStore target) =>
        writeFailure = written ||
            target.OnDisk(CultureInfo.CurrentUICulture) == (InstallerCulture.Current, Theme)
            ? null
            : target.WriteFailure ?? string.Empty;
}
