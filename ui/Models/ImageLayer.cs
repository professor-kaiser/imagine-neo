namespace PhotoLab.UI.Models;

// Immutable settings preserve previous revisions. Pixels use straight-alpha BGRA.
public sealed record ImageLayer(string Name, int Width, int Height, byte[] Pixels,
    bool Visible = true, double Opacity = 1)
{
    public string Label => $"{(Visible ? "●" : "○")} {Name} · {Width} × {Height} · {Opacity:P0}";
}
