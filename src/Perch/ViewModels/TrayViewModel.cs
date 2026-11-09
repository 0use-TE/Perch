using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Perch.Platform.Windows;
using Perch.Services;
using Perch.Services.Sounds;

namespace Perch.ViewModels;

public sealed class TrayFile : Observable
{
    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"];
    public TrayFile(string fullPath)
    {
        FullPath = fullPath;
        IsFolder = Directory.Exists(fullPath);
        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        IsImage = !IsFolder && ImageTypes.Contains(Path.GetExtension(fullPath).ToLowerInvariant());
        length = !IsFolder && File.Exists(fullPath) ? new FileInfo(fullPath).Length : null;
        Icon = ShellIcons.Get(fullPath);
        if (IsImage) _ = LoadPreviewAsync();
    }

    // A small picture of the image itself instead of the generic file icon (loaded in the background; other formats keep the icon).
    private async Task LoadPreviewAsync()
    {
        var picture = await Task.Run(() =>
        {
            try { using var stream = File.OpenRead(FullPath); return Bitmap.DecodeToWidth(stream, 180); }
            catch (Exception) { return null; }
        });
        Preview = picture;
    }

    private Bitmap? preview;
    public Bitmap? Preview { get => preview; private set { if (Set(ref preview, value)) { Changed(nameof(HasPreview)); Changed(nameof(NoPreview)); } } }
    public bool HasPreview => preview != null;
    public bool NoPreview => preview == null;
    public string FullPath { get; }
    public string Name { get; }
    private readonly long? length;
    public string Detail => IsFolder ? Loc.T("Tray.Folder") : length is { } bytes ? Size(bytes) : Loc.T("Tray.Moved");
    public bool IsFolder { get; }
    public bool IsImage { get; }
    public Bitmap? Icon { get; }
    public bool Exists => IsFolder ? Directory.Exists(FullPath) : File.Exists(FullPath);

    internal static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

// A shelf for files dragged onto the island. Only paths are kept; the files stay where they are.
public sealed class TrayViewModel : Observable
{
    private readonly Store store;
    private readonly AlertQueue alerts;
    private TrayFile? selected;
    private bool busy;

    public ObservableCollection<TrayFile> Files { get; } = [];
    public TrayFile? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) Changed(string.Empty); }
    }
    public bool IsEmpty => Files.Count == 0;
    public bool HasFiles => Files.Count > 0;
    public bool HasSelection => selected != null;
    public bool CanConvert => selected?.IsImage == true && !busy;
    public string CountText => Files.Count == 0 ? "" : Loc.F("Tray.Count", Files.Count);

    public ICommand Open { get; }
    public ICommand Reveal { get; }
    public ICommand Remove { get; }
    public ICommand Clear { get; }
    public ICommand Zip { get; }
    public ICommand Convert { get; }

    public TrayViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        Add(store.State.TrayFiles);
        store.Saving += () => store.State.TrayFiles = Files.Select(f => f.FullPath).ToList();
        Open = new RelayCommand(p => { if ((p as TrayFile ?? selected) is { } f && !ShellActions.Open(f.FullPath)) alerts.Show(AlertQueue.Warning, Loc.F("Common.CantOpen", f.Name)); });
        Reveal = new RelayCommand(p => { if ((p as TrayFile ?? selected) is { } f) ShellActions.Reveal(f.FullPath); });
        Remove = new RelayCommand(p => { if ((p as TrayFile ?? selected) is { } f) RemoveFile(f); });
        Clear = new RelayCommand(_ => { Files.Clear(); Selected = null; Saved(); });
        Zip = new RelayCommand(async _ => await ZipAsync());
        Convert = new RelayCommand(async p => await ConvertAsync(p as string ?? "png"));
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) continue;
            if (Files.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new TrayFile(path));
        }
        Saved();
    }

    public void RemoveFile(TrayFile file)
    {
        Files.Remove(file);
        if (selected == file) Selected = null;
        Saved();
    }

    // Rebuilds the file tiles so their size / folder labels pick up the new language.
    public void Relocalize()
    {
        var files = Files.ToList();
        Files.Clear();
        foreach (var file in files) Files.Add(file);
        Changed(string.Empty);
    }

    private void Saved()
    {
        store.Touch();
        Changed(string.Empty);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // Packs every file on the shelf into one zip next to the first file.
    private async Task ZipAsync()
    {
        var items = Files.Where(f => f.Exists).ToList();
        if (items.Count == 0 || busy) return;
        busy = true;
        Changed(string.Empty);
        var folder = Path.GetDirectoryName(items[0].FullPath.TrimEnd(Path.DirectorySeparatorChar)) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var target = Unique(Path.Combine(folder, $"Perch-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
        try
        {
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
                foreach (var item in items)
                {
                    if (!item.IsFolder) { zip.CreateEntryFromFile(item.FullPath, item.Name, CompressionLevel.Optimal); continue; }
                    foreach (var file in Directory.EnumerateFiles(item.FullPath, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(file, Path.Combine(item.Name, Path.GetRelativePath(item.FullPath, file)).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            });
            Add([target]);
            alerts.Show(AlertQueue.Done, Loc.F("Tray.Zipped", Path.GetFileName(target)), SoundKind.None);
        }
        catch
        {
            alerts.Show(AlertQueue.Warning, Loc.T("Tray.ZipFailed"));
        }
        finally
        {
            busy = false;
            Changed(string.Empty);
        }
    }

    // Saves a PNG or JPEG copy (JPEG at quality 85, which also shrinks large images) beside the original.
    private async Task ConvertAsync(string format)
    {
        if (selected is not { IsImage: true } source || busy) return;
        busy = true;
        Changed(string.Empty);
        var extension = format == "jpg" ? ".jpg" : ".png";
        var target = Unique(Path.Combine(Path.GetDirectoryName(source.FullPath) ?? "", Path.GetFileNameWithoutExtension(source.FullPath) + extension));
        try
        {
            await Task.Run(() => Platform.Windows.ImageConversion.Save(source.FullPath, target, format == "jpg"));
            Add([target]);
            alerts.Show(AlertQueue.Done, Loc.F("Tray.Saved", Path.GetFileName(target)), SoundKind.None);
        }
        catch
        {
            alerts.Show(AlertQueue.Warning, Loc.T("Tray.ConvertFailed"));
        }
        finally
        {
            busy = false;
            Changed(string.Empty);
        }
    }
}
