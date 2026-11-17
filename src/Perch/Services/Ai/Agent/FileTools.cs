using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Perch.Services.Ai.Agent;

// Safe, read-only local file & codebase exploration tools plus persistent AI memory.
// Sensitive credential directories (.ssh, .aws, browser profiles, Perch state) are blocked.
internal static class SafePathGuard
{
    private static readonly string[] BlockedSegments =
    [
        @"\.ssh\", @"\.aws\", @"\.gnupg\", @"\.kube\", @"\.docker\",
        @"\AppData\Local\Google\Chrome\User Data\",
        @"\AppData\Local\Microsoft\Edge\User Data\",
        @"\AppData\Roaming\Mozilla\Firefox\Profiles\",
    ];

    public static string? DefaultWorkspace { get; set; }
    public static bool WorkspaceOnly { get; set; }

    public static string Resolve(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"'));
        if (expanded.Length == 0)
        {
            if (!string.IsNullOrEmpty(DefaultWorkspace)) expanded = DefaultWorkspace;
            else throw new ArgumentException("path is empty");
        }
        else if (!Path.IsPathFullyQualified(expanded) && !string.IsNullOrEmpty(DefaultWorkspace))
        {
            expanded = Path.Combine(DefaultWorkspace, expanded);
        }
        var full = Path.GetFullPath(expanded);
        if (WorkspaceOnly && !string.IsNullOrEmpty(DefaultWorkspace))
        {
            var root = Path.GetFullPath(DefaultWorkspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Access outside workspace ({DefaultWorkspace}) is blocked by workspace boundary policy.");
        }
        var check = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        foreach (var seg in BlockedSegments)
            if (check.Contains(seg, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Access to sensitive credential directory is blocked.");
        var perchData = Store.DataFolder;
        if (full.Equals(Path.Combine(perchData, "state.json"), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Direct reading of state.json is blocked; use perch_read instead.");
        return full;
    }
}

public sealed class ReadFileTool : ITool
{
    private const int MaxLines = 350, MaxBytes = 28_000;

    public ToolSpec Spec { get; } = new("read_file",
        "Read a text file from disk (read-only, capped at 350 lines per call). Prefer this over run_powershell when reading logs, configs or source files.",
        """{"type":"object","properties":{"path":{"type":"string","description":"Full path of the file to read."},"start_line":{"type":"integer","description":"1-based start line (optional, default 1)."},"end_line":{"type":"integer","description":"1-based end line inclusive (optional)."}},"required":["path"]}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepReadFile", Path.GetFileName(Agent.Str(arguments, "path") ?? ""));

    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var full = SafePathGuard.Resolve(Agent.Str(arguments, "path") ?? "");
        if (!File.Exists(full)) throw new FileNotFoundException($"File not found: {full}");
        var start = arguments.TryGetProperty("start_line", out var s) && s.TryGetInt32(out var sv) ? Math.Max(1, sv) : 1;
        var end = arguments.TryGetProperty("end_line", out var e) && e.TryGetInt32(out var ev) ? Math.Max(start, ev) : start + MaxLines - 1;
        end = Math.Min(end, start + MaxLines - 1);

        var sb = new StringBuilder();
        var lineNo = 0;
        var shown = 0;
        var bytes = 0;
        using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            cancel.ThrowIfCancellationRequested();
            lineNo++;
            if (lineNo < start) continue;
            if (lineNo > end || bytes >= MaxBytes) break;
            var entry = $"{lineNo}: {line}";
            sb.AppendLine(entry);
            bytes += entry.Length;
            shown++;
        }
        var more = reader.ReadLine() != null;
        if (more) sb.AppendLine($"…(more lines follow after line {lineNo}; pass start_line={lineNo + 1} to continue)");
        return Task.FromResult(new ToolResult(sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd(), $"{shown}L"));
    }
}

public sealed class ListDirTool : ITool
{
    private const int MaxEntries = 100;

    public ToolSpec Spec { get; } = new("list_dir",
        "List files and subdirectories in a folder (read-only, up to 100 entries).",
        """{"type":"object","properties":{"path":{"type":"string","description":"Full path of the directory."}},"required":["path"]}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepListDir", Path.GetFileName(Agent.Str(arguments, "path")?.TrimEnd('\\', '/') ?? ""));

    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var full = SafePathGuard.Resolve(Agent.Str(arguments, "path") ?? "");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Directory not found: {full}");
        var sb = new StringBuilder();
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ >= MaxEntries) break;
            sb.AppendLine($"[dir]  {Path.GetFileName(dir)}");
        }
        foreach (var file in Directory.EnumerateFiles(full).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (count++ >= MaxEntries) break;
            var info = new FileInfo(file);
            sb.AppendLine($"[file] {info.Name} ({info.Length} B)");
        }
        return Task.FromResult(new ToolResult(sb.Length == 0 ? "(empty directory)" : sb.ToString().TrimEnd(), $"{Math.Min(count, MaxEntries)}"));
    }
}

public sealed class SearchFilesTool : ITool
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "artifacts" };

    public ToolSpec Spec { get; } = new("grep_files",
        "Search text across files in a directory (read-only, skips build/git folders, max 50 matches).",
        """{"type":"object","properties":{"path":{"type":"string","description":"Root directory to search."},"pattern":{"type":"string","description":"Text or regex pattern to match."},"ignore_case":{"type":"boolean","description":"Case-insensitive match (default true)."}},"required":["path","pattern"]}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepGrep", Agent.Str(arguments, "pattern") ?? "");

    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var root = SafePathGuard.Resolve(Agent.Str(arguments, "path") ?? "");
        var pattern = Agent.Str(arguments, "pattern") ?? "";
        if (pattern.Length == 0) throw new ArgumentException("pattern is empty");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Directory not found: {root}");
        var ignoreCase = !arguments.TryGetProperty("ignore_case", out var ic) || ic.ValueKind != JsonValueKind.False;
        var regex = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        var matches = new List<string>(50);
        void Walk(string dir)
        {
            if (matches.Count >= 50 || cancel.IsCancellationRequested) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); } catch { return; }
            foreach (var file in files)
            {
                if (matches.Count >= 50 || cancel.IsCancellationRequested) return;
                try
                {
                    if (new FileInfo(file).Length > 1_000_000) continue;
                    var lineNo = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNo++;
                        if (!regex.IsMatch(line)) continue;
                        var rel = Path.GetRelativePath(root, file);
                        var trim = line.Trim();
                        if (trim.Length > 140) trim = trim[..140] + "…";
                        matches.Add($"{rel}:{lineNo}: {trim}");
                        if (matches.Count >= 50) return;
                    }
                }
                catch { }
            }
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir); } catch { return; }
            foreach (var sub in dirs)
            {
                if (SkipDirs.Contains(Path.GetFileName(sub))) continue;
                Walk(sub);
            }
        }
        Walk(root);
        return Task.FromResult(new ToolResult(matches.Count == 0 ? "No matches found." : string.Join(Environment.NewLine, matches), $"{matches.Count}"));
    }
}

public sealed class MemoryTool : ITool
{
    private const int MaxChars = 2500;
    public static string FilePath => Path.Combine(Store.DataFolder, "memory.md");

    public static string Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : ""; }
        catch { return ""; }
    }

    public ToolSpec Spec { get; } = new("memory",
        "Read or update persistent user memory across conversations (preferences, common folders, recurring context). Keep entries concise.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["read","append","replace","clear"]},"content":{"type":"string","description":"Text to append or replace when action is append or replace."}},"required":["action"]}""");

    public ToolRisk Risk => ToolRisk.Safe;
    public string Describe(JsonElement arguments) => Loc.F("Ai.StepMemory", Agent.Str(arguments, "action") ?? "");

    public Task<ToolResult> RunAsync(JsonElement arguments, CancellationToken cancel)
    {
        var action = (Agent.Str(arguments, "action") ?? "read").Trim().ToLowerInvariant();
        var content = (Agent.Str(arguments, "content") ?? "").Trim();
        Directory.CreateDirectory(Store.DataFolder);
        switch (action)
        {
            case "read":
                var current = Load();
                return Task.FromResult(new ToolResult(current.Length == 0 ? "(memory is empty)" : current, $"{current.Length}c"));
            case "append":
                if (content.Length == 0) throw new ArgumentException("content is required");
                var existing = Load();
                var next = existing.Length == 0 ? content : existing + Environment.NewLine + content;
                if (next.Length > MaxChars) next = next[^MaxChars..];
                File.WriteAllText(FilePath, next);
                return Task.FromResult(new ToolResult("Memory updated.", "saved"));
            case "replace":
                if (content.Length > MaxChars) content = content[..MaxChars];
                File.WriteAllText(FilePath, content);
                return Task.FromResult(new ToolResult("Memory replaced.", "saved"));
            case "clear":
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return Task.FromResult(new ToolResult("Memory cleared.", "cleared"));
            default:
                throw new ArgumentException($"Unknown action: {action}");
        }
    }
}
