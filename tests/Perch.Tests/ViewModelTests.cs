using System.Collections.ObjectModel;
using Perch.Services;
using Perch.Services.Ai;
using Perch.ViewModels;

namespace Perch.Tests;

internal static class ChatLineTests
{
    public static async Task Run()
    {
        await T.Group("Chat message: blocks follow the text", () =>
        {
            var line = new ChatLine("assistant");
            line.Text = "Hello";
            var first = line.Segments[0];
            line.Text = "Hello world";
            T.Check(ReferenceEquals(first, line.Segments[0]), "a growing paragraph updates its block in place");
            T.Eq("Hello world", line.Segments[0].Text, "and shows the new text");
            line.Text = "Hello world\n\nSecond paragraph";
            T.Eq(2, line.Segments.Count, "a new paragraph is a new block");
            T.Check(ReferenceEquals(first, line.Segments[0]), "and the first block is left alone");

            line.Text = "# Title\n\n1. one\n2. two\n\n> quote\n\n---\n\n```cs\nint a;\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |";
            T.Eq("Heading,ListItem,ListItem,Quote,Rule,Code,Table", string.Join(",", line.Segments.Select(s => s.Kind)), "every kind of block is made");
            T.Eq("Title", line.Segments[0].Runs[0].Text, "heading text");
            T.Eq("•", new ChatLine("assistant", "- a").Segments[0].Info, "bullet marker");
            T.Eq("cs", line.Segments[5].Info, "code language is kept for the label");
            T.Eq(1, line.Segments[6].Table!.Rows.Count, "table rows");
            T.Check(line.Segments[5].IsCode && !line.Segments[5].IsText, "block kind flags");

            var bold = new ChatLine("assistant", "a **b** c");
            T.Eq("B", string.Join(",", bold.Segments[0].Runs.Where(r => r.Bold).Select(_ => "B")), "inline bold becomes a bold run");
            var error = new ChatLine("assistant", "**not bold**", error: true);
            T.Eq(1, error.Segments[0].Runs.Count, "an error message is shown as typed");
            T.Check(!error.Segments[0].Runs[0].Bold, "its marks are not interpreted");

            line.Text = "short";
            T.Eq(1, line.Segments.Count, "blocks that are no longer there are removed");
            line.Text = "";
            T.Eq(0, line.Segments.Count, "no text, no blocks");

            var caret = new ChatLine("assistant", "text") { Caret = true };
            T.Eq("text\u258D", caret.Segments[0].Runs.Last().Text.Length > 0 ? string.Concat(caret.Segments[0].Runs.Select(r => r.Text)) : "", "the typing caret follows the last paragraph");
            caret.Caret = false;
            T.Eq("text", string.Concat(caret.Segments[0].Runs.Select(r => r.Text)), "and goes away");
            var codeEnd = new ChatLine("assistant", "```\ncode\n```") { Caret = true };
            T.Eq(2, codeEnd.Segments.Count, "after code the caret gets a block of its own");
            return Task.CompletedTask;
        });

        await T.Group("Chat message: thinking text", () =>
        {
            var line = new ChatLine("assistant");
            line.Thinking = new string('x', 5000);
            T.Eq(5000, line.ThinkingShown.Length, "finished thinking is shown whole");
            line.Working = true;
            T.Check(line.ThinkingShown.Length <= 1501 && line.ThinkingShown.StartsWith("…"), "while it is written only the latest part is drawn");
            line.Working = false;
            line.Thinking = new string('y', 50000);
            T.Eq(30001, line.ThinkingShown.Length, "even finished thinking is capped");
            T.Eq(50000, line.Thinking.Length, "but the whole text is kept");
            line.Working = true;
            line.Thinking = new string('a', 2000) + "😀" + new string('b', 1498);
            T.Check(!char.IsLowSurrogate(line.ThinkingShown[1]), "the cut never splits a surrogate pair");
            return Task.CompletedTask;
        });
    }
}

