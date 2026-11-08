using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using PhotoLab.UI.Models;
using PhotoLab.UI.Contracts;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace PhotoLab.UI.Rendering;

public sealed class DrawingRenderer : IDisposable
{
    private CanvasRenderTarget? _target;
    private DrawingDraft? _draft;
    private int _revision = -1;
    private bool _hadPreview;
    private ColorRgba? _tint;
    private ImageRect? _clip;

    public CanvasRenderTarget Render(CanvasDevice device, ImageDocument document, DrawingStroke? preview = null,
        DrawingDraft? draft = null, ColorRgba? tint = null, ImageRect? clip = null)
    {
        draft ??= document.Drawing;
        if (_target is null || _target.SizeInPixels.Width != document.Width || _target.SizeInPixels.Height != document.Height)
        {
            Reset();
            _target = new(device, document.Width, document.Height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        }
        if (_draft != draft || _revision != draft.Revision || preview is not null || _hadPreview || _tint != tint || _clip != clip)
        {
            using var session = _target.CreateDrawingSession();
            session.Clear(Colors.Transparent);
            using var layer = clip is { } r ? session.CreateLayer(1, new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height)) : null;
            foreach (var stroke in draft.Strokes) Paint(session, stroke, tint);
            if (preview is not null) Paint(session, preview, tint);
            _draft = draft; _revision = _draft.Revision; _hadPreview = preview is not null; _tint = tint; _clip = clip;
        }
        return _target;
    }

    public byte[] GetMask(CanvasDevice device, ImageDocument document, DrawingDraft? draft = null)
    {
        byte[] pixels = Render(device, document, draft: draft).GetPixelBytes();
        // Win2D renders premultiplied alpha; the Python contract uses straight BGRA8.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            for (int c = 0; c < 3; c++)
                pixels[i + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (pixels[i + c] * 255 + alpha / 2) / alpha);
        }
        return pixels;
    }

    private static void Paint(CanvasDrawingSession session, DrawingStroke stroke, ColorRgba? tint)
    {
        if (stroke.Tool == DrawingTool.Clear) { session.Clear(Colors.Transparent); return; }
        if (stroke.Points.Count == 0) return;
        var c = tint ?? stroke.Color;
        Color color = stroke.Tool == DrawingTool.Eraser ? Colors.Transparent : Color.FromArgb((byte)c.A, (byte)c.R, (byte)c.G, (byte)c.B);
        session.Blend = stroke.Tool == DrawingTool.Eraser ? CanvasBlend.Copy : CanvasBlend.SourceOver;
        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        Vector2 first = stroke.Points[0], last = stroke.Points[^1];
        if (stroke.Tool == DrawingTool.Rectangle)
            session.DrawRectangle(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y), color, stroke.Size, style);
        else if (stroke.Tool == DrawingTool.Circle)
        {
            float side = Math.Min(Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y));
            Vector2 opposite = first + new Vector2(Math.Sign(last.X - first.X), Math.Sign(last.Y - first.Y)) * side;
            session.DrawCircle((first + opposite) / 2, side / 2, color, stroke.Size, style);
        }
        else if (stroke.Points.Count == 1 || Vector2.DistanceSquared(first, last) < 0.001f && stroke.Points.Count == 2)
            session.FillCircle(first, stroke.Size / 2, color);
        else
        {
            using var path = new CanvasPathBuilder(session);
            path.BeginFigure(first);
            if (stroke.Tool is DrawingTool.Brush or DrawingTool.Eraser)
                foreach (var point in stroke.Points.Skip(1)) path.AddLine(point);
            else path.AddLine(last);
            path.EndFigure(CanvasFigureLoop.Open);
            if (stroke.Tool == DrawingTool.Arrow && Vector2.DistanceSquared(first, last) > 0.01f)
            {
                Vector2 direction = Vector2.Normalize(last - first), normal = new(-direction.Y, direction.X);
                float head = Math.Min(Vector2.Distance(first, last) * 0.45f, Math.Max(10, stroke.Size * 3));
                path.BeginFigure(last - direction * head + normal * head * 0.55f);
                path.AddLine(last); path.AddLine(last - direction * head - normal * head * 0.55f);
                path.EndFigure(CanvasFigureLoop.Open);
            }
            using var geometry = CanvasGeometry.CreatePath(path);
            session.DrawGeometry(geometry, color, stroke.Size, style);
        }
        session.Blend = CanvasBlend.SourceOver;
    }
    public void Reset() { _target?.Dispose(); _target = null; _draft = null; _revision = -1; _hadPreview = false; _clip = null; }
    public void Dispose() => Reset();
}
