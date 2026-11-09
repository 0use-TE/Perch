using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Win32;

namespace Perch.Platform.Windows;

// Win32 details of the island window: it never takes focus, sits at the top centre of the primary screen,
// listens for hotkeys and clipboard changes, and uses a native region so transparent corners don't catch clicks.
public sealed class IslandHost : IDisposable
{
    private readonly Window window;
    private readonly nint handle;
    private readonly Func<bool> overTextBox;
    private bool placing;
    public event Action? DisplayChanged;
    public event Action<int>? HotKeyPressed;
    public event Action? ClipboardUpdated;

    // overTextBox tells whether the pointer is over a text input; only then may a click activate the window.
    public IslandHost(Window window, Func<bool> overTextBox)
    {
        this.window = window;
        this.overTextBox = overTextBox;
        handle = window.TryGetPlatformHandle()!.Handle;
        Win32Properties.AddWndProcHookCallback(window, Hook);
        AddClipboardFormatListener(handle);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        // No-activate + tool window (no taskbar button, not in Alt+Tab).
        SetWindowLongPtr(handle, -20, new nint((style | 0x80 | 0x8000000) & ~0x40000));
    }

    public nint Handle => handle;

    // Hides this window from screen capture so the frosted backdrop can sample what is behind it (the side effect:
    // screenshots, recordings and screen sharing do not show the island while this is on).
    public bool ExcludeFromCapture(bool on) => SetWindowDisplayAffinity(handle, on ? 0x11u : 0u);
    public System.Drawing.Rectangle ScreenRect() { GetWindowRect(handle, out var r); return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
    public static (int X, int Y) CursorPosition() { GetCursorPos(out var p); return (p.X, p.Y); }
    public void Passive() => SetActivation(false);

    public void BeginEditing()
    {
        SetActivation(true);
        window.Activate(); // Only called in response to a real click on a text input.
    }

    private void SetActivation(bool editable)
    {
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new nint(editable ? style & ~0x8000000 : style | 0x8000000));
    }

    public void Place(double width, double height)
    {
        if (placing) return;
        placing = true;
        try
        {
            SystemParametersInfo(0x30 /* SPI_GETWORKAREA */, 0, out var work, 0);
            var dpi = GetDpiForWindow(handle) / 96d;
            var w = (int)Math.Ceiling(width * dpi);
            var h = (int)Math.Ceiling(height * dpi);
            window.Width = width; window.Height = height;
            SetWindowPos(handle, new nint(-1), work.Left + (work.Right - work.Left - w) / 2, work.Top, w, h, 0x10 | 0x40);
        }
        finally { placing = false; }
    }

    // Clips the window to the given outline (in DIP, window coordinates).
    // The optional rectangle is added to the region: the floating capsule uses it for the strip above the pill,
    // so a pointer pushed against the top edge of the screen still counts as being on the island.
    public void Region(IReadOnlyList<Point> outline, double offsetX, Rect? extra = null)
    {
        var scale = GetDpiForWindow(handle) / 96d;
        var points = outline.Select(p => new POINT { X = (int)Math.Round((p.X + offsetX) * scale), Y = (int)Math.Round(p.Y * scale) }).ToArray();
        var region = CreatePolygonRgn(points, points.Length, 1);
        if (extra is { } strip)
        {
            var bar = CreateRectRgn((int)Math.Floor(strip.Left * scale), (int)Math.Floor(strip.Top * scale), (int)Math.Ceiling(strip.Right * scale), (int)Math.Ceiling(strip.Bottom * scale));
            CombineRgn(region, region, bar, 2 /* RGN_OR */);
            DeleteObject(bar);
        }
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region); // Success transfers ownership to Windows.
    }

    private nint Hook(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x21) // WM_MOUSEACTIVATE: only genuine text input should activate this island.
        {
            if (overTextBox()) { SetActivation(true); handled = true; return new nint(1); }
            SetActivation(false);
            handled = true; return new nint(3); // MA_NOACTIVATE, deliver click normally.
        }
        if (msg == 0x0312) { var id = wParam.ToInt32(); Avalonia.Threading.Dispatcher.UIThread.Post(() => HotKeyPressed?.Invoke(id)); handled = true; return 0; }
        if (msg == 0x031D) { Avalonia.Threading.Dispatcher.UIThread.Post(() => ClipboardUpdated?.Invoke()); return 0; }
        if (msg is 0x7E or 0x2E0 or 0x1A)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => DisplayChanged?.Invoke());
        return 0;
    }

    public bool RegisterHotKey(int id, uint modifiers, uint virtualKey) => RegisterHotKey(handle, id, modifiers | 0x4000, virtualKey); // 0x4000: no auto-repeat
    public void UnregisterHotKey(int id) => UnregisterHotKey(handle, id);

    public void Dispose()
    {
        RemoveClipboardFormatListener(handle);
        Win32Properties.RemoveWndProcHookCallback(window, Hook);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out RECT rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreatePolygonRgn([In] POINT[] points, int count, int fillMode);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
