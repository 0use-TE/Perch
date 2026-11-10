using Perch.Services.Ai;
using Perch.Services.Ai.Agent;

namespace Perch.Tests;

internal static class MarkdownTests
{
    // "B:bold| and |I:it" — one entry per run, flags first (B bold, I italic, S struck, C code, L link).
    private static string Dump(IEnumerable<MdRun> runs) => string.Join("|", runs.Select(r =>
        (r.Bold ? "B" : "") + (r.Italic ? "I" : "") + (r.Strike ? "S" : "") + (r.Code ? "C" : "") + (r.Link != null ? "L" : "") + ":" + r.Text));

    public static async Task Run()
    {
        await T.Group("Markdown blocks", () =>
    {
        var blocks = Markdown.Parse("# Title\nIntro **b**\n\n1. one\n2. two\n   more\n- a\n  - b\n\n> q1\n> q2\n\n---\n\n```python\nprint(1)\n\nprint(2)\n```\n| a | b |\n|:--|--:|\n| 1 | 2 |\nTail");
        T.Eq(BlockKind.Heading, blocks[0].Kind, "heading kind");
        T.Eq("Title", blocks[0].Text, "heading text");
        T.Eq(1, blocks[0].Level, "heading level");
        T.Eq("Intro **b**", blocks[1].Text, "paragraph keeps its marks for the inline pass");
        T.Eq(("2.", "two\nmore"), (blocks[3].Info, blocks[3].Text), "ordered list item with continuation line");
        T.Eq(("•", 0), (blocks[4].Info, blocks[4].Level), "bullet item");
        T.Eq(("b", 1), (blocks[5].Text, blocks[5].Level), "nested bullet is indented one level");
        T.Eq(("q1\nq2", BlockKind.Quote), (blocks[6].Text, blocks[6].Kind), "quote lines merge into one block");
        T.Eq(BlockKind.Rule, blocks[7].Kind, "horizontal rule");
        T.Eq(("python", "print(1)\n\nprint(2)"), (blocks[8].Info, blocks[8].Text), "fenced code keeps its language and blank lines");
        T.Eq(BlockKind.Table, blocks[9].Kind, "table after code");
        T.Eq("0,2", string.Join(",", blocks[9].Table!.Align), "table alignments from the separator row");
        T.Eq(("Tail", BlockKind.Text), (blocks[10].Text, blocks[10].Kind), "text right after a table is its own paragraph");

        T.Eq(BlockKind.Text, Markdown.Parse("#1 not a heading")[0].Kind, "#1 is not a heading");
        T.Eq(BlockKind.Text, Markdown.Parse("#include <x>")[0].Kind, "#include is not a heading");
        T.Eq(BlockKind.Heading, Markdown.Parse("###### six")[0].Kind, "six hashes is a heading");
        T.Eq(BlockKind.Text, Markdown.Parse("####### seven")[0].Kind, "seven hashes is not");
        T.Eq("trailing", Markdown.Parse("## trailing ##")[0].Text, "closing hashes are dropped");

        var two = Markdown.Parse("line one\nline two\n\nnext");
        T.Eq(2, two.Count, "blank line splits paragraphs");
        T.Eq("line one\nline two", two[0].Text, "lines of one paragraph stay together");
        T.Eq(2, Markdown.Parse("a\r\n\r\nb").Count, "CRLF line endings");
        T.Eq(0, Markdown.Parse("").Count, "empty text has no blocks");
        T.Eq(BlockKind.Rule, Markdown.Parse("***")[0].Kind, "*** is a rule");
        T.Eq(BlockKind.ListItem, Markdown.Parse("* star bullet")[0].Kind, "'* ' starts a list item");
        T.Eq(BlockKind.Text, Markdown.Parse("**bold** line")[0].Kind, "'**' is not a list marker");
        T.Eq("10.", Markdown.Parse("10) ten")[0].Info, "'10)' counts as an ordered marker");

        var open = Markdown.Parse("text\n```js\nlet a = 1;");
        T.Eq(BlockKind.Code, open[1].Kind, "an unfinished fence is code while the answer is still coming");
        T.Eq("let a = 1;", open[1].Text, "unfinished code text");

        var table = Markdown.Parse("a | b\n--- | ---\n1 | 2\n3 | 4 | 5\n6 |")[0].Table!;
        T.Eq((2, 3), (table.Header.Count, table.Rows.Count), "table without outer bars; short and long rows are fitted");
        T.Eq("4", table.Rows[1][1], "extra cells are dropped");
        T.Eq("", table.Rows[2][1], "missing cells are empty");
        T.Eq("x | y", Markdown.Parse("| a | b |\n|---|---|\n| x \\| y | z |")[0].Table!.Rows[0][0], "escaped bar stays in the cell");
        T.Eq(BlockKind.Text, Markdown.Parse("| a | b |\n|---|")[0].Kind, "a header whose separator is incomplete is plain text for now");
        T.Eq(BlockKind.Text, Markdown.Parse("| a | b |")[0].Kind, "a lone row is not a table");
        T.Eq(BlockKind.Table, Markdown.Parse("Intro:\n| a | b |\n|---|---|\n| 1 | 2 |")[1].Kind, "table directly after a paragraph line");
        T.Eq("**x**", Markdown.Parse("| a |\n|---|\n| **x** |")[0].Table!.Rows[0][0], "table cells keep their marks");

        var same = Markdown.Parse("| a |\n|---|\n| 1 |")[0].Table!;
        T.Check(same.SameAs(Markdown.Parse("| a |\n|---|\n| 1 |")[0].Table), "equal tables are the same");
        T.Check(!same.SameAs(Markdown.Parse("| a |\n|---|\n| 2 |")[0].Table), "a changed cell is a different table");
        T.Check(!same.SameAs(null), "null is a different table");
        return Task.CompletedTask;
    });
        await T.Group("Markdown inline", () =>
    {
        T.Eq("B:bold|: and |I:italic|: and |S:gone|: and |C:code", Dump(Markdown.Inline("**bold** and *italic* and ~~gone~~ and `code`")), "basic marks");
        T.Eq("B:x|:  |B:y", Dump(Markdown.Inline("__x__  __y__")), "underscore bold");
        T.Eq("I:x", Dump(Markdown.Inline("_x_")), "underscore italic");
        T.Eq(":snake_case_name and a * b", Dump(Markdown.Inline("snake_case_name and a * b")), "snake_case and spaced stars are literal");
        T.Eq("BI:both", Dump(Markdown.Inline("***both***")), "bold italic together");
        T.Eq(":a **b", Dump(Markdown.Inline("a **b")), "an unmatched mark stays literal in finished text");
        T.Eq(":a |B:b", Dump(Markdown.Inline("a **b", open: true)), "while writing, an open mark styles the rest");
        T.Eq("C:code", Dump(Markdown.Inline("`code", open: true)), "while writing, an open code mark is code to the end");
        T.Eq(":a *star* b", Dump(Markdown.Inline("a " + (char)92 + "*star" + (char)92 + "* b")), "backslash escapes");
        T.Eq("C:**not bold**", Dump(Markdown.Inline("`**not bold**`")), "marks inside code are left alone");
        T.Eq(":中文|B:加粗|:和|I:斜体|:测试", Dump(Markdown.Inline("中文**加粗**和*斜体*测试")), "Chinese text with marks");

        T.Eq(":see |L:site|: or |L:https://foo.bar/x|:). done", Dump(Markdown.Inline("see [site](https://example.com/a) or https://foo.bar/x). done")), "link, bare address, trailing punctuation");
        T.Eq("https://example.com/a", Markdown.Inline("[site](https://example.com/a)")[0].Link, "link target");
        T.Eq(":[x](javascript:alert(1)) [f](file:///c:/x)", Dump(Markdown.Inline("[x](javascript:alert(1)) [f](file:///c:/x)")), "only web addresses become links");
        T.Eq<string?>(null, Markdown.Web("javascript:alert(1)"), "javascript: is not a web address");
        T.Eq<string?>(null, Markdown.Web("file:///c:/windows"), "file: is not a web address");
        T.Eq<string?>(null, Markdown.Web("ftp://x.y/z"), "ftp: is not a web address");
        T.Eq("HTTPS://X.Y/z", Markdown.Web("HTTPS://X.Y/z"), "scheme case does not matter");
        T.Eq(":a http://", Dump(Markdown.Inline("a http://")), "a bare scheme is not a link");
        T.Eq(0, Markdown.Inline("").Count, "empty line has no runs");
        return Task.CompletedTask;
    });
    }
}

