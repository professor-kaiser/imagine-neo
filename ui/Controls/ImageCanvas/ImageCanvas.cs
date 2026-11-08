using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.UI;
using PhotoLab.UI.Controls.SelectionOverlay;
using PhotoLab.UI.Models;
using PhotoLab.UI.Rendering;

namespace PhotoLab.UI.Controls.ImageCanvas;

public sealed partial class ImageCanvas : UserControl, IDisposable
{
    private readonly CanvasControl _canvas = new();
    private readonly IImageRenderer _renderer = new Win2DImageRenderer();
    private readonly SelectionController _selection = new();
    private readonly DrawingRenderer _drawing = new();
    private DrawingStroke? _stroke;
    private ImageDocument? _drawingReference;
    private DrawingDraft? _drawingOverride;
    private PhotoLab.UI.Contracts.ColorRgba? _drawingTint;
    public DrawingDraft? ActiveDrawing => _drawingOverride ?? _document?.Drawing;
    private DrawingTool? _drawingTool;
    public DrawingTool? DrawTool { get => _drawingTool; set { if (_drawingTool == value) return; CancelDrawing(); _drawingTool = value; } }
    public float BrushSize { get; set; } = 8;
    public PhotoLab.UI.Contracts.ColorRgba BrushColor { get; set; } = new(255, 255, 255, 255);
    public event EventHandler? DrawingChanged;
    public event EventHandler? DrawingCommitted;
    public void RefreshDrawing() { _canvas.Invalidate(); DrawingChanged?.Invoke(this, EventArgs.Empty); }
    public byte[] GetDrawingMask() => _drawing.GetMask(_canvas.Device, _document ?? throw new InvalidOperationException("Choose an image first."), ActiveDrawing);
    public void SetDrawingPreview(ImageDocument? reference, DrawingDraft? draft, PhotoLab.UI.Contracts.ColorRgba? tint)
    {
        if (_drawingReference == reference && _drawingOverride == draft && _drawingTint == tint) return;
        if (reference is not null && (_document is null || reference.Width != _document.Width || reference.Height != _document.Height))
            throw new ArgumentException("Drawing reference dimensions must match the image.");
        CancelDrawing();
        if (_drawingReference != reference) _renderer.Reset();
        _drawingReference = reference; _drawingOverride = draft; _drawingTint = tint;
        _canvas.Invalidate();
    }
    internal byte[] GetDrawingPreviewPixels() => _drawing.Render(_canvas.Device, _document!,
        draft: ActiveDrawing, tint: _drawingTint, clip: _drawingOverride is not null ? Selection : null).GetPixelBytes();

