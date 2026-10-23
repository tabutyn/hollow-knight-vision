namespace HollowKnightVision.Windows.Core;

public sealed record HackerCapturedFrame(
    int UnityFrame,
    BgraFrame Image,
    double CapturedAt,
    bool IntegratesAtlas);

public sealed record HackerMatchedFrame(
    HackerCapturedFrame Capture,
    ReceiverGroundTruthSample Sample,
    double SampleObservedAt);

/// <summary>
/// Joins capture and mod telemetry by Unity's rendered frame number. It never
/// falls back to timestamps: a missing side means the frame is not atlas data.
/// </summary>
public sealed class HackerFrameSynchronizer
{
    private sealed record SampleValue(
        ReceiverGroundTruthSample Sample,
        double ObservedAt);

    private readonly object gate = new();
    private readonly int capacity;
    private readonly Dictionary<int, HackerCapturedFrame> captures = [];
    private readonly Dictionary<int, SampleValue> samples = [];
    private readonly List<int> captureOrder = [];
    private readonly List<int> sampleOrder = [];

    public HackerFrameSynchronizer(int capacity = 180)
    {
        this.capacity = Math.Max(4, capacity);
    }

    public static int Key(long unityFrame) =>
        unchecked((int)((ulong)unityFrame & 0x00FF_FFFFUL));

    public HackerMatchedFrame? Append(HackerCapturedFrame capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (gate)
        {
            if (samples.Remove(capture.UnityFrame, out var value))
            {
                sampleOrder.RemoveAll(frame => frame == capture.UnityFrame);
                return new HackerMatchedFrame(capture, value.Sample, value.ObservedAt);
            }
            if (!captures.ContainsKey(capture.UnityFrame))
            {
                captureOrder.Add(capture.UnityFrame);
            }
            captures[capture.UnityFrame] = capture;
            Trim(captures, captureOrder);
            return null;
        }
    }

    public HackerMatchedFrame? Append(
        ReceiverGroundTruthSample sample,
        double observedAt)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.CameraAvailable || !sample.HasFiniteCoordinates()) return null;
        var key = Key(sample.UnityFrame);
        lock (gate)
        {
            if (captures.Remove(key, out var capture))
            {
                captureOrder.RemoveAll(frame => frame == key);
                return new HackerMatchedFrame(capture, sample, observedAt);
            }
            if (!samples.ContainsKey(key)) sampleOrder.Add(key);
            samples[key] = new SampleValue(sample, observedAt);
            Trim(samples, sampleOrder);
            return null;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            captures.Clear();
            samples.Clear();
            captureOrder.Clear();
            sampleOrder.Clear();
        }
    }

    private void Trim<T>(Dictionary<int, T> values, List<int> order)
    {
        while (order.Count > capacity)
        {
            values.Remove(order[0]);
            order.RemoveAt(0);
        }
    }
}

public readonly record struct HackerAtlasRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double MinX => Math.Min(X, X + Width);
    public double MaxX => Math.Max(X, X + Width);
    public double MinY => Math.Min(Y, Y + Height);
    public double MaxY => Math.Max(Y, Y + Height);
    public double MidX => X + Width * .5;
    public double MidY => Y + Height * .5;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public HackerAtlasRect Offset(double dx, double dy) =>
        new(X + dx, Y + dy, Width, Height);

    public HackerAtlasRect Inset(double dx, double dy) =>
        new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public bool Intersects(HackerAtlasRect other) =>
        MinX < other.MaxX
        && other.MinX < MaxX
        && MinY < other.MaxY
        && other.MinY < MaxY;

    public HackerAtlasRect Union(HackerAtlasRect other) => new(
        Math.Min(MinX, other.MinX),
        Math.Min(MinY, other.MinY),
        Math.Max(MaxX, other.MaxX) - Math.Min(MinX, other.MinX),
        Math.Max(MaxY, other.MaxY) - Math.Min(MinY, other.MinY));
}

public enum HackerRoomSide
{
    Left,
    Right,
    Bottom,
    Top
}

