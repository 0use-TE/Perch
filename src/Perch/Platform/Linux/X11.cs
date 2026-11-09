using System.Runtime.InteropServices;
using Avalonia;

namespace Perch.Platform.Linux;

// A separate Xlib connection, used only on the UI thread. Avalonia owns its own connection.
internal static class X11
{
    private static readonly Lazy<nint> Connection = new(Open);
    private static nint ownDisplay, previousErrorHandler;
    private static readonly ErrorHandler HandleError = OnError;
    internal static nint Display => Connection.Value;
    internal static nuint Root => XDefaultRootWindow(Display);
    internal static nuint Atom(string name) => XInternAtom(Display, name, false);

    private static nint Open()
    {
        ownDisplay = XOpenDisplay(null);
        if (ownDisplay == 0) throw new InvalidOperationException("Cannot connect to the X11 display.");
        previousErrorHandler = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(HandleError));
        return ownDisplay;
    }

    private static int OnError(nint display, ref XError error)
    {
        if (display != ownDisplay)
            return ForwardError(previousErrorHandler, display, ref error);
        // Another application's window can disappear between the EWMH query and the property/geometry read.
        if (error.Code is not (3 or 9)) Services.Log.Warn($"X11 request {error.Request} failed with error {error.Code}");
        return 0;
    }

    // The previous callback may have been exported by Avalonia with a different managed delegate type.
    // Calling its native pointer avoids trying to cast that managed delegate to our own type.
    internal static unsafe int ForwardError(nint handler, nint display, ref XError error)
    {
        if (handler == 0) return 0;
        fixed (XError* pointer = &error)
            return ((delegate* unmanaged[Cdecl]<nint, XError*, int>)handler)(display, pointer);
    }

    internal static PixelRect Bounds(nuint window)
    {
        if (XGetGeometry(Display, window, out _, out _, out _, out var width, out var height, out _, out _) == 0) return default;
        if (XTranslateCoordinates(Display, window, Root, 0, 0, out var x, out var y, out _) == 0) return default;
        return new PixelRect(x, y, (int)width, (int)height);
    }

    public static (int X, int Y) CursorPosition()
    {
        if (Display == 0) return (int.MinValue, int.MinValue);
        XQueryPointer(Display, Root, out _, out _, out var x, out var y, out _, out _, out _);
        return (x, y);
    }

    public static bool IsButtonDown(int key)
    {
        if (Display == 0) return false;
        XQueryPointer(Display, Root, out _, out _, out _, out _, out _, out _, out var mask);
        return (mask & (key switch { 1 => 256u, 2 => 1024u, 4 => 512u, _ => 0u })) != 0;
    }

    public static TimeSpan IdleTime()
    {
        if (Display == 0) return TimeSpan.Zero;
        var info = XScreenSaverAllocInfo();
        if (info == 0) return TimeSpan.Zero;
        try { return XScreenSaverQueryInfo(Display, Root, info) == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds((double)Marshal.PtrToStructure<ScreenSaverInfo>(info).Idle); }
        finally { XFree(info); }
    }

    internal static void Passive(nuint window, bool passive)
    {
        var hints = new WmHints { Flags = 1, Input = passive ? 0 : 1 };
        XSetWMHints(Display, window, ref hints);
        XFlush(Display);
    }

    public static void Dock(nuint window)
    {
        if (Display == 0) return;
        SetAtoms(window, "_NET_WM_WINDOW_TYPE", [Atom("_NET_WM_WINDOW_TYPE_DOCK")]);
        SetAtoms(window, "_NET_WM_STATE", [Atom("_NET_WM_STATE_ABOVE"), Atom("_NET_WM_STATE_SKIP_TASKBAR"), Atom("_NET_WM_STATE_SKIP_PAGER")]);
        XFlush(Display);
    }

    private static void SetAtoms(nuint window, string name, nuint[] values) =>
        XChangeProperty(Display, window, Atom(name), Atom("ATOM"), 32, 0, values, values.Length);

    public static nuint[] Property(nuint window, string name)
    {
        if (Display == 0) return [];
        var status = XGetWindowProperty(Display, window, Atom(name), 0, 64, false, 0, out _, out var format, out var count, out _, out var data);
        try
        {
            if (status != 0 || format != 32 || data == 0) return [];
            var result = new nuint[(int)count];
            for (var i = 0; i < result.Length; i++) result[i] = (nuint)Marshal.ReadIntPtr(data, i * IntPtr.Size);
            return result;
        }
        finally { if (data != 0) XFree(data); }
    }

    public static void Shape(nuint window, IReadOnlyList<Point> outline, double offset, double scale, Rect? extra)
    {
        if (Display == 0 || outline.Count < 3) return;
        var points = outline.Select(p => new XPoint { X = (short)Math.Round((p.X + offset) * scale), Y = (short)Math.Round(p.Y * scale) }).ToArray();
        var region = XPolygonRegion(points, points.Length, 0);
        if (region == 0) return;
        try
        {
            if (extra is { } rect)
            {
                var bar = new XRectangle { X = (short)Math.Floor(rect.X * scale), Y = (short)Math.Floor(rect.Y * scale), Width = (ushort)Math.Ceiling(rect.Width * scale), Height = (ushort)Math.Ceiling(rect.Height * scale) };
                XUnionRectWithRegion(ref bar, region, region);
            }
            // Both the visible shape and the input region exclude the transparent animation space.
            XShapeCombineRegion(Display, window, 0, 0, 0, region, 0);
            XShapeCombineRegion(Display, window, 2, 0, 0, region, 0);
            XFlush(Display);
        }
        finally { XDestroyRegion(region); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct XPoint { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct XRectangle { public short X, Y; public ushort Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenSaverInfo { public nuint Window; public int State, Kind; public nuint Since, Idle, Mask; }
    [StructLayout(LayoutKind.Sequential)] private struct WmHints { public nint Flags; public int Input, State; public nuint IconPixmap, IconWindow; public int IconX, IconY; public nuint IconMask, WindowGroup; }
    [StructLayout(LayoutKind.Sequential)] internal struct XError { public int Type; public nint Display; public nuint Resource, Serial; public byte Code, Request, Minor; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ErrorHandler(nint display, ref XError error);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libX11.so.6")] private static extern int XGetGeometry(nint display, nuint window, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport("libX11.so.6")] private static extern int XTranslateCoordinates(nint display, nuint source, nuint destination, int x, int y, out int rootX, out int rootY, out nuint child);
    [DllImport("libXss.so.1")] private static extern nint XScreenSaverAllocInfo();
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(nint display, nuint window, nint info);
    [DllImport("libX11.so.6")] private static extern int XSetWMHints(nint display, nuint window, ref WmHints hints);
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XQueryPointer(nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int x, out int y, out uint mask);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, nuint[] data, int count);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length, [MarshalAs(UnmanagedType.Bool)] bool delete, nuint type, out nuint actualType, out int format, out nuint count, out nuint remaining, out nint data);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern nint XPolygonRegion(XPoint[] points, int count, int rule);
    [DllImport("libX11.so.6")] private static extern int XUnionRectWithRegion(ref XRectangle rect, nint source, nint destination);
    [DllImport("libX11.so.6")] private static extern int XDestroyRegion(nint region);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRegion(nint display, nuint window, int kind, int x, int y, nint region, int operation);
}
