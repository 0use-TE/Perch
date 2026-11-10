using System.Text;

namespace Perch.Services.Ai;

public enum BlockKind { Text, Heading, Quote, ListItem, Code, Table, Rule }

// A table from the answer: the header cells, the body rows (each as wide as the header) and each column's alignment
// (0 left, 1 centre, 2 right). Cells keep their markdown; the view draws the marks.
public sealed record MarkdownTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<int> Align)
{
    public bool SameAs(MarkdownTable? other)
    {
        if (other == null || Header.Count != other.Header.Count || Rows.Count != other.Rows.Count) return false;
        if (!Header.SequenceEqual(other.Header) || !Align.SequenceEqual(other.Align)) return false;
        for (var i = 0; i < Rows.Count; i++) if (!Rows[i].SequenceEqual(other.Rows[i])) return false;
        return true;
    }
}

// One block of an answer. Text is the raw markdown of the block's own text (for code: the code); Info is the language of
// a code block or the marker of a list item ("•", "3."); Level is the heading level (1-6) or how deeply a list item is nested.
public sealed record MarkdownBlock(BlockKind Kind, string Text, MarkdownTable? Table = null, string Info = "", int Level = 0);

// A piece of a line with one look: plain, bold, italic, struck through, code, or a link to a web address.
public sealed record MdRun(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null);

