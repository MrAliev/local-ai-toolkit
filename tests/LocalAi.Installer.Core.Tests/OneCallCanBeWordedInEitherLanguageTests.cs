namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The installer's review compares what its list says before and after a tick, and a switch of
/// language rewords every line without changing a fact. So one call can be worded in a language of
/// its own, while the window goes on speaking the one chosen for it.
/// </summary>
public sealed class OneCallCanBeWordedInEitherLanguageTests
{
    [Theory]
    [InlineData(InstallerLanguage.English, InstallerLanguage.Russian, "ru")]
    [InlineData(InstallerLanguage.Russian, InstallerLanguage.English, "en")]
    public void The_call_speaks_its_own_language_and_the_window_keeps_its(
        InstallerLanguage window,
        InstallerLanguage call,
        string expected)
    {
        InstallerCulture.Current = window;

        var said = InstallerCulture.In(call, () => InstallerCulture.Pick("en", "ru"));

        Assert.Equal(expected, said);
        Assert.Equal(window, InstallerCulture.Current);
        Assert.Equal(window == InstallerLanguage.Russian ? "ru" : "en", InstallerCulture.Pick("en", "ru"));
    }

    [Fact]
    public void A_call_that_throws_leaves_the_window_its_language()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Throws<InvalidOperationException>(() =>
            InstallerCulture.In<string>(InstallerLanguage.English, () => throw new InvalidOperationException()));

        Assert.Equal("ru", InstallerCulture.Pick("en", "ru"));
    }
}
