using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Models;

public sealed record OperationOptions(string Operation, string DisplayName, IReadOnlyDictionary<string, object> Parameters)
{
    public IReadOnlyList<ImageMask> Masks { get; init; } = [];
    public bool IsTransform => ImageTransform.ModeFor(Operation) != TransformMode.None;
    public ImageTransform Transform => new(Parameters.TryGetValue("angle_degrees", out var angle) ? Convert.ToDouble(angle) : 0,
        Parameters.TryGetValue("scale_factor", out var scale) ? Convert.ToDouble(scale) : 1);
    public bool IsAnalysis => Operation is "pick_color" or "extract_palette";
}

public sealed record OperationResult(ImageDocument? Image = null, ColorRgba? Color = null, IReadOnlyList<PaletteColor>? Palette = null, IReadOnlyList<ImageDocument>? Images = null);