// Splits an answer into blocks (paragraphs, headings, quotes, list items, fenced code, tables, rules) and a line of text into
// styled runs. It is not a full markdown engine: it covers what chat answers use, and tolerates text that is still being
// written (an unfinished fence is code; a table without its separator row yet is plain text until the separator arrives; a
// bold or code mark still waiting for its end applies to the rest of the text). Blocks are separate so that, while an answer
// grows, only the last one changes and the rest of the page is left alone.
public static class Markdown
{
    public static List<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var quote = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0) blocks.Add(new MarkdownBlock(BlockKind.Text, string.Join('\n', paragraph)));
            paragraph.Clear();
        }
        void FlushQuote()
        {
            if (quote.Count > 0) blocks.Add(new MarkdownBlock(BlockKind.Quote, string.Join('\n', quote)));
            quote.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(); FlushQuote();
                var language = trimmed.TrimStart('`').Trim();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                }
                var body = code.ToString().TrimEnd('\n', '\r');
                if (body.Length > 0 || language.Length > 0) blocks.Add(new MarkdownBlock(BlockKind.Code, body, null, language.Split(' ')[0]));
                continue;
            }
            if (line.Contains('|') && i + 1 < lines.Length && Cells(line) is { Count: > 0 } header && Separator(lines[i + 1], header.Count) is { } align)
            {
                FlushParagraph(); FlushQuote();
                var rows = new List<IReadOnlyList<string>>();
                for (i += 2; i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0; i++)
                {
                    var cells = Cells(lines[i]);
                    while (cells.Count < header.Count) cells.Add("");
                    if (cells.Count > header.Count) cells.RemoveRange(header.Count, cells.Count - header.Count);
                    rows.Add(cells);
                }
                i--; // the loop's own increment moves past the last row
                blocks.Add(new MarkdownBlock(BlockKind.Table, "", new MarkdownTable(header, rows, align)));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); FlushQuote(); continue; }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var inner = trimmed[1..];
                quote.Add(inner.StartsWith(' ') ? inner[1..] : inner);
                continue;
            }
            FlushQuote();
            var hashes = trimmed.Length - trimmed.TrimStart('#').Length;
            if (hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, trimmed[hashes..].Trim().TrimEnd('#').TrimEnd(), null, "", hashes)); // "#1" or "#include" is not a heading
                continue;
            }
            if (IsRule(trimmed)) { FlushParagraph(); blocks.Add(new MarkdownBlock(BlockKind.Rule, "")); continue; }
            if (ListMarker(line) is { } item)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.ListItem, item.Text, null, item.Marker, item.Level));
                continue;
            }
            // A line indented under a list item continues that item.
            if (paragraph.Count == 0 && blocks.Count > 0 && blocks[^1].Kind == BlockKind.ListItem && line.Length - trimmed.Length >= 2)
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + "\n" + trimmed };
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph(); FlushQuote();
        return blocks;
    }

    private static bool IsRule(string trimmed)
    {
        var compact = trimmed.Replace(" ", "");
        return compact.Length >= 3 && (compact.All(c => c == '-') || compact.All(c => c == '*') || compact.All(c => c == '_'));
    }

    private static (string Marker, string Text, int Level)? ListMarker(string line)
    {
        var trimmed = line.TrimStart();
        var level = Math.Min((line.Length - trimmed.Length) / 2, 3);
        if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' or '•' && trimmed[1] == ' ') return ("•", trimmed[2..].TrimStart(), level);
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits is > 0 and <= 3 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
            return (trimmed[..digits] + ".", trimmed[(digits + 2)..].TrimStart(), level);
        return null;
    }

    // The cells of one table row: leading and trailing bars are optional and "\|" is a literal bar.
    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal)) text = text[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|') { cell.Append('|'); i++; }
            else if (text[i] == '|') { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(text[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // The row of dashes under a header ("| --- | :---: | ---: |"): the column alignments, or null if the line is not one.
    private static List<int>? Separator(string line, int columns)
    {
        if (!line.Contains('-')) return null;
        var cells = Cells(line);
        if (cells.Count != columns) return null;
        var align = new List<int>();
        foreach (var cell in cells)
        {
            var core = cell.Trim();
            var left = core.StartsWith(':');
            var right = core.EndsWith(':');
            core = core.Trim(':');
            if (core.Length == 0 || core.Any(c => c != '-')) return null;
            align.Add(left && right ? 1 : right ? 2 : 0);
        }
        return align;
    }

    // ---- inline marks

    // Turns a line of markdown into styled runs: **bold**, *italic* / _italic_, ~~struck~~, `code`, [text](https://link) and
    // bare web addresses. Marks without a partner stay as typed, except that while the answer is still being written (open)
    // a mark waiting for its end styles the rest of the text, so the page does not flicker between raw marks and styled text.
    public static List<MdRun> Inline(string text, bool open = false)
    {
        var runs = new List<MdRun>();
        var buffer = new StringBuilder();
        bool bold = false, italic = false, strike = false;
        var italicMark = '*';
        void Flush()
        {
            if (buffer.Length > 0) runs.Add(new MdRun(buffer.ToString(), bold, italic, false, strike));
            buffer.Clear();
        }

        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\\' && next != '\0' && "\\`*_{}[]()#+-.!|~>".Contains(next)) { buffer.Append(next); i += 2; continue; }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1 || (end < 0 && open && i + 1 < text.Length))
                {
                    Flush();
                    var stop = end < 0 ? text.Length : end;
                    runs.Add(new MdRun(text[(i + 1)..stop], Code: true));
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var stop = text.IndexOf(')', close + 2);
                    if (stop > close + 2 && Web(text[(close + 2)..stop].Trim()) is { } url)
                    {
                        Flush();
                        foreach (var part in Inline(text[(i + 1)..close])) runs.Add(part with { Link = url });
                        i = stop + 1;
                        continue;
                    }
                }
            }
            else if (c == 'h' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (text.AsSpan(i).StartsWith("http://") || text.AsSpan(i).StartsWith("https://")))
            {
                var stop = i;
                while (stop < text.Length && !char.IsWhiteSpace(text[stop]) && text[stop] is not ('<' or '>' or '"')) stop++;
                while (stop > i + 8 && ".,;:!?)]}'\"".Contains(text[stop - 1])) stop--;
                if (stop > i + 8)
                {
                    Flush();
                    runs.Add(new MdRun(text[i..stop], bold, italic, false, strike, text[i..stop]));
                    i = stop;
                    continue;
                }
            }
            else if (c is '*' or '_' && next == c)
            {
                var mark = new string(c, 2);
                if (bold) { Flush(); bold = false; i += 2; continue; }
                if (i + 2 < text.Length && !char.IsWhiteSpace(text[i + 2]) && (open || HasClose(text, i + 2, mark)))
                {
                    Flush(); bold = true; i += 2; continue;
                }
                buffer.Append(mark); i += 2; continue;
            }
            else if (c == '~' && next == '~')
            {
                if (strike) { Flush(); strike = false; i += 2; continue; }
                if (i + 2 < text.Length && !char.IsWhiteSpace(text[i + 2]) && (open || HasClose(text, i + 2, "~~")))
                {
                    Flush(); strike = true; i += 2; continue;
                }
                buffer.Append("~~"); i += 2; continue;
            }
            else if (c is '*' or '_')
            {
                if (italic && c == italicMark && i > 0 && !char.IsWhiteSpace(text[i - 1])) { Flush(); italic = false; i++; continue; }
                var wordStart = i == 0 || !char.IsLetterOrDigit(text[i - 1]); // snake_case and 2*3 are not emphasis
                if (!italic && next != '\0' && !char.IsWhiteSpace(next) && (c == '*' || wordStart) && (open || HasClose(text, i + 1, c.ToString())))
                {
                    Flush(); italic = true; italicMark = c; i++; continue;
                }
            }
            buffer.Append(c);
            i++;
        }
        Flush();
        return runs;
    }

    // Only web addresses become links: an answer must never be able to open a file or a program by a click.
    public static string? Web(string url) =>
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;

    // Whether the mark appears again later, after a character that is not a space (so it can close what is open).
    private static bool HasClose(string text, int from, string mark)
    {
        for (var j = text.IndexOf(mark, from, StringComparison.Ordinal); j >= 0; j = text.IndexOf(mark, j + 1, StringComparison.Ordinal))
            if (j > from && !char.IsWhiteSpace(text[j - 1]) && (mark.Length > 1 || j + 1 >= text.Length || text[j + 1] != mark[0])) return true;
        return false;
    }
}
