namespace HollowKnightVision.Windows.Core;

/// <summary>A compact appearance record searchable without a pose prior.</summary>
public sealed record GlobalFeatureReference(
    Guid Id,
    AtlasPoint CameraPose,
    double SolveWidth,
    int SourceWidth,
    int SourceHeight,
    int SampleWidth,
    int SampleHeight,
    IReadOnlyList<GlobalFeatureReference.Feature> Features)
{
    public sealed record Feature(
        int Id,
        AtlasPoint SamplePoint,
        IReadOnlyList<byte> Descriptor,
        int Strength,
        double Depth,
        double DepthConfidence);
}

public sealed record GlobalFeatureMatch(
    Guid ReferenceId,
    AtlasPoint ProposedCameraPose,
    AtlasPoint MeasuredTranslation,
    IReadOnlyList<int> InlierReferenceFeatureIds,
    IReadOnlyList<int> InlierCurrentFeatureIndices,
    double Residual,
    double Confidence,
    double AmbiguityRatio);

/// <summary>Searches keyframes by local appearance without accepting a pose prior.</summary>
public sealed class GlobalFeatureMatcher
{
    private const int SampleWidth = 160;
    private const int MaximumFeatures = 96;
    private const int MaximumReferences = 32;
    private const double ConsensusRadius = 3;
    private const double AmbiguityLimit = 0.80;

    private int referenceScanOffset;
    private readonly Dictionary<Guid, AmbiguityCacheEntry> ambiguityByReferenceId = [];

    public int LastSelfPlacementPairChecks { get; private set; }
    public bool LastSelfPlacementWasAmbiguous { get; private set; }

    public GlobalFeatureReference? MakeReference(
        BgraFrame frame,
        AtlasPoint cameraPose,
        double solveWidth,
        Guid? id = null,
        double depth = 1,
        double depthConfidence = 0,
        IReadOnlyList<StencilRect>? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var exclusions = excluding ?? [];
        if (!double.IsFinite(solveWidth) || solveWidth <= 0
            || !Finite(cameraPose)
            || !double.IsFinite(depth) || depth <= 0
            || !double.IsFinite(depthConfidence) || depthConfidence is < 0 or > 1
            || exclusions.Any(rectangle => !ValidRect(rectangle)))
        {
            return null;
        }

        var grid = Sample(frame);
        if (grid is null || grid.Width <= 0 || grid.Height <= 0) return null;
        var corners = Corners(grid, point =>
        {
            var sourcePoint = new AtlasPoint(
                point.X * frame.Width / grid.Width,
                frame.Height - 1 - point.Y * frame.Height / grid.Height);
            return !exclusions.Any(rectangle => Contains(rectangle.Expand(2), sourcePoint));
        });
        var features = corners.Select((corner, index) =>
            new GlobalFeatureReference.Feature(
                index,
                corner.Point,
                corner.Descriptor,
                corner.Strength,
                depth,
                depthConfidence)).ToArray();
        if (features.Length < 6) return null;
        return new GlobalFeatureReference(
            id ?? Guid.NewGuid(),
            cameraPose,
            solveWidth,
            frame.Width,
            frame.Height,
            grid.Width,
            grid.Height,
            features);
    }

    public GlobalFeatureReference? MakeReference(
        Guid id,
        BgraFrame frame,
        AtlasPoint cameraPose,
        double solveWidth,
        double depth = 1,
        double depthConfidence = 0,
        IReadOnlyList<StencilRect>? excluding = null) =>
        MakeReference(
            frame,
            cameraPose,
            solveWidth,
            id,
            depth,
            depthConfidence,
            excluding);

