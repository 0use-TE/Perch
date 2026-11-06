using System.Windows;
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
        var source = new Uri($"pack://application:,,,/Views/{(IsLight(setting) ? "UiLight" : "UiDark")}.xaml");
        foreach (var child in Application.Current.Resources.MergedDictionaries)
        {
            var name = child.Source?.OriginalString ?? "";
            if (!name.EndsWith("UiDark.xaml", StringComparison.Ordinal) && !name.EndsWith("UiLight.xaml", StringComparison.Ordinal)) continue;
            if (!name.EndsWith(source.Segments[^1], StringComparison.Ordinal)) child.Source = source;
            return;
        }
    }

    private static bool SystemUsesLightApps()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1; }
        catch { return false; }
    }
}
