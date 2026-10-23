using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public enum LiveWorldReplayEvaluatorError
{
    EmptySession,
    InvalidRepeatCount,
    InvalidReturnThreshold,
    InvalidLoopDetectionThreshold,
    PersistentReplayCommitFailed,
    PersistentReplayObservationMismatch,
    InvalidArgument
}

public sealed class LiveWorldReplayEvaluatorException : Exception
{
    public LiveWorldReplayEvaluatorException(
        LiveWorldReplayEvaluatorError error,
        string message,
        string? argument = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        Argument = argument;
    }

    public LiveWorldReplayEvaluatorError Error { get; }
    public string? Argument { get; }
}

public enum LiveWorldReplayRouteMode
{
    Automatic,
    OneWay,
    Returning
}

/// <summary>
/// Configuration for repeatedly replaying one persisted capture route. Global
/// searches are deliberately capped at two per second even for denser input.
/// </summary>
public sealed record LiveWorldReplayOptions(
    int RepeatCount = 3,
    double ReturnThresholdPixels = 12,
    double LoopDetectionThresholdPixels = 24,
    bool ReopenSnapshotBetweenPasses = false,
    LiveWorldReplayRouteMode RouteMode = LiveWorldReplayRouteMode.OneWay);

public sealed record LiveWorldReplayPassReport(
    int PassIndex,
    int ObservationCount,
    int GlobalSearchCount,
    double RawReturnError,
    double OptimizedReturnError,
    bool EndpointFeatureMatchFound,
    double AppliedCorrectionX,
    double AppliedCorrectionY,
    bool RecordingReturnsToStart,
    int NewLoopClosures,
    bool ClosureRequired,
    bool ClosureRequirementMet,
    bool UnexpectedClosure,
    int SavedLandmarkCount,
    int SavedKeyframeCount,
    int SavedRevision,
    bool Passed);

public sealed record LiveWorldReplayReport(
    int SchemaVersion,
    int RepeatCount,
    double GlobalSearchHz,
    double ReturnThresholdPixels,
    double LoopDetectionThresholdPixels,
    bool ReopenedSnapshotBetweenPasses,
    LiveWorldReplayRouteMode RouteMode,
    IReadOnlyList<LiveWorldReplayPassReport> Passes,
    bool Passed,
    IReadOnlyList<string> FailureReasons)
{
    public const int CurrentSchemaVersion = 2;
}

/// <summary>
/// Replays a scene session without mutating it. When reopen is enabled, replay
/// observations and graph revisions are written to an isolated live-world
/// store, then both store and tracker are reconstructed from disk per pass.
/// </summary>
public sealed class LiveWorldReplayEvaluator
{
    private const double MaximumGlobalSearchHz = 2;
    private static readonly JsonSerializerOptions JsonOptions =
        LabelingExampleStore.CreateJsonOptions();

    private readonly SceneSessionStore store;

    public LiveWorldReplayEvaluator(SceneSessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public LiveWorldReplayEvaluator(string sessionRootDirectory)
        : this(new SceneSessionStore(sessionRootDirectory)) { }

    public static LiveWorldReplayReport Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? sessionDirectory = null;
        string? reportPath = null;
        var repeats = 3;
        var reopen = false;
        // Independent visits are one-way unless the caller explicitly opts in
        // to return topology. Vision coordinates must not invent route intent.
        var routeMode = LiveWorldReplayRouteMode.OneWay;
        var index = 0;
        while (index < arguments.Count)
        {
            var flag = arguments[index];
            if (flag == "--world-replay-reopen")
            {
                reopen = true;
                index++;
                continue;
            }
            if (flag == "--world-replay-one-way")
            {
                routeMode = LiveWorldReplayRouteMode.OneWay;
                index++;
                continue;
            }
            if (flag == "--world-replay-returning")
            {
                routeMode = LiveWorldReplayRouteMode.Returning;
                index++;
                continue;
            }
            if (flag is not ("--world-replay-session"
                or "--world-replay-report"
                or "--world-replay-repeats")
                || index + 1 >= arguments.Count)
            {
                throw Error(
                    LiveWorldReplayEvaluatorError.InvalidArgument,
                    $"Invalid world-replay argument: {flag}",
                    flag);
            }

            var value = arguments[index + 1];
            switch (flag)
            {
                case "--world-replay-session":
                    sessionDirectory = value;
                    break;
                case "--world-replay-report":
                    reportPath = value;
                    break;
                case "--world-replay-repeats":
                    if (!int.TryParse(value, out repeats) || repeats <= 0)
                    {
                        throw Error(
                            LiveWorldReplayEvaluatorError.InvalidArgument,
                            $"Invalid world-replay repeat count: {value}",
                            value);
                    }
                    break;
            }
            index += 2;
        }

        if (sessionDirectory is null)
        {
            const string missing = "--world-replay-session";
            throw Error(
                LiveWorldReplayEvaluatorError.InvalidArgument,
                $"Missing required world-replay argument: {missing}",
                missing);
        }

        return new LiveWorldReplayEvaluator(sessionDirectory).Evaluate(
            new LiveWorldReplayOptions(
                RepeatCount: repeats,
                ReopenSnapshotBetweenPasses: reopen,
                RouteMode: routeMode),
            reportPath);
    }

