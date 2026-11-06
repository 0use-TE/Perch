using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WpfFormats = System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;

namespace Perch.Platform.Windows;

// Grabs the screen behind the island (which is hidden from capture) and turns it into a small, soft, slightly
// more saturated picture. Drawn stretched back up under a black tint it reads as frosted glass.
internal static class BackdropCapture
{
    private const int Downscale = 10, BlurRadius = 3;
    private const double Saturation = 1.5, Lift = 1.12;

    public static BitmapSource? Grab(Rectangle area, int downscale = Downscale)
    {
        if (area.Width < 16 || area.Height < 16) return null;
        try
        {
            using var full = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
            var w = Math.Max(8, area.Width / downscale);
            var h = Math.Max(8, area.Height / downscale);
            using var small = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, w, h), 0, 0, full.Width, full.Height, GraphicsUnit.Pixel);
            }
            var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w * 4, w * 4);
            small.UnlockBits(data);

            Blur(pixels, w, h);
            Blur(pixels, w, h);
            Saturate(pixels);
            var picture = BitmapSource.Create(w, h, 96, 96, WpfFormats.Bgr32, null, pixels, w * 4);
            picture.Freeze();
            return picture;
        }
        catch (Exception) { return null; } // e.g. secure desktop or a screen being reconfigured
    }

    // Separable box blur on the small picture, edge pixels clamped.
    private static void Blur(byte[] p, int w, int h)
    {
        var tmp = new byte[p.Length];
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
