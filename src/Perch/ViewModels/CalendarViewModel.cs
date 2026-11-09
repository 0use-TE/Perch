using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Avalonia.Threading;
using Perch.Services;
using Windows.ApplicationModel.Appointments;

namespace Perch.ViewModels;

public sealed record CalendarRow(string DayLabel, bool ShowDay, string TimeText, string Title, string Location, bool Now);

// One day of the month grid.
public sealed record CalendarCell(string Label, DateTime Date, bool InMonth, bool Today, bool Selected, bool HasEvents);

// A month calendar with the events of the selected day, from calendar links (.ics): Google Calendar, Outlook.com, Apple Calendar and others can publish a
// private link, and a local .ics file works too. (Windows' own calendar store is not open to desktop apps.)
public sealed class CalendarViewModel : Observable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Store store;
    private readonly AlertQueue alerts;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer remindTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> reminded = [];
    private List<CalEvent> events = [];
    private bool loading;

    private DateTime viewMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime selected = DateTime.Today;
    private DateTime loadedFrom, loadedTo;

    public ObservableCollection<CalendarCell> Cells { get; } = [];
    public ObservableCollection<CalendarRow> DayEvents { get; } = [];
    public string MonthTitle => viewMonth.ToString("yyyy / MM");
    public string SelectedLabel => Loc.Date(selected, "Format.Date");
    public bool HasFeeds { get; private set; }
    public bool NoFeeds => !HasFeeds;
    public bool DayEmpty => HasFeeds && DayEvents.Count == 0 && !loading;
    public string Status { get; private set; } = "";
    public ICommand Refresh { get; }
    public ICommand PrevMonth { get; }
    public ICommand NextMonth { get; }
    public ICommand GoToday { get; }
    public ICommand SelectDay { get; }

    public CalendarViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        Refresh = new RelayCommand(_ => _ = RefreshAsync());
        PrevMonth = new RelayCommand(_ => MoveMonth(-1));
        NextMonth = new RelayCommand(_ => MoveMonth(1));
        GoToday = new RelayCommand(_ => { selected = DateTime.Today; viewMonth = new DateTime(selected.Year, selected.Month, 1); EnsureRange(); Rebuild(); });
        SelectDay = new RelayCommand(p =>
        {
            if (p is not DateTime day) return;
            selected = day.Date;
            if (day.Year != viewMonth.Year || day.Month != viewMonth.Month) { viewMonth = new DateTime(day.Year, day.Month, 1); EnsureRange(); }
            Rebuild();
        });
        Rebuild();
        refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        remindTimer.Tick += (_, _) => Remind();
        refreshTimer.Start();
        remindTimer.Start();
        _ = RefreshAsync();
    }

    private Settings S => store.State.Settings;

    public async Task RefreshAsync()
    {
        if (loading) return;
        var feeds = S.CalendarUrls.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        HasFeeds = feeds.Count > 0 || S.CalendarWindows;
        if (!HasFeeds)
        {
            events = [];
            Status = "";
            Rebuild();
            return;
        }
        loading = true;
        Status = Loc.T("Cal.Loading");
        Changed(string.Empty);
        // The viewed month with a month either side, always including the next week (reminders look ahead).
        var from = new[] { viewMonth.AddMonths(-1), DateTime.Today }.Min();
        var to = new[] { viewMonth.AddMonths(2), DateTime.Today.AddDays(8) }.Max();
        loadedFrom = from; loadedTo = to;
        var all = new List<CalEvent>();
        var failed = 0;
        foreach (var feed in feeds)
        {
            try
            {
                string text;
                if (feed.StartsWith("http", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("webcal", StringComparison.OrdinalIgnoreCase))
                    text = await Http.GetStringAsync(feed.StartsWith("webcal", StringComparison.OrdinalIgnoreCase) ? "https" + feed[6..] : feed);
                else if (File.Exists(feed)) text = await File.ReadAllTextAsync(feed);
                else { failed++; continue; }
                all.AddRange(IcsParser.Parse(text, from, to));
            }
            catch (Exception)
            {
                failed++;
            }
        }
        if (S.CalendarWindows)
        {
            try
            {
                // The calendars of Windows' own store (accounts added to the Windows calendar), expanded for repeats by Windows.
                var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
                foreach (var item in await store.FindAppointmentsAsync(from, to - from))
                    all.Add(new CalEvent(string.IsNullOrWhiteSpace(item.Subject) ? "(no title)" : item.Subject, item.StartTime.LocalDateTime, (item.StartTime + item.Duration).LocalDateTime, item.AllDay, item.Location ?? ""));
            }
            catch (Exception)
            {
                failed++;
            }
        }
        loading = false;
        events = all.OrderBy(e => e.Start).ToList();
        Status = failed > 0 ? Loc.T("Cal.Failed") : Loc.F("Cal.Updated", DateTime.Now.ToString("HH:mm"));
        Rebuild();
        Remind();
    }

    private void MoveMonth(int delta)
    {
        viewMonth = viewMonth.AddMonths(delta);
        selected = viewMonth.Year == DateTime.Today.Year && viewMonth.Month == DateTime.Today.Month ? DateTime.Today : viewMonth;
        EnsureRange();
        Rebuild();
    }

    // Events are loaded for a few months around the viewed one; paging further reloads.
    private void EnsureRange()
    {
        if (HasFeeds && (viewMonth.AddMonths(-1) < loadedFrom || viewMonth.AddMonths(2) > loadedTo)) _ = RefreshAsync();
    }

    private static bool OnDay(CalEvent e, DateTime day) =>
        e.Start < day.AddDays(1) && (e.End > day || (e.End <= e.Start && e.Start.Date == day));

    private void Rebuild()
    {
        var today = DateTime.Today;
        Cells.Clear();
        var first = viewMonth;
        var lead = ((int)first.DayOfWeek + 6) % 7; // weeks start on Monday
        var days = DateTime.DaysInMonth(first.Year, first.Month);
        var total = (lead + days + 6) / 7 * 7;
        for (var i = 0; i < total; i++)
        {
            var date = first.AddDays(i - lead);
            Cells.Add(new CalendarCell(date.Day.ToString(), date, date.Month == first.Month, date == today, date == selected, events.Any(e => OnDay(e, date))));
        }
        DayEvents.Clear();
        var now = DateTime.Now;
        foreach (var e in events.Where(e => OnDay(e, selected)))
        {
            var time = e.AllDay ? Loc.T("Cal.AllDay") : $"{e.Start:HH:mm}" + (e.End > e.Start ? $" – {e.End:HH:mm}" : "");
            DayEvents.Add(new CalendarRow("", false, time, e.Title, e.Location, !e.AllDay && e.Start <= now && e.End > now));
        }
        Changed(string.Empty);
    }

    // A heads-up shortly before each timed event, once.
    private void Remind()
    {
        var minutes = S.CalendarReminder;
        if (minutes <= 0) return;
        var now = DateTime.Now;
        foreach (var e in events.Where(e => !e.AllDay && e.Start > now.AddSeconds(-30) && e.Start <= now.AddMinutes(minutes)))
        {
            var key = $"{e.Start:O}|{e.Title}";
            if (!reminded.Add(key)) continue;
            var left = (int)Math.Ceiling((e.Start - now).TotalMinutes);
            alerts.Show(AlertQueue.Bell, left <= 0 ? Loc.F("Cal.Now", e.Title) : Loc.F("Cal.Soon", e.Title, left));
        }
        if (reminded.Count > 200) reminded.Clear();
    }

    public void SettingsChanged() => _ = RefreshAsync();
    public void Relocalize() => Rebuild();
}
