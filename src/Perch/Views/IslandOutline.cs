using System.Windows;
using System.Windows.Media;

namespace Perch.Views;

// One silhouette is shared by WPF rendering and the native hit region.
// Coordinates are in stage space: x is the silhouette's left edge inside the host window.
internal static class IslandOutline
{
    public const double CompactWidth = 248, CompactHeight = 36;
    public const double ExpandedWidth = 840, ExpandedHeight = 276;
    // Room around the expanded shape so spring overshoot isn't flattened against the window edge.
    public const double PadX = 16, PadBottom = 12;
    // The capsule form floats this far below the screen edge; the standard form stays flush with it.
    public const double CapsuleGap = 6;
    public const double HostWidth = ExpandedWidth + PadX * 2, HostHeight = ExpandedHeight + PadBottom + CapsuleGap;
    public const double CompactHostHeight = CompactHeight + CapsuleGap;
    public static StreamGeometry Create(double x, double width, double height, double progress, bool capsule = false)
    {
        if (capsule) return Capsule(x, width, height, progress);
        var shoulder = 18 + 22 * progress;
        var bottom = 18 + 20 * progress;
        var r = x + width;
        var l = x;
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(l, 0), true, true);
            path.LineTo(new Point(r, 0), true, false);
            path.BezierTo(new Point(r - shoulder, 0), new Point(r - shoulder, shoulder * 0.5), new Point(r - shoulder, shoulder), true, false);
            path.LineTo(new Point(r - shoulder, height - bottom), true, false);
            path.BezierTo(new Point(r - shoulder, height), new Point(r - shoulder - bottom * 0.5, height), new Point(r - shoulder - bottom, height), true, false);
            path.LineTo(new Point(l + shoulder + bottom, height), true, false);
            path.BezierTo(new Point(l + shoulder + bottom * 0.5, height), new Point(l + shoulder, height), new Point(l + shoulder, height - bottom), true, false);
            path.LineTo(new Point(l + shoulder, shoulder), true, false);
            path.BezierTo(new Point(l + shoulder, shoulder * 0.5), new Point(l + shoulder, 0), new Point(l, 0), true, false);
        }
        shape.Freeze();
        return shape;
    }

    // A detached rounded rectangle: fully round when compact, a softer large radius when open.
    private static StreamGeometry Capsule(double x, double width, double height, double progress)
    {
        var top = CapsuleGap;
        var bottom = top + height;
        var r = Math.Min(height / 2, 18 + 22 * progress);
        var right = x + width;
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(x + r, top), true, true);
            path.LineTo(new Point(right - r, top), true, false);
            path.ArcTo(new Point(right, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(right, bottom - r), true, false);
            path.ArcTo(new Point(right - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(x + r, bottom), true, false);
            path.ArcTo(new Point(x, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(x, top + r), true, false);
            path.ArcTo(new Point(x + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        shape.Freeze();
        return shape;
    }
}
