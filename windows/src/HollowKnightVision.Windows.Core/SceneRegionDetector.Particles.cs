namespace HollowKnightVision.Windows.Core;

public sealed partial class SceneRegionDetector
{
    private const int ParticleSampleWidth = 160;
    private readonly List<byte[]> intensityHistory = [];
    private readonly List<ParticleTrack> particleTracks = [];
    private sealed record ParticlePeak(int X, int Y, int Score, int Intensity);
    private sealed record ParticleDetection(StencilRect Rect, int Intensity);
    private sealed record ParticleTrack(StencilRect Rect, StencilRect WorldRect, SourcePoint Velocity,
        int Hits, int MovingHits, int BrightnessChangeHits, int LastIntensity, int Misses);

    private IReadOnlyList<StencilRect> DetectParticles(BgraFrame image, IReadOnlyList<StencilRect> exclusions,
        SourcePoint worldOffset)
    {
        var height = Math.Max(1, Round((double)image.Height * ParticleSampleWidth / image.Width));
        var pixels = Sample(image, ParticleSampleWidth, height);
        var intensity = new byte[ParticleSampleWidth * height];
        var palePeak = new bool[intensity.Length];
        for (var i = 0; i < intensity.Length; i++)
        {
            var r = pixels[i * 4]; var g = pixels[i * 4 + 1]; var b = pixels[i * 4 + 2];
            intensity[i] = (byte)Math.Min(255, r * .2126 + g * .7152 + b * .0722);
            var brightest = Math.Max(r, Math.Max(g, b)); var darkest = Math.Min(r, Math.Min(g, b));
            palePeak[i] = brightest >= 108 && brightest - darkest <= 105;
        }
        intensityHistory.Add(intensity);
        if (intensityHistory.Count > 6) intensityHistory.RemoveRange(0, intensityHistory.Count - 6);
        if (intensityHistory.Count < 3) return UpdateParticleTracks([], worldOffset);
        var scaleX = (double)image.Width / ParticleSampleWidth; var scaleY = (double)image.Height / height;
        var peaks = new List<ParticlePeak>();
        for (var y = 2; y < height - 2; y++) for (var x = 2; x < ParticleSampleWidth - 2; x++)
        {
            var i = y * ParticleSampleWidth + x;
            if (exclusions.Any(rect => Contains(rect, (x + .5) * scaleX, (y + .5) * scaleY))) continue;
            var low = 255; var high = 0;
            foreach (var history in intensityHistory) { low = Math.Min(low, history[i]); high = Math.Max(high, history[i]); }
            var current = (int)intensity[i]; var previous = intensityHistory[^2][i];
            if (!palePeak[i] || current < 108 || high - low < 18 || (Math.Abs(current - previous) < 4 && high - low < 28)) continue;
            var ringTotal = 0; var ringCount = 0; var lowerNeighbors = 0; var localMaximum = true;
            for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var value = intensity[(y + dy) * ParticleSampleWidth + x + dx];
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == 1)
                { if (value <= current - 3) lowerNeighbors++; if (value > current) localMaximum = false; }
                else { ringTotal += value; ringCount++; }
            }
            var centerSurround = current - ringTotal / Math.Max(1, ringCount);
            var axialAsymmetry = Math.Abs(intensity[i - 1] - intensity[i + 1])
                + Math.Abs(intensity[i - ParticleSampleWidth] - intensity[i + ParticleSampleWidth]);
            if (localMaximum && lowerNeighbors >= 5 && centerSurround >= 14 && axialAsymmetry <= 100)
                peaks.Add(new(x, y, centerSurround + high - low, current));
        }
        var selected = new List<ParticlePeak>();
        foreach (var peak in peaks.OrderByDescending(item => item.Score))
        {
            if (selected.Count >= 32) break;
            if (selected.Any(item => Distance(item.X - peak.X, item.Y - peak.Y) < 3)) continue;
            selected.Add(peak);
        }
        var extent = new StencilRect(0, 0, image.Width, image.Height);
        var detections = new List<ParticleDetection>();
        foreach (var peak in selected)
        {
            const double radius = 3.5;
            var clipped = new StencilRect((peak.X + .5 - radius) * scaleX, (peak.Y + .5 - radius) * scaleY,
                radius * 2 * scaleX, radius * 2 * scaleY).Intersect(extent);
            if (clipped.IsEmpty) continue;
            var integral = new StencilRect(Math.Floor(clipped.X), Math.Floor(clipped.Y),
                Math.Ceiling(clipped.Right) - Math.Floor(clipped.X), Math.Ceiling(clipped.Bottom) - Math.Floor(clipped.Y));
            detections.Add(new(integral, peak.Intensity));
        }
        return UpdateParticleTracks(detections, worldOffset);
    }

    private IReadOnlyList<StencilRect> UpdateParticleTracks(IReadOnlyList<ParticleDetection> detections, SourcePoint worldOffset)
    {
        var matched = new HashSet<int>();
        foreach (var detection in detections)
        {
            var world = detection.Rect.Offset(worldOffset.X, worldOffset.Y);
            var nearest = -1; var nearestDistance = double.PositiveInfinity;
            for (var i = 0; i < particleTracks.Count; i++)
            {
                if (matched.Contains(i)) continue;
                var track = particleTracks[i];
                var distance = Distance(track.WorldRect.MidX + track.Velocity.X - world.MidX,
                    track.WorldRect.MidY + track.Velocity.Y - world.MidY);
                if (distance < nearestDistance) { nearest = i; nearestDistance = distance; }
            }
            if (nearest >= 0 && nearestDistance < 48)
            {
                var track = particleTracks[nearest];
                var dx = world.MidX - track.WorldRect.MidX; var dy = world.MidY - track.WorldRect.MidY;
                particleTracks[nearest] = track with
                {
                    Velocity = new(track.Velocity.X * .55 + dx * .45, track.Velocity.Y * .55 + dy * .45),
                    Rect = detection.Rect, WorldRect = world, Hits = track.Hits + 1,
                    MovingHits = track.MovingHits + (Distance(dx, dy) >= .75 ? 1 : 0),
                    BrightnessChangeHits = track.BrightnessChangeHits + (Math.Abs(detection.Intensity - track.LastIntensity) >= 14 ? 1 : 0),
                    LastIntensity = detection.Intensity, Misses = 0
                };
                matched.Add(nearest);
            }
            else if (particleTracks.Count < 64)
            {
                particleTracks.Add(new(detection.Rect, world, default, 1, 0, 0, detection.Intensity, 0));
                matched.Add(particleTracks.Count - 1);
            }
        }
        for (var i = 0; i < particleTracks.Count; i++)
        {
            if (matched.Contains(i)) continue;
            var track = particleTracks[i];
            var world = track.WorldRect.Offset(track.Velocity.X, track.Velocity.Y);
            particleTracks[i] = track with
            {
                WorldRect = world, Rect = world.Offset(-worldOffset.X, -worldOffset.Y),
                Velocity = new(track.Velocity.X * .75, track.Velocity.Y * .75), Misses = track.Misses + 1
            };
        }
        particleTracks.RemoveAll(track => track.Misses > 2);
        return particleTracks.Where(track => (track.Hits == 1 && track.Misses == 0)
            || (track.Hits >= 2 && (track.MovingHits >= 1 || track.BrightnessChangeHits >= 1) && track.Misses <= 1))
            .Select(track => track.Rect).ToArray();
    }
}
