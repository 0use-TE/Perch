using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Perch.Services;

namespace Perch.ViewModels;

// CPU, memory and disk, sampled once a second with plain Win32 calls (no performance-counter setup, nothing to install).
public sealed class SystemMonitorViewModel : Observable
{
    private const int Samples = 60;
    private readonly Store store;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly double[] cpuHistory = new double[Samples], memoryHistory = new double[Samples], gpuHistory = new double[Samples];
    private long lastIdle, lastKernel, lastUser;
    private bool haveLast;

    public int Cpu { get; private set; }
    public int Memory { get; private set; }
    public int Disk { get; private set; }
    public string CpuText => $"{Cpu}%";
    public string MemoryText => $"{Memory}%";
    public string DiskText => $"{Disk}%";
    public string MemoryDetail { get; private set; } = "";
    public string DiskDetail { get; private set; } = "";
    public string DiskName { get; private set; } = "";
    public string CpuDetail { get; private set; } = "";
    // The optional extras (read from the settings each second, so switching them takes effect at once).
    // They are measured only while the page is on screen.
    public bool Active { get; set; }
    public bool ShowGpu { get; private set; }
    public Avalonia.Controls.GridLength GpuWidth => ShowGpu ? new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
    public string GpuText { get; private set; } = "--";
    public string GpuMemoryText { get; private set; } = "";
    public string GpuTempText { get; private set; } = "";
    public Avalonia.Collections.AvaloniaList<Avalonia.Point> GpuLine { get; private set; } = [];
    private int? cpuTemp;
   
    public string Uptime => Loc.F("Sys.Uptime", Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    public Avalonia.Collections.AvaloniaList<Avalonia.Point> CpuLine { get; private set; } = [];
    public Avalonia.Collections.AvaloniaList<Avalonia.Point> MemoryLine { get; private set; } = [];

    public SystemMonitorViewModel(Store store)
    {
        this.store = store;
        CpuDetail = CoresText();
        timer.Tick += (_, _) => Sample();
        timer.Start();
        Sample();
    }

    private void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleDisk();
        SampleExtras();
        CpuLine = Line(cpuHistory); MemoryLine = Line(memoryHistory); GpuLine = Line(gpuHistory);
        Changed(string.Empty);
    }

    private string CoresText() => Loc.F("Sys.Cores", Environment.ProcessorCount) + (cpuTemp is { } t ? "  ·  " + Loc.F("Sys.Temp", t) : "");

    private void SampleExtras()
    {
        var s = store.State.Settings;
        var source = Hardware.Source;
        var wasShowing = ShowGpu;
        ShowGpu = s.SysGpu;
        if (ShowGpu != wasShowing) Changed(nameof(GpuWidth));
        if (source == null || !Active) { if (!s.SysCpuTemp) cpuTemp = null; CpuDetail = CoresText(); return; }
        cpuTemp = s.SysCpuTemp ? source.CpuTemperature() : null;
        CpuDetail = CoresText();
        if (!ShowGpu) { return; }
        var gpu = source.Gpu();
        if (gpu is not { } g) { GpuText = "--"; GpuMemoryText = Loc.T("Sys.NoGpu"); GpuTempText = ""; Push(gpuHistory, 0); return; }
        GpuText = g.Usage is { } u ? $"{u}%" : "--";
        Push(gpuHistory, g.Usage ?? 0);
        GpuMemoryText = !s.SysVram || g.MemoryUsedGb is not { } used ? ""
            : g.MemoryTotalGb is { } total && total >= used ? Loc.F("Sys.Vram", $"{used:0.0}", $"{total:0.0}") : Loc.F("Sys.VramUsed", $"{used:0.0}");
        GpuTempText = g.Temperature is { } temp ? Loc.F("Sys.GpuTemp", temp) : "";
    }

    private void SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        if (haveLast)
        {
            // Kernel time already includes idle time.
            var busy = (kernel - lastKernel) + (user - lastUser) - (idle - lastIdle);
            var total = (kernel - lastKernel) + (user - lastUser);
            if (total > 0) Cpu = (int)Math.Round(Math.Clamp(busy * 100.0 / total, 0, 100));
        }
        (lastIdle, lastKernel, lastUser, haveLast) = (idle, kernel, user, true);
        Push(cpuHistory, Cpu);
    }

    private void SampleMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return;
        Memory = (int)status.MemoryLoad;
        MemoryDetail = Loc.F("Sys.Used", Gigabytes(status.TotalPhys - status.AvailPhys), Gigabytes(status.TotalPhys));
        Push(memoryHistory, Memory);
    }

    private void SampleDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var used = drive.TotalSize - drive.AvailableFreeSpace;
            Disk = drive.TotalSize > 0 ? (int)Math.Round(used * 100.0 / drive.TotalSize) : 0;
            DiskName = drive.Name.TrimEnd('\\');
            DiskDetail = Loc.F("Sys.Used", Gigabytes(used), Gigabytes(drive.TotalSize));
        }
        catch (Exception)
        {
            // The drive can disappear (network or removable); keep the last numbers.
        }
    }

    // The first reading fills the whole history, so the graph starts flat at the real value instead of climbing from zero.
    private static void Push(double[] history, double value)
    {
        if (history[0] == 0 && history[^1] == 0) Array.Fill(history, value);
        Array.Copy(history, 1, history, 0, history.Length - 1);
        history[^1] = value;
    }

    // A line in a 0..59 by 0..100 box (the page stretches it to fit).
    private static Avalonia.Collections.AvaloniaList<Avalonia.Point> Line(double[] history)
    {
        var points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>();
        for (var i = 0; i < history.Length; i++) points.Add(new Avalonia.Point(i, 100 - Math.Clamp(history[i], 0, 100)));
        return points;
    }

    private static string Gigabytes(ulong bytes) => $"{bytes / 1073741824.0:0.#} GB";
    private static string Gigabytes(long bytes) => $"{bytes / 1073741824.0:0.#} GB";

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? Loc.F("Sys.DaysHours", (int)span.TotalDays, span.Hours) : Loc.F("Sys.HoursMinutes", (int)span.TotalHours, span.Minutes);

    public void Relocalize()
    {
        CpuDetail = CoresText();
        Sample();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
