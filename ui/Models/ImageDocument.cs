using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoLab.UI.Models;

public sealed class ImageDocument : ObservableObject
{
    public IReadOnlyList<ImageLayer> Layers { get; }
    public IReadOnlyList<ImageMask> Masks { get; init; } = [];
    public DrawingDraft Drawing { get; } = new();
    public ImageTransform TransformDraft { get; set; } = ImageTransform.Identity;
    public string Id { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public BitmapImage Thumbnail { get; }
    public string Sha256 { get; }
    public ImageDocument(string id, string fileName, string filePath, int width, int height, byte[] pixels, BitmapImage thumbnail, string sha256, IReadOnlyList<ImageLayer>? layers = null)
    {
        Id = id; FileName = fileName; FilePath = filePath; Width = width; Height = height;
        Layers = layers ?? new[] { new ImageLayer(fileName, width, height, pixels) };
        Pixels = pixels; Thumbnail = thumbnail; Sha256 = sha256;
    }
    public string Dimensions => $"{Width} × {Height} px";
    private ImageRect? _selection;
    public ImageRect? Selection { get => _selection; set => SetProperty(ref _selection, value); }
}
