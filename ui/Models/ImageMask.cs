using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoLab.UI.Models;

public sealed record MaskPainting(ImageLayer Background, IReadOnlyList<DrawingStroke> Strokes, bool BlackPreview, float BrushSize);

// Values are one byte per pixel. The source and settings permit nondestructive editing.
public sealed record ImageMask(string Id, string Name, int Width, int Height, byte[] Values,
    ImageLayer Source, OperationOptions Generator, BitmapImage Thumbnail)
{
    public MaskPainting? Painting { get; init; }
    public ImageRect? Selection { get; init; }
    public string Label => $"{Name} ({Width} x {Height})";
    public byte[] Encode()
    {
        if (Values.LongLength != (long)Width * Height) throw new ArgumentException("Invalid mask size.");
        byte[] bgra = new byte[checked(Values.Length * 4)];
        for (int i = 0; i < Values.Length; i++)
        {
            bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = Values[i];
            bgra[i * 4 + 3] = 255;
        }
        return bgra;
    }
}