    public GlobalFeatureMatch? Match(
        BgraFrame frame,
        IReadOnlyList<GlobalFeatureReference> references,
        Guid? preferredReferenceId = null,
        double? currentSolveWidth = null,
        IReadOnlyList<StencilRect>? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(references);
        var exclusions = excluding ?? [];
        if (currentSolveWidth is { } width && (!double.IsFinite(width) || width <= 0)
            || exclusions.Any(rectangle => !ValidRect(rectangle)))
        {
            return null;
        }

        var currentGrid = Sample(frame);
        if (currentGrid is null) return null;
        var currentFeatures = Corners(currentGrid, point =>
        {
            var sourcePoint = new AtlasPoint(
                point.X * frame.Width / currentGrid.Width,
                frame.Height - 1 - point.Y * frame.Height / currentGrid.Height);
            return !exclusions.Any(rectangle => Contains(rectangle.Expand(2), sourcePoint));
        });
        if (currentFeatures.Count < 6) return null;

        var signature = AppearanceSignature(currentFeatures.Select(value => value.Descriptor));
        var selected = SelectReferences(references, signature, preferredReferenceId);
        var candidates = new List<Candidate>();
        foreach (var reference in selected)
        {
            candidates.AddRange(CandidatePlacements(
                reference,
                currentFeatures,
                currentGrid,
                currentSolveWidth ?? reference.SolveWidth));
        }
        candidates.Sort(CompareCandidates);
        if (candidates.Count == 0) return null;
        var best = candidates[0];
        var distinctPlacementDistance = Math.Max(
            (currentSolveWidth ?? best.Reference.SolveWidth) * 0.025,
            4);
        Candidate? runnerUp = null;
        for (var index = 1; index < candidates.Count; index++)
        {
            if (Distance(candidates[index].Camera, best.Camera) <= distinctPlacementDistance) continue;
            runnerUp = candidates[index];
            break;
        }
        var ambiguity = Math.Min(
            1,
            (runnerUp?.Confidence ?? 0) / Math.Max(best.Confidence, double.Epsilon));
        if (ambiguity >= AmbiguityLimit) return null;
        return new GlobalFeatureMatch(
            best.Reference.Id,
            best.Camera,
            best.Translation,
            best.Inliers.Select(value => best.Reference.Features[value.Reference].Id)
                .OrderBy(value => value).ToArray(),
            best.Inliers.Select(value => value.Current).OrderBy(value => value).ToArray(),
            best.Residual,
            best.Confidence,
            ambiguity);
    }

    private IReadOnlyList<GlobalFeatureReference> SelectReferences(
        IReadOnlyList<GlobalFeatureReference> references,
        IReadOnlyList<int> signature,
        Guid? preferredReferenceId)
    {
        var ranked = references.Where(Valid)
            .Select(reference => new
            {
                Reference = reference,
                Distance = SignatureDistance(
                    AppearanceSignature(reference.Features.Select(value => value.Descriptor)),
                    signature)
            })
            .OrderBy(value => value.Distance)
            .ThenBy(value => value.Reference.Id.ToString("D"), StringComparer.Ordinal)
            .Select(value => value.Reference)
            .ToArray();
        if (ranked.Length <= MaximumReferences) return ranked;

        var coverageBudget = MaximumReferences / 4;
        var firstByCell = new Dictionary<(int X, int Y), GlobalFeatureReference>();
        foreach (var reference in ranked)
        {
            var cellSize = Math.Max(1, reference.SolveWidth * 4);
            var cell = (
                (int)Math.Floor(reference.CameraPose.X / cellSize),
                (int)Math.Floor(reference.CameraPose.Y / cellSize));
            firstByCell.TryAdd(cell, reference);
        }
        var coverage = firstByCell.Values
            .Select(reference => new
            {
                Reference = reference,
                Distance = SignatureDistance(
                    AppearanceSignature(reference.Features.Select(value => value.Descriptor)),
                    signature)
            })
            .OrderBy(value => value.Distance)
            .ThenBy(value => value.Reference.Id.ToString("D"), StringComparer.Ordinal)
            .Select(value => value.Reference)
            .ToArray();
        var preferred = preferredReferenceId is { } preferredId
            ? ranked.FirstOrDefault(reference => reference.Id == preferredId)
            : null;
        var selected = preferred is null
            ? new List<GlobalFeatureReference>()
            : [preferred];
        foreach (var reference in Rotated(coverage, referenceScanOffset))
        {
            if (selected.Any(value => value.Id == reference.Id)) continue;
            selected.Add(reference);
            if (selected.Count == coverageBudget + (preferred is null ? 0 : 1)) break;
        }
        foreach (var reference in Rotated(ranked, referenceScanOffset))
        {
            if (selected.Any(value => value.Id == reference.Id)) continue;
            selected.Add(reference);
            if (selected.Count == MaximumReferences) break;
        }
        referenceScanOffset = (referenceScanOffset + MaximumReferences - coverageBudget) % ranked.Length;
        return selected;
    }

