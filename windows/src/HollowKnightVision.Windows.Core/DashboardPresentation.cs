namespace HollowKnightVision.Windows.Core;

public enum LabelingModelTab
{
    Summary,
    Objects,
    Screenshots,
}

/// <summary>
/// Selection belongs to the dashboard owner rather than a recreated model
/// workspace view, so tabs and focused rows survive workspace reconstruction.
/// </summary>
public sealed record LabelingModelWorkspaceSelection
{
    public LabelingModelTab Tab { get; init; } = LabelingModelTab.Summary;
    public Guid? ObjectExampleIdentifier { get; init; }
    public Guid? ScreenshotIdentifier { get; init; }
    public string ScreenshotGroup { get; init; } = "all";
    public Guid? ContractExampleIdentifier { get; init; }
    public string? SummaryReviewClassIdentifier { get; init; }
    public Guid? SummaryFailureIdentifier { get; init; }
}

public sealed record MenuObjectParticlePlacement(
    AtlasPoint Position,
    double Opacity,
    double Scale,
    double RotationDegrees,
    double Depth);

public static class MenuObjectParticleMotion
{
    public const double IconSize = 70;
    public const int DefaultParticleCount = 32;
    public const double SpawnInterval = .28;

    public static double Depth(int index) => ((index * 7 + 3) % 12) / 11d;

    public static double TravelDuration(int index) => 15 - Depth(index) * 7;

    public static double BaseScale(int index) => .55 + Depth(index) * .85;

    public static IReadOnlyList<int> RenderOrder(int count) =>
        Enumerable.Range(0, Math.Max(0, count))
            .OrderBy(Depth)
            .ThenBy(index => index)
            .ToArray();

    public static MenuObjectParticlePlacement? Placement(
        double elapsed,
        int index,
        int count,
        WorldSize size)
    {
        var delay = index * SpawnInterval;
        if (!double.IsFinite(elapsed)
            || elapsed < delay
            || count <= 0
            || !double.IsFinite(size.Width)
            || !double.IsFinite(size.Height)
            || size.Width <= 0
            || size.Height <= 0)
            return null;
        var depth = Depth(index);
        var duration = TravelDuration(index);
        var progress = ((elapsed - delay) % duration) / duration;
        var phase = index % 7 * .73;
        var scale = BaseScale(index) + Math.Sin(progress * Math.PI + phase) * .04;
        var sideRank = index / 2;
        var positionsPerSide = Math.Max(1, (count + 1) / 2);
        var sideFraction = (sideRank + .5) / positionsPerSide;
        var halfWidth = IconSize * scale / 2;
        var outerCenter = halfWidth * .45;
        var innerCenter = Math.Max(outerCenter, size.Width * .055 - halfWidth);
        var laneCenter = outerCenter + (innerCenter - outerCenter) * sideFraction;
        var swayLimit = Math.Min(8 + depth * 18, size.Width * .018);
        var sway = Math.Sin(progress * Math.PI * 2 + phase) * swayLimit;
        var distanceFromEdge = Math.Max(
            outerCenter,
            Math.Min(innerCenter, laneCenter + sway));
        var x = index % 2 == 0 ? distanceFromEdge : size.Width - distanceFromEdge;
        var travel = size.Height + IconSize;
        var y = size.Height + IconSize / 2 - progress * travel;
        var fadeIn = Math.Min(1, progress / .08);
        var fadeOut = Math.Min(1, (1 - progress) / .28);
        var maximumOpacity = .10 + depth * .18;
        var opacity = maximumOpacity * Math.Max(0, Math.Min(fadeIn, fadeOut));
        var rotation = Math.Sin(progress * Math.PI * 2 + phase)
            * (1.5 + depth * 4.5);
        return new MenuObjectParticlePlacement(
            new AtlasPoint(x, y), opacity, scale, rotation, depth);
    }
}

