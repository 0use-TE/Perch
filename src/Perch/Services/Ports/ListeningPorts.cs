using System.Net;
using Avalonia.Media.Imaging;

namespace Perch.Services.Ports;

// How far a listening port can be reached: only from this machine, from every network interface, or on one address.
public enum PortScope { Local, All, Address }

// One socket in the listening state, as the operating system reports it.
public sealed record ListeningPort(int Pid, int Port, IPAddress Address, string ProcessName, string? ProcessPath);

// Where the list comes from. Windows reads the TCP table of the IP Helper API; a Linux version would read
// /proc/net/tcp and /proc/<pid>/fd. Everything else (grouping, filtering, the page) is shared.
public interface IPortSource
{
    IReadOnlyList<ListeningPort> Read();
    // The icon of the program a process runs, or null when it has none or the path is unknown.
    Bitmap? Icon(string path);
}

public static class PortSource
{
    public static IPortSource? Current { get; set; }

    public static PortScope ScopeOf(IPAddress address) =>
        IPAddress.IsLoopback(address) ? PortScope.Local
        : address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? PortScope.All
        : PortScope.Address;
}
