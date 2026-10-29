namespace HollowKnightVision.Windows.Core;

public sealed partial class GroundCameraSolver
{
    private static bool Excluded(IReadOnlyList<StencilRect> exclusions, double x, double y, double width, double height)
    {
        // This executes for every source/candidate pair. Avoid a capturing
        // delegate and iterator allocation for each GPU search sample.
        for (var i = 0; i < exclusions.Count; i++)
        {
            var rect = exclusions[i];
            if (rect.IsFinite && !rect.IsEmpty && rect.X < x + width
                && rect.Right > x && rect.Y < y + height && rect.Bottom > y) return true;
        }
        return false;
    }
    private static bool MatchesRow(GroundTextureLine[] lines, int y)
    {
        for (var i = 0; i < lines.Length; i++)
            if (Math.Abs((long)lines[i].Row - y) <= 3) return true;
        return false;
    }

    private Solution? SolveReference(byte[] pixels, int width, int height, GroundTextureLine[] lines,
        double timestamp, StencilRect[] exclusions, string matchSource, bool textureFallback = false)
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
        var radiusX = broadSearchEveryFrame ? Math.Min(width, maximumLocalSearchRadius) : Math.Min(width, (int)Math.Max(
            16,
            Math.Min(maximumLocalSearchRadius, Math.Ceiling(elapsed * 400))));
        var radiusY = broadSearchEveryFrame ? Math.Min(height, maximumLocalSearchRadius)
            : Math.Min(height, (int)Math.Max(32, Math.Min(64, Math.Ceiling(elapsed * 240))));
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
        if (hypotheses.Length == 0 && !textureFallback) return null;
        var patches = textureFallback ? MakeTexturePatches(prior, width, height) : MakePatches(prior, width, height, broadSearchEveryFrame);
        if (patches.Count < 3) return null;
        var dySequence = hypotheses
            .SelectMany(proposedY => Enumerable.Range(proposedY - 3, 7))
            .ToArray();
        var uniqueDys = dySequence.Distinct().ToArray();
        var searchSources = patches.Select(source => new GroundPatchSearchSource(
            source.X,
            source.Y,
            source.Pixels)).ToArray();
        var preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var searchCandidates = textureFallback
            ? BuildTextureCandidates(patches, exclusions, width, height, predictedX, predictedY, radiusX, radiusY)
            : BuildSearchCandidates(patches, searchLines, exclusions, width, height, uniqueDys, predictedX, radiusX);
        profiledPreparation += System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
        profiledReferenceSearches++;
        var searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var execution = GroundPatchMatcher.Shared.Search(
            searchSources,
            pixels,
            width,
            height,
            searchCandidates,
            matcherBackend, allowVerticalSpread: textureFallback);
        profiledSearch += System.Diagnostics.Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds;
        LastPatchSearchExecution = execution;
        if (execution.Results.Count == 0) return null;
        // Results retain unique-dy/candidate order. Repeating a dy adds only
        // identical scores, so two scans preserve the old stable ranking ties.
        var best = execution.Results[0];
        foreach (var candidate in execution.Results)
            if (candidate.Error < best.Error) best = candidate;
        if (textureFallback)
        {
            var peakStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var selected = SelectTexturePeak(patches, pixels, width, height, exclusions, execution.Results, best);
            profiledSubpixel += System.Diagnostics.Stopwatch.GetElapsedTime(peakStarted).TotalMilliseconds;
            if (selected is null) return null;
            best = selected.Value;
        }
        GroundPatchSearchResult? rival = null;
        foreach (var candidate in execution.Results)
            if ((Math.Abs(candidate.Dx - best.Dx) > 2 || Math.Abs(candidate.Dy - best.Dy) > 2)
                && (rival is null || candidate.Error < rival.Value.Error)) rival = candidate;
        var consensusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var consensus = broadSearchEveryFrame
            ? GroundPatchConsensus.Refine(patches, pixels, width, height, best.Dx, best.Dy, exclusions, allowVerticalSpread: textureFallback) : null;
        profiledConsensus += System.Diagnostics.Stopwatch.GetElapsedTime(consensusStarted).TotalMilliseconds;
        if (broadSearchEveryFrame && consensus is null) return null;
        if (consensus is not null) best = new(consensus.Dx, consensus.Dy, consensus.Error, consensus.Patches.Length);
        else if (rival is { } competing && competing.Error <= best.Error * 1.12 + .25) return null;
        if (!textureFallback && longRowOnly && (best.Support < 8 || best.Error > 6) && (best.Support < 12 || best.Error > 10)) return null;
        if (textureFallback && (best.Support < 4 || best.Error > 8)) return null;
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
        var subpixelStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        GroundSubpixelRegistration.Result? subpixel = null;
        if (subpixelEnabled || consensus is not null || (keyframeSubpixelEnabled && promotes))
        {
            var refinementPatches = new List<GroundSubpixelRegistration.Patch>();
            foreach (var source in consensus?.Patches ?? patches.ToArray())
            {
                var x = source.X + best.Dx; var y = source.Y + best.Dy;
                const int margin = 5;
                if (x - margin < 0 || y - margin < 0 || x + 16 + margin >= width || y + 12 + margin >= height
                    || Excluded(exclusions, x - margin, y - margin, 16 + 2 * margin, 12 + 2 * margin)
                    || (!textureFallback && !MatchesRow(searchLines, y))) continue;
                var values = ReadPatch(pixels, width, height, x, y);
                if (values is not null && PatchError(source, new(x, y, values)) <= 18)
                    refinementPatches.Add(new(source.X, source.Y, source.Pixels));
            }
            subpixel = broadSearchEveryFrame && fineParallaxConsensus
                ? GroundSubpixelRegistration.RefineConsensus(refinementPatches, pixels, width, height, best.Dx, best.Dy, symmetricSubpixel ? prior.Pixels : null) : null;
            subpixel ??= broadSearchEveryFrame && symmetricSubpixel
                ? GroundSubpixelRegistration.RefineSymmetric(refinementPatches, prior.Pixels, pixels, width, height, best.Dx, best.Dy)
                : GroundSubpixelRegistration.Refine(refinementPatches, pixels, width, height, best.Dx, best.Dy);
        }
        profiledSubpixel += System.Diagnostics.Stopwatch.GetElapsedTime(subpixelStarted).TotalMilliseconds;
        var matched = new CameraVector(best.Dx + (subpixel?.Offset.X ?? 0), best.Dy + (subpixel?.Offset.Y ?? 0));
        var scale = MotionScale(matchSource, matched);
        var next = Add(prior.Position, new(matched.X * scale.X, matched.Y * scale.Y));
        var translation = Subtract(next, imagePosition);
        var expected = new CameraVector(velocity.X * Math.Min(elapsed, .1), velocity.Y * Math.Min(elapsed, .1));
        var uniqueLocalTextureMatch = broadSearchEveryFrame && prior.PoseTrusted && consensus is not null
            && best.Support is >= 4 and < 10 && best.Error <= 10
            && (Math.Abs(translation.X) > 16 || Math.Abs(translation.Y) > 32)
            && VerifySparseTranslation(consensus.Patches, pixels, width, height, exclusions, best.Dx, best.Dy);
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
        if (prior.PoseTrusted) latestVerifiedReference = currentReference;
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
        return new(translation, best.Support, best.Error, prior.PoseTrusted, prior.Timestamp, prior.Position, matched, matchSource)
        { IndependentTextureConsensus = consensus is not null && prior.PoseTrusted, UniqueLocalTextureMatch = uniqueLocalTextureMatch };
    }

    // Row matching and mask vertical overlap do not depend on dx. Compute
    // those once per source/row, then enumerate exactly the same legal samples.
    // Each bit is a source patch; ascending set bits preserve original order.
    internal List<GroundPatchSearchCandidate> BuildSearchCandidates(
        IReadOnlyList<Patch> patches, GroundTextureLine[] lines, StencilRect[] exclusions,
        int width, int height, int[] dys, int predictedX, int radiusX, bool allTextureRows = false)
    {
        if (patches.Count > 32 || radiusX < 0) throw new ArgumentOutOfRangeException(nameof(patches));
        var columns = checked(radiusX * 2 + 1);
        var firstDx = checked(predictedX - radiusX);
        var candidates = PrepareCandidates(checked(dys.Length * columns));
        if (searchValidityBuffer.Length < columns) searchValidityBuffer = new uint[columns];
        sampleLayout.Count = patches.Count;
        for (var i = 0; i < patches.Count; i++)
        { sampleLayout.X[i] = patches[i].X; sampleLayout.Y[i] = patches[i].Y; }
        Span<int> targetRows = stackalloc int[32];
        foreach (var dy in dys)
        {
            var validity = searchValidityBuffer.AsSpan(0, columns);
            validity.Clear();
            for (var sourceIndex = 0; sourceIndex < patches.Count; sourceIndex++)
            {
                var source = patches[sourceIndex];
                var y = (long)source.Y + dy;
                if (y < 0 || y + GroundPixels.PatchHeight > height) continue;
                var first = (int)Math.Clamp(-(long)source.X - firstDx, 0, columns);
                var end = (int)Math.Clamp((long)width - GroundPixels.PatchWidth - source.X - firstDx + 1, 0, columns);
                var bit = 1u << sourceIndex;
                for (var column = first; column < end; column++) validity[column] |= bit;
                targetRows[sourceIndex] = allTextureRows || MatchesRow(lines, (int)y) ? checked((int)y * width + source.X) : -1;
                foreach (var rect in exclusions)
                {
                    if (!rect.IsFinite || rect.IsEmpty || rect.Y >= y + GroundPixels.PatchHeight || rect.Bottom <= y) continue;
                    // Integer x intersects iff x > rect.X - patchWidth and x < rect.Right.
                    // Clamp doubles before conversion, including offscreen extreme masks.
                    var left = (int)Math.Clamp(Math.Floor(rect.X - GroundPixels.PatchWidth - source.X - firstDx) + 1, first, end);
                    var right = (int)Math.Clamp(Math.Ceiling(rect.Right - source.X - firstDx), first, end);
                    for (var column = left; column < right; column++) validity[column] &= ~bit;
                }
            }
            for (var column = 0; column < columns; column++)
            {
                var dx = checked(firstDx + column);
                var sampleOffset = candidates.Count * 32;
                var sampleCount = 0;
                var bits = validity[column];
                var eligible = bits; uint matched = 0;
                while (bits != 0)
                {
                    var sourceIndex = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var row = targetRows[sourceIndex];
                    if (row >= 0) matched |= 1u << sourceIndex;
                    searchSampleBuffer[sampleOffset + sampleCount++] = new(sourceIndex, row >= 0 ? row + dx : null);
                }
                var range = searchSampleRanges[candidates.Count];
                range.Count = sampleCount;
                range.Width = width; range.Height = height; range.Sources = patches.Count;
                range.EligibleSources = eligible; range.MatchedSources = matched;
                range.DisplacementX = dx; range.DisplacementY = dy;
                candidates.Add(new(dx, dy, range));
            }
        }
        return candidates;
    }

    private static List<Patch> MakePatches(Reference prior, int width, int height, bool dense = false)
    {
        var groups = new List<List<Patch>>();
        foreach (var old in prior.Lines)
        {
            if (old.Row < 0 || (long)old.Row + 12 > height) continue;
            var count = Math.Max(0, (old.EndX - old.StartX + 1) / 16);
            var budget = dense ? Math.Max(8, 32 / Math.Max(1, prior.Lines.Length)) : 8;
            var stride = Math.Max(1, (int)Math.Ceiling((double)count / budget));
            var group = new List<Patch>();
            for (var sample = 0; sample < (dense ? Math.Min(count, budget) : (count + stride - 1) / stride); sample++)
            {
                var cell = dense ? sample * count / Math.Min(count, budget) : sample * stride;
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
