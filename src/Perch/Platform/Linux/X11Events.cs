using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Perch.Platform.Linux;

// Events from our own Xlib connection, never from Avalonia's event queue.
internal sealed class X11Events : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<int, (int Code, uint Mask)> hotkeys = new();
    private readonly HashSet<uint> pressed = new();
    private readonly uint[] locks;
    private readonly int fixesEvent = -1;
    public event Action<int>? HotKeyPressed;
    public event Action? ClipboardUpdated;

    public X11Events()
    {
        var numLock = NumLockMask();
        locks = new uint[] { 0, 2, numLock, numLock | 2 }.Distinct().ToArray();
        if (XFixesQueryExtension(X11.Display, out var eventBase, out _) != 0)
        {
            fixesEvent = eventBase;
            XFixesSelectSelectionInput(X11.Display, X11.Root, X11.Atom("CLIPBOARD"), 7);
        }
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private void Poll()
    {
        while (XPending(X11.Display) > 0)
        {
            XNextEvent(X11.Display, out var e);
            if (e.Type == fixesEvent) ClipboardUpdated?.Invoke();
            if (e.Type == 3) // KeyRelease; X11 auto-repeat emits a release/press pair at the same timestamp.
            {
                if (XPending(X11.Display) > 0)
                {
                    XPeekEvent(X11.Display, out var next);
                    if (next.Type == 2 && next.Key.Keycode == e.Key.Keycode && next.Key.Time == e.Key.Time)
                    { XNextEvent(X11.Display, out _); continue; }
                }
                pressed.Remove(e.Key.Keycode);
                continue;
            }
            if (e.Type != 2) continue; // KeyPress
            if (!pressed.Add(e.Key.Keycode)) continue;
            var state = (e.Key.State & 255) & ~(locks.Aggregate(0u, (a, b) => a | b));
            foreach (var (id, value) in hotkeys.ToArray())
                if (e.Key.Keycode == value.Code && state == value.Mask) HotKeyPressed?.Invoke(id);
        }
    }

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        Unregister(id);
        var symbol = virtualKey switch
        {
            >= 0x41 and <= 0x5A => virtualKey + 32,
            >= 0x30 and <= 0x39 => virtualKey,
            >= 0x70 and <= 0x87 => 0xFFBEu + virtualKey - 0x70,
            0x20 => 0x20u, 0x0D => 0xFF0Du, 0x09 => 0xFF09u, 0x1B => 0xFF1Bu,
            0x25 => 0xFF51u, 0x26 => 0xFF52u, 0x27 => 0xFF53u, 0x28 => 0xFF54u,
            0x24 => 0xFF50u, 0x23 => 0xFF57u, 0x21 => 0xFF55u, 0x22 => 0xFF56u,
            0x2D => 0xFF63u, 0x2E => 0xFFFFu, _ => 0u,
        };
        if (symbol == 0) return false;
        var code = XKeysymToKeycode(X11.Display, symbol);
        if (code == 0) return false;
        var mask = ((modifiers & HotkeyParser.Shift) != 0 ? 1u : 0) | ((modifiers & HotkeyParser.Control) != 0 ? 4u : 0)
            | ((modifiers & HotkeyParser.Alt) != 0 ? 8u : 0) | ((modifiers & HotkeyParser.Win) != 0 ? 64u : 0);
        if (hotkeys.Values.Contains((code, mask))) return false;
        var failed = false;
        nint previous = 0;
        X11.ErrorHandler handler = (nint display, ref X11.XError error) =>
        {
            if (display == X11.Display) { failed = true; return 0; }
            return X11.ForwardError(previous, display, ref error);
        };
        XSync(X11.Display, false);
        previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(handler));
        try
        {
            foreach (var extra in locks) XGrabKey(X11.Display, code, mask | extra, X11.Root, false, 1, 1);
            XSync(X11.Display, false); // BadAccess means another client owns this shortcut.
        }
        finally { XSetErrorHandler(previous); GC.KeepAlive(handler); }
        if (failed) { foreach (var extra in locks) XUngrabKey(X11.Display, code, mask | extra, X11.Root); return false; }
        hotkeys[id] = (code, mask);
        return true;
    }

    public void Unregister(int id)
    {
        if (!hotkeys.Remove(id, out var key)) return;
        pressed.Remove((uint)key.Code);
        foreach (var extra in locks) XUngrabKey(X11.Display, key.Code, key.Mask | extra, X11.Root);
        XSync(X11.Display, false);
    }

    private static uint NumLockMask()
    {
        var map = XGetModifierMapping(X11.Display);
        if (map == 0) return 0;
        try
        {
            var modifier = Marshal.PtrToStructure<ModifierMap>(map);
            var code = XKeysymToKeycode(X11.Display, 0xFF7F);
            for (var m = 0; m < 8; m++)
                for (var i = 0; i < modifier.Count; i++)
                    if (Marshal.ReadByte(modifier.Keys, m * modifier.Count + i) == code) return 1u << m;
            return 0;
        }
        finally { XFreeModifiermap(map); }
    }

    public void Dispose()
    {
        timer.Stop();
        foreach (var id in hotkeys.Keys.ToArray()) Unregister(id);
        if (fixesEvent >= 0) XFixesSelectSelectionInput(X11.Display, X11.Root, X11.Atom("CLIPBOARD"), 0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ModifierMap { public int Count; public nint Keys; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyEvent { public int Type; public nuint Serial; public int Send; public nint Display; public nuint Window, Root, Subwindow, Time; public int X, Y, RootX, RootY; public uint State, Keycode; public int SameScreen; }
    [StructLayout(LayoutKind.Explicit, Size = 192)] private struct XEvent { [FieldOffset(0)] public int Type; [FieldOffset(0)] public KeyEvent Key; }
    [DllImport("libX11.so.6")] private static extern int XPending(nint display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(nint display, out XEvent e);
    [DllImport("libX11.so.6")] private static extern int XPeekEvent(nint display, out XEvent e);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint symbol);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(nint display, int code, uint modifiers, nuint window, [MarshalAs(UnmanagedType.Bool)] bool owner, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(nint display, int code, uint modifiers, nuint window);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern nint XGetModifierMapping(nint display);
    [DllImport("libX11.so.6")] private static extern int XFreeModifiermap(nint map);
    [DllImport("libXfixes.so.3")] private static extern int XFixesQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport("libXfixes.so.3")] private static extern void XFixesSelectSelectionInput(nint display, nuint window, nuint selection, nuint mask);
}
