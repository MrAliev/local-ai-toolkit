using System.Security.Cryptography;
using System.Text;
using LocalLm.Core.Resources;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalLm.Core;

internal sealed record DocumentText(string Sha256, string[] Pages);

internal static class DocumentTextReader
{
    public static async Task<DocumentText> ReadAsync(string path, bool pdf, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var limit = pdf ? 256 : 8;
        if (input.Length > limit * 1024L * 1024)
            throw new DocumentIndexException("source_limit", LocalLmText.Catalogue.Format("DocumentsFileSizeExceeded", path, limit));
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
        input.Position = 0;
        if (!pdf)
        {
            // Decode strictly; never silently replace an invalid byte with a plausible character.
            var bytes = new byte[input.Length];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            var (encoding, preamble) = EncodingFor(bytes);
            try
            {
                var text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
                if (text.Contains('\0')) throw new DocumentIndexException("binary_text", LocalLmText.Catalogue.Format("DocumentsTextBinary", path));
                return new(hash, [text]);
            }
            catch (DecoderFallbackException)
            { throw new DocumentIndexException("invalid_text_encoding", LocalLmText.Catalogue.Format("DocumentsTextEncodingInvalid", path)); }
        }
        try
        {
            // Await parser completion before releasing its pinned input. This is cooperative
            // cancellation between pages, not a hard parser deadline or process sandbox.
            var pages = await Task.Run(() =>
            {
                using var document = PdfDocument.Open(input);
                if (document.IsEncrypted) throw new PdfDocumentFormatException("Encrypted PDFs are not supported.");
                if (document.NumberOfPages is < 1 or > 128)
                    throw new DocumentIndexException("page_limit", LocalLmText.Catalogue.Format("DocumentsPageCountExceeded", path, document.NumberOfPages, 128));
                var result = new List<string>();
                for (var number = 1; number <= document.NumberOfPages; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = document.GetPage(number);
                    if (page.Letters.Count > 64_000) throw new DocumentIndexException("text_limit", LocalLmText.Get("DocumentsTextLimitExceeded"));
                    result.Add(ContentOrderTextExtractor.GetText(page));
                }
                return result.ToArray();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(hash, pages);
        }
        catch (Exception error) when (error is PdfDocumentFormatException or ArgumentException)
        { throw new DocumentIndexException("pdf_unreadable", LocalLmText.PdfUnreadable(path, error.Message)); }
    }

    private static (Encoding Encoding, int Preamble) EncodingFor(byte[] bytes)
    {
        ReadOnlySpan<byte> value = bytes;
        if (value.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (new UTF32Encoding(false, false, true), 4);
        if (value.StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (new UTF32Encoding(true, false, true), 4);
        if (value.StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, false, true), 2);
        if (value.StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), value.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0);
    }
}
