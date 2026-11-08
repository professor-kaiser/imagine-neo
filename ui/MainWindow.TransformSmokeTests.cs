using System.IO;
using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, string>> RunTransformSmokeAsync(bool fixtures, ImageDocument source, ImageDocument other)
    {
        static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        var outcomes = new Dictionary<string, string>();
        var original = (byte[])source.Pixels.Clone();
        int count = ViewModel.Images.Count;
        foreach (string operation in new[] { "rotate_image", "scale_image", "rotate_scale_image" })
        {
            SelectRevision(source);
            source.TransformDraft = ImageTransform.Identity;
            EditorCanvas.Selection = new(2, 3, 10, 8); source.Selection = EditorCanvas.Selection;
            ProcessingTools.SelectOperation(operation);
            Check(Toolbox.SelectedOperation == operation && EditorCanvas.TransformMode == ImageTransform.ModeFor(operation), "Transform toolbox and canvas are out of sync.");
            Check(SelectionSection.Visibility == Visibility.Collapsed, "Transform should target the whole image.");
            ProcessingTools.VerifyTransformPresets();
            ProcessingTools.SetSmokeTransformInputs(33.5, 150);
            Check(source.TransformDraft == new ImageTransform(33.5, 1.5), "Inputs did not update the document preview.");
            var expected = source.TransformDraft.ForMode(EditorCanvas.TransformMode);
            Check(EditorCanvas.CurrentTransform == expected, "Transform modes must use only their relevant parameters.");
            EditorCanvas.Fit();
            await Task.Delay(150);
            Check(EditorCanvas.RenderedImageId == source.Id && EditorCanvas.LastRenderError is null, "Transform preview rendering failed.");
            EditorCanvas.SetZoom(2);
            Check(EditorCanvas.CurrentTransform == expected && source.Pixels.SequenceEqual(original), "Preview changed original pixels or zoom changed transform values.");
            SelectRevision(other); SelectRevision(source);
            Check(ProcessingTools.GetTransformValues() == new ImageTransform(33.5, 1.5), "Switching images lost transform settings.");
            var before = await _client.SendAsync(new CommandMessage { MessageType = "list_images" });
            Check(before.Images!.Count == count, "Preview was applied automatically.");
            var size = expected.ValidatedOutputSize(source.Width, source.Height);
            Check(ProcessingTools.RequestApply(), "Transform Apply is disabled.");
            await _processingTask;
            outcomes[operation] = _lastOperationCode ?? "ok";
            if (fixtures || _lastOperationCode is null)
            {
                Check(_lastOperationCode is null && ViewModel.Images.Count == count + 1, "Transform result was not published.");
                var result = EditorCanvas.Document!;
                Check((result.Width, result.Height) == size && result.Selection is null, "Transform result dimensions or selection are wrong.");
                if (fixtures) Check(result.Pixels.Take(4).SequenceEqual(new byte[] {31,47,59,127}), "Transform result pixels were not transferred.");
                Check(EditorCanvas.CurrentTransform == ImageTransform.Identity, "Applied transform was previewed twice.");
                UndoClicked(this, new RoutedEventArgs());
                Check(EditorCanvas.Document == source && EditorCanvas.CurrentTransform == ImageTransform.Identity, "Undo did not restore the source of the transform.");
                RedoClicked(this, new RoutedEventArgs());
                Check(EditorCanvas.Document == result, "Redo did not restore the resized version.");
                await _documents.RemoveAsync(result); _history.Forget(result);
                SelectRevision(source);
            }
            else
            {
                Check(_lastOperationCode == "not_implemented" && ViewModel.Images.Count == count, "Transform placeholder should not create a version.");
                Check(EditorCanvas.Document == source && EditorCanvas.CurrentTransform == expected, "Unimplemented Apply lost its preview.");
                Check(ProcessingTools.GetTransformValues() == new ImageTransform(33.5, 1.5), "Unimplemented Apply lost its inputs.");
            }
            Check(source.Pixels.SequenceEqual(original), "Transform changed the original.");
        }
        source.TransformDraft = ImageTransform.Identity;
        SelectRevision(other); ProcessingTools.SelectOperation("to_grayscale");
        Check(EditorCanvas.TransformMode == TransformMode.None, "Leaving a transform kept the preview active.");
        return outcomes;
    }
}
