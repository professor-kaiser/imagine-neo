using PhotoLab.UI.Models;

namespace PhotoLab.UI.Services;

// Each imported image has its own lineage. Library navigation is not an edit.
public sealed class EditHistoryService
{
    private readonly Dictionary<string, ImageDocument> _parents = [];
    private readonly Dictionary<string, ImageDocument> _redo = [];
    public ImageDocument? Previous(ImageDocument image) => _parents.GetValueOrDefault(image.Id);
    public ImageDocument? Next(ImageDocument image) => _redo.GetValueOrDefault(image.Id);
    public void DiscardRedo(ImageDocument image) => _redo.Remove(image.Id);
    public void Record(ImageDocument source, ImageDocument result)
    {
        _parents[result.Id] = source;
        _redo.Remove(source.Id);
    }
    public ImageDocument? Undo(ImageDocument image)
    {
        var previous = Previous(image);
        if (previous is not null) _redo[previous.Id] = image;
        return previous;
    }
    public ImageDocument? Redo(ImageDocument image)
    {
        var next = Next(image);
        _redo.Remove(image.Id);
        return next;
    }
    public void Forget(ImageDocument image)
    {
        _parents.Remove(image.Id); _redo.Remove(image.Id);
        foreach (var key in _parents.Where(pair => pair.Value == image).Select(pair => pair.Key).ToArray()) _parents.Remove(key);
        foreach (var key in _redo.Where(pair => pair.Value == image).Select(pair => pair.Key).ToArray()) _redo.Remove(key);
    }
}
