using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task RunMaskSelectionSmokeAsync(ImageDocument source)
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        static void VerifyMask(ImageMask mask, byte[] full, ImageRect? region, int outside = 0)
        {
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                {
                    int i = y * mask.Width + x;
                    bool inside = region is not { } r || x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom;
                    Check(mask.Values[i] == (inside ? full[i] : outside), "Mask pixels do not respect the active rectangle or exterior choice.");
                }
            Check(mask.Selection == region, "The saved mask lost its generation rectangle.");
            Check(Convert.ToInt32(mask.Generator.Parameters["mask_outside_value"]) == outside, "The saved mask lost its exterior choice.");
        }
        var original = (byte[])source.Pixels.Clone();
        var reference = ViewModel.Images.First(d => d != source && d.Width == source.Width && d.Height == source.Height
            && !ReferenceEquals(d.Pixels, source.Pixels));
        var roi = new ImageRect(11, 7, 31, 23);
        var edge = new ImageRect(source.Width - 1, source.Height - 1, 1, 1);
        foreach (var (id, arguments) in new[]
        {
            ("mask_from_image", new Dictionary<string, object>()),
            ("mask_threshold", new Dictionary<string, object> { ["channel"] = "red", ["lower"] = 40, ["upper"] = 170, ["invert"] = true }),
            ("mask_threshold", new Dictionary<string, object> { ["channel"] = "hsv", ["lower_hsv"] = new[] { 170, 20, 40 }, ["upper_hsv"] = new[] { 10, 255, 255 } })
        })
        {
            SelectRevision(source);
            EditorCanvas.Selection = null;
            InspectorTabs.SelectedIndex = 1;
            BeginNewMask();
            MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>().Single(p => p.Id == id);
            MaskTools.SetPluginArguments(arguments, reset: true);
            SelectToolClicked(this, new RoutedEventArgs());
            Check(InspectorTabs.SelectedIndex == 1 && EditorCanvas.DrawTool is null, "Selecting a mask region left the Masks panel.");
            EditorCanvas.Selection = roi;
            MaskSource.SelectedItem = reference;
            var options = MaskTools.GetOptions(new(0, 0, 0, 255), null, null);
            var full = await _processing.CreateMaskAsync(source,
                new ImageLayer(reference.FileName, reference.Width, reference.Height, reference.Pixels),
                "Full reference", options, null);
            Check(EditorCanvas.Selection == roi && source.Selection == roi && MaskRegionHint.Text.Contains("31 x 23"),
                "Changing mask source or setting a rectangle lost the active selection.");
            Check(MaskTools.RequestApply(), "ROI mask Apply disabled.");
            await _processingTask;
            var created = EditorCanvas.Document!;
            Check(_lastOperationCode is null && created != source && created.Masks.Count == 1, "ROI mask creation failed: " + ViewModel.Status);
            Check(created.Width == source.Width && created.Height == source.Height && created.Pixels.SequenceEqual(original),
                "ROI mask creation cropped or changed the original image.");
            var saved = created.Masks[0];
            var savedValues = (byte[])saved.Values.Clone();
            VerifyMask(saved, full.Values, roi);
            Check(EditorCanvas.Selection == roi, "Publishing the mask lost its rectangle.");

            // Updating must use the new rectangle, not a cached one or the source image's selection.
            EditorCanvas.Selection = edge;
            Check(MaskOutsideSelection.IsEnabled, "The outside-selection choice is unavailable for a rectangle.");
            MaskOutsideSelection.SelectedIndex = 1;
            Check(MaskRegionHint.Text.Contains("white (255)"), "The region hint ignored the exterior choice.");
            MaskTools.RequestApply();
            await _processingTask;
            var updated = EditorCanvas.Document!;
            Check(_lastOperationCode is null && updated != created && updated.Masks[0].Id == saved.Id,
                "ROI update did not update the selected mask.");
            VerifyMask(updated.Masks[0], full.Values, edge, 255);
            Check(saved.Values.SequenceEqual(savedValues) && saved.Selection == roi, "Updating mutated the saved mask history.");
            UndoClicked(this, new RoutedEventArgs());
            Check(EditorCanvas.Document == created, "ROI mask Undo failed.");
            MaskList.SelectedIndex = 0;
            Check(EditorCanvas.Selection == roi && MaskOutsideSelection.SelectedIndex == 0, "Reselecting a mask did not restore its rectangle/exterior.");
            RedoClicked(this, new RoutedEventArgs());
            Check(EditorCanvas.Document == updated, "ROI mask Redo failed.");
            MaskList.SelectedIndex = 0;
            Check(EditorCanvas.Selection == edge && MaskOutsideSelection.SelectedIndex == 1, "Redo lost the updated mask rectangle/exterior.");

            ClearSelectionClicked(this, new RoutedEventArgs());
            Check(MaskRegionHint.Text.Contains("entire image") && !MaskOutsideSelection.IsEnabled,
                "Clearing the selection did not update the mask region hint or disable the exterior choice.");
            MaskTools.RequestApply();
            await _processingTask;
            Check(_lastOperationCode is null && EditorCanvas.Document != updated, "Full-image mask update failed.");
            VerifyMask(EditorCanvas.Document!.Masks[0], full.Values, null, 255);
        }
        Check(source.Pixels.SequenceEqual(original) && source.Masks.Count == 0, "ROI masks changed the original.");
        SelectRevision(source);
        EditorCanvas.Selection = null;
    }
}
