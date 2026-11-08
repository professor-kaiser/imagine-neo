namespace PhotoLab.UI.Models;

public readonly record struct ImageRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(double x, double y) => x >= X && y >= Y && x <= Right && y <= Bottom;
    public ImageRect MoveTo(int x, int y, int imageWidth, int imageHeight) =>
        this with { X = Math.Clamp(x, 0, imageWidth - Width), Y = Math.Clamp(y, 0, imageHeight - Height) };
    public override string ToString() => $"X {X} · Y {Y} · {Width} × {Height} px";
}
