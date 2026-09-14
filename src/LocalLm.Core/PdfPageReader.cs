using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using LocalLm.Core.Resources;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace LocalLm.Core;

/// <summary>One pinned PDF, bounded page rasters, and at most one encoded page in memory.</summary>
internal sealed class PdfPageReader : IAsyncDisposable
{
    private const long MaximumBytes = 256L * 1024 * 1024;
    private const long MaximumPagePixels = 4_000_000;
    private const int MaximumDimension = 4096;
    private readonly FileStream input;
    private readonly string path;
    private IAsyncEnumerator<string>? rendered;

    private PdfPageReader(string path, FileStream input, IReadOnlyList<ImageInfo> pages)
    {
        this.path = path;
        this.input = input;
        Pages = pages;
    }

    public IReadOnlyList<ImageInfo> Pages { get; }

    public static async Task<PdfPageReader> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length > MaximumBytes)
                throw new ArgumentException(LocalLmText.PdfTooLarge(path, MaximumBytes / 1024 / 1024));
            // PDFium's metadata API is synchronous. Keep it off the caller's thread, and await
            // its completion before disposing the pinned stream even if cancellation arrives.
            var sizes = await Task.Run(() =>
            {
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    return Conversion.GetPageSizes(input, leaveOpen: true).ToArray();
                throw new PlatformNotSupportedException();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (sizes.Length == 0) throw new InvalidDataException("PDF has no pages.");
            var pages = sizes.Select(size => BoundedRaster(size.Width, size.Height)).ToArray();
            input.Position = 0;
            return new PdfPageReader(path, input, pages);
        }
        catch (Exception error) when (error is PdfException or InvalidDataException)
        {
            await input.DisposeAsync();
            throw new ArgumentException(LocalLmText.PdfUnreadable(path, error.Message), nameof(path), error);
        }
        catch
        {
            await input.DisposeAsync();
            throw;
        }
    }

    public async Task<string> ReadNextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            rendered ??= RenderAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        else
            throw new PlatformNotSupportedException();
        try
        {
            if (!await rendered.MoveNextAsync()) throw new InvalidDataException("PDF ended before the expected page.");
            return rendered.Current;
        }
        catch (Exception error) when (error is PdfException or InvalidDataException)
        {
            throw new ArgumentException(LocalLmText.PdfUnreadable(path, error.Message), nameof(path), error);
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private async IAsyncEnumerable<string> RenderAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var first = 0; first < Pages.Count;)
        {
            // The library accepts one render size per enumerator. Consecutive equal-sized
            // pages share it, so an ordinary hundred-page document is opened only once for
            // rendering; a change of page size starts a new, still bounded group.
            var size = Pages[first];
            var end = first + 1;
            while (end < Pages.Count && Pages[end] == size) end++;
            input.Position = 0;
            await foreach (var bitmap in Conversion.ToImagesAsync(input, Enumerable.Range(first, end - first),
                               leaveOpen: true, options: new RenderOptions(Dpi: 150, Width: size.Width, Height: size.Height,
                                   WithAnnotations: true, WithFormFill: true), cancellationToken: cancellationToken))
            {
                string encoded;
                using (bitmap)
                using (var image = SKImage.FromBitmap(bitmap))
                using (var bytes = image.Encode(SKEncodedImageFormat.Png, 100))
                    encoded = Convert.ToBase64String(bytes.AsSpan());
                // The native raster is released before inference, and the next raster is not
                // requested until the current model call has finished.
                yield return encoded;
            }
            first = end;
        }
    }

    private static ImageInfo BoundedRaster(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("PDF page dimensions are invalid.");
        var scale = Math.Min(150.0 / 72, Math.Sqrt(MaximumPagePixels / (width * height)));
        scale = Math.Min(scale, MaximumDimension / Math.Max(width, height));
        return new ImageInfo(Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)), "png");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (rendered is not null) await rendered.DisposeAsync();
        }
        finally
        {
            await input.DisposeAsync();
        }
    }
}
