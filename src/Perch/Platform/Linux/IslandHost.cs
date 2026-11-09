using Avalonia;
using Avalonia.Controls;

namespace Perch.Platform.Linux;

// Native X11 host. Compositor-specific Wayland integration is outside this port.
public sealed class IslandHost : IDisposable
{
    private readonly Window window;
    private readonly X11Events events;
    public nint Handle { get; }
    public event Action? DisplayChanged;
    public event Action<int>? HotKeyPressed;
    public event Action? ClipboardUpdated;

    public IslandHost(Window window, Func<bool> overTextBox)
    {
        this.window = window;
        Handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        window.Topmost = true;
        window.Screens.Changed += OnScreensChanged;
        window.ScalingChanged += OnScreensChanged;
        X11.Dock((nuint)Handle);
        events = new X11Events();
        events.HotKeyPressed += id => HotKeyPressed?.Invoke(id);
        events.ClipboardUpdated += () => ClipboardUpdated?.Invoke();
        Passive();
    }

    private void OnScreensChanged(object? sender, EventArgs e) => DisplayChanged?.Invoke();
    public static (int X, int Y) CursorPosition() => X11.CursorPosition();
    public bool ExcludeFromCapture(bool on) => false;
    public void Passive() => X11.Passive((nuint)Handle, true);
    public void BeginEditing() { X11.Passive((nuint)Handle, false); window.Activate(); }
    public bool RegisterHotKey(int id, uint modifiers, uint virtualKey) => events.Register(id, modifiers, virtualKey);
    public void UnregisterHotKey(int id) => events.Unregister(id);

    public void Place(double width, double height)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        window.Width = width;
        window.Height = height;
        if (screen == null) return;
        var area = screen.WorkingArea;
        window.Position = new PixelPoint(area.X + (area.Width - (int)Math.Ceiling(width * screen.Scaling)) / 2, area.Y);
    }

    public void Region(IReadOnlyList<Point> outline, double offsetX, Rect? extra = null) =>
        X11.Shape((nuint)Handle, outline, offsetX, window.RenderScaling, extra);

    public void Dispose()
    {
        window.Screens.Changed -= OnScreensChanged;
        window.ScalingChanged -= OnScreensChanged;
        events.Dispose();
    }
}
