namespace HollowKnightVision.Windows.Core;

public readonly record struct KnightTerrainContact(CleanFloorLine? Floor, DetectedWall? Wall)
{
    public AtlasPoint? MeasuredFoot { get; init; }
}

/// <summary>Infers a wall from sustained input and camera-compensated contact.</summary>
public sealed class KnightWallContact
{
    private readonly record struct Sample(double Time, AtlasPoint Foot, int Direction, bool Partial);
    private readonly List<Sample> samples = [];
    private (double X, int Direction)? contact;

    public void Reset()
    {
        samples.Clear();
        contact = null;
    }

    public DetectedWall? Observe(KnightFootLocator.Measurement? actor, AtlasPoint? camera, int direction,
        double time, int width, int height, IReadOnlyList<DetectedWall> walls)
    {
        if (direction is not (-1 or 1) || actor is not { } measurement || camera is not { } pose
            || !double.IsFinite(time) || width <= 0 || height <= 0)
        {
            Reset();
            return null;
        }
        var foot = measurement.Foot;
        var world = new AtlasPoint(foot.X + pose.X, foot.Y - pose.Y);
        if (!double.IsFinite(world.X) || !double.IsFinite(world.Y))
        {
            Reset();
            return null;
        }
        if (samples.Count > 0 && (time <= samples[^1].Time || time - samples[^1].Time > .13))
            Reset();
        if (contact?.Direction != direction) contact = null;
        samples.Add(new(time, world, direction, measurement.Partial));
        samples.RemoveAll(sample => time - sample.Time > .55);
        if (samples.Count > 96) samples.RemoveRange(0, samples.Count - 96);

        var recent = samples.FindIndex(sample => time - sample.Time <= .24);
        if (recent < 0 || samples.Count - recent < 3 || time - samples[recent].Time < .16)
            return null;
        var first = samples[recent].Foot;
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        var partials = 0;
        for (var i = recent; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.Direction != direction) return null;
            minX = Math.Min(minX, sample.Foot.X);
            maxX = Math.Max(maxX, sample.Foot.X);
            minY = Math.Min(minY, sample.Foot.Y);
            maxY = Math.Max(maxY, sample.Foot.Y);
            if (sample.Partial) partials++;
        }
        var scale = width / 640d;
        if (maxX - minX > 3.5 * scale) return null;

        var approached = false;
        for (var i = recent - 1; i >= 0 && samples[i].Direction == direction; i--)
        {
            if ((first.X - samples[i].Foot.X) * direction < 4 * scale) continue;
            approached = true;
            break;
        }
        var face = foot.X + direction * 3 * scale;
        var torso = foot.Y - 14 * scale;
        var adjacent = walls.Any(wall => wall.SolidOnRight == (direction > 0)
            && Math.Abs(wall.X - face) <= 8 * scale && wall.Top <= torso + 15 * scale
            && wall.Bottom >= torso - 15 * scale);
        var heldContact = contact is { } prior && Math.Abs(prior.X - world.X) <= 3 * scale;
        if (!approached && !adjacent && !heldContact && partials < 3 && maxY - minY < 8 * scale)
            return null;

        var x = TerrainWallDetector.Round(face);
        var top = Math.Max(0, TerrainWallDetector.Round(minY + pose.Y - 19 * scale));
        var bottom = Math.Min(height - 1, TerrainWallDetector.Round(maxY + pose.Y - 7 * scale));
        if ((uint)x >= (uint)width || top >= bottom) return null;
        if (!heldContact) contact = (world.X, direction);
        return new(x, top, bottom, direction > 0);
    }
}