public static class HackerRoomSideExtensions
{
    public static HackerRoomSide Opposite(this HackerRoomSide side) => side switch
    {
        HackerRoomSide.Left => HackerRoomSide.Right,
        HackerRoomSide.Right => HackerRoomSide.Left,
        HackerRoomSide.Bottom => HackerRoomSide.Top,
        HackerRoomSide.Top => HackerRoomSide.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    public static bool IsHorizontal(this HackerRoomSide side) =>
        side is HackerRoomSide.Left or HackerRoomSide.Right;
}

/// <summary>
/// A doorway observed while moving between two playable scenes. Coordinates
/// are room-local, so the connector remains attached while rooms are laid out.
/// </summary>
public sealed record HackerRoomConnection(
    Guid Id,
    Guid FromRoomId,
    Guid ToRoomId,
    HackerRoomSide FromSide,
    HackerRoomSide ToSide,
    double FromCoordinate,
    double ToCoordinate);

/// <summary>
/// One independently movable Hacker room. Bounds are room-local while
/// Position places the room in Hacker atlas space.
/// </summary>
public sealed record HackerAtlasRoom(
    Guid Id,
    string SceneName,
    AtlasPoint Position,
    HackerAtlasRect Bounds)
{
    public IReadOnlyList<HackerAtlasLayer> AtlasTiles { get; init; } = [];
    public HackerAtlasRect WorldBounds => Bounds.Offset(Position.X, Position.Y);
}

public sealed record HackerAtlasLayer(
    long Id,
    BgraFrame Image,
    HackerAtlasRect Bounds,
    ulong Revision,
    HackerAtlasRect? ContentBounds);

public sealed record HackerRoomLayout(
    IReadOnlyDictionary<Guid, AtlasPoint> Positions,
    IReadOnlySet<Guid> FreeformConnectionIds)
{
    public const double RoomGap = 24;

    public static HackerRoomLayout Empty { get; } = new(
        new Dictionary<Guid, AtlasPoint>(),
        new HashSet<Guid>());

    /// <summary>
    /// Builds the largest possible rectilinear forest. Tree edges keep their
    /// doorways exactly aligned with a small gap. A conflicting loop or an
    /// overlap starts a fanned island, joined by a free-form connector.
    /// </summary>
    public static HackerRoomLayout Make(
        IReadOnlyList<HackerAtlasRoom> rooms,
        IReadOnlyList<HackerRoomConnection> connections,
        double gap = RoomGap)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(connections);
        if (rooms.Count == 0) return Empty;

        var roomsById = rooms.ToDictionary(room => room.Id);
        var positions = new Dictionary<Guid, AtlasPoint>();
        var freeform = new HashSet<Guid>();
        var pending = connections.ToList();

        HackerAtlasRect WorldBounds(HackerAtlasRoom room, AtlasPoint position) =>
            room.Bounds.Offset(position.X, position.Y);

        AtlasPoint Endpoint(
            HackerAtlasRoom room,
            AtlasPoint position,
            HackerRoomSide side,
            double coordinate)
        {
            var bounds = WorldBounds(room, position);
            if (side.IsHorizontal())
            {
                return new AtlasPoint(
                    side == HackerRoomSide.Left ? bounds.MinX : bounds.MaxX,
                    position.Y + Math.Min(
                        Math.Max(coordinate, room.Bounds.MinY),
                        room.Bounds.MaxY));
            }
            return new AtlasPoint(
                position.X + Math.Min(
                    Math.Max(coordinate, room.Bounds.MinX),
                    room.Bounds.MaxX),
                side == HackerRoomSide.Bottom ? bounds.MinY : bounds.MaxY);
        }

        AtlasPoint ChildPosition(
            HackerAtlasRoom parent,
            AtlasPoint parentPosition,
            HackerRoomSide parentSide,
            double parentCoordinate,
            HackerAtlasRoom child,
            double childCoordinate)
        {
            var parentBounds = WorldBounds(parent, parentPosition);
            return parentSide switch
            {
                HackerRoomSide.Right => new AtlasPoint(
                    parentBounds.MaxX + gap - child.Bounds.MinX,
                    parentPosition.Y + parentCoordinate - childCoordinate),
                HackerRoomSide.Left => new AtlasPoint(
                    parentBounds.MinX - gap - child.Bounds.MaxX,
                    parentPosition.Y + parentCoordinate - childCoordinate),
                HackerRoomSide.Top => new AtlasPoint(
                    parentPosition.X + parentCoordinate - childCoordinate,
                    parentBounds.MaxY + gap - child.Bounds.MinY),
                HackerRoomSide.Bottom => new AtlasPoint(
                    parentPosition.X + parentCoordinate - childCoordinate,
                    parentBounds.MinY - gap - child.Bounds.MaxY),
                _ => throw new ArgumentOutOfRangeException(nameof(parentSide))
            };
        }

        bool Collides(HackerAtlasRoom room, AtlasPoint candidate)
        {
            var candidateBounds = WorldBounds(room, candidate).Inset(-gap * .5, -gap * .5);
            return positions.Any(pair =>
                pair.Key != room.Id
                && roomsById.TryGetValue(pair.Key, out var placedRoom)
                && candidateBounds.Intersects(
                    WorldBounds(placedRoom, pair.Value).Inset(-gap * .5, -gap * .5)));
        }

        AtlasPoint FannedPosition(
            HackerAtlasRoom room,
            AtlasPoint candidate,
            HackerRoomSide connectionSide)
        {
            if (!Collides(room, candidate)) return candidate;
            var stride = connectionSide.IsHorizontal()
                ? Math.Max(64, room.Bounds.Height + gap)
                : Math.Max(64, room.Bounds.Width + gap);
            for (var distance = 1; distance <= Math.Max(8, rooms.Count * 2); distance++)
            {
                foreach (var sign in new[] { 1d, -1d })
                {
                    var shifted = connectionSide.IsHorizontal()
                        ? candidate with { Y = candidate.Y + distance * stride * sign }
                        : candidate with { X = candidate.X + distance * stride * sign };
                    if (!Collides(room, shifted)) return shifted;
                }
            }
            return candidate;
        }

        void Place(
            Guid childId,
            Guid parentId,
            HackerRoomSide parentSide,
            HackerRoomSide childSide,
            double parentCoordinate,
            double childCoordinate,
            Guid connectionId)
        {
            _ = childSide;
            if (!roomsById.TryGetValue(parentId, out var parent)
                || !roomsById.TryGetValue(childId, out var child)
                || !positions.TryGetValue(parentId, out var parentPosition)) return;
            var candidate = ChildPosition(
                parent,
                parentPosition,
                parentSide,
                parentCoordinate,
                child,
                childCoordinate);
            var fanned = FannedPosition(child, candidate, parentSide);
            positions[childId] = fanned;
            if (fanned != candidate) freeform.Add(connectionId);
        }

        while (pending.Count > 0)
        {
            var progressed = false;
            var remainder = new List<HackerRoomConnection>();
            foreach (var connection in pending)
            {
                if (!roomsById.TryGetValue(connection.FromRoomId, out var fromRoom)
                    || !roomsById.TryGetValue(connection.ToRoomId, out var toRoom)) continue;
                var fromPlaced = positions.ContainsKey(connection.FromRoomId);
                var toPlaced = positions.ContainsKey(connection.ToRoomId);
                if (!fromPlaced && !toPlaced)
                {
                    if (positions.Count == 0)
                    {
                        positions[fromRoom.Id] = fromRoom.Position;
                    }
                    else
                    {
                        remainder.Add(connection);
                        continue;
                    }
                }
                if (positions.ContainsKey(connection.FromRoomId)
                    && !positions.ContainsKey(connection.ToRoomId))
                {
                    Place(
                        toRoom.Id,
                        fromRoom.Id,
                        connection.FromSide,
                        connection.ToSide,
                        connection.FromCoordinate,
                        connection.ToCoordinate,
                        connection.Id);
                    progressed = true;
                }
                else if (positions.ContainsKey(connection.ToRoomId)
                    && !positions.ContainsKey(connection.FromRoomId))
                {
                    Place(
                        fromRoom.Id,
                        toRoom.Id,
                        connection.ToSide,
                        connection.FromSide,
                        connection.ToCoordinate,
                        connection.FromCoordinate,
                        connection.Id);
                    progressed = true;
                }
                else if (positions.TryGetValue(fromRoom.Id, out var fromPosition)
                    && positions.TryGetValue(toRoom.Id, out var toPosition))
                {
                    var fromPoint = Endpoint(
                        fromRoom,
                        fromPosition,
                        connection.FromSide,
                        connection.FromCoordinate);
                    var toPoint = Endpoint(
                        toRoom,
                        toPosition,
                        connection.ToSide,
                        connection.ToCoordinate);
                    var aligned = connection.FromSide.IsHorizontal()
                        ? Math.Abs(fromPoint.Y - toPoint.Y) < .5
                        : Math.Abs(fromPoint.X - toPoint.X) < .5;
                    if (!aligned) freeform.Add(connection.Id);
                    progressed = true;
                }
            }
            pending = remainder;
            if (!progressed
                && pending.FirstOrDefault() is { } disconnected
                && roomsById.TryGetValue(disconnected.FromRoomId, out var room))
            {
                positions[room.Id] = room.Position;
            }
        }

        foreach (var room in rooms)
        {
            positions.TryAdd(room.Id, room.Position);
        }
        return new HackerRoomLayout(positions, freeform);
    }
}

