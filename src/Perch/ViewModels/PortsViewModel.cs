using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Platform.Windows;
using Perch.Services;
using Perch.Services.Ports;

namespace Perch.ViewModels;

// One row of the list: a port a program is listening on.
public sealed class PortEntry
{
    public PortEntry(int port, int pid, string process, string? path, PortScope scope, string address, Bitmap? icon)
    {
        Port = port; Pid = pid; Process = process; Path = path; Scope = scope; Address = address; Icon = icon;
    }

    public int Port { get; }
    public int Pid { get; }
    public string Process { get; }
    public string? Path { get; }
    public PortScope Scope { get; }
    // The address it listens on: 127.0.0.1, 0.0.0.0, or one interface's address.
    public string Address { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;
    public bool Exposed => Scope != PortScope.Local;
    public string ScopeText => Scope switch { PortScope.Local => Loc.T("Ports.ScopeLocal"), PortScope.All => Loc.T("Ports.ScopeAll"), _ => Address };
    public string Detail => $"PID {Pid}";
    public string Tip => $"{Process}  ·  PID {Pid}  ·  {Address}:{Port}" + (Path != null ? Environment.NewLine + Path : "");
}

// The ports on this machine that programs are listening on: which process, which port, whether only this computer
// or every network interface can reach it, and the program's icon. Refreshes while the page is showing.
public sealed class PortsViewModel : Observable
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<PortEntry> all = [];
    private string filter = "all", search = "";
    private bool active;

    public ObservableCollection<PortEntry> Items { get; } = [];
    public bool ShowsAll => filter == "all";
    public bool ShowsLocal => filter == "local";
    public bool ShowsExposed => filter == "exposed";
    public bool IsEmpty => Items.Count == 0;
    public string CountText => Loc.F("Ports.Count", Items.Count);
    public string Search { get => search; set { if (Set(ref search, value ?? "")) Rebuild(); } }

    public ICommand SetFilter { get; }
    public ICommand Refresh { get; }
    public ICommand CopyPort { get; }
    public ICommand CopyAddress { get; }
    public ICommand OpenBrowser { get; }
    public ICommand Reveal { get; }

    public PortsViewModel()
    {
        refresh.Tick += (_, _) => Load();
        SetFilter = new RelayCommand(p => { filter = p as string ?? "all"; Rebuild(); });
        Refresh = new RelayCommand(_ => Load());
        CopyPort = new RelayCommand(p => { if (p is PortEntry e) Copy(e.Port.ToString()); });
        CopyAddress = new RelayCommand(p => { if (p is PortEntry e) Copy($"localhost:{e.Port}"); });
        OpenBrowser = new RelayCommand(p => { if (p is PortEntry e) ShellActions.Open($"http://localhost:{e.Port}/"); });
        Reveal = new RelayCommand(p => { if (p is PortEntry { Path: { } path }) ShellActions.Reveal(path); });
    }

    // The list only needs to be fresh while somebody is looking at it.
    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value) { Load(); refresh.Start(); } else refresh.Stop();
        }
    }

    public void Relocalize() => Rebuild();

    private static void Copy(string text) { if (Ui.Clipboard is { } clipboard) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text); }

    private void Load()
    {
        if (PortSource.Current is not { } source) { all = []; Rebuild(); return; }
        try
        {
            // The same program usually listens on a port twice (IPv4 and IPv6): one row, with the wider reach.
            all = source.Read()
                .GroupBy(l => (l.Pid, l.Port))
                .Select(group =>
                {
                    var first = group.First();
                    var scopes = group.Select(l => PortSource.ScopeOf(l.Address)).ToList();
                    var scope = scopes.Contains(PortScope.All) ? PortScope.All : scopes.Contains(PortScope.Address) ? PortScope.Address : PortScope.Local;
                    var shown = group.FirstOrDefault(l => PortSource.ScopeOf(l.Address) == scope) ?? first;
                    return new PortEntry(first.Port, first.Pid, first.ProcessName, first.ProcessPath, scope, shown.Address.ToString(),
                        first.ProcessPath is { } path ? source.Icon(path) : null);
                })
                .OrderBy(e => e.Port).ThenBy(e => e.Process, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception) { /* the table cannot be read right now; keep what is shown */ return; }
        Rebuild();
    }

    private void Rebuild()
    {
        var wanted = all.Where(e => filter switch { "local" => e.Scope == PortScope.Local, "exposed" => e.Exposed, _ => true });
        if (search.Trim() is { Length: > 0 } text)
            wanted = wanted.Where(e => e.Process.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Port.ToString().Contains(text) || e.Pid.ToString() == text);
        var list = wanted.ToList();
        // Only touch the visible list when something changed, so scrolling and selection survive the refresh.
        if (!Items.Select(i => (i.Pid, i.Port)).SequenceEqual(list.Select(i => (i.Pid, i.Port)))
            || Items.Zip(list).Any(pair => pair.First.Scope != pair.Second.Scope))
        {
            Items.Clear();
            foreach (var entry in list) Items.Add(entry);
        }
        Changed(string.Empty);
    }
}
