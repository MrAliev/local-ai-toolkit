using System.Text;
using LocalLm.Core.Resources;

namespace LocalLm.Core;

/// <summary>
/// What reading a document to translate produced: its text, or the refusal that says why there
/// is none, as a code and the sentence that goes with it.
///
/// The code is not an exit code. Two faces read documents now — <c>localai translate --in</c>
/// and <c>translate_local</c>'s <c>path</c> — and only one of them has exit codes at all, so the
/// console maps <see cref="RefusalCode"/> to a number at its own edge and the MCP tool returns
/// the sentence.
/// </summary>
public sealed record TranslateSourceRead(string? Text, string? RefusalCode, string? Message);

/// <summary>
/// Reads a document to translate, for whichever face was asked to.
///
/// One reader for both because the part that matters is the wording of the refusals: a second
/// copy would drift there first, and a reader that disagrees with itself about what "not text"
/// means is worse than either answer. Separate from <c>TranslateCommand</c> because that parser
/// touches no disk — which is what lets it be tested with literal paths — and separate from
/// either entry point because everything here has an answer worth asserting. Every refusal names
/// the *resolved* path: a relative path run from the wrong directory is the commonest cause, and
/// only the resolved form shows it.
/// </summary>
public static class TranslateSource
{
    /// <summary>
    /// Not <c>.mdx</c>: JSX outside a fence is mangled either way, so protecting it would promise
    /// something the translation profile does not keep.
    /// </summary>
    private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

    /// <summary>
    /// Enough of the file to answer "is this text at all". The sentence the refusal prints names
    /// this bound, so the two cannot drift apart.
    /// </summary>
    private const int SniffedBytes = 8000;

    public static bool IsMarkdown(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static TranslateSourceRead Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = Path.GetFullPath(path);

        // Before File.Exists, which answers false for a directory and would then produce the
        // sentence for a path that names nothing — true, but not the mistake that was made.
        if (Directory.Exists(resolved))
        {
            return new TranslateSourceRead(
                null,
                "file_missing",
                LocalLmText.TranslateNotAFile(resolved));
        }

        if (!File.Exists(resolved))
        {
            return new TranslateSourceRead(
                null,
                "file_missing",
                LocalLmText.TranslateFileMissing(resolved));
        }

        string text;
        try
        {
            using var stream = new FileStream(
                resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[SniffedBytes];
            var sniffed = stream.Read(head);
            if (!HasWideByteOrderMark(head.AsSpan(0, sniffed)) &&
                head.AsSpan(0, sniffed).IndexOf((byte)0) >= 0)
            {
                return new TranslateSourceRead(
                    null,
                    "file_not_text",
                    LocalLmText.TranslateFileNotText(resolved));
            }

            stream.Position = 0;
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Its own code rather than `file_missing`, because the request was right and the
            // cause is a lock or a permission: the console turns this one into EX_NOINPUT, the
            // mirror of the EX_CANTCREAT `--out` gives when it cannot write.
            return new TranslateSourceRead(
                null,
                "input_not_read",
                LocalLmText.TranslateFileNotRead(resolved, failure.Message));
        }

        // Whitespace only is empty here too: that is what the task's own guard would reject a
        // moment later, with a vaguer sentence and the wrong code.
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslateSourceRead(
                null,
                "source_missing",
                LocalLmText.TranslateFileEmpty(resolved));
        }

        return new TranslateSourceRead(text, null, null);
    }

    /// <summary>
    /// The exemption that makes the zero-byte scan safe. UTF-16 and UTF-32 with a mark decode
    /// correctly and are full of zero bytes; without a mark there is no correct decode here, and
    /// the scan is exactly what catches that. A UTF-8 mark needs no exemption — it has no zero
    /// byte, and neither does the text after it.
    /// </summary>
    private static bool HasWideByteOrderMark(ReadOnlySpan<byte> head) =>
        (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) ||
        (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF) ||
        (head.Length >= 4 &&
         head[0] == 0x00 && head[1] == 0x00 && head[2] == 0xFE && head[3] == 0xFF);
}
