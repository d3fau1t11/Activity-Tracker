using SkiaSharp;
using System.IO;

namespace Activity_Tracker.Relay;

public static class WebpImages
{
    public static byte[] PhotoPreview(byte[] webp)
    {
        using var bitmap = SKBitmap.Decode(webp) ?? throw new InvalidDataException("Invalid WebP.");
        var scale = Math.Min(1, Math.Min(3840d / bitmap.Width, 2160d / bitmap.Height));
        using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)),
            Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded?.ToArray() ?? throw new InvalidDataException("Photo preview could not be encoded.");
    }
    public static byte[] Convert(byte[] bytes, bool lossless)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes)) ?? throw new InvalidDataException("Unsupported image.");
        var info = codec.Info;
        if ((long)info.Width * info.Height > 33_554_432) throw new InvalidDataException("Image exceeds 32 megapixels.");
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Image could not be decoded.");
        using var pixels = bitmap.PeekPixels();
        using var encoded = pixels.Encode(new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, 90));
        if (encoded == null || encoded.Size > 16 * 1024 * 1024) throw new InvalidDataException("Encoded image exceeds 16 MiB.");
        return encoded.ToArray();
    }
}
