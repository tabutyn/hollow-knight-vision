namespace HollowKnightVision.Windows.Core;

public sealed partial class GroundCameraSolver
{
    private sealed record Candidate(int Dx, int Dy, double Error, int Support);
    private static bool Excluded(IReadOnlyList<StencilRect> exclusions, double x, double y, double width, double height) =>
        exclusions.Any(rect => rect.IsFinite && !rect.IsEmpty && rect.X < x + width && rect.Right > x && rect.Y < y + height && rect.Bottom > y);

    private Solution? SolveReference(byte[] pixels, int width, int height, GroundTextureLine[] lines,
        double timestamp, StencilRect[] exclusions, string matchSource)
    {
        if (!Valid(pixels, width, height, timestamp)) return null;
        if (frameWidth != width || frameHeight != height) Reset();
        frameWidth = width; frameHeight = height; Seeded = false;
        if (reference is not { } prior)
        {
            if (lines.Length == 0) return null;
            reference = new(pixels, lines, exclusions, timestamp, CameraVector.Zero);
            recentReference = reference; spatialReferences = [reference]; lastAcceptedTimestamp = timestamp; Seeded = true;
            return new(CameraVector.Zero, 0, 0);
        }
        var elapsed = timestamp - (lastAcceptedTimestamp ?? prior.Timestamp);
        if (elapsed <= 0) return null;
        var prediction = new CameraVector(imagePosition.X - prior.Position.X + velocity.X * Math.Min(elapsed, .1),
            imagePosition.Y - prior.Position.Y + velocity.Y * Math.Min(elapsed, .1));
        if (!Finite(prediction) || Math.Abs(prediction.X) > int.MaxValue - 1024 || Math.Abs(prediction.Y) > int.MaxValue - 1024) return null;
        var predictedX = (int)Math.Round(prediction.X, MidpointRounding.AwayFromZero);
        var predictedY = (int)Math.Round(prediction.Y, MidpointRounding.AwayFromZero);
        var radiusX = Math.Min(width, (int)Math.Max(16, Math.Min(96, Math.Ceiling(elapsed * 400))));
        var radiusY = Math.Min(height, (int)Math.Max(32, Math.Min(64, Math.Ceiling(elapsed * 240))));
        var searchLines = lines.Length == 0 ? prior.Lines.Select(line =>
            new GroundTextureLine(line.Row + predictedY, line.StartX + predictedX, line.EndX + predictedX)).ToArray() : lines;
        var votes = new Dictionary<int, int>();
        foreach (var old in prior.Lines)
            foreach (var line in searchLines)
            {
                var dy = line.Row - old.Row;
                var overlap = Math.Min(old.EndX + predictedX, line.EndX) - Math.Max(old.StartX + predictedX, line.StartX) + 1;
                if (overlap >= 32) votes[dy] = votes.GetValueOrDefault(dy) + overlap;
            }
        var plausible = votes.Where(pair => Math.Abs((long)pair.Key - predictedY) <= radiusY || Math.Abs((long)pair.Key) <= radiusY).ToArray();
        var longRowOnly = plausible.Length == 0;
        if (longRowOnly) plausible = votes.Where(pair => Math.Abs((long)pair.Key) <= 96 && pair.Value >= 256).ToArray();
        var hypotheses = plausible.OrderByDescending(pair => pair.Value).ThenBy(pair => Math.Abs((long)pair.Key - predictedY)).Take(4).Select(pair => pair.Key).ToArray();
        if (hypotheses.Length == 0) return null;
        var patches = MakePatches(prior, width, height);
        if (patches.Count < 3) return null;
        var candidates = new List<Candidate>();
        var verticalCache = new Dictionary<int, Candidate[]>();
        foreach (var proposedY in hypotheses)
            for (var dy = proposedY - 3; dy <= proposedY + 3; dy++)
            {
                if (verticalCache.TryGetValue(dy, out var cached)) { candidates.AddRange(cached); continue; }
                var firstCandidate = candidates.Count;
                var targetCache = new Dictionary<int, Patch>(); var unavailable = new HashSet<int>();
                for (var dx = predictedX - radiusX; dx <= predictedX + radiusX; dx++)
                {
                    var errors = new List<(int X, double Error)>();
                    foreach (var source in patches)
                    {
                        var x = source.X + dx; var y = source.Y + dy;
                        if (x < 0 || (long)x + 16 > width || y < 0 || (long)y + 12 > height || Excluded(exclusions, x, y, 16, 12)) continue;
                        if (!searchLines.Any(line => Math.Abs((long)line.Row - y) <= 3)) { errors.Add((source.X, 32)); continue; }
                        var key = y * width + x;
                        if (!targetCache.TryGetValue(key, out var target))
                        {
                            if (unavailable.Contains(key)) { errors.Add((source.X, 32)); continue; }
                            var sampled = ReadPatch(pixels, width, height, x, y);
                            if (sampled is null) { unavailable.Add(key); errors.Add((source.X, 32)); continue; }
                            target = new(x, y, sampled); targetCache.Add(key, target);
                        }
                        errors.Add((source.X, PatchError(source, target)));
                    }
                    if (errors.Count < 3) continue;
                    var inliers = errors.Where(value => value.Error <= 18).ToArray();
                    if (inliers.Length < 3 || inliers.Length * 2 <= errors.Count || inliers.Max(value => value.X) - inliers.Min(value => value.X) < 32) continue;
                    candidates.Add(new(dx, dy, errors.Sum(value => Math.Min(32, value.Error)) / errors.Count, inliers.Length));
                }
                // Retain duplicate candidates and order, just like the Mac scan.
                verticalCache.Add(dy, candidates.Skip(firstCandidate).ToArray());
            }
        var ranked = candidates.OrderBy(value => value.Error).ToArray();
        if (ranked.Length == 0) return null;
        var best = ranked[0];
        var rival = ranked.FirstOrDefault(value => Math.Abs(value.Dx - best.Dx) > 2 || Math.Abs(value.Dy - best.Dy) > 2);
        if (rival is not null && rival.Error <= best.Error * 1.12 + .25) return null;
        if (longRowOnly && (best.Support < 8 || best.Error > 6) && (best.Support < 12 || best.Error > 10)) return null;
        if (GroundPoseAcceptancePolicy.Rejects(best.Support, best.Error)) return null;
        var coarseDisplacement = new CameraVector(best.Dx, best.Dy);
        var coarseScale = MotionScale(matchSource, coarseDisplacement);
        var coarseNext = Add(prior.Position, new(coarseDisplacement.X * coarseScale.X, coarseDisplacement.Y * coarseScale.Y));
        var cadenceReference = prior.PoseTrusted ? recentReference : provisionalReference;
        var coarseAge = timestamp - (cadenceReference?.Timestamp ?? double.NegativeInfinity);
        var coarseTravel = cadenceReference is null ? double.PositiveInfinity : Length(Subtract(cadenceReference.Position, coarseNext));
        var reachesCadence = GroundRecentReferenceCadence.ShouldRefresh(coarseAge, coarseTravel)
            || Math.Abs(coarseNext.X - prior.Position.X) > width * .18 || Math.Abs(coarseNext.Y - prior.Position.Y) > height * .12;
        var promotes = reachesCadence && GroundReferencePromotionPolicy.Permits(best.Support, best.Error);
        GroundSubpixelRegistration.Result? subpixel = null;
        if (subpixelEnabled || (keyframeSubpixelEnabled && promotes))
        {
            var refinementPatches = new List<GroundSubpixelRegistration.Patch>();
            foreach (var source in patches)
            {
                var x = source.X + best.Dx; var y = source.Y + best.Dy;
                const int margin = 5;
                if (x - margin < 0 || y - margin < 0 || x + 16 + margin >= width || y + 12 + margin >= height
                    || Excluded(exclusions, x - margin, y - margin, 16 + 2 * margin, 12 + 2 * margin)
                    || !searchLines.Any(line => Math.Abs((long)line.Row - y) <= 3)) continue;
                var values = ReadPatch(pixels, width, height, x, y);
                if (values is not null && PatchError(source, new(x, y, values)) <= 18)
                    refinementPatches.Add(new(source.X, source.Y, source.Pixels));
            }
            subpixel = GroundSubpixelRegistration.Refine(refinementPatches, pixels, width, height, best.Dx, best.Dy);
        }
        var matched = new CameraVector(best.Dx + (subpixel?.Offset.X ?? 0), best.Dy + (subpixel?.Offset.Y ?? 0));
        var scale = MotionScale(matchSource, matched);
        var next = Add(prior.Position, new(matched.X * scale.X, matched.Y * scale.Y));
        var translation = Subtract(next, imagePosition);
        var expected = new CameraVector(velocity.X * Math.Min(elapsed, .1), velocity.Y * Math.Min(elapsed, .1));
        if (GroundPoseInnovationPolicy.Rejects(best.Support, best.Error, Length(Subtract(translation, expected)))) return null;
        if (cadenceReference is null || !cadenceReference.Pixels.AsSpan().SequenceEqual(pixels))
            velocity = new(velocity.X * .5 + translation.X / elapsed * .5, velocity.Y * .5 + translation.Y / elapsed * .5);
        imagePosition = next; lastAcceptedTimestamp = timestamp;
        var stabilized = searchLines.Select(line =>
        {
            var rows = prior.Lines.Select(old => old.Row + best.Dy).ToArray();
            var nearest = rows.Length == 0 ? line.Row : rows.MinBy(row => Math.Abs((long)row - line.Row));
            return line with { Row = Math.Abs((long)nearest - line.Row) <= 3 ? nearest : line.Row };
        }).ToArray();
        var currentReference = new Reference(pixels, stabilized, exclusions, timestamp, next, prior.PoseTrusted);
        var recentAge = timestamp - (cadenceReference?.Timestamp ?? double.NegativeInfinity);
        var recentTravel = cadenceReference is null ? double.PositiveInfinity : Length(Subtract(cadenceReference.Position, next));
        var promotionPermitted = GroundReferencePromotionPolicy.Permits(best.Support, best.Error);
        if (promotionPermitted && GroundRecentReferenceCadence.ShouldRefresh(recentAge, recentTravel))
        {
            if (prior.PoseTrusted) recentReference = currentReference;
            else provisionalReference = currentReference;
        }
        var stationaryRefresh = Length(translation) <= .5 && timestamp - prior.Timestamp >= 1 && best.Support >= 6 && best.Error <= 8;
        if (prior.PoseTrusted && promotionPermitted && (Math.Abs(next.X - prior.Position.X) > width * .18
            || Math.Abs(next.Y - prior.Position.Y) > height * .12 || stationaryRefresh))
        {
            reference = currentReference;
            var nearest = NearestSpatial(next);
            if (nearest >= 0 && Length(Subtract(spatialReferences[nearest].Position, next)) <= 2)
            {
                // Nearby is not identical: assigning fresh pixels to a nearby
                // old coordinate would invent motion at the next handoff.
                if (Length(Subtract(spatialReferences[nearest].Position, next)) < .000001) spatialReferences[nearest] = currentReference;
            }
            else AppendSpatial(currentReference);
        }
        return new(translation, best.Support, best.Error, prior.PoseTrusted, prior.Timestamp, prior.Position, matched, matchSource);
    }

