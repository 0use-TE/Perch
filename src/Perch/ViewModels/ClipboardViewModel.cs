using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System.Windows.Input;
using Perch.Services;
using Perch.Services.Sounds;

namespace Perch.ViewModels;

public enum ClipKind { Text, Image, Files }

// One thing that was copied: some text, a picture, or a set of files and folders (the paths, not the contents).
public sealed class ClipItem : Observable
{
    private bool pinned;

    // Text.
    public ClipItem(string text, DateTime time)
    {
        Kind = ClipKind.Text; Text = text; Time = time;
        // One tidy line for the list: whitespace collapsed, long text cut.
        Preview = Cut(Regex.Replace(text.Trim(), @"\s+", " "), 160);
        Detail = Cut(text, 600);
        Key = text;
    }

    private ClipItem(ClipKind kind, string text, string preview, string detail, string key, DateTime time)
    {
        Kind = kind; Text = text; Preview = preview; Detail = detail; Key = key; Time = time;
    }

    // Files and folders, by path.
    public static ClipItem ForFiles(IReadOnlyList<string> paths, DateTime time)
    {
        var first = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
        if (first.Length == 0) first = paths[0];
        var preview = paths.Count == 1 ? first : Loc.F("Clip.FilesMore", first, paths.Count - 1);
        return new ClipItem(ClipKind.Files, string.Join('\n', paths), preview, string.Join('\n', paths.Take(12)), "files:" + string.Join('\n', paths), time) { Paths = paths };
    }

    // A picture. Kept as compressed PNG (for putting it back) plus a small thumbnail (for the list); null when too large.
    public static ClipItem? ForImage(Bitmap bitmap, DateTime time)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        if (stream.Length > 30_000_000) return null;
        var png = stream.ToArray();
        stream.Position = 0;
        var thumb = Bitmap.DecodeToWidth(stream, 220);
        var size = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
        return new ClipItem(ClipKind.Image, "", Loc.F("Clip.Image", size), Loc.F("Clip.Image", size), "image:" + Convert.ToHexString(SHA256.HashData(png)), time) { Png = png, Thumb = thumb };
    }

    public ClipKind Kind { get; }
    public string Text { get; }
    public DateTime Time { get; }
    public string Preview { get; }
    // What the tooltip shows.
    public string Detail { get; }
    // What identifies this copy (the same text, picture or files copied again is the same item).
    public string Key { get; }
    public IReadOnlyList<string> Paths { get; private init; } = [];
    public byte[]? Png { get; private init; }
    public Bitmap? Thumb { get; private init; }
    public bool IsText => Kind == ClipKind.Text;
    public bool IsImage => Kind == ClipKind.Image;
    public bool IsFiles => Kind == ClipKind.Files;
    public bool Pinned { get => pinned; set { if (Set(ref pinned, value)) { Changed(nameof(PinGlyph)); Changed(nameof(PinTip)); } } }
    public string PinGlyph => pinned ? "" : ""; // filled / empty star
    public string PinTip => Loc.T(pinned ? "Clip.Unpin" : "Clip.Pin");
    public string TimeLabel => Time.Date == DateTime.Today ? Time.ToString("HH:mm") : Loc.Date(Time, "Format.ShortDate");
    public void Relocalize() { Changed(nameof(TimeLabel)); Changed(nameof(PinTip)); }
    private static string Cut(string value, int length) => value.Length > length ? value[..length] + "…" : value;
}

// Recent copies: text, pictures and files. The history is kept in memory only (never written to disk, since the clipboard
// often holds passwords), and copies marked by password managers as "exclude from history" are skipped. Items the user pins
// stay at the top, survive Clear, and (text and files; pictures are too large) are remembered between runs.
public sealed class ClipboardViewModel : Observable
{
    private const int Max = 40, MaxImages = 8;
    private readonly Store store;
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
    public ICommand TogglePin { get; }

