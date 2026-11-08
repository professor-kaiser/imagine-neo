using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using PhotoLab.UI.Models;
using Windows.UI;

namespace PhotoLab.UI.Controls.ImageCanvas;

public sealed partial class ImageCanvas
{
    private readonly TransformController _transformGesture = new();
    private TransformMode _transformMode;
    public event EventHandler? TransformChanged;
    public TransformMode TransformMode
    {
        get => _transformMode;
        set
        {
            if (_transformMode == value) return;
            CancelDrawing(); _transformMode = value; _needsFit = true; _canvas.Invalidate();
        }
    }
    public ImageTransform CurrentTransform => (_document?.TransformDraft ?? ImageTransform.Identity).ForMode(_transformMode);
    public void SetTransformValues(ImageTransform transform)
    {
        transform.Validate();
        if (_document is not null) _document.TransformDraft = transform;
        _canvas.Invalidate();
    }
    private void UpdateTransformGesture(Vector2 point)
    {
        if (_document is null || !_transformGesture.IsDragging) return;
        var next = _transformGesture.Update(point);
        var previous = _document.TransformDraft;
        _document.TransformDraft = new(_transformMode == TransformMode.Scale ? previous.AngleDegrees : next.AngleDegrees,
            _transformMode == TransformMode.Rotation ? previous.ScaleFactor : next.ScaleFactor);
        _canvas.Invalidate(); TransformChanged?.Invoke(this, EventArgs.Empty);
    }
    private void DrawTransformHandles(CanvasDrawingSession draw)
    {
        if (_document is null || _transformMode == TransformMode.None) return;
        var transform = CurrentTransform;
        Vector2[] corners = TransformController.Corners(_document.Width, _document.Height, transform);
        var accent = Color.FromArgb(255, 169, 206, 188);
        for (int i = 0; i < 4; i++) draw.DrawLine(corners[i], corners[(i+1)%4], accent, 1.5f / Zoom);
        if (_transformMode is TransformMode.Scale or TransformMode.RotationScale)
            foreach (var point in corners)
            {
                draw.FillRectangle(point.X - 5 / Zoom, point.Y - 5 / Zoom, 10 / Zoom, 10 / Zoom, Colors.White);
                draw.DrawRectangle(point.X - 5 / Zoom, point.Y - 5 / Zoom, 10 / Zoom, 10 / Zoom, Colors.Black, 1 / Zoom);
            }
        if (_transformMode is TransformMode.Rotation or TransformMode.RotationScale)
        {
            Vector2 handle = TransformController.RotationHandle(_document.Width, _document.Height, transform, Zoom);
            draw.DrawLine((corners[0] + corners[1]) / 2, handle, accent, 1 / Zoom);
            draw.FillCircle(handle, 7 / Zoom, accent); draw.DrawCircle(handle, 7 / Zoom, Colors.Black, 1 / Zoom);
        }
        Vector2 center = new(_document.Width / 2f, _document.Height / 2f);
        draw.DrawLine(center - new Vector2(5 / Zoom, 0), center + new Vector2(5 / Zoom, 0), accent, 1 / Zoom);
        draw.DrawLine(center - new Vector2(0, 5 / Zoom), center + new Vector2(0, 5 / Zoom), accent, 1 / Zoom);
    }
    private void DrawBackground(CanvasDrawingSession draw, float left, float top, int width, int height)
    {
        draw.FillRectangle(left, top, width, height, Color.FromArgb(255, 64, 66, 72));
        Vector2 start = _viewport.ToImage(Vector2.Zero), end = _viewport.ToImage(new((float)ActualWidth, (float)ActualHeight));
        int tile = Math.Max(8, (int)(16 / Zoom));
        for (int y = (int)Math.Floor(Math.Max(top, start.Y) / tile) * tile; y < Math.Min(top + height, end.Y + tile); y += tile)
            for (int x = (int)Math.Floor(Math.Max(left, start.X) / tile) * tile; x < Math.Min(left + width, end.X + tile); x += tile)
                if (((x / tile + y / tile) & 1) == 0)
                {
                    float x0 = Math.Max(left, x), y0 = Math.Max(top, y);
                    draw.FillRectangle(x0, y0, Math.Max(0, Math.Min(x + tile, left + width) - x0), Math.Max(0, Math.Min(y + tile, top + height) - y0), Color.FromArgb(255, 83, 85, 92));
                }
    }
}
