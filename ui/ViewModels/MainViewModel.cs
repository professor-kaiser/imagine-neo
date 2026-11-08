using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using PhotoLab.UI.Models;
using PhotoLab.UI.Services;

namespace PhotoLab.UI.ViewModels;

public sealed class MainViewModel(ImageDocumentService documents) : ObservableObject
{
    public ObservableCollection<ImageDocument> Images => documents.Documents;
    public ObservableCollection<PaletteEntry> Palette { get; } = [];
    private string _analysisText = "Pick a color or extract a palette.";
    public string AnalysisText { get => _analysisText; set => SetProperty(ref _analysisText, value); }
    private ImageDocument? _selectedImage;
    public ImageDocument? SelectedImage { get => _selectedImage; set => SetProperty(ref _selectedImage, value); }
    private ImageDocument? _sourceImage;
    public ImageDocument? SourceImage { get => _sourceImage; set => SetProperty(ref _sourceImage, value); }
    private string _status = "Starting the backend…";
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    private string _backendStatus = "Connecting…";
    public string BackendStatus { get => _backendStatus; set => SetProperty(ref _backendStatus, value); }
    private string _selectionText = "No selection";
    public string SelectionText { get => _selectionText; set => SetProperty(ref _selectionText, value); }
    private string _sourceText = "Choose a source image and a target selection.";
    public string SourceText { get => _sourceText; set => SetProperty(ref _sourceText, value); }
    private string _dimensionsText = "No image";
    public string DimensionsText { get => _dimensionsText; set => SetProperty(ref _dimensionsText, value); }
    private string _coordinatesText = "X — · Y —";
    public string CoordinatesText { get => _coordinatesText; set => SetProperty(ref _coordinatesText, value); }
    private string _zoomText = "100 %";
    public string ZoomText { get => _zoomText; set => SetProperty(ref _zoomText, value); }
}
