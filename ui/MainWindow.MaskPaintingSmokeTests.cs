using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task RunPaintMaskSmokeAsync(ImageDocument source)
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        byte[] original = (byte[])source.Pixels.Clone();
        SelectRevision(source);
        EditorCanvas.Selection = null;
        InspectorTabs.SelectedIndex = 1;
        BeginNewMask();
        MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>().Single(p => p.InputKind == "painting");
        Check(_maskPaintingActive && EditorCanvas.DrawTool == DrawingTool.Brush, "Paint mask mode did not activate the canvas.");
        MaskPaintSize.Value = 12;
        MaskPaintPreviewColor.SelectedIndex = 1;
        var draft = _activeMaskPaint!.Draft;
        draft.Add(new(DrawingTool.Brush, EditorCanvas.BrushSize, EditorCanvas.BrushColor, [new(10, 20), new(90, 20)]));
        EditorCanvas.RefreshDrawing();
        byte[] white = EditorCanvas.GetDrawingMask();
        byte[] blackPreview = EditorCanvas.GetDrawingPreviewPixels();
        int center = (20 * source.Width + 50) * 4;
        Check(white[center] == 255 && white[center + 1] == 255 && white[center + 2] == 255 && white[center + 3] == 255,
            "Painting with a black preview did not store white coverage.");
        Check(blackPreview[center] == 0 && blackPreview[center + 3] == 255, "Black preview was not rendered.");
        MaskPaintPreviewColor.SelectedIndex = 0;
        Check(white.SequenceEqual(EditorCanvas.GetDrawingMask()) && EditorCanvas.GetDrawingPreviewPixels()[center] == 255,
            "Changing preview color changed mask values.");
        Check(source.Pixels.SequenceEqual(original) && !source.Drawing.HasDrawing, "Mask painting touched the original or its drawing draft.");
        MaskPaintTool.SelectedIndex = 1;
        Check(EditorCanvas.DrawTool == DrawingTool.Eraser, "Mask eraser is not wired.");
        draft.Add(new(DrawingTool.Eraser, 6, new(255, 255, 255, 255), [new(50, 10), new(50, 30)]));
        EditorCanvas.RefreshDrawing();
        Check(EditorCanvas.GetDrawingMask()[center + 3] == 0, "Mask eraser left painted coverage.");
        UndoClicked(this, new RoutedEventArgs());
        Check(white.SequenceEqual(EditorCanvas.GetDrawingMask()), "Mask stroke undo failed.");
        RedoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.GetDrawingMask()[center + 3] == 0, "Mask stroke redo failed.");
        UndoClicked(this, new RoutedEventArgs());
        MaskPaintTool.SelectedIndex = 0;
        var other = ViewModel.Images.First(image => image != source);
        SelectRevision(other);
        SelectRevision(source);
        Check(white.SequenceEqual(EditorCanvas.GetDrawingMask()), "Changing images lost the mask painting.");
        Check(_activeMaskPaint!.Draft == draft, "Another document replaced the saved paint draft.");
        var paintRegion = new ImageRect(30, 15, 40, 12);
        SelectToolClicked(this, new RoutedEventArgs());
        Check(_maskPaintingActive && EditorCanvas.DrawTool is null && InspectorTabs.SelectedIndex == 1,
            "Painting mode cannot select a rectangle without losing the draft.");
        EditorCanvas.Selection = paintRegion;
        MaskPaintTool.SelectedIndex = 0;
        Check(EditorCanvas.DrawTool == DrawingTool.Brush && _activeMaskPaint!.Draft == draft,
            "Returning to the mask brush lost the selection or draft.");
        byte[] limitedPreview = EditorCanvas.GetDrawingPreviewPixels();
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                if (x < paintRegion.X || x >= paintRegion.Right || y < paintRegion.Y || y >= paintRegion.Bottom)
                    Check(limitedPreview[(y * source.Width + x) * 4 + 3] == 0, "Paint preview leaked outside selection.");
        Check(white.SequenceEqual(EditorCanvas.GetDrawingMask()), "ROI preview destroyed the reusable paint strokes.");
        MaskName.Text = "Painted mask";
        MaskOutsideSelection.SelectedIndex = 1;
        MaskPaintPreviewColor.SelectedIndex = 1;
        Check(MaskTools.RequestApply(), "Paint mask Apply disabled.");
        await _processingTask;
        var result = EditorCanvas.Document!;
        Check(_lastOperationCode is null && result != source && result.Masks.Count == 1, "Painted mask was not published: " + ViewModel.Status);
        var mask = result.Masks[0];
        Check(result.Pixels.SequenceEqual(original) && mask.Painting is not null && mask.Painting.BlackPreview,
            "Painted mask lost its source, preview setting or original pixels.");
        Check(mask.Selection == paintRegion, "Painted mask lost its rectangle.");
        for (int i = 0; i < mask.Values.Length; i++)
        {
            int x = i % source.Width, y = i / source.Width;
            bool inside = x >= paintRegion.X && x < paintRegion.Right && y >= paintRegion.Y && y < paintRegion.Bottom;
            Check(mask.Values[i] == (!inside || white[i * 4 + 3] > 0 ? 255 : 0), "Painted mask ignored its white exterior.");
        }
        Check(!_activeMaskPaint!.Draft.CanUndo, "Saved strokes must be the editing baseline.");
        UndoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == source, "Painted mask Undo did not restore the source version.");
        RedoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == result, "Painted mask Redo failed.");
        MaskList.SelectedIndex = 0;
        Check(MaskOutsideSelection.SelectedIndex == 1, "The painted mask lost its white exterior setting.");
        _activeMaskPaint!.Draft.Add(new(DrawingTool.Brush, 8, EditorCanvas.BrushColor, [new(20, 45), new(70, 45)]));
        EditorCanvas.RefreshDrawing();
        byte[] extended = EditorCanvas.GetDrawingMask();
        var expandedRegion = new ImageRect(15, 15, 80, 40);
        MaskPaintTool.SelectedIndex = 2;
        EditorCanvas.Selection = expandedRegion;
        MaskPaintTool.SelectedIndex = 0;
        MaskOutsideSelection.SelectedIndex = 0;
        MaskTools.RequestApply();
        await _processingTask;
        var edited = EditorCanvas.Document!;
        Check(_lastOperationCode is null && edited != result && edited.Masks.Count == 1 && edited.Masks[0].Id == mask.Id,
            "Editing a painted mask did not update the same mask.");
        Check(edited.Masks[0].Selection == expandedRegion && mask.Selection == paintRegion,
            "Editing the paint rectangle mutated the previous mask.");
        for (int i = 0; i < edited.Masks[0].Values.Length; i++)
        {
            int x = i % source.Width, y = i / source.Width;
            bool inside = x >= expandedRegion.X && x < expandedRegion.Right && y >= expandedRegion.Y && y < expandedRegion.Bottom;
            Check(edited.Masks[0].Values[i] == (inside && extended[i * 4 + 3] > 0 ? 255 : 0), "Updated painting ignored its new rectangle.");
        }
        Check(mask.Painting!.Strokes.Count == 1 && edited.Masks[0].Painting!.Strokes.Count == 2,
            "Editing mutated saved stroke history.");
        MaskPaintClearClicked(this, new RoutedEventArgs());
        Check(!EditorCanvas.GetDrawingMask().Any(v => v != 0), "Clear paint left coverage.");
        UndoClicked(this, new RoutedEventArgs());
        Check(extended.SequenceEqual(EditorCanvas.GetDrawingMask()), "Clear painting could not be undone.");
        Check(source.Pixels.SequenceEqual(original) && !source.Drawing.HasDrawing, "Paint workflow modified the original.");
        SelectRevision(source);
        MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>().First(p => p.InputKind == "image");
    }
}
