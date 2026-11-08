using System.IO.MemoryMappedFiles;
using System.IO;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Services;

public sealed class SharedImageLease : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    public PixelBufferDescriptor Descriptor { get; }
    public SharedImageLease(int width, int height)
    {
        long size = checked((long)width * height * 4);
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || size > 512L * 1024 * 1024)
            throw new ArgumentException("Invalid BGRA8 dimensions (limit: 512 MiB).");
        string name = "PhotoLab_Image_" + Guid.NewGuid().ToString("N");
        _mapping = MemoryMappedFile.CreateNew(name, size, MemoryMappedFileAccess.ReadWrite);
        Descriptor = new(name, width, height, 4, "uint8", "BGRA8", checked(width * 4), size);
    }
    public SharedImageLease(byte[] pixels, int width, int height) : this(width, height)
    {
        try
        {
            if (pixels.LongLength != Descriptor.BufferSizeBytes) throw new ArgumentException("Invalid BGRA8 buffer size.");
            using var view = _mapping.CreateViewAccessor(0, Descriptor.BufferSizeBytes, MemoryMappedFileAccess.Write);
            view.WriteArray(0, pixels, 0, pixels.Length);
        }
        catch { _mapping.Dispose(); throw; }
    }
    public byte[] ReadPixels()
    {
        byte[] pixels = new byte[checked((int)Descriptor.BufferSizeBytes)];
        using var view = _mapping.CreateViewAccessor(0, pixels.Length, MemoryMappedFileAccess.Read);
        if (view.ReadArray(0, pixels, 0, pixels.Length) != pixels.Length)
            throw new EndOfStreamException("Incomplete result in shared memory.");
        return pixels;
    }
    public void Dispose() => _mapping.Dispose();
}

public sealed class SharedMemoryService
{
    public SharedImageLease CreateImage(byte[] pixels, int width, int height) => new(pixels, width, height);
    public SharedImageLease CreateOutput(int width, int height) => new(width, height);
}
