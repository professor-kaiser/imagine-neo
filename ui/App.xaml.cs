using Microsoft.UI.Xaml;

namespace PhotoLab.UI;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US";
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
