using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Services;
using Perch.Services.Sounds;
using Perch.ViewModels;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using AvaloniaColor = Avalonia.Media.Color;
using AvaloniaPoint = Avalonia.Point;
using AvaloniaRect = Avalonia.Rect;
using AvaloniaSize = Avalonia.Size;
using Bitmap = System.Drawing.Bitmap;
using Brush = Avalonia.Media.Brush;
using Brushes = Avalonia.Media.Brushes;
using Image = Avalonia.Controls.Image;
using Pen = Avalonia.Media.Pen;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Perch.Platform.Windows;

// Lightweight fullscreen overlay for screen color picking and region screenshots.
// Captures the virtual desktop before opening so the screen freezes cleanly under the cursor.
internal static class ScreenCaptureOverlay
{
    private const int SmXVirtual = 76, SmYVirtual = 77, SmCxVirtual = 78, SmCyVirtual = 79;

    public static async Task PickColorAsync(AlertQueue alerts)
    {
        var shot = GrabVirtualScreen();
        if (shot == null) return;
        using var gdi = shot.Value.Gdi;
        using var bmp = shot.Value.Avalonia;
        var rect = shot.Value.Bounds;

        var done = new TaskCompletionSource<AvaloniaColor?>();
        var overlay = new ColorPickerOverlay(gdi, bmp, rect, done);
        overlay.Show();
        overlay.Activate();
        var picked = await done.Task;
        if (picked is not { } c) return;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        if (Ui.Clipboard is { } clipboard)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, hex);
    }

    public static async Task CaptureRegionAsync(AlertQueue alerts)
    {
        var png = await CaptureRegionPngAsync();
        if (png is not { } result) return;
        using var ms = new MemoryStream(result.Png);
        using var av = new AvaloniaBitmap(ms);
        if (Ui.Clipboard is { } clipboard)
            await clipboard.SetBitmapAsync(av);
    }

    public static async Task<(byte[] Png, int Width, int Height)?> CaptureRegionPngAsync()
    {
        var shot = GrabVirtualScreen();
        if (shot == null) return null;
        using var gdi = shot.Value.Gdi;
        using var bmp = shot.Value.Avalonia;
        var rect = shot.Value.Bounds;

        var done = new TaskCompletionSource<PixelRect?>();
        var overlay = new RegionCaptureOverlay(bmp, rect, done);
        overlay.Show();
        overlay.Activate();
        var region = await done.Task;
        if (region is not { Width: >= 4, Height: >= 4 } r) return null;

        using var cropped = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
            g.DrawImage(gdi, new Rectangle(0, 0, r.Width, r.Height), new Rectangle(r.X, r.Y, r.Width, r.Height), GraphicsUnit.Pixel);
        using var ms = new MemoryStream();
        cropped.Save(ms, ImageFormat.Png);
        return (ms.ToArray(), r.Width, r.Height);
    }

    private static (Bitmap Gdi, WriteableBitmap Avalonia, PixelRect Bounds)? GrabVirtualScreen()
    {
        var x = GetSystemMetrics(SmXVirtual);
        var y = GetSystemMetrics(SmYVirtual);
        var w = GetSystemMetrics(SmCxVirtual);
        var h = GetSystemMetrics(SmCyVirtual);
        if (w <= 0 || h <= 0) return null;
        try
        {
            var gdi = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(gdi)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            var av = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            var data = gdi.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                using var frame = av.Lock();
                for (var row = 0; row < h; row++)
                    CopyMemory(frame.Address + row * frame.RowBytes, data.Scan0 + row * data.Stride, (nuint)(w * 4));
            }
            finally { gdi.UnlockBits(data); }
            return (gdi, av, new PixelRect(x, y, w, h));
        }
        catch { return null; }
    }

    private sealed class ColorPickerOverlay : Window
    {
        private readonly Bitmap gdi;
        private readonly WriteableBitmap full;
        private readonly PixelRect bounds;
        private readonly TaskCompletionSource<AvaloniaColor?> done;
        private readonly Border loupe, swatch;
        private readonly Image zoomImage;
        private readonly TextBlock hexText, rgbText;
        private PixelPoint cursorPx;

        public ColorPickerOverlay(Bitmap gdi, WriteableBitmap full, PixelRect bounds, TaskCompletionSource<AvaloniaColor?> done)
        {
            this.gdi = gdi;
            this.full = full;
            this.bounds = bounds;
            this.done = done;
            WindowDecorations = WindowDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = new Cursor(StandardCursorType.Cross);
            Position = new PixelPoint(bounds.X, bounds.Y);

            zoomImage = new Image { Width = 110, Height = 110, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(zoomImage, BitmapInterpolationMode.None);

            var crosshair = new Border
            {
                Width = 10, Height = 10, BorderBrush = Brush.Parse("#FFAE54"), BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
            var zoomBox = new Border
            {
                Width = 110, Height = 110, CornerRadius = new CornerRadius(10), ClipToBounds = true,
                BorderBrush = Brush.Parse("#3A3A42"), BorderThickness = new Thickness(1),
                Child = new Panel { Children = { zoomImage, crosshair } },
            };

            swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
            hexText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas"), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            rgbText = new TextBlock { FontSize = 11, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas"), Foreground = Brush.Parse("#A9A9AF") };

            var info = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { swatch, hexText } },
                    rgbText,
                }
            };
            loupe = new Border
            {
                Background = Brush.Parse("#E8121215"), BorderBrush = Brush.Parse("#30FFFFFF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14), Padding = new Thickness(8), IsHitTestVisible = false,
                Child = new StackPanel { Spacing = 7, Children = { zoomBox, info } }
            };

            var stage = new Canvas { Children = { new Image { Source = full, Stretch = Stretch.Fill }, loupe } };
            Content = stage;
            Opened += (_, _) =>
            {
                var scale = RenderScaling;
                Width = bounds.Width / scale;
                Height = bounds.Height / scale;
                Position = new PixelPoint(bounds.X, bounds.Y);
                UpdateAt(new AvaloniaPoint(Width / 2, Height / 2));
            };
            PointerMoved += (_, e) => UpdateAt(e.GetPosition(stage));
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(stage).Properties.IsRightButtonPressed) { Finish(null); return; }
                if (!e.GetCurrentPoint(stage).Properties.IsLeftButtonPressed) return;
                UpdateAt(e.GetPosition(stage));
                var c = gdi.GetPixel(cursorPx.X, cursorPx.Y);
                Finish(AvaloniaColor.FromRgb(c.R, c.G, c.B));
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
            Closed += (_, _) => done.TrySetResult(null);
        }

        private void UpdateAt(AvaloniaPoint dip)
        {
            var scale = RenderScaling;
            var px = Math.Clamp((int)Math.Round(dip.X * scale), 0, gdi.Width - 1);
            var py = Math.Clamp((int)Math.Round(dip.Y * scale), 0, gdi.Height - 1);
            cursorPx = new PixelPoint(px, py);
            var c = gdi.GetPixel(px, py);
            swatch.Background = new SolidColorBrush(AvaloniaColor.FromRgb(c.R, c.G, c.B));
            hexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            rgbText.Text = $"{c.R}, {c.G}, {c.B}";

            var cx = Math.Clamp(px - 5, 0, Math.Max(0, gdi.Width - 11));
            var cy = Math.Clamp(py - 5, 0, Math.Max(0, gdi.Height - 11));
            zoomImage.Source = new CroppedBitmap(full, new PixelRect(cx, cy, Math.Min(11, gdi.Width), Math.Min(11, gdi.Height)));

            var lx = dip.X + 20 + 136 > Width ? dip.X - 150 : dip.X + 20;
            var ly = dip.Y + 20 + 176 > Height ? dip.Y - 186 : dip.Y + 20;
            Canvas.SetLeft(loupe, Math.Max(8, lx));
            Canvas.SetTop(loupe, Math.Max(8, ly));
        }

        private void Finish(AvaloniaColor? result) { done.TrySetResult(result); Close(); }
    }

    private sealed class RegionCaptureOverlay : Window
    {
        private readonly PixelRect bounds;
        private readonly TaskCompletionSource<PixelRect?> done;
        private readonly RegionCanvas canvas;
        private AvaloniaPoint? anchor;
        private AvaloniaRect selection;

        public RegionCaptureOverlay(WriteableBitmap full, PixelRect bounds, TaskCompletionSource<PixelRect?> done)
        {
            this.bounds = bounds;
            this.done = done;
            WindowDecorations = WindowDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = new Cursor(StandardCursorType.Cross);
            Position = new PixelPoint(bounds.X, bounds.Y);

            canvas = new RegionCanvas(full);
            Content = canvas;
            Opened += (_, _) =>
            {
                var scale = RenderScaling;
                Width = bounds.Width / scale;
                Height = bounds.Height / scale;
                Position = new PixelPoint(bounds.X, bounds.Y);
            };
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed) { Finish(null); return; }
                if (!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed) return;
                anchor = e.GetPosition(canvas);
                selection = new AvaloniaRect(anchor.Value, default(AvaloniaSize));
                canvas.UpdateSelection(selection, RenderScaling);
            };
            PointerMoved += (_, e) =>
            {
                if (anchor is not { } start) return;
                var cur = e.GetPosition(canvas);
                selection = new AvaloniaRect(
                    Math.Min(start.X, cur.X), Math.Min(start.Y, cur.Y),
                    Math.Abs(cur.X - start.X), Math.Abs(cur.Y - start.Y));
                canvas.UpdateSelection(selection, RenderScaling);
            };
            PointerReleased += (_, e) =>
            {
                if (anchor == null) return;
                anchor = null;
                var scale = RenderScaling;
                var px = new PixelRect(
                    Math.Clamp((int)Math.Round(selection.X * scale), 0, bounds.Width - 1),
                    Math.Clamp((int)Math.Round(selection.Y * scale), 0, bounds.Height - 1),
                    (int)Math.Round(selection.Width * scale),
                    (int)Math.Round(selection.Height * scale));
                Finish(px.Width >= 4 && px.Height >= 4 ? px : null);
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
            Closed += (_, _) => done.TrySetResult(null);
        }

        private void Finish(PixelRect? result) { done.TrySetResult(result); Close(); }

        private sealed class RegionCanvas(WriteableBitmap full) : Control
        {
            private static readonly IBrush Veil = new SolidColorBrush(AvaloniaColor.FromArgb(0x66, 0, 0, 0));
            private static readonly Pen Rim = new(Brush.Parse("#FFAE54"), 1.5);
            private AvaloniaRect sel;
            private double scale = 1;

            public void UpdateSelection(AvaloniaRect rect, double renderScale) { sel = rect; scale = renderScale; InvalidateVisual(); }

            public override void Render(DrawingContext ctx)
            {
                var all = new AvaloniaRect(Bounds.Size);
                ctx.DrawImage(full, all);
                if (sel.Width < 1 || sel.Height < 1) { ctx.FillRectangle(Veil, all); return; }
                // Dim only outside the selected rectangle.
                ctx.FillRectangle(Veil, new AvaloniaRect(0, 0, all.Width, sel.Y));
                ctx.FillRectangle(Veil, new AvaloniaRect(0, sel.Bottom, all.Width, Math.Max(0, all.Height - sel.Bottom)));
                ctx.FillRectangle(Veil, new AvaloniaRect(0, sel.Y, sel.X, sel.Height));
                ctx.FillRectangle(Veil, new AvaloniaRect(sel.Right, sel.Y, Math.Max(0, all.Width - sel.Right), sel.Height));
                ctx.DrawRectangle(null, Rim, sel);
                var label = new FormattedText($"{(int)Math.Round(sel.Width * scale)} × {(int)Math.Round(sel.Height * scale)}",
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Cascadia Mono, Consolas"), 12, Brushes.White);
                var lx = Math.Max(6, sel.X);
                var ly = sel.Y >= 26 ? sel.Y - 24 : sel.Bottom + 6;
                var pill = new AvaloniaRect(lx, ly, label.Width + 12, label.Height + 6);
                ctx.FillRectangle(Brush.Parse("#CC121215"), pill, 6);
                ctx.DrawText(label, new AvaloniaPoint(lx + 6, ly + 3));
            }
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", EntryPoint = "RtlCopyMemory")] private static extern void CopyMemory(nint dest, nint src, nuint count);
}
