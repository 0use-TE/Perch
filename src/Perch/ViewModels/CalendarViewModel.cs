using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Avalonia.Threading;
using Perch.Services;
using Windows.ApplicationModel.Appointments;

namespace Perch.ViewModels;

public sealed record CalendarRow(string DayLabel, bool ShowDay, string TimeText, string Title, string Location, bool Now);

// Upcoming events from calendar links (.ics): Google Calendar, Outlook.com, Apple Calendar and others can publish a
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

    public ObservableCollection<CalendarRow> Rows { get; } = [];
    public bool HasFeeds { get; private set; }
    public bool NoFeeds => !HasFeeds;
    public bool IsEmpty => HasFeeds && Rows.Count == 0 && !loading;
    public string Status { get; private set; } = "";
    public ICommand Refresh { get; }

    public CalendarViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        Refresh = new RelayCommand(_ => _ = RefreshAsync());
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
        var from = DateTime.Today;
        var to = DateTime.Today.AddDays(8);
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

    private void Rebuild()
    {
        Rows.Clear();
        var now = DateTime.Now;
        string? lastDay = null;
        foreach (var e in events.Where(e => e.End > now || (e.AllDay && e.End.Date > now.Date)).Take(40))
        {
            var day = e.Start.Date <= now.Date && e.End > now ? Loc.T("Common.Today") : DayLabel(e.Start.Date);
            var time = e.AllDay ? Loc.T("Cal.AllDay") : $"{e.Start:HH:mm}" + (e.End > e.Start ? $" – {e.End:HH:mm}" : "");
            Rows.Add(new CalendarRow(day, day != lastDay, time, e.Title, e.Location, !e.AllDay && e.Start <= now && e.End > now));
            lastDay = day;
        }
        Changed(string.Empty);
    }

    private static string DayLabel(DateTime day) =>
        day == DateTime.Today ? Loc.T("Common.Today") : day == DateTime.Today.AddDays(1) ? Loc.T("Common.Tomorrow") : day.ToString("dddd d", Loc.Culture);

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