    public ClipboardViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        foreach (var saved in store.State.PinnedClips)
        {
            ClipItem? item = saved.Paths.Count > 0 ? ClipItem.ForFiles(saved.Paths, saved.Time) : saved.Text.Length > 0 ? new ClipItem(saved.Text, saved.Time) : null;
            if (item == null) continue;
            item.Pinned = true;
            Items.Add(item);
        }
        store.Saving += () => store.State.PinnedClips = Items.Where(i => i.Pinned && i.Kind != ClipKind.Image)
            .Select(i => new PinnedClip { Time = i.Time, Text = i.IsText ? i.Text : "", Paths = i.IsFiles ? i.Paths.ToList() : [] }).ToList();
        Items.CollectionChanged += (_, _) => Changed(nameof(IsEmpty));
        Copy = new RelayCommand(p => { if (p is ClipItem item) _ = CopyAsync(item); });
        Remove = new RelayCommand(p => { if (p is ClipItem item) { Items.Remove(item); store.Touch(); } });
        Clear = new RelayCommand(_ =>
        {
            for (var i = Items.Count - 1; i >= 0; i--) if (!Items[i].Pinned) Items.RemoveAt(i); // pinned items stay
        });
        TogglePause = new RelayCommand(_ => Paused = !Paused);
        TogglePin = new RelayCommand(p =>
        {
            if (p is not ClipItem item) return;
            item.Pinned = !item.Pinned;
            Items.Remove(item);
            // Pinned items sit at the top (the newest pin first); an unpinned one goes back to the top of the recent ones.
            Items.Insert(item.Pinned ? 0 : Items.Count(i => i.Pinned), item);
            store.Touch();
        });
    }

    private async Task CopyAsync(ClipItem item)
    {
        try
        {
            if (Ui.Clipboard is not { } clipboard) return;
            ownCopy = item.Key;
            switch (item.Kind)
            {
                case ClipKind.Text:
                    await clipboard.SetTextAsync(item.Text);
                    break;
                case ClipKind.Image when item.Png != null:
                    using (var stream = new MemoryStream(item.Png)) await clipboard.SetBitmapAsync(new Bitmap(stream));
                    break;
                case ClipKind.Files:
                    var storage = Ui.Root?.StorageProvider;
                    var found = new List<IStorageItem>();
                    if (storage != null)
                        foreach (var path in item.Paths)
                        {
                            IStorageItem? entry = Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(new Uri(path)) : File.Exists(path) ? await storage.TryGetFileFromPathAsync(new Uri(path)) : null;
                            if (entry != null) found.Add(entry);
                        }
                    if (found.Count == 0) { ownCopy = null; alerts.Show(AlertQueue.Done, Loc.T("Clip.FilesGone"), SoundKind.None); return; }
                    await clipboard.SetFilesAsync(found);
                    break;
                default:
                    ownCopy = null;
                    return;
            }
            alerts.Show(AlertQueue.Done, Loc.T("Clip.Copied"), SoundKind.None);
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
            var formats = await clipboard.GetDataFormatsAsync();
            if (formats.Any(format => format.Identifier is "ExcludeClipboardContentFromMonitorProcessing" or "x-kde-passwordManagerHint")) return; // password managers set this
            ClipItem? item = null;
            // Text wins when there is also a picture of it (copying from a document gives both).
            if (await clipboard.TryGetTextAsync() is { } text)
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
                item = new ClipItem(text, DateTime.Now);
            }
            else if (formats.Contains(DataFormat.File) && await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
                if (paths.Count == 0) return;
                item = ClipItem.ForFiles(paths, DateTime.Now);
            }
            else if (formats.Contains(DataFormat.Bitmap) && await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap) item = ClipItem.ForImage(bitmap, DateTime.Now);
            }
            if (item == null) return;
            if (ownCopy != null && ownCopy == item.Key) { ownCopy = null; return; } // what we just put back ourselves

            var same = Items.FirstOrDefault(existing => existing.Key == item.Key);
            if (same is { Pinned: true }) return; // a pinned item stays where it is
            if (same != null) Items.Remove(same);
            Items.Insert(Items.Count(i => i.Pinned), item);
            Trim();
        }
        catch (Exception)
        {
            // Another app has the clipboard open; the next change event will try again.
        }
    }

    // Keeps the history to its size (pinned items do not count), and pictures, which are large, to fewer.
    private void Trim()
    {
        var recent = Items.Where(i => !i.Pinned).ToList();
        foreach (var old in recent.Skip(Max)) Items.Remove(old);
        foreach (var old in Items.Where(i => i.IsImage && !i.Pinned).Skip(MaxImages).ToList()) Items.Remove(old);
    }

    public void Relocalize()
    {
        foreach (var item in Items) item.Relocalize();
        Changed(string.Empty);
    }
}
