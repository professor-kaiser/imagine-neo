using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Views;

public sealed partial class ProcessingPanel
{
    // Invoked only by the application's explicit --smoke-test mode.
    internal IEnumerable<OperationOptions> GetSmokeVariantOptions()
    {
        string previousOperation = SelectedOperation;
        int previousBlur = BlurTypePicker.SelectedIndex, previousBorder = BorderTypePicker.SelectedIndex;
        int previousNested = NestedOperation.SelectedIndex;
        double radius = BlurRadius.Value, kernel = BlurKernelSize.Value, diameter = BilateralDiameter.Value;
        double sigmaColor = BilateralSigmaColor.Value, sigmaSpace = BilateralSigmaSpace.Value, thickness = BorderWidthInput.Value;
        var color = new ColorRgba(12, 34, 56, 78);
        try
        {
            BlurRadius.Value = 3.25; BlurKernelSize.Value = 7; BilateralDiameter.Value = 11;
            BilateralSigmaColor.Value = 41.5; BilateralSigmaSpace.Value = 62.25; BorderWidthInput.Value = 3;
            foreach (bool nested in new[] { false, true })
            {
                foreach (string operation in new[] { "apply_blur", "apply_border" })
                {
                    SelectOperation(nested ? "apply_to_selection" : operation);
                    if (nested) SelectChoice(NestedOperation, operation);
                    var picker = operation == "apply_blur" ? BlurTypePicker : BorderTypePicker;
                    foreach (ComboBoxItem choice in picker.Items)
                    {
                        picker.SelectedItem = choice;
                        string type = (string)choice.Tag;
                        var expected = operation == "apply_border"
                            ? new Dictionary<string, object> { ["border_type"] = type, ["thickness"] = 3, ["color"] = color }
                            : new Dictionary<string, object> { ["blur_type"] = type };
                        if (operation == "apply_blur")
                        {
                            if (type == "gaussian") expected["radius"] = 3.25;
                            if (type is "average" or "median") expected["kernel_size"] = 7;
                            if (type == "bilateral")
                            {
                                expected["diameter"] = 11; expected["sigma_color"] = 41.5; expected["sigma_space"] = 62.25;
                            }
                            RequireVisible(BlurRadius, type == "gaussian");
                            RequireVisible(BlurKernelSize, type is "average" or "median");
                            RequireVisible(BilateralDiameter, type == "bilateral");
                            RequireVisible(BilateralSigmaColor, type == "bilateral");
                            RequireVisible(BilateralSigmaSpace, type == "bilateral");
                        }
                        RequireVisible(BlurTypePicker, operation == "apply_blur");
                        RequireVisible(BorderTypePicker, operation == "apply_border");
                        RequireVisible(BorderWidthInput, operation == "apply_border");
                        bool usesColor = operation == "apply_border" && type == "constant";
                        RequireVisible(ColorHint, usesColor);
                        if (UsesWorkingColor != usesColor) throw new InvalidOperationException("Incorrect working-color context.");

                        var options = GetOptions(color, null, null);
                        var actual = nested
                            ? (Dictionary<string, object>)options.Parameters["params"] : options.Parameters;
                        if (nested && (string)options.Parameters["operation"] != operation)
                            throw new InvalidOperationException("Incorrect nested filter.");
                        if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || !Equals(pair.Value, value)))
                            throw new InvalidOperationException($"Incorrect parameters for {operation}/{type}.");
                        yield return options;
                    }
                }
            }
            SelectOperation("apply_blur"); SelectChoice(BlurTypePicker, "median");
            BlurKernelSize.Value = 4;
            bool rejected = false;
            try { GetOptions(color, null, null); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("An even median kernel was accepted.");
        }
        finally
        {
            BlurRadius.Value = radius; BlurKernelSize.Value = kernel; BilateralDiameter.Value = diameter;
            BilateralSigmaColor.Value = sigmaColor; BilateralSigmaSpace.Value = sigmaSpace; BorderWidthInput.Value = thickness;
            BlurTypePicker.SelectedIndex = previousBlur; BorderTypePicker.SelectedIndex = previousBorder;
            NestedOperation.SelectedIndex = previousNested; SelectOperation(previousOperation);
        }
    }

    private static void SelectChoice(ComboBox picker, string value) =>
        picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == value);

    private static void RequireVisible(FrameworkElement element, bool expected)
    {
        if ((element.Visibility == Visibility.Visible) != expected)
            throw new InvalidOperationException($"Unexpected parameter visibility: {element.Name}.");
    }
}

