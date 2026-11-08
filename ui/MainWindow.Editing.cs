using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Models;
using PhotoLab.UI.Services;
using Windows.Storage.Pickers;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private readonly EditHistoryService _history = new();
    private readonly ImageExportService _export = new();
    private bool _drawingMode;

    private void DrawToolClicked(object sender, RoutedEventArgs e)
    {
        StopMaskPainting();
        DisableTransformPreview();
        _drawingMode = true;
        DrawButton.IsChecked = true; SelectButton.IsChecked = PanButton.IsChecked = false;
        EditorCanvas.PanMode = EditorCanvas.PickMode = false;
        DrawingPanel.Visibility = Visibility.Visible; ProcessingTools.Visibility = Visibility.Collapsed;
        SelectionSection.Visibility = SourceSection.Visibility = AnalysisSection.Visibility = Visibility.Collapsed;
        ColorSection.Visibility = Visibility.Visible;
        InspectorTabs.SelectedIndex = 0;
        ConfigureDrawing(); UpdateEditingControls();
    }
    private void ExitDrawingMode()
    {
        StopMaskPainting();
        _drawingMode = false; DrawButton.IsChecked = false; EditorCanvas.DrawTool = null;
        DrawingPanel.Visibility = Visibility.Collapsed; ProcessingTools.Visibility = Visibility.Visible;
        SelectionSection.Visibility = Visibility.Visible;
        UpdateContextSections();
    }
    private void DrawingToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_drawingMode) ConfigureDrawing();
    }
    private void BrushSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (EditorCanvas is not null && double.IsFinite(args.NewValue)) EditorCanvas.BrushSize = (float)Math.Clamp(args.NewValue, 1, 256);
    }
    private void ConfigureDrawing()
    {
        if (DrawingToolPicker.SelectedItem is not ComboBoxItem item) return;
        EditorCanvas.DrawTool = Enum.Parse<DrawingTool>((string)item.Tag);
        EditorCanvas.BrushSize = double.IsFinite(BrushSizeInput.Value) ? (float)Math.Clamp(BrushSizeInput.Value, 1, 256) : 8;
        UpdateBrushColor();
        ActiveToolLabel.Text = "Draw - " + item.Content; ActiveToolIcon.Glyph = "\uED63";
        ColorSection.Visibility = EditorCanvas.DrawTool == DrawingTool.Eraser ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateBrushColor()
    {
        var c = WorkingColor.SelectedColor;
        EditorCanvas.BrushColor = _maskPaintingActive ? new(255, 255, 255, 255) : new(c.R, c.G, c.B, c.A);
    }
    private void UpdateEditingControls()
    {
        UpdateLayerControls();
        if (UndoButton is null || HistoryUndoButton is null) return;
        var image = EditorCanvas.Document;
        var activeDraft = _maskPaintingActive ? _activeMaskPaint?.Draft : image?.Drawing;
        bool undo = !_busy && image is not null && (activeDraft?.CanUndo == true || _history.Previous(image) is not null);
        bool redo = !_busy && image is not null && (activeDraft?.CanRedo == true
            || (activeDraft?.CanUndo != true && (_maskPaintingActive || !image.Drawing.HasDrawing)) && _history.Next(image) is not null);
        UndoButton.IsEnabled = UndoMenu.IsEnabled = HistoryUndoButton.IsEnabled = undo;
        RedoButton.IsEnabled = RedoMenu.IsEnabled = HistoryRedoButton.IsEnabled = redo;
        SaveButton.IsEnabled = SaveMenu.IsEnabled = !_busy && image is not null;
        ApplyDrawingButton.IsEnabled = ClearDrawingButton.IsEnabled = !_busy && image?.Drawing.HasDrawing == true;
        DrawingStatus.Text = image?.Drawing.HasDrawing == true ? "Pending drawing - apply to create a new version." : "No pending drawing";
        HistoryStatus.Text = image is null ? "Choose an image to get started."
            : image.Drawing.CanUndo || image.Drawing.CanRedo ? "Drawing history - " + image.FileName
            : "Image versions - " + image.FileName;
    }
    private void UndoClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || EditorCanvas.Document is not { } image) return;
        EditorCanvas.CancelDrawing();
        var draft = _maskPaintingActive ? _activeMaskPaint!.Draft : image.Drawing;
        if (draft.CanUndo) { draft.Undo(); EditorCanvas.RefreshDrawing(); ViewModel.Status = "Drawing step undone."; }
        else if (_history.Undo(image) is { } previous) { SelectRevision(previous); ViewModel.Status = "Undone: previous image version."; }
        UpdateEditingControls();
    }
    private void RedoClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || EditorCanvas.Document is not { } image) return;
        EditorCanvas.CancelDrawing();
        var draft = _maskPaintingActive ? _activeMaskPaint!.Draft : image.Drawing;
        if (draft.CanRedo) { draft.Redo(); EditorCanvas.RefreshDrawing(); ViewModel.Status = "Drawing step restored."; }
        else if (!image.Drawing.HasDrawing && _history.Redo(image) is { } next) { SelectRevision(next); ViewModel.Status = "Redone: image version restored."; }
        UpdateEditingControls();
    }
    private void SelectRevision(ImageDocument image)
    {
        float zoom = EditorCanvas.Zoom;
        ImageList.SelectedItem = image; ViewModel.SelectedImage = image; EditorCanvas.SetZoom(zoom);
    }
    private void ClearDrawingClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || EditorCanvas.Document is not { } image) return;
        EditorCanvas.CancelDrawing(); _history.DiscardRedo(image); image.Drawing.Clear(); EditorCanvas.RefreshDrawing();
        ViewModel.Status = "Drawing cleared. Undo restores it.";
    }
    private async void ApplyDrawingClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || EditorCanvas.Document?.Drawing.HasDrawing != true) return;
        _processingTask = ApplyOperationAsync(true);
        await _processingTask;
    }
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || EditorCanvas.Document is not { } image) return;
        SetBusy(true);
        try
        {
            // Export never implicitly commits a pending drawing.
            if (image.Drawing.HasDrawing || EditorCanvas.TransformMode != TransformMode.None && EditorCanvas.CurrentTransform.HasChanges)
            {
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Pending preview",
                    Content = "Apply the drawing or transformation before saving to include it. You can also save the current applied image.",
                    PrimaryButtonText = "Save applied image", CloseButtonText = "Back to editing", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(image.FileName), DefaultFileExtension = ".png" };
            picker.FileTypeChoices.Add("PNG (transparency)", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG (white background)", new List<string> { ".jpg", ".jpeg" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await _export.SaveAsync(image, file);
            ViewModel.Status = "Image saved: " + file.Path;
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }
}
