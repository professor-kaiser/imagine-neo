using System.IO;
using System.Security.Cryptography;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

public sealed partial class ImageProcessingService
{
    private async Task<OperationResult> RunPluginAsync(ImageDocument source, ImageRect? selection, OperationOptions options)
    {
        var results = await RunPluginImagesAsync(source, selection, options);
        var documents = new List<ImageDocument>();
        try
        {
            for (int i = 0; i < results.Count; i++)
            {
                var value = results[i];
                string name = Path.GetFileNameWithoutExtension(source.FileName) + " - " + options.DisplayName
                    + (results.Count > 1 ? $" {i + 1}" : "") + ".png";
                documents.Add(await new LayerDocumentService(client, memory, thumbnails).CreateAsync(source, [value], name,
                    value.Width == source.Width && value.Height == source.Height ? selection : null));
            }
            return new OperationResult(Image: documents[0], Images: documents);
        }
        catch
        {
            foreach (var document in documents)
                try { await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = document.Id }); }
                catch (Exception) { }
            throw;
        }
    }

    // Used by both ordinary transforms and mask generators; no plugin-specific UI path.
    public async Task<IReadOnlyList<ImageLayer>> RunPluginImagesAsync(ImageDocument source, ImageRect? selection, OperationOptions options)
    {
        if (options.Masks.Count > 64 || options.Masks.Any(mask => mask.Width != source.Width || mask.Height != source.Height))
            throw new ArgumentException("Choose at most 64 masks with the same dimensions as the image.");
        string batch = Guid.NewGuid().ToString("N");
        var masks = new List<SharedImageLease>();
        var outputs = new List<SharedImageLease>();
        var results = new List<ImageLayer>();
        try
        {
            foreach (var mask in options.Masks)
                masks.Add(memory.CreateImage(mask.Encode(), mask.Width, mask.Height));
            var response = await client.SendAsync(new CommandMessage
            {
                MessageType = "run_operation", Operation = "run_plugin", ImageId = source.Id,
                ResultImageId = batch, FileName = source.FileName, Params = options.Parameters,
                Selection = selection is { } r ? new(r.X, r.Y, r.Width, r.Height) : null,
                MaskBuffers = masks.Select(mask => mask.Descriptor).ToArray()
            });
            if (response.MessageType != "plugin_results_ready" || response.ResultImageId != batch ||
                response.Images is not { Count: > 0 and <= 64 } images)
                throw new InvalidDataException("Missing plugin results.");
            for (int i = 0; i < images.Count; i++)
            {
                if (images[i].ImageId != $"{batch}:{i}") throw new InvalidDataException("Unexpected plugin result identity.");
                outputs.Add(memory.CreateOutput(images[i].Width, images[i].Height));
            }
            var copied = await client.SendAsync(new CommandMessage { MessageType = "read_plugin_results",
                ResultImageId = batch, OutputBuffers = outputs.Select(output => output.Descriptor).ToArray() });
            if (copied.MessageType != "plugin_results_copied") throw new InvalidDataException("Plugin transfer failed.");
            for (int i = 0; i < images.Count; i++)
            {
                byte[] pixels = await Task.Run(outputs[i].ReadPixels);
                if (Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant() != images[i].Sha256)
                    throw new InvalidDataException("Plugin pixels differ from the backend result.");
                results.Add(new ImageLayer($"{options.DisplayName} {i + 1}", images[i].Width, images[i].Height, pixels));
            }
        }
        finally
        {
            foreach (var output in outputs) output.Dispose();
            foreach (var mask in masks) mask.Dispose();
            try { await client.SendAsync(new CommandMessage { MessageType = "release_plugin_results", ResultImageId = batch }); }
            catch (Exception) when (!client.IsConnected) { }
        }
        return results;
    }

    public async Task<ImageMask> CreateMaskAsync(ImageDocument owner, ImageLayer source, string name,
        OperationOptions options, ImageRect? selection, string? identity = null)
    {
        if (source.Width != owner.Width || source.Height != owner.Height)
            throw new ArgumentException("The mask source must have the same dimensions as the target image.");
        if (selection is { } r && (r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0
            || (long)r.X + r.Width > owner.Width || (long)r.Y + r.Height > owner.Height))
            throw new ArgumentException("The mask selection must be inside the target image.");
        // Register a temporary copy so editing still works after the source leaves the library.
        var temporary = await new LayerDocumentService(client, memory, thumbnails)
            .CreateAsync(owner, [source], source.Name, masks: []);
        try
        {
            var outputs = await RunPluginImagesAsync(temporary, selection, options with { Masks = [] });
            if (outputs.Count != 1 || outputs[0].Width != owner.Width || outputs[0].Height != owner.Height)
                throw new InvalidDataException("A mask generator must return one image with the target dimensions.");
            var output = outputs[0];
            byte[] values = new byte[checked(output.Width * output.Height)];
            for (int i = 0; i < values.Length; i++)
            {
                int offset = i * 4;
                if (output.Pixels[offset] != output.Pixels[offset + 1] || output.Pixels[offset] != output.Pixels[offset + 2]
                    || output.Pixels[offset + 3] != 255) throw new InvalidDataException("Expected a single-channel mask.");
                values[i] = output.Pixels[offset];
            }
            return new ImageMask(identity ?? Guid.NewGuid().ToString("N"), name, output.Width, output.Height, values,
                source, options with { Masks = [] }, await thumbnails.CreateAsync(output.Pixels, output.Width, output.Height))
                { Selection = selection };
        }
        finally
        {
            await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = temporary.Id });
        }
    }
}
