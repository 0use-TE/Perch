using System.Globalization;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Services;

namespace Perch.ViewModels;

// playerctl speaks MPRIS over the session bus. It is optional; without it this page stays idle.
public sealed class MusicViewModel : Observable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool polling;
    private string player = "", title = "", artist = "";
    private double position, duration;
    public bool HasSession => player.Length > 0;
    public bool Idle => !HasSession;
    public bool CanToggle => HasSession;
    public bool CanPrevious => HasSession;
    public bool CanNext => HasSession;
    public bool CanControl => HasSession;
    public bool HasTimeline => duration > 0;
    public bool IsPlaying { get; private set; }
    public string Title => title.Length > 0 ? title : Loc.T("Music.Unknown");
    public string Artist => artist;
    public string AppName => player.Split('.')[0];
    public Bitmap? Cover => null;
    public string PlayGlyph => IsPlaying ? "" : "";
    public double Progress => duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
    public string PositionText => Clock(position);
    public string DurationText => duration > 0 ? Clock(duration) : "";
    public string CompactText => artist.Length > 0 ? $"{Title} · {artist}" : Title;
    public ICommand Toggle { get; }
    public ICommand Previous { get; }
    public ICommand Next { get; }

    public MusicViewModel(Store store)
    {
        Toggle = new RelayCommand(_ => _ = ControlAsync("play-pause"));
        Previous = new RelayCommand(_ => _ = ControlAsync("previous"));
        Next = new RelayCommand(_ => _ = ControlAsync("next"));
        timer.Tick += (_, _) => _ = PollAsync();
        timer.Start();
        _ = PollAsync();
    }

    private async Task ControlAsync(string action)
    {
        if (!HasSession) return;
        await LinuxProcess.ReadAsync("playerctl", ["--player=" + player, action]);
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (polling) return;
        polling = true;
        try
        {
            var list = await LinuxProcess.ReadAsync("playerctl", ["--list-all"]);
            var players = (list ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(16).ToArray();
            string? selected = null, metadata = null;
            foreach (var candidate in players)
            {
                var data = await LinuxProcess.ReadAsync("playerctl", ["--player=" + candidate, "metadata", "--format", "{{status}}\n{{mpris:length}}\n{{position}}\n{{artist}}\n{{title}}"]);
                if (data == null) continue;
                if (metadata == null || data.StartsWith("Playing\n", StringComparison.Ordinal)) { selected = candidate; metadata = data; }
                if (data.StartsWith("Playing\n", StringComparison.Ordinal)) break;
            }
            player = selected ?? "";
            var fields = (metadata ?? "").Split('\n', 5);
            IsPlaying = fields[0] == "Playing";
            duration = fields.Length > 1 && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Max(0, d / 1_000_000) : 0;
            position = fields.Length > 2 && double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Max(0, p / 1_000_000) : 0;
            artist = fields.Length > 3 ? fields[3] : "";
            title = fields.Length > 4 ? fields[4] : "";
            Changed(string.Empty);
        }
        finally { polling = false; }
    }

    private static string Clock(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        return total >= 3600 ? $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }
    public void Relocalize() => Changed(string.Empty);
}
