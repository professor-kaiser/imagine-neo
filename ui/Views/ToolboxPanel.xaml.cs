using Microsoft.UI.Xaml.Controls;
using PhotoLab.UI.Models;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Views;

public sealed partial class ToolboxPanel : UserControl
{
    public event EventHandler<EditorTool>? ToolSelected;
    public event EventHandler<PluginDescriptor>? PluginSelected;
    public string SelectedOperation => (ToolList.SelectedItem as EditorTool)?.Operation ?? "to_grayscale";

    public ToolboxPanel()
    {
        InitializeComponent();
        Plugins.PluginSelected += (_, plugin) => { ToolList.SelectedItem = null; PluginSelected?.Invoke(this, plugin); };
        ToolList.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is not EditorTool tool || args.InRecycleQueue) return;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, tool.Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, tool.Operation);
        };
        ToolsSource.Source = EditorTool.Groups;
        SelectOperation("to_grayscale");
    }

    public void SelectOperation(string operation)
    {
        if (operation == "run_plugin") { CatalogTabs.SelectedIndex = 1; return; }
        Plugins.ClearSelection();
        ToolList.SelectedItem = EditorTool.Find(operation);
        CatalogTabs.SelectedIndex = 0;
    }
    public void SetPlugins(IReadOnlyList<PluginDescriptor> plugins, IReadOnlyList<string> errors) => Plugins.SetCatalog(plugins, errors);
    internal void VerifyPluginPresentations(PluginDescriptor plugin) => Plugins.VerifyPresentations(plugin);

    private void ToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is EditorTool tool) ToolSelected?.Invoke(this, tool);
    }

    // Clicking the active tool also returns from Hand/Selection mode to its inspector.
    private void ToolInvoked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EditorTool tool) ToolSelected?.Invoke(this, tool);
    }
}