public static class HackerAtlasCameraPlacement
{
    public static AtlasPoint CameraPosition(GroundTruthCameraTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new AtlasPoint(
            transform.CameraWorld.X * transform.PixelsPerWorldUnitX,
            transform.CameraWorld.Y * transform.PixelsPerWorldUnitY);
    }

    public static HackerAtlasRect LiveBounds(
        BgraFrame image,
        AtlasPoint position,
        AtlasPoint anchor)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new HackerAtlasRect(
            Math.Floor(position.X - anchor.X),
            Math.Floor(position.Y - anchor.Y),
            image.Width,
            image.Height);
    }
}

public sealed record HackerPreparedFrame(
    BgraFrame LiveImage,
    HackerAtlasRect LiveBounds,
    AtlasPoint FocusPoint,
    GroundTruthCameraTransform CameraTransform,
    ReceiverGroundTruthSample Sample,
    AtlasPoint AnchorPosition,
    string SceneName,
    int UnityFrame,
    double CapturedAt,
    bool IntegratesAtlas,
    bool RoomPlacementReady);

public sealed record HackerAtlasOutput(
    IReadOnlyList<HackerAtlasLayer> AtlasTiles,
    HackerAtlasRect? AtlasBounds,
    IReadOnlyList<HackerAtlasRoom> Rooms,
    IReadOnlyList<HackerRoomConnection> Connections,
    string SceneName,
    int UnityFrame,
    int RoomCount,
    IReadOnlyList<string> KnownScenes);

