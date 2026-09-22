using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Contracts;
using LocalLm.Core;
using SkiaSharp;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This evidence probe targets Windows.");
await Probe.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class Probe
{
    public static async Task RunAsync(string[] args)
    {
        var elapsed = Stopwatch.StartNew();
        object result;
        try
        {
            result = args[0] switch
            {
                "generate" => Generate(Path.GetFullPath(args[1])),
                "text" => Extract(Path.GetFullPath(args[1])),
                "raster" => await RasterAsync(Path.GetFullPath(args[1])),
                _ => throw new ArgumentException("generate|text|raster path"),
            };
        }
        catch (Exception error)
        {
            result = new { status = "rejected", error = error.GetType().Name, message = error.Message };
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result, elapsedMs = Math.Round(elapsed.Elapsed.TotalMilliseconds, 3),
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            platform = RuntimeInformation.OSDescription,
            pdfToImage = typeof(PDFtoImage.Conversion).Assembly.GetName().Version?.ToString(),
            inference = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Generate(string directory)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Fixture directory must be empty.");
        string[] english = ["Invoice ALPHA-7319", "Total: 42.50 EUR", "Repeated line", "Repeated line"];
        string[] unicode = ["Счёт БЕТА-4826", "Итого: 42,50 €"];
        var fixtures = new List<object>();
        void Add(string name, string[][] pages, bool[] scan, bool hidden = false, float width = 400, float height = 240)
        {
            var path = Path.Combine(directory, name + ".pdf");
            using (var stream = File.Create(path))
            using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
            { Creator = "LocalAi PDF evidence v1", Creation = DateTime.UnixEpoch, Modified = DateTime.UnixEpoch }))
            {
                for (var page = 0; page < pages.Length; page++)
                {
                    var canvas = document.BeginPage(width, height);
                    canvas.Clear(SKColors.White);
                    if (scan[page])
                    {
                        using var bitmap = new SKBitmap(800, 480);
                        using (var raster = new SKCanvas(bitmap)) { raster.Clear(SKColors.White); raster.Scale(2); Draw(raster, pages[page], SKColors.Black); }
                        using var image = SKImage.FromBitmap(bitmap);
                        canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
                    }
                    else Draw(canvas, pages[page], SKColors.Black);
                    if (hidden) Draw(canvas, ["HIDDEN UNRELATED TEXT"], SKColors.White, 200);
                    document.EndPage();
                }
                document.Close();
            }
            fixtures.Add(new { file = Path.GetFileName(path), kind = hidden ? "hidden-text" : scan.All(value => value) ? "scan" : scan.Any(value => value) ? "mixed" : "text",
                referencePages = pages.Select(lines => string.Join("\n", lines)).ToArray(), sha256 = Digest(path), bytes = new FileInfo(path).Length });
        }
        Add("text-en", [english], [false]);
        Add("text-unicode", [unicode], [false]);
        Add("scan-en", [english], [true]);
        Add("mixed", [english, unicode], [false, true]);
        Add("hidden-text", [english], [false], hidden: true);
        Add("large-page", [english], [false], width: 10000, height: 10000);
        Add("wide-page", [english[..2]], [false], width: 40000, height: 100);
        var original = File.ReadAllBytes(Path.Combine(directory, "text-en.pdf"));
        foreach (var percent in new[] { 25, 75, 95 })
            Damaged("truncated-" + percent, original[..(original.Length * percent / 100)]);
        var wrongXref = Regex.Replace(Encoding.Latin1.GetString(original), @"startxref\s+\d+", "startxref\n0");
        Damaged("wrong-xref", Encoding.Latin1.GetBytes(wrongXref), [string.Join("\n", english)]);
        Damaged("not-pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 512 * 1024)));
        Damaged("cyclic-pages", CyclicPages());
        return new { status = "generated", fixtures, font = "Segoe UI", seed = "literal-v1" };

        void Damaged(string name, byte[] bytes, string[]? references = null)
        {
            var path = Path.Combine(directory, name + ".pdf");
            File.WriteAllBytes(path, bytes);
            fixtures.Add(new { file = Path.GetFileName(path), kind = "damaged", referencePages = references ?? Array.Empty<string>(), sha256 = Digest(path), bytes = bytes.Length });
        }
    }

    private static void Draw(SKCanvas canvas, string[] lines, SKColor color, float start = 40)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 18);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        for (var index = 0; index < lines.Length; index++)
            canvas.DrawText(lines[index], 24, start + index * 32, SKTextAlign.Left, font, paint);
    }

    private static byte[] CyclicPages()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [2 0 R] /Count 1 /MediaBox [0 0 400 240] >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static object Extract(string path)
    {
        if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new IOException("Input exceeds 256 MiB.");
        var input = File.ReadAllBytes(path);
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        Native.FPDF_InitLibrary();
        nint document = 0;
        try
        {
            var timer = Stopwatch.StartNew();
            document = Native.FPDF_LoadMemDocument64(pinned.AddrOfPinnedObject(), (nuint)input.Length, 0);
            var loadMs = timer.Elapsed.TotalMilliseconds;
            if (document == 0) return new { status = "rejected", nativeError = Native.FPDF_GetLastError(), loadMs };
            var count = Native.FPDF_GetPageCount(document);
            if (count is < 1 or > 1000) throw new IOException("Page count outside probe budget.");
            var pages = new List<string>();
            var zeroMappings = 0;
            timer.Restart();
            for (var index = 0; index < count; index++)
            {
                var page = Native.FPDF_LoadPage(document, index);
                if (page == 0) throw new IOException("Native page load failed.");
                try
                {
                    var text = Native.FPDFText_LoadPage(page);
                    if (text == 0) throw new IOException("Native text page load failed.");
                    try
                    {
                        var length = Native.FPDFText_CountChars(text);
                        if (length is < 0 or > 1_000_000) throw new IOException("Character count outside probe budget.");
                        var value = new StringBuilder();
                        for (var character = 0; character < length; character++)
                        {
                            var scalar = Native.FPDFText_GetUnicode(text, character);
                            if (scalar == 0 || !Rune.IsValid((int)scalar)) { zeroMappings++; value.Append('\uFFFD'); }
                            else value.Append(new Rune((int)scalar).ToString());
                        }
                        pages.Add(value.ToString());
                    }
                    finally { Native.FPDFText_ClosePage(text); }
                }
                finally { Native.FPDF_ClosePage(page); }
            }
            return new { status = "read", loadMs, textMs = timer.Elapsed.TotalMilliseconds, pages, zeroMappings };
        }
        finally
        {
            if (document != 0) Native.FPDF_CloseDocument(document);
            Native.FPDF_DestroyLibrary();
            pinned.Free();
        }
    }

    private static async Task<object> RasterAsync(string path)
    {
        var client = new CaptureClient();
        var timer = Stopwatch.StartNew();
        await new LocalTasks(client).ReadImageAsync([path], "Capture raster dimensions only", LocalTaskProfile.Ocr,
            null, false, CancellationToken.None);
        return new { status = "read", pipelineMs = timer.Elapsed.TotalMilliseconds, pages = client.Pages };
    }

    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class CaptureClient : ILocalModelClient
    {
        public List<object> Pages { get; } = [];
        public Task<LocalJobResult<string>> RoutedChatAsync(LocalTaskProfile profile, string prompt, string? system,
            IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload, LocalWorkflowHint? workflow,
            string? modelOverride, int? requestedContextTokens, LocalJobPriority priority, CancellationToken cancellationToken = default)
        {
            var bytes = Convert.FromBase64String(imagesBase64!.Single());
            using var bitmap = SKBitmap.Decode(bytes);
            Pages.Add(new { number = Pages.Count + 1, bitmap.Width, bitmap.Height, pixels = workload.TotalImagePixels,
                pngSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            return Task.FromResult(new LocalJobResult<string>("raster captured; no inference",
                new LocalUsageReceipt(Guid.NewGuid(), "probe", "capture", "", TimeSpan.Zero, TimeSpan.Zero,
                    0, 0, null, null, null)));
        }
        public Task<LocalJobResult<string>> ChatAsync(string model, string prompt, string? system, IReadOnlyList<string>? imagesBase64,
            LocalJobPriority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(string model, int contextTokens, string catalogVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(string model, string catalogVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(LocalTaskProfile profile, string model,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(LocalTaskProfile profile, string model,
            ExperimentOwnerAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static class Native
    {
        [DllImport("pdfium")] internal static extern void FPDF_InitLibrary();
        [DllImport("pdfium")] internal static extern void FPDF_DestroyLibrary();
        [DllImport("pdfium")] internal static extern nint FPDF_LoadMemDocument64(nint buffer, nuint size, nint password);
        [DllImport("pdfium")] internal static extern uint FPDF_GetLastError();
        [DllImport("pdfium")] internal static extern void FPDF_CloseDocument(nint document);
        [DllImport("pdfium")] internal static extern int FPDF_GetPageCount(nint document);
        [DllImport("pdfium")] internal static extern nint FPDF_LoadPage(nint document, int index);
        [DllImport("pdfium")] internal static extern void FPDF_ClosePage(nint page);
        [DllImport("pdfium")] internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport("pdfium")] internal static extern void FPDFText_ClosePage(nint text);
        [DllImport("pdfium")] internal static extern int FPDFText_CountChars(nint text);
        [DllImport("pdfium")] internal static extern uint FPDFText_GetUnicode(nint text, int index);
    }
}
