using System.Numerics;

namespace PhotoLab.UI.Rendering;

public sealed class ViewportTransform
{
    public float Zoom { get; private set; } = 1;
    public Vector2 Offset { get; private set; }
    public Matrix3x2 Matrix => Matrix3x2.CreateScale(Zoom) * Matrix3x2.CreateTranslation(Offset);
    public Vector2 ToImage(Vector2 screen) => (screen - Offset) / Zoom;
    public Vector2 ToScreen(Vector2 image) => image * Zoom + Offset;
    public void Pan(Vector2 delta) => Offset += delta;
    public void ZoomAt(Vector2 anchor, float factor)
    {
        Vector2 image = ToImage(anchor);
        Zoom = Math.Clamp(Zoom * factor, 0.01f, 32f);
        Offset = anchor - image * Zoom;
    }
    public void Fit(float viewWidth, float viewHeight, int imageWidth, int imageHeight)
    {
        Zoom = Math.Clamp(Math.Min(Math.Max(1, viewWidth - 48) / imageWidth, Math.Max(1, viewHeight - 48) / imageHeight), 0.01f, 32f);
        Offset = new Vector2((viewWidth - imageWidth * Zoom) / 2, (viewHeight - imageHeight * Zoom) / 2);
    }
}
