using System.Globalization;
using System.IO;
using System.Net;
using Avalonia.Media.Imaging;
using Perch.Services.Ports;

namespace Perch.Platform.Linux;

public sealed class LinuxPortSource : IPortSource
{
    internal readonly record struct Socket(int Port, IPAddress Address, string Inode);

    internal static Socket? Parse(string line)
    {
        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 10 || fields[3] != "0A") return null; // TCP_LISTEN
        var endpoint = fields[1].Split(':');
        if (endpoint.Length != 2 || !int.TryParse(endpoint[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)) return null;
        if (endpoint[0].Length is not (8 or 32)) return null;
        try
        {
            var bytes = Convert.FromHexString(endpoint[0]);
            // /proc exposes each 32-bit word in host byte order, including the four IPv6 words.
            if (BitConverter.IsLittleEndian)
                for (var i = 0; i < bytes.Length; i += 4) Array.Reverse(bytes, i, 4);
            return new(port, new IPAddress(bytes), fields[9]);
        }
        catch (FormatException) { return null; }
    }

    public IReadOnlyList<ListeningPort> Read()
    {
        var sockets = new List<Socket>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try { foreach (var line in File.ReadLines(file)) if (Parse(line) is { } socket) sockets.Add(socket); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var wanted = sockets.Select(s => s.Inode).ToHashSet();
        var owners = new Dictionary<string, (int Pid, string Name, string? Path)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFiles(Path.Combine(dir, "fd")))
                {
                    var link = new FileInfo(fd).LinkTarget;
                    if (link == null || !link.StartsWith("socket:[", StringComparison.Ordinal) || !link.EndsWith(']')) continue;
                    var inode = link[8..^1];
                    if (!wanted.Contains(inode) || owners.ContainsKey(inode)) continue;
                    string? path = null;
                    try { path = new FileInfo(Path.Combine(dir, "exe")).LinkTarget; } catch (IOException) { }
                    var name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    owners[inode] = (pid, name, path);
                }
            }
            catch (IOException) { } // processes can exit between any two reads
            catch (UnauthorizedAccessException) { } // other users' sockets stay visible without owner details
        }
        return sockets.Select(s => owners.TryGetValue(s.Inode, out var owner)
            ? new ListeningPort(owner.Pid, s.Port, s.Address, owner.Name, owner.Path)
            : new ListeningPort(0, s.Port, s.Address, "?", null)).ToList();
    }

    public Bitmap? Icon(string path) => null;
}