    private static IReadOnlyList<T> Rotated<T>(IReadOnlyList<T> values, int offset)
    {
        if (values.Count == 0) return [];
        var start = offset % values.Count;
        return Enumerable.Range(0, values.Count)
            .Select(index => values[(start + index) % values.Count])
            .ToArray();
    }

    private static bool Valid(GlobalFeatureReference reference) =>
        double.IsFinite(reference.SolveWidth) && reference.SolveWidth > 0
        && Finite(reference.CameraPose)
        && reference.SourceWidth > 0 && reference.SourceHeight > 0
        && reference.SampleWidth > 0 && reference.SampleHeight > 0
        && reference.Features.Count >= 6
        && reference.Features.All(feature =>
            feature.Descriptor.Count == 25
            && Finite(feature.SamplePoint)
            && feature.SamplePoint.X >= 0 && feature.SamplePoint.X < reference.SampleWidth
            && feature.SamplePoint.Y >= 0 && feature.SamplePoint.Y < reference.SampleHeight
            && double.IsFinite(feature.Depth) && feature.Depth > 0
            && double.IsFinite(feature.DepthConfidence)
            && feature.DepthConfidence is >= 0 and <= 1);

    private IReadOnlyList<Candidate> CandidatePlacements(
        GlobalFeatureReference reference,
        IReadOnlyList<Corner> current,
        Grid currentGrid,
        double currentSolveWidth)
    {
        var referenceFeatures = reference.Features.Take(MaximumFeatures).ToArray();
        if (referenceFeatures.Length < 6) return [];
        var referenceCorners = referenceFeatures.Select(feature =>
            new Corner(feature.SamplePoint, feature.Descriptor.ToArray(), feature.Strength)).ToArray();
        if (HasAmbiguousSelfPlacement(reference.Id, referenceCorners)) return [];

        var normalizedCurrent = current.Select(corner => new Corner(
            new AtlasPoint(
                corner.Point.X * reference.SampleWidth / currentGrid.Width,
                corner.Point.Y * reference.SampleHeight / currentGrid.Height),
            corner.Descriptor,
            corner.Strength)).ToArray();
        var referenceNearest = referenceCorners.Select(corner =>
            Nearest(corner.Descriptor, normalizedCurrent)).ToArray();
        var currentNearest = normalizedCurrent.Select(corner =>
            Nearest(corner.Descriptor, referenceCorners)).ToArray();
        var pairs = new List<Pair>();
        for (var index = 0; index < referenceFeatures.Length; index++)
        {
            var choice = referenceNearest[index];
            if (choice is null || !choice.Value.Accepted) continue;
            var reverse = currentNearest[choice.Value.Index];
            if (reverse is null || !reverse.Value.Accepted || reverse.Value.Index != index) continue;
            pairs.Add(new Pair(
                index,
                choice.Value.Index,
                choice.Value.Distance,
                new AtlasPoint(
                    referenceFeatures[index].SamplePoint.X
                        - normalizedCurrent[choice.Value.Index].Point.X,
                    referenceFeatures[index].SamplePoint.Y
                        - normalizedCurrent[choice.Value.Index].Point.Y)));
        }
        if (pairs.Count < 6) return [];

        var result = new List<Candidate>();
        var remaining = pairs.ToList();
        while (remaining.Count >= 6 && result.Count < 3)
        {
            var seed = ConsensusSeed(remaining);
            if (seed is null) break;
            var inliers = remaining.Where(pair =>
                Distance(pair.Translation, seed.Value) <= ConsensusRadius).ToArray();
            var translation = Average(inliers.Select(value => value.Translation));
            var refined = remaining.Where(pair =>
                Distance(pair.Translation, translation) <= ConsensusRadius).ToArray();
            if (refined.Length < 6
                || !SpatiallySpread(refined.Select(pair =>
                    referenceFeatures[pair.Reference].SamplePoint)))
            {
                remaining.RemoveAll(pair =>
                    Distance(pair.Translation, seed.Value) <= ConsensusRadius);
                continue;
            }
            var residual = refined.Average(pair => Distance(pair.Translation, translation));
            var descriptorQuality = 1 - Math.Min(
                0.9,
                refined.Sum(pair => pair.Score) / (double)(refined.Length * 2_000));
            var coverage = refined.Length / (double)Math.Max(referenceFeatures.Length, normalizedCurrent.Length);
            var confidence = coverage * descriptorQuality
                * (1 - Math.Min(0.9, residual / ConsensusRadius));
            var referenceGain = reference.SolveWidth / reference.SampleWidth;
            var currentGain = currentSolveWidth / currentGrid.Width;
            var camera = Average(refined.Select(pair =>
            {
                var referencePoint = referenceFeatures[pair.Reference].SamplePoint;
                var currentPoint = current[pair.Current].Point;
                return new AtlasPoint(
                    reference.CameraPose.X + referencePoint.X * referenceGain
                        - currentPoint.X * currentGain,
                    reference.CameraPose.Y
                        + (reference.SampleHeight - referencePoint.Y) * referenceGain
                        - (currentGrid.Height - currentPoint.Y) * currentGain);
            }));
            result.Add(new Candidate(
                reference,
                refined,
                translation,
                residual,
                confidence,
                camera));
            remaining.RemoveAll(pair =>
                Distance(pair.Translation, translation) <= ConsensusRadius * 2);
        }
        return result;
    }

