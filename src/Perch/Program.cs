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
        using var single = new SingleInstance(Store.DataFolder);
        if (!single.IsFirst)
        {
            single.Wake();
            return;
        }
        // Anything that escapes is written to the log first (Settings > General > Log), so a crash leaves a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Log.Error("Perch stopped because of an unexpected error", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn("A background task failed", e.Exception); e.SetObserved(); };
        Log.Info($"Perch started (version {typeof(Program).Assembly.GetName().Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        single.Listen(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => SecondInstance?.Invoke()));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // The bundled fonts cover both Perch's dinosaur and icons on systems without Windows fonts.
        .With(new Avalonia.Media.FontManagerOptions
        {
            FontFallbacks =
            [
                new() { FontFamily = new Avalonia.Media.FontFamily("avares://Perch/Assets/Fonts#Perch Icons") },
                new() { FontFamily = new Avalonia.Media.FontFamily("avares://Perch/Assets/Fonts#Symbols") },
            ],
        })
        .LogToTrace();
}
