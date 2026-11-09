using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Perch.Services;
using Perch.Services.Sounds;

namespace Perch.ViewModels;

// One alarm in the list.
public sealed class AlarmItem : Observable
{
    private readonly AlarmViewModel owner;
    public AlarmItem(AlarmData data, AlarmViewModel owner) { Data = data; this.owner = owner; }

    public AlarmData Data { get; }
    public string TimeText => $"{Data.Hour:00}:{Data.Minute:00}";
    public string Name => Data.Label.Length > 0 ? Data.Label : Loc.T("Alarm.Default");
    public string DaysText => AlarmViewModel.DaysText(Data.Days);
    public bool Enabled { get => Data.Enabled; set { if (Data.Enabled == value) return; Data.Enabled = value; Changed(); owner.Changed_(); } }
    public void Refresh() => Changed(string.Empty);
}

// The alarm clock: a list of alarms that ring at their time (once, or on chosen weekdays), with snooze. A ringing alarm
// keeps playing its sound until it is stopped (or two minutes pass).
public sealed class AlarmViewModel : Observable
{
    private const int RingSeconds = 120;
    private readonly Store store;
    private readonly AlertQueue alerts;
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Queue<AlarmItem> waitingToRing = new();
    private DateTime lastTick = DateTime.Now;
    private DateTime ringStarted;
    private DateTime? snoozeUntil;
    private AlarmItem? ringing, editing;
    private bool isEditing;
    private decimal? editHour = 7, editMinute = 0;
    private string editLabel = "";
    private SoundOption? editSound;

    public ObservableCollection<AlarmItem> Items { get; } = [];
    // The sounds an alarm can pick: follow the default, any built-in sound, or silence.
    public IReadOnlyList<SoundOption> SoundChoices { get; } =
        [new(SoundBank.Default, "Sound.default"), .. SoundBank.Options, new(SoundBank.Off, "Sound.off")];

    public bool IsEditing { get => isEditing; private set { if (Set(ref isEditing, value)) Changed(nameof(IsListMode)); } }
    public bool IsRinging => ringing != null;
    public bool IsListMode => !isEditing && ringing == null;
    public bool NotRinging => ringing == null;
    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;
    public string EditTitle => Loc.T(editing == null ? "Alarm.NewTitle" : "Alarm.EditTitle");
    public string NextText => Next();

    public decimal? EditHour { get => editHour; set => Set(ref editHour, value); }
    public decimal? EditMinute { get => editMinute; set => Set(ref editMinute, value); }
    public string EditLabel { get => editLabel; set => Set(ref editLabel, value ?? ""); }
    public SoundOption? EditSound { get => editSound; set => Set(ref editSound, value); }
    // Monday..Sunday, one flag each (the day buttons bind to these).
    public bool Day0 { get => days[0]; set => Day(0, value); }
    public bool Day1 { get => days[1]; set => Day(1, value); }
    public bool Day2 { get => days[2]; set => Day(2, value); }
    public bool Day3 { get => days[3]; set => Day(3, value); }
    public bool Day4 { get => days[4]; set => Day(4, value); }
    public bool Day5 { get => days[5]; set => Day(5, value); }
    public bool Day6 { get => days[6]; set => Day(6, value); }
    private readonly bool[] days = new bool[7];
    private void Day(int index, bool value) { if (days[index] == value) return; days[index] = value; Changed("Day" + index); }

    // What the ringing alarm shows.
    public string RingingTime => ringing?.TimeText ?? "";
    public string RingingName => ringing?.Name ?? "";
    public string SnoozeText => Loc.F("Alarm.Snooze", store.State.Settings.SnoozeMinutes);
    public string CompactText => ringing == null ? "" : ringing.Data.Label.Length > 0 ? $"{ringing.TimeText}  {ringing.Data.Label}" : ringing.TimeText;

