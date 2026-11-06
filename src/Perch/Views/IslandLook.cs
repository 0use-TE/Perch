using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Perch.Services;

namespace Perch.Views;

// The island's material, shared by the island window and the settings preview. It is always black when closed.
// "Translucent" lets the open island show the frosted screen behind it through a black tint.
internal static class IslandLook
{
    public static bool IsGlass(Settings s) => s.Appearance == "glass";

    public static void Apply(Path outline, Path grain, Path sheen, bool translucent)
    {
        grain.IsVisible = false;
        sheen.IsVisible = false;
        if (translucent)
        {
            outline.Fill = new SolidColorBrush(Colors.Black);
            // Closed it is plain black with no edge at all; a hairline rim only fades in as the island opens.
            outline.Stroke = new SolidColorBrush(Color.FromArgb(0, 0xFF, 0xFF, 0xFF));
            outline.StrokeThickness = 0.75;
        }
        else
        {
            outline.Fill = Brushes.Black;
            outline.Stroke = null;
        }
    }

    // open 0 = closed (opaque black), open 1 = fully open (black at the user's opacity over the backdrop).
    public static void SetGlassFill(Path outline, Settings s, double open)
    {
        if (outline.Fill is not SolidColorBrush brush) return;
        var opacity = Math.Clamp(s.GlassOpacity, 10, 95) / 100.0;
        var alpha = 1 - Math.Clamp(open, 0, 1) * (1 - opacity);
        brush.Color = Color.FromArgb((byte)Math.Round(255 * alpha), 6, 8, 14);
        if (outline.Stroke is SolidColorBrush rim) rim.Color = Color.FromArgb((byte)Math.Round(0x30 * Math.Clamp(open, 0, 1)), 0xFF, 0xFF, 0xFF);
    }
}
