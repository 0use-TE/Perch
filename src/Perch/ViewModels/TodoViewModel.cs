using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Perch.Services;

namespace Perch.ViewModels;

public sealed class TodoItem : Observable
{
    private bool done;
    private DateTime? due;
    public TodoItem(string text, DateTime created) { Text = text; Created = created; }
    public string Text { get; }
    public DateTime Created { get; }
    public DateTime? Completed { get; set; }
    public bool Done
    {
        get => done;
        set { if (Set(ref done, value)) { Completed = value ? DateTime.Now : null; Changed(nameof(IsOverdue)); } }
    }
    public DateTime? Due
    {
        get => due;
        set { if (Set(ref due, value?.Date)) { Changed(nameof(DueLabel)); Changed(nameof(IsOverdue)); Changed(nameof(HasDue)); } }
    }
    public bool HasDue => Due != null;
    public bool IsOverdue => !Done && Due is { } d && d < DateTime.Today;
    public string DueLabel => Due is not { } d ? "" : TodoViewModel.DayName(d) is { } name ? name : d < DateTime.Today ? Loc.F("Todo.Overdue", Loc.Date(d, "Format.ShortDate")) : Loc.Date(d, "Format.ShortDate");
}

public sealed record WeekColumn(string Weekday, string Day, DateTime Date, bool Today, IReadOnlyList<TodoItem> Items);
public sealed record MonthCell(string Day, DateTime Date, bool InMonth, bool Today, int Count)
{
    public bool HasItems => Count > 0;
}

public enum TodoView { List, Day, Week, Month }

public sealed class TodoViewModel : Observable
{
    private readonly Store store;
    private string draft = "", filter = "all", sort = "created";
    private DateTime? draftDue;
    private TodoView view = TodoView.List;
    private DateTime anchor = DateTime.Today;
    private bool refreshQueued;

    public ObservableCollection<TodoItem> All { get; } = [];
    public ObservableCollection<TodoItem> Visible { get; } = [];
    public ObservableCollection<WeekColumn> Week { get; } = [];
    public ObservableCollection<MonthCell> Month { get; } = [];

    public string Draft { get => draft; set => Set(ref draft, value); }
    public string DraftDueLabel => draftDue is { } d ? DayName(d) ?? Loc.Date(d, "Format.ShortDate") : Loc.T("Todo.NoDate");
    public bool DraftHasDue => draftDue != null;

    public bool FilterAll => filter == "all";
    public bool FilterActive => filter == "active";
    public bool FilterDone => filter == "done";
    public string SortLabel => Loc.T(sort == "due" ? "Todo.SortDue" : "Todo.SortCreated");
    public bool ViewList => view == TodoView.List;
    public bool ViewDay => view == TodoView.Day;
    public bool ViewWeek => view == TodoView.Week;
    public bool ViewMonth => view == TodoView.Month;
    public bool ShowsList => view is TodoView.List or TodoView.Day;
    public bool ShowsInput => view != TodoView.Month;
    public bool ShowsPeriod => view != TodoView.List;
    public bool IsEmpty => ShowsList && Visible.Count == 0;
    public string EmptyText => Loc.T(view == TodoView.Day ? "Todo.EmptyDay" : filter == "done" ? "Todo.EmptyDone" : "Todo.Empty");
    public string PeriodTitle => view switch
    {
        TodoView.Day => Loc.Date(anchor, "Format.DayTitle"),
        TodoView.Week => Loc.F("Format.Range", Loc.Date(WeekStart(anchor), "Format.ShortDate"), Loc.Date(WeekStart(anchor).AddDays(6), "Format.ShortDate")),
        TodoView.Month => Loc.Date(anchor, "Format.MonthYear"),
        _ => "",
    };

    public string CountText => All.Count(t => !t.Done).ToString("00");
    public string Summary => All.Where(t => !t.Done).OrderBy(t => t.Due ?? DateTime.MaxValue).ThenByDescending(t => t.Created).FirstOrDefault()?.Text ?? Loc.T("Todo.Empty");

    public ICommand Add { get; }
    public ICommand Delete { get; }
    public ICommand ClearDone { get; }
    public ICommand SetFilter { get; }
    public ICommand ToggleSort { get; }
    public ICommand SetView { get; }
    public ICommand CycleDue { get; }
    public ICommand Previous { get; }
    public ICommand Next { get; }
    public ICommand Today { get; }
    public ICommand OpenDay { get; }

