namespace HollowKnightVision.Windows.Core;

/// <summary>Requires repeated map-aligned evidence before a visual face can cut a route.</summary>
internal sealed class VisualPathWallHistory
{
    private sealed record Sample(double Time, IReadOnlyList<AtlasPathWall> Walls, IReadOnlyList<AtlasPathPlatform> Exposed);
    private const double WitnessDuration = .25;
    private readonly Queue<Sample> history = new();

    internal IReadOnlyList<AtlasPathWall> Observe(IReadOnlyList<AtlasPathWall> walls,
        IReadOnlyList<AtlasPathPlatform> exposed, double time)
    {
        if (!double.IsFinite(time)) return [];
        if (history.TryPeek(out _))
        {
            var previous = history.Last().Time;
            if (time == previous) return [];
            if (time < previous || time - previous > WitnessDuration) history.Clear();
        }

        // At any capture rate retain the first witness at least a quarter second old.
        while (history.Count >= 2 && time - history.ElementAt(1).Time >= WitnessDuration) history.Dequeue();
        if (history.Count == 120) history.Dequeue();
        history.Enqueue(new(time, walls, exposed));
        if (!Corroborated) return [];

        var result = new List<AtlasPathWall>();
        foreach (var wall in walls)
        {
            var common = wall;
            var contact = wall.ConfirmedByContact ? 1 : 0;
            foreach (var sample in history.Take(history.Count - 1))
            {
                var overlap = 0;
                AtlasPathWall best = default;
                foreach (var other in sample.Walls)
                {
                    var shared = Math.Min(common.High, other.High) - Math.Max(common.Low, other.Low);
                    if (Math.Abs(other.X - wall.X) > 3 || other.SolidSide != wall.SolidSide || shared <= overlap) continue;
                    best = other;
                    overlap = shared;
                }
                if (overlap == 0)
                {
                    common = common with { High = common.Low };
                    break;
                }
                common = common with { Low = Math.Max(common.Low, best.Low), High = Math.Min(common.High, best.High) };
                if (best.ConfirmedByContact) contact++;
            }
            if (common.Low < common.High)
                result.Add(common with { ConfirmedByContact = wall.ConfirmedByContact && contact >= 2 });
        }
        return result;
    }

    internal IEnumerable<AtlasPathPlatform> ExposedFloors(int height, int width)
    {
        if (history.Count < 2) yield break;
        var recent = history.TakeLast(2).ToArray();
        var walls = recent[0].Walls.Concat(recent[1].Walls).ToArray();
        foreach (var floor in recent[1].Exposed)
        foreach (var other in recent[0].Exposed)
        {
            if (Math.Abs(other.Y - floor.Y) > 2) continue;
            var left = Math.Max(other.Left, floor.Left);
            var right = Math.Min(other.Right, floor.Right);
            if (right - left <= width) continue;
            var body = floor.Y + height / 2;
            foreach (var x in walls.Where(wall => left < wall.X && wall.X < right && wall.Low <= body && body < wall.High)
                         .Select(wall => wall.X).Distinct().Order())
            {
                if (x - left > width) yield return new(left, x - 1, floor.Y);
                left = x + 1;
            }
            if (right - left > width) yield return new(left, right, floor.Y);
        }
    }

    internal IEnumerable<AtlasPathPlatform> ClearCrossings(AtlasPathWall wall, int height, int width)
    {
        if (!Corroborated) return [];
        var margin = Math.Max(2, width / 2);
        bool Crosses(AtlasPathPlatform floor) => floor.Left + margin <= wall.X && wall.X <= floor.Right - margin;
        return history.Last().Exposed.Where(floor => Crosses(floor) && floor.Y < wall.High && floor.Y + height > wall.Low
            && history.All(sample => sample.Exposed.Any(proof => Crosses(proof) && Math.Abs(proof.Y - floor.Y) <= 2)
                && !sample.Walls.Any(face => Math.Abs(face.X - wall.X) <= 3 && face.High > floor.Y && face.Low < floor.Y + height)));
    }

    internal void Reset() => history.Clear();

    private bool Corroborated => history.Count >= 4 && history.Last().Time - history.Peek().Time >= WitnessDuration;
}
