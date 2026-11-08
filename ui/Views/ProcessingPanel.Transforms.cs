using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Views;

public sealed partial class ProcessingPanel
{
    private bool _settingTransform;
    private int _imageWidth, _imageHeight;
    private string? _transformError;
    public bool IsTransform => ImageTransform.ModeFor(SelectedOperation) != TransformMode.None;
    public event EventHandler? TransformParametersChanged;
    public event EventHandler? FitTransformRequested;
    public ImageTransform GetTransformValues()
    {
        var values = new ImageTransform(AngleInput.Value, ScaleInput.Value / 100);
        values.Validate(); return values;
    }
    public void SetTransformValues(ImageTransform values)
    {
        _settingTransform = true;
        try { AngleInput.Value = values.AngleDegrees; ScaleInput.Value = values.ScaleFactor * 100; }
        finally { _settingTransform = false; }
        RefreshTransformInfo();
    }
    public void SetImageDimensions(int width, int height)
    { _imageWidth = width; _imageHeight = height; RefreshTransformInfo(); }
    private void TransformValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_settingTransform || AngleInput is null || ScaleInput is null || TransformSizeText is null) return;
        RefreshTransformInfo();
        if (_transformError is null) TransformParametersChanged?.Invoke(this, EventArgs.Empty);
    }
    private void AnglePresetClicked(object sender, RoutedEventArgs e) =>
        AngleInput.Value = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    private void ScalePresetClicked(object sender, RoutedEventArgs e) =>
        ScaleInput.Value = double.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
    private void ResetTransformClicked(object sender, RoutedEventArgs e)
    { SetTransformValues(ImageTransform.Identity); TransformParametersChanged?.Invoke(this, EventArgs.Empty); }
    private void FitTransformClicked(object sender, RoutedEventArgs e) => FitTransformRequested?.Invoke(this, EventArgs.Empty);
    private void RefreshTransformInfo()
    {
        if (TransformSizeText is null) return;
        _transformError = null;
        try
        {
            var values = GetTransformValues().ForMode(ImageTransform.ModeFor(SelectedOperation));
            var size = _imageWidth > 0 ? values.ValidatedOutputSize(_imageWidth, _imageHeight) : (Width: 0, Height: 0);
            TransformSizeText.Text = _imageWidth > 0 ? $"Preview output: {size.Width} × {size.Height} px" : "Choose an image for a preview.";
        }
        catch (ArgumentException error) { _transformError = error.Message; TransformSizeText.Text = error.Message; }
        if (IsTransform && ApplyButton is not null) ApplyButton.IsEnabled = _hasImage && !_busy && _transformError is null;
    }
    internal void VerifyTransformPresets()
    {
        foreach (double angle in new double[] { -90, -45, 0, 45, 90, 180 })
        {
            AnglePresetClicked(new Button { Tag = angle.ToString(CultureInfo.InvariantCulture) }, new RoutedEventArgs());
            if (AngleInput.Value != angle) throw new InvalidOperationException("Angle preset did not update the input.");
        }
        foreach (double scale in new double[] { 25, 50, 100, 200 })
        {
            ScalePresetClicked(new Button { Tag = scale.ToString(CultureInfo.InvariantCulture) }, new RoutedEventArgs());
            if (ScaleInput.Value != scale) throw new InvalidOperationException("Scale preset did not update the input.");
        }
        AngleInput.Value = double.NaN;
        if (ApplyButton.IsEnabled) throw new InvalidOperationException("Invalid transform input left Apply enabled.");
        ResetTransformClicked(this, new RoutedEventArgs());
    }
    internal void SetSmokeTransformInputs(double angle, double percent)
    { AngleInput.Value = angle; ScaleInput.Value = percent; }
    private void RefreshTransformControls()
    {
        var mode = ImageTransform.ModeFor(SelectedOperation);
        TransformPanel.Visibility = Visible(IsTransform);
        AngleControls.Visibility = Visible(mode is TransformMode.Rotation or TransformMode.RotationScale);
        ScaleControls.Visibility = Visible(mode is TransformMode.Scale or TransformMode.RotationScale);
        TransformGestureHint.Text = mode switch
        {
            TransformMode.Rotation => "Drag the round handle above the image to rotate. Positive angles turn clockwise.",
            TransformMode.Scale => "Drag a corner handle to resize. Proportions are locked; 100% is the original size.",
            _ => "Drag the round handle to rotate, or a corner to resize. Proportions are locked."
        };
        RefreshTransformInfo();
    }
}
