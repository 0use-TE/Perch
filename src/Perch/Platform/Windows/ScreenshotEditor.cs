using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Perch.Services;
using AvaloniaBrushes = Avalonia.Media.Brushes;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaPoint = Avalonia.Point;
using AvaloniaRect = Avalonia.Rect;
using AvaloniaSize = Avalonia.Size;
using Bitmap = System.Drawing.Bitmap;
using Brush = Avalonia.Media.Brush;
using GdiBrush = System.Drawing.SolidBrush;
using GdiColor = System.Drawing.Color;
using GdiFont = System.Drawing.Font;
using GdiFontStyle = System.Drawing.FontStyle;
using GdiPen = System.Drawing.Pen;
using Pen = Avalonia.Media.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Perch.Platform.Windows;

internal enum CaptureOutcome { Cancelled, Copied, Saved }

internal static class ScreenshotEditor
{
    internal enum DrawTool { Select, Rect, Ellipse, Arrow, Line, Brush, Mosaic, Text, Icon }

    internal sealed class Annotation
    {
        public DrawTool Tool { get; init; }
        public AvaloniaColor Color { get; init; }
        public double Thickness { get; init; }
        public AvaloniaPoint Start { get; set; }
        public AvaloniaPoint End { get; set; }
        public List<AvaloniaPoint>? Points { get; init; }
        public string? Text { get; set; }
        public bool IsBadge { get; init; }
    }

    private static readonly AvaloniaColor[] Palette =
    [
        AvaloniaColor.Parse("#FF4D4F"),
        AvaloniaColor.Parse("#FFAE54"),
        AvaloniaColor.Parse("#FAAD14"),
        AvaloniaColor.Parse("#52C41A"),
        AvaloniaColor.Parse("#1890FF"),
        AvaloniaColor.Parse("#722ED1"),
        AvaloniaColor.Parse("#FFFFFF"),
        AvaloniaColor.Parse("#141414"),
    ];

    private static readonly (string Label, bool IsBadge)[] IconItems =
    [
        ("1", true), ("2", true), ("3", true), ("4", true), ("5", true),
        ("6", true), ("7", true), ("8", true), ("9", true),
        ("✓", false), ("✗", false), ("★", false), ("❤", false), ("⚠", false),
        ("❗", false), ("❓", false), ("👍", false), ("🔥", false), ("💡", false),
        ("📌", false), ("🔒", false), ("👉", false), ("💬", false),
    ];

    public static async Task<(byte[] Png, int Width, int Height, CaptureOutcome Outcome)?> RunAsync(
        Bitmap gdi, WriteableBitmap full, PixelRect bounds, bool startFullscreen, bool allowSave = true)
    {
        var done = new TaskCompletionSource<(byte[] Png, int Width, int Height, CaptureOutcome Outcome)?>();
        var overlay = new EditorOverlay(gdi, full, bounds, startFullscreen, allowSave, done);
        overlay.Show();
        overlay.Activate();
        return await done.Task;
    }

    private static bool Zh => Loc.Culture.TwoLetterISOLanguageName == "zh";
    private static string T(string zh, string en) => Zh ? zh : en;

    private sealed class EditorOverlay : Window
    {
        private readonly Bitmap gdi;
        private readonly WriteableBitmap full;
        private readonly PixelRect bounds;
        private readonly bool allowSave;
        private readonly TaskCompletionSource<(byte[] Png, int Width, int Height, CaptureOutcome Outcome)?> done;
        private readonly EditorCanvas canvas;
        private readonly Canvas root;
        private readonly Border toolbarWrap;
        private readonly StackPanel subBar;
        private readonly StackPanel iconStrip;
        private readonly Dictionary<DrawTool, Button> toolButtons = new();
        private readonly List<Border> colorDots = [];
        private readonly List<Border> sizeDots = [];
        private readonly Dictionary<(int, int), IBrush> mosaicCache = new();
        private readonly List<Annotation> annotations = [];

        private AvaloniaRect selection;
        private bool hasSelection;
        private DrawTool activeTool = DrawTool.Select;
        private AvaloniaColor activeColor = Palette[0];
        private int sizeIndex = 1; // 0=S, 1=M, 2=L
        private (string Label, bool IsBadge) activeIcon = IconItems[0];

        // Drag state
        private enum DragMode { None, Selecting, MovingSelection, Resizing, Drawing, MovingAnnotation }
        private DragMode dragMode;
        private int resizeHandle = -1; // 0..7: NW,N,NE,E,SE,S,SW,W
        private AvaloniaPoint dragOrigin;
        private AvaloniaRect dragStartRect;
        private Annotation? activeAnnotation;
        private AvaloniaPoint annotationOriginStart, annotationOriginEnd;
        private TextBox? activeTextBox;
        private AvaloniaPoint pendingTextPos;
        private DateTime lastClickTime;
        private bool finished;
        private bool toolbarDragging;
        private AvaloniaPoint toolbarDragStart, toolbarStartPos;

        private double StrokeWidth => sizeIndex switch { 0 => 2, 1 => 4, _ => 7 };
        private double IconSize => sizeIndex switch { 0 => 22, 1 => 30, _ => 42 };
        private double FontSizePx => sizeIndex switch { 0 => 15, 1 => 20, _ => 28 };

