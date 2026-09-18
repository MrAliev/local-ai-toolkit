using System.Globalization;
using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// One answer to "which language, which theme" for every window in the process. The start screen
/// used to own it, so the wizards behind it could read the choice but never change it (#602).
/// </summary>
public sealed class InstallerPresentationTests : IDisposable
{
    private readonly TemporaryDirectory root = new("LocalAi-presentation");

    public void Dispose() => root.Dispose();

    [Fact]
    public void A_language_chosen_is_spoken_at_once_and_remembered()
    {
        var presentation = new InstallerPresentation(Store());

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(InstallerLanguage.Russian, InstallerCulture.Current);
        Assert.True(presentation.IsRussian);
        Assert.False(presentation.IsEnglish);
        Assert.Equal(
            InstallerLanguage.Russian,
            Store().ReadLanguage(new CultureInfo("en-US")));
    }

    [Fact]
    public void A_language_chosen_is_announced_to_whoever_follows_it()
    {
        var presentation = new InstallerPresentation(Store());
        var heard = 0;
        presentation.LanguageChanged += (_, _) => heard++;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(1, heard);
    }

    /// <summary>
    /// Choosing the language already on screen is not a change. Announcing it would repaint every
    /// window for nothing, and rebuild the rows on a start screen somebody is reading.
    /// </summary>
    [Fact]
    public void Choosing_the_language_already_spoken_announces_nothing()
    {
        var presentation = new InstallerPresentation(Store());
        var heard = 0;
        presentation.LanguageChanged += (_, _) => heard++;

        presentation.ChooseLanguage(InstallerLanguage.English);

        Assert.Equal(0, heard);
    }

