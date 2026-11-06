using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    // 32 px shell icon for a file, folder or shortcut. Frozen, so it can be made on a background thread.
    public static ImageSource? Get(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100);
        if (result == 0 || info.hIcon == 0) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
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
