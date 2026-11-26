using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum LiveWorldModelError
{
    UnsupportedSchema,
    InvalidRevision,
    StaleRevision,
    DuplicateObservationId,
    DuplicateKeyframeId,
    DuplicateLandmarkId,
    DuplicateRelativeEdgeId,
    DuplicateLoopEdgeId,
    DanglingObservationId,
    DanglingKeyframeId,
    DanglingLandmarkId,
    InvalidGeometry,
    UnanchoredWorld,
    InvalidIslandMembership,
    InvalidDoorwayFloorConstraint
}

public sealed class LiveWorldModelException(
    LiveWorldModelError error,
    string message,
    int? relatedId = null,
    int? expectedRevision = null,
    int? actualRevision = null) : Exception(message)
{
    public LiveWorldModelError Error { get; } = error;
    public int? RelatedId { get; } = relatedId;
    public int? ExpectedRevision { get; } = expectedRevision;
    public int? ActualRevision { get; } = actualRevision;
}

public readonly record struct WorldSize(double Width, double Height);
public readonly record struct WorldRect(AtlasPoint Origin, WorldSize Size)
{
    public double X => Origin.X;
    public double Y => Origin.Y;
    public double Width => Size.Width;
    public double Height => Size.Height;
}

[JsonConverter(typeof(LiveWorldObservationJsonConverter))]
public sealed record LiveWorldObservation(
    int Id,
    int SourceObservationId,
    double Timestamp,
    int SourceWidth,
    int SourceHeight,
    double SolveWidth,
    int SampleWidth,
    IReadOnlyList<WorldRect> ExcludedRects,
    AtlasPoint LocalPose,
    ulong CaptureGeneration,
    ulong LocalEpoch,
    ulong BasisRevision,
    int RoomId,
    AtlasPoint RawPose,
    AtlasPoint OptimizedPose)
{
    public IReadOnlyList<LiveWorldWalkedFloor>? WalkedGround { get; init; }
    public double? CaptureTimestamp { get; init; }
    public LiveWorldGroundGeometry? GroundGeometry { get; init; }
    public LiveObservedFloorCandidates? ObservedFloorCandidates { get; init; }
    public WorldRect? ObservedPlayerBounds { get; init; }

    public static LiveWorldObservation Create(
        int id,
        int sourceObservationId,
        double timestamp,
        int sourceWidth,
        int sourceHeight,
        double solveWidth,
        int sampleWidth,
        IReadOnlyList<WorldRect>? excludedRects,
        AtlasPoint rawPose,
        AtlasPoint optimizedPose,
        AtlasPoint? localPose = null,
        ulong captureGeneration = 0,
        ulong localEpoch = 0,
        ulong basisRevision = 0,
        int roomId = 0) => new(
            id,
            sourceObservationId,
            timestamp,
            sourceWidth,
            sourceHeight,
            solveWidth,
            sampleWidth,
            excludedRects ?? [],
            localPose ?? rawPose,
            captureGeneration,
            localEpoch,
            basisRevision,
            roomId,
            rawPose,
            optimizedPose);
}

