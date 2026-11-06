using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Perch.Platform.Windows;

// Gives an ordinary (non-layered) window the Windows 11 acrylic backdrop, dark title area and rounded corners.
// Falls back silently on older Windows, where the window keeps its tinted background.
internal static class WindowBackdrop
{
    private const int UseImmersiveDarkMode = 20, WindowCornerPreference = 33, SystemBackdropType = 38;
    private const int RoundedCorners = 2, Acrylic = 3;

    public static bool Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || Environment.OSVersion.Version.Build < 22621) return false;
        if (HwndSource.FromHwnd(handle) is { } source) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        Set(handle, UseImmersiveDarkMode, dark ? 1 : 0);
        Set(handle, WindowCornerPreference, RoundedCorners);
        return Set(handle, SystemBackdropType, Acrylic) == 0;
    }

    // Acrylic when the island is frosted; a plain opaque window otherwise.
    public static void SetAcrylic(Window window, bool on)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && Environment.OSVersion.Version.Build >= 22621) Set(handle, SystemBackdropType, on ? Acrylic : 1);
    }

    // A thin, quiet window border like the Windows Settings app (instead of the accent-coloured default).
    public static void SetBorder(Window window, bool light)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || Environment.OSVersion.Version.Build < 22000) return;
        Set(handle, 34, light ? 0x00D9D6D6 : 0x00363333); // DWMWA_BORDER_COLOR, COLORREF 0x00BBGGRR
    }

    // Switches the backdrop between its dark and light variant (theme change) without recreating the window.
    public static void SetDark(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0) Set(handle, UseImmersiveDarkMode, dark ? 1 : 0);
    }

    private static int Set(nint handle, int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
