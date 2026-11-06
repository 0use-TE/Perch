using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Platform.Windows;
using Perch.Services;
using Windows.Media.Control;

namespace Perch.ViewModels;

// "Now playing" from the Windows media controls (System Media Transport Controls, the card in the volume flyout).
// Any app that registers there (Spotify, browsers, Windows media apps, many players) shows up with no per-app work.
public sealed class MusicViewModel : Observable
{
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    private GlobalSystemMediaTransportControlsSession? session;
    private string title = "", artist = "", app = "";
    private BitmapSource? cover;
    private bool playing;
    private double position, duration;
    private int version;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool fromWindow;   // no media session: the track was read from a player's window title instead
    private bool polling;
    private string fromProcess = "";
    private readonly Store store;

    public bool HasSession => session != null || fromWindow;
    public bool Idle => !HasSession;
    // Buttons work through the media session, or, for a player found by its window title, through that player's own
    // global shortcuts (set in Settings > Features > Music).
    private PlayerKeySet? Keys => fromWindow ? store.State.Settings.PlayerKeys.FirstOrDefault(k => k.Process == fromProcess) : null;
    private bool Has(Func<PlayerKeySet, string> pick) => session != null || (Keys is { } k && KeySender.CanSend(pick(k)));
    public bool CanToggle => Has(k => k.Play);
    public bool CanPrevious => Has(k => k.Previous);
    public bool CanNext => Has(k => k.Next);
    public bool CanControl => CanToggle || CanPrevious || CanNext;
    public bool HasTimeline => duration > 0;
    public bool FromWindow => fromWindow;
    public bool IsPlaying => playing;
    public string Title => title.Length > 0 ? title : Loc.T("Music.Unknown");
    public string Artist => artist;
    public string AppName => app;
    public BitmapSource? Cover => cover;
    public bool HasCover => cover != null;
    public string PlayGlyph => playing ? "" : "";
    public double Progress => duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
    public string PositionText => Clock(position);
    public string DurationText => duration > 0 ? Clock(duration) : "";
    public string CompactText => artist.Length > 0 ? $"{Title} · {artist}" : Title;

    public ICommand Toggle { get; }
    public ICommand Next { get; }
    public ICommand Previous { get; }

    public MusicViewModel(Store store)
    {
        this.store = store;
        Toggle = new RelayCommand(_ => { if (session != null) _ = session.TryTogglePlayPauseAsync(); else Press(k => k.Play); });
        Next = new RelayCommand(_ => { if (session != null) _ = session.TrySkipNextAsync(); else Press(k => k.Next); });
        Previous = new RelayCommand(_ => { if (session != null) _ = session.TrySkipPreviousAsync(); else Press(k => k.Previous); });
        clock.Tick += (_, _) => Tick();
        watch.Tick += async (_, _) => await PollPlayersAsync();
        watch.Start();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => OnUi(() => Attach(manager.GetCurrentSession()));
            Attach(manager.GetCurrentSession());
        }
        catch (Exception)
        {
            // No media session service (very old or locked-down Windows): the page just stays idle.
        }
    }

    private static void OnUi(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    private void Attach(GlobalSystemMediaTransportControlsSession? next)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnChanged;
            session.PlaybackInfoChanged -= OnChanged;
            session.TimelinePropertiesChanged -= OnChanged;
        }
        session = next;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnChanged;
            session.PlaybackInfoChanged += OnChanged;
            session.TimelinePropertiesChanged += OnChanged;
        }
        _ = RefreshAsync();
    }

    private async void Press(Func<PlayerKeySet, string> pick)
    {
        if (Keys is not { } keys || pick(keys) is not { Length: > 0 } combo) return;
        await KeySender.SendAsync(combo);
        await Task.Delay(700); // let the player react, then re-read what it shows
        await PollPlayersAsync();
    }

    // Players that never register with the Windows media controls (NetEase Cloud Music and similar):
    // shown from their window title while no media session exists.
    private async Task PollPlayersAsync()
    {
        if (session != null || polling) { if (session != null && fromWindow) { fromWindow = false; Changed(string.Empty); } return; }
        polling = true;
        try
        {
            var found = await Task.Run(PlayerWatcher.Find);
            if (session != null) return; // a real media session appeared meanwhile
            if (found == null)
            {
                if (!fromWindow) return;
                fromWindow = false;
                title = artist = app = ""; cover = null; playing = false; position = duration = 0;
            }
            else
            {
                fromWindow = true;
                fromProcess = found.Process;
                (app, title, artist, playing) = (found.App, found.Title, found.Artist, found.Playing);
                cover = null; position = duration = 0;
            }
            Changed(string.Empty);
        }
        catch (Exception)
        {
            // Never let a probe of other apps take the page down.
        }
        finally
        {
            polling = false;
        }
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession sender, object args) => OnUi(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var current = session;
        var mine = ++version;
        if (current == null)
        {
            if (!fromWindow) { title = artist = app = ""; cover = null; playing = false; position = duration = 0; }
            clock.Stop();
            Changed(string.Empty);
            return;
        }
        fromWindow = false;
        try
        {
            var properties = await current.TryGetMediaPropertiesAsync();
            BitmapSource? picture = null;
            if (properties.Thumbnail is { } thumbnail) picture = await LoadAsync(thumbnail);
            if (mine != version) return; // a newer refresh is already running
            title = properties.Title ?? "";
            artist = properties.Artist ?? "";
            app = AppLabel(current.SourceAppUserModelId);
            cover = picture;
            playing = current.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = current.GetTimelineProperties();
            duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);
            if (playing) position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
            if (duration > 0) position = Math.Min(position, duration);
            if (playing) clock.Start(); else clock.Stop();
        }
        catch (Exception)
        {
            // Some apps fail while switching tracks; the next change event refreshes again.
        }
        Changed(string.Empty);
    }

    private static async Task<BitmapSource?> LoadAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var source = stream.AsStreamForRead();
            var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Tick()
    {
        if (!playing) return;
        position = duration > 0 ? Math.Min(duration, position + 1) : position + 1;
        Changed(nameof(Progress)); Changed(nameof(PositionText));
    }

    // "cloudmusic.exe" -> "cloudmusic"; store apps keep their package name.
    private static string AppLabel(string id)
    {
        var name = id.Split('!')[0].Split('_')[0];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string Clock(double seconds)
    {
        var total = (int)Math.Max(0, seconds);
        return total >= 3600 ? $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }

    public void Relocalize() => Changed(string.Empty);
}
