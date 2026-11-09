using System.IO;
using System.Text;

namespace Perch.Services;

// Perch's own log: one text file per day in the data folder's "logs" folder (a week is kept), plus the last few hundred
// lines in memory for the log viewer in Settings. Errors that would otherwise crash the program end up here as well.
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Recent = new();
    private const int RecentMax = 500;
    private static bool cleaned;

    public static string Folder => Path.Combine(Store.DataFolder, "logs");
    public static string TodayFile => Path.Combine(Folder, $"perch-{DateTime.Now:yyyyMMdd}.log");

    // Raised (on any thread) with each new line.
    public static event Action<string>? Added;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message, Exception? error = null) => Write("WARN ", error == null ? message : message + Environment.NewLine + Describe(error));
    public static void Error(string message, Exception? error = null) => Write("ERROR", error == null ? message : message + Environment.NewLine + Describe(error));

    // The exception with every inner exception and its stack, the way it is needed to find a bug.
    public static string Describe(Exception error)
    {
        var text = new StringBuilder();
        for (var e = error; e != null; e = e.InnerException)
        {
            if (text.Length > 0) text.AppendLine("  ---> caused by:");
            text.Append("  ").Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            if (e.StackTrace is { } stack) text.AppendLine(stack);
            if (e is AggregateException many && many.InnerExceptions.Count > 1)
                foreach (var each in many.InnerExceptions.Skip(1)) text.AppendLine("  ---> also:").AppendLine(Describe(each));
        }
        return text.ToString().TrimEnd();
    }

    // The newest lines (from memory, or from today's file when the program has only just started).
    public static string Tail(int lines = 300)
    {
        lock (Gate)
        {
            if (Recent.Count > 0) return string.Join(Environment.NewLine, Recent.Skip(Math.Max(0, Recent.Count - lines)));
        }
        try
        {
            if (!File.Exists(TodayFile)) return "";
            var all = File.ReadAllLines(TodayFile);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception) { return ""; }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            Recent.AddLast(line);
            while (Recent.Count > RecentMax) Recent.RemoveFirst();
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(TodayFile, line + Environment.NewLine);
                if (!cleaned) { cleaned = true; CleanOld(); }
            }
            catch (Exception) { } // a log that cannot be written must never take the program down
        }
        try { Added?.Invoke(line); } catch (Exception) { }
    }

    private static void CleanOld()
    {
        foreach (var file in Directory.GetFiles(Folder, "perch-*.log"))
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                try { File.Delete(file); } catch (Exception) { }
    }
}
