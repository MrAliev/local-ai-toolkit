using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Applies the saved palette, with Windows High Contrast taking precedence while enabled.
///
/// The palette is merged dictionary zero and nothing else ever is, so a swap is one assignment
/// and no control has to be told. That only holds while every brush reference is a
/// DynamicResource: a StaticResource brush is a permanently light control after a swap, which
/// is why <c>ThemeReferencesAreDynamicTests</c> exists.
///
/// The system subscription is a static event, so it is a root that would otherwise keep this
/// object — and the window it repaints — alive for the life of the process. It is dropped
/// on shutdown. Explicit themes also listen so High Contrast can override them temporarily.
/// </summary>
public sealed class InstallerThemeSwitch : IDisposable
{
    private const string LightPalette = "Themes/Light.xaml";
    private const string DarkPalette = "Themes/Dark.xaml";

    private readonly ResourceDictionary resources;
    private readonly Func<bool> systemPrefersDark;
    private readonly Func<bool, ResourceDictionary> palette;
    private readonly Func<bool> highContrast;
    private readonly Func<ResourceDictionary> highContrastPalette;
    private InstallerTheme chosen;
    private bool listening;
    private bool disposed;

    public InstallerThemeSwitch(
        ResourceDictionary resources,
        InstallerTheme chosen,
        Func<bool>? systemPrefersDark = null,
        Func<bool, ResourceDictionary>? palette = null,
        Func<bool>? highContrast = null,
        Func<ResourceDictionary>? highContrastPalette = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        this.resources = resources;
        this.systemPrefersDark = systemPrefersDark ?? InstallerThemes.SystemPrefersDark;
        this.highContrast = highContrast ?? (() => SystemParameters.HighContrast);
        this.highContrastPalette = highContrastPalette ?? HighContrastPalette.Create;
        // Relative rather than a pack URI: at run time this resolves against the application,
        // which is the assembly the palettes live in. A test hands its own in, because loading
        // the real one needs that assembly to be the running application.
        this.palette = palette ?? (dark => new ResourceDictionary
        {
            Source = new Uri(dark ? DarkPalette : LightPalette, UriKind.Relative),
        });
        this.chosen = chosen;
    }

    /// <summary>Raised after the palette has been swapped, for chrome the resources cannot reach.</summary>
    public event EventHandler<bool>? Applied;

    public bool IsDark { get; private set; }
    public bool IsHighContrast { get; private set; }

    /// <summary>Paints the effective theme without changing the saved Light/Dark/System choice.</summary>
    public void Apply()
    {
        if (disposed) return;
        IsDark = InstallerThemes.IsDark(chosen, systemPrefersDark());
        IsHighContrast = highContrast();
        var chosenPalette = IsHighContrast ? highContrastPalette() : palette(IsDark);
        if (IsHighContrast && chosenPalette["WindowFill"] is SolidColorBrush background)
            IsDark = (background.Color.R + background.Color.G + background.Color.B) < 384;

        // Replaced, never appended: the palette is dictionary zero and the control styles that
        // reference it come after, so a second palette on the end would win over neither.
        if (resources.MergedDictionaries.Count == 0)
        {
            resources.MergedDictionaries.Add(chosenPalette);
        }
        else
        {
            resources.MergedDictionaries[0] = chosenPalette;
        }

        // High Contrast must be observed even while an explicit Light/Dark choice is saved.
        Listen(true);
        Applied?.Invoke(this, IsDark);
    }

    public void Choose(InstallerTheme theme)
    {
        chosen = theme;
        Apply();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Listen(false);
    }

    private void Listen(bool wanted)
    {
        if (wanted == listening || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (wanted)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        else
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        listening = wanted;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // General is the category the light/dark switch arrives under. The event comes in on a
        // background thread, and every one of these touches WPF resources.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            if (!disposed)
            {
                Apply();
            }
        }));
    }
}
