using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// External OLE drag source used by test_native_drop.ps1, never included in the app.
public static class NativeDragProbe {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static string Drop(string[] files, int x, int y) {
        string result = "not started";
        using (var form = new Form()) {
            form.Text = "Imagine Neo drop test source";
            form.Size = new Size(190, 110);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 200, 60);
            form.TopMost = true;
            var label = new Label { Dock = DockStyle.Fill, Text = "Automated file drag", TextAlign = ContentAlignment.MiddleCenter };
            form.Controls.Add(label);
            label.MouseDown += delegate {
                var data = new DataObject(DataFormats.FileDrop, files);
                result = label.DoDragDrop(data, DragDropEffects.Copy).ToString();
                form.BeginInvoke(new Action(form.Close));
            };
            form.Shown += delegate {
                Point source = label.PointToScreen(new Point(60, 40));
                var input = new Thread(() => {
                    try {
                        Thread.Sleep(400);
                        SetCursorPos(source.X, source.Y);
                        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(200);
                        for (int step = 1; step <= 30; step++) {
                            SetCursorPos(source.X + (x - source.X) * step / 30, source.Y + (y - source.Y) * step / 30);
                            Thread.Sleep(25);
                        }
                        Thread.Sleep(700);
                    } finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
                });
                input.IsBackground = true;
                input.Start();
            };
            var timeout = new System.Windows.Forms.Timer { Interval = 12000 };
            timeout.Tick += delegate { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); form.Close(); };
            timeout.Start();
            Application.Run(form);
            timeout.Dispose();
        }
        return result;
    }
    public static void Capture(IntPtr hwnd, string path) {
        RECT rect;
        GetWindowRect(hwnd, out rect);
        using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, bitmap.Size);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