/// <summary>
/// Prepares every exact capture/telemetry match for immediate presentation.
/// This remains independent from the slower atlas compositor so atlas work can
/// never limit the live Hacker frame rate.
/// </summary>
public sealed class HackerFramePreparer
{
    private sealed record PlayableReference(
        HackerAtlasRect LiveBounds,
        AtlasPoint? HeroScreenPoint,
        AtlasPoint Velocity,
        bool FacingRight);

    private readonly object gate = new();
    private readonly Dictionary<string, AtlasPoint> anchorPositions = new(StringComparer.Ordinal);
    private PlayableReference? lastPlayableReference;

    public void Reset()
    {
        lock (gate)
        {
            anchorPositions.Clear();
            lastPlayableReference = null;
        }
    }

    public HackerPreparedFrame? Process(HackerMatchedFrame match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (gate)
        {
            var image = match.Capture.Image;
            var transform = GroundTruthCameraTransform.FromSample(
                match.Sample,
                match.SampleObservedAt,
                new WorldSize(image.Width, image.Height));
            if (transform is null) return null;

            var cleanedFrame = FrameReplacingMarker(image);
            var position = HackerAtlasCameraPlacement.CameraPosition(transform);
            var heroScreenPoint = HeroScreenPoint(
                match.Sample,
                image.Width,
                image.Height,
                requireInBounds: true);
            var suppliesHeroProjection = SuppliesHeroProjection(match.Sample);
            var canInitializeRoom = match.Sample.HeroAvailable
                && (heroScreenPoint is not null || !suppliesHeroProjection);

            AtlasPoint anchor;
            bool roomPlacementReady;
            if (anchorPositions.TryGetValue(transform.SceneName, out var knownAnchor))
            {
                anchor = knownAnchor;
                roomPlacementReady = true;
            }
            else if (canInitializeRoom)
            {
                var origin = TransitionOrigin(
                    image.Width,
                    image.Height,
                    heroScreenPoint,
                    lastPlayableReference);
                anchor = new AtlasPoint(position.X - origin.X, position.Y - origin.Y);
                anchorPositions[transform.SceneName] = anchor;
                roomPlacementReady = true;
            }
            else if (lastPlayableReference is { } previous)
            {
                // Transient cutscene scenes do not own atlas space. Keep their
                // live frame at the doorway instead of flashing to world zero.
                anchor = new AtlasPoint(
                    position.X - previous.LiveBounds.MinX,
                    position.Y - previous.LiveBounds.MinY);
                roomPlacementReady = false;
            }
            else
            {
                anchor = position;
                roomPlacementReady = false;
            }

            var liveBounds = HackerAtlasCameraPlacement.LiveBounds(
                image,
                position,
                anchor);
            if (roomPlacementReady
                && match.Sample.HeroAvailable
                && (heroScreenPoint is not null || !suppliesHeroProjection))
            {
                lastPlayableReference = new PlayableReference(
                    liveBounds,
                    heroScreenPoint,
                    new AtlasPoint(match.Sample.VelocityX, match.Sample.VelocityY),
                    match.Sample.FacingRight);
            }

            return new HackerPreparedFrame(
                cleanedFrame,
                liveBounds,
                new AtlasPoint(liveBounds.MidX, liveBounds.MidY),
                transform,
                match.Sample,
                anchor,
                transform.SceneName,
                match.Capture.UnityFrame,
                match.Capture.CapturedAt,
                match.Capture.IntegratesAtlas,
                roomPlacementReady);
        }
    }

