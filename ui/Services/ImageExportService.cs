using System.IO;
using PhotoLab.UI.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLab.UI.Services;

public sealed class ImageExportService
{
    public async Task SaveAsync(ImageDocument document, StorageFile destination)
    {
        string extension = Path.GetExtension(destination.Name).ToLowerInvariant();
        bool jpeg = extension is ".jpg" or ".jpeg";
        if (!jpeg && extension != ".png") throw new ArgumentException("Choose a PNG or JPEG file.");
        byte[] pixels = document.Pixels;
        if (jpeg)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
                pixels[i + 3] = 255;
            }
        }
        // Encode completely before touching the destination; WIC uses its own stream adapter.
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)document.Width, (uint)document.Height, 96, 96, pixels);
        await encoder.FlushAsync();
        encoded.Seek(0);
        using var transaction = await destination.OpenTransactedWriteAsync();
        using var output = transaction.Stream;
        output.Size = 0;
        await RandomAccessStream.CopyAsync(encoded, output);
        await output.FlushAsync();
        await transaction.CommitAsync();
    }
}
