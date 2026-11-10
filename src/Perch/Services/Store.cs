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
    // Which built-in sound each kind of reminder plays (ids from SoundBank), and how loud (0-100).
    public string SoundReminder { get; set; } = "gentle";
    public string SoundPomodoro { get; set; } = "bell";
    public string SoundAlarm { get; set; } = "alarm";
    public int SoundVolume { get; set; } = 70;
    // Minutes a snoozed alarm waits before ringing again.
    public int SnoozeMinutes { get; set; } = 5;
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public bool FocusAutoContinue { get; set; } = true;
    public bool ShowNetworkSpeed { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    // Optional: the island can be dragged sideways along the top edge, and remembers where it was left (DIP from the centre).
    public bool IslandDrag { get; set; }
    public double IslandOffset { get; set; }
    // A soft tick when the dragged island clings to the centre (off by default).
    public bool SnapSound { get; set; }
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
    public bool ShowPorts { get; set; } = true;
    public bool ShowAi { get; set; } = true;
    public bool ShowAlarm { get; set; } = true;
    public bool ShowDino { get; set; } = true;
    // Best score of the little dinosaur game.
    public int DinoBest { get; set; }
    // AI chat: the servers it can talk to (any OpenAI-compatible one) and the model last used.
    public List<AiProvider> AiProviders { get; set; } = [];
    public string AiProviderId { get; set; } = "";
    public string AiModel { get; set; } = "";
    // Web search for the AI chat (the client-side agent's web_search tool): which service, its key (protected) or address.
    public bool WebSearch { get; set; }
    // The AI may offer to act on this computer (create folders, open apps, run scripts); each action still needs the user's OK.
    public bool AiLocalTools { get; set; } = OperatingSystem.IsWindows();
    // When an action asks first: "ask" = every one, "script" = only scripts (the dangerous ones), "none" = never.
    public string AiApproval { get; set; } = "ask";
    public string SearchEngine { get; set; } = "local"; // "local" (no key), "tavily", "bocha" or "searxng"
    public string SearchKey { get; set; } = "";
    public string SearchUrl { get; set; } = "";
    [JsonIgnore] public string SearchKeyPlain { get => Secrets.Unprotect(SearchKey); set => SearchKey = Secrets.Protect(value?.Trim(), SearchKey); }
    [JsonIgnore] public string SearchKeyInput
    {
        get => SearchKey.Length > 0 ? AiProvider.KeyMask : "";
        set { if (value != AiProvider.KeyMask && !(SearchKey.Length > 0 && value.Length > 0 && value.All(c => c == '*'))) SearchKeyPlain = value; }
    }
    public bool ShowSystem { get; set; } = true;
    // Optional extras on the system page.
    public bool SysCpuTemp { get; set; } = true;
    public bool SysGpu { get; set; } = OperatingSystem.IsWindows();
    public bool SysVram { get; set; } = OperatingSystem.IsWindows();
    public bool MusicInCompact { get; set; } = true;   // the closed island shows the song that is playing
    public bool CalendarWindows { get; set; } = OperatingSystem.IsWindows();  // include the calendars of Windows' own calendar store
    public string CalendarUrls { get; set; } = "";     // one .ics link or file path per line
    public int CalendarReminder { get; set; } = 10;    // minutes before a meeting; 0 = off
    public bool HotkeysEnabled { get; set; } = true;
    public string HotkeyIsland { get; set; } = "Ctrl+Alt+I";   // not Ctrl+Alt+P: NetEase Cloud Music already uses that for play/pause
    public string HotkeyNote { get; set; } = "Ctrl+Alt+N";
    public string HotkeySearch { get; set; } = "Ctrl+Alt+F"; // the global search
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

// A chat server that speaks the OpenAI "chat completions" protocol: DeepSeek, OpenAI, Ollama, LM Studio, OpenRouter...
public sealed class AiProvider : Perch.ViewModels.Observable
{
    private string status = "";
    public const string DeepSeekId = "deepseek";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    // The address up to (not including) /chat/completions, e.g. https://api.openai.com/v1
    public string BaseUrl { get; set; } = "";
    // The key as stored on disk: protected, never plain text (see Secrets).
    public string Key { get; set; } = "";
    public List<string> Models { get; set; } = [];

    [JsonIgnore] public string ApiKey { get => Secrets.Unprotect(Key); set => Key = Secrets.Protect(value?.Trim(), Key); }
    // What the key box in Settings shows: a row of stars when a key is saved, never the key itself. Typing replaces it.
    public const string KeyMask = "************";
    [JsonIgnore] public string KeyInput
    {
        get => Key.Length > 0 ? KeyMask : "";
        set { if (value != KeyMask && !(Key.Length > 0 && value.Length > 0 && value.All(c => c == '*'))) ApiKey = value; }
    }
    [JsonIgnore] public string ModelsText
    {
        get => string.Join("\n", Models);
        set => Models = (value ?? "").Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    }

    // A line under the card in Settings ("Found 12 models", an error...). Not saved.
    [JsonIgnore] public string Status { get => status; set => Set(ref status, value); }

    // The models as a list the Settings page can show and edit (kept in step with Models, which is what gets saved).
    private System.Collections.ObjectModel.ObservableCollection<ModelItem>? items;
    [JsonIgnore] public System.Collections.ObjectModel.ObservableCollection<ModelItem> ModelItems => items ??= new(Models.Select(m => new ModelItem(this, m)));

    public void SetModels(IEnumerable<string> names)
    {
        Models = names.Distinct().ToList();
        Changed(nameof(ModelsText));
        if (items == null) return;
        items.Clear();
        foreach (var model in Models) items.Add(new ModelItem(this, model));
    }

    public void AddModel(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Models.Contains(name)) return;
        Models.Add(name);
        ModelItems.Add(new ModelItem(this, name));
        Changed(nameof(ModelsText));
    }

    public void RemoveModel(ModelItem item)
    {
        Models.Remove(item.Name);
        ModelItems.Remove(item);
        Changed(nameof(ModelsText));
    }

    public static AiProvider DeepSeek() => new() { Id = DeepSeekId, Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Models = ["deepseek-chat", "deepseek-reasoner"] };
}

