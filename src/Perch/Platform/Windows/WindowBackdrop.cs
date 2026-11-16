using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Win32;
using Perch.Views;

namespace Perch.Platform.Windows;

// Windows-only window tweaks: a thin quiet border like the Windows Settings app, and the checks behind the frosted material.
internal static class WindowBackdrop
{
    // Tracks when each HWND was hidden from screen capture, so we never re-apply WDA_EXCLUDEFROMCAPTURE redundantly
    // and always give DWM one composition frame (~45 ms) to drop the window before sampling the screen behind it.
    private static readonly ConcurrentDictionary<nint, long> excludedSince = new();
    private static long lastPopupTick;

    public static void InstallPopupGuard()
    {
        // Every popup (ToolTip, ContextMenu, Flyout, ComboBox dropdown) must also be excluded from screen capture,
        // otherwise the periodic backdrop sampler photographs open tooltips and menus into the blurred glass background.
        Control.LoadedEvent.AddClassHandler<TopLevel>((top, _) =>
        {
            if (top is IslandWindow or SettingsWindow) return;
            if (top.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != 0)
            {
                SetWindowDisplayAffinity(hwnd, 0x11u);
                Interlocked.Exchange(ref lastPopupTick, Environment.TickCount64);
            }
        });
    }

    public static void SetBorder(Window window, bool light)
    {
        if (window.TryGetPlatformHandle() is not { } handle || Environment.OSVersion.Version.Build < 22000) return;
        Set(handle.Handle, 34, light ? 0x00D9D6D6 : 0x00363333); // DWMWA_BORDER_COLOR, COLORREF 0x00BBGGRR
    }

    // Keeps a window out of screenshots and screen capture (so it can photograph what is behind itself). Returns false
    // where Windows cannot do that (before Windows 10 2004).
    public static bool ExcludeFromCapture(nint hwnd, bool on, bool force = false)
    {
        if (hwnd == 0) return false;
        if (on)
        {
            if (!force && excludedSince.ContainsKey(hwnd)) return true;
            if (!SetWindowDisplayAffinity(hwnd, 0x11u))
            {
                excludedSince.TryRemove(hwnd, out _);
                return false;
            }
            excludedSince[hwnd] = Environment.TickCount64;
            return true;
        }
        if (excludedSince.TryRemove(hwnd, out _) || force)
            SetWindowDisplayAffinity(hwnd, 0u);
        return false;
    }

    // True once DWM has had time to apply WDA_EXCLUDEFROMCAPTURE to this window and any newly opened popup.
    public static bool ReadyToSample(nint hwnd)
    {
        if (!excludedSince.TryGetValue(hwnd, out var since)) return false;
        var now = Environment.TickCount64;
        return now - since >= 45 && now - Interlocked.Read(ref lastPopupTick) >= 45;
    }

    public static void NoteShown(nint hwnd)
    {
        if (excludedSince.ContainsKey(hwnd))
            excludedSince[hwnd] = Environment.TickCount64;
    }

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