public static class LiveGameStatusPresentationPolicy
{
    public static string Text(string? recognizedContext, string? roomStatus = null)
    {
        var context = recognizedContext ?? "Finding State";
        return context == "Gameplay" && roomStatus is { Length: > 0 }
            ? $"{context} · {roomStatus}"
            : context;
    }
}

public enum DashboardFeatureReviewMarkerKind
{
    CameraConsistentGround,
    GroundFeature,
    OccludedGroundFeature,
    GroundFeatureDepthError,
    SelectedGroundFeature,
    TransitionDoor,
}

public enum DashboardFeatureReviewLineKind
{
    CurrentGround,
    PersistentGround,
    RoomTransition,
    RoomBoundary,
    PositiveGroundTruth,
    NegativeGroundTruth,
    SelectedGroundTruth,
}

public enum DashboardFeatureReviewCoordinateSpace
{
    Atlas,
    Live,
}

public sealed record DashboardGroundFeatureDetails(
    int SegmentId,
    int SequenceIndex,
    AtlasPoint WorldPosition,
    IReadOnlyList<byte> ReferencePixels,
    IReadOnlyList<byte> ReferenceOpacity);

public sealed record GroundViewerSlot(
    int SequenceIndex,
    GroundHypothesisAtlasFeature? Feature);

/// <summary>
/// Builds a spatially faithful ground-feature row. Missing sequence indices
/// remain black slots instead of collapsing a deleted gap in the atlas edge.
/// </summary>
public static class GroundViewerRowAssembler
{
    public static IReadOnlyList<GroundViewerSlot> Slots(
        IReadOnlyList<GroundHypothesisAtlasFeature> features,
        int segmentId)
    {
        ArgumentNullException.ThrowIfNull(features);
        var row = features.Where(feature => feature.SegmentId == segmentId).ToArray();
        if (row.Length == 0) return [];
        var minimum = row.Min(feature => feature.SequenceIndex);
        var maximum = row.Max(feature => feature.SequenceIndex);
        var byIndex = new Dictionary<int, GroundHypothesisAtlasFeature>();
        foreach (var feature in row)
            byIndex.TryAdd(feature.SequenceIndex, feature);
        var result = new List<GroundViewerSlot>(checked(maximum - minimum + 1));
        for (var sequenceIndex = minimum; sequenceIndex <= maximum; sequenceIndex++)
        {
            byIndex.TryGetValue(sequenceIndex, out var feature);
            result.Add(new GroundViewerSlot(sequenceIndex, feature));
            if (sequenceIndex == int.MaxValue) break;
        }
        return result;
    }

    public static IReadOnlyList<byte> StitchedPixels(
        IReadOnlyList<GroundViewerSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var tileWidth = GroundHypothesisTracker.FeatureWidth;
        var tileHeight = GroundHypothesisTracker.FeatureHeight;
        var width = checked(slots.Count * tileWidth);
        if (width == 0) return [];
        var pixels = new byte[checked(width * tileHeight)];
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var feature = slots[slotIndex].Feature;
            if (feature?.ReferencePixels.Count != GroundHypothesisTracker.FeaturePixelCount)
                continue;
            var hasOpacity = feature.ReferenceOpacity.Count
                == GroundHypothesisTracker.FeaturePixelCount;
            for (var y = 0; y < tileHeight; y++)
            {
                for (var x = 0; x < tileWidth; x++)
                {
                    var sourceIndex = y * tileWidth + x;
                    var destination = y * width + slotIndex * tileWidth + x;
                    var opacity = hasOpacity ? feature.ReferenceOpacity[sourceIndex] : 255;
                    pixels[destination] = (byte)(
                        (feature.ReferencePixels[sourceIndex] * opacity + 127) / 255);
                }
            }
        }
        return pixels;
    }

    public static GroundHypothesisAtlasFeature? NearestFeature(
        int slotIndex,
        IReadOnlyList<GroundViewerSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0) return null;
        var clamped = Math.Clamp(slotIndex, 0, slots.Count - 1);
        if (slots[clamped].Feature is { } direct) return direct;
        for (var distance = 1; distance < slots.Count; distance++)
        {
            var left = clamped - distance;
            if (left >= 0 && slots[left].Feature is { } leftFeature) return leftFeature;
            var right = clamped + distance;
            if (right < slots.Count && slots[right].Feature is { } rightFeature)
                return rightFeature;
        }
        return null;
    }
}

