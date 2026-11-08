using Microsoft.UI.Xaml.Media;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Models;

public sealed class PaletteEntry(PaletteColor entry)
{
    public ColorRgba Color => entry.Color;
    public SolidColorBrush Swatch => new(Windows.UI.Color.FromArgb((byte)Color.A, (byte)Color.R, (byte)Color.G, (byte)Color.B));
    public string Label => $"RGBA({Color.R}, {Color.G}, {Color.B}, {Color.A})";
    public string FrequencyText => $"{entry.Count} pixels · {entry.Frequency:P1}";
}
