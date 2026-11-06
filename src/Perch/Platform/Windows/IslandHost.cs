using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Perch.Platform.Windows;

// The native region excludes transparent corners from cross-process hit testing.
public sealed class IslandHost : IDisposable
{
    private readonly Window window;
    private readonly HwndSource source;
    private readonly nint handle;
    private bool placing;
    public event Action? DisplayChanged;
    public event Action<int>? HotKeyPressed;
    public event Action? ClipboardUpdated;
    public IslandHost(Window window)
    {
        this.window = window;
        handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(Hook);
        AddClipboardFormatListener(handle);
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        // Passive mode also prevents WPF's keyboard provider from activating via SetFocus.
        SetWindowLongPtr(handle, -20, new nint((style | 0x80 | 0x8000000) & ~0x40000));
    }
    // Hides this window from screen capture so the frosted backdrop can sample what is behind it (the side effect:
    // screenshots, recordings and screen sharing do not show the island while this is on).
    public bool ExcludeFromCapture(bool on) => SetWindowDisplayAffinity(handle, on ? 0x11u : 0u);
    public System.Drawing.Rectangle ScreenRect() { GetWindowRect(handle, out var r); return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
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
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            var work = screen.WorkingArea;
            var dpi = GetDpiForWindow(handle) / 96d;
            var w = (int)Math.Ceiling(width * dpi);
            var h = (int)Math.Ceiling(height * dpi);
            window.Width = width; window.Height = height;
            SetWindowPos(handle, new nint(-1), work.Left + (work.Width - w) / 2, work.Top, w, h, 0x10 | 0x40);
        }
        finally { placing = false; }
    }
    public void Region(Geometry outline, double offsetX)
    {
        var scale = GetDpiForWindow(handle) / 96d;
        // The region clips the drawn (anti-aliased) edge, which would show as stair-steps; growing it keeps the whole soft edge visible.
        var grown = Geometry.Combine(outline, outline.GetWidenedPathGeometry(new Pen(Brushes.Black, 3)), GeometryCombineMode.Union, null);
        var figure = grown.GetFlattenedPathGeometry(0.2, ToleranceType.Absolute).Figures[0];
        var points = new List<POINT>();
        void Add(Point point) => points.Add(new POINT { X = (int)Math.Round((point.X + offsetX) * scale), Y = (int)Math.Round(point.Y * scale) });
        Add(figure.StartPoint);
        foreach (var segment in figure.Segments)
        {
            if (segment is PolyLineSegment poly) foreach (var point in poly.Points) Add(point);
            else if (segment is LineSegment line) Add(line.Point);
        }
        var region = CreatePolygonRgn(points.ToArray(), points.Count, 1);
        if (SetWindowRgn(handle, region, true) == 0) DeleteObject(region); // Success transfers ownership to Windows.
    }
    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x21) // WM_MOUSEACTIVATE: only genuine text input should activate this island.
        {
            GetCursorPos(out var cursor);
            var point = window.PointFromScreen(new Point(cursor.X, cursor.Y));
            var hit = window.InputHitTest(point) as DependencyObject;
            while (hit != null)
            {
                if (hit is TextBox) { SetActivation(true); handled = true; return new nint(1); }
                hit = hit is Visual or Visual3D ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            }
            SetActivation(false);
            handled = true; return new nint(3); // MA_NOACTIVATE, deliver click normally.
        }
        if (msg == 0x0312) { var id = wParam.ToInt32(); window.Dispatcher.BeginInvoke(() => HotKeyPressed?.Invoke(id)); handled = true; return 0; }
        if (msg == 0x031D) { window.Dispatcher.BeginInvoke(() => ClipboardUpdated?.Invoke()); return 0; }
        if (msg is 0x7E or 0x2E0 or 0x1A)
            window.Dispatcher.BeginInvoke(() => DisplayChanged?.Invoke());
        return 0;
    }
    public bool RegisterHotKey(int id, uint modifiers, uint virtualKey) => RegisterHotKey(handle, id, modifiers | 0x4000, virtualKey); // 0x4000: no auto-repeat
    public void UnregisterHotKey(int id) => UnregisterHotKey(handle, id);
    public void Dispose()
    {
        RemoveClipboardFormatListener(handle);
        source.RemoveHook(Hook);
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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreatePolygonRgn([In] POINT[] points, int count, int fillMode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
