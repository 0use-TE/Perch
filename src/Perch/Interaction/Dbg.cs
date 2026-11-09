namespace Perch.Interaction;

// Diagnostic log for hover/expand problems: set the PERCH_DBG environment variable to a file path and Perch appends
// every pointer and state change to it. Costs nothing when the variable is not set.
public static class Dbg
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("PERCH_DBG");
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public static void Log(string text) { if (Path != null) try { System.IO.File.AppendAllText(Path, Clock.ElapsedMilliseconds.ToString().PadLeft(7) + " ms  " + text + Environment.NewLine); } catch { } }
}
