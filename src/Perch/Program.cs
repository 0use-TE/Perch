using System.Threading;
using Avalonia;
using Perch.Services;

namespace Perch;

internal static class Program
{
    // Raised (on a background thread) when Perch is started again while this copy is running.
    public static event Action? SecondInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Only one Perch per data folder: a second start just wakes the running one (it opens the island) and leaves.
        // The names come from the data folder, so a test copy pointed elsewhere with PERCH_DATA can still run beside it.
        var key = "Perch." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Store.DataFolder.ToLowerInvariant())))[..16];
        using var single = new Mutex(true, @"Local\" + key, out var first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + ".wake");
        if (!first)
        {
            wake.Set();
            return;
        }
        // Anything that escapes is written to the log first (Settings > General > Log), so a crash leaves a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Log.Error("Perch stopped because of an unexpected error", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn("A background task failed", e.Exception); e.SetObserved(); };
        Log.Info($"Perch started (version {typeof(Program).Assembly.GetName().Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        var listener = new Thread(() =>
        {
            while (wake.WaitOne()) SecondInstance?.Invoke();
        }) { IsBackground = true, Name = "Perch second-start listener" };
        listener.Start();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // Perch's own icons (the dinosaur) live in a small font; any glyph Segoe Fluent Icons lacks is looked up there.
        .With(new Avalonia.Media.FontManagerOptions { FontFallbacks = [new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("avares://Perch/Assets/Fonts#Perch Icons") }] })
        .LogToTrace();
}
