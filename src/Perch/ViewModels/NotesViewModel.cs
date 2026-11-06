using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Perch.Services;

namespace Perch.ViewModels;

public sealed class NoteItem : Observable
{
    private string title, text;
    private bool highlight;
    public NoteItem(string title, string text, bool highlight, DateTime updated) { this.title = title; this.text = text; this.highlight = highlight; Updated = updated; }

    // The title is its own field. A note with no title (typed body only) is listed under its first line.
    public string Title
    {
        get => title;
        set { if (Set(ref title, value)) Edited(); }
    }
    public string Text
    {
        get => text;
        set { if (Set(ref text, value)) Edited(); }
    }
    public bool Highlight { get => highlight; set => Set(ref highlight, value); }
    public DateTime Updated { get; private set; }
    public bool IsBlank => string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text);
    public bool TitleEmpty => string.IsNullOrEmpty(title);
    public bool BodyEmpty => string.IsNullOrEmpty(text);
    private string[] Lines => text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string DisplayTitle => !string.IsNullOrWhiteSpace(title) ? title.Trim() : Lines.FirstOrDefault() ?? Loc.T("Notes.New");
    public string Preview => string.Join(" ", string.IsNullOrWhiteSpace(title) ? Lines.Skip(1) : Lines);
    // One line for the overview page: the title, then the start of the body.
    public string Summary => string.Join(" · ", new[] { DisplayTitle, Preview }.Where(part => part.Length > 0));
    public string UpdatedLabel => Updated.Date == DateTime.Today ? Updated.ToString("HH:mm") : Loc.Date(Updated, "Format.ShortDate");
    public void Relocalize() => Changed(string.Empty);

    private void Edited()
    {
        Updated = DateTime.Now;
        Changed(nameof(DisplayTitle)); Changed(nameof(Preview)); Changed(nameof(Summary)); Changed(nameof(UpdatedLabel));
        Changed(nameof(IsBlank)); Changed(nameof(TitleEmpty)); Changed(nameof(BodyEmpty));
    }
}

public sealed class NotesViewModel : Observable
{
    private readonly Store store;
    private NoteItem? selected;
    public ObservableCollection<NoteItem> Items { get; } = [];
    public NoteItem? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) Changed(nameof(HasSelection)); }
    }
    public bool HasSelection => selected != null;
    // Shown on the overview page: the first highlighted note, otherwise the latest one.
    public string OverviewText
    {
        get
        {
            var note = Items.FirstOrDefault(n => n.Highlight && !n.IsBlank) ?? Items.Where(n => !n.IsBlank).OrderByDescending(n => n.Updated).FirstOrDefault();
            return note?.Summary ?? Loc.T("Notes.Empty");
        }
    }
    public ICommand New { get; }
    public ICommand Delete { get; }
    public ICommand ToggleHighlight { get; }

    public NotesViewModel(Store store)
    {
        this.store = store;
        foreach (var data in store.State.Notes.OrderByDescending(n => n.Highlight).ThenByDescending(n => n.Updated))
        {
            var title = data.Title;
            var body = data.Text;
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(body))
            {
                // Notes saved before titles existed: the first line becomes the title, the rest stays as the body.
                var lines = body.ReplaceLineEndings("\n").Split('\n');
                var first = Array.FindIndex(lines, line => line.Trim().Length > 0);
                title = lines[first].Trim();
                body = string.Join("\n", lines.Skip(first + 1)).TrimStart('\n');
            }
            Add(new NoteItem(title, body, data.Highlight, data.Updated == default ? DateTime.Now : data.Updated));
        }
        if (Items.Count == 0) Add(Blank());
        Selected = Items[0];
        store.Saving += () => store.State.Notes = Items.Where(n => !n.IsBlank).Select(n => new NoteData { Title = n.Title, Text = n.Text, Highlight = n.Highlight, Updated = n.Updated }).ToList();

        New = new RelayCommand(_ =>
        {
            var blank = Items.FirstOrDefault(n => n.IsBlank);
            if (blank == null) { blank = Blank(); Insert(Items.Count(n => n.Highlight), blank); }
            Selected = blank;
        });
        Delete = new RelayCommand(p =>
        {
            if ((p as NoteItem ?? Selected) is not { } note) return;
            var index = Items.IndexOf(note);
            note.PropertyChanged -= NoteChanged;
            Items.Remove(note);
            if (Items.Count == 0) Add(Blank());
            Selected = Items[Math.Clamp(index, 0, Items.Count - 1)];
            Saved();
        });
        ToggleHighlight = new RelayCommand(p =>
        {
            if ((p as NoteItem ?? Selected) is not { } note) return;
            note.Highlight = !note.Highlight;
            var ordered = Items.OrderByDescending(n => n.Highlight).ThenByDescending(n => n.Updated).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var from = Items.IndexOf(ordered[i]);
                if (from != i) Items.Move(from, i);
            }
            Selected = note;
        });
    }

    private static NoteItem Blank() => new("", "", false, DateTime.Now);

    public void Relocalize()
    {
        foreach (var note in Items) note.Relocalize();
        Changed(string.Empty);
    }

    private void Add(NoteItem note) => Insert(Items.Count, note);

    private void Insert(int index, NoteItem note)
    {
        note.PropertyChanged += NoteChanged;
        Items.Insert(index, note);
    }

    private void NoteChanged(object? sender, PropertyChangedEventArgs e) => Saved();

    private void Saved()
    {
        store.Touch();
        Changed(nameof(OverviewText));
    }
}
