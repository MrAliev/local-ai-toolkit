using LocalAi.Contracts.Localization;

namespace LocalLm.Core;

/// <summary>
/// Which language a local model is asked to answer in.
///
/// A model answers in the language of the material it was given, so a Russian reader asking about
/// an English build log got English back: their machine's language decided every string the
/// product printed and none of the words the model produced. The language is the one already
/// resolved for the product's own output, so the answer and the line above it agree.
///
/// Naming it in the prompt is the whole mechanism. Measured on this machine against an English
/// build log, the model then answered in fluent Russian and left the paths, the line numbers,
/// <c>CS8602</c> and the quoted log lines exactly as they were — which the triage prompt already
/// demands and the model honoured without further asking. Nothing is translated afterwards, so
/// there is no second model call and nothing to detect.
///
/// The instruction itself is English whichever reader it serves: it is addressed to the model,
/// not to them, and it names their language rather than being written in it.
/// </summary>
internal static class AnswerLanguage
{
    /// <summary>
    /// The sentence to append to a system prompt. It repeats the preservation rule because it is
    /// the instruction that could otherwise be read as licence to translate an identifier, and
    /// the profiles that carry it quote code, paths and log lines by design.
    /// </summary>
    public static string Instruction =>
        "Answer in " + Name +
        ", whatever language the supplied material is in. Leave file paths, line numbers, " +
        "identifiers, error codes and quoted lines exactly as they appear.";

    /// <summary>
    /// English for the model to read, not a token: it is the name of a language inside a
    /// sentence, and the catalogue's rule about untranslated identifiers does not reach here.
    /// </summary>
    private static string Name => OutputCulture.IsRussian ? "Russian" : "English";
}
