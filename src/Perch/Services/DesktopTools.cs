using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
#if WINDOWS
using Microsoft.Win32;
using System.Runtime.InteropServices;
#endif

namespace Perch.Services;

public sealed record EnvironmentEntry(string Name, string Value, string Scope)
{
    public bool IsPath => Name.Equals("PATH", StringComparison.OrdinalIgnoreCase);
    public string Preview => DesktopTools.IsSecret(Name) ? "••••••••" : Value;
    public string ScopeText => Loc.T("Tools." + Scope);
}

public sealed record FirewallEntry(string Id, string Name, bool Enabled, string Direction, string Action,
    string Protocol, string Ports, string Profile, string Source, bool Managed)
{
    public bool Editable => Source == "Local";
    public bool Deletable => Editable && Managed;
    public bool HasNumericPort => Ports.Length > 0 && char.IsAsciiDigit(Ports[0]);
    public string StateText => Loc.T(Enabled ? "Tools.Enabled" : "Tools.Disabled");
    public string ToggleText => Loc.T(Enabled ? "Tools.Disable" : "Tools.Enable");
    public string Detail => $"{Protocol} · {(Ports.Length == 0 ? "—" : Ports)} · {Loc.T("Tools." + Direction)} · {Loc.T("Tools." + Action)} · " +
        string.Join(", ", Profile.Split(',', StringSplitOptions.TrimEntries).Select(p => p is "Private" or "Public" or "Domain" or "Any" ? Loc.T("Tools." + p) : p));
    public string Tip => Name + Environment.NewLine + Detail + Environment.NewLine + Loc.T(Editable ? "Tools.LocalRule" : "Tools.PolicyRule");
}

// Registry values stay unexpanded: editing %JAVA_HOME% must not silently replace it with today's path.
// Writes needing elevation run a fixed operation in a short-lived Windows PowerShell process.
public static class DesktopTools
{
    public static bool Supported => OperatingSystem.IsWindows();
    public const string UserKey = "Environment";
    public const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static bool IsSecret(string name) => new[] { "KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static void ValidateVariable(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(['=', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException(Loc.T("Tools.InvalidName"));
        if (value != null && (value.Contains('\0') || value.Length > 32766))
            throw new ArgumentException(Loc.T("Tools.InvalidValue"));
    }

    public static string PathFromLines(string text) => string.Join(';', text.Replace("\r", "").Split('\n')
        .Select(s => s.Trim()).Where(s => s.Length > 0));

    public static bool PathDirectoryExists(string entry)
    {
        var trimmed = entry.Trim().Trim('"');
        if (trimmed.Length == 0) return true;
        try { return Directory.Exists(Environment.ExpandEnvironmentVariables(trimmed)); }
        catch { return false; }
    }

    public static IReadOnlyList<EnvironmentEntry> ReadEnvironment()
    {
#if WINDOWS
        var result = new List<EnvironmentEntry>();
        foreach (var scope in new[] { "User", "Machine" })
        {
            using var key = (scope == "User" ? Registry.CurrentUser : Registry.LocalMachine)
                .OpenSubKey(scope == "User" ? UserKey : MachineKey);
            if (key == null) continue;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                    result.Add(new(name, value, scope));
        }
        return result.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Scope).ToList();
#else
        return [];
#endif
    }

    public static async Task SaveEnvironment(string scope, string name, string? value)
    {
        if (!Supported) throw new PlatformNotSupportedException();
        if (scope is not ("User" or "Machine")) throw new ArgumentException(nameof(scope));
        ValidateVariable(name, value);
#if WINDOWS
        if (scope == "User")
        {
            await Task.Run(() =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(UserKey);
                if (value == null) key.DeleteValue(name, false);
                else
                {
                    var kind = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                        ? key.GetValueKind(name) : value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String;
                    key.SetValue(name, value, kind == RegistryValueKind.ExpandString ? kind : RegistryValueKind.String);
                }
                Broadcast();
            });
        }
        else
        {
            var operation = value == null ? "$k.DeleteValue($n,$false)" :
                $"$v={Literal(value)}; $kind=[Microsoft.Win32.RegistryValueKind]::String; " +
                "if ($k.GetValueNames() -contains $n) {$kind=$k.GetValueKind($n)} elseif ($v.Contains('%')) {$kind=[Microsoft.Win32.RegistryValueKind]::ExpandString}; $k.SetValue($n,$v,$kind)";
            await Elevated($"$k=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey({Literal(MachineKey)},$true); if ($null -eq $k) {{throw 'Missing environment key'}}; $n={Literal(name)}; try {{{operation}}} finally {{$k.Dispose()}}; " + BroadcastScript);
        }
