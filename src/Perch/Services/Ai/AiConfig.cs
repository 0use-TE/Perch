using System.IO;

namespace Perch.Services.Ai;

// A developer convenience only: while working on Perch, the DeepSeek key can sit in a ".env" file in the project folder
// (or in the DeepSeekAPI environment variable) instead of being typed into Settings. It is used only for the DeepSeek
// server and only when that server has no key of its own in Settings. People who install Perch never need it: they
// enter keys in Settings > AI, where they are stored protected on their own computer.
public static class AiConfig
{
    private const string KeyName = "DeepSeekAPI";

    public static string? Key => FromFile() ?? Environment.GetEnvironmentVariable(KeyName) ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");

    private static string? FromFile()
    {
        // The program's folder and its parents (a build inside the project finds the project's ".env"), then the working folder.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var file = Path.Combine(directory.FullName, ".env");
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var text = line.Trim();
                        var at = text.IndexOf('=');
                        if (text.Length == 0 || text[0] == '#' || at <= 0 || !text[..at].Trim().Equals(KeyName, StringComparison.OrdinalIgnoreCase)) continue;
                        var value = text[(at + 1)..].Trim().Trim('"', '\'');
                        if (value.Length > 0) return value;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return null;
    }
}
