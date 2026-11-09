using System.Runtime.InteropServices;
using Microsoft.Win32;
using Perch.Services;

namespace Perch.Platform.Windows;

// CPU temperature, GPU usage and video memory from Windows' own performance counters (the same ones Task Manager reads, so
// nothing to install and no administrator rights), plus the GPU temperature through NVIDIA's driver library when present.
public sealed class WindowsHardwareSource : IHardwareSource
{
    private Counters? thermal, engines, memory;
    private bool thermalFailed, gpuFailed;
    private double? lastThermal;
    private int still;
    private double? totalGb;
    private bool totalRead;
    private Nvml? nvml;
    private bool nvmlTried;

    public int? CpuTemperature()
    {
        if (thermalFailed) return null;
        try
        {
            thermal ??= Counters.Open("\\Thermal Zone Information(*)\\Temperature");
            if (thermal == null) { thermalFailed = true; return null; }
            double? best = null;
            foreach (var (_, kelvin) in thermal.Read())
            {
                var celsius = kelvin - 273.15;
                if (celsius is > 1 and < 150 && (best == null || celsius > best)) best = celsius;
            }
            if (best == null) return null;
            // Many desktops expose one fixed, made-up zone (always 27.85 °C); a reading that never moves is not a real sensor.
            if (lastThermal is { } last && Math.Abs(last - best.Value) < 0.01) still++; else still = 0;
            lastThermal = best;
            return still >= 20 ? null : (int)Math.Round(best.Value);
        }
        catch (Exception) { thermalFailed = true; return null; }
    }

    public GpuReading? Gpu()
    {
        if (gpuFailed) return null;
        try
        {
            engines ??= Counters.Open("\\GPU Engine(*)\\Utilization Percentage");
            memory ??= Counters.Open("\\GPU Adapter Memory(*)\\Dedicated Usage");
            if (engines == null && memory == null) { gpuFailed = true; return null; }

            // Per adapter: the busiest kind of engine (3D, copy, video ...) is the adapter's load, as in Task Manager.
            var perType = new Dictionary<(string Luid, string Type), double>();
            if (engines != null)
                foreach (var (name, value) in engines.Read())
                {
                    var luid = Between(name, "luid_", "_phys");
                    var at = name.LastIndexOf("engtype_", StringComparison.Ordinal);
                    var type = at >= 0 ? name[(at + 8)..] : "";
                    if (luid.Length == 0) continue;
                    perType[(luid, type)] = perType.GetValueOrDefault((luid, type)) + value;
                }
            var load = new Dictionary<string, double>();
            foreach (var ((luid, _), value) in perType) load[luid] = Math.Max(load.GetValueOrDefault(luid), value);

            // The adapter holding the most video memory is the one worth showing (a discrete card beats the integrated one).
            string? chosen = null;
            double used = 0;
            if (memory != null)
                foreach (var (name, bytes) in memory.Read())
                {
                    var luid = Between(name, "luid_", "_phys");
                    if (luid.Length > 0 && (chosen == null || bytes > used)) { chosen = luid; used = bytes; }
                }
            if (chosen == null && load.Count > 0) chosen = load.MaxBy(p => p.Value).Key;
            if (chosen == null) return null;

            int? usage = load.TryGetValue(chosen, out var u) ? (int)Math.Round(Math.Clamp(u, 0, 100)) : null;
            return new GpuReading(usage, memory == null ? null : used / 1073741824.0, TotalGb(), GpuTemperature());
        }
        catch (Exception) { gpuFailed = true; return null; }
    }

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        if (a < 0) return "";
        a += start.Length;
        var b = text.IndexOf(end, a, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }

    // The driver records each adapter's dedicated memory in the registry; the largest one is the discrete card.
    private double? TotalGb()
    {
        if (totalRead) return totalGb;
        totalRead = true;
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey == null) return null;
            long best = 0;
            foreach (var name in classKey.GetSubKeyNames())
            {
                if (name.Length != 4 || !char.IsDigit(name[0])) continue;
                using var sub = classKey.OpenSubKey(name);
                var value = sub?.GetValue("HardwareInformation.qwMemorySize");
                long size = value switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    _ => 0,
                };
                if (size > best) best = size;
            }
            if (best > 0) totalGb = best / 1073741824.0;
        }
        catch (Exception) { totalGb = null; }
        return totalGb;
    }

    private int? GpuTemperature()
    {
        if (!nvmlTried) { nvmlTried = true; nvml = Nvml.Load(); }
        return nvml?.Temperature();
    }

    // ---- performance counters (PDH)

    private sealed class Counters
    {
        private IntPtr query, counter;

        public static Counters? Open(string path)
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0) return null;
            if (PdhAddEnglishCounterW(q, path, IntPtr.Zero, out var c) != 0) { PdhCloseQuery(q); return null; }
            PdhCollectQueryData(q); // rates need a first reading to compare with
            return new Counters { query = q, counter = c };
        }

        // Every instance of the (wildcard) counter with its current value.
        public List<(string Name, double Value)> Read()
        {
            var list = new List<(string, double)>();
            if (PdhCollectQueryData(query) != 0) return list;
            uint size = 0, count = 0;
            PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero); // asks for the size
            if (size == 0) return list;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer) != 0) return list;
                var step = Marshal.SizeOf<Item>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<Item>(buffer + i * step);
                    if (item.Status is not (0 or 1) || item.Name == IntPtr.Zero) continue; // valid data only
                    list.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return list;
        }

        private const uint PdhFmtDouble = 0x200;

        [StructLayout(LayoutKind.Sequential)]
        private struct Item { public IntPtr Name; public uint Status; public double Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    }

    // ---- NVIDIA's management library (ships with the display driver); absent on other GPUs, which then show no temperature.

    private sealed unsafe class Nvml
    {
        private IntPtr device;
        private delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> getTemperature;

        public static Nvml? Load()
        {
            try
            {
                var lib = IntPtr.Zero;
                foreach (var name in new[] { "nvml.dll", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll" })
                    if (NativeLibrary.TryLoad(name, out lib)) break;
                if (lib == IntPtr.Zero) return null;
                if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out var init) || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out var byIndex)
                    || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetTemperature", out var temp)) return null;
                if (((delegate* unmanaged[Cdecl]<int>)init)() != 0) return null;
                IntPtr handle;
                if (((delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)byIndex)(0, &handle) != 0) return null;
                return new Nvml { device = handle, getTemperature = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)temp };
            }
            catch (Exception) { return null; }
        }

        public int? Temperature()
        {
            uint t;
            return getTemperature(device, 0, &t) == 0 ? (int)t : null; // 0 = the GPU's core sensor
        }
    }
}
