namespace HollowKnightVision.Windows.Core;

internal static class BotPathMotion
{
    internal static BotRoadHint Road(AtlasPathHint route, int height, double width, bool frontier)
    {
        var dx = route.FallSteering ?? route.Next.X - route.Current.X;
        var dy = route.FallSteering.HasValue ? -1 : route.Next.Y - route.Current.Y;
        var magnitude = Math.Max(1, Math.Sqrt((double)dx * dx + (double)dy * dy));
        return new(dx / magnitude, dy / magnitude, Math.Min(1, height / width),
            Math.Min(1, route.Remaining / (4 * width)), frontier);
    }

    internal static int ForwardPixels(AtlasPathHint old, AtlasPathPoint oldPosition, int best,
        AtlasPathPoint position, AtlasPathPoint projection, int remaining)
    {
        var horizontal = old.Next.X != old.Current.X;
        var direction = horizontal ? Math.Sign(old.Next.X - old.Current.X) : Math.Sign(old.Next.Y - old.Current.Y);
        var oldAxis = horizontal ? old.Current.X : old.Current.Y;
        var newAxis = horizontal ? projection.X : projection.Y;
        var physical = horizontal ? position.X - oldPosition.X : position.Y - oldPosition.Y;
        return Math.Max(0, Math.Min(best - remaining, Math.Min((newAxis - oldAxis) * direction, physical * direction)));
    }
}
