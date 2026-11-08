namespace PhotoLab.UI.Models;

// The catalogue describes UI tools only. Availability still comes from the backend.
public sealed record EditorTool(string Operation, string Label, string Glyph, string Description)
{
    public static IReadOnlyList<ToolGroup> Groups { get; } =
    [
        new("TRANSFORM",
            new("rotate_image", "Rotation", "\uE7AD", "Rotate the entire image around its center."),
            new("scale_image", "Scale", "\uE740", "Resize the entire image while preserving its proportions."),
            new("rotate_scale_image", "Rotation + scale", "\uE7C1", "Set the angle and size together before applying.")),
        new("ADJUSTMENTS",
            new("to_grayscale", "Grayscale", "\uE790", "Turn the image or selection into black and white."),
            new("apply_color_separation", "Color channels", "\uE7B5", "Isolate the red, green, blue or alpha channel.")),
        new("FILTERS & EFFECTS",
            new("apply_blur", "Blur", "\uE9D9", "Choose a Gaussian, average, median or bilateral blur."),
            new("apply_border", "Border", "\uE739", "Choose a solid, replicated, reflected or wrapped border."),
            new("apply_contours", "Edge detection", "\uE7A7", "Find edges with adjustable lower and upper thresholds.")),
        new("SELECTION",
            new("replace_selection_with_color", "Fill selection", "\uE2B3", "Fill the selected area with your working color."),
            new("replace_selection_with_region", "Replace region", "\uE8C8", "Replace a selection with a region from another image."),
            new("apply_to_selection", "Filter selection", "\uEF20", "Apply a filter exclusively inside the selected area.")),
        new("COLOR & ANALYSIS",
            new("pick_color", "Color picker", "\uEF3C", "Click the canvas to sample a pixel, or enter coordinates."),
            new("extract_palette", "Extract palette", "\uE790", "Find the main colors and their frequency in the image."))
    ];

    public static EditorTool Find(string operation) => Groups.SelectMany(group => group).Single(tool => tool.Operation == operation);
}

public sealed class ToolGroup(string title, params EditorTool[] tools) : List<EditorTool>(tools)
{
    public string Title { get; } = title;
}