        public EditorOverlay(
            Bitmap gdi, WriteableBitmap full, PixelRect bounds,
            bool startFullscreen, bool allowSave,
            TaskCompletionSource<(byte[] Png, int Width, int Height, CaptureOutcome Outcome)?> done)
        {
            this.gdi = gdi;
            this.full = full;
            this.bounds = bounds;
            this.allowSave = allowSave;
            this.done = done;

            WindowDecorations = WindowDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = new Cursor(StandardCursorType.Cross);
            Position = new PixelPoint(bounds.X, bounds.Y);

            canvas = new EditorCanvas(full, annotations, mosaicCache, () => activeAnnotation);
            (toolbarWrap, subBar, iconStrip) = BuildToolbar();
            toolbarWrap.IsVisible = false;

            root = new Canvas { Children = { canvas, toolbarWrap } };
            Content = root;

            Opened += (_, _) =>
            {
                var scale = RenderScaling;
                Width = bounds.Width / scale;
                Height = bounds.Height / scale;
                canvas.Width = Width;
                canvas.Height = Height;
                Position = new PixelPoint(bounds.X, bounds.Y);
                if (startFullscreen)
                {
                    selection = new AvaloniaRect(0, 0, Width, Height);
                    hasSelection = true;
                    activeTool = DrawTool.Arrow;
                    UpdateToolHighlight();
                    canvas.Update(selection, scale, showHandles: true);
                    PositionToolbar();
                }
            };

            canvas.PointerPressed += OnCanvasPointerPressed;
            canvas.PointerMoved += OnCanvasPointerMoved;
            canvas.PointerReleased += OnCanvasPointerReleased;
            KeyDown += OnOverlayKeyDown;
            Closed += (_, _) => { if (!finished) done.TrySetResult(null); };
        }

