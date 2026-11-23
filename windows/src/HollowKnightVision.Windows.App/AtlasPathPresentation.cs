using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

internal static class AtlasPathPresentation
{
    internal static ViewportRect? Draw(Canvas canvas, IReadOnlyList<AtlasPathStroke> strokes, int knightWidth,
        AtlasPoint origin, AtlasPathPoint? target, IReadOnlyList<AtlasPathPoint> route,
        ViewportSize? scale = null, AtlasPathHint? hint = null)
    {
        ViewportRect? bounds = null;
        var (scaleX, scaleY) = scale ?? new ViewportSize(1, 1);
        foreach (var stroke in strokes)
        {
            var brush = stroke.Kind switch
            {
                AtlasPathKind.Drop => Brushes.DarkOrange,
                AtlasPathKind.DoubleJump => Brushes.MediumVioletRed,
                _ => Brushes.Gold
            };
            Draw(stroke.A, stroke.B, brush);
            if (stroke.Kind != AtlasPathKind.Drop || stroke.Horizontal || stroke.B.Y - stroke.A.Y < 4) continue;
            var tip = new AtlasPathPoint(stroke.A.X, (stroke.A.Y + stroke.B.Y) / 2);
            var size = Math.Min(Math.Max(4, knightWidth / 2), (stroke.B.Y - stroke.A.Y) / 2);
            Draw(tip, new(tip.X - size, tip.Y + size), brush);
            Draw(tip, new(tip.X + size, tip.Y + size), brush);
        }

        if (target is { } endpoint)
        {
            var mark = Math.Max(4, knightWidth / 2);
            Draw(new(endpoint.X - mark, endpoint.Y), new(endpoint.X + mark, endpoint.Y), Brushes.White, 2);
            Draw(new(endpoint.X, endpoint.Y - mark), new(endpoint.X, endpoint.Y + mark), Brushes.White, 2);
        }
        if (route.Count >= 2)
        {
            var first = route[0];
            for (var i = 1; i < route.Count; i++)
            {
                var before = route[i - 1];
                var next = route[i];
                if (i + 1 < route.Count && SameDirection(before, next, route[i + 1])) continue;
                Draw(first, next, Brushes.White, 2);
                first = next;
            }
        }
        if (hint is { } guidance && guidance.Current != guidance.Next)
        {
            var from = guidance.Current;
            var tip = guidance.Next;
            Draw(from, tip, Brushes.White, 2);
            var length = Math.Min(Math.Max(3, knightWidth / 2), from.DistanceTo(tip));
            var x = Math.Sign(tip.X - from.X) * length;
            var y = Math.Sign(tip.Y - from.Y) * length;
            Draw(tip, new(tip.X - x + y / 2, tip.Y - y + x / 2), Brushes.White, 2);
            Draw(tip, new(tip.X - x - y / 2, tip.Y - y - x / 2), Brushes.White, 2);
        }
        return bounds;

        void Draw(AtlasPathPoint a, AtlasPathPoint b, Brush brush, double thickness = 1.5)
        {
            var x1 = a.X * scaleX + origin.X;
            var y1 = -a.Y * scaleY + origin.Y;
            var x2 = b.X * scaleX + origin.X;
            var y2 = -b.Y * scaleY + origin.Y;
            var span = new ViewportRect(Math.Min(x1, x2), Math.Min(y1, y2),
                Math.Max(1, Math.Abs(x1 - x2)), Math.Max(1, Math.Abs(y1 - y2)));
            bounds = bounds is { } current ? current.Union(span) : span;
            canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness,
                IsHitTestVisible = false
            });
        }
    }

    private static bool SameDirection(AtlasPathPoint a, AtlasPathPoint b, AtlasPathPoint c) =>
        Math.Sign(b.X - a.X) == Math.Sign(c.X - b.X) && Math.Sign(b.Y - a.Y) == Math.Sign(c.Y - b.Y);
}
