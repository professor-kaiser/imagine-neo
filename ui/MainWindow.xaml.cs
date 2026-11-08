using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using PhotoLab.UI.Contracts;
using PhotoLab.UI.Models;
using PhotoLab.UI.Services;
using PhotoLab.UI.ViewModels;

namespace PhotoLab.UI;

public sealed partial class MainWindow : Window
{
    private readonly NamedPipeClient _client = new();
    private readonly BackendProcessService _backend;
    private readonly ImageImportService _import;
    private readonly ImageDocumentService _documents;
    private readonly ImageProcessingService _processing;
    private readonly NativeFileDropService _nativeDrop;
    private Task _processingTask = Task.CompletedTask;
    private string? _lastOperationCode;
    private bool _busy, _loaded, _closing;
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        _backend = new(_client); _documents = new(_client); _import = new(_client, new());
        _processing = new(_client, new(), new());
        ViewModel = new(_documents);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "imagine-neo.ico"));
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            var background = Windows.UI.Color.FromArgb(255, 33, 35, 38);
            titleBar.BackgroundColor = titleBar.ButtonBackgroundColor = titleBar.InactiveBackgroundColor = titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ForegroundColor = titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 166, 171, 175);
        }
        Root.DataContext = ViewModel;
        // Contextual sections may not load until first shown. Keep their source model ready.
        SourceList.ItemsSource = ViewModel.Images;
        MaskSource.ItemsSource = ViewModel.Images;
        Root.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 1240;
            ToolColumn.Width = new GridLength(compact ? 182 : 212);
            InspectorColumn.Width = new GridLength(compact ? 284 : 320);
            BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1480, workArea.Width - 40), Math.Min(960, workArea.Height - 40)));
        _nativeDrop = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _nativeDrop.FilesDropped += async (_, paths) => await ImportDroppedPathsAsync(paths);
        _nativeDrop.DropFailed += (_, error) => ReportError(error);
        EditorCanvas.ViewChanged += (_, _) => UpdateViewport();
        EditorCanvas.SelectionChanged += (_, _) => UpdateSelection();
        EditorCanvas.DrawingChanged += (_, _) => UpdateEditingControls();
        EditorCanvas.DrawingCommitted += (_, _) => { if (EditorCanvas.Document is { } image) _history.DiscardRedo(image); };
        WorkingColor.SelectedColorChanged += (_, _) => UpdateBrushColor();
        UpdateBrushColor();
        EditorCanvas.RenderFailed += (_, message) => ViewModel.Status = "Rendering error: " + message;
        SourceCanvas.FixedSelectionSize = true;
        SourceCanvas.SelectionChanged += (_, _) => UpdateSourceText();
        Toolbox.ToolSelected += (_, tool) =>
        {
            if (ProcessingTools.SelectedOperation != tool.Operation) ProcessingTools.SelectOperation(tool.Operation);
            else UpdateToolInspector();
        };
        Toolbox.PluginSelected += (_, plugin) => ProcessingTools.SelectPlugin(plugin);
        ProcessingTools.SelectedOperationChanged += (_, _) => UpdateToolInspector();
        ProcessingTools.TransformParametersChanged += TransformParametersChanged;
        ProcessingTools.FitTransformRequested += (_, _) => EditorCanvas.Fit();
        EditorCanvas.TransformChanged += CanvasTransformChanged;
        ProcessingTools.ParameterContextChanged += (_, _) => UpdateContextSections();
        ViewModel.Images.CollectionChanged += (_, _) => UpdateLibrary();
        UpdateToolInspector();
        EditorCanvas.PixelPicked += (_, point) =>
        {
            ProcessingTools.SetPixelCoordinates((int)point.X, (int)point.Y);
            ProcessingTools.RequestApply();
        };
        AppWindow.Closing += WindowClosing;
    }

    private async void RootLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ConnectAsync();
        string[] args = Environment.GetCommandLineArgs();
        int smoke = Array.IndexOf(args, "--smoke-test");
        if (smoke >= 0 && args.Length > smoke + 1) await RunUiSmokeAsync(args[smoke + 1]);
        else if (Environment.GetEnvironmentVariable("PHOTOLAB_SMOKE_REPORT") is { Length: > 0 } report) await RunUiSmokeAsync(report);
    }
    private async Task ConnectAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await _backend.EnsureReadyAsync();
            var catalog = await _client.SendAsync(new CommandMessage { MessageType = "list_plugins" });
            var plugins = catalog.Plugins ?? [];
            Toolbox.SetPlugins(plugins.Where(p => p.OutputKind == "image").ToArray(), catalog.PluginErrors ?? []);
            SetMaskGenerators(plugins);
            if (ProcessingTools.IsPlugin)
            {
                var current = plugins.FirstOrDefault(p => p.Id == ProcessingTools.SelectedPluginId);
                if (current is not null) ProcessingTools.SelectPlugin(current);
                else ProcessingTools.SelectOperation("to_grayscale");
            }
            // Restore original buffers if a previous backend process was restarted.
            var stored = await _client.SendAsync(new CommandMessage { MessageType = "list_images" });
            var identities = (stored.Images ?? []).Select(image => image.ImageId).ToHashSet();
            foreach (var image in ViewModel.Images.Where(image => !identities.Contains(image.Id)))
            {
                using var lease = new SharedMemoryService().CreateImage(image.Pixels, image.Width, image.Height);
                var response = await _client.SendAsync(new CommandMessage { MessageType = "import_image", ImageId = image.Id,
                    FileName = image.FileName, BatchId = Guid.NewGuid().ToString("N"), PixelBuffer = lease.Descriptor });
                if (response.Sha256 != image.Sha256) throw new InvalidDataException("Could not restore the image pixels.");
            }
            ViewModel.BackendStatus = "● Python connected";
            ViewModel.Status = "Ready. Import images to get started.";
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }
    private async void ReconnectClicked(object sender, RoutedEventArgs e) => await ConnectAsync();
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (string extension in ImageDropService.SupportedExtensions) picker.FileTypeFilter.Add(extension);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await ImportBatchAsync(files);
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }
    private void RootDragOver(object sender, DragEventArgs e)
    {
        bool accepted = !_busy && ImageDropService.CanRead(e.DataView);
        e.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropOverlay.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        if (accepted)
        {
            e.DragUIOverride.Caption = "Import images into Imagine Neo";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = false;
        }
        e.Handled = true;
    }
    private void RootDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(Root);
        if (point.X <= 0 || point.Y <= 0 || point.X >= Root.ActualWidth || point.Y >= Root.ActualHeight || ReferenceEquals(e.OriginalSource, Root))
            DropOverlay.Visibility = Visibility.Collapsed;
    }
    private async void RootDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Collapsed;
        e.AcceptedOperation = DataPackageOperation.None;
        if (_busy || !ImageDropService.CanRead(e.DataView)) return;
        var deferral = e.GetDeferral();
        try
        {
            if (await ImportDropAsync(e.DataView)) e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception error) { ReportError(error); }
        finally { deferral.Complete(); }
    }
    private async Task<bool> ImportDropAsync(DataPackageView data)
    {
        if (_busy) return false;
        SetBusy(true);
        try
        {
            var items = await ImageDropService.ReadAsync(data);
            return await ImportBatchAsync(items.OfType<StorageFile>(), items.Count(item => item is not StorageFile)) > 0;
        }
        finally { SetBusy(false); }
    }
    private async Task ImportDroppedPathsAsync(IEnumerable<string> paths)
    {
        if (_busy) { ViewModel.Status = "Please wait for the current operation, then drop your images again."; return; }
        SetBusy(true);
        try
        {
            var files = new List<StorageFile>();
            var errors = new List<string>();
            int folders = 0;
            foreach (string path in paths)
            {
                if (Directory.Exists(path)) { folders++; continue; }
                try { files.Add(await StorageFile.GetFileFromPathAsync(path)); }
                catch (Exception error) { errors.Add($"{Path.GetFileName(path)}: {error.Message}"); }
            }
            await ImportBatchAsync(files, folders);
            if (errors.Count > 0) ViewModel.Status += " " + string.Join(" · ", errors);
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }
    private async Task<int> ImportBatchAsync(IEnumerable<StorageFile> files, int skippedFolders = 0)
    {
        int successes = 0;
        var errors = new List<string>();
        string batch = Guid.NewGuid().ToString("N");
        foreach (var file in files)
        {
            try
            {
                if (!ImageDropService.IsSupported(file))
                    throw new InvalidDataException("Unsupported format. Use PNG, JPG, TIFF, BMP or GIF.");
                ViewModel.Status = "Importing: " + file.Name;
                var document = await _import.ImportAsync(file, batch);
                ViewModel.Images.Add(document); ViewModel.SelectedImage = document;
                successes++;
            }
            catch (Exception error) { errors.Add($"{file.Name} : {error.Message}"); }
        }
        ViewModel.Status = $"{successes} image(s) imported."
            + (skippedFolders > 0 ? $" {skippedFolders} folder(s) skipped; drop image files directly." : "")
            + (errors.Count > 0 ? " Errors: " + string.Join(" · ", errors) : "");
        ViewModel.BackendStatus = _client.IsConnected ? "● Python connected" : "○ Python disconnected";
        return successes;
    }
    private async void ApplyProcessingRequested(object? sender, EventArgs e)
    {
        _processingTask = ApplyOperationAsync();
        await _processingTask;
    }
    private async Task ApplyOperationAsync(bool drawing = false)
    {
        if (_busy || EditorCanvas.Document is not { } source) return;
        if (!drawing && source.Drawing.HasDrawing)
        {
            ViewModel.Status = "Apply or clear the pending drawing before using another image tool.";
            return;
        }
        var selection = drawing || ProcessingTools.IsTransform ? null : EditorCanvas.Selection;
        float zoom = EditorCanvas.Zoom;
        _lastOperationCode = null;
        SetBusy(true);
        try
        {
            var color = WorkingColor.SelectedColor;
            var options = drawing ? new OperationOptions("apply_drawing", "Drawing", new Dictionary<string, object>())
                : ProcessingTools.GetOptions(new(color.R, color.G, color.B, color.A), SourceCanvas.Document, SourceCanvas.Selection);
            byte[]? mask = drawing ? EditorCanvas.GetDrawingMask() : null;
            if (mask is not null && !Enumerable.Range(0, mask.Length / 4).Any(i => mask[i * 4 + 3] != 0))
                throw new ArgumentException("The drawing is empty. Add a visible stroke before applying.");
            if (options.Operation is "replace_selection_with_color" or "replace_selection_with_region" && selection is null)
                throw new ArgumentException("Draw a target selection on the canvas first.");
            ViewModel.Status = options.DisplayName + "…";
            var result = await _processing.RunAsync(source, selection, options, mask);
            if (result.Image is { } image)
            {
                foreach (var published in result.Images ?? new[] { image })
                {
                    _history.Record(source, published);
                    ViewModel.Images.Add(published);
                }
                source.Drawing.Reset();
                if (options.IsTransform) source.TransformDraft = ImageTransform.Identity;
                ImageList.SelectedItem = image;
                ViewModel.SelectedImage = image;
                if (options.IsTransform || image.Width != source.Width || image.Height != source.Height) EditorCanvas.Fit(); else EditorCanvas.SetZoom(zoom);
                ViewModel.Status = options.DisplayName + ": new version added to the library. Original preserved.";
            }
            else
            {
                ViewModel.Palette.Clear();
                if (result.Color is { } sampled)
                {
                    SetWorkingColor(sampled);
                    ViewModel.Palette.Add(new(new(sampled, 1, 1)));
                    ViewModel.AnalysisText = $"Pixel : RGBA({sampled.R}, {sampled.G}, {sampled.B}, {sampled.A})";
                }
                else if (result.Palette is { } palette)
                {
                    foreach (var entry in palette) ViewModel.Palette.Add(new(entry));
                    ViewModel.AnalysisText = $"{palette.Count} colors. Click a color to select it.";
                }
                ViewModel.Status = options.DisplayName + ": result available in the Analysis results panel.";
            }
            ProcessingTools.ShowStatus("Done", ViewModel.Status);
        }
        catch (BackendCommandException error)
        {
            _lastOperationCode = error.Code ?? "error";
            if (error.Code == "not_implemented")
            {
                ViewModel.Status = $"Coming soon: this tool is not available yet. {error.Message}";
                ProcessingTools.ShowStatus("Coming soon", $"This tool does not provide a result yet.");
                if (drawing) ViewModel.Status = "Drawing retained. Apply drawing is not available yet.";
                else if (ProcessingTools.IsTransform) ViewModel.Status = "Preview retained. This transformation is not available yet.";
            }
            else { ReportError(error); ProcessingTools.ShowStatus("Processing error", error.Message); }
        }
        catch (Exception error) { _lastOperationCode = "error"; ReportError(error); ProcessingTools.ShowStatus("Error", error.Message); }
        finally { SetBusy(false); }
    }
    private void SetWorkingColor(ColorRgba color) => WorkingColor.SelectedColor = Windows.UI.Color.FromArgb((byte)color.A, (byte)color.R, (byte)color.G, (byte)color.B);
    private void PaletteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListView)sender).SelectedItem is PaletteEntry entry) SetWorkingColor(entry.Color);
    }
    private async void RemoveClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || ViewModel.SelectedImage is not { } image) return;
        SetBusy(true);
        try
        {
            await _documents.RemoveAsync(image);
            _history.Forget(image);
            foreach (var key in _maskPaintSessions.Keys.Where(key => key.StartsWith(image.Id + "/", StringComparison.Ordinal)).ToArray())
                _maskPaintSessions.Remove(key);
            if (ViewModel.SourceImage == image) ViewModel.SourceImage = null;
            ViewModel.SelectedImage = ViewModel.Images.FirstOrDefault();
            ViewModel.Status = "Image removed from the session and the Python registry.";
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }
    private void ImageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorCanvas is null) return;
        var image = ImageList.SelectedItem as ImageDocument;
        EditorCanvas.Document = image;
        SyncTransformDocument();
        RefreshLayers();
        DocumentTitle.Text = image?.FileName ?? "Your workspace";
        ViewModel.DimensionsText = image?.Dimensions ?? "No image";
        EmptyWorkspace.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = image is not null && !_busy;
        if (image is not null) ImageList.ScrollIntoView(image);
        UpdateEditingControls();
        UpdateSelection();
    }
    private void SourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCanvas is null) return;
        SourceCanvas.Document = SourceList.SelectedItem as ImageDocument;
        UpdateSourceRegion();
    }
    private void UpdateSelection()
    {
        ViewModel.SelectionText = EditorCanvas.Selection?.ToString() ?? "No selection";
        ProcessingTools?.SetContext(EditorCanvas.Document is not null, _busy, EditorCanvas.Selection is not null);
        ContextHint.Text = EditorCanvas.Document is null ? "Choose an image to get started"
            : EditorCanvas.Selection is { } selection ? "Selection · " + selection : "Entire image · Original preserved";
        UpdateTransformContext();
        UpdateSourceRegion();
        UpdateMaskSelectionContext();
    }
    private void UpdateSourceRegion()
    {
        if (SourceCanvas is null) return;
        if (EditorCanvas.Selection is { } target && SourceCanvas.Document is { } source)
        {
            if (target.Width > source.Width || target.Height > source.Height)
            {
                SourceCanvas.Selection = null; ViewModel.SourceText = "The source image is smaller than the target selection."; return;
            }
            var previous = SourceCanvas.Selection;
            SourceCanvas.Selection = new ImageRect(0, 0, target.Width, target.Height)
                .MoveTo(previous?.X ?? 0, previous?.Y ?? 0, source.Width, source.Height);
        }
        else SourceCanvas.Selection = null;
        UpdateSourceText();
    }
    private void UpdateSourceText() => ViewModel.SourceText = SourceCanvas.Selection is { } source
        ? $"Source: {source}. Move the rectangle; its size matches the target."
        : "Choose a source image and a target selection.";
    private void UpdateViewport()
    {
        ViewModel.ZoomText = $"{EditorCanvas.Zoom * 100:0.#} %";
        var point = EditorCanvas.CursorPixel;
        ViewModel.CoordinatesText = $"X {Math.Floor(point.X):0} · Y {Math.Floor(point.Y):0}";
    }
    private void FitClicked(object sender, RoutedEventArgs e) => EditorCanvas.Fit();
    private void ActualSizeClicked(object sender, RoutedEventArgs e) => EditorCanvas.SetZoom(1);
    private void SelectToolClicked(object sender, RoutedEventArgs e)
    {
        bool maskContext = InspectorTabs.SelectedIndex == 1;
        if (maskContext && IsPaintMask)
        {
            MaskPaintTool.SelectedIndex = 2;
            UpdateLayerControls();
            return;
        }
        ExitDrawingMode(); DisableTransformPreview();
        PanButton.IsChecked = false; SelectButton.IsChecked = true;
        EditorCanvas.PanMode = false; EditorCanvas.PickMode = false;
        ActiveToolLabel.Text = "Rectangular selection"; ActiveToolIcon.Glyph = "\uEF20";
        if (!maskContext) InspectorTabs.SelectedIndex = 0;
        SelectionSection.IsExpanded = true;
    }
    private void PanToolClicked(object sender, RoutedEventArgs e)
    {
        ExitDrawingMode(); DisableTransformPreview();
        PanButton.IsChecked = true; SelectButton.IsChecked = false;
        EditorCanvas.PanMode = true; EditorCanvas.PickMode = false;
        ActiveToolLabel.Text = "Hand · Pan canvas"; ActiveToolIcon.Glyph = "\uE7C9";
    }
    private void ShowLayersClicked(object sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 1;
    private void ShowHistoryClicked(object sender, RoutedEventArgs e) => InspectorTabs.SelectedIndex = 2;
    private void UpdateToolInspector()
    {
        ExitDrawingMode();
        string operation = ProcessingTools.SelectedOperation;
        Toolbox.SelectOperation(operation);
        SelectButton.IsChecked = PanButton.IsChecked = false;
        EditorCanvas.PanMode = false; EditorCanvas.PickMode = operation == "pick_color";
        ActiveToolLabel.Text = ProcessingTools.SelectedLabel;
        ActiveToolIcon.Glyph = ProcessingTools.IsPlugin ? "\uEA86" : EditorTool.Find(operation).Glyph;
        InspectorTabs.SelectedIndex = 0;
        ConfigureTransformTool();
        UpdateContextSections(); UpdateSelection();
    }
    private void UpdateContextSections()
    {
        if (_drawingMode) return;
        string operation = ProcessingTools.SelectedOperation;
        SelectionSection.Visibility = ProcessingTools.IsTransform ? Visibility.Collapsed : Visibility.Visible;
        ColorSection.Visibility = ProcessingTools.UsesWorkingColor ? Visibility.Visible : Visibility.Collapsed;
        SourceSection.Visibility = operation == "replace_selection_with_region" ? Visibility.Visible : Visibility.Collapsed;
        AnalysisSection.Visibility = operation is "pick_color" or "extract_palette" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateLibrary()
    {
        LibraryCount.Text = $"{ViewModel.Images.Count} images";
        EmptyLibrary.Visibility = ViewModel.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        EditorCanvas.Selection = null;
        if (ViewModel.SelectedImage is { } image) image.Selection = null;
        UpdateSelection();
    }
    private void SetBusy(bool value)
    {
        _busy = value; ImportButton.IsEnabled = EmptyImportButton.IsEnabled = !value;
        if (value) EditorCanvas.CancelDrawing();
        EditorCanvas.IsHitTestVisible = !value;
        DrawButton.IsEnabled = DrawingToolPicker.IsEnabled = BrushSizeInput.IsEnabled = !value;
        UpdateEditingControls();
        RemoveButton.IsEnabled = !value && EditorCanvas.Document is not null;
        Toolbox.IsEnabled = SelectButton.IsEnabled = PanButton.IsEnabled = !value;
        ImageList.IsEnabled = SourceList.IsEnabled = WorkingColor.IsEnabled = !value;
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value) DropOverlay.Visibility = Visibility.Collapsed;
        ProcessingTools.SetContext(EditorCanvas.Document is not null, value, EditorCanvas.Selection is not null);
    }
    private void ReportError(Exception error)
    {
        ViewModel.Status = error.Message;
        ViewModel.BackendStatus = _client.IsConnected ? "● Python connected" : "○ Backend unavailable";
    }
    private async void WindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true; _closing = true;
        await _backend.StopOwnedAsync();
        _nativeDrop.Dispose();
        _client.Dispose(); _backend.Dispose(); EditorCanvas.Dispose(); SourceCanvas.Dispose();
        Close();
    }
    private async Task RunUiSmokeAsync(string reportPath)
    {
        // Uses the same decoder, import service, collection and selection handler as interactive imports.
        try
        {
            string root = BackendProcessService.FindProjectRoot();
            var files = new List<StorageFile>();
            foreach (string file in new[] { "sample-alpha.png", "sample-color.png" })
                files.Add(await StorageFile.GetFileFromPathAsync(Path.Combine(root, "artifacts", "fixtures", file)));
            // Exercise the same StorageItems data package and asynchronous path as a drop,
            // including mixed valid files, an unsupported file, a corrupt image and a folder.
            string invalidPath = Path.Combine(root, "artifacts", "fixtures", "invalid-drop.png");
            await File.WriteAllTextAsync(invalidPath, "This is intentionally not a PNG image.");
            var droppedItems = new List<IStorageItem> { files[0],
                await StorageFile.GetFileFromPathAsync(Path.Combine(root, "README.md")),
                await StorageFile.GetFileFromPathAsync(invalidPath),
                await StorageFolder.GetFolderFromPathAsync(Path.Combine(root, "artifacts", "fixtures")), files[1] };
            var drop = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            drop.SetStorageItems(droppedItems);
            if (!ImageDropService.CanRead(drop.GetView()) || !await ImportDropAsync(drop.GetView()))
                throw new InvalidOperationException("The storage-item drop was rejected: " + ViewModel.Status);
            if (ViewModel.Images.Count != 2) throw new InvalidOperationException(ViewModel.Status);
            if (!ViewModel.Status.Contains("README.md") || !ViewModel.Status.Contains("invalid-drop.png") || !ViewModel.Status.Contains("folder(s) skipped"))
                throw new InvalidOperationException("Mixed drop errors were not reported.");
            if (EmptyWorkspace.Visibility != Visibility.Collapsed || LibraryCount.Text != "2 images")
                throw new InvalidOperationException("The workspace did not update after import.");
            var textDrop = new DataPackage(); textDrop.SetText("not an image");
            if (ImageDropService.CanRead(textDrop.GetView())) throw new InvalidOperationException("Text was accepted as a file drop.");
            var stored = await _client.SendAsync(new CommandMessage { MessageType = "list_images" });
            foreach (var image in ViewModel.Images)
                if (stored.Images?.SingleOrDefault(record => record.ImageId == image.Id)?.Sha256 != image.Sha256)
                    throw new InvalidDataException("The Python registry does not match the UI library.");
            var source = ViewModel.Images[0];
            ImageList.SelectedItem = source;
            ViewModel.SelectedImage = source;
            var region = new ImageRect(3, 4, 20, 16);
            EditorCanvas.Selection = region;
            source.Selection = region;
            SourceList.SelectedIndex = 0;
            UpdateSelection();
            EditorCanvas.SetZoom(2);
            if (EditorCanvas.Selection != new ImageRect(3, 4, 20, 16) || SourceCanvas.Selection?.Width != 20)
                throw new InvalidOperationException("Incorrect selection after zooming or choosing a source.");
            if (!ProcessingTools.RequestApply()) throw new InvalidOperationException("The Grayscale button is disabled.");
            await _processingTask;
            if (ViewModel.Images.Count != 3) throw new InvalidOperationException(ViewModel.Status);
            var partialResult = ViewModel.SelectedImage!;
            VerifyGrayscaleResult(source, partialResult, region);
            await Task.Delay(300);
            if (EditorCanvas.RenderedImageId != partialResult.Id || EditorCanvas.Selection != region)
                throw new InvalidOperationException("The partial result or its selection is not displayed.");

            ImageList.SelectedItem = source;
            ViewModel.SelectedImage = source;
            ClearSelectionClicked(this, new RoutedEventArgs());
            EditorCanvas.SetZoom(2);
            if (!ProcessingTools.RequestApply()) throw new InvalidOperationException("The Grayscale button is disabled.");
            await _processingTask;
            if (ViewModel.Images.Count != 4) throw new InvalidOperationException(ViewModel.Status);
            VerifyGrayscaleResult(source, ViewModel.SelectedImage!, null);
            var fullResult = ViewModel.SelectedImage!;
            var operationOutcomes = new Dictionary<string, string> { ["to_grayscale"] = "ok" };
            bool fixtureMode = Environment.GetEnvironmentVariable("PHOTOLAB_TEST_FIXTURES") == "1";
            var expectedErrors = JsonSerializer.Deserialize<Dictionary<string, string>>(
                Environment.GetEnvironmentVariable("PHOTOLAB_TEST_EXPECTED_ERRORS") ?? "{}") ?? [];
            var verifiedErrors = new Dictionary<string, string>();
            foreach (string operation in new[] { "apply_blur", "apply_border", "apply_contours", "apply_color_separation",
                         "replace_selection_with_color", "replace_selection_with_region", "pick_color", "extract_palette", "apply_to_selection" })
            {
                ImageList.SelectedItem = source;
                ViewModel.SelectedImage = source;
                EditorCanvas.Selection = region;
                source.Selection = region;
                SourceList.SelectedIndex = 1;
                UpdateSelection();
                ProcessingTools.SelectOperation(operation);
                if (Toolbox.SelectedOperation != operation || ActiveToolLabel.Text != ProcessingTools.SelectedLabel)
                    throw new InvalidOperationException("The toolbox and inspector are out of sync.");
                if ((SourceSection.Visibility == Visibility.Visible) != (operation == "replace_selection_with_region"))
                    throw new InvalidOperationException("The source inspector is not contextual.");
                ProcessingTools.SetPixelCoordinates(5, 6);
                int before = ViewModel.Images.Count;
                if (!ProcessingTools.RequestApply()) throw new InvalidOperationException($"The {operation} control is not ready.");
                await _processingTask;
                operationOutcomes[operation] = _lastOperationCode ?? "ok";
                if (expectedErrors.TryGetValue(operation, out string? expectedCode))
                {
                    if (fixtureMode || _lastOperationCode != expectedCode || ViewModel.Images.Count != before || !_client.IsConnected)
                        throw new InvalidOperationException($"{operation}: expected error {expectedCode}, got {_lastOperationCode ?? "ok"}. {ViewModel.Status}");
                    if (expectedCode == "opencv_error" && !ViewModel.Status.Contains("OpenCV"))
                        throw new InvalidOperationException("The OpenCV error details are not displayed.");
                    verifiedErrors[operation] = ViewModel.Status;
                    await _client.SendAsync(new CommandMessage { MessageType = "ping" });
                    continue;
                }
                if (_lastOperationCode is not null && _lastOperationCode != "not_implemented")
                    throw new InvalidOperationException($"{operation} : {ViewModel.Status}");
                if (_lastOperationCode == "not_implemented")
                {
                    if (fixtureMode || ViewModel.Images.Count != before || !_client.IsConnected)
                        throw new InvalidOperationException($"The {operation} integration failed.");
                }
                else if (operation is "pick_color" or "extract_palette")
                {
                    if (ViewModel.Images.Count != before || ViewModel.Palette.Count == 0)
                        throw new InvalidOperationException($"Missing {operation} analysis result.");
                    if (fixtureMode && operation == "pick_color" && WorkingColor.SelectedColor != Windows.UI.Color.FromArgb(109, 71, 83, 97))
                        throw new InvalidOperationException("The sampled pixel did not update the color picker.");
                    if (fixtureMode && operation == "extract_palette" && ViewModel.Palette[0].Color != new ColorRgba(31, 47, 59, 255))
                        throw new InvalidOperationException("The returned palette is not displayed.");
                }
                else if (ViewModel.Images.Count != before + 1 || ViewModel.SelectedImage == source)
                    throw new InvalidOperationException($"Missing {operation} image result.");
            }
            if (verifiedErrors.Count != expectedErrors.Count)
                throw new InvalidOperationException("Some expected errors were not verified.");
            int verifiedFilterVariants = 0;
            foreach (var options in ProcessingTools.GetSmokeVariantOptions())
            {
                if ((ColorSection.Visibility == Visibility.Visible) != ProcessingTools.UsesWorkingColor)
                    throw new InvalidOperationException("The color section did not follow the selected border type.");
                if (fixtureMode)
                {
                    var result = await _processing.RunAsync(source, region, options);
                    if (result.Image is null) throw new InvalidOperationException("Missing filter variant result.");
                    // These transport probes do not add session documents or alter the normal smoke counts.
                    await _documents.RemoveAsync(result.Image);
                }
                verifiedFilterVariants++;
            }
            if (verifiedFilterVariants != 18) throw new InvalidOperationException("Some OpenCV choices were not verified.");
            await RunEditingSmokeAsync(root, fixtureMode, source, fullResult);
            var transformOutcomes = await RunTransformSmokeAsync(fixtureMode, source, fullResult);
            await RunPluginSmokeAsync(source);
            await RunLayerSmokeAsync(source, fixtureMode);
            ImageList.SelectedItem = fullResult;
            ViewModel.SelectedImage = fullResult;
            ProcessingTools.SelectOperation("to_grayscale");
            EditorCanvas.SetZoom(2);
            stored = await _client.SendAsync(new CommandMessage { MessageType = "list_images" });
            foreach (var image in ViewModel.Images)
                if (stored.Images?.SingleOrDefault(record => record.ImageId == image.Id)?.Sha256 != image.Sha256)
                    throw new InvalidDataException("The Python registry and displayed results differ.");
            await Task.Delay(1500);
            if (EditorCanvas.RenderedImageId != ViewModel.SelectedImage.Id || EditorCanvas.LastRenderError is not null)
                throw new InvalidOperationException("Win2D rendering failed: " + EditorCanvas.LastRenderError);
            if (Math.Abs(EditorCanvas.Zoom - 2) > 0.001f)
                throw new InvalidOperationException("The initial fit overrode the selected zoom.");
            var runtime = await _client.SendAsync(new CommandMessage { MessageType = "ping" });
            if (runtime.BackendRevision != BackendProcessService.ComputeSourceRevision(root))
                throw new InvalidOperationException("The backend did not load the current Python source files.");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { status = "ok", count = ViewModel.Images.Count,
                images = stored.Images, selection = partialResult.Selection, zoom = EditorCanvas.Zoom, win2d_rendered = true,
                imported_count = 2, grayscale_results = 2, grayscale_verified = true,
                layers_history_verified = true, masks_creation_parameters_history_verified = true, masks_plugin_selection_verified = true, mask_hsv_palette_verified = true, paint_mask_history_verified = true, public_color_palettes_verified = true, plugin_multiple_sizes_verified = fixtureMode,
                plugin_forms_verified = true, plugin_presentations_verified = true, plugin_full_roi_history_verified = true,
                drawing_masks_verified = true, editing_history_verified = true, image_export_verified = true,
                drawing_apply_verified = true, drawing_compositing_verified = !fixtureMode,
                transform_controls_verified = transformOutcomes.Count, transform_operation_outcomes = transformOutcomes,
                transform_resized_results_verified = transformOutcomes.Values.Any(value => value == "ok"),
                transform_placeholders_verified = transformOutcomes.Values.Any(value => value == "not_implemented"),
                storage_drop_verified = true, mixed_drop_errors_verified = true, contextual_toolbox_verified = true,
                filter_variant_controls_verified = verifiedFilterVariants, filter_variant_transport_verified = fixtureMode,
                operation_outcomes = operationOutcomes, all_operations_wired = operationOutcomes.Count == 10 && transformOutcomes.Count == 3, test_fixtures = fixtureMode,
                expected_errors = expectedErrors, expected_errors_verified = verifiedErrors.Count == expectedErrors.Count,
                verified_error_messages = verifiedErrors,
                backend_revision_verified = true, backend_revision = runtime.BackendRevision,
                backend_pid = runtime.BackendPid, pipe_name = _client.PipeName,
                scope = "Real WinUI imports, all ten operation controls, full/ROI grayscale, returned mappings and analysis display. Fixture mode uses test-only callbacks for unfinished algorithms. Native picker/Explorer gestures require manual verification." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { status = "error", message = error.ToString() }));
        }
        finally { Close(); }
    }

    private static void VerifyGrayscaleResult(ImageDocument source, ImageDocument result, ImageRect? selection)
    {
        if (result.Id == source.Id || result.Width != source.Width || result.Height != source.Height)
            throw new InvalidDataException("The result must be a new image with the same dimensions.");
        bool changed = false;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int offset = (y * source.Width + x) * 4;
                bool inside = selection is not { } r || (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom);
                if (result.Pixels[offset + 3] != source.Pixels[offset + 3])
                    throw new InvalidDataException("The filter changed the alpha channel.");
                if (inside)
                {
                    if (result.Pixels[offset] != result.Pixels[offset + 1] || result.Pixels[offset] != result.Pixels[offset + 2])
                        throw new InvalidDataException("The processed region still contains colors.");
                    changed |= !result.Pixels.AsSpan(offset, 3).SequenceEqual(source.Pixels.AsSpan(offset, 3));
                }
                else if (!result.Pixels.AsSpan(offset, 4).SequenceEqual(source.Pixels.AsSpan(offset, 4)))
                    throw new InvalidDataException("Pixels outside the selection changed.");
            }
        }
        if (!changed || result.Thumbnail.PixelWidth <= 0 || result.Thumbnail.PixelWidth > 96 ||
            result.Thumbnail.PixelHeight <= 0 || result.Thumbnail.PixelHeight > 96)
            throw new InvalidDataException("Unchanged result or missing thumbnail.");
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.Pixels)).ToLowerInvariant() != source.Sha256)
            throw new InvalidDataException("The original image was modified.");
    }
}
