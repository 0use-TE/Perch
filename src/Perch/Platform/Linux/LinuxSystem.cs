using System.Globalization;
using System.IO;
using Perch.Services;

namespace Perch.Platform.Linux;

internal static class LinuxSystem
{
    internal static bool Cpu(out long idle, out long total)
    {
        idle = total = 0;
        try { return ParseCpu(File.ReadLines("/proc/stat").First(), out idle, out total); }
        catch (IOException) { return false; }
    }

    internal static bool ParseCpu(string line, out long idle, out long total)
    {
        idle = total = 0;
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") return false;
        // guest and guest_nice (fields 9+) are already included in user/nice.
        for (var i = 1; i < Math.Min(fields.Length, 9); i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            total += value;
            if (i is 4 or 5) idle += value; // idle + iowait
        }
        return true;
    }

    internal static bool Memory(out ulong total, out ulong available)
    {
        total = available = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !ulong.TryParse(fields[1], out var kb)) continue;
                if (fields[0] == "MemTotal:") total = kb * 1024;
                if (fields[0] == "MemAvailable:") available = kb * 1024;
            }
            available = Math.Min(available, total);
            return total > 0;
        }
        catch (IOException) { return false; }
    }
}

internal sealed class LinuxHardwareSource : IHardwareSource
{
    public int? CpuTemperature()
    {
        try
        {
            foreach (var device in Directory.EnumerateDirectories("/sys/class/hwmon"))
            {
                var name = File.ReadAllText(Path.Combine(device, "name")).Trim();
                if (name is not ("coretemp" or "k10temp" or "zenpower")) continue;
                var readings = Directory.EnumerateFiles(device, "temp*_input")
                    .Select(path => int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value / 1000 : -1)
                    .Where(value => value is > 0 and < 130).ToArray();
                if (readings.Length > 0) return readings.Max();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public GpuReading? Gpu() => null; // Driver-specific GPU counters are outside this initial port.
}
