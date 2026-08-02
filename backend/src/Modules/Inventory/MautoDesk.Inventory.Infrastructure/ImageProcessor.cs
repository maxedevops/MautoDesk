using MautoDesk.Inventory.Application;
using SkiaSharp;

namespace MautoDesk.Inventory.Infrastructure;

/// <summary>
/// Decodes an uploaded image and re-encodes it as JPEG.
/// </summary>
/// <remarks>
/// <para>
/// <b>The re-encode is a security control, not a convenience.</b> Decoding to a
/// pixel buffer and writing a fresh file discards everything that was not
/// pixels: EXIF (including the GPS coordinates of the dealer's lot, or their
/// home), colour-profile payloads, appended archives, and the polyglot tricks
/// that make a file a valid JPEG and a valid script at the same time.
/// </para>
/// <para>
/// It is also where a 12-megapixel phone photo stops being 8 MB of listing
/// weight. Long edge capped at 2400px for the full size and 480px for the
/// thumbnail — enough for a full-screen listing photo on a retina display, and
/// far less egress than the original.
/// </para>
/// </remarks>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private const int MaxEdge = 2400;

    private const int ThumbnailEdge = 480;

    /// <summary>High enough that a car's paint still looks like paint.</summary>
    private const int Quality = 82;

    private const int ThumbnailQuality = 70;

    public ProcessedImage? Process(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Null, not an exception: "this is not an image" is an ordinary outcome
        // of accepting files from the internet, and the caller turns it into a
        // rejection with a reason.
        using var original = SKBitmap.Decode(content);

        if (original is null || original.Width == 0 || original.Height == 0)
        {
            return null;
        }

        using var full = Resize(original, MaxEdge);
        using var thumbnail = Resize(original, ThumbnailEdge);

        var fullBytes = Encode(full, Quality);
        var thumbnailBytes = Encode(thumbnail, ThumbnailQuality);

        return fullBytes is null || thumbnailBytes is null
            ? null
            : new ProcessedImage(fullBytes, thumbnailBytes, full.Width, full.Height);
    }

    /// <summary>Scales the long edge down to a cap, never up.</summary>
    private static SKBitmap Resize(SKBitmap source, int maxEdge)
    {
        var longest = Math.Max(source.Width, source.Height);

        if (longest <= maxEdge)
        {
            // Copied rather than returned directly so every path can be disposed
            // by the caller without disposing the original twice.
            return source.Copy();
        }

        var scale = (double)maxEdge / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return source.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
    }

    private static byte[]? Encode(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);

        return data?.ToArray();
    }
}
