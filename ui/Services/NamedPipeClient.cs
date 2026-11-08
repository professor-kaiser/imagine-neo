using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Services;

public sealed class NamedPipeClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<byte> _pending = [];
    private NamedPipeClientStream? _pipe;
    public string PipeName { get; private set; }
    public bool IsConnected => _pipe?.IsConnected == true;
    public NamedPipeClient(string? name = null) => PipeName = name ?? Environment.GetEnvironmentVariable("PHOTOLAB_PIPE_NAME")
        ?? "PhotoLab_v1_" + Regex.Replace(Environment.UserName, "[^A-Za-z0-9_-]", "_");

    public void UsePrivateSession()
    {
        DisposePipe();
        PipeName = "PhotoLab_session_" + Guid.NewGuid().ToString("N");
    }

    public async Task ConnectAsync(int timeoutMs = 1500)
    {
        DisposePipe();
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(timeoutMs); _pipe = pipe; }
        catch { pipe.Dispose(); throw; }
    }

    public async Task<ResponseMessage> SendAsync(CommandMessage command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pipe is null || !_pipe.IsConnected) throw new IOException("Backend unavailable. Click Reconnect.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, IpcJson.Options) + "\n");
            if (line.Length > 65536) throw new InvalidDataException("Command is too long.");
            await _pipe.WriteAsync(line, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
            var response = JsonSerializer.Deserialize<ResponseMessage>(await ReadLineAsync(timeout.Token), IpcJson.Options)
                ?? throw new InvalidDataException("Empty response.");
            if (response.ProtocolVersion != 1 || response.RequestId != command.RequestId || response.ImageId != command.ImageId)
                throw new InvalidDataException("Incompatible or mismatched IPC response.");
            if (response.Status == "error")
            {
                if (response.MessageType != "error" || string.IsNullOrWhiteSpace(response.Code) || response.Message is null)
                    throw new InvalidDataException("Incomplete or incompatible backend error response.");
                throw new BackendCommandException(response.Message, response.Code);
            }
            string expected = command.MessageType switch { "import_image" => "image_imported", "remove_image" => "image_removed",
                "ping" => "pong", "list_images" => "images_listed", "list_plugins" => "plugins_listed", "shutdown" => "shutdown_ack",
                "to_grayscale" => "image_processed",
                "read_plugin_results" => "plugin_results_copied", "release_plugin_results" => "plugin_results_released",
                "run_operation" => command.Operation switch { "run_plugin" when command.OutputBuffer is null => "plugin_results_ready", "pick_color" => "color_picked", "extract_palette" => "palette_extracted", _ => "image_processed" }, _ => "" };
            if (response.Status != "ok" || response.MessageType != expected)
                throw new InvalidDataException("Invalid IPC response status or type.");
            if (command.ResultImageId is not null && response.ResultImageId != command.ResultImageId)
                throw new InvalidDataException("Incorrect processing result identity.");
            return response;
        }
        catch (BackendCommandException) { throw; }
        catch (JsonException error)
        {
            DisposePipe();
            throw new InvalidDataException($"Backend response does not match the JSON contract (field {error.Path ?? "unknown"}). Restart the backend with the current project version.", error);
        }
        catch { DisposePipe(); throw; }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> ReadLineAsync(CancellationToken token)
    {
        byte[] chunk = new byte[4096];
        while (true)
        {
            int newline = _pending.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                if (newline + 1 > 65536) throw new InvalidDataException("IPC response is too long.");
                byte[] line = _pending.GetRange(0, newline).ToArray();
                _pending.RemoveRange(0, newline + 1);
                return line;
            }
            if (_pending.Count >= 65536) throw new InvalidDataException("IPC response is too long.");
            int count = await _pipe!.ReadAsync(chunk, token);
            if (count == 0) throw new EndOfStreamException("The backend closed the connection.");
            _pending.AddRange(chunk.AsSpan(0, count).ToArray());
        }
    }

    private void DisposePipe() { _pipe?.Dispose(); _pipe = null; _pending.Clear(); }
    public void Dispose() => DisposePipe();
}

public sealed class BackendCommandException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
