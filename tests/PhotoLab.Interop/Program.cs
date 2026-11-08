using System.Diagnostics;
using System.IO.Pipes;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Controls.SelectionOverlay;
using PhotoLab.UI.Controls.ImageCanvas;
using PhotoLab.UI.Models;
using PhotoLab.UI.Rendering;
using PhotoLab.UI.Services;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : "../..");
string python = Environment.GetEnvironmentVariable("PHOTOLAB_PYTHON") ?? Path.Combine(root, ".tools/miniforge3/envs/photolab/python.exe");
string pipeName = "PhotoLab_interop_" + Guid.NewGuid().ToString("N");
var start = new ProcessStartInfo(python) { WorkingDirectory = Path.Combine(root, "backend"), UseShellExecute = false, CreateNoWindow = true };
// Test-only injection reproduces OpenCV failures even after the user's blur is fixed.
start.ArgumentList.Add(Path.Combine(root, "backend/tests/fixture_backend.py"));
start.ArgumentList.Add("--opencv-error"); start.ArgumentList.Add("--pipe-name"); start.ArgumentList.Add(pipeName);
using var backend = Process.Start(start) ?? throw new InvalidOperationException("Backend launch failed");
using var client = new NamedPipeClient(pipeName);
var results = new List<string>();
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    results.Add(description);
    Console.WriteLine("PASS " + description);
}
try
{
    for (int attempt = 0; attempt < 40; attempt++)
    {
        try { await client.ConnectAsync(250); break; }
        catch (TimeoutException) { if (backend.HasExited) throw new InvalidOperationException("Backend exited early"); }
    }
    var initialReady = await client.SendAsync(new CommandMessage { MessageType = "ping" });
    Check(initialReady.BackendRevision == BackendProcessService.ComputeSourceRevision(root) && initialReady.BackendPid == backend.Id,
        "C# and Python agree on the source revision and the actual backend process identity");
    var catalogue = await client.SendAsync(new CommandMessage { MessageType = "list_plugins" });
    Check(catalogue.Plugins?.Any(p => p.Id == "denoise" && p.Parameters.Any(field =>
        field.Name == "strength" && field.Type == "number" && field.Default.GetDouble() == 10)) == true
        && catalogue.PluginErrors?.Count == 0, "YAML plugin catalogue deserializes into typed C# fields and defaults");
    var maskFields = catalogue.Plugins!.Single(p => p.Id == "mask_threshold").Parameters;
    Check(maskFields.Single(p => p.Name == "channel").Choices!.Contains("hsv")
        && maskFields.Single(p => p.Name == "lower").PaletteChannel == "channel"
        && maskFields.Single(p => p.Name == "lower_hsv").Default.EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 0, 0, 0 }),
        "Choice, palette and HSV defaults cross the catalogue contract");
    Check(catalogue.Plugins!.Single(p => p.Id == "mask_paint").InputKind == "painting",
        "The paint generator declares its common painting input");
    var colorFields = catalogue.Plugins!.Single(p => p.Id == "test_colors").Parameters;
    Check(colorFields[0].Type == "color_hsv" && colorFields[1].Type == "color_bgra",
        "Public color parameter types cross the catalogue contract");
    var expected = new Dictionary<string, string>();
    for (int index = 0; index < 2; index++)
    {
        int width = 3 + index * 17, height = 2 + index * 11;
        byte[] pixels = Enumerable.Range(0, width * height * 4).Select(value => (byte)((value * 19 + index * 51) % 256)).ToArray();
        string id = "interop-" + index;
        using var lease = new SharedMemoryService().CreateImage(pixels, width, height);
        var response = await client.SendAsync(new CommandMessage { MessageType = "import_image", ImageId = id,
            BatchId = "interop-batch", FileName = $"test-{index}.png", PixelBuffer = lease.Descriptor });
        expected[id] = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        Check(response.Sha256 == expected[id] && response.Width == width && response.Height == height,
            $"C# raw mapping -> Python BGRA8 array, image {index}, exact hash and dimensions");
        if (index == 0)
        {
            try
            {
                await client.SendAsync(new CommandMessage { MessageType = "import_image", ImageId = "missing",
                    BatchId = "interop-batch", FileName = "missing.png", PixelBuffer = lease.Descriptor with { MappingName = "PhotoLab_Image_missing_" + Guid.NewGuid().ToString("N") } });
                throw new InvalidOperationException("Missing mapping was accepted");
            }
            catch (BackendCommandException) { Check(client.IsConnected, "Bad import returns a correlated error without disconnecting C#"); }
        }
    }
    var stored = await client.SendAsync(new CommandMessage { MessageType = "list_images" });
    Check(stored.Images?.Count == 2 && stored.Images.All(record => expected[record.ImageId] == record.Sha256),
        "Python owns both images after all C# mappings have been disposed");
    using (var colorOutput = new SharedMemoryService().CreateOutput(3, 2))
    {
        await client.SendAsync(new CommandMessage { MessageType = "run_operation",
            Operation = "run_plugin", ImageId = "interop-0", ResultImageId = "color-result", FileName = "color.png",
            OutputBuffer = colorOutput.Descriptor, Params = new Dictionary<string, object> {
                ["plugin_id"] = "test_colors", ["arguments"] = new Dictionary<string, object> {
                    ["accent"] = new[] { 120, 255, 255 }, ["tint"] = new[] { 55, 66, 77, 88 } } } });
        byte[] values = colorOutput.ReadPixels();
        Check(Enumerable.Range(0, 6).All(i => values.AsSpan(i * 4, 4).SequenceEqual(new byte[] { 55, 66, 77, 88 })),
            "HSV/BGRA constructor arguments preserve channel order and alpha over IPC");
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "color-result" });
    }
    using (var hsvOutput = new SharedMemoryService().CreateOutput(3, 2))
    {
        await client.SendAsync(new CommandMessage { MessageType = "run_operation",
            Operation = "run_plugin", ImageId = "interop-0", ResultImageId = "hsv-result", FileName = "hsv.png",
            OutputBuffer = hsvOutput.Descriptor, Params = new Dictionary<string, object> {
                ["plugin_id"] = "mask_threshold", ["arguments"] = new Dictionary<string, object> {
                    ["channel"] = "hsv", ["lower_hsv"] = new[] { 0, 0, 0 }, ["upper_hsv"] = new[] { 179, 255, 255 } } } });
        Check(hsvOutput.ReadPixels().All(value => value == 255), "HSV triplet arguments generate a full-range OpenCV mask via IPC");
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "hsv-result" });
    }
    using (var pluginOutput = new SharedMemoryService().CreateOutput(3, 2))
    {
        var response = await client.SendAsync(new CommandMessage { MessageType = "run_operation",
            Operation = "run_plugin", ImageId = "interop-0", ResultImageId = "plugin-result",
            FileName = "denoised.png", OutputBuffer = pluginOutput.Descriptor, Selection = new(1, 0, 1, 1),
            Params = new Dictionary<string, object> { ["plugin_id"] = "denoise", ["arguments"] = new Dictionary<string, object>() } });
        byte[] pluginPixels = pluginOutput.ReadPixels();
        Check(response.Sha256 == Convert.ToHexString(SHA256.HashData(pluginPixels)).ToLowerInvariant()
            && Enumerable.Range(0, pluginPixels.Length).Where(i => i < 4 || i >= 8).All(i => pluginPixels[i] == (byte)(i * 19 % 256)),
            "Generic plugin command returns real denoising pixels and preserves everything outside the ROI");
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "plugin-result" });
    }
    using (var mask = new SharedMemoryService().CreateImage(
        Enumerable.Range(0, 24).Select(i => (byte)(i % 4 == 3 ? 255 : 0)).ToArray(), 3, 2))
    {
        using var maskedOutput = new SharedMemoryService().CreateOutput(3, 2);
        await client.SendAsync(new CommandMessage { MessageType = "run_operation",
            Operation = "run_plugin", ImageId = "interop-0", ResultImageId = "masked-result", FileName = "masked.png",
            MaskBuffers = new[] { mask.Descriptor }, OutputBuffer = maskedOutput.Descriptor,
            Params = new Dictionary<string, object> { ["plugin_id"] = "laplacian", ["arguments"] = new Dictionary<string, object>() } });
        Check(maskedOutput.ReadPixels().SequenceEqual(Enumerable.Range(0, 24).Select(i => (byte)(i * 19 % 256))),
            "An explicitly chosen zero OpenCV mask preserves every source pixel through C#/Python IPC");
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "masked-result" });
        var ready = await client.SendAsync(new CommandMessage { MessageType = "run_operation",
            Operation = "run_plugin", ImageId = "interop-0", ResultImageId = "multi", FileName = "multi.png",
            MaskBuffers = Array.Empty<PixelBufferDescriptor>(),
            Params = new Dictionary<string, object> { ["plugin_id"] = "test_layers", ["arguments"] = new Dictionary<string, object>() } });
        Check(ready.Images is { Count: 2 } && ready.Images[0].Width == 5 && ready.Images[0].Height == 3 &&
            ready.Images[1].Width == 2 && ready.Images[1].Height == 7, "Plugin negotiates independent output dimensions");
        using var first = new SharedMemoryService().CreateOutput(5, 3);
        using var second = new SharedMemoryService().CreateOutput(2, 7);
        await client.SendAsync(new CommandMessage { MessageType = "read_plugin_results", ResultImageId = "multi",
            OutputBuffers = new[] { first.Descriptor, second.Descriptor } });
        Check(first.ReadPixels().Take(4).SequenceEqual(new byte[] { 255, 0, 0, 255 }) &&
            second.ReadPixels().Take(4).SequenceEqual(new byte[] { 0, 0, 255, 128 }), "Multiple BGRA images cross C#/Python IPC with exact alpha");
        await client.SendAsync(new CommandMessage { MessageType = "release_plugin_results", ResultImageId = "multi" });
        var after = await client.SendAsync(new CommandMessage { MessageType = "list_images" });
        Check(after.Images!.All(image => !image.ImageId.StartsWith("multi:")), "Temporary plugin results are released as a batch");
    }
    using (var output = new SharedMemoryService().CreateOutput(3, 2))
    {
        try
        {
            await client.SendAsync(new CommandMessage { MessageType = "run_operation", ImageId = "interop-0",
                Operation = "apply_blur", Params = new Dictionary<string, object> { ["radius"] = 5.0 },
                ResultImageId = "failed-blur", FileName = "failed-blur.png", OutputBuffer = output.Descriptor });
            throw new InvalidOperationException("The OpenCV error fixture unexpectedly succeeded");
        }
        catch (BackendCommandException error)
        {
            Check(error.Code == "opencv_error" && error.Message.Contains("ksize") && error.Message.Contains("-5"),
                "Real cv2.error numeric -5 becomes textual opencv_error with native diagnostic, not JsonException");
        }
        Check(client.IsConnected && (await client.SendAsync(new CommandMessage { MessageType = "ping" })).Status == "ok",
            "Same C# pipe accepts the next request after an algorithm exception");
        stored = await client.SendAsync(new CommandMessage { MessageType = "list_images" });
        Check(stored.Images?.Count == 2 && stored.Images.All(record => expected[record.ImageId] == record.Sha256),
            "Failed operation creates no result and leaves all registered originals unchanged");
        var converted = await client.SendAsync(new CommandMessage { MessageType = "run_operation", ImageId = "interop-0",
            Operation = "to_grayscale", Params = new Dictionary<string, object>(), ResultImageId = "recovery-gray",
            FileName = "recovery-gray.png", OutputBuffer = output.Descriptor });
        byte[] pixels = output.ReadPixels();
        Check(converted.Sha256 == Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant()
            && Enumerable.Range(0, 6).All(i => pixels[i * 4] == pixels[i * 4 + 1] && pixels[i * 4] == pixels[i * 4 + 2]
                && pixels[i * 4 + 3] == (byte)(((i * 4 + 3) * 19) % 256)),
            "Grayscale returns correct BGRA8 pixels through shared memory on the same connection after failure");
        await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "recovery-gray" });
    }

    // A legacy or malformed server must yield a clear protocol diagnostic and close that connection.
    string legacyName = "PhotoLab_legacy_" + Guid.NewGuid().ToString("N");
    using (var legacyServer = new NamedPipeServerStream(legacyName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
    using (var legacyClient = new NamedPipeClient(legacyName))
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        var request = new CommandMessage { MessageType = "ping" };
        var reply = Task.Run(async () =>
        {
            await legacyServer.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(legacyServer, leaveOpen: true);
            if (await reader.ReadLineAsync(deadline.Token) is null) throw new EndOfStreamException();
            byte[] invalid = JsonSerializer.SerializeToUtf8Bytes(new { protocol_version = 1, message_type = "error",
                request_id = request.RequestId, image_id = (string?)null, status = "error", code = -5, message = "legacy OpenCV error" });
            await legacyServer.WriteAsync(invalid, deadline.Token);
            await legacyServer.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token);
            await legacyServer.FlushAsync(deadline.Token);
        });
        await legacyClient.ConnectAsync();
        try
        {
            await legacyClient.SendAsync(request, deadline.Token);
            throw new InvalidOperationException("Legacy numeric error code was accepted");
        }
        catch (InvalidDataException error)
        {
            Check(error.InnerException is JsonException && error.Message.Contains("$.code") && !legacyClient.IsConnected,
                "Incompatible legacy JSON identifies the code field and closes the invalid connection");
        }
        await reply;
    }
    foreach (string? staleRevision in new string?[] { null, new string('0', 64) })
    {
        string staleName = "PhotoLab_stale_" + Guid.NewGuid().ToString("N");
        using var staleServer = new NamedPipeServerStream(staleName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var freshClient = new NamedPipeClient(staleName);
        using var service = new BackendProcessService(freshClient);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var oldSession = Task.Run(async () =>
        {
            await staleServer.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(staleServer, leaveOpen: true);
            var ping = JsonSerializer.Deserialize<CommandMessage>((await reader.ReadLineAsync(deadline.Token))!, IpcJson.Options)!;
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(new { protocol_version = 1, message_type = "pong",
                request_id = ping.RequestId, image_id = (string?)null, status = "ok", backend_revision = staleRevision });
            await staleServer.WriteAsync(response, deadline.Token);
            await staleServer.WriteAsync(new byte[] { 10 }, deadline.Token);
            await staleServer.FlushAsync(deadline.Token);
            return await reader.ReadLineAsync(deadline.Token);
        });
        try
        {
            await service.EnsureReadyAsync();
            var ready = await freshClient.SendAsync(new CommandMessage { MessageType = "ping" });
            Check(freshClient.PipeName != staleName && ready.BackendRevision == BackendProcessService.ComputeSourceRevision(root),
                $"UI starts a current private backend when the existing revision is {(staleRevision is null ? "missing" : "outdated")}");
            Check(await oldSession is null, "Outdated external session is disconnected without sending shutdown or changing its images");
        }
        finally { await service.StopOwnedAsync(); }
    }
    foreach (string operation in new[] { "rotate_image", "scale_image", "rotate_scale_image" })
    {
        foreach (var values in new[] { new ImageTransform(90, 2), new ImageTransform(-33.5, 0.375) })
        {
            var transform = values.ForMode(ImageTransform.ModeFor(operation));
            var (width, height) = transform.ValidatedOutputSize(20, 13);
            using var output = new SharedMemoryService().CreateOutput(width, height);
            var parameters = new Dictionary<string, object>();
            if (operation != "scale_image") parameters["angle_degrees"] = transform.AngleDegrees;
            if (operation != "rotate_image") parameters["scale_factor"] = transform.ScaleFactor;
            string identity = Guid.NewGuid().ToString("N");
            var reply = await client.SendAsync(new CommandMessage { MessageType = "run_operation", Operation = operation,
                ImageId = "interop-1", ResultImageId = identity, FileName = "transform.png", Params = parameters, OutputBuffer = output.Descriptor });
            byte[] pixels = output.ReadPixels();
            Check(reply.Width == width && reply.Height == height && reply.Sha256 == Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant()
                && pixels.Take(4).SequenceEqual(new byte[] {31,47,59,127}), $"C#/Python agree on {operation} output bounds and returned pixels ({width}x{height})");
            await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = identity });
        }
    }
    await client.SendAsync(new CommandMessage { MessageType = "remove_image", ImageId = "interop-0" });
    stored = await client.SendAsync(new CommandMessage { MessageType = "list_images" });
    Check(stored.Images?.Count == 1, "C# remove_image updates Python registry");
    var view = new ViewportTransform();
    view.Fit(900, 600, 320, 200);
    Vector2 anchor = new(401, 212), before = view.ToImage(anchor);
    view.ZoomAt(anchor, 2.4f);
    Check(Vector2.Distance(before, view.ToImage(anchor)) < 0.001f, "Zoom preserves the image pixel below the cursor");
    view.Pan(new(34, -12));
    Check(Vector2.Distance(new(45, 65), view.ToImage(view.ToScreen(new(45, 65)))) < 0.001f, "Image/screen coordinate round trip survives pan and zoom");
    Check(new ImageTransform(90, 2).OutputSize(4, 3) == (6, 8), "Quarter-turn output swaps width and height without floating residue");
    Check(new ImageTransform(45, 1).OutputSize(4, 3) == (5, 5), "Arbitrary-angle output expands to contain the entire image");
    foreach (float zoom in new[] { 0.5f, 1f, 3f })
    {
        var gesture = new TransformController();
        var center = new Vector2(50, 30);
        var handle = TransformController.RotationHandle(100, 60, ImageTransform.Identity, zoom);
        Check(gesture.Begin(handle, 100, 60, zoom, TransformMode.Rotation, ImageTransform.Identity), "Rotation handle accepts a press at every zoom");
        Vector2 quarterTurn = center + Vector2.TransformNormal(handle - center, Matrix3x2.CreateRotation(MathF.PI / 2));
        Check(Math.Abs(gesture.Update(quarterTurn).AngleDegrees - 90) < 0.02, "Dragging clockwise updates the angle in image coordinates");
        gesture.End();
        Vector2 corner = new(100, 60);
        Check(gesture.Begin(corner, 100, 60, zoom, TransformMode.Scale, ImageTransform.Identity), "Scale corner accepts a press at every zoom");
        Check(Math.Abs(gesture.Update(center + (corner - center) * 1.5f).ScaleFactor - 1.5) < 0.0001, "Corner drag scales uniformly about the center");
        gesture.End();
        Check(!gesture.Begin(center, 100, 60, zoom, TransformMode.RotationScale, ImageTransform.Identity), "The center is not a transform handle");
    }
    var selection = new SelectionController();
    selection.Begin(new(20, 10), 1); selection.Update(new(100, 70), 320, 200);
    Check(selection.Selection == new ImageRect(20, 10, 80, 60), "Rectangle creation stores image-pixel coordinates");
    selection.Begin(new(60, 40), 1); selection.Update(new(500, 500), 320, 200);
    Check(selection.Selection == new ImageRect(240, 140, 80, 60), "Moving selection clamps to image bounds without resizing");
    selection.Begin(new(240, 140), 2); selection.Update(new(230, 120), 320, 200);
    Check(selection.Selection == new ImageRect(230, 120, 90, 80), "Corner resize modifies both dimensions in image pixels");
    selection.FixedSize = true;
    selection.Begin(new(230, 120), 1); selection.Update(new(0, 0), 320, 200);
    Check(selection.Selection?.Width == 90 && selection.Selection?.Height == 80, "Source region movement preserves target dimensions");
    await client.SendAsync(new CommandMessage { MessageType = "shutdown" });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await backend.WaitForExitAsync(timeout.Token);
    Check(backend.ExitCode == 0, "Graceful C# -> Python shutdown");
    Directory.CreateDirectory(Path.Combine(root, "artifacts"));
    await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "interop-report.json"),
        JsonSerializer.Serialize(new { status = "ok", test_fixtures = "Real OpenCV failure injected in memory; production algorithms unchanged", checks = results }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    if (!backend.HasExited) { backend.Kill(); await backend.WaitForExitAsync(); }
}