    public ICommand Add { get; }
    public ICommand Edit { get; }
    public ICommand Delete { get; }
    public ICommand Save { get; }
    public ICommand Cancel { get; }
    public ICommand Preview { get; }
    public ICommand Stop { get; }
    public ICommand Snooze { get; }
    // A ringing alarm began or ended (the island opens itself and shows the page while it rings).
    public event Action<bool>? RingingChanged;

    public AlarmViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        foreach (var data in store.State.Alarms) Items.Add(new AlarmItem(data, this));
        Items.CollectionChanged += (_, _) => { Changed(nameof(IsEmpty)); Changed(nameof(HasItems)); };
        Add = new RelayCommand(_ => BeginEdit(null));
        Edit = new RelayCommand(p => { if (p is AlarmItem item) BeginEdit(item); });
        Delete = new RelayCommand(p => { if (p is AlarmItem item) { store.State.Alarms.Remove(item.Data); Items.Remove(item); Changed_(); } });
        Save = new RelayCommand(_ => SaveEdit());
        Cancel = new RelayCommand(_ => { Audio.Stop(); IsEditing = false; });
        Preview = new RelayCommand(_ => Audio.Preview(Audio.IdFor(SoundKind.Alarm, editSound?.Id)));
        Stop = new RelayCommand(_ => EndRing(missed: false));
        Snooze = new RelayCommand(_ => SnoozeRing());
        tick.Tick += (_, _) => Tick();
        tick.Start();
    }

    private Settings S => store.State.Settings;

    // Something in the list changed: save, and refresh what depends on all of it.
    internal void Changed_() { store.Touch(); Changed(nameof(NextText)); }

    public void Relocalize() { foreach (var item in Items) item.Refresh(); Changed(string.Empty); }

    // ---- editing

    private void BeginEdit(AlarmItem? item)
    {
        editing = item;
        var data = item?.Data;
        EditHour = data?.Hour ?? Math.Min(23, DateTime.Now.Hour + 1);
        EditMinute = data?.Minute ?? 0;
        EditLabel = data?.Label ?? "";
        for (var i = 0; i < 7; i++) Day(i, data != null && (data.Days & (1 << i)) != 0);
        EditSound = SoundChoices.FirstOrDefault(o => o.Id == (data?.Sound ?? "")) ?? SoundChoices[0];
        Changed(nameof(EditTitle));
        IsEditing = true;
    }

    private void SaveEdit()
    {
        Audio.Stop();
        var data = editing?.Data ?? new AlarmData();
        data.Hour = (int)Math.Clamp(editHour ?? 7, 0, 23);
        data.Minute = (int)Math.Clamp(editMinute ?? 0, 0, 59);
        data.Label = editLabel.Trim();
        data.Days = Enumerable.Range(0, 7).Where(i => days[i]).Sum(i => 1 << i);
        data.Sound = editSound?.Id ?? "";
        data.Enabled = true; // saving an alarm turns it on
        if (editing == null)
        {
            store.State.Alarms.Add(data);
            Items.Add(new AlarmItem(data, this));
        }
        else editing.Refresh();
        // Earliest first, in the list and in the saved data.
        var ordered = Items.OrderBy(i => i.Data.Hour * 60 + i.Data.Minute).ToList();
        Items.Clear();
        foreach (var item in ordered) Items.Add(item);
        store.State.Alarms.Sort((a, b) => (a.Hour * 60 + a.Minute).CompareTo(b.Hour * 60 + b.Minute));
        IsEditing = false;
        Changed_();
    }

    // ---- schedule

    // "Once" (no days), "Every day", "Weekdays", "Weekends", or the days themselves.
    public static string DaysText(int mask) => mask switch
    {
        0 => Loc.T("Alarm.Once"),
        127 => Loc.T("Alarm.Daily"),
        31 => Loc.T("Alarm.Weekdays"),
        96 => Loc.T("Alarm.Weekends"),
        _ => string.Join(" ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => Loc.T("Alarm.Day" + (i + 1)))),
    };

    private static int DayBit(DateTime date) => 1 << (((int)date.DayOfWeek + 6) % 7); // Monday = bit 0

    // When the alarm next rings after `from`.
    private static DateTime? NextRing(AlarmData alarm, DateTime from)
    {
        for (var offset = 0; offset < 8; offset++)
        {
            var day = from.Date.AddDays(offset);
            var at = day.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
            if (at <= from) continue;
            if (alarm.Days == 0 || (alarm.Days & DayBit(day)) != 0) return at;
        }
        return null;
    }

    private string Next()
    {
        var now = DateTime.Now;
        var next = store.State.Alarms.Where(a => a.Enabled).Select(a => NextRing(a, now)).Where(t => t != null).Min();
        if (next is not { } when) return Loc.T("Alarm.NoneOn");
        var day = when.Date == now.Date ? Loc.T("Common.Today") : when.Date == now.Date.AddDays(1) ? Loc.T("Common.Tomorrow") : Loc.T("Alarm.Day" + (((int)when.DayOfWeek + 6) % 7 + 1));
        return Loc.F("Alarm.Next", $"{day} {when:HH:mm}");
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var from = lastTick;
        lastTick = now;
        if (S.ShowAlarm)
        {
            // An alarm rings when its time falls between the previous check and now (so a short sleep or a busy moment
            // does not skip it); alarms that were due before Perch started are not rung late.
            foreach (var alarm in store.State.Alarms.Where(a => a.Enabled).ToList())
            {
                var due = now.Date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
                if (due <= from || due > now) continue;
                if (alarm.Days != 0 && (alarm.Days & DayBit(now)) == 0) continue;
                if (alarm.Days == 0) { alarm.Enabled = false; Items.FirstOrDefault(i => i.Data == alarm)?.Refresh(); Changed_(); }
                var item = Items.FirstOrDefault(i => i.Data == alarm);
                if (item != null) Ring(item);
            }
            if (snoozeUntil is { } at && now >= at && ringing == null)
            {
                snoozeUntil = null;
                if (snoozed != null) Ring(snoozed);
            }
        }
        if (ringing != null && (now - ringStarted).TotalSeconds >= RingSeconds) EndRing(missed: true);
        if (now.Second == 0) Changed(nameof(NextText)); // the "next alarm" line follows the clock
    }

    // ---- ringing

    private AlarmItem? snoozed;

    private void Ring(AlarmItem item)
    {
        if (ringing != null) { if (ringing != item && !waitingToRing.Contains(item)) waitingToRing.Enqueue(item); return; }
        ringing = item;
        ringStarted = DateTime.Now;
        Audio.Ring(item.Data.Sound);
        RingingStateChanged();
        RingingChanged?.Invoke(true);
    }

    private void RingingStateChanged()
    {
        Changed(nameof(IsRinging)); Changed(nameof(NotRinging)); Changed(nameof(IsListMode));
        Changed(nameof(RingingTime)); Changed(nameof(RingingName)); Changed(nameof(SnoozeText)); Changed(nameof(CompactText));
    }

    private void EndRing(bool missed)
    {
        if (ringing == null) return;
        var item = ringing;
        Audio.Stop();
        ringing = null; snoozed = null; snoozeUntil = null;
        RingingStateChanged();
        RingingChanged?.Invoke(false);
        if (missed) alerts.Show(AlertQueue.Bell, Loc.F("Alarm.Missed", item.TimeText), SoundKind.None);
        if (waitingToRing.Count > 0) Ring(waitingToRing.Dequeue());
    }

    private void SnoozeRing()
    {
        if (ringing == null) return;
        var item = ringing;
        var minutes = S.SnoozeMinutes;
        EndRing(missed: false);
        snoozed = item;
        snoozeUntil = DateTime.Now.AddMinutes(minutes);
        alerts.Show(AlertQueue.Bell, Loc.F("Alarm.Snoozed", minutes), SoundKind.None);
    }
}
