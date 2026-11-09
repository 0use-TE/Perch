using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Perch.Platform.Linux;
using Perch.Services;

// No test-framework dependency; exits nonzero on the first failed assertion.
if (args.Length == 2 && args[0] == "--instance")
{
    using var instance = new SingleInstance(args[1]);
    Console.WriteLine(instance.IsFirst ? "first" : "second");
    if (!instance.IsFirst) instance.Wake();
    return;
}

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
    Console.WriteLine("PASS " + message);
}

Check(LinuxSystem.ParseCpu("cpu 100 20 30 400 50 6 7 8 90 10", out var idle, out var total) && idle == 450 && total == 621,
    "CPU accounting includes iowait and does not double count guest time");
Check(!LinuxSystem.ParseCpu("cpu malformed", out _, out _), "Malformed CPU samples are rejected");
Check(LinuxSystem.Memory(out var memory, out var available) && memory > 0 && available <= memory, "Live /proc/meminfo is usable");

var ipv4 = LinuxPortSource.Parse("0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000 1000 0 1234");
Check(ipv4 is { Port: 8080, Inode: "1234" } && ipv4.Value.Address.Equals(IPAddress.Loopback), "IPv4 proc address and port byte order");
var ipv6 = LinuxPortSource.Parse("0: 00000000000000000000000001000000:01BB 00000000000000000000000000000000:0000 0A 0:0 0:0 0 1000 0 5678");
Check(ipv6 is { Port: 443 } && ipv6.Value.Address.Equals(IPAddress.IPv6Loopback), "IPv6 proc address byte order");
Check(LinuxPortSource.Parse("0: 0100007F:1F90 00000000:0000 01 0:0 0:0 0 1000 0 1234") == null, "Established sockets are not reported as listeners");
Check(LinuxPortSource.Parse("0: ZZZZZZZZ:FFFF 00000000:0000 0A 0:0 0:0 0 1000 0 1234") == null, "Malformed addresses are ignored");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Check(new LinuxPortSource().Read().Any(p => p.Port == port && p.Pid == Environment.ProcessId && p.Address.Equals(IPAddress.Loopback)), "Live listener is attributed to this test process");
}

var root = Path.Combine(Path.GetTempPath(), "perch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
var oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
var oldDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
try
{
    var user = Path.Combine(root, "user");
    var system = Path.Combine(root, "system");
    Directory.CreateDirectory(Path.Combine(user, "applications"));
    Directory.CreateDirectory(Path.Combine(system, "applications"));
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", user);
    Environment.SetEnvironmentVariable("XDG_DATA_DIRS", system);
    Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "TestDesktop");
    File.WriteAllText(Path.Combine(system, "applications", "hidden.desktop"), "[Desktop Entry]\nType=Application\nName=System app\nExec=true\n");
    File.WriteAllText(Path.Combine(user, "applications", "hidden.desktop"), "[Desktop Entry]\nHidden=true\n");
    File.WriteAllText(Path.Combine(user, "applications", "visible.desktop"), "[Desktop Entry]\nType=Application\nName=Visible\nExec=true\nOnlyShowIn=TestDesktop;\n[Desktop Action New]\nName=Wrong name\n");
    File.WriteAllText(Path.Combine(system, "applications", "other.desktop"), "[Desktop Entry]\nType=Application\nName=Other\nExec=true\nOnlyShowIn=OtherDesktop;\n");
    var entries = DesktopEntries.Scan();
    Check(entries.Count == 1 && entries[0].Name == "Visible", "XDG overrides, Hidden, desktop filtering and action sections");

    using var first = new SingleInstance(Path.Combine(root, "State"));
    Check(first.IsFirst, "First instance owns the data directory");
    using var wake = new ManualResetEventSlim();
    first.Listen(wake.Set);
    string Child(string folder)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") info.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--instance"); info.ArgumentList.Add(folder);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        Check(process.WaitForExit(8000) && process.ExitCode == 0, "Instance helper exits successfully");
        return output.Trim();
    }
    Check(Child(Path.Combine(root, "State")) == "second" && wake.Wait(3000), "A second process wakes the existing instance");
    Check(Child(Path.Combine(root, "state")) == "first", "Linux data directories remain case-sensitive");
}
finally
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
    Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
    Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", oldDesktop);
    Directory.Delete(root, true);
}
Console.WriteLine($"{count} assertions passed.");
