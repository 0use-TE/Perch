using System.Text;
using System.Text.Json;
using System.Threading;
using Avalonia.Threading;
using Perch.Services.Ports;
using Perch.ViewModels;

namespace Perch.Services.Ai.Agent;

public sealed class PerchContext(
    TodoViewModel todo,
    NotesViewModel notes,
    AlarmViewModel alarm,
    FocusViewModel focus,
    ClipboardViewModel clipboard,
    PortsViewModel ports)
{
    public TodoViewModel Todo { get; } = todo;
    public NotesViewModel Notes { get; } = notes;
    public AlarmViewModel Alarm { get; } = alarm;
    public FocusViewModel Focus { get; } = focus;
    public ClipboardViewModel Clipboard { get; } = clipboard;
    public PortsViewModel Ports { get; } = ports;
}

// Reads Perch's own data (todos, notes, alarms, focus timer, clipboard history, listening ports, non-secret env vars).
public sealed class PerchReadTool(PerchContext ctx) : ITool
{
    public ToolSpec Spec { get; } = new("perch_read",
        "Read data from Perch itself: todos, notes, alarms, focus timer status, recent clipboard text, listening network ports, environment variables, or recent Windows notifications.",
        """{"type":"object","properties":{"kind":{"type":"string","enum":["todos","notes","alarms","focus","clipboard","ports","env","notifications"],"description":"Which Perch data to read."},"query":{"type":"string","description":"Optional filter text."}},"required":["kind"]}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepPerchRead", Agent.Str(arguments, "kind") ?? "");

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var kind = (Agent.Str(arguments, "kind") ?? "").Trim().ToLowerInvariant();
        var query = (Agent.Str(arguments, "query") ?? "").Trim();
        bool Match(string s) => query.Length == 0 || s.Contains(query, StringComparison.OrdinalIgnoreCase);

        if (kind == "ports")
        {
            var list = (PortSource.Current?.Read() ?? [])
                .GroupBy(p => (p.Pid, p.Port))
                .Select(g => g.First())
                .Where(p => Match($"{p.Port} {p.ProcessName} {p.Pid}"))
                .OrderBy(p => p.Port)
                .Take(40)
                .ToList();
            if (list.Count == 0) return new ToolResult("No matching listening ports.", "0");
            var sb = new StringBuilder();
            foreach (var p in list)
                sb.AppendLine($"Port {p.Port} | {p.ProcessName} (PID {p.Pid}) | {p.Address}");
            return new ToolResult(sb.ToString().TrimEnd(), $"{list.Count}");
        }

        if (kind == "env")
        {
            var vars = DesktopTools.ReadEnvironment()
                .Where(v => Match(v.Name))
                .Take(40)
                .ToList();
            if (vars.Count == 0) return new ToolResult("No matching environment variables.", "0");
            var sb = new StringBuilder();
            foreach (var v in vars)
                sb.AppendLine($"[{v.Scope}] {v.Name} = {v.Preview}");
            return new ToolResult(sb.ToString().TrimEnd(), $"{vars.Count}");
        }

        if (kind == "notifications")
        {
#if WINDOWS
            var notes = Perch.Platform.Windows.SystemNotifications.Recent
                .Where(n => Match($"{n.App} {n.Title} {n.Body}"))
                .Take(20)
                .ToList();
            if (notes.Count == 0) return new ToolResult("No recent Windows notifications.", "0");
            var sb = new StringBuilder();
            foreach (var n in notes)
                sb.AppendLine($"[{n.Time:HH:mm}] {(string.IsNullOrEmpty(n.App) ? "" : n.App + ": ")}{n.Title} {n.Body}".Trim());
            return new ToolResult(sb.ToString().TrimEnd(), $"{notes.Count}");
#else
            return new ToolResult("System notifications are only supported on Windows.", "0");
#endif
        }

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var sb = new StringBuilder();
            switch (kind)
            {
                case "todos":
                    var todos = ctx.Todo.All.Where(t => Match(t.Text)).Take(40).ToList();
                    foreach (var t in todos)
                        sb.AppendLine($"[{(t.Done ? "x" : " ")}] {t.Text}" + (t.HasDue ? $" (due: {t.Due:yyyy-MM-dd})" : ""));
                    return new ToolResult(todos.Count == 0 ? "No todos." : sb.ToString().TrimEnd(), $"{todos.Count}");

                case "notes":
                    var notes = ctx.Notes.Items.Where(n => !n.IsBlank && Match(n.Title + " " + n.Text)).Take(20).ToList();
                    foreach (var n in notes)
                    {
                        sb.AppendLine($"## {n.DisplayTitle} ({n.UpdatedLabel})");
                        if (!string.IsNullOrWhiteSpace(n.Text)) sb.AppendLine(n.Text.Length > 400 ? n.Text[..400] + "…" : n.Text);
                    }
                    return new ToolResult(notes.Count == 0 ? "No notes." : sb.ToString().TrimEnd(), $"{notes.Count}");

                case "alarms":
                    var alarms = ctx.Alarm.Items.ToList();
                    foreach (var a in alarms)
                        sb.AppendLine($"{a.TimeText} | {a.Name} | {(a.Enabled ? "Enabled" : "Disabled")} | {a.DaysText}");
                    return new ToolResult(alarms.Count == 0 ? "No alarms." : sb.ToString().TrimEnd(), $"{alarms.Count}");

                case "focus":
                    return new ToolResult($"Phase: {ctx.Focus.PhaseLabel}, Running: {ctx.Focus.Running}, Remaining: {ctx.Focus.Remaining}, Today sessions: {ctx.Focus.TodaySessions}", ctx.Focus.Remaining);

                case "clipboard":
                    var clips = ctx.Clipboard.Items.Where(c => !c.IsImage && Match(c.Text)).Take(15).ToList();
                    foreach (var c in clips)
                        sb.AppendLine($"[{c.TimeLabel}]{(c.Pinned ? " [Pinned]" : "")} {(c.Text.Length > 300 ? c.Text[..300] + "…" : c.Text)}");
                    return new ToolResult(clips.Count == 0 ? "Clipboard history is empty." : sb.ToString().TrimEnd(), $"{clips.Count}");

                default:
                    throw new ArgumentException($"Unknown kind: {kind}");
            }
        });
    }
}

// Performs actions inside Perch: add/complete todos, create notes, add alarms, control focus timer.
public sealed class PerchActionTool(PerchContext ctx) : ITool
{
    public ToolSpec Spec { get; } = new("perch_action",
        "Control Perch features: add or complete a todo, create a note, add an alarm, or start/pause/reset the focus timer.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["add_todo","complete_todo","add_note","add_alarm","focus_start","focus_pause","focus_reset"]},"title":{"type":"string","description":"Note title or alarm label."},"text":{"type":"string","description":"Todo text or note body."},"due":{"type":"string","description":"Optional todo due date YYYY-MM-DD."},"hour":{"type":"integer","description":"Alarm hour (0-23)."},"minute":{"type":"integer","description":"Alarm minute (0-59)."}},"required":["action"]}""");

