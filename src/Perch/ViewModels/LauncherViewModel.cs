using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Perch.Platform.Windows;
using Perch.Services;

namespace Perch.ViewModels;

public sealed class AppEntry : Observable
{
    private Bitmap? icon;
    private bool pinned;
    public AppEntry(string name, string fullPath, string folder) { Name = name; FullPath = fullPath; Folder = folder; }
    public string Name { get; }
    public string FullPath { get; }
    public string Folder { get; }
    public Bitmap? Icon { get => icon; set => Set(ref icon, value); }
    public bool Pinned { get => pinned; set => Set(ref pinned, value); }
}

public sealed class CommandItem(string name, string command)
{
    public string Name { get; } = name;
    public string Command { get; } = command;
}

// Apps from the Start menu (classic shortcuts) plus user-defined command lines, the Windows stand-in for Shortcuts.
public sealed class LauncherViewModel : Observable
{
    private static readonly string[] Skipped = ["uninstall", "卸载", "readme", "help", "帮助", "documentation", "website"];
    private readonly Store store;
    private readonly AlertQueue alerts;
    private List<AppEntry> all = [];
    private string search = "", section = "apps", newName = "", newCommand = "";
    private bool pinMode, loading, loaded;

    public ObservableCollection<AppEntry> Visible { get; } = [];
    public ObservableCollection<CommandItem> Commands { get; } = [];
    public string Search { get => search; set { if (Set(ref search, value)) Filter(); } }
    public bool PinMode => pinMode;
    public string PinModeLabel => Loc.T(pinMode ? "Common.Done" : "Apps.EditPinned");
    public bool ShowsApps => section == "apps";
    public bool ShowsCommands => section == "commands";
    public string Status => loading ? Loc.T("Apps.Loading") : Visible.Count == 0 ? Loc.T(search.Length > 0 ? "Apps.NoMatch" : "Apps.None") : "";
    public bool HasStatus => Status.Length > 0;
    public bool NoCommands => Commands.Count == 0;
    public string NewName { get => newName; set => Set(ref newName, value); }
    public string NewCommand { get => newCommand; set => Set(ref newCommand, value); }

    public ICommand Launch { get; }
    public ICommand TogglePinMode { get; }
    public ICommand SetSection { get; }
    public ICommand AddCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RemoveCommand { get; }

    public LauncherViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        foreach (var c in store.State.Commands) Commands.Add(new(c.Name, c.Command));
        store.Saving += () =>
        {
            store.State.Commands = Commands.Select(c => new CommandData { Name = c.Name, Command = c.Command }).ToList();
            if (loaded) store.State.PinnedApps = all.Where(a => a.Pinned).Select(a => a.FullPath).ToList();
        };

        Launch = new RelayCommand(p =>
        {
            if (p is not AppEntry app) return;
            if (pinMode) { app.Pinned = !app.Pinned; store.Touch(); return; }
            if (!ShellActions.Open(app.FullPath)) alerts.Show(AlertQueue.Warning, Loc.F("Common.CantOpen", app.Name));
        });
        TogglePinMode = new RelayCommand(_ => { pinMode = !pinMode; Filter(); });
        SetSection = new RelayCommand(p => { section = p as string ?? "apps"; Changed(string.Empty); });
        AddCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(NewCommand)) return;
            var name = string.IsNullOrWhiteSpace(NewName) ? NewCommand.Trim() : NewName.Trim();
            Commands.Add(new(name, NewCommand.Trim()));
            NewName = ""; NewCommand = "";
            store.Touch();
            Changed(nameof(NoCommands));
        });
        RunCommand = new RelayCommand(p =>
        {
            if (p is not CommandItem c) return;
            var ok = ShellActions.Run(c.Command);
            alerts.Show(ok ? AlertQueue.Done : AlertQueue.Warning, Loc.F(ok ? "Apps.Ran" : "Apps.RunFailed", c.Name));
        });
        RemoveCommand = new RelayCommand(p =>
        {
            if (p is CommandItem c) { Commands.Remove(c); store.Touch(); Changed(nameof(NoCommands)); }
        });
    }

    public void Relocalize() => Changed(string.Empty);

    // Reads the Start menu the first time the page opens; icons load in the background afterwards.
    public async void EnsureLoaded()
    {
        if (loaded || loading) return;
        loading = true;
        Changed(nameof(Status)); Changed(nameof(HasStatus));
        var pinned = new HashSet<string>(store.State.PinnedApps, StringComparer.OrdinalIgnoreCase);
        var found = await Task.Run(() => Scan());
        all = found.Select(f => new AppEntry(f.Name, f.Path, f.Folder) { Pinned = pinned.Contains(f.Path) }).ToList();
        loading = false;
        loaded = true;
        Filter();
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        var entries = all.ToList();
        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                var icon = ShellIcons.Get(entry.FullPath);
                if (icon != null) dispatcher.InvokeAsync(() => { entry.Icon = icon; });
            }
        });
    }

    private static List<(string Name, string Path, string Folder)> Scan()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var seen = new Dictionary<string, (string Name, string Path, string Folder)>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (Skipped.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)) || seen.ContainsKey(name)) continue;
                var folder = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root);
                seen[name] = (name, file, folder == "." ? "" : folder);
            }
        }
        return seen.Values.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Pinned apps come first; in pin mode every app shows so it can be pinned.
    private void Filter()
    {
        var query = search.Trim();
        IEnumerable<AppEntry> items = all;
        if (query.Length > 0)
            items = items.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || a.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        items = items.OrderByDescending(a => a.Pinned);
        Visible.Clear();
        foreach (var item in items) Visible.Add(item);
        Changed(string.Empty);
    }
}
