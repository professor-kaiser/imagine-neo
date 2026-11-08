using System.Text.Json;

namespace PhotoLab.UI.Contracts;

public sealed record PluginDescriptor(string Id, string Name, string Description, List<PluginParameter> Parameters, string OutputKind = "image", string InputKind = "image");
public sealed record PluginParameter(string Name, string Type, string Label, string Description,
    bool Required, JsonElement Default, double? Min = null, double? Max = null, List<string>? Choices = null,
    Dictionary<string, List<string>>? VisibleWhen = null, string? PaletteChannel = null);
