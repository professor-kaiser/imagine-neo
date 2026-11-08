using System.IO;
using System.Security.Cryptography;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

public sealed partial class ImageProcessingService(NamedPipeClient client, SharedMemoryService memory, ThumbnailService thumbnails)
{
    public async Task<OperationResult> RunAsync(ImageDocument source, ImageRect? selection, OperationOptions options, byte[]? drawingMask = null)
    {
        if (options.Operation == "run_plugin") return await RunPluginAsync(source, selection, options);
        string? resultId = options.IsAnalysis ? null : Guid.NewGuid().ToString("N");
        string suffix = options.Operation == "to_grayscale" ? "Grayscale" : options.DisplayName;
        string? fileName = options.IsAnalysis ? null : Path.GetFileNameWithoutExtension(source.FileName) + " — " + suffix + Path.GetExtension(source.FileName);
        var (width, height) = options.IsTransform ? options.Transform.ValidatedOutputSize(source.Width, source.Height) : (source.Width, source.Height);
        if (options.IsTransform) selection = null;
        using var output = options.IsAnalysis ? null : memory.CreateOutput(width, height);
        if ((options.Operation == "apply_drawing") != (drawingMask is not null))
            throw new ArgumentException("Drawing operations require a full-size BGRA mask.");
        using var mask = drawingMask is null ? null : memory.CreateImage(drawingMask, source.Width, source.Height);
        var response = await client.SendAsync(new CommandMessage
        {
            MessageType = "run_operation", ImageId = source.Id, ResultImageId = resultId,
            FileName = fileName, OutputBuffer = output?.Descriptor, MaskBuffer = mask?.Descriptor,
            Operation = options.Operation, Params = options.Parameters,
            Selection = options.Operation is not ("pick_color" or "apply_drawing") && selection is { } r ? new(r.X, r.Y, r.Width, r.Height) : null
        });
        if (options.Operation == "pick_color")
        {
            ValidateColor(response.Color);
            return new OperationResult(Color: response.Color);
        }
        if (options.Operation == "extract_palette")
        {
            if (response.Palette is not { Count: > 0 and <= 256 } palette)
                throw new InvalidDataException("Palette is missing or too large.");
            foreach (var item in palette)
            {
                ValidateColor(item.Color);
                if (item.Count <= 0 || !double.IsFinite(item.Frequency) || item.Frequency < 0 || item.Frequency > 1)
                    throw new InvalidDataException("Invalid palette frequency.");
            }
            return new OperationResult(Palette: palette);
        }
        try
        {
            var (pixels, digest) = await Task.Run(() =>
            {
                byte[] bytes = output!.ReadPixels();
                return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            });
            if (response.Width != width || response.Height != height || response.Sha256 != digest)
                throw new InvalidDataException("Result pixels differ from those computed by Python.");
            var thumbnail = await thumbnails.CreateAsync(pixels, width, height);
            return new OperationResult(Image: new ImageDocument(resultId!, fileName!, source.FilePath, width, height, pixels, thumbnail, digest)
            { Selection = selection, Masks = source.Masks });
        }
        catch
        {
            // Roll back a result the UI could not materialize, preserving the source.
            try { await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = resultId }); }
            catch (Exception) { /* A broken connection is reported by the original failure. */ }
            throw;
        }
    }

    private static void ValidateColor(ColorRgba? color)
    {
        if (color is null || new[] { color.R, color.G, color.B, color.A }.Any(value => value < 0 || value > 255))
            throw new InvalidDataException("Invalid RGBA color in the backend response.");
    }
}