    private static List<Patch> MakePatches(Reference prior, int width, int height)
    {
        var groups = new List<List<Patch>>();
        foreach (var old in prior.Lines)
        {
            if (old.Row < 0 || (long)old.Row + 12 > height) continue;
            var count = Math.Max(0, (old.EndX - old.StartX + 1) / 16);
            var stride = Math.Max(1, (int)Math.Ceiling((double)count / 8));
            var group = new List<Patch>();
            for (var cell = 0; cell < count; cell += stride)
            {
                var x = old.StartX + cell * 16;
                if (!(old.EvidenceRanges ?? [new GroundTextureSpan(old.StartX, old.EndX)]).Any(range => range.Start <= x + 15 && range.EndInclusive >= x)
                    || Excluded(prior.Exclusions, x, old.Row, 16, 12)) continue;
                var values = ReadPatch(prior.Pixels, width, height, x, old.Row);
                if (values is null || values.Max() - values.Min() < 12) continue;
                group.Add(new(x, old.Row, values));
            }
            if (group.Count > 0) groups.Add(group);
        }
        var patches = new List<Patch>(); var offset = 0;
        while (patches.Count < 32)
        {
            var added = false;
            foreach (var group in groups.Where(group => offset < group.Count))
            { patches.Add(group[offset]); added = true; if (patches.Count == 32) break; }
            if (!added) break;
            offset++;
        }
        return patches;
    }
}
