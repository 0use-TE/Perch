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
        await CaptureInteractiveAsync(alerts, startFullscreen: false);
    }

    public static async Task CaptureFullscreenAsync(AlertQueue alerts)
    {
        await CaptureInteractiveAsync(alerts, startFullscreen: true);
    }

    private static async Task CaptureInteractiveAsync(AlertQueue alerts, bool startFullscreen)
    {
        var shot = GrabVirtualScreen();
        if (shot == null) return;
        using var gdi = shot.Value.Gdi;
        using var bmp = shot.Value.Avalonia;
        var rect = shot.Value.Bounds;

        var result = await ScreenshotEditor.RunAsync(gdi, bmp, rect, startFullscreen, allowSave: true);
        if (result is not { } res) return;

        SetClipboardImage(res.Png);

        var msg = res.Outcome == CaptureOutcome.Saved
            ? Loc.T("Tool.ShotSaved")
            : Loc.F("Tool.ShotCopied", res.Width, res.Height);
        alerts.Show(AlertQueue.Done, msg, SoundKind.None);
    }

    private static void SetClipboardImage(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        using var bmp = new Bitmap(ms);
        var w = bmp.Width;
        var h = bmp.Height;

        // Build CF_DIB (40-byte BITMAPINFOHEADER + bottom-up 32bpp BGRA pixels)
        var rowBytes = w * 4;
        var dibSize = 40 + rowBytes * h;
        var dibBytes = new byte[dibSize];
        BitConverter.TryWriteBytes(dibBytes.AsSpan(0, 4), 40);          // biSize
        BitConverter.TryWriteBytes(dibBytes.AsSpan(4, 4), w);           // biWidth
        BitConverter.TryWriteBytes(dibBytes.AsSpan(8, 4), h);           // biHeight (positive = bottom-up)
        BitConverter.TryWriteBytes(dibBytes.AsSpan(12, 2), (short)1);   // biPlanes
        BitConverter.TryWriteBytes(dibBytes.AsSpan(14, 2), (short)32);  // biBitCount
        BitConverter.TryWriteBytes(dibBytes.AsSpan(16, 4), 0);          // biCompression = BI_RGB
        BitConverter.TryWriteBytes(dibBytes.AsSpan(20, 4), rowBytes * h);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                var srcRow = data.Scan0 + (h - 1 - y) * data.Stride;
                Marshal.Copy(srcRow, dibBytes, 40 + y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        if (!OpenClipboard(0)) return;
        try
        {
            EmptyClipboard();
            var pngFmt = RegisterClipboardFormatW("PNG");
            if (pngFmt != 0) SetClipboardBytes(pngFmt, pngBytes);
            SetClipboardBytes(8, dibBytes); // CF_DIB = 8
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetClipboardBytes(uint format, byte[] bytes)
    {
        var hMem = GlobalAlloc(0x0042, (nuint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
        if (hMem == 0) return;
        var ptr = GlobalLock(hMem);
        if (ptr == 0) { GlobalFree(hMem); return; }
        try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
        finally { GlobalUnlock(hMem); }
        if (SetClipboardData(format, hMem) == 0) GlobalFree(hMem);
    }

    public static async Task<(byte[] Png, int Width, int Height)?> CaptureRegionPngAsync()
    {
        var shot = GrabVirtualScreen();
        if (shot == null) return null;
        using var gdi = shot.Value.Gdi;
        using var bmp = shot.Value.Avalonia;
        var rect = shot.Value.Bounds;

        var result = await ScreenshotEditor.RunAsync(gdi, bmp, rect, startFullscreen: false, allowSave: false);
        return result is { } res ? (res.Png, res.Width, res.Height) : null;
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

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string lpszFormat);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);
    [DllImport("kernel32.dll", EntryPoint = "RtlCopyMemory")] private static extern void CopyMemory(nint dest, nint src, nuint count);
}
