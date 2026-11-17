using Avalonia.Threading;
using Perch.Services;
using Perch.Services.Sounds;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Perch.Platform.Windows;

public sealed record SystemNotificationItem(string App, string Title, string Body, DateTime Time);

// Listens to Windows Toast notifications via UserNotificationListener and forwards new ones to Perch's island alerts.
internal sealed class SystemNotifications : IDisposable
{
    private static readonly object Gate = new();
    private static readonly List<SystemNotificationItem> RecentList = [];

    private readonly Store store;
    private readonly AlertQueue alerts;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<uint> seen = [];
    private UserNotificationListener? listener;
    private bool seeded, busy;

    public static IReadOnlyList<SystemNotificationItem> Recent
    {
        get { lock (Gate) return RecentList.ToList(); }
    }

    public SystemNotifications(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        poll.Tick += (_, _) => _ = CheckAsync();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            listener = UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed) { listener = null; return; }
            await CheckAsync();
            poll.Start();
        }
        catch (Exception e)
        {
            Log.Warn("Windows notification listener unavailable", e);
            listener = null;
        }
    }

    private async Task CheckAsync()
    {
        if (busy || listener == null) return;
        busy = true;
        try
        {
            var items = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var incoming = new List<SystemNotificationItem>();
            foreach (var n in items)
            {
                if (!seen.Add(n.Id)) continue;
                var app = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? [];
                var title = texts.ElementAtOrDefault(0) ?? "";
                var body = string.Join(" ", texts.Skip(1));
                if (title.Length == 0 && body.Length == 0) continue;
                var entry = new SystemNotificationItem(app, title, body, n.CreationTime.LocalDateTime);
                lock (Gate)
                {
                    RecentList.Insert(0, entry);
                    if (RecentList.Count > 25) RecentList.RemoveRange(25, RecentList.Count - 25);
                }
                if (seeded && store.State.Settings.SystemNotifications) incoming.Add(entry);
            }
            seeded = true;
            foreach (var item in incoming)
            {
                var text = string.IsNullOrEmpty(item.App)
                    ? (string.IsNullOrEmpty(item.Body) ? item.Title : $"{item.Title} — {item.Body}")
                    : (string.IsNullOrEmpty(item.Body) ? $"{item.App}: {item.Title}" : $"{item.App}: {item.Title} — {item.Body}");
                alerts.Show(AlertQueue.Bell, text, SoundKind.None);
            }
        }
        catch { }
        finally { busy = false; }
    }

    public void Dispose() => poll.Stop();
}
