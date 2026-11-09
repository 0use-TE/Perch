using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media.Imaging;
using Perch.Services.Ports;

namespace Perch.Platform.Windows;

// Listening TCP ports with their owning process, from the IP Helper API (the same table `netstat -ano` prints).
internal sealed class WindowsPortSource : IPortSource
{
    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3, Success = 0;
    private readonly Dictionary<int, (string Name, string? Path)> processes = [];
    private readonly Dictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ListeningPort> Read()
    {
        var found = new List<(int Pid, int Port, IPAddress Address)>();
        Table(AfInet, 24, 4, 8, 20, 4, found);
        Table(AfInet6, 56, 0, 20, 52, 16, found);

        // Process names and paths change rarely; look each process up once and forget the ones that are gone.
        var alive = new HashSet<int>(found.Select(f => f.Pid));
        foreach (var gone in processes.Keys.Where(pid => !alive.Contains(pid)).ToList()) processes.Remove(gone);
        var result = new List<ListeningPort>(found.Count);
        foreach (var (pid, port, address) in found)
        {
            if (!processes.TryGetValue(pid, out var info)) processes[pid] = info = Describe(pid);
            result.Add(new ListeningPort(pid, port, address, info.Name, info.Path));
        }
        return result;
    }

    public Bitmap? Icon(string path)
    {
        if (!icons.TryGetValue(path, out var icon)) icons[path] = icon = ShellIcons.Get(path);
        return icon;
    }

    // Reads one table. Offsets are within a row: where the local address is, the local port, the owning process, and
    // how many bytes the address takes (4 for IPv4, 16 for IPv6).
    private static void Table(int family, int rowSize, int addressOffset, int portOffset, int pidOffset, int addressSize, List<(int, int, IPAddress)> into)
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != Success) return;
            var count = Marshal.ReadInt32(buffer);
            var address = new byte[addressSize];
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                Marshal.Copy(row + addressOffset, address, 0, addressSize);
                var raw = Marshal.ReadInt32(row + portOffset);
                var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order in the low 16 bits
                into.Add((Marshal.ReadInt32(row + pidOffset), port, new IPAddress(address)));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (string Name, string? Path) Describe(int pid)
    {
        if (pid == 0) return ("System Idle Process", null);
        if (pid == 4) return ("System", null);
        var path = OpenedImagePath(pid) ?? UnopenedImagePath(pid);
        if (path != null) return (Path.GetFileName(path), path);
        try { return (Process.GetProcessById(pid).ProcessName + ".exe", null); }
        catch (Exception) { return ($"PID {pid}", null); } // exited meanwhile, or not allowed to look
    }

    // The program file of a process we are allowed to open.
    private static string? OpenedImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == 0) return null;
        try
        {
            var text = new StringBuilder(1024);
            var length = text.Capacity;
            return QueryFullProcessImageName(handle, 0, text, ref length) ? text.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    // Services and other protected processes cannot be opened from a normal account, but Windows will still say which
    // file they run (this is how Task Manager shows them): ask for the image name by process id instead.
    private static string? UnopenedImagePath(int pid)
    {
        const int capacity = 2048;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var info = new ProcessIdInformation { ProcessId = pid, ImageName = new UnicodeString { Length = 0, MaximumLength = capacity, Buffer = buffer } };
            if (NtQuerySystemInformation(88 /* SystemProcessIdInformation */, ref info, Marshal.SizeOf<ProcessIdInformation>(), out _) != 0 || info.ImageName.Length == 0) return null;
            return ToDosPath(Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2));
        }
        catch (Exception) { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // \Device\HarddiskVolume3\Windows\... -> C:\Windows\...
    private static string? ToDosPath(string? ntPath)
    {
        if (ntPath == null) return null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\');
            var device = new StringBuilder(512);
            if (QueryDosDevice(letter, device, device.Capacity) == 0) continue;
            var prefix = device.ToString();
            if (ntPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return letter + ntPath[prefix.Length..];
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public nint Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIdInformation { public nint ProcessId; public UnicodeString ImageName; }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref ProcessIdInformation info, int size, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int QueryDosDevice(string name, StringBuilder target, int max);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sort, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
