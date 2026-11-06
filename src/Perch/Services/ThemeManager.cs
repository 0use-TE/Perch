using Avalonia;
using Avalonia.Styling;
using Microsoft.Win32;

namespace Perch.Services;

// Dark / light chrome (settings window and right-click menu). "system" follows the Windows app theme.
public static class ThemeManager
{
    public static bool IsLight(string setting) => setting switch
    {
        "light" => true,
        "system" => SystemUsesLightApps(),
        _ => false,
    };

    public static void Apply(string setting)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = IsLight(setting) ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private static bool SystemUsesLightApps()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1; }
        catch { return false; }
    }
}
