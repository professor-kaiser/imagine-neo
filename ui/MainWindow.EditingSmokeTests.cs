using System.IO;
using System.Numerics;
using Microsoft.UI.Xaml;
using PhotoLab.UI.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task RunEditingSmokeAsync(string root, bool fixtures, ImageDocument source, ImageDocument applied)
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        var original = (byte[])source.Pixels.Clone();
        int count = ViewModel.Images.Count;
        SelectRevision(applied);
        UndoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == source, "Undo did not restore the parent version.");
        RedoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == applied, "Redo did not restore the applied version.");
        SelectRevision(source);
        DrawToolClicked(this, new RoutedEventArgs());
        Check(DrawingPanel.Visibility == Visibility.Visible && ProcessingTools.Visibility == Visibility.Collapsed, "Drawing inspector missing.");
        BrushSizeInput.Value = 12;
        SetWorkingColor(new(240, 80, 20, 128));
        Check(EditorCanvas.BrushSize == 12 && EditorCanvas.BrushColor == new PhotoLab.UI.Contracts.ColorRgba(240, 80, 20, 128), "Drawing settings were not propagated.");
        source.Drawing.Add(new(DrawingTool.Brush, 12, EditorCanvas.BrushColor, [new(10, 20), new(90, 20)]));
        EditorCanvas.RefreshDrawing();
        byte[] painted = EditorCanvas.GetDrawingMask();
        int middle = (20 * source.Width + 50) * 4;
        Check(painted[middle + 3] == 128 && Math.Abs(painted[middle + 2] - 240) <= 1 && Math.Abs(painted[middle] - 20) <= 1, "Mask must carry straight BGRA color and alpha.");
        EditorCanvas.SetZoom(3);
        Check(painted.SequenceEqual(EditorCanvas.GetDrawingMask()), "Zoom changed native-resolution mask pixels.");
        source.Drawing.Add(new(DrawingTool.Eraser, 6, new(0, 0, 0, 255), [new(50, 10), new(50, 30)]));
        EditorCanvas.RefreshDrawing();
        Check(EditorCanvas.GetDrawingMask()[middle + 3] == 0, "Eraser did not clear mask alpha.");
        Check(source.Pixels.SequenceEqual(original), "Drawing changed source pixels before Apply.");
        UndoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.GetDrawingMask().SequenceEqual(painted), "Undo eraser failed.");
        RedoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.GetDrawingMask()[middle + 3] == 0, "Redo eraser failed.");
        ClearDrawingClicked(this, new RoutedEventArgs());
        Check(!EditorCanvas.GetDrawingMask().Any(b => b != 0), "Clear left drawing pixels behind.");
        UndoClicked(this, new RoutedEventArgs());
        Check(source.Drawing.HasDrawing, "Clear drawing could not be undone.");
        UndoClicked(this, new RoutedEventArgs());
        source.Drawing.Add(new(DrawingTool.Brush, 2, new(0, 255, 0, 255), [new(5, 5)]));
        Check(!source.Drawing.CanRedo, "New drawing failed to discard the redo branch.");
        var draft = EditorCanvas.GetDrawingMask();
        SelectRevision(applied); SelectRevision(source);
        Check(draft.SequenceEqual(EditorCanvas.GetDrawingMask()), "Changing images lost the pending drawing.");
        var stored = await _client.SendAsync(new PhotoLab.UI.Contracts.CommandMessage { MessageType = "list_images" });
        Check(stored.Images!.Count == count, "A stroke was sent to Python before Apply.");
        source.Drawing.Reset();
        foreach (var tool in new[] { DrawingTool.Rectangle, DrawingTool.Circle, DrawingTool.Line, DrawingTool.Arrow })
        {
            DrawingToolPicker.SelectedIndex = (int)tool;
            Check(EditorCanvas.DrawTool == tool, "Drawing tool picker is not wired.");
            foreach (bool reverse in new[] { false, true })
            {
                source.Drawing.Reset();
                Vector2 start = reverse ? new(80, 70) : new(20, 10), end = reverse ? new(20, 10) : new(80, 70);
                source.Drawing.Add(new(tool, 4, new(0, 255, 0, 255), [start, end]));
                var mask = EditorCanvas.GetDrawingMask();
                Check(mask.Any(b => b != 0) && mask[3] == 0, "Shape rasterization is empty or extends outside its bounds.");
                if (tool is DrawingTool.Rectangle or DrawingTool.Circle)
                    Check(mask[(40 * source.Width + 50) * 4 + 3] == 0, "Outlined shape unexpectedly filled its center.");
            }
        }
        source.Drawing.Add(new(DrawingTool.Brush, 6, new(240, 80, 20, 128), [new(8, 80), new(25, 80)]));
        EditorCanvas.RefreshDrawing();
        var beforeApply = EditorCanvas.GetDrawingMask();
        ApplyDrawingClicked(this, new RoutedEventArgs());
        await _processingTask;
        Check(_lastOperationCode is null && ViewModel.Images.Count == count + 1, "Apply drawing did not publish a result.");
        var result = EditorCanvas.Document!;
        if (!fixtures) VerifyDrawingResult(source, result, beforeApply);
        Check(!source.Drawing.CanUndo && !source.Drawing.HasDrawing, "Successful apply did not clear the draft.");
        UndoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == source, "Applied drawing cannot be undone.");
        RedoClicked(this, new RoutedEventArgs());
        Check(EditorCanvas.Document == result, "Applied drawing cannot be redone.");
        await _documents.RemoveAsync(result); _history.Forget(result);
        SelectRevision(source);
        source.Drawing.Reset(); EditorCanvas.RefreshDrawing();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.Combine(root, "artifacts"));
        foreach (string extension in new[] { ".png", ".jpg" })
        {
            var file = await folder.CreateFileAsync("editing-export-test" + extension, CreationCollisionOption.ReplaceExisting);
            await _export.SaveAsync(source, file);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            byte[] roundTrip = data.DetachPixelData();
            Check(decoder.PixelWidth == source.Width && decoder.PixelHeight == source.Height, "Export changed image dimensions.");
            if (extension == ".png") Check(roundTrip.SequenceEqual(original), "PNG export changed source colors or transparency.");
            else
            {
                Check(Enumerable.Range(0, roundTrip.Length / 4).All(i => roundTrip[i * 4 + 3] == 255), "JPEG export is not opaque.");
                Check(roundTrip[0] >= 240 && roundTrip[1] >= 240 && roundTrip[2] >= 240, "JPEG transparency was not flattened on white.");
            }
        }
        Check(source.Pixels.SequenceEqual(original), "Export mutated the image.");
        SelectRevision(applied); ExitDrawingMode(); UpdateEditingControls();
        Check(ViewModel.Images.Count == count, "Editing probes changed the library count.");
    }
    private static void VerifyDrawingResult(ImageDocument source, ImageDocument result, byte[] mask)
    {
        bool changed = false;
        for (int i = 0; i < mask.Length; i += 4)
        {
            if (mask[i + 3] == 0)
            {
                if (!result.Pixels.AsSpan(i, 4).SequenceEqual(source.Pixels.AsSpan(i, 4)))
                    throw new InvalidDataException("Drawing changed a pixel outside the mask.");
                continue;
            }
            double frontAlpha = mask[i + 3] / 255d, backAlpha = source.Pixels[i + 3] / 255d;
            double alpha = frontAlpha + backAlpha * (1 - frontAlpha);
            for (int channel = 0; channel < 4; channel++)
            {
                double expected = channel == 3 ? 255 * alpha
                    : (mask[i + channel] * frontAlpha + source.Pixels[i + channel] * backAlpha * (1 - frontAlpha)) / alpha;
                if (Math.Abs(result.Pixels[i + channel] - expected) > 0.500001)
                    throw new InvalidDataException("Drawing did not composite its color and alpha correctly.");
            }
            changed |= !result.Pixels.AsSpan(i, 4).SequenceEqual(source.Pixels.AsSpan(i, 4));
        }
        if (!changed) throw new InvalidDataException("Applying the drawing did not change the image.");
    }

}
