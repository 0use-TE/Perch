using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Services.Ai;

namespace Perch.Views;

// A table from an AI answer, drawn as a grid with thin lines. Column widths follow how much text each column holds, and long
// cells wrap, so a wide table still fits the chat. While an answer is being written the table grows; when only the text of
// cells changed the existing cells are updated in place instead of rebuilt.
public sealed class TableView : Border
{
    public static readonly StyledProperty<MarkdownTable?> TableProperty = AvaloniaProperty.Register<TableView, MarkdownTable?>(nameof(Table));

    private static readonly IBrush Line = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x34));
    private static readonly IBrush HeaderFill = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC));

    private readonly Grid grid = new();
    private readonly List<MdText> cells = [];
    private int columns, rows;

    static TableView() => TableProperty.Changed.AddClassHandler<TableView>((view, _) => view.Rebuild());

    public TableView()
    {
        BorderBrush = Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        ClipToBounds = true;
        Margin = new Thickness(0, 5);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Child = grid;
    }

    public MarkdownTable? Table { get => GetValue(TableProperty); set => SetValue(TableProperty, value); }

    private static void SetText(MdText cell, string markdown)
    {
        cell.Tag = markdown;
        cell.Runs = Markdown.Inline(markdown);
    }

    private void Rebuild()
    {
        var table = Table;
        if (table == null || table.Header.Count == 0) { grid.Children.Clear(); cells.Clear(); columns = rows = 0; return; }
        var all = new List<IReadOnlyList<string>> { table.Header };
        all.AddRange(table.Rows);
        if (columns == table.Header.Count && rows == all.Count)
        {
            for (var r = 0; r < all.Count; r++)
                for (var c = 0; c < columns; c++)
                    if (cells[r * columns + c].Tag as string != all[r][c]) SetText(cells[r * columns + c], all[r][c]);
            return;
        }

        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        cells.Clear();
        columns = table.Header.Count; rows = all.Count;
        for (var c = 0; c < columns; c++)
        {
            var longest = all.Max(row => row[c].Length);
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Clamp(longest, 6, 40), GridUnitType.Star)) { MinWidth = 48 });
        }
        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var text = new MdText
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    LineHeight = 18,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = Ink,
                    TextAlignment = table.Align[c] switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
                };
                SetText(text, all[r][c]);
                var cell = new Border
                {
                    Child = text,
                    Padding = new Thickness(10, 6),
                    Background = r == 0 ? HeaderFill : null,
                    BorderBrush = Line,
                    BorderThickness = new Thickness(c > 0 ? 1 : 0, r > 0 ? 1 : 0, 0, 0),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                cells.Add(text);
            }
        }
    }
}
