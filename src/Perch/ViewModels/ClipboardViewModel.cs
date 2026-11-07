using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Input.Platform;
using Perch.Platform.Windows;
using System.Windows.Input;
using Perch.Services;

namespace Perch.ViewModels;

public sealed class ClipItem(string text, DateTime time) : Observable
{
    public string Text { get; } = text;
    public DateTime Time { get; } = time;
    // One tidy line for the list: whitespace collapsed, long text cut.
    public string Preview { get; } = Cut(Regex.Replace(text.Trim(), @"\s+", " "), 160);
    public string TimeLabel => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Loc.Date(Time, "Format.ShortDate");
    public void Relocalize() => Changed(nameof(TimeLabel));
    private static string Cut(string value, int length) => value.Length > length ? value[..length] + "…" : value;
}

// Recent copied text. It is kept in memory only (never written to disk, since the clipboard often holds passwords),
// and copies marked by password managers as "exclude from history" are skipped.
public sealed class ClipboardViewModel : Observable
{
    private const int Max = 40;
    private readonly AlertQueue alerts;
    private string? ownCopy;
    private bool paused;

    public ObservableCollection<ClipItem> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public bool Paused { get => paused; private set { if (Set(ref paused, value)) { Changed(nameof(PauseLabel)); Changed(nameof(PausedNote)); } } }
    public string PauseLabel => Loc.T(paused ? "Clip.Resume" : "Clip.Pause");
    public string PausedNote => paused ? Loc.T("Clip.Paused") : "";
    public ICommand Copy { get; }
    public ICommand Remove { get; }
    public ICommand Clear { get; }
    public ICommand TogglePause { get; }

    public ClipboardViewModel(AlertQueue alerts)
    {
        this.alerts = alerts;
        Items.CollectionChanged += (_, _) => Changed(nameof(IsEmpty));
        Copy = new RelayCommand(p =>
        {
            if (p is not ClipItem item) return;
            ownCopy = item.Text;
            _ = CopyAsync(item.Text);
        });
        Remove = new RelayCommand(p => { if (p is ClipItem item) Items.Remove(item); });
        Clear = new RelayCommand(_ => Items.Clear());
        TogglePause = new RelayCommand(_ => Paused = !Paused);
    }

    private async Task CopyAsync(string text)
    {
        try
        {
            if (Ui.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(text);
            alerts.Show(AlertQueue.Done, Loc.T("Clip.Copied"));
        }
        catch (Exception) { ownCopy = null; }
    }

    // Called from the window's clipboard-changed message.
    public async void Capture()
    {
        if (paused) return;
        try
        {
            if (Ui.Clipboard is not { } clipboard) return;
#pragma warning disable CS0618 // the replacement API has a different shape; this one works on Windows
            var formats = await clipboard.GetFormatsAsync();
            if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing")) return; // password managers set this
            if (await clipboard.GetTextAsync() is not { } text) return;
#pragma warning restore CS0618
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
            if (ownCopy != null && ownCopy == text) { ownCopy = null; return; } // what we just put back ourselves
            var same = Items.FirstOrDefault(item => item.Text == text);
            if (same != null) Items.Remove(same);
            Items.Insert(0, new ClipItem(text, DateTime.Now));
            while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
        }
        catch (Exception)
        {
            // Another app has the clipboard open; the next change event will try again.
        }
    }

    public void Relocalize()
    {
        foreach (var item in Items) item.Relocalize();
        Changed(string.Empty);
    }
}
