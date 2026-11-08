using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI;

public sealed partial class MainWindow
{
    private async Task RunPluginSmokeAsync(ImageDocument source)
    {
        var response = await _client.SendAsync(new CommandMessage { MessageType = "list_plugins" });
        var demo = response.Plugins?.Single(p => p.Id == "denoise")
            ?? throw new InvalidOperationException("Denoising plugin missing from YAML catalogue.");
        if (response.PluginErrors?.Count > 0) throw new InvalidOperationException(string.Join("\n", response.PluginErrors));
        SelectRevision(source);
        Toolbox.VerifyPluginPresentations(demo);
        ProcessingTools.VerifyPluginForm();
        ProcessingTools.VerifyScalarPluginInputs();
        ProcessingTools.VerifyPublicColorInputs();
        ProcessingTools.SelectPlugin(demo);
        int count = ViewModel.Images.Count;
        var before = (byte[])source.Pixels.Clone();
        var originalSelection = source.Selection;
        foreach (ImageRect? region in new ImageRect?[] { null, new(2, 3, 8, 7) })
        {
            SelectRevision(source);
            EditorCanvas.Selection = region;
            if (!ProcessingTools.RequestApply()) throw new InvalidOperationException("Plugin Apply was not available.");
            await _processingTask;
            if (_lastOperationCode is not null || ViewModel.Images.Count != count + 1 || EditorCanvas.Document == source)
                throw new InvalidOperationException("Plugin did not publish a new image: " + ViewModel.Status);
            var result = EditorCanvas.Document!;
            if (result.Width != source.Width || result.Height != source.Height)
                throw new InvalidOperationException("Plugin changed dimensions.");
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    int offset = (y * source.Width + x) * 4;
                    if (result.Pixels[offset + 3] != before[offset + 3])
                        throw new InvalidOperationException("Denoising changed alpha.");
                    if (region is { } r && !(x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom)
                        && !result.Pixels.AsSpan(offset, 4).SequenceEqual(before.AsSpan(offset, 4)))
                        throw new InvalidOperationException("Plugin changed pixels outside the selection.");
                }
            UndoClicked(this, new RoutedEventArgs());
            if (EditorCanvas.Document != source) throw new InvalidOperationException("Plugin Undo failed.");
            RedoClicked(this, new RoutedEventArgs());
            if (EditorCanvas.Document != result) throw new InvalidOperationException("Plugin Redo failed.");
            await _documents.RemoveAsync(result); _history.Forget(result);
        }
        if (!source.Pixels.SequenceEqual(before)) throw new InvalidOperationException("Plugin modified the original.");
        SelectRevision(source);
        EditorCanvas.Selection = originalSelection;
        ProcessingTools.SelectOperation("to_grayscale");
    }
}
