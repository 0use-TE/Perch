using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Perch.Services;

namespace Perch.ViewModels;

// CPU, memory and disk, sampled once a second with plain Win32 calls (no performance-counter setup, nothing to install).
public sealed class SystemMonitorViewModel : Observable
{
    private const int Samples = 60;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly double[] cpuHistory = new double[Samples], memoryHistory = new double[Samples];
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
    public string Uptime => Loc.F("Sys.Uptime", Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    public Avalonia.Collections.AvaloniaList<Avalonia.Point> CpuLine { get; private set; } = [];
    public Avalonia.Collections.AvaloniaList<Avalonia.Point> MemoryLine { get; private set; } = [];

    public SystemMonitorViewModel()
    {
        CpuDetail = Loc.F("Sys.Cores", Environment.ProcessorCount);
        timer.Tick += (_, _) => Sample();
        timer.Start();
        Sample();
    }

    private void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleDisk();
        CpuLine = Line(cpuHistory); MemoryLine = Line(memoryHistory);
        Changed(string.Empty);
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
        CpuDetail = Loc.F("Sys.Cores", Environment.ProcessorCount);
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
