using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;

namespace Perch.Services;

// Source-generated JSON: no run-time reflection, so it also works with Native AOT and trimming.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppState))]
internal sealed partial class StateJson : JsonSerializerContext;

public sealed class Settings
{
    public string Language { get; set; } = ""; // "" follows Windows; otherwise a code from Loc.Available
    public string City { get; set; } = "";
    public bool Fahrenheit { get; set; }
    public bool WeatherAlerts { get; set; } = true;
    public bool WaterReminder { get; set; } = true;
    public int WaterMinutes { get; set; } = 60;
    public bool SitReminder { get; set; } = true;
    public int SitMinutes { get; set; } = 50;
    public bool BedtimeReminder { get; set; }
    public string Bedtime { get; set; } = "23:30";
    public bool BatteryAlerts { get; set; } = true;
    public bool AlertSound { get; set; } = true;
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public bool FocusAutoContinue { get; set; } = true;
    public bool ShowNetworkSpeed { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public bool HoverExpand { get; set; } = true;
    public bool? ReducedMotion { get; set; } // null follows the system animation setting
    public string Shape { get; set; } = "standard";   // "standard" (notch) or "capsule" (floating pill)
    public string Appearance { get; set; } = "black"; // "black" (solid) or "glass" (black when closed, translucent Windows 11 backdrop when open)
    public List<PlayerKeySet> PlayerKeys { get; set; } = [];
    // Navigation bar: the order of the pages, and which of them stay on the bar (the rest are under "More").
    public List<string> NavOrder { get; set; } = [];
    public List<string> NavPinned { get; set; } = ["music", "focus", "todo", "notes", "calendar", "info"];
    public string Theme { get; set; } = "dark";       // chrome of the settings window and menu: "dark", "light" or "system"
    public string PhotoFolder { get; set; } = "";
    public string CityName { get; set; } = "";        // the picked weather location, as shown ("Halifax, Nova Scotia, Canada")
    public double? Latitude { get; set; }             // set when a place is picked from the search list or located
    public double? Longitude { get; set; }
    public bool UseDeviceLocation { get; set; }       // follow the device position (re-read every half hour)
    // Which pages of the island are shown; Overview is always there.
    public bool ShowMusic { get; set; } = true;
    public bool ShowFocus { get; set; } = true;
    public bool ShowTodo { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowInfo { get; set; } = true;
    public bool ShowApps { get; set; } = true;
    public bool ShowTray { get; set; } = true;
    public bool ShowPhotos { get; set; } = true;
    public bool ShowCalendar { get; set; } = true;
    public bool ShowClipboard { get; set; } = true;
    public bool ShowSystem { get; set; } = true;
    public bool MusicInCompact { get; set; } = true;   // the closed island shows the song that is playing
    public bool CalendarWindows { get; set; } = true;  // include the calendars of Windows' own calendar store
    public string CalendarUrls { get; set; } = "";     // one .ics link or file path per line
    public int CalendarReminder { get; set; } = 10;    // minutes before a meeting; 0 = off
    public bool HotkeysEnabled { get; set; } = true;
    public string HotkeyIsland { get; set; } = "Ctrl+Alt+I";   // not Ctrl+Alt+P: NetEase Cloud Music already uses that for play/pause
    public string HotkeyNote { get; set; } = "Ctrl+Alt+N";
    public bool CollapseOnLeave { get; set; } = true; // false: once open, it closes only on a click outside (or the pin)
    public int ExpandDelay { get; set; } = 100;       // ms the pointer must rest on the island before it opens (0 to 1000)
    public int CollapseDelay { get; set; } = 400;     // ms after the pointer leaves before it closes (0 to 1000)
    public int GlassOpacity { get; set; } = 50;       // percent, how solid the black tint is when the island is open
}

// The global shortcuts of one music player, pressed by Perch when you click play/pause, previous or next.
public sealed class PlayerKeySet
{
    public string Process { get; set; } = "";
    public string Name { get; set; } = "";
    public string Play { get; set; } = "";
    public string Previous { get; set; } = "";
    public string Next { get; set; } = "";
}

public sealed class TodoData
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime? Due { get; set; }
    public DateTime Created { get; set; }
    public DateTime? Completed { get; set; }
}

public sealed class NoteData
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Highlight { get; set; }
    public DateTime Updated { get; set; }
}

public sealed class CommandData
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
}

public sealed class FocusDay
{
    public string Date { get; set; } = "";
    public int Sessions { get; set; }
    public int Minutes { get; set; }
}

public sealed class AppState
{
    public Settings Settings { get; set; } = new();
    public List<TodoData> Todos { get; set; } = [];
    public List<NoteData> Notes { get; set; } = [];
    public List<string> PinnedApps { get; set; } = [];
    public List<CommandData> Commands { get; set; } = [];
    public List<string> TrayFiles { get; set; } = [];
    public List<FocusDay> Focus { get; set; } = [];
}

// Everything Perch keeps lives in one JSON file under %AppData%\Perch, written shortly after the last change.
public sealed class Store
{
    // PERCH_DATA redirects the state folder (used for side-by-side testing without touching the real settings).
    private static readonly string Folder = Environment.GetEnvironmentVariable("PERCH_DATA") is { Length: > 0 } custom ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Perch");
    private static readonly string FilePath = Path.Combine(Folder, "state.json");
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(800) };
    public AppState State { get; }
    // View models copy their live collections into State here, right before the file is written.
    public event Action? Saving;

    public Store()
    {
        State = Load();
        Normalize(State.Settings);
        debounce.Tick += (_, _) => Flush();
    }

    // Fills in what older settings files lack: an entry for each known player, and the island shortcut that
    // no longer clashes with NetEase Cloud Music's play/pause key.
    private static void Normalize(Settings s)
    {
        s.ExpandDelay = Math.Clamp(s.ExpandDelay, 0, 1000);
        s.CollapseDelay = Math.Clamp(s.CollapseDelay, 0, 1000);
        foreach (var (process, name) in PlayerWatcher.Players)
        {
            if (s.PlayerKeys.Any(k => string.Equals(k.Process, process, StringComparison.OrdinalIgnoreCase))) continue;
            // NetEase Cloud Music's usual global shortcuts (check them in its settings: Shortcuts).
            var netease = process == "cloudmusic";
            s.PlayerKeys.Add(new PlayerKeySet { Process = process, Name = name, Play = netease ? "Ctrl+Alt+P" : "", Previous = netease ? "Ctrl+Alt+Left" : "", Next = netease ? "Ctrl+Alt+Right" : "" });
        }
        if (s.HotkeyIsland == "Ctrl+Alt+P") s.HotkeyIsland = "Ctrl+Alt+I";
        string[] pages = ["music", "focus", "todo", "notes", "info", "calendar", "apps", "tray", "photos", "clipboard", "system"];
        s.NavOrder = s.NavOrder.Where(pages.Contains).Distinct().ToList();
        s.NavOrder.AddRange(pages.Where(id => !s.NavOrder.Contains(id)));
        s.NavPinned = s.NavPinned.Where(pages.Contains).Distinct().ToList();
    }

    private static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize(File.ReadAllText(FilePath), StateJson.Default.AppState) ?? new();
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return new();
    }

    public void Touch() { debounce.Stop(); debounce.Start(); }

    public void Flush()
    {
        debounce.Stop();
        try
        {
            Saving?.Invoke();
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, StateJson.Default.AppState));
            File.Move(temp, FilePath, true);
        }
        catch { }
    }
}
