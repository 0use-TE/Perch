using Avalonia.Threading;

namespace Perch.Services;

// Water, sitting and bedtime reminders, shown through the island's alert queue.
public sealed class Reminders : IDisposable
{
    private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Store store;
    private readonly AlertQueue alerts;
    private DateTime lastWater = DateTime.Now, activeSince = DateTime.Now;
    private DateTime? bedtimeShown;

    public Reminders(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        timer.Tick += (_, _) => Check();
        timer.Start();
    }

    private void Check()
    {
        var s = store.State.Settings;
        var now = DateTime.Now;
        var away = UserInput.IdleTime() >= AwayAfter;

        // Stepping away for five minutes counts as a break from sitting.
        if (away) activeSince = now;
        else if (s.SitReminder && now - activeSince >= TimeSpan.FromMinutes(Math.Max(10, s.SitMinutes)))
        {
            alerts.Show(AlertQueue.Bell, Loc.F("Remind.Sit", Math.Max(10, s.SitMinutes)));
            activeSince = now;
        }

        if (s.WaterReminder && !away && now.Hour is >= 8 and < 22 && now - lastWater >= TimeSpan.FromMinutes(Math.Max(10, s.WaterMinutes)))
        {
            alerts.Show(AlertQueue.Bell, Loc.T("Remind.Water"));
            lastWater = now;
        }

        if (s.BedtimeReminder && TimeSpan.TryParse(s.Bedtime, out var bed) && bedtimeShown != now.Date
            && now.TimeOfDay >= bed && now.TimeOfDay < bed + TimeSpan.FromMinutes(30))
        {
            alerts.Show(AlertQueue.Moon, Loc.T("Remind.Bed"));
            bedtimeShown = now.Date;
        }
    }

    public void Dispose() => timer.Stop();
}