public sealed record GroundViewerRowState(
    IReadOnlyList<GroundHypothesisAtlasFeature> Features,
    IReadOnlyList<GroundViewerSlot> Slots,
    GroundHypothesisAtlasFeature Selected)
{
    public static GroundViewerRowState Refreshed(
        GroundHypothesisAtlasFeature selected,
        IReadOnlyList<GroundHypothesisAtlasFeature> available)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(available);
        var byIndex = new Dictionary<int, GroundHypothesisAtlasFeature>();
        foreach (var feature in available.Where(
                     feature => feature.SegmentId == selected.SegmentId))
            byIndex.TryAdd(feature.SequenceIndex, feature);
        byIndex.TryAdd(selected.SequenceIndex, selected);
        var features = byIndex.Values
            .OrderBy(feature => feature.SequenceIndex)
            .ToArray();
        if (features.Length == 0) features = [selected];
        return new GroundViewerRowState(
            features,
            GroundViewerRowAssembler.Slots(features, selected.SegmentId),
            features.FirstOrDefault(
                feature => feature.SequenceIndex == selected.SequenceIndex) ?? selected);
    }
}

/// <summary>
/// Diagnostic scan of painted pixels rather than allocated 256-pixel tiles.
/// Every alpha-bearing row counts, so vertical movement can grow Y without
/// falsely growing the horizontal footprint.
/// </summary>
public readonly record struct GroundAtlasFootprint(
    int MinimumX,
    int MaximumX,
    int LeftPixels,
    int RightPixels,
    int PaintedPixels)
{
    public static GroundAtlasFootprint Measure(
        LiveTiledAtlasSnapshot snapshot,
        double viewX,
        double viewWidth)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Measure(snapshot.Tiles, viewX, viewWidth);
    }

    public static GroundAtlasFootprint Measure(
        IReadOnlyList<LiveTiledAtlasTile> tiles,
        double viewX,
        double viewWidth)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var minimum = int.MaxValue;
        var maximum = int.MinValue;
        var left = 0;
        var right = 0;
        var painted = 0;
        var viewLeft = (int)Math.Floor(viewX);
        var viewRight = (int)Math.Ceiling(viewX + viewWidth);
        foreach (var tile in tiles)
        {
            var image = tile.Image;
            for (var x = 0; x < image.Width; x++)
            {
                var count = 0;
                for (var y = 0; y < image.Height; y++)
                {
                    if (image.Pixels[y * image.Stride + x * BgraFrame.BytesPerPixel + 3] > 0)
                        count++;
                }
                if (count == 0) continue;
                var worldX = checked((int)tile.WorldBounds.X + x);
                minimum = Math.Min(minimum, worldX);
                maximum = Math.Max(maximum, checked(worldX + 1));
                painted = checked(painted + count);
                if (worldX < viewLeft) left = checked(left + count);
                if (worldX >= viewRight) right = checked(right + count);
            }
        }
        return new GroundAtlasFootprint(
            painted > 0 ? minimum : 0,
            painted > 0 ? maximum : 0,
            left,
            right,
            painted);
    }
}

public sealed record DashboardFeatureReviewMarker(
    AtlasPoint WorldPosition,
    DashboardFeatureReviewMarkerKind Kind,
    WorldSize? WorldSize = null,
    DashboardGroundFeatureDetails? GroundFeatureDetails = null,
    DashboardFeatureReviewCoordinateSpace CoordinateSpace = DashboardFeatureReviewCoordinateSpace.Atlas,
    bool IsVisible = true,
    double Opacity = 1);