internal static class DsmlTests
{
    private static readonly string Bar = "\uFF5C";
    private static string Open => "<" + Bar + Bar + "DSML" + Bar + Bar;
    private static string Close => "</" + Bar + Bar + "DSML" + Bar + Bar;

    private static string Sample() =>
        "Let me check.\n\n" + Open + " calls>\n" + Open + " invoke name=\"run_powershell\">\n"
        + Open + " parameter name=\"script\" string=\"true\">$p='x'\n\"a < b\" + $q" + Close + " parameter>\n"
        + Open + " parameter name=\"timeout_seconds\" string=\"false\">45" + Close + " parameter>\n"
        + Close + " invoke>\n" + Open + " invoke name=\"open_app\">" + Open + " parameter name=\"target\" string=\"true\">cmd" + Close + " parameter>" + Close + " invoke>\n" + Close + " calls>";

    public static Task Run() => T.Group("DeepSeek tool calls written as text", () =>
    {
        foreach (var chunk in new[] { 1, 2, 3, 7, 5000 })
        {
            var filter = new DsmlFilter();
            var shown = new System.Text.StringBuilder();
            var text = Sample();
            for (var i = 0; i < text.Length; i += chunk) shown.Append(filter.Push(text.Substring(i, Math.Min(chunk, text.Length - i))));
            shown.Append(filter.Finish());
            var calls = filter.Calls();
            T.Eq("Let me check.\n\n", shown.ToString(), $"markup is hidden (pieces of {chunk})");
            T.Eq(2, calls.Count, $"both calls found (pieces of {chunk})");
            T.Eq("run_powershell", calls[0].Name, $"first call name (pieces of {chunk})");
            using var args = System.Text.Json.JsonDocument.Parse(calls[0].Arguments);
            T.Eq("$p='x'\n\"a < b\" + $q", args.RootElement.GetProperty("script").GetString(), $"script survives with quotes and < (pieces of {chunk})");
            T.Eq(45, args.RootElement.GetProperty("timeout_seconds").GetInt32(), $"string=\"false\" gives a number (pieces of {chunk})");
            T.Eq("open_app", calls[1].Name, $"second call name (pieces of {chunk})");
        }

        var plain = new DsmlFilter();
        var kept = new System.Text.StringBuilder();
        foreach (var piece in new[] { "if a ", "<", " b and x<|y and ", "<b>bold</b> end <" }) kept.Append(plain.Push(piece));
        kept.Append(plain.Finish());
        T.Eq("if a < b and x<|y and <b>bold</b> end <", kept.ToString(), "ordinary text with < passes through unchanged");
        T.Check(!plain.Found && plain.Calls().Count == 0, "no markup, no calls");

        var lone = new DsmlFilter();
        T.Eq("x", lone.Push("x<"), "a '<' that might start markup is held back");
        T.Eq("<", lone.Finish(), "and released when the reply ends");
        return Task.CompletedTask;
    });
}