#endif
    }

    // Search only executable files in PATH, never interpret input as a shell command.
    public static IReadOnlyList<string> Locate(string command, string? searchPath = null, string? pathExt = null)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0 || command.IndexOfAny(['\r', '\n', '\0']) >= 0) return [];
        searchPath ??= CurrentPath();
        pathExt ??= Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var allowed = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (Path.HasExtension(command) && !allowed.Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase)) return [];
        var suffixes = Path.HasExtension(command) ? new[] { "" } : allowed;
        var result = new List<string>();
        var folders = Path.IsPathRooted(command) ? new[] { "" } : searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in folders)
            foreach (var suffix in suffixes)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(folder.Trim().Trim('"'), command + suffix));
                    if (File.Exists(candidate) && !result.Contains(candidate, StringComparer.OrdinalIgnoreCase)) result.Add(candidate);
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
            }
        return result;
    }

    private static string CurrentPath()
    {
        if (!Supported) return Environment.GetEnvironmentVariable("PATH") ?? "";
        var values = ReadEnvironment();
        var path = string.Join(';', new[] { "Machine", "User" }.Select(scope =>
            values.FirstOrDefault(v => v.Scope == scope && v.IsPath)?.Value ?? ""));
        var map = values.OrderBy(v => v.Scope == "User" ? 1 : 0).GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 8; i++)
        {
            var expanded = System.Text.RegularExpressions.Regex.Replace(path, "%([^%]+)%", m =>
                map.TryGetValue(m.Groups[1].Value, out var value) ? value : Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
            if (expanded == path) break;
            path = expanded;
        }
        return path;
    }

    public static async Task<IReadOnlyList<FirewallEntry>> ReadFirewall()
    {
        if (!Supported) return [];
        const string script = """
            $ports=@{}; Get-NetFirewallPortFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object {$ports[$_.InstanceID]=$_}
            if ($ports.Count -eq 0) { Get-NetFirewallPortFilter -PolicyStore PersistentStore -ErrorAction SilentlyContinue | ForEach-Object {$ports[$_.InstanceID]=$_} }
            $rows=@(Get-NetFirewallRule -PolicyStore ActiveStore | ForEach-Object {
                $p=$ports[$_.InstanceID]
                if ($null -eq $p) { try {$p=$_ | Get-NetFirewallPortFilter} catch {$p=$null} }
                $portText=($(if ([string]$_.Direction -eq 'Outbound') {$p.RemotePort} else {$p.LocalPort}) -join ',')
                if ([string]::IsNullOrWhiteSpace($portText) -and $null -ne $p -and $null -ne $p.IcmpType) { $portText=($p.IcmpType -join ',') }
                [pscustomobject]@{Id=$_.Name;Name=$_.DisplayName;Enabled=([string]$_.Enabled -eq 'True');Direction=[string]$_.Direction;Action=[string]$_.Action;Protocol=$(if ($null -eq $p) {'—'} else {[string]$p.Protocol});Ports=$portText;Profile=[string]$_.Profile;Source=[string]$_.PolicyStoreSourceType;Managed=($_.Name.StartsWith('Perch-') -and $_.Group -eq 'Perch')}
            }); ConvertTo-Json -InputObject $rows -Compress -Depth 3
            """;
        using var json = JsonDocument.Parse(await PowerShell(script));
        return json.RootElement.EnumerateArray().Select(e => new FirewallEntry(
            S(e, "Id"), S(e, "Name"), e.GetProperty("Enabled").GetBoolean(), S(e, "Direction"), S(e, "Action"),
            S(e, "Protocol"), S(e, "Ports"), S(e, "Profile"), S(e, "Source"), e.GetProperty("Managed").GetBoolean()))
            .OrderByDescending(r => r.Managed).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    public static int ValidatePort(string port) => int.TryParse(port, out var number) && number is >= 1 and <= 65535
        ? number : throw new ArgumentException(Loc.T("Tools.InvalidPort"));

    public static Task CreateFirewall(string name, string port, string protocol, string direction, string action, string profile)
        => Elevated(CreateRuleScript(name, port, protocol, direction, action, profile));

    internal static string CreateRuleScript(string name, string port, string protocol, string direction, string action, string profile)
    {
        var number = ValidatePort(port);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException(Loc.T("Tools.InvalidName"));
        if (protocol is not ("TCP" or "UDP") || direction is not ("Inbound" or "Outbound") || action is not ("Allow" or "Block")
            || profile is not ("Private" or "Public" or "Domain")) throw new ArgumentException("Invalid rule option");
        var portParameter = direction == "Inbound" ? "LocalPort" : "RemotePort";
        return $"New-NetFirewallRule -PolicyStore PersistentStore -Name 'Perch-{Guid.NewGuid():N}' -Group 'Perch' -DisplayName {Literal(name)} -Protocol {protocol} -{portParameter} {number} -Direction {direction} -Action {action} -Profile {profile} -Enabled True | Out-Null";
    }

    public static Task ChangeFirewall(FirewallEntry rule, bool delete)
        => Elevated(ChangeRuleScript(rule, delete));

    internal static string ChangeRuleScript(FirewallEntry rule, bool delete)
    {
        if (!rule.Editable || (delete && !rule.Deletable)) throw new InvalidOperationException(Loc.T("Tools.ManagedOnly"));
        // Exact identity, no wildcard matching. Recheck the rule in the local policy store at the time of the write.
        var select = $"$r=@(Get-NetFirewallRule -PolicyStore PersistentStore | Where-Object {{$_.Name -ceq {Literal(rule.Id)}}}); if ($r.Count -ne 1) {{throw 'Rule changed or disappeared'}}; ";
        return select + (delete
            ? "if ($r[0].Group -ne 'Perch' -or -not $r[0].Name.StartsWith('Perch-')) {throw 'Not a Perch rule'}; $r | Remove-NetFirewallRule"
            : $"$r | Set-NetFirewallRule -Enabled {(rule.Enabled ? "False" : "True")}");
    }

    internal static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
    private static string Encoded(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    private static string ShellPath => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static async Task<string> PowerShell(string script)
    {
        var start = new ProcessStartInfo(ShellPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("PowerShell did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException(Loc.T("Tools.Timeout")); }
        var text = await output;
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
        return text;
    }

    private static async Task Elevated(string script)
    {
        if (!Supported) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo(ShellPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Encoded("$ErrorActionPreference='Stop'; try { " + script + "; exit 0 } catch { exit 1 }");
        using var process = Process.Start(start) ?? throw new IOException("PowerShell did not start");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(Loc.T("Tools.WriteFailed"));
    }

#if WINDOWS
    private const string BroadcastScript = "Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class Notify { [DllImport(\"user32.dll\",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h,uint m,UIntPtr w,string l,uint f,uint t,out UIntPtr r); }'; $result=[UIntPtr]::Zero; [void][Notify]::SendMessageTimeout([IntPtr]65535,26,[UIntPtr]::Zero,'Environment',2,3000,[ref]$result)";
    private static void Broadcast() => SendMessageTimeout(65535, 26, 0, "Environment", 2, 3000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
#endif
}
