using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoLab.UI.Services;

/// <summary>
/// Win32 file-drop fallback for desktop launches where WinUI's OLE target is unavailable
/// (notably elevated sessions). File paths still pass through the normal decoder/importer.
/// </summary>
public sealed class NativeFileDropService : IDisposable
{
    private const uint DropFiles = 0x0233;
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private bool _disposed;
    public event EventHandler<string[]>? FilesDropped;
    public event EventHandler<Exception>? DropFailed;

    public NativeFileDropService(nint window)
    {
        _window = window;
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, 0x4E454F, 0)) throw new Win32Exception();
        DragAcceptFiles(window, true);
        // These three shell-transfer messages carry dropped files only. The exception is
        // scoped to this window; no process-wide message filter or privilege change is used.
        foreach (uint message in new uint[] { DropFiles, 0x0049, 0x004A })
            ChangeWindowMessageFilterEx(window, message, 1, 0);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message != DropFiles) return DefSubclassProc(window, message, wParam, lParam);
        nint drop = (nint)wParam;
        try
        {
            uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
            if (count > 4096) throw new InvalidOperationException("Drop fewer than 4,096 files at once.");
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(drop, i, null, 0);
                if (length > 32767) throw new InvalidOperationException("A dropped file path is too long.");
                var path = new StringBuilder((int)length + 1);
                DragQueryFile(drop, i, path, (uint)path.Capacity);
                paths[i] = path.ToString();
            }
            FilesDropped?.Invoke(this, paths);
        }
        catch (Exception error) { DropFailed?.Invoke(this, error); }
        finally { DragFinish(drop); }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DragAcceptFiles(_window, false);
        RemoveWindowSubclass(_window, _callback, 0x4E454F);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint size);
    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint drop);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeInfo);
}