    private static int CompareCandidates(Candidate left, Candidate right)
    {
        var order = right.Confidence.CompareTo(left.Confidence);
        if (order != 0) return order;
        order = left.Residual.CompareTo(right.Residual);
        if (order != 0) return order;
        order = string.Compare(
            left.Reference.Id.ToString("D"),
            right.Reference.Id.ToString("D"),
            StringComparison.Ordinal);
        if (order != 0) return order;
        order = left.Translation.X.CompareTo(right.Translation.X);
        return order != 0 ? order : left.Translation.Y.CompareTo(right.Translation.Y);
    }

    private static NearestChoice? Nearest(
        IReadOnlyList<byte> descriptor,
        IReadOnlyList<Corner> candidates)
    {
        if (candidates.Count == 0) return null;
        (int Index, int Distance)? best = null;
        (int Index, int Distance)? second = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = (Index: index, Distance: DescriptorDistance(
                descriptor,
                candidates[index].Descriptor));
            if (best is null
                || candidate.Distance < best.Value.Distance
                || candidate.Distance == best.Value.Distance && candidate.Index < best.Value.Index)
            {
                second = best;
                best = candidate;
            }
            else if (second is null
                || candidate.Distance < second.Value.Distance
                || candidate.Distance == second.Value.Distance && candidate.Index < second.Value.Index)
            {
                second = candidate;
            }
        }
        if (best is null || best.Value.Distance > 1_200) return null;
        var ratioPass = second is null
            || second.Value.Distance > best.Value.Distance * 1.12;
        return new NearestChoice(best.Value.Index, best.Value.Distance, ratioPass);
    }

    private static AtlasPoint? ConsensusSeed(IReadOnlyList<Pair> pairs)
    {
        if (pairs.Count == 0) return null;
        var bestIndex = 0;
        var bestSupport = -1;
        var bestError = double.PositiveInfinity;
        for (var index = 0; index < pairs.Count; index++)
        {
            var support = 0;
            var error = 0.0;
            for (var candidate = 0; candidate < pairs.Count; candidate++)
            {
                var distance = Distance(pairs[candidate].Translation, pairs[index].Translation);
                if (distance > ConsensusRadius) continue;
                support++;
                error += distance;
            }
            if (support > bestSupport
                || support == bestSupport && error < bestError
                || support == bestSupport && error == bestError && index < bestIndex)
            {
                bestIndex = index;
                bestSupport = support;
                bestError = error;
            }
        }
        return pairs[bestIndex].Translation;
    }

    private bool HasAmbiguousSelfPlacement(Guid referenceId, IReadOnlyList<Corner> features)
    {
        var fingerprint = unchecked((ulong)features.Count);
        foreach (var feature in features)
        {
            foreach (var value in feature.Descriptor)
            {
                fingerprint = unchecked(fingerprint * 1_099_511_628_211UL) ^ value;
            }
        }
        if (ambiguityByReferenceId.TryGetValue(referenceId, out var cached)
            && cached.Fingerprint == fingerprint)
        {
            LastSelfPlacementWasAmbiguous = cached.Ambiguous;
            LastSelfPlacementPairChecks = 0;
            return cached.Ambiguous;
        }

        const int descriptorBinWidth = 41;
        var descriptorBins = new Dictionary<(int First, int Middle, int Last), List<int>>();
        for (var index = 0; index < features.Count; index++)
        {
            var descriptor = features[index].Descriptor;
            var bin = (
                descriptor[0] / descriptorBinWidth,
                descriptor[12] / descriptorBinWidth,
                descriptor[24] / descriptorBinWidth);
            if (!descriptorBins.TryGetValue(bin, out var values))
            {
                values = [];
                descriptorBins[bin] = values;
            }
            values.Add(index);
        }

        LastSelfPlacementPairChecks = 0;
        LastSelfPlacementWasAmbiguous = false;
        var translations = new List<AtlasPoint>();
        for (var first = 0; first < features.Count; first++)
        {
            var descriptor = features[first].Descriptor;
            var center = (
                descriptor[0] / descriptorBinWidth,
                descriptor[12] / descriptorBinWidth,
                descriptor[24] / descriptorBinWidth);
            for (var firstOffset = -1; firstOffset <= 1; firstOffset++)
            for (var middleOffset = -1; middleOffset <= 1; middleOffset++)
            for (var lastOffset = -1; lastOffset <= 1; lastOffset++)
            {
                if (!descriptorBins.TryGetValue((
                        center.Item1 + firstOffset,
                        center.Item2 + middleOffset,
                        center.Item3 + lastOffset), out var candidates))
                {
                    continue;
                }
                foreach (var second in candidates)
                {
                    if (first == second) continue;
                    LastSelfPlacementPairChecks++;
                    if (DescriptorDistance(
                            features[first].Descriptor,
                            features[second].Descriptor) > 40)
                    {
                        continue;
                    }
                    var translation = new AtlasPoint(
                        features[first].Point.X - features[second].Point.X,
                        features[first].Point.Y - features[second].Point.Y);
                    if (Math.Sqrt(
                            translation.X * translation.X
                            + translation.Y * translation.Y) < 8)
                    {
                        continue;
                    }
                    translations.Add(translation);
                }
            }
        }
        if (translations.Count < 6)
        {
            ambiguityByReferenceId[referenceId] = new AmbiguityCacheEntry(fingerprint, false);
            return false;
        }

        var bins = new Dictionary<(int X, int Y), List<AtlasPoint>>();
        foreach (var translation in translations)
        {
            var bin = (
                (int)Math.Floor(translation.X / ConsensusRadius),
                (int)Math.Floor(translation.Y / ConsensusRadius));
            if (!bins.TryGetValue(bin, out var values))
            {
                values = [];
                bins[bin] = values;
            }
            values.Add(translation);
        }
        foreach (var seed in translations)
        {
            var center = (
                (int)Math.Floor(seed.X / ConsensusRadius),
                (int)Math.Floor(seed.Y / ConsensusRadius));
            var support = 0;
            for (var y = center.Item2 - 1; y <= center.Item2 + 1; y++)
            for (var x = center.Item1 - 1; x <= center.Item1 + 1; x++)
            {
                if (!bins.TryGetValue((x, y), out var candidates)) continue;
                foreach (var candidate in candidates)
                {
                    if (Distance(candidate, seed) > ConsensusRadius) continue;
                    support++;
                    if (support < 6) continue;
                    LastSelfPlacementWasAmbiguous = true;
                    ambiguityByReferenceId[referenceId] =
                        new AmbiguityCacheEntry(fingerprint, true);
                    return true;
                }
            }
        }
        ambiguityByReferenceId[referenceId] = new AmbiguityCacheEntry(fingerprint, false);
        return false;
    }

    private static Grid? Sample(BgraFrame image)
    {
        if (image.Width <= 0 || image.Height <= 0) return null;
        var height = Math.Max(
            1,
            (int)Math.Round(
                image.Height * (double)SampleWidth / image.Width,
                MidpointRounding.AwayFromZero));
        var luma = new byte[checked(SampleWidth * height)];
        for (var y = 0; y < height; y++)
        {
            var logicalSourceY = ((y + 0.5) * image.Height / height) - 0.5;
            var sourceY = Math.Clamp(image.Height - 1 - logicalSourceY, 0, image.Height - 1.0);
            var y0 = (int)Math.Floor(sourceY);
            var y1 = Math.Min(image.Height - 1, y0 + 1);
            var yWeight = sourceY - y0;
            for (var x = 0; x < SampleWidth; x++)
            {
                var sourceX = Math.Clamp(
                    ((x + 0.5) * image.Width / SampleWidth) - 0.5,
                    0,
                    image.Width - 1.0);
                var x0 = (int)Math.Floor(sourceX);
                var x1 = Math.Min(image.Width - 1, x0 + 1);
                var xWeight = sourceX - x0;
                var blue = SampleChannel(image, x0, x1, y0, y1, xWeight, yWeight, 0);
                var green = SampleChannel(image, x0, x1, y0, y1, xWeight, yWeight, 1);
                var red = SampleChannel(image, x0, x1, y0, y1, xWeight, yWeight, 2);
                luma[y * SampleWidth + x] = (byte)((red * 54 + green * 183 + blue * 19) / 256);
            }
        }
        return new Grid(luma, SampleWidth, height);
    }

    private static int SampleChannel(
        BgraFrame image,
        int x0,
        int x1,
        int y0,
        int y1,
        double xWeight,
        double yWeight,
        int channel)
    {
        var topLeft = image.Pixels[y0 * image.Stride + x0 * BgraFrame.BytesPerPixel + channel];
        var topRight = image.Pixels[y0 * image.Stride + x1 * BgraFrame.BytesPerPixel + channel];
        var bottomLeft = image.Pixels[y1 * image.Stride + x0 * BgraFrame.BytesPerPixel + channel];
        var bottomRight = image.Pixels[y1 * image.Stride + x1 * BgraFrame.BytesPerPixel + channel];
        var top = topLeft + (topRight - topLeft) * xWeight;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
        return Math.Clamp(
            (int)Math.Round(top + (bottom - top) * yWeight, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);
    }

    private static IReadOnlyList<Corner> Corners(
        Grid grid,
        Func<AtlasPoint, bool> allowed)
    {
        if (grid.Width < 7 || grid.Height < 7) return [];
        var all = new List<Corner>();
        for (var y = 3; y < grid.Height - 3; y++)
        for (var x = 3; x < grid.Width - 3; x++)
        {
            var point = new AtlasPoint(x, y);
            if (!allowed(point)) continue;
            var strength = Math.Abs(Value(grid, x + 1, y) - Value(grid, x - 1, y))
                + Math.Abs(Value(grid, x, y + 1) - Value(grid, x, y - 1));
            if (strength < 55) continue;
            var descriptor = new byte[25];
            var index = 0;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                descriptor[index++] = grid.Pixels[(y + dy) * grid.Width + x + dx];
            }
            all.Add(new Corner(point, descriptor, strength));
        }
        var selected = new List<Corner>();
        foreach (var corner in all
                     .OrderByDescending(value => value.Strength)
                     .ThenBy(value => value.Point.Y)
                     .ThenBy(value => value.Point.X))
        {
            if (selected.Any(value => Distance(value.Point, corner.Point) < 7)) continue;
            selected.Add(corner);
            if (selected.Count == MaximumFeatures) break;
        }
        return selected;
    }

    private static int Value(Grid grid, int x, int y) => grid.Pixels[y * grid.Width + x];

    private static int DescriptorDistance(IReadOnlyList<byte> first, IReadOnlyList<byte> second)
    {
        var total = 0;
        var count = Math.Min(first.Count, second.Count);
        for (var index = 0; index < count; index++)
        {
            total += Math.Abs(first[index] - second[index]);
        }
        return total;
    }

    private static IReadOnlyList<int> AppearanceSignature(IEnumerable<IReadOnlyList<byte>> descriptors)
    {
        var bins = new int[16];
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Count != 25) continue;
            foreach (var value in descriptor) bins[Math.Min(15, value / 16)]++;
        }
        var total = Math.Max(1, bins.Sum());
        return bins.Select(value => value * 4_096 / total).ToArray();
    }

    private static int SignatureDistance(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        var total = 0;
        for (var index = 0; index < Math.Min(first.Count, second.Count); index++)
        {
            total += Math.Abs(first[index] - second[index]);
        }
        return total;
    }

    private static double Distance(AtlasPoint first, AtlasPoint second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static AtlasPoint Average(IEnumerable<AtlasPoint> values)
    {
        var count = 0;
        var x = 0.0;
        var y = 0.0;
        foreach (var value in values)
        {
            count++;
            x += value.X;
            y += value.Y;
        }
        return new AtlasPoint(x / count, y / count);
    }

    private static bool SpatiallySpread(IEnumerable<AtlasPoint> values)
    {
        var points = values.ToArray();
        if (points.Length == 0) return false;
        var xSpan = points.Max(value => value.X) - points.Min(value => value.X);
        var ySpan = points.Max(value => value.Y) - points.Min(value => value.Y);
        var occupiedCells = points.Select(value => (
            (int)(value.X / 40),
            (int)(value.Y / 30))).ToHashSet();
        return xSpan >= 30 && ySpan >= 18 && occupiedCells.Count >= 3;
    }

    private static bool ValidRect(StencilRect rectangle) =>
        rectangle.IsFinite && rectangle.Width >= 0 && rectangle.Height >= 0;

    private static bool Contains(StencilRect rectangle, AtlasPoint point) =>
        point.X >= rectangle.X && point.X < rectangle.Right
        && point.Y >= rectangle.Y && point.Y < rectangle.Bottom;

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private sealed record Grid(byte[] Pixels, int Width, int Height);
    private sealed record Corner(AtlasPoint Point, IReadOnlyList<byte> Descriptor, int Strength);
    private readonly record struct Pair(
        int Reference,
        int Current,
        int Score,
        AtlasPoint Translation);
    private sealed record Candidate(
        GlobalFeatureReference Reference,
        IReadOnlyList<Pair> Inliers,
        AtlasPoint Translation,
        double Residual,
        double Confidence,
        AtlasPoint Camera);
    private readonly record struct NearestChoice(int Index, int Distance, bool Accepted);
    private readonly record struct AmbiguityCacheEntry(ulong Fingerprint, bool Ambiguous);
}
