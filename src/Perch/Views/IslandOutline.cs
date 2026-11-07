using Avalonia;
using Avalonia.Media;

namespace Perch.Views;

// One silhouette is shared by drawing and the native hit region: both come from the same polygon of finely sampled points.
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
    private const int CurveSamples = 12;

    public static StreamGeometry Create(double x, double width, double height, double progress, bool capsule = false)
    {
        return ToGeometry(Polygon(x, width, height, progress, capsule));
    }

    public static StreamGeometry ToGeometry(IReadOnlyList<Point> points)
    {
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(points[0], true);
            for (var i = 1; i < points.Count; i++) path.LineTo(points[i]);
            path.EndFigure(true);
        }
        return shape;
    }

    public static List<Point> Polygon(double x, double width, double height, double progress, bool capsule = false)
    {
        var points = new List<Point>();
        void Line(Point p) => points.Add(p);
        void Bezier(Point from, Point c1, Point c2, Point to)
        {
            for (var i = 1; i <= CurveSamples; i++)
            {
                var t = i / (double)CurveSamples; var u = 1 - t;
                points.Add(new Point(
                    u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X,
                    u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y));
            }
        }
        void Arc(Point center, double r, double fromDegrees, double toDegrees)
        {
            for (var i = 1; i <= CurveSamples; i++)
            {
                var a = (fromDegrees + (toDegrees - fromDegrees) * i / CurveSamples) * Math.PI / 180;
                points.Add(new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)));
            }
        }

        if (capsule)
        {
            // A detached rounded rectangle: fully round when compact, a softer large radius when open.
            var top = CapsuleGap;
            var bottom = top + height;
            var r = Math.Min(height / 2, 18 + 22 * progress);
            var right = x + width;
            Line(new Point(x + r, top));
            Line(new Point(right - r, top));
            Arc(new Point(right - r, top + r), r, -90, 0);
            Line(new Point(right, bottom - r));
            Arc(new Point(right - r, bottom - r), r, 0, 90);
            Line(new Point(x + r, bottom));
            Arc(new Point(x + r, bottom - r), r, 90, 180);
            Line(new Point(x, top + r));
            Arc(new Point(x + r, top + r), r, 180, 270);
            return points;
        }

        var shoulder = 18 + 22 * progress;
        var low = 18 + 20 * progress;
        var rr = x + width;
        var l = x;
        Line(new Point(l, 0));
        Line(new Point(rr, 0));
        Bezier(new Point(rr, 0), new Point(rr - shoulder, 0), new Point(rr - shoulder, shoulder * 0.5), new Point(rr - shoulder, shoulder));
        Line(new Point(rr - shoulder, height - low));
        Bezier(new Point(rr - shoulder, height - low), new Point(rr - shoulder, height), new Point(rr - shoulder - low * 0.5, height), new Point(rr - shoulder - low, height));
        Line(new Point(l + shoulder + low, height));
        Bezier(new Point(l + shoulder + low, height), new Point(l + shoulder + low * 0.5, height), new Point(l + shoulder, height), new Point(l + shoulder, height - low));
        Line(new Point(l + shoulder, shoulder));
        Bezier(new Point(l + shoulder, shoulder), new Point(l + shoulder, shoulder * 0.5), new Point(l + shoulder, 0), new Point(l, 0));
        return points;
    }

    // The polygon pushed outward by a hair, for the native window region: the region clips the drawn (anti-aliased) edge,
    // which would otherwise show as stair-steps.
    public static List<Point> Grown(List<Point> polygon, double amount)
    {
        var area = 0.0;
        for (var i = 0; i < polygon.Count; i++) { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count]; area += a.X * b.Y - b.X * a.Y; }
        var sign = area > 0 ? 1 : -1;
        var grown = new List<Point>(polygon.Count);
        for (var i = 0; i < polygon.Count; i++)
        {
            var prev = polygon[(i + polygon.Count - 1) % polygon.Count];
            var next = polygon[(i + 1) % polygon.Count];
            var p = polygon[i];
            static (double, double) Normal(Point a, Point b, double sign)
            {
                var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
                return length < 1e-9 ? (0, 0) : (sign * dy / length, -sign * dx / length);
            }
            var (n1x, n1y) = Normal(prev, p, sign);
            var (n2x, n2y) = Normal(p, next, sign);
            var nx = n1x + n2x; var ny = n1y + n2y; var n = Math.Sqrt(nx * nx + ny * ny);
            grown.Add(n < 1e-9 ? p : new Point(p.X + nx / n * amount, p.Y + ny / n * amount));
        }
        return grown;
    }
}
