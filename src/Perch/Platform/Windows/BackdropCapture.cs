using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using AvaloniaPlatform = Avalonia.Platform;

namespace Perch.Platform.Windows;

// Grabs the screen behind the island (which is hidden from capture) and turns it into a small, soft, slightly
// more saturated picture. Drawn stretched back up under a black tint it reads as frosted glass.
internal static class BackdropCapture
{
    private const int Downscale = 10, BlurRadius = 3;
    private const double Saturation = 1.5, Lift = 1.12;
    private static readonly ConcurrentDictionary<nint, byte> running = new();

    public static Avalonia.Media.Imaging.WriteableBitmap? Grab(Rectangle area, int downscale = Downscale)
    {
        if (GrabRaw(area, downscale) is not { } raw) return null;
        var picture = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(raw.W, raw.H), new Vector(96, 96), AvaloniaPlatform.PixelFormat.Bgra8888, AvaloniaPlatform.AlphaFormat.Opaque);
        using (var frame = picture.Lock())
            for (var y = 0; y < raw.H; y++) Marshal.Copy(raw.Pixels, y * raw.W * 4, frame.Address + y * frame.RowBytes, raw.W * 4);
        return picture;
    }

    // Samples and blurs the screen behind `hwnd` on a background thread and updates `target.Background` in place.
    public static void SampleAsync(nint hwnd, Rectangle area, Border target, int downscale = Downscale, Action? onReady = null)
    {
        if (!WindowBackdrop.ReadyToSample(hwnd) || area.Width < 16 || area.Height < 16) return;
        if (!running.TryAdd(hwnd, 1)) return;
        var rect = area;
        _ = Task.Run(() =>
        {
            var raw = GrabRaw(rect, downscale);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (raw is not { } result || !WindowBackdrop.ReadyToSample(hwnd)) return;
                    ApplyToBorder(target, result.Pixels, result.W, result.H);
                    onReady?.Invoke();
                }
                finally
                {
                    running.TryRemove(hwnd, out _);
                }
            }, DispatcherPriority.Render);
        });
    }

    private static void ApplyToBorder(Border target, byte[] pixels, int w, int h)
    {
        if (target.Background is ImageBrush { Source: Avalonia.Media.Imaging.WriteableBitmap existing } &&
            existing.PixelSize.Width == w && existing.PixelSize.Height == h)
        {
            using (var frame = existing.Lock())
                for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, frame.Address + y * frame.RowBytes, w * 4);
            target.InvalidateVisual();
            return;
        }
        var picture = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), AvaloniaPlatform.PixelFormat.Bgra8888, AvaloniaPlatform.AlphaFormat.Opaque);
        using (var frame = picture.Lock())
            for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, frame.Address + y * frame.RowBytes, w * 4);
        target.Background = new ImageBrush(picture) { Stretch = Stretch.Fill, TileMode = TileMode.None };
    }

    private static (byte[] Pixels, int W, int H)? GrabRaw(Rectangle area, int downscale)
    {
        if (area.Width < 16 || area.Height < 16) return null;
        try
        {
            using var full = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
            var w = Math.Max(8, area.Width / downscale);
            var h = Math.Max(8, area.Height / downscale);
            using var small = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, w, h), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel);
            }
            var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w * 4, w * 4);
            small.UnlockBits(data);

            var tmp = new byte[pixels.Length];
            Blur(pixels, tmp, w, h);
            Blur(pixels, tmp, w, h);
            Saturate(pixels);
            return (pixels, w, h);
        }
        catch (Exception) { return null; } // e.g. secure desktop or a screen being reconfigured
    }

    // Separable box blur on the small picture, edge pixels clamped.
    private static void Blur(byte[] p, byte[] tmp, int w, int h)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = -BlurRadius; k <= BlurRadius; k++) sum += p[(y * w + Math.Clamp(x + k, 0, w - 1)) * 4 + c];
                    tmp[(y * w + x) * 4 + c] = (byte)(sum / (2 * BlurRadius + 1));
                }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var k = -BlurRadius; k <= BlurRadius; k++) sum += tmp[(Math.Clamp(y + k, 0, h - 1) * w + x) * 4 + c];
                    p[(y * w + x) * 4 + c] = (byte)(sum / (2 * BlurRadius + 1));
                }
    }

    private static void Saturate(byte[] p)
    {
        for (var i = 0; i < p.Length; i += 4)
        {
            double b = p[i], g = p[i + 1], r = p[i + 2];
            var luma = 0.299 * r + 0.587 * g + 0.114 * b;
            p[i] = (byte)Math.Clamp((luma + (b - luma) * Saturation) * Lift, 0, 255);
            p[i + 1] = (byte)Math.Clamp((luma + (g - luma) * Saturation) * Lift, 0, 255);
            p[i + 2] = (byte)Math.Clamp((luma + (r - luma) * Saturation) * Lift, 0, 255);
            p[i + 3] = 255;
        }
    }
}
