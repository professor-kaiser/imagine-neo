using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private sealed class MaskPaintSession
    {
        public DrawingDraft Draft { get; } = new();
        public ImageLayer Background { get; private set; }
        public ImageDocument Reference { get; private set; }
        public bool BlackPreview { get; set; }
        public float BrushSize { get; set; } = 12;
        public MaskPaintSession(ImageDocument owner, ImageLayer background, MaskPainting? saved)
        {
            Background = background;
            Reference = MakeReference(owner, background);
            if (saved is not null)
            {
                Draft.Load(saved.Strokes);
                BlackPreview = saved.BlackPreview; BrushSize = saved.BrushSize;
            }
        }
        private static ImageDocument MakeReference(ImageDocument owner, ImageLayer background) =>
            new(Guid.NewGuid().ToString("N"), background.Name, "", background.Width, background.Height,
                background.Pixels, owner.Thumbnail, "");
        public void SetBackground(ImageDocument owner, ImageLayer background)
        {
            if (Background == background) return;
            Background = background; Reference = MakeReference(owner, background);
        }
        public MaskPainting Snapshot() => new(Background, Draft.Snapshot(), BlackPreview, BrushSize);
    }

    private readonly Dictionary<string, MaskPaintSession> _maskPaintSessions = new();
    private MaskPaintSession? _activeMaskPaint;
    private bool _maskPaintingActive, _syncMaskPainting, _refreshingMasks;
    private bool IsPaintMask => (MaskMethod?.SelectedItem as PluginDescriptor)?.InputKind == "painting";
    private string MaskPaintKey => EditorCanvas.Document!.Id + "/" + ((MaskList.SelectedItem as ImageMask)?.Id ?? "new");

    private MaskPaintSession GetMaskPaintSession()
    {
        var owner = EditorCanvas.Document ?? throw new InvalidOperationException("Choose an image.");
        string key = MaskPaintKey;
        if (!_maskPaintSessions.TryGetValue(key, out var session))
        {
            var saved = (MaskList.SelectedItem as ImageMask)?.Painting;
            var background = saved?.Background ?? _maskSource
                ?? new ImageLayer(owner.FileName, owner.Width, owner.Height, owner.Pixels);
            session = new MaskPaintSession(owner, background, saved);
            _maskPaintSessions[key] = session;
        }
        return session;
    }

    private void StopMaskPainting()
    {
        if (!_maskPaintingActive) return;
        _maskPaintingActive = false; _activeMaskPaint = null;
        EditorCanvas.SetDrawingPreview(null, null, null);
        EditorCanvas.DrawTool = null;
    }

    private void SyncMaskPainting()
    {
        if (_syncMaskPainting || _refreshingMasks || MaskPaintPanel is null || EditorCanvas is null) return;
        _syncMaskPainting = true;
        try
        {
            MaskPaintPanel.Visibility = IsPaintMask ? Visibility.Visible : Visibility.Collapsed;
            var owner = EditorCanvas.Document;
            bool active = IsPaintMask && InspectorTabs.SelectedIndex == 1 && owner is not null && !owner.Drawing.HasDrawing;
            if (!active) { StopMaskPainting(); return; }
            var session = GetMaskPaintSession();
            if (session.Background.Width != owner!.Width || session.Background.Height != owner.Height)
            {
                StopMaskPainting();
                MaskPaintStatus.Text = "The reference image must match the target dimensions.";
                return;
            }
            if (_activeMaskPaint != session)
            {
                MaskPaintSize.Value = session.BrushSize;
                MaskPaintPreviewColor.SelectedIndex = session.BlackPreview ? 1 : 0;
            }
            _activeMaskPaint = session; _maskPaintingActive = true;
            _maskSource = session.Background;
            _drawingMode = false; DrawButton.IsChecked = false;
            DrawingPanel.Visibility = Visibility.Collapsed; ProcessingTools.Visibility = Visibility.Visible;
            DisableTransformPreview();
            EditorCanvas.PanMode = EditorCanvas.PickMode = false;
            bool selecting = MaskPaintTool.SelectedIndex == 2;
            SelectButton.IsChecked = selecting; PanButton.IsChecked = false;
            EditorCanvas.BrushColor = new(255, 255, 255, 255);
            EditorCanvas.BrushSize = session.BrushSize;
            EditorCanvas.DrawTool = selecting ? null : MaskPaintTool.SelectedIndex == 1 ? DrawingTool.Eraser : DrawingTool.Brush;
            EditorCanvas.SetDrawingPreview(session.Reference, session.Draft,
                session.BlackPreview ? new(0, 0, 0, 255) : new(255, 255, 255, 255));
            MaskPaintClear.IsEnabled = !_busy && session.Draft.HasDrawing;
            MaskPaintStatus.Text = selecting ? "Draw or resize the mask region, then choose Brush or Eraser."
                : "Paint on the canvas. Preview: " + (session.BlackPreview ? "black" : "white")
                    + ". Inside the selection, painted pixels become white (255).";
            ActiveToolLabel.Text = selecting ? "Mask region selection" : "Paint mask";
            ContextHint.Text = "Mask painting - original pixels preserved";
        }
        finally { _syncMaskPainting = false; }
    }

    private void MaskPaintSourceChanged()
    {
        if (!_refreshingMasks && IsPaintMask && EditorCanvas.Document is { } owner && _maskSource is { } background)
            GetMaskPaintSession().SetBackground(owner, background);
    }

    private void MaskPaintSettingsChanged(object sender, SelectionChangedEventArgs e) => UpdateMaskPaintSettings();
    private void MaskPaintSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => UpdateMaskPaintSettings();
    private void UpdateMaskPaintSettings()
    {
        if (_syncMaskPainting || MaskPaintSize is null || !IsPaintMask || EditorCanvas.Document is null) return;
        var session = GetMaskPaintSession();
        if (double.IsFinite(MaskPaintSize.Value)) session.BrushSize = (float)Math.Clamp(MaskPaintSize.Value, 1, 256);
        session.BlackPreview = MaskPaintPreviewColor.SelectedIndex == 1;
        SyncMaskPainting();
    }

    private void MaskPaintClearClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || !_maskPaintingActive || _activeMaskPaint is null) return;
        EditorCanvas.CancelDrawing();
        _activeMaskPaint.Draft.Clear();
        EditorCanvas.RefreshDrawing();
    }

    private void MaskInspectorChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncMaskPainting();
        if (EditorCanvas is not null && UndoButton is not null) UpdateEditingControls();
    }
}
