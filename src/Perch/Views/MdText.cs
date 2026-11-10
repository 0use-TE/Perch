using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Services.Ai;

namespace Perch.Views;

// Selectable text drawn from styled runs (see Markdown.Inline): bold, italic, struck-through, code and links. Clicking a
// link opens it in the browser; only web addresses ever become links.
public sealed class MdText : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<MdRun>?> RunsProperty = AvaloniaProperty.Register<MdText, IReadOnlyList<MdRun>?>(nameof(Runs));

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");
    private static readonly IBrush LinkInk = new SolidColorBrush(Color.FromRgb(0x7A, 0xB8, 0xFF));
    private static readonly IBrush CodeFill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    // Where each link sits in the text, so a click can be matched to it.
    private readonly List<(int Start, int End, string Url)> links = [];

    static MdText() => RunsProperty.Changed.AddClassHandler<MdText>((text, _) => text.Rebuild());

    public IReadOnlyList<MdRun>? Runs { get => GetValue(RunsProperty); set => SetValue(RunsProperty, value); }

    private void Rebuild()
    {
        Inlines?.Clear();
        links.Clear();
        if (Runs is not { } runs) return;
        Inlines ??= [];
        var position = 0;
        foreach (var part in runs)
        {
            var run = new Avalonia.Controls.Documents.Run(part.Text);
            if (part.Bold) run.FontWeight = FontWeight.Bold;
            if (part.Italic) run.FontStyle = FontStyle.Italic;
            if (part.Code) { run.FontFamily = Mono; run.Background = CodeFill; }
            if (part.Strike) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
            if (part.Link != null)
            {
                run.Foreground = LinkInk;
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                links.Add((position, position + part.Text.Length, part.Link));
            }
            Inlines.Add(run);
            position += part.Text.Length;
        }
    }

    private string? LinkAt(PointerEventArgs e)
    {
        if (links.Count == 0) return null;
        var point = e.GetPosition(this) - new Point(Padding.Left, Padding.Top);
        var hit = TextLayout.HitTestPoint(point);
        if (!hit.IsInside) return null;
        foreach (var (start, end, url) in links) if (hit.TextPosition >= start && hit.TextPosition < end) return url;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e) != null ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // A click opens the link, a drag selects text (and must not open anything).
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
        if (LinkAt(e) is { } url && Markdown.Web(url) is { } safe) ShellActions.Open(safe);
    }
}