internal sealed class LiveWorldObservationJsonConverter
    : JsonConverter<LiveWorldObservation>
{
    public override LiveWorldObservation Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Deserialize<Payload>(ref reader, options)
            ?? throw new JsonException("Live-world observation is missing.");
        return new LiveWorldObservation(
            payload.Id,
            payload.SourceObservationId,
            payload.Timestamp,
            payload.SourceWidth,
            payload.SourceHeight,
            payload.SolveWidth,
            payload.SampleWidth,
            payload.ExcludedRects,
            payload.LocalPose ?? payload.RawPose,
            payload.CaptureGeneration,
            payload.LocalEpoch,
            payload.BasisRevision,
            payload.RoomId,
            payload.RawPose,
            payload.OptimizedPose)
        {
            WalkedGround = payload.WalkedGround,
            CaptureTimestamp = payload.CaptureTimestamp,
            GroundGeometry = payload.GroundGeometry,
            ObservedFloorCandidates = payload.ObservedFloorCandidates,
            ObservedPlayerBounds = payload.ObservedPlayerBounds
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        LiveWorldObservation value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Payload
        {
            Id = value.Id,
            SourceObservationId = value.SourceObservationId,
            Timestamp = value.Timestamp,
            SourceWidth = value.SourceWidth,
            SourceHeight = value.SourceHeight,
            SolveWidth = value.SolveWidth,
            SampleWidth = value.SampleWidth,
            ExcludedRects = value.ExcludedRects,
            LocalPose = value.LocalPose,
            CaptureGeneration = value.CaptureGeneration,
            LocalEpoch = value.LocalEpoch,
            BasisRevision = value.BasisRevision,
            RoomId = value.RoomId,
            RawPose = value.RawPose,
            OptimizedPose = value.OptimizedPose,
            WalkedGround = value.WalkedGround,
            CaptureTimestamp = value.CaptureTimestamp,
            GroundGeometry = value.GroundGeometry,
            ObservedFloorCandidates = value.ObservedFloorCandidates,
            ObservedPlayerBounds = value.ObservedPlayerBounds
        }, options);

    private sealed class Payload
    {
        [JsonRequired] public int Id { get; init; }
        [JsonRequired] public int SourceObservationId { get; init; }
        [JsonRequired] public double Timestamp { get; init; }
        [JsonRequired] public int SourceWidth { get; init; }
        [JsonRequired] public int SourceHeight { get; init; }
        [JsonRequired] public double SolveWidth { get; init; }
        [JsonRequired] public int SampleWidth { get; init; }
        [JsonRequired] public required IReadOnlyList<WorldRect> ExcludedRects { get; init; }
        public AtlasPoint? LocalPose { get; init; }
        public ulong CaptureGeneration { get; init; }
        public ulong LocalEpoch { get; init; }
        public ulong BasisRevision { get; init; }
        public int RoomId { get; init; }
        [JsonRequired] public AtlasPoint RawPose { get; init; }
        [JsonRequired] public AtlasPoint OptimizedPose { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<LiveWorldWalkedFloor>? WalkedGround { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? CaptureTimestamp { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LiveWorldGroundGeometry? GroundGeometry { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LiveObservedFloorCandidates? ObservedFloorCandidates { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WorldRect? ObservedPlayerBounds { get; init; }
    }
}

public sealed record LiveWorldKeyframe(int Id, int ObservationId, int SourceObservationId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TopOriginFeatures { get; init; }
}

public sealed record LiveWorldLandmark(
    int Id,
    int KeyframeId,
    int SourceObservationId,
    AtlasPoint SamplePoint,
    AtlasPoint WorldPoint,
    IReadOnlyList<float> Descriptor,
    double Depth,
    double DepthConfidence)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GroundClosureOnly { get; init; }
}

public sealed record LiveWorldRelativeMotionEdge(
    int Id,
    int FromObservationId,
    int ToObservationId,
    double DeltaX,
    double DeltaY,
    int Support);

public sealed record LiveWorldLoopClosureEdge(
    int Id,
    int FromKeyframeId,
    int ToKeyframeId,
    IReadOnlyList<int> LandmarkIds,
    double DeltaX,
    double DeltaY,
    int Support,
    double Ambiguity)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ResidualPixels { get; init; }
}

/// <summary>Validated, versioned pose graph with stable visual-room ownership.</summary>
public sealed record LiveWorldSnapshot
{
    public const int CurrentSchemaVersion = 3;
    private const int WindowsTrackingSchema = 1;

    public LiveWorldSnapshot(
        int schemaVersion = WindowsTrackingSchema,
        int mapRevision = 0,
        int? anchoredObservationId = null,
        IReadOnlyList<LiveWorldObservation>? observations = null,
        IReadOnlyList<LiveWorldKeyframe>? keyframes = null,
        IReadOnlyList<LiveWorldLandmark>? landmarks = null,
        IReadOnlyList<LiveWorldRelativeMotionEdge>? relativeMotionEdges = null,
        IReadOnlyList<LiveWorldLoopClosureEdge>? loopClosureEdges = null,
        IReadOnlyList<LiveTrackingIsland>? trackingIslands = null,
        IReadOnlyList<LiveDoorwayFloorConstraint>? doorwayFloorConstraints = null)
    {
        SchemaVersion = schemaVersion;
        MapRevision = mapRevision;
        AnchoredObservationId = anchoredObservationId;
        Observations = observations ?? [];
        Keyframes = keyframes ?? [];
        Landmarks = landmarks ?? [];
        RelativeMotionEdges = relativeMotionEdges ?? [];
        LoopClosureEdges = loopClosureEdges ?? [];
        TrackingIslands = trackingIslands ?? (UsesSwiftWireFormat
            ? Observations.Select(value => LiveTrackingIsland.Legacy(value.Id, value)).ToArray()
            : []);
        DoorwayFloorConstraints = doorwayFloorConstraints ?? [];
        Validate();
    }

    public int SchemaVersion { get; }
    public int MapRevision { get; }
    public int? AnchoredObservationId { get; }
    public IReadOnlyList<LiveWorldObservation> Observations { get; }
    public IReadOnlyList<LiveWorldKeyframe> Keyframes { get; }
    public IReadOnlyList<LiveWorldLandmark> Landmarks { get; }
    public IReadOnlyList<LiveWorldRelativeMotionEdge> RelativeMotionEdges { get; }
    public IReadOnlyList<LiveWorldLoopClosureEdge> LoopClosureEdges { get; }
    public IReadOnlyList<LiveTrackingIsland> TrackingIslands { get; }
    public IReadOnlyList<LiveDoorwayFloorConstraint> DoorwayFloorConstraints { get; }
    [JsonIgnore] public bool UsesSwiftWireFormat => SchemaVersion >= 2;

    public IReadOnlySet<int> UnplacedObservationIds() => TrackingIslands
        .Where(island => island.RequiresPlacement)
        .SelectMany(island => island.Members.Select(member => member.ObservationId))
        .ToHashSet();

    public IReadOnlyList<StencilRect> CompositionMasks(LiveWorldObservation observation,
        VisualRoomCompositionBounds roomBounds = default)
    {
        var exclusions = observation.ExcludedRects.Select(rectangle =>
        {
            var mask = new StencilRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            return UsesSwiftWireFormat ? LiveWorldJson.FlipMask(mask, observation.SourceHeight) : mask;
        });
        if (roomBounds != VisualRoomCompositionBounds.Unbounded)
            exclusions = exclusions.Concat(VisualRoomCompositionMask.OmittedRects(roomBounds,
                observation.OptimizedPose, observation.SolveWidth, new(observation.SourceWidth, observation.SourceHeight)));
        return exclusions.ToArray();
    }

    public LiveWorldSnapshot ReplaceOptimizedPoses(
        IReadOnlyDictionary<int, AtlasPoint> poses,
        int baseRevision)
    {
        ArgumentNullException.ThrowIfNull(poses);
        if (baseRevision != MapRevision)
        {
            throw Error(
                LiveWorldModelError.StaleRevision,
                $"Expected map revision {MapRevision}; received {baseRevision}.",
                expectedRevision: MapRevision,
                actualRevision: baseRevision);
        }
        if (MapRevision == int.MaxValue
            || !poses.Keys.ToHashSet().SetEquals(Observations.Select(value => value.Id))
            || poses.Values.Any(point => !Finite(point)))
        {
            throw Error(LiveWorldModelError.InvalidRevision, "Replacement pose set is invalid.");
        }
        var replaced = Observations.Select(observation => observation with
        {
            OptimizedPose = poses[observation.Id]
        }).ToArray();
        var originals = Observations.ToDictionary(observation => observation.Id);
        var replacements = replaced.ToDictionary(observation => observation.Id);
        var keyframeById = Keyframes.ToDictionary(keyframe => keyframe.Id);
        var movedLandmarks = Landmarks.Select(landmark =>
        {
            if (!keyframeById.TryGetValue(landmark.KeyframeId, out var keyframe)
                || !originals.TryGetValue(keyframe.ObservationId, out var oldObservation)
                || !replacements.TryGetValue(keyframe.ObservationId, out var newObservation))
            {
                return landmark;
            }
            return landmark with
            {
                WorldPoint = new AtlasPoint(
                    landmark.WorldPoint.X
                        + (newObservation.OptimizedPose.X - oldObservation.OptimizedPose.X)
                        * landmark.Depth,
                    landmark.WorldPoint.Y
                        + (newObservation.OptimizedPose.Y - oldObservation.OptimizedPose.Y)
                        * landmark.Depth)
            };
        }).ToArray();
        var movedIslands = TrackingIslands.Select(island =>
        {
            var firstMember = island.Members[0];
            var previous = originals[firstMember.ObservationId].OptimizedPose;
            var next = replacements[firstMember.ObservationId].OptimizedPose;
            return island with { WorldFromLocal = new AtlasPoint(
                island.WorldFromLocal.X + (next.X - previous.X),
                island.WorldFromLocal.Y + (next.Y - previous.Y)) };
        }).ToArray();
        return new LiveWorldSnapshot(
            SchemaVersion,
            MapRevision + 1,
            AnchoredObservationId,
            replaced,
            Keyframes,
            movedLandmarks,
            RelativeMotionEdges,
            LoopClosureEdges,
            movedIslands,
            DoorwayFloorConstraints);
    }

    public LiveWorldSnapshot TranslateRoom(int roomId, CameraVector translation)
    {
        if (roomId < 0 || !double.IsFinite(translation.X) || !double.IsFinite(translation.Y))
        {
            throw Error(LiveWorldModelError.InvalidGeometry, "Room translation is invalid.");
        }
        var positions = Observations.ToDictionary(
            observation => observation.Id,
            observation => observation.RoomId == roomId
                ? new AtlasPoint(
                    observation.OptimizedPose.X + translation.X,
                    observation.OptimizedPose.Y + translation.Y)
                : observation.OptimizedPose);
        return ReplaceOptimizedPoses(positions, MapRevision);
    }

    public void Validate()
    {
        if (SchemaVersion is < WindowsTrackingSchema or > CurrentSchemaVersion)
        {
            throw Error(
                LiveWorldModelError.UnsupportedSchema,
                $"Unsupported live-world schema {SchemaVersion}.",
                SchemaVersion);
        }
        if (MapRevision < 0)
        {
            throw Error(LiveWorldModelError.InvalidRevision, "Map revision cannot be negative.");
        }
        Unique(Observations.Select(value => value.Id), LiveWorldModelError.DuplicateObservationId);
        Unique(Keyframes.Select(value => value.Id), LiveWorldModelError.DuplicateKeyframeId);
        Unique(Landmarks.Select(value => value.Id), LiveWorldModelError.DuplicateLandmarkId);
        Unique(RelativeMotionEdges.Select(value => value.Id), LiveWorldModelError.DuplicateRelativeEdgeId);
        Unique(LoopClosureEdges.Select(value => value.Id), LiveWorldModelError.DuplicateLoopEdgeId);

        var observationIds = Observations.Select(value => value.Id).ToHashSet();
        if (Observations.Count == 0)
        {
            if (AnchoredObservationId is not null || Keyframes.Count != 0 || Landmarks.Count != 0
                || RelativeMotionEdges.Count != 0 || LoopClosureEdges.Count != 0)
            {
                throw Error(LiveWorldModelError.UnanchoredWorld, "Empty world contains graph data.");
            }
        }
        else if (AnchoredObservationId is null || !observationIds.Contains(AnchoredObservationId.Value))
        {
            throw Error(LiveWorldModelError.UnanchoredWorld, "World has no valid anchor.");
        }
        if (Observations.Any(observation => !Valid(observation)))
        {
            throw Error(LiveWorldModelError.InvalidGeometry, "Observation geometry is invalid.");
        }

        var observationById = Observations.ToDictionary(value => value.Id);
        ValidateIslands(observationById);
        var keyframeIds = Keyframes.Select(value => value.Id).ToHashSet();
        foreach (var keyframe in Keyframes)
        {
            if (!observationById.TryGetValue(keyframe.ObservationId, out var observation))
            {
                throw Error(
                    LiveWorldModelError.DanglingObservationId,
                    $"Keyframe references missing observation {keyframe.ObservationId}.",
                    keyframe.ObservationId);
            }
            if (keyframe.Id < 0 || keyframe.SourceObservationId < 0
                || keyframe.SourceObservationId != observation.SourceObservationId)
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Keyframe geometry is invalid.");
            }
        }
        var keyframeById = Keyframes.ToDictionary(value => value.Id);
        foreach (var landmark in Landmarks)
        {
            if (!keyframeById.TryGetValue(landmark.KeyframeId, out var keyframe))
            {
                throw Error(
                    LiveWorldModelError.DanglingKeyframeId,
                    $"Landmark references missing keyframe {landmark.KeyframeId}.",
                    landmark.KeyframeId);
            }
            if (landmark.Id < 0 || landmark.SourceObservationId < 0
                || landmark.SourceObservationId != keyframe.SourceObservationId
                || !Finite(landmark.SamplePoint) || !Finite(landmark.WorldPoint)
                || landmark.Descriptor.Count == 0 || landmark.Descriptor.Any(value => !float.IsFinite(value))
                || !double.IsFinite(landmark.Depth) || landmark.Depth <= 0
                || !double.IsFinite(landmark.DepthConfidence)
                || landmark.DepthConfidence is < 0 or > 1)
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Landmark geometry is invalid.");
            }
        }
        foreach (var edge in RelativeMotionEdges)
        {
            var missing = !observationIds.Contains(edge.FromObservationId)
                ? edge.FromObservationId
                : !observationIds.Contains(edge.ToObservationId) ? edge.ToObservationId : (int?)null;
            if (missing is not null)
            {
                throw Error(
                    LiveWorldModelError.DanglingObservationId,
                    $"Relative edge references missing observation {missing}.",
                    missing);
            }
            if (edge.Id < 0 || edge.FromObservationId == edge.ToObservationId
                || observationById[edge.FromObservationId].RoomId
                    != observationById[edge.ToObservationId].RoomId
                || !double.IsFinite(edge.DeltaX) || !double.IsFinite(edge.DeltaY)
                || edge.Support <= 0)
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Relative edge is invalid.");
            }
        }
        var landmarkById = Landmarks.ToDictionary(value => value.Id);
        var landmarkIds = landmarkById.Keys.ToHashSet();
        foreach (var edge in LoopClosureEdges)
        {
            var missingKeyframe = !keyframeIds.Contains(edge.FromKeyframeId)
                ? edge.FromKeyframeId
                : !keyframeIds.Contains(edge.ToKeyframeId) ? edge.ToKeyframeId : (int?)null;
            if (missingKeyframe is not null)
            {
                throw Error(
                    LiveWorldModelError.DanglingKeyframeId,
                    $"Loop edge references missing keyframe {missingKeyframe}.",
                    missingKeyframe);
            }
            var distinctLandmarks = edge.LandmarkIds.ToHashSet();
            var missingLandmark = edge.LandmarkIds.FirstOrDefault(id => !landmarkIds.Contains(id), -1);
            if (edge.LandmarkIds.Count == 0
                || distinctLandmarks.Count != edge.LandmarkIds.Count
                || missingLandmark != -1)
            {
                throw Error(
                    LiveWorldModelError.DanglingLandmarkId,
                    "Loop edge landmark references are invalid.",
                    missingLandmark);
            }
            var fromObservation = observationById[keyframeById[edge.FromKeyframeId].ObservationId];
            var toObservation = observationById[keyframeById[edge.ToKeyframeId].ObservationId];
            if (edge.Id < 0 || edge.FromKeyframeId == edge.ToKeyframeId
                || fromObservation.RoomId != toObservation.RoomId
                || edge.LandmarkIds.Any(id => landmarkById[id].KeyframeId != edge.FromKeyframeId)
                || !double.IsFinite(edge.DeltaX) || !double.IsFinite(edge.DeltaY)
                || edge.Support < 3 || !double.IsFinite(edge.Ambiguity) || edge.Ambiguity < 0
                || (edge.ResidualPixels is { } residual && (!double.IsFinite(residual) || residual < 0)))
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Loop edge is invalid.");
            }
        }
    }

    private void ValidateIslands(IReadOnlyDictionary<int, LiveWorldObservation> observations)
    {
        if (!UsesSwiftWireFormat && TrackingIslands.Count == 0 && DoorwayFloorConstraints.Count == 0)
            return;

        var islandIds = new HashSet<int>();
        var owners = new Dictionary<int, (LiveTrackingIsland Island, LiveIslandMember Member)>();
        foreach (var island in TrackingIslands)
        {
            if (island is not { IsValid: true } || !islandIds.Add(island.Id))
                throw Error(LiveWorldModelError.InvalidIslandMembership, "Invalid or duplicated Atlas island.");
            foreach (var member in island.Members)
            {
                if (member is null || !observations.TryGetValue(member.ObservationId, out var observation)
                    || observation.RoomId != island.RoomId || !Finite(member.LocalPose)
                    || !owners.TryAdd(member.ObservationId, (island, member)))
                {
                    throw Error(LiveWorldModelError.InvalidIslandMembership, "Atlas island contains an invalid member.");
                }
                var dx = observation.OptimizedPose.X - member.LocalPose.X - island.WorldFromLocal.X;
                var dy = observation.OptimizedPose.Y - member.LocalPose.Y - island.WorldFromLocal.Y;
                if (dx * dx + dy * dy > 1e-12)
                    throw Error(LiveWorldModelError.InvalidIslandMembership, "Atlas island placement disagrees with its member.");
            }
        }
        if (owners.Count != observations.Count)
            throw Error(LiveWorldModelError.InvalidIslandMembership, "An observation is missing its Atlas island.");

        var doorwayIds = new HashSet<int>();
        foreach (var doorway in DoorwayFloorConstraints)
        {
            if (doorway is null || !doorwayIds.Add(doorway.Id) || !doorway.IsValid(observations, owners))
                throw Error(LiveWorldModelError.InvalidDoorwayFloorConstraint, "Invalid or duplicated doorway floor evidence.");
        }
    }

    private static bool Valid(LiveWorldObservation observation) =>
        observation.Id >= 0 && observation.SourceObservationId >= 0 && observation.RoomId >= 0
        && double.IsFinite(observation.Timestamp)
        && (observation.CaptureTimestamp is null || double.IsFinite(observation.CaptureTimestamp.Value))
        && observation.SourceWidth > 0 && observation.SourceHeight > 0
        && observation.SampleWidth > 0
        && double.IsFinite(observation.SolveWidth) && observation.SolveWidth > 0
        && Finite(observation.LocalPose) && Finite(observation.RawPose)
        && Finite(observation.OptimizedPose)
        && (observation.GroundGeometry is null || observation.GroundGeometry.IsValid(observation))
        && (observation.ObservedFloorCandidates is null || observation.ObservedFloorCandidates.IsValid(observation))
        && (observation.ObservedPlayerBounds is not { } player ||
            ValidRect(player) && player.Width > 0 && player.Height > 0)
        && (observation.WalkedGround is null
            || observation.WalkedGround.All(floor => floor.IsValid(observation.OptimizedPose)))
        && observation.ExcludedRects is not null && observation.ExcludedRects.All(ValidRect);

    private static bool ValidRect(WorldRect rectangle) =>
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height)
        && rectangle.Width >= 0 && rectangle.Height >= 0;

    internal static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static void Unique(IEnumerable<int> identifiers, LiveWorldModelError duplicateError)
    {
        var seen = new HashSet<int>();
        foreach (var identifier in identifiers)
        {
            if (identifier < 0)
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Graph identifier is negative.");
            }
            if (!seen.Add(identifier))
            {
                throw Error(duplicateError, $"Duplicate graph identifier {identifier}.", identifier);
            }
        }
    }

    private static LiveWorldModelException Error(
        LiveWorldModelError error,
        string message,
        int? relatedId = null,
        int? expectedRevision = null,
        int? actualRevision = null) =>
        new(error, message, relatedId, expectedRevision, actualRevision);
}

/// <summary>Atomic compare-and-swap persistence for a validated live-world graph.</summary>
public sealed class LiveWorldSnapshotStore
{
    public LiveWorldSnapshotStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string SnapshotPath => Path.Combine(RootDirectory, "world.json");

    public LiveWorldSnapshot Load()
    {
        if (!File.Exists(SnapshotPath)) return new LiveWorldSnapshot();
        try
        {
            var snapshot = LiveWorldJson.ReadSnapshot(File.ReadAllBytes(SnapshotPath));
            snapshot.Validate();
            return snapshot;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Live-world snapshot JSON is corrupt.", error);
        }
    }

    public bool Commit(LiveWorldSnapshot snapshot, int expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        var current = Load();
        if (current.MapRevision != expectedRevision) return false;
        if (snapshot.MapRevision != expectedRevision + 1)
        {
            throw new LiveWorldModelException(
                LiveWorldModelError.StaleRevision,
                "Committed snapshot must advance the disk revision exactly once.",
                expectedRevision: expectedRevision + 1,
                actualRevision: snapshot.MapRevision);
        }
        Directory.CreateDirectory(RootDirectory);
        AtomicFile.Write(SnapshotPath, LiveWorldJson.WriteSnapshot(snapshot));
        return true;
    }
}