public sealed record DashboardFeatureReviewLine(
    AtlasPoint Start,
    AtlasPoint End,
    DashboardFeatureReviewLineKind Kind,
    DashboardFeatureReviewCoordinateSpace CoordinateSpace = DashboardFeatureReviewCoordinateSpace.Atlas);

public sealed record DashboardFeatureReviewOverlay(
    IReadOnlyList<DashboardFeatureReviewMarker> Markers,
    IReadOnlyList<DashboardFeatureReviewLine> Lines);

public readonly record struct DashboardRgba(
    double Red,
    double Green,
    double Blue,
    double Alpha);

public static class DashboardFeatureReviewPalette
{
    public static DashboardRgba Rgba(DashboardFeatureReviewMarkerKind kind) => kind switch
    {
        DashboardFeatureReviewMarkerKind.CameraConsistentGround => new(.27, 1, .47, .95),
        DashboardFeatureReviewMarkerKind.GroundFeature => new(.75, .31, 1, .95),
        DashboardFeatureReviewMarkerKind.OccludedGroundFeature => new(1, .50, 0, .95),
        DashboardFeatureReviewMarkerKind.GroundFeatureDepthError => new(1, .24, .24, .95),
        DashboardFeatureReviewMarkerKind.SelectedGroundFeature => new(1, 1, 1, 1),
        DashboardFeatureReviewMarkerKind.TransitionDoor => new(.12, .90, 1, .95),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DashboardRgba Rgba(DashboardFeatureReviewLineKind kind) => kind switch
    {
        DashboardFeatureReviewLineKind.CurrentGround => new(.27, 1, .47, .95),
        DashboardFeatureReviewLineKind.PersistentGround => new(.75, .31, 1, .86),
        DashboardFeatureReviewLineKind.RoomTransition => new(1, .52, .12, .95),
        DashboardFeatureReviewLineKind.RoomBoundary => new(.12, .90, 1, .95),
        DashboardFeatureReviewLineKind.PositiveGroundTruth => new(.27, 1, .47, 1),
        DashboardFeatureReviewLineKind.NegativeGroundTruth => new(1, .18, .18, 1),
        DashboardFeatureReviewLineKind.SelectedGroundTruth => new(1, 1, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record DashboardOverlayQuad(
    DashboardFeatureReviewMarkerKind? MarkerKind,
    DashboardFeatureReviewLineKind? LineKind,
    IReadOnlyList<AtlasPoint> Points,
    double Opacity = 1);

public static class DashboardFeatureReviewGeometry
{
    public const int MaximumMarkers = 2_000;
    public const int MaximumLines = 4_096;
    public const double MarkerSize = 8;
    public const double LineWidth = 1;

    public static AtlasPoint? ScreenPoint(
        AtlasPoint world,
        double zoom,
        AtlasPoint pan)
    {
        if (!Finite(world) || !double.IsFinite(zoom) || zoom <= 0 || !Finite(pan))
            return null;
        var point = new AtlasPoint(
            world.X * zoom + pan.X,
            world.Y * zoom + pan.Y);
        return Finite(point) ? point : null;
    }

    public static IReadOnlyList<DashboardOverlayQuad> MarkerQuads(
        DashboardFeatureReviewOverlay? overlay,
        double zoom,
        AtlasPoint pan)
    {
        if (overlay is null) return [];
        var result = new List<DashboardOverlayQuad>();
        var valid = overlay.Markers
            .Where(marker => marker.IsVisible)
            .Select(marker => (Marker: marker,
                Center: ScreenPoint(marker.WorldPosition, zoom, pan)))
            .Where(item => item.Center is not null)
            .Take(MaximumMarkers);
        foreach (var item in valid)
        {
            var marker = item.Marker;
            var center = item.Center!.Value;
            var halfWidth = marker.WorldSize is { } worldSize
                ? Math.Max(1, worldSize.Width * zoom * .5)
                : MarkerSize * .5;
            var halfHeight = marker.WorldSize is { } size
                ? Math.Max(1, size.Height * zoom * .5)
                : MarkerSize * .5;
            var thickness = marker.Kind == DashboardFeatureReviewMarkerKind.SelectedGroundFeature
                ? LineWidth + 1
                : LineWidth;
            var left = center.X - halfWidth;
            var right = center.X + halfWidth;
            var bottom = center.Y - halfHeight;
            var top = center.Y + halfHeight;
            result.Add(MarkerQuad(marker, left, bottom, right, bottom + thickness));
            result.Add(MarkerQuad(marker, left, top - thickness, right, top));
            result.Add(MarkerQuad(marker, left, bottom + thickness, left + thickness, top - thickness));
            result.Add(MarkerQuad(marker, right - thickness, bottom + thickness, right, top - thickness));
        }
        return result;
    }

    public static DashboardGroundFeatureDetails? GroundFeature(
        AtlasPoint screenPoint,
        DashboardFeatureReviewOverlay? overlay,
        double zoom,
        AtlasPoint atlasPan,
        AtlasPoint livePan)
    {
        if (overlay is null || !double.IsFinite(zoom) || zoom <= 0) return null;
        foreach (var marker in overlay.Markers.Reverse())
        {
            if (marker.GroundFeatureDetails is not { } details) continue;
            var center = ScreenPoint(
                marker.WorldPosition,
                zoom,
                marker.CoordinateSpace == DashboardFeatureReviewCoordinateSpace.Atlas
                    ? atlasPan
                    : livePan);
            if (center is null) continue;
            var halfWidth = Math.Max(
                6,
                (marker.WorldSize?.Width ?? MarkerSize) * zoom * .5);
            var halfHeight = Math.Max(
                6,
                (marker.WorldSize?.Height ?? MarkerSize) * zoom * .5);
            if (Math.Abs(screenPoint.X - center.Value.X) <= halfWidth
                && Math.Abs(screenPoint.Y - center.Value.Y) <= halfHeight)
            {
                return details;
            }
        }
        return null;
    }

    public static IReadOnlyList<DashboardOverlayQuad> LineQuads(
        DashboardFeatureReviewOverlay? overlay,
        double zoom,
        AtlasPoint pan)
    {
        if (overlay is null) return [];
        var result = new List<DashboardOverlayQuad>();
        foreach (var line in overlay.Lines)
        {
            var start = ScreenPoint(line.Start, zoom, pan);
            var end = ScreenPoint(line.End, zoom, pan);
            if (start is null || end is null) continue;
            var dx = end.Value.X - start.Value.X;
            var dy = end.Value.Y - start.Value.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length <= double.Epsilon) continue;
            var offset = new AtlasPoint(
                -dy / length * LineWidth * .5,
                dx / length * LineWidth * .5);
            result.Add(new DashboardOverlayQuad(
                null,
                line.Kind,
                [
                    new AtlasPoint(start.Value.X - offset.X, start.Value.Y - offset.Y),
                    new AtlasPoint(start.Value.X + offset.X, start.Value.Y + offset.Y),
                    new AtlasPoint(end.Value.X - offset.X, end.Value.Y - offset.Y),
                    new AtlasPoint(end.Value.X + offset.X, end.Value.Y + offset.Y),
                ]));
            if (result.Count == MaximumLines) break;
        }
        return result;
    }

    private static DashboardOverlayQuad MarkerQuad(
        DashboardFeatureReviewMarker marker,
        double left,
        double bottom,
        double right,
        double top) =>
        new(
            marker.Kind,
            null,
            [
                new AtlasPoint(left, bottom),
                new AtlasPoint(right, bottom),
                new AtlasPoint(left, top),
                new AtlasPoint(right, top),
            ],
            Math.Clamp(marker.Opacity, 0, 1));

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}

public readonly record struct DashboardRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double MinX => Math.Min(X, X + Width);
    public double MaxY => Math.Max(Y, Y + Height);
    public bool IsValid => double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0
        && Height > 0;
}

/// <summary>
/// Converts review evidence into the same atlas coordinates used by the live
/// viewport. Live diagnostics use the capture rectangle; persistent evidence
/// and room topology remain in atlas space.
/// </summary>
public static class LiveFeatureReviewOverlay
{
    public static DashboardFeatureReviewOverlay Make(
        GroundHypothesisTrackingResult? groundHypotheses = null,
        IReadOnlyList<VisualRoomPortal>? transitionPortals = null,
        DashboardGroundFeatureDetails? selectedGroundFeature = null,
        DashboardRect liveBounds = default)
    {
        var markers = new List<DashboardFeatureReviewMarker>();
        var lines = new List<DashboardFeatureReviewLine>();
        AppendGroundHypotheses(
            groundHypotheses, selectedGroundFeature, liveBounds, markers, lines);
        AppendTransitionPortals(transitionPortals ?? [], markers, lines);
        return new DashboardFeatureReviewOverlay(markers, lines);
    }

    private static void AppendTransitionPortals(
        IReadOnlyList<VisualRoomPortal> portals,
        ICollection<DashboardFeatureReviewMarker> markers,
        ICollection<DashboardFeatureReviewLine> lines)
    {
        foreach (var portal in portals)
        {
            var doors = new[]
            {
                (X: portal.LeftDoorWorldX, Range: portal.LeftDoorYRange),
                (X: portal.RightDoorWorldX, Range: portal.RightDoorYRange),
            };
            if (!doors.All(door => double.IsFinite(door.X)
                && double.IsFinite(door.Range.Lower)
                && double.IsFinite(door.Range.Upper)
                && door.Range.Upper > door.Range.Lower))
                continue;
            foreach (var door in doors)
            {
                markers.Add(new DashboardFeatureReviewMarker(
                    new AtlasPoint(door.X, (door.Range.Lower + door.Range.Upper) * .5),
                    DashboardFeatureReviewMarkerKind.TransitionDoor,
                    new WorldSize(5, door.Range.Upper - door.Range.Lower),
                    Opacity: .95));
            }
            lines.Add(new DashboardFeatureReviewLine(
                new AtlasPoint(portal.LeftDoorWorldX,
                    (portal.LeftDoorYRange.Lower + portal.LeftDoorYRange.Upper) * .5),
                new AtlasPoint(portal.RightDoorWorldX,
                    (portal.RightDoorYRange.Lower + portal.RightDoorYRange.Upper) * .5),
                DashboardFeatureReviewLineKind.RoomTransition));
        }
    }

    private static void AppendGroundHypotheses(
        GroundHypothesisTrackingResult? hypotheses,
        DashboardGroundFeatureDetails? selectedGroundFeature,
        DashboardRect liveBounds,
        ICollection<DashboardFeatureReviewMarker> markers,
        ICollection<DashboardFeatureReviewLine> lines)
    {
        if (hypotheses is null || !liveBounds.IsValid) return;
        var selectedKey = selectedGroundFeature is null
            ? ((int SegmentId, int SequenceIndex)?)null
            : (selectedGroundFeature.SegmentId, selectedGroundFeature.SequenceIndex);
        var detailsByKey = new Dictionary<(int SegmentId, int SequenceIndex), DashboardGroundFeatureDetails>();
        foreach (var feature in hypotheses.AtlasFeatures)
        {
            var position = feature.AtlasPosition;
            if (!Finite(position)) continue;
            var key = (feature.SegmentId, feature.SequenceIndex);
            var details = new DashboardGroundFeatureDetails(
                feature.SegmentId,
                feature.SequenceIndex,
                position,
                feature.ReferencePixels,
                feature.ReferenceOpacity);
            var selected = key == selectedKey;
            detailsByKey[key] = details;
            markers.Add(new DashboardFeatureReviewMarker(
                position,
                selected
                    ? DashboardFeatureReviewMarkerKind.SelectedGroundFeature
                    : DashboardFeatureReviewMarkerKind.GroundFeature,
                new WorldSize(
                    GroundHypothesisTracker.FeatureWidth,
                    GroundHypothesisTracker.FeatureHeight),
                details,
                Opacity: selected ? 1 : FeatureOpacity(feature.ReferenceOpacity)));
        }
        foreach (var feature in hypotheses.Features)
        {
            if (feature.Classification == GroundHypothesisClassification.Candidate) continue;
            var key = (feature.SegmentId, feature.SequenceIndex);
            detailsByKey.TryGetValue(key, out var details);
            var selected = key == selectedKey;
            var position = new AtlasPoint(
                liveBounds.MinX + feature.ImageRect.X + feature.ImageRect.Width * .5,
                liveBounds.MaxY - feature.ImageRect.Y - feature.ImageRect.Height * .5);
            if (!Finite(position)) continue;
            markers.Add(new DashboardFeatureReviewMarker(
                position,
                selected
                    ? DashboardFeatureReviewMarkerKind.SelectedGroundFeature
                    : MarkerKind(feature.Classification),
                new WorldSize(
                    GroundHypothesisTracker.FeatureWidth,
                    GroundHypothesisTracker.FeatureHeight),
                details,
                DashboardFeatureReviewCoordinateSpace.Live,
                true,
                1));
        }
        foreach (var review in GroundLineDetector.FeatureHypothesisPresenceReviews(
                     hypotheses.LineReviews))
        {
            var y = liveBounds.MaxY - review.Line.Row - .5;
            var start = new AtlasPoint(
                liveBounds.MinX + review.Line.XSpan.Start, y);
            var end = new AtlasPoint(
                liveBounds.MinX + review.Line.XSpan.EndInclusive + 1, y);
            if (!Finite(start) || !Finite(end)) continue;
            lines.Add(new DashboardFeatureReviewLine(
                start,
                end,
                DashboardFeatureReviewLineKind.CurrentGround,
                DashboardFeatureReviewCoordinateSpace.Live));
        }
        foreach (var line in hypotheses.AtlasLines)
        {
            if (!Finite(line.AtlasStart) || !Finite(line.AtlasEnd)) continue;
            lines.Add(new DashboardFeatureReviewLine(
                line.AtlasStart,
                line.AtlasEnd,
                DashboardFeatureReviewLineKind.PersistentGround));
        }
    }

    private static DashboardFeatureReviewMarkerKind MarkerKind(
        GroundHypothesisClassification classification) => classification switch
        {
            GroundHypothesisClassification.GroundPlane =>
                DashboardFeatureReviewMarkerKind.CameraConsistentGround,
            GroundHypothesisClassification.GlobalMatch =>
                DashboardFeatureReviewMarkerKind.GroundFeature,
            GroundHypothesisClassification.Occluded =>
                DashboardFeatureReviewMarkerKind.OccludedGroundFeature,
            GroundHypothesisClassification.DepthError =>
                DashboardFeatureReviewMarkerKind.GroundFeatureDepthError,
            _ => DashboardFeatureReviewMarkerKind.OccludedGroundFeature,
        };

    private static double FeatureOpacity(IReadOnlyList<byte> values) =>
        values.Count == GroundHypothesisTracker.FeaturePixelCount
            ? Math.Max(.15, values.Sum(value => (int)value)
                / (double)(values.Count * 255))
            : .15;

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}

public static class DashboardTileIdentity
{
    public static Guid For(HackerAtlasLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return FromId(layer.Id);
    }

    public static Guid FromId(long id)
    {
        var bits = unchecked((ulong)id);
        var high = bits >> 48;
        var low = bits & 0x0000_FFFF_FFFF_FFFFUL;
        return Guid.Parse($"7A11A500-{high:X4}-4000-8000-{low:X12}");
    }
}
