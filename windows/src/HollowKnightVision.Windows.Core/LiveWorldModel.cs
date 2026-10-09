using System.Text.Json;

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
    UnanchoredWorld
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

public sealed record LiveWorldKeyframe(int Id, int ObservationId, int SourceObservationId);

public sealed record LiveWorldLandmark(
    int Id,
    int KeyframeId,
    int SourceObservationId,
    AtlasPoint SamplePoint,
    AtlasPoint WorldPoint,
    IReadOnlyList<float> Descriptor,
    double Depth,
    double DepthConfidence);

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
    double Ambiguity);

/// <summary>Validated, versioned pose graph with stable visual-room ownership.</summary>
public sealed record LiveWorldSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public LiveWorldSnapshot(
        int schemaVersion = CurrentSchemaVersion,
        int mapRevision = 0,
        int? anchoredObservationId = null,
        IReadOnlyList<LiveWorldObservation>? observations = null,
        IReadOnlyList<LiveWorldKeyframe>? keyframes = null,
        IReadOnlyList<LiveWorldLandmark>? landmarks = null,
        IReadOnlyList<LiveWorldRelativeMotionEdge>? relativeMotionEdges = null,
        IReadOnlyList<LiveWorldLoopClosureEdge>? loopClosureEdges = null)
    {
        SchemaVersion = schemaVersion;
        MapRevision = mapRevision;
        AnchoredObservationId = anchoredObservationId;
        Observations = observations ?? [];
        Keyframes = keyframes ?? [];
        Landmarks = landmarks ?? [];
        RelativeMotionEdges = relativeMotionEdges ?? [];
        LoopClosureEdges = loopClosureEdges ?? [];
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
        return new LiveWorldSnapshot(
            SchemaVersion,
            MapRevision + 1,
            AnchoredObservationId,
            replaced,
            Keyframes,
            movedLandmarks,
            RelativeMotionEdges,
            LoopClosureEdges);
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
        if (SchemaVersion != CurrentSchemaVersion)
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
                || edge.Support < 3 || !double.IsFinite(edge.Ambiguity) || edge.Ambiguity < 0)
            {
                throw Error(LiveWorldModelError.InvalidGeometry, "Loop edge is invalid.");
            }
        }
    }

    private static bool Valid(LiveWorldObservation observation) =>
        observation.Id >= 0 && observation.SourceObservationId >= 0 && observation.RoomId >= 0
        && double.IsFinite(observation.Timestamp)
        && observation.SourceWidth > 0 && observation.SourceHeight > 0
        && observation.SampleWidth > 0
        && double.IsFinite(observation.SolveWidth) && observation.SolveWidth > 0
        && Finite(observation.LocalPose) && Finite(observation.RawPose)
        && Finite(observation.OptimizedPose)
        && observation.ExcludedRects.All(rectangle =>
            double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
            && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height)
            && rectangle.Width >= 0 && rectangle.Height >= 0);

    private static bool Finite(AtlasPoint point) =>
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
    private static readonly JsonSerializerOptions JsonOptions =
        LabelingExampleStore.CreateJsonOptions();

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
            var snapshot = JsonSerializer.Deserialize<LiveWorldSnapshot>(
                File.ReadAllBytes(SnapshotPath), JsonOptions)
                ?? throw new InvalidDataException("Empty live-world snapshot.");
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
        AtomicFile.Write(SnapshotPath, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
        return true;
    }
}
