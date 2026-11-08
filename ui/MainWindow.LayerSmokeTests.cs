using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task RunLayerSmokeAsync(ImageDocument source, bool fixtureMode)
    {
        var before = ViewModel.Images.ToHashSet();
        var pixels = (byte[])source.Pixels.Clone();
        SelectRevision(source);
        EditorCanvas.Selection = null;
        InspectorTabs.SelectedIndex = 1;
        if (source.Masks.Count != 0 || MaskList.Items.Count != 0)
            throw new InvalidOperationException("An original must start without masks.");
        MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>().Single(p => p.Id == "mask_threshold");
        MaskTools.VerifyMaskColorInputs();
        await MaskTools.VerifyHsvWheelAsync();
        MaskTools.SetPluginArguments(new Dictionary<string, object>
            { ["channel"] = "red", ["lower"] = 100, ["upper"] = 200, ["invert"] = false, ["blur_size"] = 1 });
        MaskName.Text = "Red range";
        if (!MaskTools.RequestApply()) throw new InvalidOperationException("Mask creation disabled.");
        await _processingTask;
        var added = EditorCanvas.Document!;
        if (_lastOperationCode is not null || added == source || added.Masks.Count != 1 || !added.Pixels.SequenceEqual(pixels))
            throw new InvalidOperationException("Mask creation changed the image or failed: " + ViewModel.Status);
        var first = added.Masks[0];
        for (int i = 0; i < first.Values.Length; i++)
        {
            byte expected = pixels[i * 4 + 3] != 0 && pixels[i * 4 + 2] >= 100 && pixels[i * 4 + 2] <= 200 ? (byte)255 : (byte)0;
            if (first.Values[i] != expected) throw new InvalidOperationException("Mask threshold bytes differ.");
        }
        MaskList.SelectedIndex = 0;
        MaskName.Text = "Inverted";
        MaskTools.SetPluginArguments(new Dictionary<string, object> { ["invert"] = true });
        if (!MaskTools.RequestApply()) throw new InvalidOperationException("Mask update disabled.");
        await _processingTask;
        var edited = EditorCanvas.Document!;
        if (_lastOperationCode is not null || edited == added || edited.Masks[0].Name != "Inverted"
            || first.Name != "Red range" || !edited.Pixels.SequenceEqual(pixels))
            throw new InvalidOperationException("Mask parameters or history failed: " + ViewModel.Status);
        for (int i = 0; i < first.Values.Length; i++)
            if (edited.Masks[0].Values[i] != (pixels[i * 4 + 3] == 0 ? 0 : 255 - first.Values[i]))
                throw new InvalidOperationException("Editing did not regenerate the mask from the saved source.");
        UndoClicked(this, new RoutedEventArgs());
        if (EditorCanvas.Document != added) throw new InvalidOperationException("Mask Undo failed.");
        RedoClicked(this, new RoutedEventArgs());
        if (EditorCanvas.Document != edited) throw new InvalidOperationException("Mask Redo failed.");
        MaskList.SelectedIndex = 0;
        MaskDuplicateClicked(this, new RoutedEventArgs());
        await _processingTask;
        if (EditorCanvas.Document!.Masks.Count != 2) throw new InvalidOperationException("Mask duplication failed.");
        string copyId = EditorCanvas.Document.Masks[1].Id;
        MaskMoveClicked(MaskUpButton, new RoutedEventArgs());
        await _processingTask;
        var owner = EditorCanvas.Document!;
        if (owner.Masks[0].Id != copyId) throw new InvalidOperationException("Mask order failed.");

        // Per-plugin choices, default empty list, deterministic order and real transport.
        var catalog = await _client.SendAsync(new CommandMessage { MessageType = "list_plugins" });
        var laplacian = catalog.Plugins!.Single(p => p.Id == "laplacian");
        var denoise = catalog.Plugins!.Single(p => p.Id == "denoise");
        ProcessingTools.SelectPlugin(laplacian);
        OperationOptions Options() => ProcessingTools.GetOptions(new(0, 0, 0, 255), null, null);
        if (Options().Masks.Count != 0) throw new InvalidOperationException("Masks were implicitly selected.");
        ProcessingTools.ChooseMask(owner.Masks[1].Id, true);
        ProcessingTools.ChooseMask(owner.Masks[0].Id, true);
        if (!Options().Masks.Select(m => m.Id).SequenceEqual(owner.Masks.Select(m => m.Id)))
            throw new InvalidOperationException("Selected mask order differs from the list.");
        ProcessingTools.SelectPlugin(denoise);
        if (Options().Masks.Count != 0) throw new InvalidOperationException("Mask choices leaked to another plugin.");
        ProcessingTools.SelectPlugin(laplacian);
        if (Options().Masks.Count != 2) throw new InvalidOperationException("Plugin mask choices were lost.");
        ProcessingTools.ChooseMask(owner.Masks[0].Id, false);
        if (Options().Masks.Count != 1 || Options().Masks[0].Id != owner.Masks[1].Id)
            throw new InvalidOperationException("Unselected mask would be sent.");
        var roi = new ImageRect(2, 3, 8, 7);
        EditorCanvas.Selection = roi;
        var reference = (await _processing.RunPluginImagesAsync(owner, roi, Options() with { Masks = [] }))[0];
        if (!ProcessingTools.RequestApply()) throw new InvalidOperationException("Masked transform disabled.");
        await _processingTask;
        var transformed = EditorCanvas.Document!;
        if (_lastOperationCode is not null || transformed == owner || transformed.Masks.Count != 2)
            throw new InvalidOperationException("Masked transform failed: " + ViewModel.Status);
        for (int y = 0; y < owner.Height; y++)
            for (int x = 0; x < owner.Width; x++)
            {
                int i = y * owner.Width + x;
                var expected = owner.Masks[1].Values[i] != 0 && x >= roi.X && x < roi.Right && y >= roi.Y && y < roi.Bottom
                    ? reference.Pixels : owner.Pixels;
                if (!transformed.Pixels.AsSpan(i * 4, 4).SequenceEqual(expected.AsSpan(i * 4, 4)))
                    throw new InvalidOperationException("Plugin did not respect the chosen mask and selection.");
            }
        UndoClicked(this, new RoutedEventArgs());
        if (EditorCanvas.Document != owner) throw new InvalidOperationException("Transform Undo lost the masks.");

        MaskList.SelectedIndex = 0;
        int count = ViewModel.Images.Count;
        MaskTools.SetPluginArguments(new Dictionary<string, object> { ["lower"] = 220, ["upper"] = 10 });
        MaskTools.RequestApply();
        await _processingTask;
        if (_lastOperationCode is null || EditorCanvas.Document != owner || ViewModel.Images.Count != count)
            throw new InvalidOperationException("Invalid mask parameters published a partial revision.");
        MaskRemoveClicked(this, new RoutedEventArgs());
        await _processingTask;
        MaskList.SelectedIndex = 0;
        MaskRemoveClicked(this, new RoutedEventArgs());
        await _processingTask;
        if (EditorCanvas.Document!.Masks.Count != 0) throw new InvalidOperationException("Last mask could not be removed.");

        // A mismatched mask remains stored, but cannot be sent to a resized image.
        var small = await _processing.CreateMaskAsync(source,
            new ImageLayer(source.FileName, source.Width, source.Height, source.Pixels), "Existing mask",
            new OperationOptions("run_plugin", "Image as mask", new Dictionary<string, object>
                { ["plugin_id"] = "mask_from_image", ["arguments"] = new Dictionary<string, object>() }), null);
        var incompatible = new ImageDocument("test-small", "small.png", "", 1, 1, [0, 0, 0, 255], source.Thumbnail, "")
            { Masks = new[] { small } };
        ProcessingTools.SetMasks(incompatible);
        if (Options().Masks.Count != 0) throw new InvalidOperationException("Incompatible mask was selected.");
        ProcessingTools.SetMasks(EditorCanvas.Document);

        if (fixtureMode)
        {
            SelectRevision(source);
            EditorCanvas.Selection = null;
            ProcessingTools.SelectPlugin(catalog.Plugins!.Single(p => p.Id == "test_layers"));
            int start = ViewModel.Images.Count;
            ProcessingTools.RequestApply();
            await _processingTask;
            var outputs = ViewModel.Images.Skip(start).ToArray();
            if (_lastOperationCode is not null || outputs.Length != 2
                || outputs[0].Width != 5 || outputs[0].Height != 3 || outputs[1].Width != 2 || outputs[1].Height != 7
                || outputs.Any(d => d.Layers.Count != 1 || d.Selection is not null))
                throw new InvalidOperationException("Multiple results were stacked or resized: " + ViewModel.Status);
            UndoClicked(this, new RoutedEventArgs());
            if (EditorCanvas.Document != source) throw new InvalidOperationException("Multiple result Undo failed.");
            RedoClicked(this, new RoutedEventArgs());
            if (EditorCanvas.Document != outputs[0]) throw new InvalidOperationException("Multiple result Redo failed.");
            SelectRevision(source);
            EditorCanvas.Selection = roi;
            start = ViewModel.Images.Count;
            ProcessingTools.RequestApply();
            await _processingTask;
            if (_lastOperationCode is not null || ViewModel.Images.Count != start + 2)
                throw new InvalidOperationException("Multiple ROI results failed.");
            foreach (var output in ViewModel.Images.Skip(start))
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++)
                        if (x < roi.X || x >= roi.Right || y < roi.Y || y >= roi.Bottom)
                        {
                            int offset = (y * source.Width + x) * 4;
                            if (!output.Pixels.AsSpan(offset, 4).SequenceEqual(pixels.AsSpan(offset, 4)))
                                throw new InvalidOperationException("Multiple results changed outside-selection pixels.");
                        }
        }
        SelectRevision(source);
        EditorCanvas.Selection = null;
        InspectorTabs.SelectedIndex = 1;
        BeginNewMask();
        MaskMethod.SelectedItem = MaskMethod.Items.OfType<PluginDescriptor>().Single(p => p.Id == "mask_threshold");
        MaskTools.SetPluginArguments(new Dictionary<string, object>
        {
            ["channel"] = "hsv", ["lower_hsv"] = new[] { 170, 60, 60 }, ["upper_hsv"] = new[] { 10, 255, 255 }
        }, reset: true);
        MaskTools.RequestApply();
        await _processingTask;
        var hsvResult = EditorCanvas.Document!;
        if (_lastOperationCode is not null || hsvResult == source || hsvResult.Masks.Count != 1)
            throw new InvalidOperationException("HSV mask failed: " + ViewModel.Status);
        for (int i = 0; i < pixels.Length / 4; i++)
        {
            var color = Windows.UI.Color.FromArgb(pixels[i * 4 + 3], pixels[i * 4 + 2], pixels[i * 4 + 1], pixels[i * 4]);
            var hsv = Controls.ColorPicker.ColorThresholdValues.ToHsv(color);
            byte expected = color.A != 0 && (hsv[0] >= 170 || hsv[0] <= 10) && hsv[1] >= 60 && hsv[2] >= 60 ? (byte)255 : (byte)0;
            if (hsvResult.Masks[0].Values[i] != expected) throw new InvalidOperationException("HSV values differ across UI/Python.");
        }
        UndoClicked(this, new RoutedEventArgs());
        if (EditorCanvas.Document != source) throw new InvalidOperationException("HSV mask Undo failed.");
        RedoClicked(this, new RoutedEventArgs());
        MaskList.SelectedIndex = 0;
        var restored = (IReadOnlyDictionary<string, object>)MaskTools.GetOptions(new(0, 0, 0, 255), null, null).Parameters["arguments"];
        if (EditorCanvas.Document != hsvResult || !((int[])restored["lower_hsv"]).SequenceEqual(new[] { 170, 60, 60 }))
            throw new InvalidOperationException("HSV mask settings were not restored.");
        await RunMaskSelectionSmokeAsync(source);
        await RunPaintMaskSmokeAsync(source);
        if (!source.Pixels.SequenceEqual(pixels) || source.Masks.Count != 0)
            throw new InvalidOperationException("Mask work mutated the original.");
        SelectRevision(source);
        foreach (var revision in ViewModel.Images.Where(image => !before.Contains(image)).ToArray())
        {
            await _documents.RemoveAsync(revision);
            _history.Forget(revision);
        }
        InspectorTabs.SelectedIndex = 0;
        ProcessingTools.SelectOperation("to_grayscale");
    }
}