    private static AtlasPoint TransitionOrigin(
        int imageWidth,
        int imageHeight,
        AtlasPoint? heroScreenPoint,
        PlayableReference? previous)
    {
        if (previous is null) return new AtlasPoint(0, 0);
        if (previous.HeroScreenPoint is { } oldHero
            && heroScreenPoint is { } newHero)
        {
            return new AtlasPoint(
                previous.LiveBounds.MinX + oldHero.X - newHero.X,
                previous.LiveBounds.MinY + oldHero.Y - newHero.Y);
        }

        var overlapX = Math.Min(32, Math.Round(
            imageWidth * .1,
            MidpointRounding.AwayFromZero));
        var overlapY = Math.Min(24, Math.Round(
            imageHeight * .1,
            MidpointRounding.AwayFromZero));
        if (Math.Abs(previous.Velocity.Y) > Math.Abs(previous.Velocity.X)
            && Math.Abs(previous.Velocity.Y) > .05)
        {
            return new AtlasPoint(
                previous.LiveBounds.MinX,
                previous.Velocity.Y > 0
                    ? previous.LiveBounds.MaxY - overlapY
                    : previous.LiveBounds.MinY - imageHeight + overlapY);
        }

        var exitsRight = Math.Abs(previous.Velocity.X) > .05
            ? previous.Velocity.X > 0
            : previous.FacingRight;
        return new AtlasPoint(
            exitsRight
                ? previous.LiveBounds.MaxX - overlapX
                : previous.LiveBounds.MinX - imageWidth + overlapX,
            previous.LiveBounds.MinY);
    }

    private static AtlasPoint? HeroScreenPoint(
        ReceiverGroundTruthSample sample,
        int frameWidth,
        int frameHeight,
        bool requireInBounds)
    {
        if (!sample.HeroAvailable
            || sample.HeroScreenX is not { } screenX
            || sample.HeroScreenY is not { } screenY
            || sample.ProjectionPixelWidth is not > 0
            || sample.ProjectionPixelHeight is not > 0)
        {
            return null;
        }
        var point = new AtlasPoint(
            screenX * frameWidth / sample.ProjectionPixelWidth.Value,
            screenY * frameHeight / sample.ProjectionPixelHeight.Value);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        if (requireInBounds
            && (point.X < 0 || point.X > frameWidth
                || point.Y < 0 || point.Y > frameHeight))
        {
            return null;
        }
        return point;
    }

    private static bool SuppliesHeroProjection(ReceiverGroundTruthSample sample) =>
        sample.HeroScreenX is not null
        && sample.HeroScreenY is not null
        && sample.ProjectionPixelWidth is > 0
        && sample.ProjectionPixelHeight is > 0;

