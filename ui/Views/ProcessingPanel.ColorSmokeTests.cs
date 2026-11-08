using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PhotoLab.UI.Controls.ColorPicker;

namespace PhotoLab.UI.Views;

public sealed partial class ProcessingPanel
{
    private static IEnumerable<T> ColorDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in ColorDescendants<T>(child)) yield return descendant;
        }
    }

    internal async Task VerifyHsvWheelAsync()
    {
        ((ComboBox)_pluginInputs["channel"]).SelectedItem = "hsv";
        // Exercise the native spectrum after its template is loaded, including HSV
        // changes that do not change its RGB Color and therefore emit no ColorChanged.
        await Task.Delay(100);
        UpdateLayout();
        var lower = (PluginColorInput)_pluginInputs["lower_hsv"];
        var spectrum = ColorDescendants<ColorSpectrum>(lower).Single();
        lower.Value = new[] { 137, 200, 0 };
        if (Math.Abs(spectrum.HsvColor.X - 274) > 0.01
            || Math.Abs(spectrum.HsvColor.Y * 255 - 200) > 0.01)
            throw new InvalidOperationException("HSV wheel lost the restored hue/saturation at V=0: " + spectrum.HsvColor);
        spectrum.HsvColor = new Vector4(240, 0.8f, spectrum.HsvColor.Z, 1);
        var arguments = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
        if (!((int[])arguments["lower_hsv"]).SequenceEqual(new[] { 120, 204, 0 }))
            throw new InvalidOperationException("Changing the HSV wheel at black did not preserve the threshold: "
                + string.Join(",", (int[])arguments["lower_hsv"]));
        // Adjusting brightness must not quantize either of the other thresholds.
        var valueBar = ColorDescendants<Slider>(lower).Single();
        foreach (var hsv in new[] { new[] { 137, 200, 0 }, new[] { 137, 200, 1 },
            new[] { 137, 1, 128 }, new[] { 137, 0, 128 } })
        {
            lower.Value = hsv;
            valueBar.Value = hsv[2] + 1;
            if (!((int[])lower.Value).SequenceEqual(new[] { hsv[0], hsv[1], hsv[2] + 1 })
                || Math.Abs(spectrum.HsvColor.X - hsv[0] * 2) > 0.01)
                throw new InvalidOperationException("The V bar changed H/S at low brightness or saturation.");
        }
        // A hue change at zero saturation has the same RGB color.
        lower.Value = new[] { 137, 0, 0 };
        spectrum.HsvColor = new Vector4(120, 0, spectrum.HsvColor.Z, 1);
        if (!((int[])lower.Value).SequenceEqual(new[] { 60, 0, 0 }))
            throw new InvalidOperationException("A hue change at zero saturation was ignored.");
        var upper = (PluginColorInput)_pluginInputs["upper_hsv"];
        upper.Value = new[] { 179, 255, 255 };
        ColorDescendants<Slider>(upper).Single().Value = 254;
        if (!((int[])upper.Value).SequenceEqual(new[] { 179, 255, 254 }))
            throw new InvalidOperationException("The upper hue endpoint moved when adjusting V.");
        channelsRoundTrip();
        void channelsRoundTrip()
        {
            ((ComboBox)_pluginInputs["channel"]).SelectedItem = "red";
            ((ComboBox)_pluginInputs["channel"]).SelectedItem = "hsv";
            var restored = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
            if (!((int[])restored["lower_hsv"]).SequenceEqual(new[] { 60, 0, 0 })
                || !((int[])restored["upper_hsv"]).SequenceEqual(new[] { 179, 255, 254 }))
                throw new InvalidOperationException("Switching channels lost the HSV thresholds.");
        }
        SetPluginArguments(new Dictionary<string, object>(), reset: true);
    }
}
