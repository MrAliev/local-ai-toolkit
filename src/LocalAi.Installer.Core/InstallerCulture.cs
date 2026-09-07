using LocalAi.Contracts.Localization;

namespace LocalAi.Installer.Core;

/// <summary>
/// The languages the installer speaks. The same two every document in this repository comes in.
/// </summary>
public enum InstallerLanguage
{
    English,
    Russian,
}

/// <summary>
/// Which language the installer is speaking, for the length of one run.
///
/// Process state rather than a parameter threaded through every call, because there is exactly
/// one installer window and it cannot be in two languages at once. It lives in the core rather
/// than beside the view models because the removal wizard's rows, presets and preview — the
/// sentences somebody actually consents to — are built here.
///
/// The one thing this deliberately does not touch is <see cref="System.Globalization"/>: numbers
/// and dates keep the operating system's formatting, which is what the rest of the machine uses.
///
/// Why this assembly's text is <see cref="Pick"/> rather than a <c>.resx</c> pair reached through
/// a <c>TextCatalogue</c>, which is what the other five assemblies use and what this repository's
/// notes state unconditionally: both strings sit in one call, so a pair cannot drift apart at all.
/// The parity test a catalogue brings exists to catch that drift after it has happened; here there
/// is nothing to catch. Deliberate, and the reason is recorded because the shape looks like an
/// oversight from the outside (#400).
///
/// What was an oversight, and is fixed rather than argued with: nothing outside the installer
/// window ever set <see cref="Current"/>, so any other process reaching this text was answered in
/// English on every machine and looked like it was working. <see cref="FollowProcessCulture"/> is
/// how such a process says which language it resolved to.
/// </summary>
public static class InstallerCulture
{
    /// <summary>
    /// English until somebody says otherwise, rather than the machine's language. A derived
    /// default would make every test that does not pin a language answer differently depending on
    /// the machine it ran on.
    /// </summary>
    public static InstallerLanguage Current { get; set; } = InstallerLanguage.English;

    public static bool IsRussian => Current == InstallerLanguage.Russian;

    /// <summary>The one of two strings this language calls for.</summary>
    public static string Pick(string english, string russian) => IsRussian ? russian : english;

    /// <summary>
    /// Adopts the language this process resolved to, as <c>OutputCulture</c> decided it — the
    /// environment variable, then the stored choice, then the machine.
    ///
    /// For every entry point that is not the installer window. The decision itself is not repeated
    /// here: it is read from the one place that makes it, so the two cannot come to disagree.
    /// </summary>
    public static void FollowProcessCulture() =>
        Current = OutputCulture.IsRussian ? InstallerLanguage.Russian : InstallerLanguage.English;
}