internal static class ClipboardTests
{
    public static async Task Run()
    {
        await T.Group("Clipboard items", () =>
        {
            var text = new ClipItem("  hello \n\n   world  ", new DateTime(2026, 1, 1));
            T.Eq("hello world", text.Preview, "text preview is one tidy line");
            T.Eq(ClipKind.Text, text.Kind, "kind");
            T.Eq(161, new ClipItem(new string('x', 400), DateTime.Now).Preview.Length, "long text is cut with an ellipsis");
            var files = ClipItem.ForFiles(["C:/a/report.pdf", "C:/a/b.txt", "C:/a/c.txt"], DateTime.Now);
            T.Eq(ClipKind.Files, files.Kind, "files kind");
            T.Check(files.Detail.Contains("report.pdf") && files.Paths.Count == 3, "files keep their paths");
            T.Eq("report.pdf", ClipItem.ForFiles(["C:/a/report.pdf"], DateTime.Now).Preview, "a single file is just its name");
            T.Check(files.Key != text.Key && files.Key == ClipItem.ForFiles(["C:/a/report.pdf", "C:/a/b.txt", "C:/a/c.txt"], DateTime.Now).Key, "the same files are the same item");
            text.Pinned = true;
            T.Check(text.PinGlyph != new ClipItem("x", DateTime.Now).PinGlyph, "a pinned item shows a different star");
            return Task.CompletedTask;
        });

        await T.Group("Clipboard history: pinning", () =>
        {
            var store = new Store();
            var clipboard = new ClipboardViewModel(store, new AlertQueue());
            var a = new ClipItem("a", DateTime.Now); var b = new ClipItem("b", DateTime.Now); var c = new ClipItem("c", DateTime.Now);
            clipboard.Items.Add(a); clipboard.Items.Add(b); clipboard.Items.Add(c);
            clipboard.TogglePin.Execute(c);
            T.Eq("c,a,b", Order(clipboard), "a pinned item moves to the top");
            clipboard.TogglePin.Execute(b);
            T.Eq("b,c,a", Order(clipboard), "the newest pin goes first");
            clipboard.Clear.Execute(null);
            T.Eq("b,c", Order(clipboard), "Clear keeps what is pinned");
            clipboard.TogglePin.Execute(b);
            T.Eq("c,b", Order(clipboard), "an unpinned item goes below the pinned ones");
            clipboard.Remove.Execute(c);
            T.Eq("b", Order(clipboard), "a pinned item can still be removed");
            T.Check(!clipboard.IsEmpty, "not empty while something is left");
            return Task.CompletedTask;
        });

        await T.Group("Clipboard history: pins survive a restart", () =>
        {
            var store = new Store();
            var clipboard = new ClipboardViewModel(store, new AlertQueue());
            var note = new ClipItem("remember this", new DateTime(2026, 2, 3, 4, 5, 6));
            var files = ClipItem.ForFiles(["C:/x/one.txt", "C:/x/two.txt"], DateTime.Now);
            clipboard.Items.Add(note); clipboard.Items.Add(files); clipboard.Items.Add(new ClipItem("not pinned", DateTime.Now));
            clipboard.TogglePin.Execute(note); clipboard.TogglePin.Execute(files);
            store.Flush();

            var again = new Store();
            T.Eq(2, again.State.PinnedClips.Count, "two pins were saved (the unpinned copy was not)");
            var reloaded = new ClipboardViewModel(again, new AlertQueue());
            T.Eq(2, reloaded.Items.Count, "both come back");
            T.Check(reloaded.Items.All(i => i.Pinned), "still pinned");
            T.Check(reloaded.Items.Any(i => i.Text == "remember this"), "the text is back");
            T.Check(reloaded.Items.Any(i => i.IsFiles && i.Paths.SequenceEqual(["C:/x/one.txt", "C:/x/two.txt"])), "the file paths are back");
            return Task.CompletedTask;
        });
    }

    private static string Order(ClipboardViewModel clipboard) => string.Join(",", clipboard.Items.Select(i => i.Text));
}

