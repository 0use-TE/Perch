using System.Collections.ObjectModel;
using Perch.Services;
using Perch.Services.Sounds;

namespace Perch.ViewModels;

public sealed record CalendarDay(string Label, bool Today)
{
    public bool Blank => Label.Length == 0;
}

public sealed class IslandViewModel : Observable
{
    private readonly Store store;
    private string time = "", date = "", compactText = "Perch", compactGlyph = "";
    private bool compactActive, isWide;

    public AlertQueue Alerts { get; }
    public TodoViewModel Todo { get; }
    public NotesViewModel Notes { get; }
    public FocusViewModel Focus { get; }
    public WeatherViewModel Weather { get; }
    public SystemStatusViewModel Status { get; }
    public LauncherViewModel Launcher { get; }
    public TrayViewModel Tray { get; }
    public PhotosViewModel Photos { get; }
    public MusicViewModel Music { get; }
    public SystemMonitorViewModel Monitor { get; }
    public ClipboardViewModel Clipboard { get; }
    public SearchViewModel Search { get; }
    public CalendarViewModel Calendar { get; }
    public PortsViewModel Ports { get; }
    public DinoViewModel Dino { get; }
    public AiViewModel Ai { get; }
    public AlarmViewModel Alarm { get; }

    public string Time { get => time; private set => Set(ref time, value); }
    public string Date { get => date; private set => Set(ref date, value); }
    public string Hours => Time.Split(':')[0];
    public string Minutes => Time.Contains(':') ? Time.Split(':')[1] : "";
    public string MonthTitle { get; private set; } = "";
    public ObservableCollection<CalendarDay> CalendarDays { get; } = [];

    // The compact island's left side: an alert, then a running focus timer, then network speed, then the name.
    public string CompactText { get => compactText; private set => Set(ref compactText, value); }
    public string CompactGlyph { get => compactGlyph; private set { if (Set(ref compactGlyph, value)) Changed(nameof(HasCompactGlyph)); } }
    public bool HasCompactGlyph => compactGlyph.Length > 0;
    public bool CompactActive { get => compactActive; private set => Set(ref compactActive, value); }
    public bool IsWide => isWide;
    public string AlertText => Alerts.Current?.Text ?? "";
    public bool HasAlert => Alerts.Current != null;
    public event Action? ShapeChanged;

    public IslandViewModel(Store store)
    {
        this.store = store;
        Audio.Settings = store.State.Settings;
        Alerts = new AlertQueue();
        Todo = new TodoViewModel(store);
        Notes = new NotesViewModel(store);
        Focus = new FocusViewModel(store, Alerts);
        Weather = new WeatherViewModel(store, Alerts);
        Status = new SystemStatusViewModel(store, Alerts);
        Launcher = new LauncherViewModel(store, Alerts);
        Tray = new TrayViewModel(store, Alerts);
        Photos = new PhotosViewModel(store);
        Music = new MusicViewModel(store);
        Monitor = new SystemMonitorViewModel(store);
        Clipboard = new ClipboardViewModel(store, Alerts);
        Calendar = new CalendarViewModel(store, Alerts);
        Ports = new PortsViewModel();
        Dino = new DinoViewModel(store);
        Ai = new AiViewModel(store);
        Search = new SearchViewModel(store, Todo, Notes, Clipboard, Launcher);
        Alarm = new AlarmViewModel(store, Alerts);
        Alarm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AlarmViewModel.IsRinging) or nameof(AlarmViewModel.CompactText)) UpdateCompact(); };
        Music.PropertyChanged += (_, e) => { if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MusicViewModel.IsPlaying) or nameof(MusicViewModel.Title) or nameof(MusicViewModel.Artist)) UpdateCompact(); };
        Alerts.Changed += () => { Changed(nameof(AlertText)); Changed(nameof(HasAlert)); UpdateCompact(); };
        Focus.PropertyChanged += (_, _) => UpdateCompact();
        Status.PropertyChanged += (_, _) => { if (store.State.Settings.ShowNetworkSpeed) UpdateCompact(); };
        UpdateClock();
        UpdateCompact();
    }

    // Called when the UI language changes: every text built in code is rebuilt.
    public void Relocalize()
    {
        Date = "";
        UpdateClock();
        Todo.DayChanged();
        Notes.Relocalize();
        Focus.Relocalize();
        Weather.Relocalize();
        Launcher.Relocalize();
        Search.Relocalize();
        Tray.Relocalize();
        Photos.Relocalize();
        Music.Relocalize();
        Monitor.Relocalize();
        Clipboard.Relocalize();
        Calendar.Relocalize();
        Ports.Relocalize();
        Ai.Relocalize();
        Alarm.Relocalize();
        UpdateCompact();
        Changed(string.Empty);
    }

    public void UpdateCompact()
    {
        var settings = store.State.Settings;
        var showNet = settings.ShowNetworkSpeed;
        var music = settings.ShowMusic && settings.MusicInCompact && Music.IsPlaying;
        if (Alarm.IsRinging) { CompactGlyph = "\uE121"; CompactText = Alarm.CompactText; }
        else if (Alerts.Current is { } alert) { CompactGlyph = alert.Glyph; CompactText = alert.Text; }
        else if (Focus.Running) { CompactGlyph = ""; CompactText = $"{Focus.PhaseLabel}  {Focus.Remaining}"; }
        else if (music) { CompactGlyph = "\uE8D6"; CompactText = Music.CompactText; }
        else if (showNet) { CompactGlyph = ""; CompactText = Status.NetText; }
        else { CompactGlyph = ""; CompactText = "Perch"; }
        CompactActive = Alarm.IsRinging || Alerts.Current != null || Focus.Running;
        var wide = Alarm.IsRinging || Alerts.Current != null || Focus.Running || music || showNet;
        if (wide == isWide) return;
        isWide = wide;
        ShapeChanged?.Invoke();
    }

    public void UpdateClock()
    {
        var now = DateTime.Now;
        Time = now.ToString("HH:mm"); Changed(nameof(Hours)); Changed(nameof(Minutes));
        var dateLabel = Loc.Date(now, "Format.Date");
        if (Date == dateLabel) return;
        var dayChanged = Date.Length > 0;
        Date = dateLabel;
        if (dayChanged) Todo.DayChanged();
        MonthTitle = now.ToString("yyyy / MM"); Changed(nameof(MonthTitle));
        CalendarDays.Clear();
        var first = new DateTime(now.Year, now.Month, 1);
        for (var i = 0; i < ((int)first.DayOfWeek + 6) % 7; i++) CalendarDays.Add(new("", false));
        for (var i = 1; i <= DateTime.DaysInMonth(now.Year, now.Month); i++) CalendarDays.Add(new(i.ToString(), i == now.Day));
    }
}
