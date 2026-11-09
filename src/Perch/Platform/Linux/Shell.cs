using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Services;

namespace Perch.Platform.Linux;

public static class ShellIcons
{
    public static Bitmap? Get(string path)
    {
        try
        {
            var icon = path.EndsWith(".desktop", StringComparison.Ordinal) ? DesktopEntries.Read(path).GetValueOrDefault("Icon") : null;
            if (icon == null) return null;
            if (Path.IsPathRooted(icon) && File.Exists(icon)) return new Bitmap(icon);
            foreach (var root in DesktopEntries.DataDirectories())
                foreach (var relative in new[] { $"icons/hicolor/48x48/apps/{icon}.png", $"icons/hicolor/32x32/apps/{icon}.png", $"pixmaps/{icon}.png" })
                    if (File.Exists(Path.Combine(root, relative))) return new Bitmap(Path.Combine(root, relative));
        }
        catch (Exception) { }
        return null;
    }
}

public static class ShellActions
{
    public static bool Open(string target)
    {
        // gio implements the desktop-entry Exec field and its quoting/field-code rules.
        if (Path.IsPathRooted(target) && target.EndsWith(".desktop", StringComparison.Ordinal))
            return Start("gio", "launch", target);
        return Start("xdg-open", target);
    }

    public static void Reveal(string path) => Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    public static bool Run(string command) => Start("/bin/sh", "-c", command);

    internal static bool Start(string executable, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            return process != null;
        }
        catch (Exception e) { Log.Warn($"Could not start {executable}", e); return false; }
    }
}

public static class StartupEntry
{
    public static void Apply(bool enabled)
    {
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathRooted(config)) config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var path = Path.Combine(config, "autostart", "net.0use.Perch.desktop");
            if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
            if (Environment.ProcessPath is not { } executable) return;
            var exec = Quote(executable);
            // dotnet run / dotnet Perch.dll needs the managed entry point as well.
            if (Path.GetFileNameWithoutExtension(executable) == "dotnet") exec += " " + Quote(typeof(Program).Assembly.Location);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"[Desktop Entry]\nType=Application\nName=Perch\nExec={exec}\nIcon=net.0use.Perch\nTerminal=false\n");
        }
        catch (Exception e) { Log.Warn("Could not update XDG autostart", e); }
    }

    // Desktop Entry strings are unescaped before the Exec argument quoting is interpreted.
    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\\\\\").Replace("\"", "\\\\\"").Replace("`", "\\\\`").Replace("$", "\\\\$").Replace("%", "%%").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}

public static class UserInput
{
    public static TimeSpan IdleTime() => X11.IdleTime();
}

public sealed class FullscreenWatcher : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    public bool IsFullscreen { get; private set; }
    public event Action? Changed;
    public FullscreenWatcher(nint self)
    {
        timer.Tick += (_, _) =>
        {
            var active = X11.Property(X11.Root, "_NET_ACTIVE_WINDOW").FirstOrDefault();
            var full = active != 0 && active != (nuint)self && X11.Property(active, "_NET_WM_STATE").Contains(X11.Atom("_NET_WM_STATE_FULLSCREEN"));
            if (full)
            {
                var bounds = X11.Bounds(active);
                var window = Ui.Root as Avalonia.Controls.Window;
                var primary = (window?.Screens.Primary ?? window?.Screens.All.FirstOrDefault())?.Bounds;
                Interaction.Dbg.Log($"fullscreen candidate {active}: bounds={bounds}, primary={primary}");
                full = primary is { } screen && bounds.X <= screen.X && bounds.Y <= screen.Y && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;
            }
            if (full == IsFullscreen) return;
            IsFullscreen = full;
            Changed?.Invoke();
        };
        timer.Start();
    }
    public void Dispose() => timer.Stop();
}

public static class PowerInfo
{
    public readonly record struct Reading(bool HasBattery, bool Charging, int Percent);
    public static Reading Read()
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories("/sys/class/power_supply"))
            {
                if (File.ReadAllText(Path.Combine(path, "type")).Trim() != "Battery") continue;
                if (!int.TryParse(File.ReadAllText(Path.Combine(path, "capacity")).Trim(), out var capacity)) continue;
                var status = File.ReadAllText(Path.Combine(path, "status")).Trim();
                return new(true, status is "Charging" or "Full" or "Not charging", Math.Clamp(capacity, 0, 100));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new(false, false, 100);
    }
}

public static class ImageConversion
{
    public static void Save(string source, string target, bool jpeg)
    {
        using var image = SkiaSharp.SKBitmap.Decode(source) ?? throw new IOException("Unsupported image");
        using var flat = new SkiaSharp.SKBitmap(image.Width, image.Height);
        using (var canvas = new SkiaSharp.SKCanvas(flat))
        {
            canvas.Clear(jpeg ? SkiaSharp.SKColors.White : SkiaSharp.SKColors.Transparent);
            canvas.DrawBitmap(image, 0, 0);
        }
        using var encoded = flat.Encode(jpeg ? SkiaSharp.SKEncodedImageFormat.Jpeg : SkiaSharp.SKEncodedImageFormat.Png, jpeg ? 85 : 100);
        using var output = File.Create(target);
        encoded.SaveTo(output);
    }
}

public static class Ui
{
    public static Avalonia.Controls.TopLevel? Root =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    public static Avalonia.Input.Platform.IClipboard? Clipboard => Root?.Clipboard;
}
