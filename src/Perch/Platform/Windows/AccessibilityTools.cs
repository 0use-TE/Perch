using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Perch.Services;
using Perch.Services.Ai;
using Perch.Services.Ai.Agent;

namespace Perch.Platform.Windows;

// Windows UI Automation / Accessibility tools for the AI agent:
// - ui_inspect (Safe): list visible windows or inspect the accessibility control tree of a window.
// - ui_interact (Change): invoke/click or set text on an accessible UI element.
internal static class AccessibilityHelpers
{
    public sealed record WinInfo(nint Hwnd, string Title, string Process, bool Active);

    public static List<WinInfo> ListWindows()
    {
        var fg = GetForegroundWindow();
        var list = new List<WinInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            var proc = "";
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
            list.Add(new WinInfo(hwnd, title, proc, hwnd == fg));
            return true;
        }, 0);
        return list;
    }

    public static nint FindWindow(string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Equals("active", StringComparison.OrdinalIgnoreCase))
            return GetForegroundWindow();
        var windows = ListWindows();
        var match = windows.FirstOrDefault(w => w.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || w.Process.Equals(query, StringComparison.OrdinalIgnoreCase));
        return match?.Hwnd ?? 0;
    }

    public static async Task<string> RunUiaScriptAsync(string script, CancellationToken cancel)
    {
        var wrapped = "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$ProgressPreference='SilentlyContinue';"
            + "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes;" + script;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)));
        using var proc = Process.Start(info) ?? throw new InvalidOperationException("PowerShell could not be started.");
        var outTask = proc.StandardOutput.ReadToEndAsync(cancel);
        var errTask = proc.StandardError.ReadToEndAsync(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await proc.WaitForExitAsync(timeout.Token);
        var output = (await outTask).Trim();
        var error = (await errTask).Trim();
        if (proc.ExitCode != 0 && output.Length == 0) throw new InvalidOperationException(error.Length > 0 ? error : "UI Automation failed.");
        return output;
    }

    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}

public sealed class UiInspectTool : ITool
{
    public ToolSpec Spec { get; } = new("ui_inspect",
        "Inspect desktop windows and accessibility controls via Windows UI Automation. Omit window_title to list open windows; pass window_title (or 'active') to inspect controls inside that window.",
        """{"type":"object","properties":{"window_title":{"type":"string","description":"Optional window title or process name to inspect (or 'active' for foreground window). Omit to list open windows."},"max_depth":{"type":"integer","description":"Tree depth when inspecting a window (1-5, default 3)."}}}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepUiInspect", Agent.Str(arguments, "window_title") ?? "windows");

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var title = Agent.Str(arguments, "window_title")?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            var wins = AccessibilityHelpers.ListWindows().Take(35).ToList();
            var sb = new StringBuilder();
            foreach (var w in wins)
                sb.AppendLine($"{(w.Active ? "[ACTIVE] " : "")}{w.Title}  ({w.Process})");
            return new ToolResult(sb.Length == 0 ? "No visible windows." : sb.ToString().TrimEnd(), $"{wins.Count}");
        }

        var hwnd = AccessibilityHelpers.FindWindow(title);
        if (hwnd == 0) return new ToolResult($"No window matching \"{title}\".", "0");
        var depth = arguments.TryGetProperty("max_depth", out var d) && d.TryGetInt32(out var dv) ? Math.Clamp(dv, 1, 5) : 3;

        var script = $$"""
            $root=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]{{hwnd}})
            if ($null -eq $root) { throw 'Window not accessible' }
            $w=[System.Windows.Automation.TreeWalker]::ControlViewWalker
            $count=0
            function Walk($el,$dep) {
                if ($null -eq $el -or $script:count -ge 80 -or $dep -gt {{depth}}) { return }
                $c=$el.Current
                $type=$c.ControlType.ProgrammaticName -replace '^ControlType\.',''
                $name=$c.Name
                $aid=$c.AutomationId
                $val=''
                try { $vp=$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); if ($vp) { $val=$vp.Current.Value } } catch {}
                if ($name -or $aid -or $val) {
                    $indent=('  ' * $dep)
                    $line="$indent[$type]"
                    if ($name) { $line+=" `"$name`"" }
                    if ($aid) { $line+=" id=$aid" }
                    if ($val) { $v=if ($val.Length -gt 60) { $val.Substring(0,60)+'…' } else { $val }; $line+=" val=`"$v`"" }
                    Write-Output $line
                    $script:count++
                }
                if ($dep -lt {{depth}}) {
                    $child=$w.GetFirstChild($el)
                    while ($null -ne $child -and $script:count -lt 80) {
                        Walk $child ($dep+1)
                        $child=$w.GetNextSibling($child)
                    }
                }
            }
            Walk $root 0
            """;
        var output = await AccessibilityHelpers.RunUiaScriptAsync(script, cancel);
        return new ToolResult(output.Length == 0 ? "No accessible controls found." : output, "UIA");
    }
}

public sealed class UiInteractTool : ITool
{
    public ToolSpec Spec { get; } = new("ui_interact",
        "Interact with an accessible UI control in a Windows application (click/invoke, toggle, focus, or set text).",
        """{"type":"object","properties":{"window_title":{"type":"string","description":"Target window title or process name (or 'active')."},"target":{"type":"string","description":"Control Name or AutomationId."},"action":{"type":"string","enum":["click","set_text","focus"],"description":"Action to perform."},"text":{"type":"string","description":"Text to set when action is set_text."}},"required":["window_title","target","action"]}""");

    public ToolRisk Risk => ToolRisk.Change;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepUiInteract", Agent.Str(arguments, "target") ?? "");
    public string Detail(JsonElement arguments) => $"{Agent.Str(arguments, "action")} \"{Agent.Str(arguments, "target")}\" in \"{Agent.Str(arguments, "window_title")}\"";

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var title = Agent.Str(arguments, "window_title")?.Trim() ?? "active";
        var target = Agent.Str(arguments, "target")?.Trim() ?? "";
        var action = (Agent.Str(arguments, "action") ?? "click").Trim().ToLowerInvariant();
        var text = Agent.Str(arguments, "text") ?? "";
        if (target.Length == 0) throw new ArgumentException("target is required");

        var hwnd = AccessibilityHelpers.FindWindow(title);
        if (hwnd == 0) throw new InvalidOperationException($"No window matching \"{title}\".");

        var escTarget = target.Replace("'", "''");
        var escText = text.Replace("'", "''");
        var script = $$"""
            $root=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]{{hwnd}})
            if ($null -eq $root) { throw 'Window not accessible' }
            $condName=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '{{escTarget}}')
            $condId=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '{{escTarget}}')
            $el=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.OrCondition($condName, $condId)))
            if ($null -eq $el) { throw "Control '{{escTarget}}' not found" }
            if ('{{action}}' -eq 'focus') {
                $el.SetFocus()
                Write-Output 'Focused'
            } elseif ('{{action}}' -eq 'set_text') {
                try {
                    $vp=$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                    $vp.SetValue('{{escText}}')
                    Write-Output 'Text set'
                } catch { throw 'Control does not support ValuePattern' }
            } else {
                try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'Invoked'; exit 0 } catch {}
                try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Write-Output 'Toggled'; exit 0 } catch {}
                try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Output 'Selected'; exit 0 } catch {}
                try { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Write-Output 'Expanded'; exit 0 } catch {}
                throw 'Control does not support Invoke/Toggle/Selection/Expand'
            }
            """;
        var output = await AccessibilityHelpers.RunUiaScriptAsync(script, cancel);
        return new ToolResult($"{action} on \"{target}\": {output}", output);
    }
}
