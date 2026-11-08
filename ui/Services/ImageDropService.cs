using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotoLab.UI.Services;

public static class ImageDropService
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };

    public static bool CanRead(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems);
    public static bool IsSupported(StorageFile file) => SupportedExtensions.Contains(file.FileType, StringComparer.OrdinalIgnoreCase);

    public static async Task<IReadOnlyList<IStorageItem>> ReadAsync(DataPackageView data)
    {
        if (!CanRead(data)) throw new InvalidDataException("Drop image files from File Explorer.");
        // Resolve the data while the XAML drop deferral is held; do not depend on file paths.
        return await data.GetStorageItemsAsync();
    }
}
