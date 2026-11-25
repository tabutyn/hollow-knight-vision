using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class GameplayPlacementGeometry
{
    internal const int MaximumDashes = 4096;
    internal const double StrokeWidth = 1;

    internal readonly record struct Dash(ViewportPoint Start, ViewportPoint End, bool Estimated);

    internal static List<Dash> Build(LiveWorldPlacementProjection placement,
        Func<AtlasPoint, ViewportPoint> project, ViewportSize viewport, bool showsEstimatedOutlines)
    {
        List<Dash> dashes = [];
        if (!viewport.IsUsable) return dashes;

        foreach (var doorway in placement.Doorways)
        {
            if (dashes.Count == MaximumDashes) return dashes;
            var start = project(doorway.Start);
            var end = project(doorway.End);
            if (Math.Abs(end.X - start.X) > Math.Abs(end.Y - start.Y))
                Append(start, end, estimated: false);
        }

        if (!showsEstimatedOutlines) return dashes;
        foreach (var footprint in placement.EstimatedFootprints)
        {
            if (dashes.Count == MaximumDashes) break;
            if (!(footprint.Width > 0 && footprint.Height > 0)) continue;
            var topLeft = project(new(footprint.X, footprint.Y + footprint.Height));
            var bottomRight = project(new(footprint.X + footprint.Width, footprint.Y));
            if (!topLeft.IsFinite || !bottomRight.IsFinite) continue;
            var topRight = new ViewportPoint(bottomRight.X, topLeft.Y);
            var bottomLeft = new ViewportPoint(topLeft.X, bottomRight.Y);
            Append(topLeft, topRight, estimated: true);
            Append(topRight, bottomRight, estimated: true);
            Append(bottomRight, bottomLeft, estimated: true);
            Append(bottomLeft, topLeft, estimated: true);
        }
        return dashes;

        void Append(ViewportPoint start, ViewportPoint end, bool estimated)
        {
            if (dashes.Count == MaximumDashes || !start.IsFinite || !end.IsFinite) return;
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;

            // Clip to the screen, including the stroke margin, before counting dashes.
            double first = 0, last = 1;
            if (!Clip(-dx, start.X + StrokeWidth, ref first, ref last)
                || !Clip(dx, viewport.Width + StrokeWidth - start.X, ref first, ref last)
                || !Clip(-dy, start.Y + StrokeWidth, ref first, ref last)
                || !Clip(dy, viewport.Height + StrokeWidth - start.Y, ref first, ref last)) return;
            var a = new ViewportPoint(start.X + first * dx, start.Y + first * dy);
            var b = new ViewportPoint(start.X + last * dx, start.Y + last * dy);
            dx = b.X - a.X;
            dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length <= 0) return;

            var count = (int)Math.Min(64, Math.Ceiling(length / 12));
            for (var index = 0; index < count && dashes.Count < MaximumDashes; index++)
            {
                var from = (double)index / count;
                var to = (index + .6) / count;
                dashes.Add(new(new(a.X + from * dx, a.Y + from * dy),
                    new(a.X + to * dx, a.Y + to * dy), estimated));
            }
        }
    }

    private static bool Clip(double direction, double distance, ref double first, ref double last)
    {
        if (direction == 0) return distance >= 0;
        var t = distance / direction;
        if (direction < 0) first = Math.Max(first, t);
        else last = Math.Min(last, t);
        return first <= last;
    }
}
