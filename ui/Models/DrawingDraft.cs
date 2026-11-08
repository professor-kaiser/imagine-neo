using System.Numerics;
using PhotoLab.UI.Contracts;

namespace PhotoLab.UI.Models;

public enum DrawingTool { Brush, Eraser, Rectangle, Circle, Line, Arrow, Clear }
public sealed record DrawingStroke(DrawingTool Tool, float Size, ColorRgba Color, IReadOnlyList<Vector2> Points);

// Commands, rather than full-image snapshots, keep the draft history small.
public sealed class DrawingDraft
{
    private readonly List<DrawingStroke> _strokes = [];
    private IReadOnlyList<DrawingStroke> _baseline = [];
    private int _position;
    public int Revision { get; private set; }
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _strokes.Count;
    public IEnumerable<DrawingStroke> Strokes => _baseline.Concat(_strokes.Take(_position));
    public DrawingStroke[] Snapshot() => Strokes.Select(s => s with { Points = s.Points.ToArray() }).ToArray();
    public void Load(IEnumerable<DrawingStroke> strokes)
    {
        _baseline = strokes.Select(s => s with { Points = s.Points.ToArray() }).ToArray();
        _strokes.Clear(); _position = 0; Revision++;
    }
    public bool HasDrawing => Strokes.Reverse().TakeWhile(s => s.Tool != DrawingTool.Clear)
        .Any(s => s.Tool != DrawingTool.Eraser && s.Color.A > 0);
    public void Add(DrawingStroke stroke)
    {
        _strokes.RemoveRange(_position, _strokes.Count - _position);
        _strokes.Add(stroke with { Points = stroke.Points.ToArray() });
        _position++; Revision++;
    }
    public void Undo() { if (CanUndo) { _position--; Revision++; } }
    public void Redo() { if (CanRedo) { _position++; Revision++; } }
    public void Clear() => Add(new(DrawingTool.Clear, 1, new(0, 0, 0, 0), []));
    public void Reset() { _baseline = []; _strokes.Clear(); _position = 0; Revision++; }
}
