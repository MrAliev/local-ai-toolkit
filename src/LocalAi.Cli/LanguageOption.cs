using System.Globalization;
using LocalAi.Contracts.Localization;

namespace LocalAi.Cli;

/// <summary>
/// The <c>--language</c> option, in one place because three commands carry it and a fourth
/// deliberately does not.
///
/// It takes the two codes this installation has strings for rather than a language name, because
/// on these commands the language of the answer and the language of the notice line above it are
/// the same fact: the option is the per-call form of a setting that already exists under that
/// name. <c>translate --from</c> and <c>--to</c> take names instead, and rightly — those name
/// content languages in an open set that nothing validates.
///
/// Exact codes only. No <c>ru-RU</c>, and no <c>system</c>: a per-call flag has no use for
/// "follow the machine", which is what the absence of the flag already means.
/// </summary>
internal static class LanguageOption
{
    public const string Name = "--language";

    /// <summary>The accepted set, rendered the way <c>policy</c> renders it.</summary>
    public static string Supported => string.Join(", ", OutputCulture.Supported);

    public static bool IsSupported(string? value) =>
        value is not null &&
        OutputCulture.Supported.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the whole run, not the prompt alone: the notice line above the answer and the
    /// answer itself are the same reader's language, and a Russian line over an English answer is
    /// the state this option exists to prevent rather than to create. Called only on a successful
    /// parse, so a refusal is printed in the language the run started in — the override never took
    /// effect, and there is nothing to switch to.
    ///
    /// It outranks <c>--json</c>, which pins English for a machine reader. That default says the
    /// output must not depend on whose machine produced it; a language on the command line did not
    /// come from the machine, it came from the caller. Formatting stays invariant either way,
    /// because this overload sets the UI culture alone.
    /// </summary>
    public static void Apply(string? language)
    {
        if (language is null)
        {
            return;
        }

        OutputCulture.Apply(language, CultureInfo.CurrentUICulture);
    }
}
