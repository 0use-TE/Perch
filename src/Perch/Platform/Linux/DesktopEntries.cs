using System.Globalization;
using System.IO;

namespace Perch.Platform.Linux;

internal static class DesktopEntries
{
    internal static IEnumerable<string> DataDirectories()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return !string.IsNullOrEmpty(home) && Path.IsPathRooted(home) ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var dir in (string.IsNullOrEmpty(dirs) ? "/usr/local/share:/usr/share" : dirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathRooted(dir)) yield return dir;
    }

    internal static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = false;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { entry = text == "[Desktop Entry]"; continue; }
            if (!entry || text.StartsWith('#')) continue;
            var equals = text.IndexOf('=');
            if (equals > 0) result[text[..equals].Trim()] = text[(equals + 1)..];
        }
        return result;
    }

    internal static List<(string Name, string Path, string Folder)> Scan()
    {
        var found = new List<(string Name, string Path, string Folder)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':');
        var culture = CultureInfo.CurrentUICulture.Name.Replace('-', '_');
        foreach (var data in DataDirectories())
        {
            var root = Path.Combine(data, "applications");
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.desktop", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                // Hidden user entries still shadow the system entry with the same desktop-file ID.
                if (!seen.Add(Path.GetRelativePath(root, path).Replace('/', '-'))) continue;
                try
                {
                    var values = Read(path);
                    if (values.GetValueOrDefault("Type") != "Application" || values.GetValueOrDefault("Hidden") == "true" || values.GetValueOrDefault("NoDisplay") == "true") continue;
                    if (values.TryGetValue("OnlyShowIn", out var only) && !only.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(desktops.Contains)) continue;
                    if (values.TryGetValue("NotShowIn", out var not) && not.Split(';').Where(x => x.Length > 0).Any(desktops.Contains)) continue;
                    if (values.TryGetValue("TryExec", out var exec) && !ExecutableExists(exec)) continue;
                    var name = values.GetValueOrDefault($"Name[{culture}]") ?? values.GetValueOrDefault($"Name[{culture.Split('_')[0]}]") ?? values.GetValueOrDefault("Name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    found.Add((name.Replace("\\s", " ").Replace("\\n", " ").Replace("\\\\", "\\"), path, ""));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return found.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool ExecutableExists(string path)
    {
        if (Path.IsPathRooted(path)) return File.Exists(path) && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Where(Path.IsPathRooted).Any(dir => ExecutableExists(Path.Combine(dir, path)));
    }
}