    public TodoViewModel(Store store)
    {
        this.store = store;
        foreach (var data in store.State.Todos)
        {
            var item = new TodoItem(data.Text, data.Created == default ? DateTime.Now : data.Created) { Due = data.Due, Done = data.Done };
            item.Completed = data.Completed;
            Hook(item);
            All.Add(item);
        }
        All.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null) foreach (TodoItem item in e.OldItems) item.PropertyChanged -= ItemChanged;
            Changed_();
        };
        store.Saving += () => store.State.Todos = All.Select(t => new TodoData { Text = t.Text, Done = t.Done, Due = t.Due, Created = t.Created, Completed = t.Completed }).ToList();

        Add = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(Draft)) return;
            var item = new TodoItem(Draft.Trim(), DateTime.Now) { Due = draftDue ?? (view is TodoView.Day or TodoView.Week ? anchor : null) };
            Hook(item);
            All.Add(item);
            Draft = "";
            draftDue = null;
        });
        Delete = new RelayCommand(p => { if (p is TodoItem item) All.Remove(item); });
        ClearDone = new RelayCommand(_ => { foreach (var item in All.Where(t => t.Done).ToList()) All.Remove(item); });
        SetFilter = new RelayCommand(p => { filter = p as string ?? "all"; Refresh(); });
        ToggleSort = new RelayCommand(_ => { sort = sort == "due" ? "created" : "due"; Refresh(); });
        SetView = new RelayCommand(p =>
        {
            view = p switch { "day" => TodoView.Day, "week" => TodoView.Week, "month" => TodoView.Month, _ => TodoView.List };
            Refresh();
        });
        CycleDue = new RelayCommand(_ =>
        {
            var today = DateTime.Today;
            var weekend = today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);
            draftDue = draftDue == null ? today : draftDue == today ? today.AddDays(1) : draftDue == today.AddDays(1) && weekend > today.AddDays(1) ? weekend : null;
            Changed(nameof(DraftDueLabel)); Changed(nameof(DraftHasDue));
        });
        Previous = new RelayCommand(_ => Shift(-1));
        Next = new RelayCommand(_ => Shift(1));
        Today = new RelayCommand(_ => { anchor = DateTime.Today; Refresh(); });
        OpenDay = new RelayCommand(p => { if (p is DateTime date) { anchor = date.Date; view = TodoView.Day; Refresh(); } });
        Refresh();
    }

    // Labels like Today / Tomorrow depend on the date and language, so lists rebuild at midnight.
    public void DayChanged() => Refresh();

    internal static string? DayName(DateTime date)
    {
        var today = DateTime.Today;
        return date.Date == today ? Loc.T("Common.Today") : date.Date == today.AddDays(1) ? Loc.T("Common.Tomorrow") : date.Date == today.AddDays(-1) ? Loc.T("Common.Yesterday") : null;
    }

    private static DateTime WeekStart(DateTime date) => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private void Hook(TodoItem item) => item.PropertyChanged += ItemChanged;

    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => Changed_();

    // Rebuilt after the current input event finishes, so a checkbox isn't removed while it is being clicked.
    private void Changed_()
    {
        store.Touch();
        if (refreshQueued) return;
        refreshQueued = true;
        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Shift(int direction)
    {
        anchor = view switch
        {
            TodoView.Week => anchor.AddDays(7 * direction),
            TodoView.Month => anchor.AddMonths(direction),
            _ => anchor.AddDays(direction),
        };
        Refresh();
    }

    private IEnumerable<TodoItem> Filtered(IEnumerable<TodoItem> items) => filter switch
    {
        "active" => items.Where(t => !t.Done),
        "done" => items.Where(t => t.Done),
        _ => items,
    };

    private void Refresh()
    {
        refreshQueued = false;
        IEnumerable<TodoItem> items = All;
        if (view == TodoView.Day) items = items.Where(t => t.Due == anchor || (anchor == DateTime.Today && t.IsOverdue));
        items = Filtered(items);
        items = sort == "due"
            ? items.OrderBy(t => t.Done).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenByDescending(t => t.Created)
            : items.OrderBy(t => t.Done).ThenByDescending(t => t.Created);
        Visible.Clear();
        foreach (var item in items) Visible.Add(item);

        Week.Clear();
        if (view == TodoView.Week)
        {
            var start = WeekStart(anchor);
            for (var i = 0; i < 7; i++)
            {
                var date = start.AddDays(i);
                var dayItems = Filtered(All.Where(t => t.Due == date)).OrderBy(t => t.Done).ToList();
                Week.Add(new(date.ToString("ddd", Loc.Culture), date.Day.ToString(), date, date == DateTime.Today, dayItems));
            }
        }

        Month.Clear();
        if (view == TodoView.Month)
        {
            var first = new DateTime(anchor.Year, anchor.Month, 1);
            var start = WeekStart(first);
            var cells = (int)Math.Ceiling(((first - start).Days + DateTime.DaysInMonth(first.Year, first.Month)) / 7.0) * 7;
            for (var i = 0; i < cells; i++)
            {
                var date = start.AddDays(i);
                Month.Add(new(date.Day.ToString(), date, date.Month == first.Month, date == DateTime.Today, All.Count(t => !t.Done && t.Due == date)));
            }
        }
        Changed(string.Empty);
    }
}
