using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;
using PhotoLab.UI.Services;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private ImageLayer? _maskSource;
    private int MaskOutsideValue => MaskOutsideSelection?.SelectedIndex == 1 ? 255 : 0;

    private void MaskOutsideSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMaskSelectionContext();

    private void SetMaskGenerators(IReadOnlyList<PluginDescriptor> plugins)
    {
        string? previous = (MaskMethod.SelectedItem as PluginDescriptor)?.Id;
        var generators = plugins.Where(p => p.OutputKind == "mask").ToArray();
        MaskMethod.ItemsSource = generators;
        MaskMethod.SelectedItem = generators.FirstOrDefault(p => p.Id == previous) ?? generators.FirstOrDefault();
    }

    private void RefreshLayers()
    {
        if (MaskList is null) return;
        StopMaskPainting();
        _refreshingMasks = true;
        try
        {
            MaskList.ItemsSource = EditorCanvas.Document?.Masks;
            BeginNewMask();
            ProcessingTools.SetMasks(EditorCanvas.Document);
        }
        finally { _refreshingMasks = false; }
        UpdateLayerControls();
    }

    private void UpdateLayerControls()
    {
        if (MasksPanel is null || MaskTools is null) return;
        SyncMaskPainting();
        var image = EditorCanvas.Document;
        MasksPanel.IsEnabled = !_busy && image is not null && !image.Drawing.HasDrawing;
        bool selected = MaskList.SelectedItem is ImageMask;
        MaskDuplicateButton.IsEnabled = MaskRemoveButton.IsEnabled = selected;
        MaskUpButton.IsEnabled = selected && MaskList.SelectedIndex > 0;
        MaskDownButton.IsEnabled = selected && MaskList.SelectedIndex < image!.Masks.Count - 1;
        UpdateMaskSelectionContext();
        MaskTools.SetApplyLabel(selected ? "Update mask" : "Create mask");
    }

    private void UpdateMaskSelectionContext()
    {
        if (MaskTools is null || MaskRegionHint is null) return;
        var image = EditorCanvas.Document;
        var selection = EditorCanvas.Selection;
        MaskTools.SetContext(image is not null && _maskSource is not null && MaskMethod.SelectedItem is PluginDescriptor
            && (!IsPaintMask || _maskPaintingActive), _busy, selection is not null);
        MaskOutsideSelection.IsEnabled = image is not null && selection is not null && !_busy;
        string outside = MaskOutsideValue == 255 ? "white (255)" : "black (0)";
        MaskRegionHint.Text = image is null ? "Choose an image."
            : selection is { } r ? $"Mask region: X {r.X}, Y {r.Y}, {r.Width} x {r.Height} px. Outside: {outside}."
            : "Mask region: entire image (no selection).";
    }

    private void BeginNewMask()
    {
        StopMaskPainting();
        MaskList.SelectedIndex = -1;
        MaskName.Text = "Mask";
        MaskOutsideSelection.SelectedIndex = 0;
        MaskPreview.Source = null;
        _maskSource = null;
        if (EditorCanvas.Document is { } source)
            _maskSource = _maskPaintSessions.GetValueOrDefault(source.Id + "/new")?.Background
                ?? new ImageLayer(source.FileName, source.Width, source.Height, source.Pixels);
        MaskSource.SelectedItem = ViewModel.Images.FirstOrDefault(image => image.Pixels == _maskSource?.Pixels);
        UpdateLayerControls();
    }

    private void MaskNewClicked(object sender, RoutedEventArgs e) => BeginNewMask();

    private void MaskSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaskSource.SelectedItem is ImageDocument source)
        {
            _maskSource = new ImageLayer(source.FileName, source.Width, source.Height, source.Pixels);
            MaskPaintSourceChanged();
        }
        UpdateLayerControls();
    }

    private void MaskMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaskTools is null) return;
        if (MaskMethod.SelectedItem is PluginDescriptor plugin) MaskTools.SelectPlugin(plugin);
        UpdateLayerControls();
    }

    private void MaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MaskName is null) return;
        if (MaskList.SelectedItem is ImageMask mask)
        {
            MaskName.Text = mask.Name;
            MaskOutsideSelection.SelectedIndex = mask.Generator.Parameters.TryGetValue("mask_outside_value", out var outside)
                && Convert.ToInt32(outside) == 255 ? 1 : 0;
            MaskSource.SelectedIndex = -1;
            _maskSource = mask.Painting?.Background ?? mask.Source;
            MaskPreview.Source = mask.Thumbnail;
            MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>()
                .FirstOrDefault(p => p.Id == (string)mask.Generator.Parameters["plugin_id"]);
            if (MaskMethod.SelectedItem is not null)
                MaskTools.SetPluginArguments((IReadOnlyDictionary<string, object>)mask.Generator.Parameters["arguments"], reset: true);
            EditorCanvas.Selection = EditorCanvas.Document is { } owner && mask.Width == owner.Width && mask.Height == owner.Height
                ? mask.Selection : null;
        }
        UpdateLayerControls();
    }

    private async Task PublishMasksAsync(ImageDocument source, IReadOnlyList<ImageMask> masks, string? selectedId, ImageRect? selection)
    {
        var result = await new LayerDocumentService(_client, new(), new())
            .CreateAsync(source, [new ImageLayer(source.FileName, source.Width, source.Height, source.Pixels)],
                source.FileName, selection, masks);
        _history.Record(source, result);
        ViewModel.Images.Add(result);
        SelectRevision(result);
        MaskList.SelectedItem = result.Masks.FirstOrDefault(m => m.Id == selectedId);
        ViewModel.Status = "Masks updated. Image pixels preserved; Undo restores the previous masks.";
    }

    private async void MaskApplyRequested(object? sender, EventArgs e)
    {
        _processingTask = ApplyMaskAsync();
        await _processingTask;
    }

    private async Task ApplyMaskAsync()
    {
        if (_busy || EditorCanvas.Document is not { } source || source.Drawing.HasDrawing || _maskSource is null) return;
        var previous = MaskList.SelectedItem as ImageMask;
        string name = MaskName.Text.Trim();
        var selection = EditorCanvas.Selection;
        _lastOperationCode = null;
        SetBusy(true);
        try
        {
            if (name.Length is < 1 or > 256) throw new ArgumentException("Enter a mask name (1 to 256 characters).");
            if (previous is null && source.Masks.Count >= 64) throw new ArgumentException("At most 64 masks per image.");
            if (MaskMethod.SelectedItem is not PluginDescriptor) throw new ArgumentException("Choose an available mask generator.");
            var options = MaskTools.GetOptions(new(0, 0, 0, 255), null, null);
            options = options with { Parameters = new Dictionary<string, object>(options.Parameters)
                { ["mask_outside_value"] = MaskOutsideValue } };
            ImageLayer input = _maskSource;
            MaskPainting? painting = null;
            string? paintKey = null;
            if (IsPaintMask)
            {
                if (!_maskPaintingActive || _activeMaskPaint is null)
                    throw new ArgumentException("Open the Masks panel and choose a matching reference image before painting.");
                paintKey = MaskPaintKey;
                painting = _activeMaskPaint.Snapshot();
                input = new ImageLayer("Mask painting", source.Width, source.Height, EditorCanvas.GetDrawingMask());
            }
            var mask = await _processing.CreateMaskAsync(source, input, name, options, selection, previous?.Id);
            mask = mask with { Painting = painting };
            var masks = source.Masks.ToList();
            if (previous is null) masks.Add(mask); else masks[masks.IndexOf(previous)] = mask;
            await PublishMasksAsync(source, masks, mask.Id, selection);
            if (paintKey is not null) _maskPaintSessions.Remove(paintKey);
            MaskTools.ShowStatus("Done", "White = active; black = excluded. Choose this mask in a transformation to use it.");
        }
        catch (Exception error)
        {
            _lastOperationCode = "error"; ReportError(error); MaskTools.ShowStatus("Error", error.Message);
        }
        finally { SetBusy(false); }
    }

    private async Task ChangeMasksAsync(Func<List<ImageMask>, string?> change)
    {
        if (_busy || EditorCanvas.Document is not { } source || source.Drawing.HasDrawing) return;
        SetBusy(true);
        try
        {
            var selection = EditorCanvas.Selection;
            var masks = source.Masks.ToList();
            string? selected = change(masks);
            if (masks.Count > 64) throw new ArgumentException("At most 64 masks per image.");
            await PublishMasksAsync(source, masks, selected, selection);
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }

    private async void MaskDuplicateClicked(object sender, RoutedEventArgs e)
    {
        if (MaskList.SelectedItem is not ImageMask mask) return;
        _processingTask = ChangeMasksAsync(masks =>
        {
            var copy = mask with { Id = Guid.NewGuid().ToString("N"), Name = mask.Name + " copy" };
            masks.Add(copy); return copy.Id;
        });
        await _processingTask;
    }

    private async void MaskRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (MaskList.SelectedItem is not ImageMask mask) return;
        _processingTask = ChangeMasksAsync(masks => { masks.Remove(mask); return null; });
        await _processingTask;
    }

    private async void MaskMoveClicked(object sender, RoutedEventArgs e)
    {
        if (MaskList.SelectedItem is not ImageMask mask) return;
        int target = MaskList.SelectedIndex + ((sender as Button)?.Tag as string == "up" ? -1 : 1);
        if (target < 0 || target >= EditorCanvas.Document!.Masks.Count) return;
        _processingTask = ChangeMasksAsync(masks =>
        {
            int index = masks.IndexOf(mask);
            (masks[index], masks[target]) = (masks[target], masks[index]);
            return mask.Id;
        });
        await _processingTask;
    }
}
