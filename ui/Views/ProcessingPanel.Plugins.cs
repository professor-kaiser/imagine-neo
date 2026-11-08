using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Controls.ColorPicker;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Views;

public sealed partial class ProcessingPanel
{
    private PluginDescriptor? _plugin;
    private readonly Dictionary<string, FrameworkElement> _pluginInputs = new();
    private readonly Dictionary<string, FrameworkElement> _pluginFieldContainers = new();
    public bool IsPlugin => _plugin is not null;
    public string? SelectedPluginId => _plugin?.Id;

    public void SelectPlugin(PluginDescriptor plugin)
    {
        if (_plugin == plugin) { SelectedOperationChanged?.Invoke(this, EventArgs.Empty); return; }
        _plugin = plugin;
        PluginFields.Children.Clear(); _pluginInputs.Clear(); _pluginFieldContainers.Clear();
        foreach (var parameter in plugin.Parameters)
        {
            string label = parameter.Label + (parameter.Required ? " *" : "");
            bool hasDefault = parameter.Default.ValueKind != JsonValueKind.Undefined;
            FrameworkElement input = parameter.Type switch
            {
                "text" when parameter.Choices is { Count: > 0 } => new ComboBox
                {
                    Header = label, ItemsSource = parameter.Choices,
                    SelectedItem = hasDefault ? parameter.Default.GetString() : null,
                    PlaceholderText = "Choose an option"
                },
                "hsv" or "color_hsv" => new PluginColorInput(label, true),
                "color_bgra" => new PluginColorInput(label, false, bgra: true),
                "integer" when parameter.PaletteChannel is not null => new PluginColorInput(label, false,
                    parameter.Min ?? 0, parameter.Max ?? 255),
                "text" => new TextBox { Header = label, MaxLength = 4096,
                    Text = hasDefault ? parameter.Default.GetString() ?? "" : "" },
                "number" or "integer" => new NumberBox
                {
                    Header = label, Minimum = parameter.Min ?? -9007199254740991d,
                    Maximum = parameter.Max ?? 9007199254740991d,
                    SmallChange = parameter.Type == "integer" ? 1 : 0.1,
                    Value = hasDefault ? parameter.Default.GetDouble() : double.NaN,
                    ValidationMode = NumberBoxValidationMode.Disabled,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
                },
                "boolean" => new CheckBox { Content = label, IsThreeState = !hasDefault,
                    IsChecked = hasDefault ? parameter.Default.GetBoolean() : null },
                _ => throw new InvalidOperationException("Unsupported plugin parameter type: " + parameter.Type)
            };
            input.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetAutomationId(input, "PluginParameter_" + parameter.Name);
            AutomationProperties.SetName(input, label);
            var container = new StackPanel { Spacing = 4 };
            container.Children.Add(input);
            PluginFields.Children.Add(container);
            _pluginInputs[parameter.Name] = input;
            _pluginFieldContainers[parameter.Name] = container;
            if (input is PluginColorInput color && hasDefault) color.Value = PluginDefault(parameter);
            if (!string.IsNullOrEmpty(parameter.Description))
                container.Children.Add(new TextBlock { Text = parameter.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        }
        foreach (var choice in _pluginInputs.Values.OfType<ComboBox>())
            choice.SelectionChanged += (_, _) => RefreshPluginFields();
        RefreshPluginFields();
        RefreshMaskChoices();
        Refresh(); SetDefaultStatus();
        SelectedOperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private OperationOptions GetPluginOptions()
    {
        var arguments = new Dictionary<string, object>();
        foreach (var parameter in _plugin!.Parameters)
        {
            if (!IsPluginParameterVisible(parameter)) continue;
            var input = _pluginInputs[parameter.Name];
            object value;
            if (input is ComboBox choice) value = choice.SelectedItem as string
                ?? throw new ArgumentException(parameter.Label + ": choose an option.");
            else if (input is PluginColorInput color) value = color.Value;
            else if (input is TextBox text) value = text.Text;
            else if (input is CheckBox check)
                value = check.IsChecked ?? throw new ArgumentException(parameter.Label + " : valeur obligatoire.");
            else
            {
                double number = ((NumberBox)input).Value;
                if (!double.IsFinite(number) || Math.Abs(number) > 9007199254740991d
                    || parameter.Min is { } min && number < min || parameter.Max is { } max && number > max)
                    throw new ArgumentException(parameter.Label + " : renseignez un nombre dans les limites indiquées.");
                if (parameter.Type == "integer")
                {
                    if (number != Math.Truncate(number)) throw new ArgumentException(parameter.Label + " : un entier est requis.");
                    value = (long)number;
                }
                else value = number;
            }
            arguments[parameter.Name] = value;
        }
        return new("run_plugin", _plugin.Name, new Dictionary<string, object>
        {
            ["plugin_id"] = _plugin.Id, ["arguments"] = arguments
        }) { Masks = _availableMasks.Where(m => _pluginMasks.GetValueOrDefault(_plugin.Id)?.Contains(m.Id) == true
            && m.Width == _maskWidth && m.Height == _maskHeight).ToArray() };
    }

    private IReadOnlyList<ImageMask> _availableMasks = [];
    private int _maskWidth, _maskHeight;
    private readonly Dictionary<string, HashSet<string>> _pluginMasks = new();

    public void SetMasks(ImageDocument? image)
    {
        _availableMasks = image?.Masks ?? [];
        _maskWidth = image?.Width ?? 0; _maskHeight = image?.Height ?? 0;
        RefreshMaskChoices();
    }

    private void RefreshMaskChoices()
    {
        PluginMaskChoices.Children.Clear();
        if (_plugin is null) return;
        if (!_pluginMasks.TryGetValue(_plugin.Id, out var selected))
            _pluginMasks[_plugin.Id] = selected = [];
        foreach (var mask in _availableMasks)
        {
            bool compatible = mask.Width == _maskWidth && mask.Height == _maskHeight;
            var check = new CheckBox { Content = mask.Label + (compatible ? "" : " - different size"),
                IsChecked = compatible && selected.Contains(mask.Id), IsEnabled = compatible, Tag = mask.Id };
            AutomationProperties.SetAutomationId(check, "PluginMask_" + mask.Id);
            check.Checked += (_, _) => selected.Add(mask.Id);
            check.Unchecked += (_, _) => selected.Remove(mask.Id);
            PluginMaskChoices.Children.Add(check);
        }
        if (_availableMasks.Count == 0)
            PluginMaskChoices.Children.Add(new TextBlock { Text = "No masks on this image.", FontSize = 11 });
    }

    private static object PluginDefault(PluginParameter parameter) => parameter.Type switch
    {
        "hsv" or "color_hsv" or "color_bgra" => parameter.Default.EnumerateArray().Select(v => v.GetInt32()).ToArray(),
        "text" => parameter.Default.GetString() ?? "",
        "boolean" => parameter.Default.GetBoolean(),
        _ => parameter.Default.GetDouble()
    };

    private bool IsPluginParameterVisible(PluginParameter parameter) => parameter.VisibleWhen is null
        || parameter.VisibleWhen.All(condition => _pluginInputs.GetValueOrDefault(condition.Key) is ComboBox choice
            && choice.SelectedItem is string selected && condition.Value.Contains(selected));

    private void RefreshPluginFields()
    {
        if (_plugin is null) return;
        foreach (var parameter in _plugin.Parameters)
        {
            _pluginFieldContainers[parameter.Name].Visibility = Visible(IsPluginParameterVisible(parameter));
            if (parameter.PaletteChannel is { } reference && _pluginInputs[parameter.Name] is PluginColorInput color
                && _pluginInputs.GetValueOrDefault(reference) is ComboBox choice)
                color.SetComponent(choice.SelectedItem as string ?? "gray");
        }
    }

    public void SetPluginArguments(IReadOnlyDictionary<string, object> arguments, bool reset = false)
    {
        if (reset && _plugin is not null)
            SetPluginArguments(_plugin.Parameters.Where(p => p.Default.ValueKind != JsonValueKind.Undefined)
                .ToDictionary(p => p.Name, PluginDefault));
        foreach (var (name, value) in arguments)
        {
            if (!_pluginInputs.TryGetValue(name, out var input)) continue;
            if (input is ComboBox choice) choice.SelectedItem = value;
            else if (input is PluginColorInput color) color.Value = value;
            else if (input is TextBox text) text.Text = Convert.ToString(value) ?? "";
            else if (input is CheckBox check) check.IsChecked = Convert.ToBoolean(value);
            else if (input is NumberBox number) number.Value = Convert.ToDouble(value);
        }
        RefreshPluginFields();
    }

    public void SetApplyLabel(string text) => ApplyButton.Content = text;

    internal void ChooseMask(string id, bool selected)
    {
        var check = PluginMaskChoices.Children.OfType<CheckBox>().Single(c => Equals(c.Tag, id));
        if (!check.IsEnabled) throw new InvalidOperationException("Mask is not compatible.");
        check.IsChecked = selected;
    }

    internal void VerifyMaskColorInputs()
    {
        if (_pluginInputs["channel"] is not ComboBox channels || channels.Items.Count != 6)
            throw new InvalidOperationException("Channel must be a select containing HSV.");
        channels.SelectedItem = "red";
        var lower = (PluginColorInput)_pluginInputs["lower"];
        lower.ChooseBarValue(67);
        var arguments = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
        if (!Equals(arguments["lower"], 67L) || arguments.ContainsKey("lower_hsv"))
            throw new InvalidOperationException("Value bar did not set the selected channel.");
        channels.SelectedItem = "hsv";
        var hsv = (PluginColorInput)_pluginInputs["lower_hsv"];
        if (_pluginFieldContainers["lower"].Visibility != Visibility.Collapsed
            || _pluginFieldContainers["lower_hsv"].Visibility != Visibility.Visible)
            throw new InvalidOperationException("HSV did not switch the bound editors.");
        lower.Value = double.NaN;
        hsv.Value = new[] { 137, 0, 0 };
        if (!((int[])hsv.Value).SequenceEqual(new[] { 137, 0, 0 }))
            throw new InvalidOperationException("Palette synchronization lost hue at black.");
        hsv.ChooseColor(Windows.UI.Color.FromArgb(255, 0, 255, 0));
        arguments = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
        if (!((int[])arguments["lower_hsv"]).SequenceEqual(new[] { 60, 255, 255 })
            || arguments.ContainsKey("lower"))
            throw new InvalidOperationException("HSV palette or inactive bound validation failed.");
        hsv.Value = new[] { 180, 0, 0 };
        bool rejected = false;
        try { GetPluginOptions(); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Out-of-range HSV accepted.");
        SetPluginArguments(new Dictionary<string, object>(), reset: true);
    }

    internal void VerifyPublicColorInputs()
    {
        SelectPlugin(new PluginDescriptor("color_probe", "Colors", "", [
            new("accent", "color_hsv", "Accent", "", false, JsonSerializer.SerializeToElement(new[] { 0, 0, 0 })),
            new("tint", "color_bgra", "Tint", "", false, JsonSerializer.SerializeToElement(new[] { 11, 22, 33, 44 }))]));
        if (_pluginInputs["accent"] is not PluginColorInput hsv || _pluginInputs["tint"] is not PluginColorInput bgra)
            throw new InvalidOperationException("Public color types did not create palettes.");
        hsv.ChooseColor(Windows.UI.Color.FromArgb(255, 0, 0, 255));
        bgra.ChooseColor(Windows.UI.Color.FromArgb(88, 77, 66, 55));
        var arguments = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
        if (!((int[])arguments["accent"]).SequenceEqual(new[] { 120, 255, 255 })
            || !((int[])arguments["tint"]).SequenceEqual(new[] { 55, 66, 77, 88 }))
            throw new InvalidOperationException("Palette channels or BGRA alpha were reordered.");
        bgra.Value = new[] { 55, 66, 77, 0 };
        if (!((int[])bgra.Value).SequenceEqual(new[] { 55, 66, 77, 0 }))
            throw new InvalidOperationException("Transparent BGRA lost its color channels.");
        bgra.Value = new[] { 0, 0, 0, 256 };
        bool rejected = false;
        try { GetPluginOptions(); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Invalid BGRA alpha accepted.");
    }

    internal void VerifyScalarPluginInputs()
    {
        var descriptor = new PluginDescriptor("form_probe", "Form probe", "", [
            new("note", "text", "Note", "", false, JsonSerializer.SerializeToElement("hello")),
            new("enabled", "boolean", "Enabled", "", false, JsonSerializer.SerializeToElement(true)),
            new("count", "integer", "Count", "", true, default, 1, 10)]);
        SelectPlugin(descriptor);
        if (_pluginInputs["note"] is not TextBox || _pluginInputs["enabled"] is not CheckBox)
            throw new InvalidOperationException("Text/boolean plugin fields have incorrect controls.");
        var count = (NumberBox)_pluginInputs["count"];
        foreach (double invalid in new[] { double.NaN, 1.5, 11 })
        {
            count.Value = invalid;
            try { GetPluginOptions(); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid plugin number was accepted.");
        }
        count.Value = 3;
        var arguments = (Dictionary<string, object>)GetPluginOptions().Parameters["arguments"];
        if (!Equals(arguments["note"], "hello") || !Equals(arguments["enabled"], true) || !Equals(arguments["count"], 3L))
            throw new InvalidOperationException("Plugin form values do not match constructor arguments.");
    }

    internal void VerifyPluginForm()
    {
        if (_plugin is null) throw new InvalidOperationException("Missing selected plugin.");
        foreach (var parameter in _plugin.Parameters)
            if ((parameter.Type is "number" or "integer") != (_pluginInputs[parameter.Name] is NumberBox || parameter.Type == "integer" && _pluginInputs[parameter.Name] is PluginColorInput))
                throw new InvalidOperationException("Plugin input type does not match the manifest.");
        if (!ApplyButton.IsEnabled) throw new InvalidOperationException("Plugin Apply is disabled.");
    }
}