    private static BgraFrame FrameReplacingMarker(BgraFrame image)
    {
        var markerWidth = Math.Min(
            image.Width,
            checked((int)Math.Ceiling(image.Width * 120d / 640)));
        var markerHeight = Math.Min(
            image.Height,
            checked((int)Math.Ceiling(image.Height * 6d / 360)));
        if (markerWidth <= 0
            || markerHeight <= 0
            || markerHeight * 2 > image.Height)
        {
            return image;
        }

        var pixels = image.Pixels.ToArray();
        var copyBytes = checked(markerWidth * BgraFrame.BytesPerPixel);
        for (var row = 0; row < markerHeight; row++)
        {
            Buffer.BlockCopy(
                image.Pixels,
                checked((row + markerHeight) * image.Stride),
                pixels,
                checked(row * image.Stride),
                copyBytes);
        }
        return new BgraFrame(
            image.Width,
            image.Height,
            image.Stride,
            pixels,
            image.CaptureId,
            image.ObservedAt);
    }
}

/// <summary>
/// Direct-camera atlas with storage and origin wholly separate from Gameplay.
/// It sees every prepared frame to detect fades, but rasterizes only cadence
/// frames and fade-recovery references.
/// </summary>
public sealed class HackerAtlasPipeline
{
    private sealed record FadeReference(
        HackerPreparedFrame Prepared,
        FrameSignalProfile Profile,
        AtlasPoint CameraPosition);

    private sealed class RoomState
    {
        internal RoomState(string sceneName, AtlasPoint position)
        {
            SceneName = sceneName;
            Position = position;
            Composer = new LiveTiledAtlas(
                anchorPosition: new AtlasPoint(0, 0),
                sourceCacheLimit: 48,
                pixelPolicy: LiveTiledAtlasPixelPolicy.NewestQuilted);
        }

