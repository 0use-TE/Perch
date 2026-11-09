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
#if WINDOWS
        Services.Translucency.Probe = WindowBackdrop.TranslucencyAvailable;
        Services.Sounds.Audio.Player = new WinSoundPlayer();
        Services.Hardware.Source = new WindowsHardwareSource();
        Services.Ports.PortSource.Current = new WindowsPortSource();
#else
        Services.Translucency.Probe = () => false;
        Services.Sounds.Audio.Player = new LinuxSoundPlayer();
        Services.Hardware.Source = new LinuxHardwareSource();
        Services.Ports.PortSource.Current = new LinuxPortSource();
#endif
        Services.Translucency.Refresh();
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
