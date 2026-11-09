using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Perch.Views;

namespace Perch;

public partial class App : Application
{
    public static void Quit()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public override void Initialize()
    {
        // Each platform says whether it can show the translucent material; where it cannot, the solid look is used.
        if (OperatingSystem.IsWindows()) Perch.Services.Translucency.Probe = Perch.Platform.Windows.WindowBackdrop.TranslucencyAvailable;
        Perch.Services.Translucency.Refresh();
        if (OperatingSystem.IsWindows()) Perch.Services.Sounds.Audio.Player = new Perch.Platform.Windows.WinSoundPlayer();
        if (OperatingSystem.IsWindows()) Perch.Services.Hardware.Source = new Perch.Platform.Windows.WindowsHardwareSource();
        // Where the list of listening ports comes from (a Linux build would add its own source).
        if (OperatingSystem.IsWindows()) Perch.Services.Ports.PortSource.Current = new Perch.Platform.Windows.WindowsPortSource();
        AvaloniaXamlLoader.Load(this);
        ImeGuard.Install(); // hints over empty text boxes hide while the input method is composing
        // An error in a click or other handler on the UI thread is logged and the program carries on, instead of closing.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Perch.Services.Log.Error("An action failed", e.Exception);
            e.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new IslandWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
