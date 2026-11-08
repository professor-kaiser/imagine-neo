using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using PhotoLab.UI.Models;

namespace PhotoLab.UI.Rendering;

public sealed class Win2DImageRenderer : IImageRenderer
{
    private CanvasBitmap? _bitmap;
    private string? _identity;
    public void Draw(CanvasDrawingSession session, CanvasDevice device, ImageDocument document)
    {
        if (_identity != document.Id || _bitmap is null)
        {
            Reset();
            byte[] display = (byte[])document.Pixels.Clone();
            for (int i = 0; i < display.Length; i += 4)
            {
                int alpha = display[i + 3];
                for (int channel = 0; channel < 3; channel++) display[i + channel] = (byte)((display[i + channel] * alpha + 127) / 255);
            }
            _bitmap = CanvasBitmap.CreateFromBytes(device, display, document.Width, document.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            _identity = document.Id;
        }
        session.DrawImage(_bitmap, 0, 0);
    }
    public void Reset() { _bitmap?.Dispose(); _bitmap = null; _identity = null; }
    public void Dispose() => Reset();
}