// One model of a provider, as a row in the Settings list: its name and a button that removes it.
public sealed class ModelItem
{
    public ModelItem(AiProvider owner, string name)
    {
        Name = name;
        Remove = new Perch.ViewModels.RelayCommand(_ => owner.RemoveModel(this));
    }
    public string Name { get; }
    public Perch.ViewModels.RelayCommand Remove { get; }
}

// One alarm clock entry. Days is a bit mask (Monday = 1 ... Sunday = 64); 0 means it rings once and then switches itself off.
public sealed class AlarmData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public string Label { get; set; } = "";
    public int Days { get; set; }
    public bool Enabled { get; set; } = true;
    // "" follows the alarm sound in Settings; otherwise a sound id (or "off").
    public string Sound { get; set; } = "";
}

public sealed class FocusDay
{
    public string Date { get; set; } = "";
    public int Sessions { get; set; }
    public int Minutes { get; set; }
}

// A pinned clipboard item: some text, or the paths of files (pictures are not kept between runs).
public sealed class PinnedClip
{
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";
    public List<string> Paths { get; set; } = [];
}

public sealed class AppState
{
    public List<PinnedClip> PinnedClips { get; set; } = [];
    public Settings Settings { get; set; } = new();
    public List<TodoData> Todos { get; set; } = [];
    public List<NoteData> Notes { get; set; } = [];
    public List<string> PinnedApps { get; set; } = [];
    public List<CommandData> Commands { get; set; } = [];
    public List<string> TrayFiles { get; set; } = [];
    public List<FocusDay> Focus { get; set; } = [];
    public List<AlarmData> Alarms { get; set; } = [];
}

// Everything Perch keeps lives in one JSON file under %AppData%\Perch, written shortly after the last change.
public sealed class Store
{
    // PERCH_DATA redirects the state folder (used for side-by-side testing without touching the real settings).
    public static string DataFolder => Folder;
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
        if (s.AiProviders.Count == 0) s.AiProviders.Add(AiProvider.DeepSeek());
        s.ExpandDelay = Math.Clamp(s.ExpandDelay, 0, 1000);
        s.CollapseDelay = Math.Clamp(s.CollapseDelay, 0, 1000);
        s.SoundVolume = Math.Clamp(s.SoundVolume, 0, 100);
        s.SnoozeMinutes = Math.Clamp(s.SnoozeMinutes, 1, 60);
        foreach (var id in new[] { s.SoundReminder, s.SoundPomodoro, s.SoundAlarm }) _ = id;
        if (!Perch.Services.Sounds.SoundBank.Exists(s.SoundReminder) && s.SoundReminder != Perch.Services.Sounds.SoundBank.Off) s.SoundReminder = "gentle";
        if (!Perch.Services.Sounds.SoundBank.Exists(s.SoundPomodoro) && s.SoundPomodoro != Perch.Services.Sounds.SoundBank.Off) s.SoundPomodoro = "bell";
        if (!Perch.Services.Sounds.SoundBank.Exists(s.SoundAlarm) && s.SoundAlarm != Perch.Services.Sounds.SoundBank.Off) s.SoundAlarm = "alarm";
#if WINDOWS
        foreach (var (process, name) in PlayerWatcher.Players)
        {
            if (s.PlayerKeys.Any(k => string.Equals(k.Process, process, StringComparison.OrdinalIgnoreCase))) continue;
            // NetEase Cloud Music's usual global shortcuts (check them in its settings: Shortcuts).
            var netease = process == "cloudmusic";
            s.PlayerKeys.Add(new PlayerKeySet { Process = process, Name = name, Play = netease ? "Ctrl+Alt+P" : "", Previous = netease ? "Ctrl+Alt+Left" : "", Next = netease ? "Ctrl+Alt+Right" : "" });
        }
#endif
        if (s.HotkeyIsland == "Ctrl+Alt+P") s.HotkeyIsland = "Ctrl+Alt+I";
        string[] pages = ["music", "focus", "todo", "notes", "info", "calendar", "apps", "tray", "photos", "clipboard", "system", "ports", "ai", "alarm", "dino", "search"];
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
