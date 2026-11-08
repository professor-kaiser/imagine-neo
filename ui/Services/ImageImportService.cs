using System.IO;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

public sealed class ImageImportService(NamedPipeClient client, SharedMemoryService memory)
{
    public async Task<ImageDocument> ImportAsync(StorageFile file, string batchId)
    {
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        uint rawWidth = decoder.OrientedPixelWidth, rawHeight = decoder.OrientedPixelHeight;
        if (rawWidth > 32768 || rawHeight > 32768 || (long)rawWidth * rawHeight * 4 > 512L * 1024 * 1024)
            throw new InvalidDataException("Image is too large (maximum: 512 MiB of pixels).");
        var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        byte[] pixels = pixelData.DetachPixelData();
        int width = checked((int)rawWidth), height = checked((int)rawHeight);
        string identity = Guid.NewGuid().ToString("N");
        string digest = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var thumbnail = new BitmapImage();
        using (var preview = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale))
        {
            if (preview is not null) await thumbnail.SetSourceAsync(preview);
        }
        using var lease = memory.CreateImage(pixels, width, height);
        var response = await client.SendAsync(new CommandMessage { MessageType = "import_image", ImageId = identity,
            BatchId = batchId, FileName = file.Name, PixelBuffer = lease.Descriptor });
        if (response.Sha256 != digest || response.Width != width || response.Height != height)
        {
            await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = identity });
            throw new InvalidDataException("The backend received pixels that differ from the decoded image.");
        }
        return new ImageDocument(identity, file.Name, file.Path, width, height, pixels, thumbnail, digest);
    }
}
