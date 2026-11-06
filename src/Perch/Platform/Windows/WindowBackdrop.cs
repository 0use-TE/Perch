using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Perch.Platform.Windows;

// Small DWM tweaks for an ordinary window: a thin quiet border like the Windows Settings app. The acrylic
// backdrop itself comes from the window's TransparencyLevelHint.
internal static class WindowBackdrop
{
    public static void SetBorder(Window window, bool light)
    {
        if (window.TryGetPlatformHandle() is not { } handle || Environment.OSVersion.Version.Build < 22000) return;
        Set(handle.Handle, 34, light ? 0x00D9D6D6 : 0x00363333); // DWMWA_BORDER_COLOR, COLORREF 0x00BBGGRR
    }

    private static int Set(nint handle, int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
