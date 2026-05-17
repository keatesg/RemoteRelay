using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace RemoteRelay.Server.Services;

public static class ImageCompressor
{
    private const int MaxDimension = 1280;
    private const int JpegQuality = 75;

    public static async Task<(byte[] Data, string ContentType)> LoadAndCompressAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var fileStream = File.OpenRead(path);
        using var buffered = new MemoryStream();
        await fileStream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        buffered.Position = 0;

        using var decoded = SKBitmap.Decode(buffered)
            ?? throw new InvalidDataException($"Could not decode image: {path}");

        var (width, height) = ScaledSize(decoded.Width, decoded.Height);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var output = new SKBitmap(info);
        using (var canvas = new SKCanvas(output))
        using (var sourceImage = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.White);
            var destination = new SKRect(0, 0, width, height);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            canvas.DrawImage(sourceImage, destination, sampling);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return (data.ToArray(), "image/jpeg");
    }

    private static (int Width, int Height) ScaledSize(int width, int height)
    {
        var largest = Math.Max(width, height);
        if (largest <= MaxDimension)
        {
            return (width, height);
        }

        var scale = MaxDimension / (double)largest;
        return ((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }
}