    public ToolRisk Risk => ToolRisk.Change;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepPerchAction", Agent.Str(arguments, "action") ?? "");
    public string Detail(JsonElement arguments)
    {
        var action = Agent.Str(arguments, "action") ?? "";
        var text = Agent.Str(arguments, "text") ?? Agent.Str(arguments, "title") ?? "";
        return text.Length > 0 ? $"{action}: {text}" : action;
    }

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var action = (Agent.Str(arguments, "action") ?? "").Trim().ToLowerInvariant();
        var title = Agent.Str(arguments, "title")?.Trim() ?? "";
        var text = Agent.Str(arguments, "text")?.Trim() ?? "";
        var dueStr = Agent.Str(arguments, "due")?.Trim();
        int? hour = arguments.TryGetProperty("hour", out var h) && h.TryGetInt32(out var hv) ? hv : null;
        int? minute = arguments.TryGetProperty("minute", out var m) && m.TryGetInt32(out var mv) ? mv : null;

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            switch (action)
            {
                case "add_todo":
                    if (text.Length == 0) throw new ArgumentException("text is required");
                    DateTime? due = DateTime.TryParse(dueStr, out var d) ? d.Date : null;
                    ctx.Todo.Draft = text;
                    ctx.Todo.Add.Execute(null);
                    if (due != null && ctx.Todo.All.LastOrDefault() is { } added) added.Due = due;
                    return new ToolResult($"Added todo: {text}", text);

                case "complete_todo":
                    if (text.Length == 0) throw new ArgumentException("text is required");
                    var target = ctx.Todo.All.FirstOrDefault(t => !t.Done && t.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
                    if (target == null) return new ToolResult($"No matching open todo for \"{text}\".", "not found");
                    target.Done = true;
                    return new ToolResult($"Completed todo: {target.Text}", target.Text);

                case "add_note":
                    if (title.Length == 0 && text.Length == 0) throw new ArgumentException("title or text is required");
                    ctx.Notes.New.Execute(null);
                    if (ctx.Notes.Selected is { } note) { note.Title = title; note.Text = text; }
                    return new ToolResult($"Created note: {(title.Length > 0 ? title : text)}", title.Length > 0 ? title : "note");

                case "add_alarm":
                    if (hour is null or < 0 or > 23 || minute is null or < 0 or > 59) throw new ArgumentException("valid hour (0-23) and minute (0-59) are required");
                    ctx.Alarm.Add.Execute(null);
                    ctx.Alarm.EditHour = hour.Value;
                    ctx.Alarm.EditMinute = minute.Value;
                    ctx.Alarm.EditLabel = title;
                    ctx.Alarm.Save.Execute(null);
                    return new ToolResult($"Added alarm at {hour.Value:00}:{minute.Value:00}" + (title.Length > 0 ? $" ({title})" : ""), $"{hour.Value:00}:{minute.Value:00}");

                case "focus_start":
                    if (!ctx.Focus.Running) ctx.Focus.Toggle.Execute(null);
                    return new ToolResult($"Focus timer running ({ctx.Focus.Remaining}).", ctx.Focus.Remaining);

                case "focus_pause":
                    if (ctx.Focus.Running) ctx.Focus.Toggle.Execute(null);
                    return new ToolResult($"Focus timer paused ({ctx.Focus.Remaining}).", ctx.Focus.Remaining);

                case "focus_reset":
                    ctx.Focus.Reset.Execute(null);
                    return new ToolResult($"Focus timer reset ({ctx.Focus.Remaining}).", ctx.Focus.Remaining);

                default:
                    throw new ArgumentException($"Unknown action: {action}");
            }
        });
    }
}
