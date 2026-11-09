namespace Perch.Services;

// What the system page shows beyond CPU, memory and disk. Each platform supplies its own source; every value is optional
// because many machines simply do not report it (no temperature sensor exposed, no GPU, ...).
public readonly record struct GpuReading(int? Usage, double? MemoryUsedGb, double? MemoryTotalGb, int? Temperature);

public interface IHardwareSource
{
    // Degrees Celsius of the processor (or the machine's hottest reported thermal zone), if the system reports one.
    int? CpuTemperature();
    // Utilisation, video memory and temperature of the busiest graphics adapter.
    GpuReading? Gpu();
}

public static class Hardware
{
    public static IHardwareSource? Source { get; set; }
}