internal static class SearchTests
{
    public static async Task Run()
    {
        await T.Group("Global search", () =>
        {
            var store = new Store();
            var alerts = new AlertQueue();
            var todo = new TodoViewModel(store);
            var notes = new NotesViewModel(store);
            var clipboard = new ClipboardViewModel(store, alerts);
            var launcher = new LauncherViewModel(store, alerts);
            var search = new SearchViewModel(store, todo, notes, clipboard, launcher);

            todo.All.Add(new TodoItem("Buy milk", DateTime.Now));
            todo.All.Add(new TodoItem("Call the bank", DateTime.Now));
            todo.All.Add(new TodoItem("Old milk chore", DateTime.Now) { Done = true });
            notes.Items.Add(new NoteItem("Recipe", "pancakes need milk and eggs", false, DateTime.Now));
            notes.Items.Add(new NoteItem("Travel", "passport", false, DateTime.Now));
            clipboard.Items.Add(new ClipItem("milk shake link", DateTime.Now));
            clipboard.Items.Add(new ClipItem("unrelated", DateTime.Now));

            search.Query = "milk";
            T.Eq("Todo,Todo,Note,Clip", string.Join(",", search.Hits.Select(h => h.Kind)), "every kind of item is searched, grouped, open to-dos before done ones");
            T.Eq("Buy milk", search.Hits[0].Title, "an open to-do comes before a done one");
            T.Check(search.Hits[0].HasHeader && !search.Hits[1].HasHeader && search.Hits[2].HasHeader, "a heading starts each group");
            T.Eq(search.Hits[0], search.Selected, "the first hit is highlighted");

            search.Query = "MILK pancakes";
            T.Eq("Recipe", string.Join(",", search.Hits.Select(h => h.Title)), "every word must match, in any case");
            search.Query = "zzz";
            T.Check(search.NoResults && search.Hits.Count == 0, "nothing found says so");
            search.Query = "   ";
            T.Check(search.Idle && !search.NoResults, "spaces alone are no search");

            search.Query = "";
            T.Check(search.Hits.Any(h => h.Kind == HitKind.Todo && h.Title == "Buy milk"), "an empty box offers open to-dos");
            T.Check(!search.Hits.Any(h => h.Title == "Old milk chore"), "but not finished ones");
            T.Check(search.Hits.Any(h => h.Kind == HitKind.Note) && search.Hits.Any(h => h.Kind == HitKind.Clip), "and recent notes and copies");

            search.Query = "milk";
            store.State.Settings.ShowTodo = false;
            search.Query = "milk ";
            T.Check(!search.Hits.Any(h => h.Kind == HitKind.Todo), "a feature that is switched off is not searched");
            store.State.Settings.ShowTodo = true;

            search.Query = "milk";
            var before = search.Selected;
            search.MoveSelection(1);
            T.Check(!ReferenceEquals(before, search.Selected), "the arrow keys move the highlight");
            search.MoveSelection(-100);
            T.Eq(search.Hits[0], search.Selected, "and stop at the first hit");
            search.MoveSelection(100);
            T.Eq(search.Hits[^1], search.Selected, "and at the last one");

            string? went = null;
            search.Navigate += id => went = id;
            search.Selected = search.Hits.First(h => h.Kind == HitKind.Note);
            search.ActivateSelected();
            T.Eq("notes", went, "opening a note goes to the notes page");
            T.Eq("Recipe", notes.Selected?.Title, "with that note selected");
            return Task.CompletedTask;
        });
    }
}

internal static class StoreTests
{
    public static async Task Run()
    {
        await T.Group("Settings file", () =>
        {
            var store = new Store();
            T.Eq("Ctrl+Alt+F", store.State.Settings.HotkeySearch, "default search shortcut");
            T.Check(store.State.Settings.NavOrder.Contains("search"), "the search page is in the navigation order");
            store.State.Todos.Add(new TodoData { Text = "persisted", Created = DateTime.Now });
            store.State.Settings.GlassOpacity = 33;
            store.State.PinnedClips.Add(new PinnedClip { Text = "kept", Time = DateTime.Now });
            store.Flush();
            var again = new Store();
            T.Eq(33, again.State.Settings.GlassOpacity, "a setting survives a restart");
            T.Eq("persisted", again.State.Todos.FirstOrDefault(t => t.Text == "persisted")?.Text, "a to-do survives a restart");
            T.Check(again.State.PinnedClips.Any(p => p.Text == "kept"), "a pinned copy survives a restart");
            return Task.CompletedTask;
        });

        await T.Group("Hotkey text", () =>
        {
            T.Check(Perch.Platform.HotkeyParser.TryParse("Ctrl+Alt+F", out var modifiers, out var key), "Ctrl+Alt+F parses");
            T.Eq(Perch.Platform.HotkeyParser.Control | Perch.Platform.HotkeyParser.Alt, modifiers, "its modifiers");
            T.Eq(0x46u, key, "its key is F");
            T.Check(Perch.Platform.HotkeyParser.TryParse("ctrl + shift + 5", out _, out var digit) && digit == 0x35, "case, spaces and digits");
            T.Check(!Perch.Platform.HotkeyParser.TryParse("F", out _, out _), "a key without a modifier is refused");
            T.Check(!Perch.Platform.HotkeyParser.TryParse("Ctrl+Nope", out _, out _), "an unknown key is refused");
            T.Check(!Perch.Platform.HotkeyParser.TryParse("Hyper+F", out _, out _), "an unknown modifier is refused");
            T.Check(!Perch.Platform.HotkeyParser.TryParse("", out _, out _), "empty text is refused");
            return Task.CompletedTask;
        });
    }
}