    public LiveWorldReplayReport Evaluate(
        LiveWorldReplayOptions? options = null,
        string? outputPath = null)
    {
        options ??= new LiveWorldReplayOptions();
        if (options.RepeatCount <= 0)
        {
            throw Error(
                LiveWorldReplayEvaluatorError.InvalidRepeatCount,
                "World replay repeat count must be positive.");
        }
        if (!double.IsFinite(options.ReturnThresholdPixels)
            || options.ReturnThresholdPixels <= 0)
        {
            throw Error(
                LiveWorldReplayEvaluatorError.InvalidReturnThreshold,
                "World replay return threshold must be finite and positive.");
        }
        if (!double.IsFinite(options.LoopDetectionThresholdPixels)
            || options.LoopDetectionThresholdPixels < options.ReturnThresholdPixels)
        {
            throw Error(
                LiveWorldReplayEvaluatorError.InvalidLoopDetectionThreshold,
                "Loop detection threshold must be finite and at least the return threshold.");
        }

        // Durable manifest order is capture order. A damaged timestamp must not
        // reorder its corresponding PNG evidence.
        var observations = store.Manifest.Observations.ToArray();
        if (observations.Length == 0)
        {
            throw Error(
                LiveWorldReplayEvaluatorError.EmptySession,
                "World replay session contains no observations.");
        }

        // SceneSessionStore owns UUID rooms; LiveWorldTracker uses compact IDs.
        // Preserve first-seen room ownership across every pass.
        var nextRoomId = 0;
        var trackerRoomIds = new Dictionary<Guid, int>();
        foreach (var observation in observations)
        {
            if (!trackerRoomIds.ContainsKey(observation.RoomId))
            {
                trackerRoomIds.Add(observation.RoomId, nextRoomId++);
            }
        }

        var frames = observations.Select(store.Source).ToArray();
        var pixelsPerSolveUnit = frames[0].Width / observations[0].SolveWidth;
        var endpointMatcher = new GlobalFeatureMatcher();
        var endpointReference = endpointMatcher.MakeReference(
            frames[0],
            observations[0].CameraPosition,
            observations[0].SolveWidth,
            excluding: observations[0].ExcludedRects);
        var endpointMatch = endpointReference is null
            ? null
            : endpointMatcher.Match(
                frames[^1],
                [endpointReference],
                currentSolveWidth: observations[^1].SolveWidth,
                excluding: observations[^1].ExcludedRects);
        var rawEndpointDistance = Distance(
            observations[0].CameraPosition,
            observations[^1].CameraPosition) * pixelsPerSolveUnit;
        // Matching appearance supports alignment, never topology. A recording
        // only infers a return when endpoint room and source geometry agree.
        var inferredReturnToStart = observations.Length >= 2
            && observations[0].RoomId == observations[^1].RoomId
            && rawEndpointDistance <= options.LoopDetectionThresholdPixels;
        var recordingReturnsToStart = options.RouteMode switch
        {
            LiveWorldReplayRouteMode.OneWay => false,
            LiveWorldReplayRouteMode.Returning => true,
            LiveWorldReplayRouteMode.Automatic => inferredReturnToStart,
            _ => false
        };
        var expectedEndPose = recordingReturnsToStart
            ? endpointMatch?.ProposedCameraPose ?? observations[0].CameraPosition
            : observations[^1].CameraPosition;
        var rawReturnError = Distance(
            expectedEndPose,
            observations[^1].CameraPosition) * pixelsPerSolveUnit;

        var persistentReplayRoot = options.ReopenSnapshotBetweenPasses
            ? Path.Combine(Path.GetTempPath(), $"hkv-world-replay-{Guid.NewGuid():N}")
            : null;
        try
        {
            LiveWorldSessionStore? persistentWorldStore = persistentReplayRoot is null
                ? null
                : new LiveWorldSessionStore(persistentReplayRoot);
            var persistentRevision = 0;
            var tracker = new LiveWorldTracker();
            var nextObservationId = 0;
            var previousTimestamp = double.NegativeInfinity;
            var reports = new List<LiveWorldReplayPassReport>();
            var sourceStart = observations[0].Timestamp;
            var sourceSpan = Math.Max(
                observations[^1].Timestamp - sourceStart,
                observations.Length * 0.001) + 1;

            for (var passIndex = 0; passIndex < options.RepeatCount; passIndex++)
            {
                if (passIndex > 0)
                {
                    if (options.ReopenSnapshotBetweenPasses)
                    {
                        if (persistentReplayRoot is null)
                        {
                            throw Error(
                                LiveWorldReplayEvaluatorError.PersistentReplayCommitFailed,
                                "Persistent replay root is unavailable.");
                        }
                        var reopened = new LiveWorldSessionStore(persistentReplayRoot);
                        var state = reopened.Load();
                        tracker = new LiveWorldTracker(state.Snapshot);
                        persistentWorldStore = reopened;
                        persistentRevision = state.SceneRevision;
                    }
                    // Visit-local motion and confirmations reset; durable
                    // descriptors remain immediately searchable.
                    tracker.BeginVisit();
                }

                var closureCountBefore = tracker.Snapshot.LoopClosureEdges.Count;
                var lastReplayId = nextObservationId;
                double? lastSearchTimestamp = null;
                var globalSearchCount = 0;
                var appliedCorrection = new AtlasPoint(0, 0);

                for (var sourceIndex = 0; sourceIndex < observations.Length; sourceIndex++)
                {
                    var source = observations[sourceIndex];
                    var candidateTimestamp = passIndex * sourceSpan
                        + (source.Timestamp - sourceStart);
                    var timestamp = Math.Max(candidateTimestamp, previousTimestamp + 0.001);
                    previousTimestamp = timestamp;
                    var shouldSearch = lastSearchTimestamp is null
                        || timestamp - lastSearchTimestamp.Value >= 1 / MaximumGlobalSearchHz;
                    if (shouldSearch)
                    {
                        lastSearchTimestamp = timestamp;
                        globalSearchCount++;
                    }

                    var proposedPose = new AtlasPoint(
                        source.CameraPosition.X + appliedCorrection.X,
                        source.CameraPosition.Y + appliedCorrection.Y);
                    var storedObservation = persistentWorldStore?.Append(
                        frames[sourceIndex],
                        timestamp,
                        proposedPose,
                        source.SolveWidth,
                        source.ExcludedRects);
                    var replayObservationId = storedObservation?.Id ?? nextObservationId;
                    if (replayObservationId != nextObservationId)
                    {
                        throw Error(
                            LiveWorldReplayEvaluatorError.PersistentReplayObservationMismatch,
                            "Persistent replay observation identity does not match capture order.");
                    }

                    var update = tracker.Ingest(
                        replayObservationId,
                        timestamp,
                        frames[sourceIndex],
                        proposedPose,
                        source.SolveWidth,
                        sourceObservationId: storedObservation?.Id ?? source.Id,
                        exclusions: source.ExcludedRects,
                        searchForClosure: shouldSearch,
                        roomId: trackerRoomIds[source.RoomId]);
                    if (update.AcceptedClosure is not null)
                    {
                        appliedCorrection = new AtlasPoint(
                            appliedCorrection.X + update.Correction.X,
                            appliedCorrection.Y + update.Correction.Y);
                    }

                    if (persistentWorldStore is not null)
                    {
                        var committed = persistentWorldStore.Commit(
                            update.Snapshot,
                            persistentRevision,
                            update.Revision);
                        if (!committed)
                        {
                            throw Error(
                                LiveWorldReplayEvaluatorError.PersistentReplayCommitFailed,
                                "Persistent replay snapshot compare-and-swap failed.");
                        }
                        persistentRevision = update.Revision;
                    }
                    lastReplayId = nextObservationId;
                    nextObservationId++;
                }

                var last = tracker.Snapshot.Observations.Single(value =>
                    value.Id == lastReplayId);
                var optimizedReturnError = Distance(
                    expectedEndPose,
                    last.OptimizedPose) * pixelsPerSolveUnit;
                var optimizedEndpointDistanceFromStart = Distance(
                    observations[0].CameraPosition,
                    last.OptimizedPose) * pixelsPerSolveUnit;
                var newLoopClosures = tracker.Snapshot.LoopClosureEdges.Count
                    - closureCountBefore;
                var closureRequired = recordingReturnsToStart;
                var closureRequirementMet = !closureRequired || newLoopClosures >= 1;
                var unexpectedClosure = !recordingReturnsToStart
                    && newLoopClosures > 0
                    && optimizedEndpointDistanceFromStart
                        <= options.LoopDetectionThresholdPixels;
                var passed = closureRequired
                    ? closureRequirementMet
                        && optimizedReturnError <= options.ReturnThresholdPixels
                    : !unexpectedClosure
                        && optimizedReturnError <= options.ReturnThresholdPixels;

                reports.Add(new LiveWorldReplayPassReport(
                    passIndex,
                    observations.Length,
                    globalSearchCount,
                    rawReturnError,
                    optimizedReturnError,
                    endpointMatch is not null,
                    appliedCorrection.X * pixelsPerSolveUnit,
                    appliedCorrection.Y * pixelsPerSolveUnit,
                    recordingReturnsToStart,
                    newLoopClosures,
                    closureRequired,
                    closureRequirementMet,
                    unexpectedClosure,
                    tracker.Snapshot.Landmarks.Count,
                    tracker.Snapshot.Keyframes.Count,
                    tracker.Snapshot.MapRevision,
                    passed));
            }

            var failures = reports.Where(pass => !pass.Passed).Select(pass =>
            {
                if (pass.UnexpectedClosure)
                {
                    return $"pass {pass.PassIndex}: one-way endpoint collapsed onto the route start";
                }
                if (!pass.ClosureRequirementMet)
                {
                    return $"pass {pass.PassIndex}: no loop closure accepted for a returning recording";
                }
                var kind = pass.RecordingReturnsToStart ? "return" : "terminal";
                return $"pass {pass.PassIndex}: optimized {kind} error "
                    + $"{pass.OptimizedReturnError} exceeds {options.ReturnThresholdPixels} pixels";
            }).ToArray();
            var report = new LiveWorldReplayReport(
                LiveWorldReplayReport.CurrentSchemaVersion,
                options.RepeatCount,
                MaximumGlobalSearchHz,
                options.ReturnThresholdPixels,
                options.LoopDetectionThresholdPixels,
                options.ReopenSnapshotBetweenPasses,
                options.RouteMode,
                reports,
                failures.Length == 0,
                failures);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                AtomicFile.Write(outputPath, EncodeSorted(report));
            }
            return report;
        }
        finally
        {
            if (persistentReplayRoot is not null && Directory.Exists(persistentReplayRoot))
            {
                try { Directory.Delete(persistentReplayRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static byte[] EncodeSorted(LiveWorldReplayReport report)
    {
        var element = JsonSerializer.SerializeToElement(report, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = true
               }))
        {
            WriteSorted(element, writer);
        }
        return stream.ToArray();
    }

    private static void WriteSorted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray())
                {
                    WriteSorted(value, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static double Distance(AtlasPoint left, AtlasPoint right) => Math.Sqrt(
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static LiveWorldReplayEvaluatorException Error(
        LiveWorldReplayEvaluatorError error,
        string message,
        string? argument = null,
        Exception? innerException = null) => new(
            error,
            message,
            argument,
            innerException);
}
