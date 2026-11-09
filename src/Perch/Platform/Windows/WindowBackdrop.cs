using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Win32;

namespace Perch.Platform.Windows;

// Windows-only window tweaks: a thin quiet border like the Windows Settings app, and the checks behind the frosted material.
internal static class WindowBackdrop
{
    public static void SetBorder(Window window, bool light)
    {
        if (window.TryGetPlatformHandle() is not { } handle || Environment.OSVersion.Version.Build < 22000) return;
        Set(handle.Handle, 34, light ? 0x00D9D6D6 : 0x00363333); // DWMWA_BORDER_COLOR, COLORREF 0x00BBGGRR
    }

    // Keeps a window out of screenshots and screen capture (so it can photograph what is behind itself). Returns false
    // where Windows cannot do that (before Windows 10 2004).
    public static bool ExcludeFromCapture(nint hwnd, bool on) => SetWindowDisplayAffinity(hwnd, on ? 0x11u : 0u);

    public static System.Drawing.Rectangle ScreenRect(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    // Acrylic and the island's frosted glass only work on a local desktop with "Transparency effects" turned on.
    // In a Remote Desktop session Windows paints acrylic as one flat colour, and a window hidden from screen capture
    // (which the island's glass relies on) shows up as a black box for the person watching.
    public static bool TranslucencyAvailable()
    {
        if (GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int off || off != 0;
        }
        catch { return true; }
    }

    // Windows swaps the acrylic backdrop for a flat colour whenever the window is not the active one, so a frosted
    // window looks solid as soon as another program is in front. Telling the system the window is still active
    // (WM_NCACTIVATE with TRUE) keeps the frosted look; the window has its own caption, so nothing else changes.
    public static void KeepActive(Window window)
    {
        Win32Properties.AddWndProcHookCallback(window, (nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg != 0x0086 /* WM_NCACTIVATE */ || wParam != 0) return 0;
            handled = true;
            return DefWindowProc(hwnd, msg, 1, -1); // lParam -1: do not repaint the (unused) frame
        });
    }

    private static int Set(nint handle, int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
