using Microsoft.Graphics.Canvas;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Rendering;

public interface IImageRenderer : IDisposable
{
    void Draw(CanvasDrawingSession session, CanvasDevice device, ImageDocument document);
    void Reset();
}