        internal Guid Id { get; } = Guid.NewGuid();
        internal string SceneName { get; }
        internal AtlasPoint Position { get; }
        internal LiveTiledAtlas Composer { get; }
        internal HudStencilTracker HudStencilTracker { get; } = new();
        internal FadeReference? FadeReference { get; set; }
        internal int? LastIntegratedUnityFrame { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, RoomState> rooms = new(StringComparer.Ordinal);
    private readonly List<HackerRoomConnection> connections = [];
    private HackerPreparedFrame? lastPlayableFrame;
    private long nextObservationId;
    private ulong revision;

    public void Reset()
    {
        lock (gate)
        {
            rooms.Clear();
            connections.Clear();
            lastPlayableFrame = null;
            nextObservationId = 0;
            revision = unchecked(revision + 1);
        }
    }

    public HackerAtlasOutput? Process(HackerPreparedFrame prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        lock (gate)
        {
            var profile = FrameRegionRenderer.SignalProfile(prepared.LiveImage);
            var position = HackerAtlasCameraPlacement.CameraPosition(
                prepared.CameraTransform);
            RoomState state;
            if (!rooms.TryGetValue(prepared.SceneName, out state!))
            {
                // A cutscene may expose a camera and scene name without a
                // playable room. It must not acquire atlas ownership.
                if (!prepared.RoomPlacementReady
                    || !prepared.Sample.HeroAvailable
                    || !profile.HasGameplaySignal)
                {
                    return null;
                }
                state = new RoomState(
                    prepared.SceneName,
                    new AtlasPoint(prepared.LiveBounds.MinX, prepared.LiveBounds.MinY));
                rooms[prepared.SceneName] = state;
            }

            if (IsFadeOrCutscene(prepared, profile, position, state.FadeReference))
            {
                if (state.FadeReference is not { } reference
                    || state.LastIntegratedUnityFrame == reference.Prepared.UnityFrame)
                {
                    return null;
                }
                return Integrate(reference.Prepared, state);
            }

            if (lastPlayableFrame is { } previous
                && !string.Equals(
                    previous.SceneName,
                    prepared.SceneName,
                    StringComparison.Ordinal)
                && rooms.TryGetValue(previous.SceneName, out var previousState))
            {
                previousState.Composer.EndTemporalWindow();
                state.Composer.EndTemporalWindow();
                RecordConnection(previous, previousState, prepared, state);
            }
            lastPlayableFrame = prepared;

            UpdateFadeReference(prepared, profile, position, state);
            return prepared.IntegratesAtlas ? Integrate(prepared, state) : null;
        }
    }

    private HackerAtlasOutput? Integrate(
        HackerPreparedFrame prepared,
        RoomState state)
    {
        var image = prepared.LiveImage;
        var hudStencil = state.HudStencilTracker.Observe(
            image,
            prepared.Sample.HeroAvailable,
            prepared.CapturedAt,
            generation: 0);
        var regions = SceneRegions.HackerAtlas(
            prepared.Sample,
            image.Width,
            image.Height,
            hudStencil);
        var atlasFrame = FrameRegionRenderer.MapFrame(image, regions);

        var observationId = nextObservationId;
        nextObservationId = checked(nextObservationId + 1);
        var localPosition = new AtlasPoint(
            prepared.LiveBounds.MinX - state.Position.X,
            prepared.LiveBounds.MinY - state.Position.Y);
        if (!state.Composer.Insert(
                observationId,
                atlasFrame,
                image.Width,
                localPosition,
                captureIdentity: prepared.UnityFrame,
                timestamp: prepared.CapturedAt))
        {
            return null;
        }
        state.LastIntegratedUnityFrame = prepared.UnityFrame;
        revision = unchecked(revision + 1);

        var roomSnapshots = rooms.Values
            .OrderBy(room => room.SceneName, StringComparer.Ordinal)
            .Select(room =>
            {
                var snapshot = room.Composer.Snapshot;
                var contentBounds = Rect(snapshot.ContentBounds);
                return new HackerAtlasRoom(
                    room.Id,
                    room.SceneName,
                    room.Position,
                    contentBounds)
                {
                    AtlasTiles = snapshot.Tiles.Select(tile => new HackerAtlasLayer(
                        tile.Id,
                        tile.Image,
                        Rect(tile.WorldBounds),
                        revision,
                        contentBounds)).ToArray()
                };
            })
            .ToArray();

        HackerAtlasRect? atlasBounds = null;
        foreach (var room in roomSnapshots)
        {
            if (room.Bounds.IsEmpty) continue;
            atlasBounds = atlasBounds is { } existing
                ? existing.Union(room.WorldBounds)
                : room.WorldBounds;
        }

        var flattened = new List<HackerAtlasLayer>();
        foreach (var room in roomSnapshots)
        {
            foreach (var tile in room.AtlasTiles)
            {
                flattened.Add(new HackerAtlasLayer(
                    flattened.Count,
                    tile.Image,
                    tile.Bounds.Offset(room.Position.X, room.Position.Y),
                    revision,
                    atlasBounds));
            }
        }

        return new HackerAtlasOutput(
            flattened,
            atlasBounds,
            roomSnapshots,
            connections.ToArray(),
            prepared.SceneName,
            prepared.UnityFrame,
            rooms.Count,
            rooms.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    private void RecordConnection(
        HackerPreparedFrame previous,
        RoomState previousRoom,
        HackerPreparedFrame current,
        RoomState currentRoom)
    {
        var fromSide = TransitionSide(previous, current);
        var toSide = fromSide.Opposite();
        var duplicate = connections.Any(connection =>
            connection.FromRoomId == previousRoom.Id
            && connection.ToRoomId == currentRoom.Id
            && connection.FromSide == fromSide
            && connection.ToSide == toSide
            || connection.FromRoomId == currentRoom.Id
            && connection.ToRoomId == previousRoom.Id
            && connection.FromSide == toSide
            && connection.ToSide == fromSide);
        if (duplicate) return;
        connections.Add(new HackerRoomConnection(
            Guid.NewGuid(),
            previousRoom.Id,
            currentRoom.Id,
            fromSide,
            toSide,
            DoorwayCoordinate(previous, previousRoom, fromSide),
            DoorwayCoordinate(current, currentRoom, toSide)));
    }

    private static HackerRoomSide TransitionSide(
        HackerPreparedFrame previous,
        HackerPreparedFrame current)
    {
        if (HeroScreenPoint(
                previous.Sample,
                previous.LiveBounds.Width,
                previous.LiveBounds.Height) is { } hero)
        {
            var edgeDistances = new (HackerRoomSide Side, double Distance)[]
            {
                (HackerRoomSide.Left, hero.X),
                (HackerRoomSide.Right, previous.LiveBounds.Width - hero.X),
                (HackerRoomSide.Bottom, hero.Y),
                (HackerRoomSide.Top, previous.LiveBounds.Height - hero.Y)
            };
            var nearest = edgeDistances.MinBy(value => value.Distance);
            if (nearest.Distance
                <= Math.Min(previous.LiveBounds.Width, previous.LiveBounds.Height) * .35)
            {
                return nearest.Side;
            }
        }

        var deltaX = current.LiveBounds.MidX - previous.LiveBounds.MidX;
        var deltaY = current.LiveBounds.MidY - previous.LiveBounds.MidY;
        if (Math.Abs(deltaX) >= Math.Abs(deltaY) && Math.Abs(deltaX) > 1)
            return deltaX >= 0 ? HackerRoomSide.Right : HackerRoomSide.Left;
        if (Math.Abs(deltaY) > 1)
            return deltaY >= 0 ? HackerRoomSide.Top : HackerRoomSide.Bottom;
        if (Math.Abs(previous.Sample.VelocityY) > Math.Abs(previous.Sample.VelocityX)
            && Math.Abs(previous.Sample.VelocityY) > .05)
        {
            return previous.Sample.VelocityY >= 0
                ? HackerRoomSide.Top
                : HackerRoomSide.Bottom;
        }
        if (Math.Abs(previous.Sample.VelocityX) > .05)
            return previous.Sample.VelocityX >= 0
                ? HackerRoomSide.Right
                : HackerRoomSide.Left;
        return previous.Sample.FacingRight
            ? HackerRoomSide.Right
            : HackerRoomSide.Left;
    }

    private static double DoorwayCoordinate(
        HackerPreparedFrame prepared,
        RoomState room,
        HackerRoomSide side)
    {
        var hero = HeroScreenPoint(
                prepared.Sample,
                prepared.LiveBounds.Width,
                prepared.LiveBounds.Height)
            ?? new AtlasPoint(
                prepared.LiveBounds.Width * .5,
                prepared.LiveBounds.Height * .5);
        return side.IsHorizontal()
            ? prepared.LiveBounds.MinY + hero.Y - room.Position.Y
            : prepared.LiveBounds.MinX + hero.X - room.Position.X;
    }

    private static AtlasPoint? HeroScreenPoint(
        ReceiverGroundTruthSample sample,
        double frameWidth,
        double frameHeight)
    {
        if (!sample.HeroAvailable
            || sample.HeroScreenX is not { } screenX
            || sample.HeroScreenY is not { } screenY
            || sample.ProjectionPixelWidth is not > 0
            || sample.ProjectionPixelHeight is not > 0)
        {
            return null;
        }
        return new AtlasPoint(
            screenX * frameWidth / sample.ProjectionPixelWidth.Value,
            screenY * frameHeight / sample.ProjectionPixelHeight.Value);
    }

    private static bool IsFadeOrCutscene(
        HackerPreparedFrame prepared,
        FrameSignalProfile profile,
        AtlasPoint cameraPosition,
        FadeReference? reference)
    {
        if (!prepared.Sample.HeroAvailable || !profile.HasGameplaySignal)
            return true;
        if (reference is null) return false;
        var distance = Math.Sqrt(
            Math.Pow(cameraPosition.X - reference.CameraPosition.X, 2)
            + Math.Pow(cameraPosition.Y - reference.CameraPosition.Y, 2));
        var nearbyLimit = Math.Max(24, prepared.LiveImage.Width * .12);
        if (distance > nearbyLimit) return false;
        return profile.MeanPeak < Math.Max(6, reference.Profile.MeanPeak * .62)
            && profile.VisibleFraction < reference.Profile.VisibleFraction * .75;
    }

    private static void UpdateFadeReference(
        HackerPreparedFrame prepared,
        FrameSignalProfile profile,
        AtlasPoint cameraPosition,
        RoomState state)
    {
        if (state.FadeReference is not { } current)
        {
            state.FadeReference = new FadeReference(
                prepared,
                profile,
                cameraPosition);
            return;
        }
        var distance = Math.Sqrt(
            Math.Pow(cameraPosition.X - current.CameraPosition.X, 2)
            + Math.Pow(cameraPosition.Y - current.CameraPosition.Y, 2));
        var nearbyLimit = Math.Max(24, prepared.LiveImage.Width * .12);
        if (distance > nearbyLimit
            || profile.MeanPeak > current.Profile.MeanPeak * 1.02
            || profile.VisibleFraction > current.Profile.VisibleFraction * 1.05)
        {
            state.FadeReference = new FadeReference(
                prepared,
                profile,
                cameraPosition);
        }
    }

    private static HackerAtlasRect Rect(StencilRect rect) => new(
        rect.X,
        rect.Y,
        rect.Width,
        rect.Height);
}
