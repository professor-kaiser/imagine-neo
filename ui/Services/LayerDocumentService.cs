using System.IO;
using System.Security.Cryptography;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

public sealed class LayerDocumentService(NamedPipeClient client, SharedMemoryService memory, ThumbnailService thumbnails)
{
    public async Task<ImageDocument> CreateAsync(ImageDocument source, IReadOnlyList<ImageLayer> layers,
        string fileName, ImageRect? selection = null, IReadOnlyList<ImageMask>? masks = null)
    {
        if (layers.Count is < 1 or > 64) throw new ArgumentException("A document needs between 1 and 64 layers.");
        int width = layers.Max(layer => layer.Width), height = layers.Max(layer => layer.Height);
        if (width is < 1 or > 32768 || height is < 1 or > 32768 || (long)width * height * 4 > 512L * 1024 * 1024)
            throw new ArgumentException("Layer canvas exceeds the image size limit.");
        foreach (var layer in layers)
            if (layer.Width <= 0 || layer.Height <= 0 || layer.Pixels.LongLength != (long)layer.Width * layer.Height * 4 ||
                !double.IsFinite(layer.Opacity) || layer.Opacity < 0 || layer.Opacity > 1)
                throw new ArgumentException("Invalid BGRA layer or opacity.");
        var pixels = await Task.Run(() => Composite(layers, width, height));
        string id = Guid.NewGuid().ToString("N");
        string digest = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var thumbnail = await thumbnails.CreateAsync(pixels, width, height);
        using var lease = memory.CreateImage(pixels, width, height);
        try
        {
            var response = await client.SendAsync(new CommandMessage { MessageType = "import_image", ImageId = id,
                BatchId = Guid.NewGuid().ToString("N"), FileName = fileName, PixelBuffer = lease.Descriptor });
            if (response.Sha256 != digest || response.Width != width || response.Height != height)
                throw new InvalidDataException("Layer composite differs from the backend image.");
            return new ImageDocument(id, fileName, source.FilePath, width, height, pixels, thumbnail, digest, layers.ToArray())
            { Masks = masks ?? source.Masks, Selection = selection is { } r && r.Right <= width && r.Bottom <= height ? selection : null };
        }
        catch
        {
            try { await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = id }); }
            catch (Exception) { }
            throw;
        }
    }

    public static byte[] Composite(IReadOnlyList<ImageLayer> layers, int width, int height)
    {
        byte[] result = new byte[checked(width * height * 4)];
        bool first = true;
        foreach (var layer in layers.Where(layer => layer.Visible))
        {
            for (int y = 0; y < layer.Height; y++)
                for (int x = 0; x < layer.Width; x++)
                {
                    int s = (y * layer.Width + x) * 4, d = (y * width + x) * 4;
                    // Exact bytes for the first full-opacity layer, including hidden RGB.
                    if (first && layer.Opacity == 1)
                    {
                        Array.Copy(layer.Pixels, s, result, d, 4);
                        continue;
                    }
                    double a = layer.Pixels[s + 3] / 255.0 * layer.Opacity;
                    if (a == 0) continue;
                    double b = result[d + 3] / 255.0 * (1 - a), alpha = a + b;
                    for (int c = 0; c < 3; c++)
                        result[d + c] = (byte)Math.Clamp(Math.Round((layer.Pixels[s + c] * a + result[d + c] * b) / alpha,
                            MidpointRounding.AwayFromZero), 0, 255);
                    result[d + 3] = (byte)Math.Clamp(Math.Round(alpha * 255, MidpointRounding.AwayFromZero), 0, 255);
                }
            first = false;
        }
        return result;
    }
}
