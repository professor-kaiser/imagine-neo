using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoLab.UI.Contracts;

public sealed record PixelBufferDescriptor(string MappingName, int Width, int Height, int Channels,
    string Dtype, string PixelFormat, int RowStrideBytes, long BufferSizeBytes);

public sealed record SelectionDescriptor(int X, int Y, int Width, int Height);
public sealed record ColorRgba(int R, int G, int B, int A);
public sealed record PaletteColor(ColorRgba Color, int Count, double Frequency);

public sealed record CommandMessage
{
    public int ProtocolVersion { get; init; } = 1;
    public required string MessageType { get; init; }
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string? ImageId { get; init; }
    public string? BatchId { get; init; }
    public string? FileName { get; init; }
    public PixelBufferDescriptor? PixelBuffer { get; init; }
    public string? ResultImageId { get; init; }
    public PixelBufferDescriptor? OutputBuffer { get; init; }
    public PixelBufferDescriptor? MaskBuffer { get; init; }
    public IReadOnlyList<PixelBufferDescriptor>? MaskBuffers { get; init; }
    public IReadOnlyList<PixelBufferDescriptor>? OutputBuffers { get; init; }
    public SelectionDescriptor? Selection { get; init; }
    public string? Operation { get; init; }
    public IReadOnlyDictionary<string, object>? Params { get; init; }
}

public sealed record BackendImage(string ImageId, string FileName, int Width, int Height, string Sha256);
public sealed record ResponseMessage
{
    public int ProtocolVersion { get; init; }
    public string MessageType { get; init; } = "";
    public string? RequestId { get; init; }
    public string? ImageId { get; init; }
    public string Status { get; init; } = "";
    public string? ResultImageId { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public string? BackendRevision { get; init; }
    public int BackendPid { get; init; }
    public string? Sha256 { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Removed { get; init; }
    public List<BackendImage>? Images { get; init; }
    public List<PluginDescriptor>? Plugins { get; init; }
    public List<string>? PluginErrors { get; init; }
    public ColorRgba? Color { get; init; }
    public List<PaletteColor>? Palette { get; init; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
