using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Perch.ViewModels;

namespace Perch.Services.Ai.Agent;

// Tools that act on this computer. Each one asks the user first (see ToolRisk): nothing here runs on the model's word alone.

// Makes a folder (with any missing parent folders).
public sealed class CreateFolderTool : ITool
{
    public ToolSpec Spec { get; } = new("create_folder",
        "Create a folder on this computer, including any missing parent folders. Always give the full path (for example C:\\Users\\Name\\Documents\\Project).",
        """{"type":"object","properties":{"path":{"type":"string","description":"The full path of the folder to create."}},"required":["path"]}""");

    public ToolRisk Risk => ToolRisk.Change;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepCreate", Agent.Str(arguments, "path") ?? "");

    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var path = Environment.ExpandEnvironmentVariables(Agent.Str(arguments, "path")?.Trim().Trim('"') ?? "");
        if (path.Length == 0) throw new ArgumentException("path is empty");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Give a full path such as C:\\Users\\Name\\Documents\\Project, not a relative one.");
        var full = Path.GetFullPath(path);
        var existed = Directory.Exists(full);
        Directory.CreateDirectory(full);
        return Task.FromResult(existed
            ? new ToolResult("The folder already exists: " + full, Loc.T("Ai.AlreadyThere"))
            : new ToolResult("Created: " + full, Loc.T("Ai.Created")));
    }
}

// Opens an app, a file, a folder or a web address, the way double-clicking or typing it into Run would.
public sealed class OpenAppTool : ITool
{
    public ToolSpec Spec { get; } = new("open_app",
        "Open an application, file, folder or web address on this computer. Give an app's name as the user knows it (like Notepad, Chrome, Visual Studio Code), "
        + "an executable name (notepad, calc), a full path, or an http(s) link.",
        """{"type":"object","properties":{"target":{"type":"string","description":"App name, executable, full path or link to open."}},"required":["target"]}""");

    public ToolRisk Risk => ToolRisk.Change;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepOpen", Agent.Str(arguments, "target") ?? "");

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var target = Environment.ExpandEnvironmentVariables(Agent.Str(arguments, "target")?.Trim().Trim('"') ?? "");
        if (target.Length == 0) throw new ArgumentException("target is empty");

        // A link or something that exists is opened as it is; otherwise the name is looked up in the Start menu.
        string what = target;
        var direct = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || File.Exists(target) || Directory.Exists(target);
        if (!direct)
        {
            var apps = await Task.Run(LauncherViewModel.Scan, cancel);
            var found = apps.Where(a => a.Name.Equals(target, StringComparison.CurrentCultureIgnoreCase)).Cast<(string Name, string Path, string Folder)?>().FirstOrDefault()
                ?? apps.Where(a => a.Name.StartsWith(target, StringComparison.CurrentCultureIgnoreCase)).Cast<(string Name, string Path, string Folder)?>().FirstOrDefault()
                ?? apps.Where(a => a.Name.Contains(target, StringComparison.CurrentCultureIgnoreCase)).Cast<(string Name, string Path, string Folder)?>().FirstOrDefault();
            if (found is { } match) { target = match.Path; what = match.Name; }
        }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception first)
        {
            // A bare name like "notepad" is looked up in the usual program folders when the shell cannot resolve it itself.
            var path = FindProgram(target);
            if (path == null)
            {
                Log.Warn($"open_app: Windows could not open \"{target}\" ({first.NativeErrorCode}: {first.Message})");
                throw new FileNotFoundException($"Could not find or open \"{what}\". Try the app's exact name, its executable name, or a full path.");
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Win32Exception second)
            {
                Log.Warn($"open_app: could not start {path} ({second.NativeErrorCode}: {second.Message})");
                throw new InvalidOperationException($"Found {path} but Windows would not start it: {second.Message}");
            }
            what = path;
        }
        return new ToolResult("Opened: " + what, what);
    }

    // notepad -> C:\Windows\System32\notepad.exe : the PATH folders, the system folders and the user's app-execution-alias folder.
    private static string? FindProgram(string name)
    {
        if (name.Contains('\\') || name.Contains('/') || name.Contains(' ')) return null;
        var file = Path.HasExtension(name) ? name : name + ".exe";
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Environment.SystemDirectory).Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        foreach (var folder in folders)
        {
            try { var candidate = Path.Combine(folder.Trim('"'), file); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }
}

// Runs a PowerShell script and returns what it printed. The user sees the whole script before it runs.
public sealed class PowerShellTool : ITool
{
    private const int MaxOutput = 6000, DefaultSeconds = 30, MaxSeconds = 120;

    public ToolSpec Spec { get; } = new("run_powershell",
        "Run a PowerShell script on this computer (Windows PowerShell 5, no profile, no window) and return its output and exit code. "
        + "Use it for jobs the other tools cannot do. Keep scripts short and single-purpose, print what matters, and never write scripts that prompt for input.",
        """{"type":"object","properties":{"script":{"type":"string","description":"The PowerShell code to run."},"timeout_seconds":{"type":"integer","description":"How long it may run before it is stopped (default 30, at most 120)."}},"required":["script"]}""");

    public ToolRisk Risk => ToolRisk.Dangerous;
    public string Describe(JsonElement arguments) => Loc.T("Ai.StepScript");
    public string Detail(JsonElement arguments) => Agent.Str(arguments, "script") ?? "";

    public async Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var script = Agent.Str(arguments, "script");
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("script is empty");
        var seconds = DefaultSeconds;
        if (arguments.TryGetProperty("timeout_seconds", out var t) && t.TryGetInt32(out var asked)) seconds = Math.Clamp(asked, 1, MaxSeconds);

        // The script goes in encoded, so no quoting or line-ending problem can change it on the way.
        var wrapped = "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$ProgressPreference='SilentlyContinue';" + script;
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)) })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell could not be started.");
        process.StandardInput.Close(); // nothing to read from the keyboard
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var reading = Task.WhenAll(
            Pump(process.StandardOutput, output),
            Pump(process.StandardError, errors));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        var timedOut = false;
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            if (cancel.IsCancellationRequested) throw;
            timedOut = true;
        }
        await Task.WhenAny(reading, Task.Delay(1000)); // the last of the output, without waiting for a hung pipe

        var text = new StringBuilder();
        text.Append(timedOut ? $"Stopped after {seconds} seconds (timeout)." : $"Exit code: {process.ExitCode}").AppendLine();
        Append(text, "Output", output.ToString());
        Append(text, "Errors", errors.ToString());
        return new ToolResult(text.ToString().TrimEnd(), timedOut ? Loc.F("Ai.StepTimeout", seconds) : Loc.F("Ai.StepExit", process.ExitCode));
    }

    private static async Task Pump(StreamReader reader, StringBuilder into)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            lock (into) if (into.Length < MaxOutput * 2) into.Append(buffer, 0, read);
    }

    private static void Append(StringBuilder text, string title, string body)
    {
        body = body.Trim();
        if (body.Length == 0) return;
        if (body.Length > MaxOutput) body = body[..MaxOutput] + "\n…(truncated)";
        text.AppendLine("--- " + title + " ---").AppendLine(body);
    }
}