        private (Border Wrap, StackPanel SubBar, StackPanel IconStrip) BuildToolbar()
        {
            var mainRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

            void AddTool(DrawTool tool, string glyph, string tip)
            {
                var btn = MakeBarButton(glyph, tip, () =>
                {
                    CommitTextBox();
                    activeTool = tool;
                    UpdateToolHighlight();
                });
                toolButtons[tool] = btn;
                mainRow.Children.Add(btn);
            }

            AddTool(DrawTool.Select, "\uE7C8", T("选择/移动", "Select / Move"));
            AddTool(DrawTool.Rect, "\uE739", T("矩形", "Rectangle"));
            AddTool(DrawTool.Ellipse, "\uEA3A", T("椭圆", "Ellipse"));
            AddTool(DrawTool.Arrow, "\uE72A", T("箭头", "Arrow"));
            AddTool(DrawTool.Line, "\uE94E", T("直线", "Line"));
            AddTool(DrawTool.Brush, "\uE76D", T("画笔", "Brush"));
            AddTool(DrawTool.Mosaic, "\uE80A", T("马赛克", "Mosaic"));
            AddTool(DrawTool.Text, "\uE8D2", T("文字", "Text"));
            AddTool(DrawTool.Icon, "\uE734", T("图标/序号", "Icons & Badges"));

            mainRow.Children.Add(MakeDivider());
            mainRow.Children.Add(MakeBarButton("\uE7A7", T("撤销 (Ctrl+Z)", "Undo (Ctrl+Z)"), Undo));
            mainRow.Children.Add(MakeBarButton("\uE740", T("全屏 (Ctrl+A)", "Fullscreen (Ctrl+A)"), SelectAll));

            if (allowSave)
            {
                mainRow.Children.Add(MakeBarButton("\uE896", T("下载到图片文件夹", "Download to Pictures"), QuickDownload));
                mainRow.Children.Add(MakeBarButton("\uE74E", T("另存为 (Ctrl+S)", "Save As (Ctrl+S)"), async () => await SaveAsync()));
            }

            mainRow.Children.Add(MakeBarButton("\uE711", T("取消 (Esc)", "Cancel (Esc)"), () => Finish(null)));

            var okBtn = MakeBarButton("\uE73E", T("完成并复制 (Enter)", "Done & Copy (Enter)"), () => CompleteCopy());
            okBtn.Background = Brush.Parse("#2B52C41A");
            if (okBtn.Content is TextBlock tb) tb.Foreground = Brush.Parse("#73D13D");
            mainRow.Children.Add(okBtn);

            // Sub-bar: size dots + color palette
            var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 4, 4, 2) };
            for (var i = 0; i < 3; i++)
            {
                var idx = i;
                var dotSize = i switch { 0 => 6.0, 1 => 10.0, _ => 14.0 };
                var dot = new Border
                {
                    Width = dotSize, Height = dotSize, CornerRadius = new CornerRadius(dotSize / 2),
                    Background = AvaloniaBrushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                var holder = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(6),
                    Background = idx == sizeIndex ? Brush.Parse("#33FFAE54") : AvaloniaBrushes.Transparent,
                    BorderBrush = idx == sizeIndex ? Brush.Parse("#FFAE54") : AvaloniaBrushes.Transparent,
                    BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand),
                    Child = dot
                };
                holder.PointerPressed += (_, e) => { e.Handled = true; sizeIndex = idx; RefreshSubBar(); };
                sizeDots.Add(holder);
                sub.Children.Add(holder);
            }

            sub.Children.Add(MakeDivider());

            foreach (var col in Palette)
            {
                var c = col;
                var swatch = new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(c),
                    BorderBrush = c == activeColor ? Brush.Parse("#FFAE54") : Brush.Parse("#55FFFFFF"),
                    BorderThickness = new Thickness(c == activeColor ? 2 : 1),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                swatch.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    activeColor = c;
                    RefreshSubBar();
                    if (activeTextBox != null) activeTextBox.Foreground = new SolidColorBrush(c);
                };
                colorDots.Add(swatch);
                sub.Children.Add(swatch);
            }

            // Icon picker strip (shown when Icon tool is selected)
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(2, 4, 2, 0), IsVisible = false };
            foreach (var item in IconItems)
            {
                var it = item;
                var lbl = new TextBlock
                {
                    Text = it.Label,
                    FontSize = it.IsBadge ? 12 : 14,
                    FontWeight = it.IsBadge ? FontWeight.Bold : FontWeight.Normal,
                    Foreground = AvaloniaBrushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var chip = new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                    Background = it == activeIcon ? Brush.Parse("#44FFAE54") : Brush.Parse("#18FFFFFF"),
                    BorderBrush = it == activeIcon ? Brush.Parse("#FFAE54") : AvaloniaBrushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Child = lbl
                };
                chip.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    activeIcon = it;
                    foreach (var child in icons.Children.OfType<Border>())
                    {
                        var match = child == chip;
                        child.Background = match ? Brush.Parse("#44FFAE54") : Brush.Parse("#18FFFFFF");
                        child.BorderBrush = match ? Brush.Parse("#FFAE54") : AvaloniaBrushes.Transparent;
                    }
                };
                icons.Children.Add(chip);
            }

            var stack = new StackPanel { Spacing = 2, Children = { mainRow, sub, icons } };
            var wrap = new Border
            {
                Background = Brush.Parse("#EE141418"),
                BorderBrush = Brush.Parse("#38FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 5),
                Cursor = new Cursor(StandardCursorType.SizeAll),
                Child = stack
            };
            wrap.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(wrap).Properties.IsLeftButtonPressed) return;
                toolbarDragging = true;
                toolbarDragStart = e.GetPosition(root);
                toolbarStartPos = new AvaloniaPoint(Canvas.GetLeft(wrap), Canvas.GetTop(wrap));
                e.Pointer.Capture(wrap);
                e.Handled = true;
            };
            wrap.PointerMoved += (_, e) =>
            {
                if (!toolbarDragging) return;
                var cur = e.GetPosition(root);
                var left = Math.Clamp(toolbarStartPos.X + (cur.X - toolbarDragStart.X), 8, Math.Max(8, Width - wrap.Bounds.Width - 8));
                var top = Math.Clamp(toolbarStartPos.Y + (cur.Y - toolbarDragStart.Y), 8, Math.Max(8, Height - wrap.Bounds.Height - 8));
                Canvas.SetLeft(wrap, left);
                Canvas.SetTop(wrap, top);
                e.Handled = true;
            };
            wrap.PointerReleased += (_, e) =>
            {
                if (!toolbarDragging) return;
                toolbarDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
            return (wrap, sub, icons);
        }

        private static Button MakeBarButton(string glyph, string tip, Action onClick)
        {
            var btn = new Button
            {
                Width = 30, Height = 28, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = AvaloniaBrushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new Avalonia.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 14,
                    Foreground = Brush.Parse("#E6E6EA"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTip.SetTip(btn, tip);
            btn.Click += (_, e) => { e.Handled = true; onClick(); };
            return btn;
        }

        private static Border MakeDivider() => new()
        {
            Width = 1, Height = 18, Margin = new Thickness(4, 0),
            Background = Brush.Parse("#28FFFFFF"), VerticalAlignment = VerticalAlignment.Center
        };

        private void UpdateToolHighlight()
        {
            foreach (var (tool, btn) in toolButtons)
            {
                var on = tool == activeTool;
                btn.Background = on ? Brush.Parse("#38FFAE54") : AvaloniaBrushes.Transparent;
                if (btn.Content is TextBlock tb)
                    tb.Foreground = on ? Brush.Parse("#FFAE54") : Brush.Parse("#E6E6EA");
            }
            subBar.IsVisible = activeTool != DrawTool.Select;
            iconStrip.IsVisible = activeTool == DrawTool.Icon;
            Cursor = activeTool == DrawTool.Select
                ? new Cursor(StandardCursorType.Arrow)
                : new Cursor(StandardCursorType.Cross);
            if (hasSelection) PositionToolbar();
        }

        private void RefreshSubBar()
        {
            for (var i = 0; i < sizeDots.Count; i++)
            {
                var on = i == sizeIndex;
                sizeDots[i].Background = on ? Brush.Parse("#33FFAE54") : AvaloniaBrushes.Transparent;
                sizeDots[i].BorderBrush = on ? Brush.Parse("#FFAE54") : AvaloniaBrushes.Transparent;
            }
            for (var i = 0; i < colorDots.Count; i++)
            {
                var on = Palette[i] == activeColor;
                colorDots[i].BorderBrush = on ? Brush.Parse("#FFAE54") : Brush.Parse("#55FFFFFF");
                colorDots[i].BorderThickness = new Thickness(on ? 2 : 1);
            }
        }

        private void PositionToolbar()
        {
            toolbarWrap.IsVisible = true;
            toolbarWrap.Measure(new AvaloniaSize(double.PositiveInfinity, double.PositiveInfinity));
            var tw = toolbarWrap.DesiredSize.Width;
            var th = toolbarWrap.DesiredSize.Height;
            var left = Math.Clamp(selection.Right - tw, 8, Math.Max(8, Width - tw - 8));
            if (selection.Width < tw && selection.X + tw <= Width - 8) left = Math.Max(8, selection.X);

            double top;
            if (selection.Bottom + 8 + th <= Height - 8) top = selection.Bottom + 8;
            else if (selection.Y - 8 - th >= 8) top = selection.Y - 8 - th;
            else top = Math.Max(8, selection.Bottom - th - 10);

            Canvas.SetLeft(toolbarWrap, left);
            Canvas.SetTop(toolbarWrap, top);
        }

        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var pt = e.GetPosition(canvas);
            var props = e.GetCurrentPoint(canvas).Properties;

            if (props.IsRightButtonPressed)
            {
                if (activeTextBox != null) { CancelTextBox(); return; }
                Finish(null);
                return;
            }
            if (!props.IsLeftButtonPressed) return;

            CommitTextBox();

            if (!hasSelection)
            {
                dragMode = DragMode.Selecting;
                dragOrigin = pt;
                selection = new AvaloniaRect(pt, default(AvaloniaSize));
                canvas.Update(selection, RenderScaling, showHandles: false);
                return;
            }

            // Double click inside selection in Select mode -> quick copy
            var now = DateTime.UtcNow;
            if (activeTool == DrawTool.Select && selection.Contains(pt) && (now - lastClickTime).TotalMilliseconds < 320)
            {
                CompleteCopy();
                return;
            }
            lastClickTime = now;

            // Check resize handles first (in Select mode or when clicking handles)
            var handle = HitTestHandle(pt);
            if (handle >= 0)
            {
                dragMode = DragMode.Resizing;
                resizeHandle = handle;
                dragOrigin = pt;
                dragStartRect = selection;
                toolbarWrap.IsVisible = false;
                return;
            }

            if (activeTool == DrawTool.Select)
            {
                // Check if clicking an existing Icon or Text annotation to move it
                var hitAnn = HitTestMovableAnnotation(pt);
                if (hitAnn != null)
                {
                    dragMode = DragMode.MovingAnnotation;
                    activeAnnotation = hitAnn;
                    dragOrigin = pt;
                    annotationOriginStart = hitAnn.Start;
                    annotationOriginEnd = hitAnn.End;
                    return;
                }
                if (selection.Contains(pt))
                {
                    dragMode = DragMode.MovingSelection;
                    dragOrigin = pt;
                    dragStartRect = selection;
                    toolbarWrap.IsVisible = false;
                }
                return;
            }

            if (!selection.Contains(pt)) return;

            if (activeTool == DrawTool.Text)
            {
                BeginTextBox(pt);
                return;
            }

            if (activeTool == DrawTool.Icon)
            {
                var ann = new Annotation
                {
                    Tool = DrawTool.Icon,
                    Color = activeColor,
                    Thickness = IconSize,
                    Start = pt,
                    End = pt,
                    Text = activeIcon.Label,
                    IsBadge = activeIcon.IsBadge
                };
                annotations.Add(ann);
                activeAnnotation = ann;
                dragMode = DragMode.MovingAnnotation;
                dragOrigin = pt;
                annotationOriginStart = pt;
                annotationOriginEnd = pt;
                canvas.InvalidateVisual();
                return;
            }

            // Start drawing shape / brush / mosaic
            dragMode = DragMode.Drawing;
            dragOrigin = pt;
            activeAnnotation = new Annotation
            {
                Tool = activeTool,
                Color = activeColor,
                Thickness = activeTool == DrawTool.Mosaic ? StrokeWidth * 3.5 : StrokeWidth,
                Start = pt,
                End = pt,
                Points = activeTool is DrawTool.Brush or DrawTool.Mosaic ? [pt] : null
            };
            if (activeTool == DrawTool.Mosaic) AddMosaicBlocks(pt, activeAnnotation.Thickness);
            canvas.InvalidateVisual();
        }

        private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            var pt = e.GetPosition(canvas);
            if (dragMode == DragMode.None)
            {
                if (hasSelection && HitTestHandle(pt) is >= 0 and var h)
                {
                    Cursor = h switch
                    {
                        0 or 4 => new Cursor(StandardCursorType.TopLeftCorner),
                        2 or 6 => new Cursor(StandardCursorType.TopRightCorner),
                        1 or 5 => new Cursor(StandardCursorType.SizeNorthSouth),
                        _ => new Cursor(StandardCursorType.SizeWestEast),
                    };
                }
                else
                {
                    Cursor = activeTool == DrawTool.Select
                        ? new Cursor(StandardCursorType.Arrow)
                        : new Cursor(StandardCursorType.Cross);
                }
                return;
            }

            if (dragMode == DragMode.Selecting)
            {
                selection = ClampRect(
                    Math.Min(dragOrigin.X, pt.X), Math.Min(dragOrigin.Y, pt.Y),
                    Math.Abs(pt.X - dragOrigin.X), Math.Abs(pt.Y - dragOrigin.Y));
                canvas.Update(selection, RenderScaling, showHandles: false);
            }
            else if (dragMode == DragMode.MovingSelection)
            {
                var dx = pt.X - dragOrigin.X;
                var dy = pt.Y - dragOrigin.Y;
                var nx = Math.Clamp(dragStartRect.X + dx, 0, Math.Max(0, Width - dragStartRect.Width));
                var ny = Math.Clamp(dragStartRect.Y + dy, 0, Math.Max(0, Height - dragStartRect.Height));
                selection = new AvaloniaRect(nx, ny, dragStartRect.Width, dragStartRect.Height);
                canvas.Update(selection, RenderScaling, showHandles: true);
            }
            else if (dragMode == DragMode.Resizing)
            {
                selection = ResizeRect(dragStartRect, resizeHandle, pt.X - dragOrigin.X, pt.Y - dragOrigin.Y);
                canvas.Update(selection, RenderScaling, showHandles: true);
            }
            else if (dragMode == DragMode.MovingAnnotation && activeAnnotation != null)
            {
                var dx = pt.X - dragOrigin.X;
                var dy = pt.Y - dragOrigin.Y;
                activeAnnotation.Start = new AvaloniaPoint(annotationOriginStart.X + dx, annotationOriginStart.Y + dy);
                activeAnnotation.End = new AvaloniaPoint(annotationOriginEnd.X + dx, annotationOriginEnd.Y + dy);
                canvas.InvalidateVisual();
            }
            else if (dragMode == DragMode.Drawing && activeAnnotation != null)
            {
                var clamped = new AvaloniaPoint(
                    Math.Clamp(pt.X, selection.X, selection.Right),
                    Math.Clamp(pt.Y, selection.Y, selection.Bottom));
                activeAnnotation.End = clamped;
                if (activeAnnotation.Points != null)
                {
                    activeAnnotation.Points.Add(clamped);
                    if (activeAnnotation.Tool == DrawTool.Mosaic)
                        AddMosaicBlocks(clamped, activeAnnotation.Thickness);
                }
                canvas.InvalidateVisual();
            }
        }

        private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (dragMode == DragMode.None) return;

            if (dragMode == DragMode.Selecting)
            {
                dragMode = DragMode.None;
                if (selection.Width < 4 || selection.Height < 4)
                {
                    hasSelection = false;
                    selection = default;
                    canvas.Update(selection, RenderScaling, showHandles: false);
                    return;
                }
                hasSelection = true;
                UpdateToolHighlight();
                canvas.Update(selection, RenderScaling, showHandles: true);
                PositionToolbar();
                return;
            }

            if (dragMode is DragMode.MovingSelection or DragMode.Resizing)
            {
                dragMode = DragMode.None;
                PositionToolbar();
                return;
            }

            if (dragMode == DragMode.MovingAnnotation)
            {
                dragMode = DragMode.None;
                activeAnnotation = null;
                return;
            }

            if (dragMode == DragMode.Drawing && activeAnnotation != null)
            {
                var a = activeAnnotation;
                activeAnnotation = null;
                dragMode = DragMode.None;
                var dx = a.End.X - a.Start.X;
                var dy = a.End.Y - a.Start.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (a.Points is { Count: > 1 } || dist >= 3)
                    annotations.Add(a);
                canvas.InvalidateVisual();
            }
        }

        private void AddMosaicBlocks(AvaloniaPoint dip, double brushRadius)
        {
            var scale = RenderScaling;
            const int blockPx = 10;
            var rDip = brushRadius;
            var minBx = (int)Math.Floor((dip.X - rDip) * scale / blockPx);
            var maxBx = (int)Math.Floor((dip.X + rDip) * scale / blockPx);
            var minBy = (int)Math.Floor((dip.Y - rDip) * scale / blockPx);
            var maxBy = (int)Math.Floor((dip.Y + rDip) * scale / blockPx);

            for (var by = minBy; by <= maxBy; by++)
            for (var bx = minBx; bx <= maxBx; bx++)
            {
                if (mosaicCache.ContainsKey((bx, by))) continue;
                var px = Math.Clamp(bx * blockPx + blockPx / 2, 0, gdi.Width - 1);
                var py = Math.Clamp(by * blockPx + blockPx / 2, 0, gdi.Height - 1);
                var c = gdi.GetPixel(px, py);
                mosaicCache[(bx, by)] = new SolidColorBrush(AvaloniaColor.FromRgb(c.R, c.G, c.B));
            }
        }

        private void BeginTextBox(AvaloniaPoint pt)
        {
            pendingTextPos = pt;
            var tb = new TextBox
            {
                FontSize = FontSizePx,
                Foreground = new SolidColorBrush(activeColor),
                Background = Brush.Parse("#55000000"),
                BorderBrush = Brush.Parse("#FFAE54"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2),
                MinWidth = 80,
                AcceptsReturn = false
            };
            Canvas.SetLeft(tb, pt.X);
            Canvas.SetTop(tb, pt.Y);
            tb.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter) { ke.Handled = true; CommitTextBox(); }
                else if (ke.Key == Key.Escape) { ke.Handled = true; CancelTextBox(); }
            };
            activeTextBox = tb;
            root.Children.Add(tb);
            tb.Focus();
        }

        private void CommitTextBox()
        {
            if (activeTextBox == null) return;
            var text = activeTextBox.Text?.Trim();
            root.Children.Remove(activeTextBox);
            activeTextBox = null;
            if (!string.IsNullOrEmpty(text))
            {
                annotations.Add(new Annotation
                {
                    Tool = DrawTool.Text,
                    Color = activeColor,
                    Thickness = FontSizePx,
                    Start = pendingTextPos,
                    End = pendingTextPos,
                    Text = text
                });
                canvas.InvalidateVisual();
            }
        }

        private void CancelTextBox()
        {
            if (activeTextBox == null) return;
            root.Children.Remove(activeTextBox);
            activeTextBox = null;
        }

        private Annotation? HitTestMovableAnnotation(AvaloniaPoint pt)
        {
            for (var i = annotations.Count - 1; i >= 0; i--)
            {
                var a = annotations[i];
                if (a.Tool == DrawTool.Icon)
                {
                    var r = a.Thickness * 0.7;
                    if (Math.Abs(pt.X - a.Start.X) <= r && Math.Abs(pt.Y - a.Start.Y) <= r) return a;
                }
                else if (a.Tool == DrawTool.Text)
                {
                    var w = (a.Text?.Length ?? 1) * a.Thickness * 0.75 + 12;
                    var h = a.Thickness * 1.5;
                    if (pt.X >= a.Start.X - 4 && pt.X <= a.Start.X + w && pt.Y >= a.Start.Y - 4 && pt.Y <= a.Start.Y + h) return a;
                }
            }
            return null;
        }

        private int HitTestHandle(AvaloniaPoint pt)
        {
            if (!hasSelection) return -1;
            var pts = GetHandlePoints(selection);
            for (var i = 0; i < pts.Length; i++)
                if (Math.Abs(pt.X - pts[i].X) <= 7 && Math.Abs(pt.Y - pts[i].Y) <= 7) return i;
            return -1;
        }

        private static AvaloniaPoint[] GetHandlePoints(AvaloniaRect r)
        {
            var mx = r.X + r.Width / 2;
            var my = r.Y + r.Height / 2;
            return
            [
                new(r.X, r.Y), new(mx, r.Y), new(r.Right, r.Y), new(r.Right, my),
                new(r.Right, r.Bottom), new(mx, r.Bottom), new(r.X, r.Bottom), new(r.X, my)
            ];
        }

        private AvaloniaRect ClampRect(double x, double y, double w, double h)
        {
            x = Math.Clamp(x, 0, Width);
            y = Math.Clamp(y, 0, Height);
            w = Math.Min(w, Width - x);
            h = Math.Min(h, Height - y);
            return new AvaloniaRect(x, y, w, h);
        }

        private AvaloniaRect ResizeRect(AvaloniaRect r, int handle, double dx, double dy)
        {
            var x1 = r.X; var y1 = r.Y; var x2 = r.Right; var y2 = r.Bottom;
            if (handle is 0 or 6 or 7) x1 = Math.Clamp(x1 + dx, 0, x2 - 8);
            if (handle is 2 or 3 or 4) x2 = Math.Clamp(x2 + dx, x1 + 8, Width);
            if (handle is 0 or 1 or 2) y1 = Math.Clamp(y1 + dy, 0, y2 - 8);
            if (handle is 4 or 5 or 6) y2 = Math.Clamp(y2 + dy, y1 + 8, Height);
            return new AvaloniaRect(x1, y1, x2 - x1, y2 - y1);
        }

        private async void OnOverlayKeyDown(object? sender, KeyEventArgs e)
        {
            if (activeTextBox != null) return;
            if (e.Key == Key.Escape) { e.Handled = true; Finish(null); }
            else if (e.Key == Key.Enter && hasSelection) { e.Handled = true; CompleteCopy(); }
            else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; Undo(); }
            else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; SelectAll(); }
            else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && allowSave && hasSelection)
            {
                e.Handled = true;
                await SaveAsync();
            }
        }

        private void Undo()
        {
            CommitTextBox();
            if (annotations.Count == 0) return;
            annotations.RemoveAt(annotations.Count - 1);
            canvas.InvalidateVisual();
        }

        private void SelectAll()
        {
            CommitTextBox();
            selection = new AvaloniaRect(0, 0, Width, Height);
            hasSelection = true;
            canvas.Update(selection, RenderScaling, showHandles: true);
            PositionToolbar();
        }

        private void CompleteCopy()
        {
            CommitTextBox();
            if (!hasSelection) return;
            var rendered = RenderFinalPng();
            Finish(rendered == null ? null : (rendered.Value.Png, rendered.Value.Width, rendered.Value.Height, CaptureOutcome.Copied));
        }

        private void QuickDownload()
        {
            CommitTextBox();
            if (!hasSelection) return;
            var rendered = RenderFinalPng();
            if (rendered is not { } result) return;

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Perch");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"Perch_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, result.Png);
            Finish((result.Png, result.Width, result.Height, CaptureOutcome.Saved));
        }

        private async Task SaveAsync()
        {
            CommitTextBox();
            if (!hasSelection) return;
            var rendered = RenderFinalPng();
            if (rendered is not { } result) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("保存截图", "Save Screenshot"),
                SuggestedFileName = $"Perch_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
            });
            if (file == null) return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(result.Png);
            Finish((result.Png, result.Width, result.Height, CaptureOutcome.Saved));
        }

        private (byte[] Png, int Width, int Height)? RenderFinalPng()
        {
            var scale = RenderScaling;
            var px = new PixelRect(
                Math.Clamp((int)Math.Round(selection.X * scale), 0, bounds.Width - 1),
                Math.Clamp((int)Math.Round(selection.Y * scale), 0, bounds.Height - 1),
                Math.Clamp((int)Math.Round(selection.Width * scale), 1, bounds.Width),
                Math.Clamp((int)Math.Round(selection.Height * scale), 1, bounds.Height));
            if (px.Width < 4 || px.Height < 4) return null;

            using var cropped = new Bitmap(px.Width, px.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(gdi, new Rectangle(0, 0, px.Width, px.Height),
                    new Rectangle(px.X, px.Y, px.Width, px.Height), GraphicsUnit.Pixel);

                // 1. Apply Mosaic annotations directly on the cropped bitmap
                const int blockPx = 10;
                foreach (var a in annotations)
                {
                    if (a.Tool != DrawTool.Mosaic || a.Points == null) continue;
                    var rPx = (float)(a.Thickness * scale);
                    var visited = new HashSet<(int, int)>();
                    foreach (var pt in a.Points)
                    {
                        var minBx = (int)Math.Floor((pt.X * scale - rPx) / blockPx);
                        var maxBx = (int)Math.Floor((pt.X * scale + rPx) / blockPx);
                        var minBy = (int)Math.Floor((pt.Y * scale - rPx) / blockPx);
                        var maxBy = (int)Math.Floor((pt.Y * scale + rPx) / blockPx);
                        for (var by = minBy; by <= maxBy; by++)
                        for (var bx = minBx; bx <= maxBx; bx++)
                        {
                            if (!visited.Add((bx, by))) continue;
                            var sx = Math.Clamp(bx * blockPx + blockPx / 2, 0, gdi.Width - 1);
                            var sy = Math.Clamp(by * blockPx + blockPx / 2, 0, gdi.Height - 1);
                            var col = gdi.GetPixel(sx, sy);
                            using var b = new GdiBrush(col);
                            g.FillRectangle(b, bx * blockPx - px.X, by * blockPx - px.Y, blockPx, blockPx);
                        }
                    }
                }

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // 2. Render vector shapes, text, and icons
                float X(double dipX) => (float)(dipX * scale - px.X);
                float Y(double dipY) => (float)(dipY * scale - px.Y);

                foreach (var a in annotations)
                {
                    if (a.Tool == DrawTool.Mosaic) continue;
                    var gc = GdiColor.FromArgb(a.Color.A, a.Color.R, a.Color.G, a.Color.B);
                    var w = (float)(a.Thickness * scale);

                    switch (a.Tool)
                    {
                        case DrawTool.Rect:
                        {
                            using var pen = new GdiPen(gc, w) { LineJoin = LineJoin.Round };
                            g.DrawRectangle(pen,
                                X(Math.Min(a.Start.X, a.End.X)), Y(Math.Min(a.Start.Y, a.End.Y)),
                                (float)(Math.Abs(a.End.X - a.Start.X) * scale),
                                (float)(Math.Abs(a.End.Y - a.Start.Y) * scale));
                            break;
                        }
                        case DrawTool.Ellipse:
                        {
                            using var pen = new GdiPen(gc, w);
                            g.DrawEllipse(pen,
                                X(Math.Min(a.Start.X, a.End.X)), Y(Math.Min(a.Start.Y, a.End.Y)),
                                (float)(Math.Abs(a.End.X - a.Start.X) * scale),
                                (float)(Math.Abs(a.End.Y - a.Start.Y) * scale));
                            break;
                        }
                        case DrawTool.Line:
                        {
                            using var pen = new GdiPen(gc, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(pen, X(a.Start.X), Y(a.Start.Y), X(a.End.X), Y(a.End.Y));
                            break;
                        }
                        case DrawTool.Arrow:
                        {
                            var x1 = X(a.Start.X); var y1 = Y(a.Start.Y);
                            var x2 = X(a.End.X); var y2 = Y(a.End.Y);
                            var angle = Math.Atan2(y2 - y1, x2 - x1);
                            var headLen = Math.Max(12f * (float)scale, w * 4.2f);
                            var backX = x2 - (float)(Math.Cos(angle) * headLen * 0.7);
                            var backY = y2 - (float)(Math.Sin(angle) * headLen * 0.7);
                            using var pen = new GdiPen(gc, w) { StartCap = LineCap.Round };
                            g.DrawLine(pen, x1, y1, backX, backY);
                            var p1 = new PointF(x2, y2);
                            var p2 = new PointF(x2 - (float)(Math.Cos(angle - 0.45) * headLen), y2 - (float)(Math.Sin(angle - 0.45) * headLen));
                            var p3 = new PointF(x2 - (float)(Math.Cos(angle + 0.45) * headLen), y2 - (float)(Math.Sin(angle + 0.45) * headLen));
                            using var brush = new GdiBrush(gc);
                            g.FillPolygon(brush, [p1, p2, p3]);
                            break;
                        }
                        case DrawTool.Brush when a.Points is { Count: > 1 }:
                        {
                            using var pen = new GdiPen(gc, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                            var pts = a.Points.Select(p => new PointF(X(p.X), Y(p.Y))).ToArray();
                            g.DrawLines(pen, pts);
                            break;
                        }
                        case DrawTool.Text when !string.IsNullOrEmpty(a.Text):
                        {
                            using var font = new GdiFont("Microsoft YaHei UI", w, GdiFontStyle.Bold, GraphicsUnit.Pixel);
                            using var brush = new GdiBrush(gc);
                            g.DrawString(a.Text, font, brush, X(a.Start.X), Y(a.Start.Y));
                            break;
                        }
                        case DrawTool.Icon when !string.IsNullOrEmpty(a.Text):
                        {
                            var cx = X(a.Start.X);
                            var cy = Y(a.Start.Y);
                            if (a.IsBadge)
                            {
                                var r = w * 0.52f;
                                using var fill = new GdiBrush(gc);
                                g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
                                using var ring = new GdiPen(GdiColor.White, Math.Max(1.5f, (float)scale * 1.5f));
                                g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
                                using var font = new GdiFont("Segoe UI", w * 0.58f, GdiFontStyle.Bold, GraphicsUnit.Pixel);
                                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                var textCol = gc.GetBrightness() > 0.8f ? GdiColor.Black : GdiColor.White;
                                using var tb = new GdiBrush(textCol);
                                g.DrawString(a.Text, font, tb, cx, cy + 1, sf);
                            }
                            else
                            {
                                using var font = new GdiFont("Segoe UI Emoji", w * 0.85f, GdiFontStyle.Regular, GraphicsUnit.Pixel);
                                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                                using var tb = new GdiBrush(gc);
                                g.DrawString(a.Text, font, tb, cx, cy, sf);
                            }
                            break;
                        }
                    }
                }
            }

            using var ms = new MemoryStream();
            cropped.Save(ms, ImageFormat.Png);
            return (ms.ToArray(), px.Width, px.Height);
        }

        private void Finish((byte[] Png, int Width, int Height, CaptureOutcome Outcome)? result)
        {
            if (finished) return;
            finished = true;
            done.TrySetResult(result);
            Close();
        }

        private sealed class EditorCanvas : Control, Avalonia.Rendering.ICustomHitTest
        {
            private readonly WriteableBitmap full;
            private readonly List<Annotation> annotations;
            private readonly Dictionary<(int, int), IBrush> mosaicCache;
            private readonly Func<Annotation?> getActive;

            public EditorCanvas(
                WriteableBitmap full,
                List<Annotation> annotations,
                Dictionary<(int, int), IBrush> mosaicCache,
                Func<Annotation?> getActive)
            {
                this.full = full;
                this.annotations = annotations;
                this.mosaicCache = mosaicCache;
                this.getActive = getActive;
            }

            public bool HitTest(AvaloniaPoint point) => true;
            private static readonly IBrush Veil = new SolidColorBrush(AvaloniaColor.FromArgb(0x66, 0, 0, 0));
            private static readonly Pen Rim = new(Brush.Parse("#FFAE54"), 1.5);
            private static readonly IBrush HandleFill = AvaloniaBrushes.White;
            private static readonly Pen HandleRim = new(Brush.Parse("#FFAE54"), 1.2);
            private AvaloniaRect sel;
            private double scale = 1;
            private bool showHandles;

            public void Update(AvaloniaRect rect, double renderScale, bool showHandles)
            {
                sel = rect;
                scale = renderScale;
                this.showHandles = showHandles;
                InvalidateVisual();
            }

            public override void Render(DrawingContext ctx)
            {
                var all = new AvaloniaRect(Bounds.Size);
                ctx.DrawImage(full, all);

                if (sel.Width < 1 || sel.Height < 1)
                {
                    ctx.FillRectangle(Veil, all);
                    return;
                }

                // Draw mosaic blocks clipped to selection
                using (ctx.PushClip(sel))
                {
                    const int blockPx = 10;
                    var blockDip = blockPx / scale;
                    foreach (var a in annotations.Append(getActive()).OfType<Annotation>())
                    {
                        if (a.Tool != DrawTool.Mosaic || a.Points == null) continue;
                        var rPx = a.Thickness * scale;
                        foreach (var pt in a.Points)
                        {
                            var minBx = (int)Math.Floor((pt.X * scale - rPx) / blockPx);
                            var maxBx = (int)Math.Floor((pt.X * scale + rPx) / blockPx);
                            var minBy = (int)Math.Floor((pt.Y * scale - rPx) / blockPx);
                            var maxBy = (int)Math.Floor((pt.Y * scale + rPx) / blockPx);
                            for (var by = minBy; by <= maxBy; by++)
                            for (var bx = minBx; bx <= maxBx; bx++)
                            {
                                if (mosaicCache.TryGetValue((bx, by), out var b))
                                    ctx.FillRectangle(b, new AvaloniaRect(bx * blockDip, by * blockDip, blockDip + 0.5, blockDip + 0.5));
                            }
                        }
                    }

                    // Draw vector annotations
                    foreach (var a in annotations.Append(getActive()).OfType<Annotation>())
                    {
                        if (a.Tool == DrawTool.Mosaic) continue;
                        DrawAnnotation(ctx, a);
                    }
                }

                // Dim outside selection
                ctx.FillRectangle(Veil, new AvaloniaRect(0, 0, all.Width, sel.Y));
                ctx.FillRectangle(Veil, new AvaloniaRect(0, sel.Bottom, all.Width, Math.Max(0, all.Height - sel.Bottom)));
                ctx.FillRectangle(Veil, new AvaloniaRect(0, sel.Y, sel.X, sel.Height));
                ctx.FillRectangle(Veil, new AvaloniaRect(sel.Right, sel.Y, Math.Max(0, all.Width - sel.Right), sel.Height));
                ctx.DrawRectangle(null, Rim, sel);

                if (showHandles)
                {
                    foreach (var hp in GetHandlePoints(sel))
                    {
                        var hr = new AvaloniaRect(hp.X - 3.5, hp.Y - 3.5, 7, 7);
                        ctx.FillRectangle(HandleFill, hr);
                        ctx.DrawRectangle(null, HandleRim, hr);
                    }
                }

                // Size pill
                var label = new FormattedText(
                    $"{(int)Math.Round(sel.Width * scale)} × {(int)Math.Round(sel.Height * scale)}",
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Cascadia Mono, Consolas"), 12, AvaloniaBrushes.White);
                var lx = Math.Max(6, sel.X);
                var ly = sel.Y >= 26 ? sel.Y - 24 : sel.Y + 6;
                var pill = new AvaloniaRect(lx, ly, label.Width + 12, label.Height + 6);
                ctx.FillRectangle(Brush.Parse("#CC121215"), pill, 6);
                ctx.DrawText(label, new AvaloniaPoint(lx + 6, ly + 3));
            }

            private static void DrawAnnotation(DrawingContext ctx, Annotation a)
            {
                var brush = new SolidColorBrush(a.Color);
                var pen = new Pen(brush, a.Thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

                switch (a.Tool)
                {
                    case DrawTool.Rect:
                        ctx.DrawRectangle(null, pen, new AvaloniaRect(
                            Math.Min(a.Start.X, a.End.X), Math.Min(a.Start.Y, a.End.Y),
                            Math.Abs(a.End.X - a.Start.X), Math.Abs(a.End.Y - a.Start.Y)));
                        break;
                    case DrawTool.Ellipse:
                    {
                        var rx = Math.Abs(a.End.X - a.Start.X) / 2;
                        var ry = Math.Abs(a.End.Y - a.Start.Y) / 2;
                        var cx = (a.Start.X + a.End.X) / 2;
                        var cy = (a.Start.Y + a.End.Y) / 2;
                        ctx.DrawEllipse(null, pen, new AvaloniaPoint(cx, cy), rx, ry);
                        break;
                    }
                    case DrawTool.Line:
                        ctx.DrawLine(pen, a.Start, a.End);
                        break;
                    case DrawTool.Arrow:
                    {
                        var angle = Math.Atan2(a.End.Y - a.Start.Y, a.End.X - a.Start.X);
                        var headLen = Math.Max(12, a.Thickness * 4.2);
                        var back = new AvaloniaPoint(
                            a.End.X - Math.Cos(angle) * headLen * 0.7,
                            a.End.Y - Math.Sin(angle) * headLen * 0.7);
                        ctx.DrawLine(pen, a.Start, back);
                        var p2 = new AvaloniaPoint(a.End.X - Math.Cos(angle - 0.45) * headLen, a.End.Y - Math.Sin(angle - 0.45) * headLen);
                        var p3 = new AvaloniaPoint(a.End.X - Math.Cos(angle + 0.45) * headLen, a.End.Y - Math.Sin(angle + 0.45) * headLen);
                        var geo = new StreamGeometry();
                        using (var gc = geo.Open())
                        {
                            gc.BeginFigure(a.End, true);
                            gc.LineTo(p2);
                            gc.LineTo(p3);
                            gc.EndFigure(true);
                        }
                        ctx.DrawGeometry(brush, null, geo);
                        break;
                    }
                    case DrawTool.Brush when a.Points is { Count: > 1 }:
                    {
                        var geo = new StreamGeometry();
                        using (var gc = geo.Open())
                        {
                            gc.BeginFigure(a.Points[0], false);
                            for (var i = 1; i < a.Points.Count; i++) gc.LineTo(a.Points[i]);
                            gc.EndFigure(false);
                        }
                        ctx.DrawGeometry(null, pen, geo);
                        break;
                    }
                    case DrawTool.Text when !string.IsNullOrEmpty(a.Text):
                    {
                        var ft = new FormattedText(a.Text, System.Globalization.CultureInfo.CurrentUICulture,
                            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI", Avalonia.Media.FontStyle.Normal, FontWeight.Bold),
                            a.Thickness, brush);
                        ctx.DrawText(ft, a.Start);
                        break;
                    }
                    case DrawTool.Icon when !string.IsNullOrEmpty(a.Text):
                    {
                        if (a.IsBadge)
                        {
                            var r = a.Thickness * 0.52;
                            ctx.DrawEllipse(brush, new Pen(AvaloniaBrushes.White, 1.5), a.Start, r, r);
                            var luminance = (0.299 * a.Color.R + 0.587 * a.Color.G + 0.114 * a.Color.B) / 255.0;
                            var fg = luminance > 0.78 ? AvaloniaBrushes.Black : AvaloniaBrushes.White;
                            var ft = new FormattedText(a.Text, System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface("Segoe UI", Avalonia.Media.FontStyle.Normal, FontWeight.Bold),
                                a.Thickness * 0.58, fg);
                            ctx.DrawText(ft, new AvaloniaPoint(a.Start.X - ft.Width / 2, a.Start.Y - ft.Height / 2));
                        }
                        else
                        {
                            var ft = new FormattedText(a.Text, System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface("Segoe UI Emoji"),
                                a.Thickness * 0.85, brush);
                            ctx.DrawText(ft, new AvaloniaPoint(a.Start.X - ft.Width / 2, a.Start.Y - ft.Height / 2));
                        }
                        break;
                    }
                }
            }
        }
    }
}
