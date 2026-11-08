using PhotoLab.UI.Models;
using Microsoft.UI.Xaml;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private void ConfigureTransformTool()
    {
        var mode = ImageTransform.ModeFor(ProcessingTools.SelectedOperation);
        EditorCanvas.TransformMode = mode;
        SyncTransformDocument();
    }
    private void SyncTransformDocument()
    {
        var image = EditorCanvas.Document;
        ProcessingTools.SetImageDimensions(image?.Width ?? 0, image?.Height ?? 0);
        ProcessingTools.SetTransformValues(image?.TransformDraft ?? ImageTransform.Identity);
    }
    private void TransformParametersChanged(object? sender, EventArgs e)
    {
        if (_busy || _drawingMode || !ProcessingTools.IsTransform || SelectButton.IsChecked == true || PanButton.IsChecked == true) return;
        EditorCanvas.SetTransformValues(ProcessingTools.GetTransformValues());
        UpdateTransformContext();
    }
    private void CanvasTransformChanged(object? sender, EventArgs e)
    {
        if (EditorCanvas.Document is { } image) ProcessingTools.SetTransformValues(image.TransformDraft);
        UpdateTransformContext();
    }
    private void UpdateTransformContext()
    {
        if (EditorCanvas.TransformMode == TransformMode.None || EditorCanvas.Document is null) return;
        var values = EditorCanvas.CurrentTransform;
        ContextHint.Text = $"Preview · {values.AngleDegrees:0.##}° · {values.ScaleFactor * 100:0.##}% · Apply to create a version";
    }
    private void DisableTransformPreview() => EditorCanvas.TransformMode = TransformMode.None;
}
