using Microsoft.UI.Xaml.Controls;
using Windows.UI;
namespace PhotoLab.UI.Controls.ColorPicker;
public sealed partial class ColorPickerPanel : UserControl
{
    public event EventHandler? SelectedColorChanged;
    public Color SelectedColor { get => Picker.Color; set => Picker.Color = value; }
    public ColorPickerPanel() { InitializeComponent(); Refresh(); }
    private void OnColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, ColorChangedEventArgs args) { Refresh(); SelectedColorChanged?.Invoke(this, EventArgs.Empty); }
    private void Refresh()
    {
        if (ColorValues is null) return;
        var c = Picker.Color;
        ColorValues.Text = $"RGBA({c.R}, {c.G}, {c.B}, {c.A})";
    }
}
