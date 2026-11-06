using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Win32;

namespace Perch.Platform.Windows;

// Shell and imaging calls want a single-threaded apartment, which thread-pool threads are not.
public static class StaTask
{
    public static Task Run(Action work)
    {
        var done = new TaskCompletionSource();
        var thread = new System.Threading.Thread(() =>
        {
            try { work(); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

public static class ShellIcons
{
    // 32 px shell icon for a file, folder or shortcut. Safe to call from a background thread.
    public static Bitmap? Get(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100);
        if (result == 0 || info.hIcon == 0) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var picture = icon.ToBitmap();
            using var stream = new MemoryStream();
            picture.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}

public static class ShellActions
{
    public static bool Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
        catch { return false; }
    }

    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // Runs a user-defined command line through cmd without a console window.
    public static bool Run(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            return true;
        }
        catch { return false; }
    }
}

public static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue("Perch", $"\"{exe}\"");
            else key.DeleteValue("Perch", false);
        }
        catch { }
    }
}

public static class UserInput
{
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}

// Reports when a full-screen app (video, game, presentation) covers the primary monitor.
public sealed class FullscreenWatcher : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly nint self;
    public bool IsFullscreen { get; private set; }
    public event Action? Changed;

    public FullscreenWatcher(nint self)
    {
        this.self = self;
        timer.Tick += (_, _) => Check();
        timer.Start();
    }

    private void Check()
    {
        var full = Detect();
        if (full == IsFullscreen) return;
        IsFullscreen = full;
        Changed?.Invoke();
    }

    private bool Detect()
    {
        var window = GetForegroundWindow();
        if (window == 0 || window == self || window == GetShellWindow() || window == GetDesktopWindow()) return false;
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;
        if (!GetWindowRect(window, out var rect)) return false;
        var monitor = MonitorFromWindow(window, 2);
        if (monitor != MonitorFromPoint(new POINT(), 1)) return false; // the island only lives on the primary monitor
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    public void Dispose() => timer.Stop();

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}

// The Windows "asterisk" notification sound.
public static class Beep
{
    public static void Play() => MessageBeep(0x40);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
}

// Battery state (the same data the Windows power flyout reads).
public static class PowerInfo
{
    public readonly record struct Reading(bool HasBattery, bool Charging, int Percent);

    public static Reading Read()
    {
        if (!GetSystemPowerStatus(out var status)) return new(false, false, 100);
        var has = status.BatteryFlag != 128 && status.BatteryFlag != 255; // 128 = no battery, 255 = unknown
        var percent = status.BatteryLifePercent == 255 ? 100 : Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        return new(has, status.ACLineStatus == 1, percent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

// Saves a PNG or JPEG copy of an image (JPEG at quality 85, which also shrinks large images).
public static class ImageConversion
{
    public static void Save(string source, string target, bool jpeg)
    {
        using var image = System.Drawing.Image.FromFile(source);
        if (!jpeg) { image.Save(target, System.Drawing.Imaging.ImageFormat.Png); return; }
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var flat = new System.Drawing.Bitmap(image.Width, image.Height);
        using (var g = System.Drawing.Graphics.FromImage(flat)) { g.Clear(System.Drawing.Color.White); g.DrawImage(image, 0, 0, image.Width, image.Height); }
        flat.Save(target, codec, parameters);
    }
}

// Reaches the UI thread's top-level window from code that has no view (clipboard, screen metrics).
public static class Ui
{
    public static Avalonia.Controls.TopLevel? Root =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    public static Avalonia.Input.Platform.IClipboard? Clipboard => Root?.Clipboard;
}