    public void CancelDrawing() { _transformGesture.End(); _stroke = null; _dragging = false; _panning = false; _canvas.Invalidate(); }
    private Vector2 ClampPoint(Vector2 point) => Vector2.Clamp(point, Vector2.Zero, new(_document!.Width, _document.Height));
    private readonly ViewportTransform _viewport = new();
    private ImageDocument? _document;
    private Vector2 _last;
    private bool _dragging, _panning, _needsFit;
    public event EventHandler? ViewChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler<string>? RenderFailed;
    public float Zoom => _viewport.Zoom;
    public string? RenderedImageId { get; private set; }
    public string? LastRenderError { get; private set; }
    public Vector2 CursorPixel { get; private set; }
    public bool PanMode { get; set; }
    public bool PickMode { get; set; }
    public event EventHandler<Vector2>? PixelPicked;
    public bool FixedSelectionSize { get => _selection.FixedSize; set => _selection.FixedSize = value; }
    public ImageRect? Selection
    {
        get => _selection.Selection;
        set
        {
            if (_selection.Selection == value) return;
            _selection.Selection = value;
            if (!FixedSelectionSize && _document is not null) _document.Selection = value;
            _canvas.Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public ImageDocument? Document
    {
        get => _document;
        set { CancelDrawing(); _drawingReference = null; _drawingOverride = null; _drawingTint = null; _document = value; RenderedImageId = null; LastRenderError = null; _selection.Selection = FixedSelectionSize ? null : value?.Selection; _renderer.Reset(); _needsFit = true; _canvas.Invalidate(); }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ImageCanvasPeer(this);
    private sealed class ImageCanvasPeer(ImageCanvas owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ImageCanvas);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsControlElementCore() => true;
    }

    public ImageCanvas()
    {
        Content = _canvas;
        AllowDrop = true;
        _canvas.AllowDrop = true;
        _canvas.ClearColor = Color.FromArgb(255, 25, 27, 29);
        _canvas.Draw += Draw;
        _canvas.CreateResources += (_, _) => { _renderer.Reset(); _drawing.Reset(); _canvas.Invalidate(); };
        _canvas.PointerPressed += Pressed;
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += Released;
        _canvas.PointerCanceled += (_, _) => CancelDrawing();
        _canvas.PointerCaptureLost += (_, _) => CancelDrawing();
        _canvas.PointerWheelChanged += Wheel;
        SizeChanged += (_, _) => { if (_needsFit) Fit(); _canvas.Invalidate(); };
    }

    public void Fit()
    {
        if (_document is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (width, height) = CurrentTransform.OutputSize(_document.Width, _document.Height);
        int margin = _transformMode == TransformMode.None ? 0 : 40;
        _viewport.Fit((float)ActualWidth - margin, (float)ActualHeight - margin, width, height);
        _viewport.Pan(new Vector2(margin / 2f, margin / 2f) + new Vector2((width - _document.Width) / 2f, (height - _document.Height) / 2f) * Zoom);
        _needsFit = false; _canvas.Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetZoom(float zoom)
    {
        _needsFit = false;
        _viewport.ZoomAt(new((float)ActualWidth / 2, (float)ActualHeight / 2), zoom / _viewport.Zoom);
        _canvas.Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    private Vector2 Position(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        return new((float)p.X, (float)p.Y);
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null) return;
        var properties = e.GetCurrentPoint(_canvas).Properties;
        if (PickMode && !PanMode && properties.IsLeftButtonPressed)
        {
            Vector2 point = _viewport.ToImage(Position(e));
            if (point.X >= 0 && point.Y >= 0 && point.X < _document.Width && point.Y < _document.Height)
                PixelPicked?.Invoke(this, new(MathF.Floor(point.X), MathF.Floor(point.Y)));
            e.Handled = true;
            return;
        }
        _panning = PanMode || properties.IsMiddleButtonPressed || properties.IsRightButtonPressed;
        _dragging = _panning || properties.IsLeftButtonPressed;
        if (!_dragging) return;
        _last = Position(e);
        if (!_panning && _transformMode != TransformMode.None)
        {
            if (!_transformGesture.Begin(_viewport.ToImage(_last), _document.Width, _document.Height, Zoom, _transformMode, CurrentTransform))
            { _dragging = false; e.Handled = true; return; }
        }
        else if (!_panning && DrawTool is { } tool)
        {
            Vector2 point = _viewport.ToImage(_last);
            if (point.X < 0 || point.Y < 0 || point.X >= _document.Width || point.Y >= _document.Height)
            { _dragging = false; return; }
            _stroke = new(tool, BrushSize, BrushColor, new List<Vector2> { point });
            _canvas.Invalidate();
        }
        else if (!_panning) _selection.Begin(_viewport.ToImage(_last), Zoom);
        _canvas.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        Vector2 position = Position(e);
        CursorPixel = _viewport.ToImage(position);
        if (_dragging && _document is not null)
        {
            if (_panning) _viewport.Pan(position - _last);
            else if (_transformGesture.IsDragging) UpdateTransformGesture(CursorPixel);
            else if (_stroke is not null) ExtendStroke(CursorPixel);
            else
            {
                _selection.Update(CursorPixel, _document.Width, _document.Height);
                if (!FixedSelectionSize) _document.Selection = Selection;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            _last = position; _canvas.Invalidate();
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_transformGesture.IsDragging) { UpdateTransformGesture(_viewport.ToImage(Position(e))); _transformGesture.End(); }
        if (_stroke is not null && _document is not null)
        {
            ExtendStroke(_viewport.ToImage(Position(e)));
            ActiveDrawing!.Add(_stroke);
            DrawingCommitted?.Invoke(this, EventArgs.Empty);
            _stroke = null;
            RefreshDrawing();
        }
        _dragging = false; _panning = false; _canvas.ReleasePointerCapture(e.Pointer);
    }
    private void ExtendStroke(Vector2 point)
    {
        if (_stroke?.Points is not List<Vector2> points) return;
        point = ClampPoint(point);
        if (_stroke.Tool is DrawingTool.Brush or DrawingTool.Eraser)
        {
            if (Vector2.DistanceSquared(points[^1], point) > 0.04f) points.Add(point);
        }
        else if (points.Count == 1) points.Add(point);
        else points[1] = point;
    }
    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        _viewport.ZoomAt(Position(e), MathF.Pow(1.15f, e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta / 120f));
        _needsFit = false; _canvas.Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty); e.Handled = true;
    }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var draw = args.DrawingSession;
        if (_document is null) return;
        if (_needsFit) Fit();
        draw.Transform = _viewport.Matrix;
        var transform = CurrentTransform;
        var (width, height) = transform.OutputSize(_document.Width, _document.Height);
        DrawBackground(draw, (_document.Width - width) / 2f, (_document.Height - height) / 2f, width, height);
        draw.Transform = transform.Matrix(_document.Width, _document.Height) * _viewport.Matrix;
        try { _renderer.Draw(draw, sender.Device, _drawingReference ?? _document); RenderedImageId = _document.Id; }
        catch (Exception error) { LastRenderError = error.Message; RenderFailed?.Invoke(this, error.Message); }
        try
        {
            if (ActiveDrawing?.HasDrawing == true || ActiveDrawing?.CanUndo == true || _stroke is not null)
                draw.DrawImage(_drawing.Render(sender.Device, _document, _stroke, ActiveDrawing, _drawingTint,
                    clip: _drawingOverride is not null ? Selection : null));
        }
        catch (Exception error) { LastRenderError = error.Message; RenderFailed?.Invoke(this, error.Message); }
        draw.Transform = _viewport.Matrix;
        DrawTransformHandles(draw);
        if (_transformMode == TransformMode.None && (DrawTool is null || _drawingOverride is not null) && Selection is { } region)
        {
            if (DrawTool is null) draw.FillRectangle(region.X, region.Y, region.Width, region.Height, Color.FromArgb(28, 98, 190, 255));
            draw.DrawRectangle(region.X, region.Y, region.Width, region.Height, Colors.Black, 3 / Zoom);
            draw.DrawRectangle(region.X, region.Y, region.Width, region.Height, Color.FromArgb(255, 111, 200, 255), 1 / Zoom);
            if (!FixedSelectionSize && DrawTool is null)
                foreach (var p in new[] { new Vector2(region.X, region.Y), new(region.Right, region.Y), new(region.Right, region.Bottom), new(region.X, region.Bottom),
                    new(region.X + region.Width / 2f, region.Y), new(region.X + region.Width / 2f, region.Bottom), new(region.X, region.Y + region.Height / 2f), new(region.Right, region.Y + region.Height / 2f) })
                    draw.FillRectangle(p.X - 3 / Zoom, p.Y - 3 / Zoom, 6 / Zoom, 6 / Zoom, Colors.White);
        }
        draw.Transform = Matrix3x2.Identity;
    }
    public void Dispose() { _drawing.Dispose(); _renderer.Dispose(); _canvas.RemoveFromVisualTree(); }
}
