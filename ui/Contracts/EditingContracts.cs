using PhotoLab.UI.Models;

namespace PhotoLab.UI.Contracts;

// Extension boundaries only; none are registered as active processing features.
public sealed record DocumentRevision(Guid Id, Guid? ParentId, string Operation, DateTimeOffset CreatedAt);
public sealed record LayerDescriptor(Guid Id, string Name, bool Visible, double Opacity, Guid? MaskId);
public sealed record ProcessingRequest(string Operation, string ImageId, ImageRect? Selection, IReadOnlyDictionary<string, object> Parameters);
public interface IHistoryService
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    Task UndoAsync(CancellationToken token);
    Task RedoAsync(CancellationToken token);
}
public interface IPreviewService { Task PreviewAsync(ProcessingRequest request, CancellationToken token); }
public interface IExportService { Task ExportAsync(string imageId, string destination, CancellationToken token); }
public interface IToolPlugin { string Id { get; } string DisplayName { get; } IReadOnlyList<string> Operations { get; } }
