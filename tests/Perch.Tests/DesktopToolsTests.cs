using System.Diagnostics;
using System.Net;
using System.Text;
using Perch.Services;
using Perch.ViewModels;
#if WINDOWS
using Microsoft.Win32;
using Perch.Platform.Windows;
using Perch.Services.Ports;
#endif

namespace Perch.Tests;

internal static class DesktopToolsTests
{
    public static async Task Run()
    {
        await T.Group("Desktop tools: validation and command lookup", async () =>
        {
            await T.Throws<ArgumentException>(() => { DesktopTools.ValidateVariable("A=B", "x"); return Task.CompletedTask; }, "equals sign cannot enter a variable name");
            await T.Throws<ArgumentException>(() => { DesktopTools.ValidateVariable("X", "x\0y"); return Task.CompletedTask; }, "NUL cannot enter a value");
            await T.Throws<ArgumentException>(() => { DesktopTools.ValidatePort("0"); return Task.CompletedTask; }, "port zero is refused");
            await T.Throws<ArgumentException>(() => { DesktopTools.ValidatePort("65536"); return Task.CompletedTask; }, "out-of-range port is refused");
            await T.Throws<ArgumentException>(() => { DesktopTools.CreateRuleScript("test", "80", "TCP;evil", "Inbound", "Allow", "Private"); return Task.CompletedTask; }, "protocol is a fixed choice, never command text");
            T.Eq("B;A;B", DesktopTools.PathFromLines("B\r\n A \n\nB"), "PATH edits preserve ordering and repeated entries");
            var data = Path.Combine(Path.GetTempPath(), "perch-locate-" + Guid.NewGuid().ToString("N"));
            var one = Directory.CreateDirectory(Path.Combine(data, "one")).FullName;
            var two = Directory.CreateDirectory(Path.Combine(data, "two")).FullName;
            try
            {
                File.WriteAllText(Path.Combine(one, "tool.exe"), "test fixture; never execute");
                File.WriteAllText(Path.Combine(two, "tool.cmd"), "test fixture; never execute");
                var found = DesktopTools.Locate("tool", $"{one};{two};{one}", ".exe;.cmd");
                T.Eq(2, found.Count, "all command matches are found without duplicates");
                T.Eq(Path.Combine(one, "tool.exe"), found[0], "first PATH directory takes precedence");
                T.Eq(0, DesktopTools.Locate("tool && whoami", one, ".exe").Count, "shell text is never executed");
                T.Eq(1, DesktopTools.Locate(Path.Combine(two, "tool.cmd"), one).Count, "an explicit full path is supported");
            }
            finally { Directory.Delete(data, true); }
            T.Check(new EnvironmentEntry("API_TOKEN", "secret", "User").Preview != "secret", "sensitive values are masked in the list");
        });
#if WINDOWS
        await T.Group("Desktop tools: user environment roundtrip", async () =>
        {
            var name = "PERCH_TEST_" + Guid.NewGuid().ToString("N");
            try
            {
                var raw = "%USERPROFILE%\\测试'folder";
                await DesktopTools.SaveEnvironment("User", name, raw);
                T.Eq(raw, DesktopTools.ReadEnvironment().Single(v => v.Name == name && v.Scope == "User").Value, "raw percent references, Unicode and quotes survive saving");
                using (var key = Registry.CurrentUser.OpenSubKey(DesktopTools.UserKey))
                    T.Eq(RegistryValueKind.ExpandString, key!.GetValueKind(name), "expandable values keep their registry type");
                await DesktopTools.SaveEnvironment("User", name, "");
                T.Check(DesktopTools.ReadEnvironment().Any(v => v.Name == name && v.Value == ""), "saving an empty value does not delete the variable");
                await DesktopTools.SaveEnvironment("User", name, null);
                T.Check(!DesktopTools.ReadEnvironment().Any(v => v.Name == name), "delete removes only the test variable");
            }
            finally { await DesktopTools.SaveEnvironment("User", name, null); }
        });

        await T.Group("Desktop tools: process termination", async () =>
        {
            await T.Throws<InvalidOperationException>(() => { PortsViewModel.EndProcess(new PortEntry(1, Environment.ProcessId, "Perch.Tests", null, PortScope.Local, "127.0.0.1", null)); return Task.CompletedTask; }, "the current process cannot be terminated");
            var script = "$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $l.Start(); [Console]::WriteLine($l.LocalEndpoint.Port); Start-Sleep 60";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var child = Process.Start(start)!;
            var previous = PortSource.Current;
            try
            {
                var port = int.Parse((await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
                PortSource.Current = new WindowsPortSource();
                var item = new PortEntry(port, child.Id, "test listener", null, PortScope.Local, "127.0.0.1", null);
                var wrongPort = new PortEntry(port == 65535 ? 1 : port + 1, child.Id, "test listener", null, PortScope.Local, "127.0.0.1", null);
                await T.Throws<InvalidOperationException>(() => { PortsViewModel.EndProcess(wrongPort); return Task.CompletedTask; }, "an unrelated port cannot authorize killing the process");
                PortsViewModel.EndProcess(item);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                T.Check(child.HasExited, "the selected disposable listener can be ended");
            }
            finally { PortSource.Current = previous; if (!child.HasExited) child.Kill(); }
        });

        await T.Group("Desktop tools: firewall boundaries", async () =>
        {
            var system = new FirewallEntry("SystemRule", "System", true, "Inbound", "Allow", "TCP", "80", "Private", "GroupPolicy", false);
            await T.Throws<InvalidOperationException>(() => { DesktopTools.ChangeRuleScript(system, false); return Task.CompletedTask; }, "group policy rules cannot be changed");
            var local = system with { Source = "Local" };
            await T.Throws<InvalidOperationException>(() => { DesktopTools.ChangeRuleScript(local, true); return Task.CompletedTask; }, "foreign local rules cannot be deleted");
            var inbound = DesktopTools.CreateRuleScript("a'; exit 0; 'b", "3000", "TCP", "Inbound", "Allow", "Private");
            T.Check(inbound.Contains("-DisplayName 'a''; exit 0; ''b'"), "rule names stay literal even with quotes and shell punctuation");
            T.Check(inbound.Contains("-LocalPort 3000"), "inbound rules use local ports");
            var outbound = DesktopTools.CreateRuleScript("out", "443", "TCP", "Outbound", "Block", "Private");
            T.Check(outbound.Contains("-RemotePort 443"), "outbound rules use destination ports");
            var wildcard = local with { Id = "Perch-*'", Managed = true };
            T.Check(DesktopTools.ChangeRuleScript(wildcard, true).Contains("$_.Name -ceq 'Perch-*'''"), "rule changes match exact identity, not wildcards");
            var rules = await DesktopTools.ReadFirewall();
            T.Check(rules.All(r => r.Id.Length > 0 && r.Name.Length > 0), "real firewall query produces identified rules without changing them");
            T.Check(rules.All(r => r.Source is "Local" or "GroupPolicy" or "Dynamic" or "Generated" or "Hardcoded"), "policy source is available to enforce read-only rules");
            Console.WriteLine($"Read {rules.Count} firewall rules (no writes performed)");
        });
#endif
    }
}