    /// <summary>
    /// The disk refusing is not the choice failing. The window in front of the person switches
    /// anyway, and says that the next launch will not remember it — silently forgetting was the
    /// only failure the store knew how to have.
    /// </summary>
    [Fact]
    public void A_language_that_cannot_be_remembered_still_applies_and_says_so()
    {
        var store = Unwritable();
        var presentation = new InstallerPresentation(store);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(InstallerLanguage.Russian, InstallerCulture.Current);
        Assert.True(presentation.HasWriteFailure);
        Assert.False(string.IsNullOrWhiteSpace(presentation.WriteFailureNotice));
        Assert.True(presentation.HasWriteFailureReason);
        Assert.Contains(store.WriteFailure!, presentation.WriteFailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_theme_that_cannot_be_remembered_still_applies_and_says_so()
    {
        var applied = new List<InstallerTheme>();
        var presentation = new InstallerPresentation(Unwritable(), applied.Add);

        presentation.ChooseTheme(InstallerTheme.Dark);

        Assert.Equal([InstallerTheme.Dark], applied);
        Assert.True(presentation.IsDarkTheme);
        Assert.True(presentation.HasWriteFailure);
    }

    /// <summary>A write that reaches the disk takes the earlier complaint away.</summary>
    [Fact]
    public void A_later_choice_that_is_remembered_clears_the_complaint()
    {
        var blocker = root.Combine("blocker");
        File.WriteAllText(blocker, string.Empty);
        var presentation = new InstallerPresentation(
            new InstallerPreferencesStore(Path.Combine(blocker, "preferences")));
        presentation.ChooseTheme(InstallerTheme.Dark);
        Assert.True(presentation.HasWriteFailure);

        File.Delete(blocker);
        presentation.ChooseTheme(InstallerTheme.Light);

        Assert.False(presentation.HasWriteFailure);
        Assert.Null(presentation.WriteFailureNotice);
    }

    /// <summary>
    /// The notice says the next launch will not remember the choice. A write that failed while the
    /// file already held that very choice loses nothing, and saying otherwise would be untrue.
    /// </summary>
    [Fact]
    public void A_failed_write_the_disk_already_agrees_with_is_not_a_complaint()
    {
        var directory = root.Combine("preferences");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "ui-preferences.json");
        File.WriteAllText(file, "{\"schemaVersion\":1,\"language\":\"English\",\"theme\":null}");
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(directory));

        // Readable but not writable: every write fails with a sharing violation.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            presentation.ChooseLanguage(InstallerLanguage.Russian);
            Assert.True(presentation.HasWriteFailure);

            presentation.ChooseLanguage(InstallerLanguage.English);
            Assert.False(presentation.HasWriteFailure);
        }
    }

    /// <summary>The notice is text on screen, so it follows the language like everything else.</summary>
    [Fact]
    public void The_complaint_is_said_in_the_language_now_chosen()
    {
        var presentation = new InstallerPresentation(Unwritable());
        presentation.ChooseTheme(InstallerTheme.Dark);
        var english = presentation.WriteFailureNotice;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.NotEqual(english, presentation.WriteFailureNotice);
        Assert.Matches("[А-Яа-я]", presentation.WriteFailureNotice!);
    }

    [Fact]
    public void With_nothing_chosen_the_installer_follows_the_system()
    {
        var presentation = new InstallerPresentation(Store());

        Assert.Equal(InstallerTheme.System, presentation.Theme);
        Assert.True(presentation.IsSystemTheme);
        Assert.False(presentation.IsLightTheme);
        Assert.False(presentation.IsDarkTheme);
    }

    [Fact]
    public void A_theme_remembered_earlier_is_the_one_shown()
    {
        Store().WriteTheme(InstallerTheme.Dark);

        Assert.Equal(InstallerTheme.Dark, new InstallerPresentation(Store()).Theme);
    }

    [Fact]
    public void Choosing_a_theme_repaints_moves_the_selection_and_is_remembered()
    {
        var applied = new List<InstallerTheme>();
        var presentation = new InstallerPresentation(Store(), applied.Add);

        presentation.ChooseTheme(InstallerTheme.Light);

        Assert.Equal([InstallerTheme.Light], applied);
        Assert.True(presentation.IsLightTheme);
        Assert.False(presentation.IsSystemTheme);
        Assert.Equal(InstallerTheme.Light, Store().ReadTheme());
    }

    /// <summary>
    /// The hidden start window keeps its panel bound, and the binding settling there reports the
    /// theme back as a click. Repainting and writing again for it would repeat a refused write's
    /// complaint on every switch.
    /// </summary>
    [Fact]
    public void Choosing_the_theme_already_shown_does_nothing()
    {
        var applied = new List<InstallerTheme>();
        var presentation = new InstallerPresentation(Unwritable(), applied.Add);

        presentation.ChooseTheme(InstallerTheme.System);

        Assert.Empty(applied);
        Assert.False(presentation.HasWriteFailure);
    }

    /// <summary>The panel repaints itself, or the person watches their own choice do nothing.</summary>
    [Fact]
    public void Choosing_a_theme_announces_the_change()
    {
        var presentation = new InstallerPresentation(Store());
        var announced = new List<string?>();
        presentation.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseTheme(InstallerTheme.Dark);

        Assert.Contains(announced, name => Announces(name, nameof(InstallerPresentation.IsDarkTheme)));
        Assert.Contains(announced, name => Announces(name, nameof(InstallerPresentation.IsSystemTheme)));
    }

    [Fact]
    public void Choosing_a_language_announces_which_one_is_active()
    {
        var presentation = new InstallerPresentation(Store());
        var announced = new List<string?>();
        presentation.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Contains(announced, name => Announces(name, nameof(InstallerPresentation.IsRussian)));
        Assert.Contains(announced, name => Announces(name, nameof(InstallerPresentation.IsEnglish)));
    }

    /// <summary>
    /// The contract a binding reads: a named property, or an empty name meaning all of them.
    /// </summary>
    internal static bool Announces(string? announced, string property) =>
        string.IsNullOrEmpty(announced) || announced == property;

    private InstallerPreferencesStore Store() => new(root.Combine("preferences"));

    /// <summary>
    /// A store whose directory sits under a file, so creating it fails on every machine the same
    /// way — a read-only attribute would be ignored by an elevated run.
    /// </summary>
    private InstallerPreferencesStore Unwritable()
    {
        var blocker = root.Combine("not-a-directory");
        File.WriteAllText(blocker, string.Empty);
        return new InstallerPreferencesStore(Path.Combine(blocker, "preferences"));
    }
}
