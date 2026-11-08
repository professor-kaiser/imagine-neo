using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PhotoLab.UI.Controls.ColorPicker;

// Shared plugin field: an HSV wheel or a colored scalar bar with exact numeric thresholds.
// Numeric HSV values remain authoritative, including hue at zero saturation/value.
public sealed class PluginColorInput : UserControl
{
    private readonly NumberBox[] _numbers;
    private readonly Microsoft.UI.Xaml.Controls.ColorPicker? _palette;
    private readonly ColorSpectrum? _spectrum;
    private readonly SolidColorBrush? _preview;
    private readonly Slider? _bar;
    private readonly Grid? _checkerboard;
    private readonly LinearGradientBrush _track = new() { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    private readonly LinearGradientBrush _fill = new() { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
    private readonly SolidColorBrush _thumb = new();
    private readonly bool _hsv, _bgra;
    private readonly string _label;
    private string _component = "gray";
    private bool _sync;

    public PluginColorInput(string label, bool hsv, double minimum = 0, double maximum = 255, bool bgra = false)
    {
        _hsv = hsv; _bgra = bgra; _label = label;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        if (bgra) { row.RowDefinitions.Add(new RowDefinition()); row.RowDefinitions.Add(new RowDefinition()); }
        _numbers = new NumberBox[hsv ? 3 : bgra ? 4 : 1];
        for (int i = 0; i < _numbers.Length; i++)
        {
            if (!bgra || i < 2) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string? componentLabel = hsv ? new[] { "H", "S", "V" }[i] : bgra ? new[] { "B", "G", "R", "A" }[i] : null;
            var number = new NumberBox { Header = componentLabel,
                Minimum = hsv ? 0 : minimum, Maximum = hsv && i == 0 ? 179 : maximum,
                SmallChange = 1, Value = double.NaN, ValidationMode = NumberBoxValidationMode.Disabled,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 0 };
            AutomationProperties.SetName(number, label + (componentLabel is null ? "" : " " + componentLabel));
            Grid.SetColumn(number, bgra ? i % 2 : i); Grid.SetRow(number, bgra ? i / 2 : 0); row.Children.Add(number); _numbers[i] = number;
            number.ValueChanged += (_, _) => SyncPalette();
        }
        panel.Children.Add(row);
        if (hsv)
        {
            _spectrum = new ColorSpectrum { MinWidth = 0, MaxWidth = 256, Height = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch, Shape = ColorSpectrumShape.Ring,
                Components = ColorSpectrumComponents.HueSaturation };
            AutomationProperties.SetName(_spectrum, label + " HSV color wheel");
            // Listen to HSV itself: ColorChanged can be silent for changes at black,
            // gray or low saturation because many HSV values map to the same RGB bytes.
            _spectrum.RegisterPropertyChangedCallback(ColorSpectrum.HsvColorProperty, (_, _) => ReadSpectrum());
            _bar = CreateValueBar(0, 255, label + " Value (V)");
            _preview = new SolidColorBrush();
            var wheel = new StackPanel { Spacing = 6 };
            wheel.Children.Add(_spectrum);
            wheel.Children.Add(new TextBlock { Text = "Value (V)", FontSize = 12 });
            wheel.Children.Add(_bar);
            wheel.Children.Add(new Border { Height = 20, Background = _preview,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 128, 128, 128)), BorderThickness = new Thickness(1) });
            panel.Children.Add(new Expander { Header = "HSV color wheel", Content = wheel, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        else if (bgra)
        {
            _palette = new Microsoft.UI.Xaml.Controls.ColorPicker { MinWidth = 0, MaxWidth = 256,
                HorizontalAlignment = HorizontalAlignment.Stretch, ColorSpectrumShape = ColorSpectrumShape.Box,
                ColorSpectrumComponents = ColorSpectrumComponents.HueSaturation,
                IsAlphaEnabled = true, IsAlphaSliderVisible = true, IsAlphaTextInputVisible = false,
                IsHexInputVisible = false, IsColorChannelTextInputVisible = false, IsMoreButtonVisible = false,
                IsColorPreviewVisible = true, IsColorSliderVisible = true };
            AutomationProperties.SetName(_palette, label + " BGRA palette");
            _palette.ColorChanged += (_, args) =>
            {
                if (_sync) return;
                _sync = true;
                try
                {
                    int[] values = [args.NewColor.B, args.NewColor.G, args.NewColor.R, args.NewColor.A];
                    for (int i = 0; i < values.Length; i++) _numbers[i].Value = values[i];
                }
                finally { _sync = false; }
            };
            panel.Children.Add(new Expander { Header = "BGRA palette", Content = _palette, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        else
        {
            _bar = CreateValueBar(minimum, maximum, label + " value bar");
            // The native Slider retains keyboard, pointer, touch and accessibility behavior.
            var barPanel = new Grid();
            _checkerboard = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            for (int x = 0; x < 32; x++)
                _checkerboard.ColumnDefinitions.Add(new ColumnDefinition());
            for (int y = 0; y < 2; y++)
            {
                _checkerboard.RowDefinitions.Add(new RowDefinition());
                for (int x = 0; x < 32; x++)
                {
                    var cell = new Border { Background = new SolidColorBrush((x + y) % 2 == 0
                        ? Color.FromArgb(255, 110, 110, 110) : Color.FromArgb(255, 190, 190, 190)) };
                    Grid.SetColumn(cell, x); Grid.SetRow(cell, y); _checkerboard.Children.Add(cell);
                }
            }
            barPanel.Children.Add(_checkerboard);
            barPanel.Children.Add(_bar);
            panel.Children.Add(barPanel);
        }
        Content = panel;
    }

    private Slider CreateValueBar(double minimum, double maximum, string name)
    {
        _track.GradientStops.Add(new GradientStop());
        _track.GradientStops.Add(new GradientStop { Offset = 1 });
        _fill.GradientStops.Add(new GradientStop());
        _fill.GradientStops.Add(new GradientStop { Offset = 1 });
        var bar = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = 1,
            SmallChange = 1, LargeChange = 16, SnapsTo = SliderSnapsTo.StepValues,
            Background = _track, Foreground = _fill, CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        bar.Resources["SliderTrackThemeHeight"] = 12d;
        foreach (string state in new[] { "", "PointerOver", "Pressed" })
        {
            bar.Resources["SliderTrackFill" + state] = _track;
            bar.Resources["SliderTrackValueFill" + state] = _fill;
            bar.Resources["SliderThumbBackground" + state] = _thumb;
        }
        AutomationProperties.SetName(bar, name);
        bar.ValueChanged += (_, args) =>
        {
            if (!_sync) _numbers[_hsv ? 2 : 0].Value = Math.Round(args.NewValue);
        };
        return bar;
    }

    private void ReadSpectrum()
    {
        if (_sync || _spectrum is null) return;
        var hsv = _spectrum.HsvColor;
        _sync = true;
        try
        {
            _numbers[0].Value = Math.Clamp(Math.Round(hsv.X / 2.0), 0, 179);
            _numbers[1].Value = Math.Clamp(Math.Round(hsv.Y * 255.0), 0, 255);
            if (!double.IsFinite(_numbers[2].Value)) _numbers[2].Value = 255;
        }
        finally { _sync = false; }
        SyncPalette();
    }

    public object Value
    {
        get
        {
            var values = new int[_numbers.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double number = _numbers[i].Value;
                if (!double.IsFinite(number) || number != Math.Truncate(number)
                    || number < _numbers[i].Minimum || number > _numbers[i].Maximum)
                    throw new ArgumentException(_label + ": enter whole numbers within the displayed limits.");
                values[i] = (int)number;
            }
            return _hsv || _bgra ? values : (object)(long)values[0];
        }
        set
        {
            _sync = true;
            try
            {
                if (_hsv || _bgra)
                {
                    if (value is not IEnumerable<int> components || components.ToArray() is not { } values || values.Length != _numbers.Length)
                        throw new ArgumentException("Unexpected number of color components.");
                    for (int i = 0; i < values.Length; i++) _numbers[i].Value = values[i];
                }
                else _numbers[0].Value = Convert.ToDouble(value);
            }
            finally { _sync = false; }
            SyncPalette();
        }
    }

    public void SetComponent(string component)
    {
        _component = component;
        if (_checkerboard is not null)
            _checkerboard.Visibility = component == "alpha" ? Visibility.Visible : Visibility.Collapsed;
        SyncPalette();
    }

    private void SyncPalette()
    {
        if (_sync || _palette is null && _bar is null) return;
        object value;
        try { value = Value; }
        catch (ArgumentException) { return; }
        _sync = true;
        try
        {
            if (_spectrum is not null)
            {
                var parts = (int[])value;
                // The wheel edits only H/S at full brightness so it stays usable at
                // V=0. The separate V bar and preview retain the actual threshold.
                _spectrum.HsvColor = new(parts[0] * 2, parts[1] / 255f, 1, 1);
                _preview!.Color = ColorThresholdValues.FromHsv(parts);
            }
            if (_palette is not null)
            {
                var parts = (int[])value;
                _palette.Color = Color.FromArgb((byte)parts[3], (byte)parts[2], (byte)parts[1], (byte)parts[0]);
            }
            if (_bar is not null)
            {
                var hsv = _hsv ? (int[])value : null;
                byte current = hsv is not null ? (byte)hsv[2] : Convert.ToByte(value);
                _bar.Value = current;
                Color start = hsv is not null ? Color.FromArgb(255, 0, 0, 0)
                    : ColorThresholdValues.FromComponent((byte)_bar.Minimum, _component);
                Color end = hsv is not null ? ColorThresholdValues.FromHsv([hsv[0], hsv[1], 255])
                    : ColorThresholdValues.FromComponent((byte)_bar.Maximum, _component);
                Color selected = hsv is not null ? ColorThresholdValues.FromHsv(hsv)
                    : ColorThresholdValues.FromComponent(current, _component);
                _track.GradientStops[0].Color = start;
                _track.GradientStops[1].Color = end;
                _fill.GradientStops[0].Color = start;
                _fill.GradientStops[1].Color = selected;
                _thumb.Color = selected with { A = 255 };
            }
        }
        finally { _sync = false; }
    }

    internal void ChooseColor(Color color)
    {
        if (_hsv) Value = ColorThresholdValues.ToHsv(color);
        else if (_palette is not null) _palette.Color = color;
        else throw new InvalidOperationException("Use the value bar for a scalar channel.");
    }

    internal void ChooseBarValue(double value)
    {
        if (_bar is null) throw new InvalidOperationException("Use the HSV wheel for this field.");
        _bar.Value = value;
    }
}

internal static class ColorThresholdValues
{
    public static Color FromComponent(byte value, string component) => component switch
    {
        "red" => Color.FromArgb(255, value, 0, 0),
        "green" => Color.FromArgb(255, 0, value, 0),
        "blue" => Color.FromArgb(255, 0, 0, value),
        "alpha" => Color.FromArgb(value, 128, 128, 128),
        _ => Color.FromArgb(255, value, value, value)
    };

    // OpenCV's uint8 BGR2HSV integer conversion (12-bit lookup-table arithmetic).
    public static int[] ToHsv(Color color)
    {
        int r = color.R, g = color.G, b = color.B;
        int v = Math.Max(r, Math.Max(g, b)), diff = v - Math.Min(r, Math.Min(g, b));
        if (diff == 0) return [0, 0, v];
        int saturation = (diff * (int)Math.Round((255 << 12) / (double)v) + 2048) >> 12;
        int hue = v == r ? g - b : v == g ? b - r + 2 * diff : r - g + 4 * diff;
        hue = (hue * (int)Math.Round((180 << 12) / (6.0 * diff)) + 2048) >> 12;
        if (hue < 0) hue += 180;
        return [hue, saturation, v];
    }

    public static Color FromHsv(int[] hsv)
    {
        double h = hsv[0] / 30.0, s = hsv[1] / 255.0, v = hsv[2] / 255.0;
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromArgb(255, (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
