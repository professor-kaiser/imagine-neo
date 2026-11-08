using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLab.UI.Services;

public sealed class ThumbnailService
{
    public async Task<BitmapImage> CreateAsync(byte[] pixels, int width, int height)
    {
        // This small PNG stays inside the UI; IPC always transfers raw shared memory.
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)width, (uint)height, 96, 96, pixels);
        double scale = Math.Min(1, 96d / Math.Max(width, height));
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale));
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale));
        await encoder.FlushAsync();
        stream.Seek(0);
        var thumbnail = new BitmapImage();
        await thumbnail.SetSourceAsync(stream);
        return thumbnail;
    }
}
