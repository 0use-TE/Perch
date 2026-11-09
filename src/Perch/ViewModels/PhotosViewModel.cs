using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Perch.Services;

namespace Perch.ViewModels;

public sealed class PhotoItem(string fullPath) : Observable
{
    private Bitmap? thumbnail;
    public string FullPath { get; } = fullPath;
    public Bitmap? Thumbnail { get => thumbnail; set => Set(ref thumbnail, value); }
}

// The newest pictures from the photo folder (Pictures unless set otherwise), loaded the first time the page opens.
public sealed class PhotosViewModel : Observable
{
    private static readonly string[] Types = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic"];
    private readonly Store store;
    private bool loaded, loading;

    public ObservableCollection<PhotoItem> Items { get; } = [];
    public string Folder => string.IsNullOrWhiteSpace(store.State.Settings.PhotoFolder)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        : store.State.Settings.PhotoFolder.Trim();
    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;
    public string Status => loading ? Loc.T("Photos.Loading") : Items.Count == 0 ? Loc.T("Photos.Empty") : Loc.F("Photos.Count", Items.Count);
    public bool IsEmpty => !loading && Items.Count == 0;
    public ICommand Open { get; }
    public ICommand OpenFolder { get; }
    public ICommand Reload { get; }

    public PhotosViewModel(Store store)
    {
        this.store = store;
        Open = new RelayCommand(p => { if (p is PhotoItem photo) ShellActions.Open(photo.FullPath); });
        OpenFolder = new RelayCommand(_ => ShellActions.Open(Folder));
        Reload = new RelayCommand(_ => { loaded = false; EnsureLoaded(); });
    }

    public void Relocalize() => Changed(string.Empty);

    public void SettingsChanged()
    {
        Changed(string.Empty);
        if (loaded) { loaded = false; EnsureLoaded(); }
    }

    public async void EnsureLoaded()
    {
        if (loaded || loading) return;
        loading = true;
        Items.Clear();
        Changed(string.Empty);
        var folder = Folder;
        var files = await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(folder)) return new List<string>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
                return Directory.EnumerateFiles(folder, "*", options)
                    .Where(f => Types.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(40)
                    .Select(f => f.FullName)
                    .ToList();
            }
            catch { return new List<string>(); }
        });
        foreach (var file in files) Items.Add(new PhotoItem(file));
        loading = false;
        loaded = true;
        Changed(string.Empty);
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        var photos = Items.ToList();
        await Task.Run(() =>
        {
            foreach (var photo in photos)
            {
                var image = Thumbnail(photo.FullPath);
                if (image != null) dispatcher.InvokeAsync(() => { photo.Thumbnail = image; });
            }
        });
    }

    private static Bitmap? Thumbnail(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            return Bitmap.DecodeToHeight(input, 240);
        }
        catch { return null; }
    }
}
