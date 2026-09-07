using System.Globalization;
using LocalAi.Contracts.Localization;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// `InstallerCulture.Current` is a settable static that starts at English, and only the installer
/// window ever set it. Anything else reaching this assembly's text — a CLI, a launcher, a future
/// caller of `BrokerModelInstaller` — was therefore answered in English on every machine, and
/// looked like it was working (#400).
///
/// `OutputCulture`'s own comment names that failure; this is the way out of it. The rule for which
/// language a process speaks stays in one place, and the installer's static is told to follow it
/// rather than carrying a second copy of the rule that could drift.
///
/// The default stays English rather than becoming the machine's language. A derived default would
/// make every unpinned test in this repository answer differently depending on the machine it ran
/// on, which is the shape of test that proves nothing and fails somewhere else.
/// </summary>
[PinInstallerLanguage]
public sealed class TheInstallerLanguageFollowsTheProcessTests
{
    [Theory]
    [InlineData("ru", InstallerLanguage.Russian)]
    [InlineData("en", InstallerLanguage.English)]
    // A language nothing here has resources for falls back the way the rest of the product does.
    [InlineData("de", InstallerLanguage.English)]
    public void Following_the_process_adopts_the_language_it_resolved_to(
        string requested,
        InstallerLanguage expected)
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            OutputCulture.Apply(requested, CultureInfo.InvariantCulture);

            InstallerCulture.FollowProcessCulture();

            Assert.Equal(expected, InstallerCulture.Current);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }

    /// <summary>
    /// And it is the same decision `OutputCulture.Pick` makes, taken from the same place rather
    /// than restated. Two copies of "is this Russian" is how one of them comes to be wrong.
    /// </summary>
    [Fact]
    public void It_is_the_same_decision_the_rest_of_the_product_makes()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            OutputCulture.Apply("ru", CultureInfo.InvariantCulture);
            InstallerCulture.FollowProcessCulture();

            Assert.Equal(
                OutputCulture.Pick("english", "russian"),
                InstallerCulture.Pick("english", "russian"),
                StringComparer.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
            CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }
}
