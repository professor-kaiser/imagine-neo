using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;
namespace PhotoLab.UI.Views;
public sealed partial class ProcessingPanel : UserControl
{
    private bool _hasImage, _busy;
    private EditorTool _tool = EditorTool.Find("to_grayscale");
    public event EventHandler? ApplyRequested;
    public event EventHandler? SelectedOperationChanged;
    public event EventHandler? ParameterContextChanged;
    private string ActiveFilter => SelectedOperation == "apply_to_selection" ? Choice(NestedOperation) : SelectedOperation;
    public bool UsesWorkingColor => SelectedOperation is "replace_selection_with_color" or "pick_color"
        || (ActiveFilter == "apply_border" && Choice(BorderTypePicker) == "constant");
    public string SelectedOperation => IsPlugin ? "run_plugin" : _tool.Operation;
    public string SelectedLabel => _plugin?.Name ?? _tool.Label;
    public ProcessingPanel() { InitializeComponent(); Refresh(); SetDefaultStatus(); }
    public void SetContext(bool hasImage, bool busy, bool hasSelection = false)
    {
        _hasImage = hasImage; _busy = busy; Refresh();
        TargetHint.Text = !hasImage ? "Import an image to use this tool."
            : IsTransform ? "Target: entire image · expanded frame, no cropping" : hasSelection ? "Target: current selection" : "Target: entire image";
    }
    public bool RequestApply()
    {
        if (!ApplyButton.IsEnabled) return false;
        ApplyRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
    private void ApplyClicked(object sender, RoutedEventArgs e) => RequestApply();
    public void SelectOperation(string operation)
    {
        _plugin = null;
        _tool = EditorTool.Find(operation);
        Refresh(); SetDefaultStatus();
        SelectedOperationChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetPixelCoordinates(int x, int y) { PixelX.Value = x; PixelY.Value = y; }
    public void ShowStatus(string title, string message)
    {
        OperationStatus.Title = title; OperationStatus.Message = message;
        OperationStatus.Severity = title is "Error" or "Processing error" ? InfoBarSeverity.Error
            : title == "Done" ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        OperationStatus.IsOpen = true;
    }
    public OperationOptions GetOptions(ColorRgba color, ImageDocument? source, ImageRect? sourceRegion)
    {
        if (IsPlugin) return GetPluginOptions();
        string operation = SelectedOperation;
        var parameters = GetFilterParameters(operation, color);
        if (IsTransform)
        {
            var values = GetTransformValues().ForMode(ImageTransform.ModeFor(operation));
            if (_imageWidth > 0) values.ValidatedOutputSize(_imageWidth, _imageHeight);
            if (operation != "scale_image") parameters["angle_degrees"] = values.AngleDegrees;
            if (operation != "rotate_image") parameters["scale_factor"] = values.ScaleFactor;
        }
        if (operation == "replace_selection_with_color") parameters["color"] = color;
        if (operation == "replace_selection_with_region")
        {
            if (source is null || sourceRegion is not { } r)
                throw new ArgumentException("Choose a source image and a target selection of a compatible size.");
            parameters["source_image_id"] = source.Id;
            parameters["source_region"] = new SelectionDescriptor(r.X, r.Y, r.Width, r.Height);
        }
        if (operation == "pick_color")
        {
            parameters["x"] = Integer(PixelX.Value, "X", 0);
            parameters["y"] = Integer(PixelY.Value, "Y", 0);
        }
        if (operation == "extract_palette") parameters["max_colors"] = Integer(PaletteSize.Value, "Number of colors", 1);
        if (operation == "apply_to_selection")
        {
            string nested = (string)((ComboBoxItem)NestedOperation.SelectedItem).Tag;
            parameters["operation"] = nested;
            parameters["params"] = GetFilterParameters(nested, color);
        }
        return new(operation, SelectedLabel, parameters);
    }
    private Dictionary<string, object> GetFilterParameters(string operation, ColorRgba color)
    {
        var parameters = new Dictionary<string, object>();
        if (operation == "apply_blur")
        {
            string type = Choice(BlurTypePicker);
            parameters["blur_type"] = type;
            if (type == "gaussian") parameters["radius"] = Positive(BlurRadius.Value, "Sigma", 1000);
            if (type is "average" or "median")
            {
                int size = Integer(BlurKernelSize.Value, "Kernel size", type == "median" ? 3 : 1, 255);
                if (type == "median" && size % 2 == 0) throw new ArgumentException("Median blur requires an odd kernel size (3, 5, 7…).");
                parameters["kernel_size"] = size;
            }
            if (type == "bilateral")
            {
                parameters["diameter"] = Integer(BilateralDiameter.Value, "Diameter", 1, 255);
                parameters["sigma_color"] = Positive(BilateralSigmaColor.Value, "Color sigma", 1000);
                parameters["sigma_space"] = Positive(BilateralSigmaSpace.Value, "Spatial sigma", 1000);
            }
        }
        if (operation == "apply_border")
        {
            parameters["border_type"] = Choice(BorderTypePicker);
            parameters["thickness"] = Integer(BorderWidthInput.Value, "Width", 1, 32768);
            parameters["color"] = color;
        }
        if (operation == "apply_contours")
        {
            if (!double.IsFinite(LowThreshold.Value) || !double.IsFinite(HighThreshold.Value) || LowThreshold.Value < 0 || HighThreshold.Value < LowThreshold.Value)
                throw new ArgumentException("Thresholds must satisfy 0 ≤ lower threshold ≤ upper threshold.");
            parameters["threshold1"] = LowThreshold.Value;
            parameters["threshold2"] = HighThreshold.Value;
        }
        if (operation == "apply_color_separation") parameters["mode"] = (string)((ComboBoxItem)ChannelPicker.SelectedItem).Tag;
        return parameters;
    }
    private static string Choice(ComboBox picker) => (picker?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static double Positive(double value, string label, double maximum)
    {
        if (!double.IsFinite(value) || value <= 0 || value > maximum)
            throw new ArgumentException($"{label} must be greater than 0 and at most {maximum}.");
        return value;
    }
    private static int Integer(double value, string label, int minimum, int maximum = int.MaxValue)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
            throw new ArgumentException($"{label} must be an integer between {minimum} and {maximum}.");
        return (int)value;
    }
    private void NestedOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        Refresh(); ParameterContextChanged?.Invoke(this, EventArgs.Empty);
    }
    private void FilterVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        Refresh();
        if (OperationStatus is not null) SetDefaultStatus();
        ParameterContextChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Refresh()
    {
        if (ApplyButton is null) return;
        string operation = SelectedOperation;
        ToolTitle.Text = SelectedLabel;
        ToolDescription.Text = _plugin?.Description ?? _tool.Description;
        PluginFields.Visibility = Visible(IsPlugin);
        PluginMasksPanel.Visibility = Visible(IsPlugin && _plugin?.OutputKind != "mask");
        foreach (var check in PluginMaskChoices.Children.OfType<CheckBox>())
            check.IsEnabled = !_busy && _availableMasks.Any(m => Equals(check.Tag, m.Id) && m.Width == _maskWidth && m.Height == _maskHeight);
        foreach (var input in _pluginInputs.Values.OfType<Control>()) input.IsEnabled = !_busy;
        string filter = ActiveFilter;
        ApplyButton.IsEnabled = _hasImage && !_busy;
        ApplyButton.Content = operation is "pick_color" or "extract_palette" ? "Analyze image" : "Apply " + SelectedLabel.ToLowerInvariant();
        ParametersPanel.Visibility = operation == "to_grayscale" || IsPlugin ? Visibility.Collapsed : Visibility.Visible;
        NestedOperation.Visibility = Visible(operation == "apply_to_selection");
        string blurType = Choice(BlurTypePicker);
        BlurTypePicker.Visibility = BlurTypeHint.Visibility = Visible(filter == "apply_blur");
        BlurRadius.Visibility = Visible(filter == "apply_blur" && blurType == "gaussian");
        BlurKernelSize.Visibility = Visible(filter == "apply_blur" && blurType is "average" or "median");
        BilateralDiameter.Visibility = BilateralSigmaColor.Visibility = BilateralSigmaSpace.Visibility = Visible(filter == "apply_blur" && blurType == "bilateral");
        BlurTypeHint.Text = blurType switch
        {
            "gaussian" => "Smooth, weighted blur. Sigma controls the strength; the kernel size is automatic.",
            "average" => "Average neighboring pixels in a square kernel.",
            "median" => "Reduce speckled noise. The kernel size must be an odd integer of at least 3.",
            _ => "Smooth similar colors while keeping strong edges. Larger diameters take more time."
        };
        BorderTypePicker.Visibility = BorderWidthInput.Visibility = BorderTypeHint.Visibility = Visible(filter == "apply_border");
        BorderTypeHint.Text = Choice(BorderTypePicker) switch
        {
            "constant" => "Use the working color for a solid border.",
            "replicate" => "Repeat the nearest edge pixel.",
            "reflect" => "Mirror the image, including its edge pixel.",
            "reflect_101" => "Mirror the image without repeating its edge pixel (OpenCV default).",
            _ => "Repeat pixels from the opposite edge."
        };
        LowThreshold.Visibility = HighThreshold.Visibility = Visible(filter == "apply_contours");
        ChannelPicker.Visibility = Visible(filter == "apply_color_separation");
        ColorHint.Visibility = Visible(UsesWorkingColor && operation != "pick_color");
        SourceHint.Visibility = Visible(operation == "replace_selection_with_region");
        PixelHint.Visibility = PixelX.Visibility = PixelY.Visibility = Visible(operation == "pick_color");
        PaletteSize.Visibility = Visible(operation == "extract_palette");
        ParametersPanel.IsEnabled = !_busy;
        RefreshTransformControls();
    }
    private void SetDefaultStatus() => OperationStatus.IsOpen = false;
    private static Visibility Visible(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
}
