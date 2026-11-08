using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Views;

public sealed partial class PluginCatalog : UserControl
{
    private bool _syncing;
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImagineNeo", "plugin-view.txt");
    public event EventHandler<PluginDescriptor>? PluginSelected;
    public PluginDescriptor? SelectedPlugin => Cards.SelectedItem as PluginDescriptor;

    public PluginCatalog()
    {
        InitializeComponent();
        try { ViewMode.SelectedIndex = File.Exists(PreferencePath) && File.ReadAllText(PreferencePath) == "dropdown" ? 1 : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void SetCatalog(IReadOnlyList<PluginDescriptor> plugins, IReadOnlyList<string> errors)
    {
        string? selectedId = SelectedPlugin?.Id;
        _syncing = true;
        try
        {
            Cards.ItemsSource = Dropdown.ItemsSource = plugins;
            Cards.SelectedItem = Dropdown.SelectedItem = plugins.FirstOrDefault(p => p.Id == selectedId);
        }
        finally { _syncing = false; }
        EmptyHint.Text = plugins.Count == 0 ? "Aucun plugin disponible." : "";
        EmptyHint.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Errors.Message = string.Join("\n", errors); Errors.IsOpen = errors.Count > 0;
    }

    public void ClearSelection()
    {
        _syncing = true;
        try { Cards.SelectedItem = Dropdown.SelectedItem = null; }
        finally { _syncing = false; }
    }

    private void PluginChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ((Microsoft.UI.Xaml.Controls.Primitives.Selector)sender).SelectedItem is not PluginDescriptor plugin) return;
        _syncing = true;
        try { Cards.SelectedItem = Dropdown.SelectedItem = plugin; }
        finally { _syncing = false; }
        PluginSelected?.Invoke(this, plugin);
    }

    private void PluginInvoked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PluginDescriptor plugin) PluginSelected?.Invoke(this, plugin);
    }

    private void ViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Cards is null || Dropdown is null) return;
        Cards.Visibility = ViewMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        Dropdown.Visibility = ViewMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, ViewMode.SelectedIndex == 1 ? "dropdown" : "cards");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal void VerifyPresentations(PluginDescriptor plugin)
    {
        plugin = ((IReadOnlyList<PluginDescriptor>)Cards.ItemsSource).Single(p => p.Id == plugin.Id);
        int previousMode = ViewMode.SelectedIndex;
        ViewMode.SelectedIndex = 1;
        Dropdown.SelectedItem = plugin;
        if (!ReferenceEquals(Cards.SelectedItem, plugin) || Dropdown.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Plugin dropdown did not synchronize selection.");
        ViewMode.SelectedIndex = 0;
        if (!ReferenceEquals(Cards.SelectedItem, plugin) || Cards.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Plugin cards lost selection.");
        ViewMode.SelectedIndex = previousMode;
    }
}
