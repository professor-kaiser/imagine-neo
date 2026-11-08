using System.Numerics;

namespace PhotoLab.UI.Models;

public enum TransformMode { None, Rotation, Scale, RotationScale }

public sealed record ImageTransform(double AngleDegrees, double ScaleFactor)
{
    public static ImageTransform Identity { get; } = new(0, 1);
    public bool HasChanges => Math.Abs(AngleDegrees % 360) > 0.000001 || Math.Abs(ScaleFactor - 1) > 0.000001;
    public static TransformMode ModeFor(string operation) => operation switch
    {
        "rotate_image" => TransformMode.Rotation, "scale_image" => TransformMode.Scale,
        "rotate_scale_image" => TransformMode.RotationScale, _ => TransformMode.None
    };
    public ImageTransform ForMode(TransformMode mode) => mode switch
    {
        TransformMode.Rotation => new(AngleDegrees, 1), TransformMode.Scale => new(0, ScaleFactor),
        TransformMode.RotationScale => this, _ => Identity
    };
    public void Validate()
    {
        if (!double.IsFinite(AngleDegrees) || AngleDegrees < -360 || AngleDegrees > 360)
            throw new ArgumentException("Angle must be between -360 and 360 degrees.");
        if (!double.IsFinite(ScaleFactor) || ScaleFactor < 0.01 || ScaleFactor > 8)
            throw new ArgumentException("Scale must be between 1% and 800%.");
    }
    public (int Width, int Height) OutputSize(int width, int height)
    {
        Validate();
        double angle = AngleDegrees * Math.PI / 180;
        double cos = Math.Abs(Math.Cos(angle)), sin = Math.Abs(Math.Sin(angle));
        // Remove floating-point residue at quarter turns and integral dimensions.
        return (Math.Max(1, (int)Math.Ceiling(ScaleFactor * (width * cos + height * sin) - 1e-9)),
                Math.Max(1, (int)Math.Ceiling(ScaleFactor * (width * sin + height * cos) - 1e-9)));
    }
    public (int Width, int Height) ValidatedOutputSize(int width, int height)
    {
        var size = OutputSize(width, height);
        if (size.Width > 32768 || size.Height > 32768 || (long)size.Width * size.Height * 4 > 512L * 1024 * 1024)
            throw new ArgumentException("The transformed image exceeds 32768 pixels per side or 512 MiB. Reduce the scale.");
        return size;
    }
    public Matrix3x2 Matrix(int width, int height)
    {
        Vector2 center = new(width / 2f, height / 2f);
        return Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateScale((float)ScaleFactor)
            * Matrix3x2.CreateRotation((float)(AngleDegrees * Math.PI / 180)) * Matrix3x2.CreateTranslation(center);
    }
}
