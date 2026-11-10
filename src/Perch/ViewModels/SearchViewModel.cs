using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Perch.Services;

namespace Perch.ViewModels;

public enum HitKind { App, Todo, Note, Clip }

// One line of the search results: what it is, what to show, and the thing itself.
public sealed class SearchHit(HitKind kind, string title, string detail, object source, string header, Bitmap? icon = null)
{
    public HitKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public object Source { get; } = source;
    // Set on the first hit of each group ("Apps", "To-dos", ...), empty on the rest.
    public string Header { get; } = header;
    public bool HasHeader => Header.Length > 0;
    public Bitmap? Icon { get; } = icon;
    public bool HasIcon => Icon != null;
    public bool NoIcon => Icon == null;
    // Segoe Fluent Icons: the same glyphs as the pages' tabs.
    public string Glyph => Kind switch { HitKind.Todo => "", HitKind.Note => "", HitKind.Clip => "", _ => "" };
}

// One search box over everything Perch holds: installed apps, to-dos, notes and the clipboard history. Every word typed must
// appear in an item (any case), and what is found is grouped in that order. Enter or a click uses the hit: an app starts,
// a clipboard item is copied again, a to-do or note opens on its own page.
public sealed class SearchViewModel : Observable
{
    private const int PerGroup = 6;
    private readonly Store store;
    private readonly TodoViewModel todo;
    private readonly NotesViewModel notes;
    private readonly ClipboardViewModel clipboard;
    private readonly LauncherViewModel launcher;
    private string query = "";
    private SearchHit? selected;

    public ObservableCollection<SearchHit> Hits { get; } = [];
    public string Query { get => query; set { if (Set(ref query, value)) Run(); } }
    public SearchHit? Selected { get => selected; set => Set(ref selected, value); }
    public bool Typed => query.Trim().Length > 0;
    public bool Idle => !Typed;
    public bool NoResults => Typed && Hits.Count == 0;
    public ICommand Open { get; }
    // Asks the window to show a page ("todo", "notes"); the hit's own item is already selected there.
    public event Action<string>? Navigate;

    public SearchViewModel(Store store, TodoViewModel todo, NotesViewModel notes, ClipboardViewModel clipboard, LauncherViewModel launcher)
    {
        this.store = store; this.todo = todo; this.notes = notes; this.clipboard = clipboard; this.launcher = launcher;
        Open = new RelayCommand(p => { if (p is SearchHit hit) Activate(hit); });
        // Apps are read from the Start menu in the background; results appear once they have been.
        launcher.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LauncherViewModel.Status)) Run(); };
    }

    // The page has come on screen: start fresh, and make sure the app list has been read.
    public void Reset()
    {
        launcher.EnsureLoaded();
        query = "";
        Changed(nameof(Query));
        Run();
    }

    public void Relocalize() { Changed(string.Empty); Run(); }

    public void MoveSelection(int step)
    {
        if (Hits.Count == 0) return;
        var at = selected == null ? -1 : Hits.IndexOf(selected);
        Selected = Hits[Math.Clamp(at + step, 0, Hits.Count - 1)];
    }

    public void ActivateSelected()
    {
        if (selected != null) Activate(selected);
    }

    private void Activate(SearchHit hit)
    {
        switch (hit.Source)
        {
            case AppEntry app: launcher.Launch.Execute(app); break;
            case ClipItem clip: clipboard.Copy.Execute(clip); break;
            case TodoItem: Navigate?.Invoke("todo"); break;
            case NoteItem note: notes.Selected = note; Navigate?.Invoke("notes"); break;
        }
    }

    // With nothing typed the page is not empty: it offers what is most likely wanted (pinned apps, open to-dos, the latest notes
    // and copies). With words typed, every word must appear in an item.
    private void Run()
    {
        var terms = query.Split([' ', '	', '　'], StringSplitOptions.RemoveEmptyEntries);
        var idle = terms.Length == 0;
        Hits.Clear();
        Changed(nameof(Typed)); Changed(nameof(Idle));
        var s = store.State.Settings;

        bool Matches(string text) => terms.All(t => text.Contains(t, StringComparison.CurrentCultureIgnoreCase));
        // Earlier and shorter matches first: a name that starts with the word beats one that merely contains it.
        int Rank(string title) => idle ? 0 : title.StartsWith(terms[0], StringComparison.CurrentCultureIgnoreCase) ? 0 : title.Contains(terms[0], StringComparison.CurrentCultureIgnoreCase) ? 1 : 2;
        var group = "";
        void Add<T>(HitKind kind, string header, IEnumerable<(T Item, string Title, string Detail, Bitmap? Icon)> found, int limit = PerGroup)
        {
            group = header;
            foreach (var (item, title, detail, icon) in found.Take(limit))
            {
                Hits.Add(new SearchHit(kind, title, detail, item!, group, icon));
                group = "";
            }
        }

        if (s.ShowApps)
            Add(HitKind.App, Loc.T(idle ? "Search.PinnedApps" : "Search.Apps"),
                launcher.AllApps.Where(a => idle ? a.Pinned : Matches(a.Name)).OrderBy(a => Rank(a.Name)).ThenBy(a => a.Name.Length)
                .Select(a => (a, a.Name, "", a.Icon)));
        if (s.ShowTodo)
            Add(HitKind.Todo, Loc.T(idle ? "Search.OpenTodos" : "Search.Todos"),
                todo.All.Where(t => idle ? !t.Done : Matches(t.Text)).OrderBy(t => t.Done).ThenBy(t => Rank(t.Text)).ThenBy(t => t.Due ?? DateTime.MaxValue)
                .Select(t => (t, t.Text, t.Done ? Loc.T("Search.Done") : t.DueLabel, (Bitmap?)null)), idle ? 4 : PerGroup);
        if (s.ShowNotes)
            Add(HitKind.Note, Loc.T(idle ? "Search.RecentNotes" : "Search.Notes"),
                notes.Items.Where(n => !n.IsBlank && (idle || Matches(n.Title + " " + n.Text))).OrderBy(n => Rank(n.DisplayTitle)).ThenByDescending(n => n.Updated)
                .Select(n => (n, n.DisplayTitle, n.Preview, (Bitmap?)null)), idle ? 3 : PerGroup);
        if (s.ShowClipboard)
            Add(HitKind.Clip, Loc.T(idle ? "Search.RecentClips" : "Search.Clipboard"),
                clipboard.Items.Where(c => !c.IsImage && (idle || Matches(c.Text))).Select(c => (c, c.Preview, c.TimeLabel, (Bitmap?)null)), idle ? 3 : PerGroup);
        Selected = Hits.FirstOrDefault();
        Changed(nameof(NoResults));
    }
}
