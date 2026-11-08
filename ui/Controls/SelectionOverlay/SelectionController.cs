using System.Numerics;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Controls.SelectionOverlay;

public sealed class SelectionController
{
    private Vector2 _anchor;
    private ImageRect? _initial;
    private int _handle;
    private bool _moving;
    public ImageRect? Selection { get; set; }
    public bool FixedSize { get; set; }

    public void Begin(Vector2 point, float zoom)
    {
        _anchor = point; _initial = Selection; _handle = 0; _moving = false;
        if (Selection is { } rectangle)
        {
            float radius = 7 / zoom;
            if (!FixedSize)
            {
                bool nearX = point.X >= rectangle.X - radius && point.X <= rectangle.Right + radius;
                bool nearY = point.Y >= rectangle.Y - radius && point.Y <= rectangle.Bottom + radius;
                if (nearY && Math.Abs(point.X - rectangle.X) <= radius) _handle |= 1;
                else if (nearY && Math.Abs(point.X - rectangle.Right) <= radius) _handle |= 2;
                if (nearX && Math.Abs(point.Y - rectangle.Y) <= radius) _handle |= 4;
                else if (nearX && Math.Abs(point.Y - rectangle.Bottom) <= radius) _handle |= 8;
            }
            _moving = _handle == 0 && (FixedSize || rectangle.Contains(point.X, point.Y));
        }
    }

    public void Update(Vector2 point, int width, int height)
    {
        int x = Math.Clamp((int)MathF.Round(point.X), 0, width), y = Math.Clamp((int)MathF.Round(point.Y), 0, height);
        if (_moving && _initial is { } moved)
        {
            Selection = moved.MoveTo(moved.X + (int)MathF.Round(point.X - _anchor.X), moved.Y + (int)MathF.Round(point.Y - _anchor.Y), width, height);
            return;
        }
        if (FixedSize) return;
        if (_handle != 0 && _initial is { } original)
        {
            int left = original.X, right = original.Right, top = original.Y, bottom = original.Bottom;
            if ((_handle & 1) != 0) left = Math.Min(x, right - 1);
            if ((_handle & 2) != 0) right = Math.Max(x, left + 1);
            if ((_handle & 4) != 0) top = Math.Min(y, bottom - 1);
            if ((_handle & 8) != 0) bottom = Math.Max(y, top + 1);
            Selection = new(left, top, right - left, bottom - top);
            return;
        }
        int startX = Math.Clamp((int)MathF.Round(_anchor.X), 0, width), startY = Math.Clamp((int)MathF.Round(_anchor.Y), 0, height);
        int selectionWidth = Math.Abs(x - startX), selectionHeight = Math.Abs(y - startY);
        Selection = selectionWidth > 0 && selectionHeight > 0 ? new(Math.Min(x, startX), Math.Min(y, startY), selectionWidth, selectionHeight) : null;
    }
}
