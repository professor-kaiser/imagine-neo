using System.Numerics;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Controls.ImageCanvas;

public sealed class TransformController
{
    private bool _rotating;
    private Vector2 _center, _lastVector, _startVector;
    private ImageTransform _start = ImageTransform.Identity, _current = ImageTransform.Identity;
    public bool IsDragging { get; private set; }
    public static Vector2[] Corners(int width, int height, ImageTransform transform) =>
        new[] { Vector2.Zero, new Vector2(width, 0), new Vector2(width, height), new Vector2(0, height) }
            .Select(point => Vector2.Transform(point, transform.Matrix(width, height))).ToArray();
    public static Vector2 RotationHandle(int width, int height, ImageTransform transform, float zoom)
    {
        var top = Vector2.Transform(new(width / 2f, 0), transform.Matrix(width, height));
        var direction = Vector2.TransformNormal(new(0, -1), Matrix3x2.CreateRotation((float)(transform.AngleDegrees * Math.PI / 180)));
        return top + direction * (32 / zoom);
    }
    public bool Begin(Vector2 point, int width, int height, float zoom, TransformMode mode, ImageTransform transform)
    {
        End();
        float radiusSquared = MathF.Pow(12 / zoom, 2);
        bool canRotate = mode is TransformMode.Rotation or TransformMode.RotationScale;
        bool canScale = mode is TransformMode.Scale or TransformMode.RotationScale;
        _rotating = canRotate && Vector2.DistanceSquared(point, RotationHandle(width, height, transform, zoom)) <= radiusSquared;
        if (!_rotating && !(canScale && Corners(width, height, transform).Any(c => Vector2.DistanceSquared(c, point) <= radiusSquared))) return false;
        _center = new(width / 2f, height / 2f);
        _startVector = _lastVector = point - _center;
        _start = _current = transform;
        IsDragging = _startVector.LengthSquared() > 0.000001f;
        return IsDragging;
    }
    public ImageTransform Update(Vector2 point)
    {
        if (!IsDragging) return _current;
        Vector2 vector = point - _center;
        if (vector.LengthSquared() < 0.000001f) return _current;
        if (_rotating)
        {
            double delta = Math.Atan2(_lastVector.X * vector.Y - _lastVector.Y * vector.X, Vector2.Dot(_lastVector, vector)) * 180 / Math.PI;
            double angle = (_current.AngleDegrees + delta + 540) % 360 - 180;
            _current = _current with { AngleDegrees = Math.Round(angle, 2) };
        }
        else _current = _current with { ScaleFactor = Math.Round(Math.Clamp(_start.ScaleFactor * vector.Length() / _startVector.Length(), 0.01, 8) * 100, 2) / 100 };
        _lastVector = vector;
        return _current;
    }
    public void End() => IsDragging = false;
}
