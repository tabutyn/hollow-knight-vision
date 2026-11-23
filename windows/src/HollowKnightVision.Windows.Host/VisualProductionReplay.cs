using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal sealed record VisualProductionReplayOptions(string Path, string Output, int? FrameLimit, bool AllowIncomplete)
{
    internal static VisualProductionReplayOptions Parse(string[] args)
    {
        string? path = null, output = null;
        int? limit = null;
        var allowIncomplete = false;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--visual-replay-allow-incomplete" && !allowIncomplete)
            {
                allowIncomplete = true;
                continue;
            }
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected a value for {option}.");
            switch (option)
            {
                case "--replay-visual-archive" when path is null:
                    path = args[i];
                    break;
                case "--visual-replay-output" when output is null:
                    output = args[i];
                    break;
                case "--visual-replay-frame-limit" when limit is null && int.TryParse(args[i], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed) && parsed > 0:
                    limit = parsed;
                    break;
                default:
                    throw new ArgumentException($"Invalid or repeated visual replay option {option}.");
            }
        }
        if (path is null || output is null)
            throw new ArgumentException("Expected --replay-visual-archive PATH --visual-replay-output DIRECTORY.");
        return new(path, output, limit, allowIncomplete);
    }
}

internal sealed record VisualProductionReplayReport(int SchemaVersion,
    [property: JsonPropertyName("sourcePathID")] Guid SourcePathId, int Frames, int ArchiveFrameCount,
    int SelectedFrameCount, int SkippedBeforeVision, IReadOnlyList<int> MissingFrameIndices, bool ArchiveComplete,
    int MaskInputs, int GroundVerifiedFrames, int CommittedFrames, int WorldObservations, int AtlasTiles,
    double ElapsedSeconds, double FramesPerSecond, string VisualDigest, string CoarseVisualDigest, string WorldDigest,
    string AtlasDigest, string WorldDirectory, string OutputPath, string WorldSnapshotPath, string DiagnosticsPath,
    string WorldSnapshotTimestampDomain, IReadOnlyList<string> Notes);

/// <summary>Feeds recorded images and visual input into the capture worker's camera and durable atlas pipeline.</summary>
internal static class VisualProductionReplay
{
    private readonly record struct FrameKey(ulong Generation, long TimestampBits)
    {
        internal static FrameKey Of(ulong generation, double timestamp) =>
            new(generation, BitConverter.DoubleToInt64Bits(timestamp));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal static VisualProductionReplayReport Run(VisualProductionReplayOptions options, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(options.Path);
        var source = new RecordedInputPathStore().Load(path);
        if (source.VisualFrameArchive is not { } archive)
            throw new InvalidDataException("The input path has no visual frame archive.");
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(path)!, archive, source.Id,
            requireComplete: !options.AllowIncomplete);
        var manifest = reader.Manifest;
        var selected = manifest.Frames.Take(options.FrameLimit ?? manifest.Frames.Count).ToArray();
        var missing = selected.Where(frame => frame.FileName is null).Select(frame => frame.Index).ToArray();
        var started = manifest.StageEvents.Where(stage => stage.Stage == "visionStarted")
            .Select(stage => FrameKey.Of(stage.CaptureGeneration, stage.CaptureTimestamp)).ToHashSet();
        var available = selected.Where(frame => frame.FileName is not null).ToArray();
        var frames = started.Count == 0 ? available : available.Where(frame =>
            started.Contains(FrameKey.Of(frame.CaptureGeneration, frame.CaptureTimestamp))).ToArray();
        if (frames.Length == 0) throw new InvalidDataException("The selected archive has no readable frames delivered to vision.");
        var groundInputs = new Dictionary<FrameKey, VisualReplayGroundInput>();
        foreach (var input in manifest.GroundInputs)
        {
            if (input is null || !double.IsFinite(input.CaptureTimestamp))
                throw new InvalidDataException("Visual replay ground input has invalid timing.");
            groundInputs[FrameKey.Of(input.CaptureGeneration, input.CaptureTimestamp)] = input;
        }

        var output = Path.GetFullPath(options.Output);
        if (Path.Exists(output)) throw new IOException("The visual replay output path already exists.");
        Directory.CreateDirectory(output);
        var worldDirectory = Path.Combine(output, "world");
        var snapshotPath = Path.Combine(output, "world-snapshot.json");
        var diagnosticsPath = Path.Combine(output, "frames.jsonl");
        var world = new WindowsLiveWorldPipeline(worldDirectory);
        var atlas = new GroundTruthAtlasMosaic(pixelsPerWorldUnit: 1, sourceTopInsetFraction: 0);
        var camera = new VisualGameplayTracker();
        var motion = new MaskedFrameMotionTracker();
        var tracking = new GameplayTrackingPipeline(camera, motion, deterministicGlobalSearch: true);
        var admissionPolicy = new GameplayFrameAdmission();
        var coarseMotion = new LowResolutionRoomMotionTracker();
        var masksUsed = 0;
        var groundVerified = 0;
        var committed = 0;
        var lostTracking = false;
        long? generation = null;
        var timeBase = StartTimestamp(source, manifest);
        var replayTrace = new InputPathRecorder();
        replayTrace.Start(timeBase.Start, source.CreatedAt, sourcePathId: source.Id,
            metadata: new Dictionary<string, string> { ["playbackMode"] = "visual-archive" });
        var eventIndex = 0;
        var held = InputButtons.None;
        var began = Stopwatch.GetTimestamp();
        using var visualDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var coarseDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var diagnostics = new StreamWriter(diagnosticsPath, append: false);
            foreach (var evidence in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = reader.ReadImage(evidence);
                var key = FrameKey.Of(evidence.CaptureGeneration, evidence.CaptureTimestamp);
                groundInputs.TryGetValue(key, out var input);
                if (input is not null) masksUsed++;
                var captureGeneration = checked((long)evidence.CaptureGeneration);
                if (generation.HasValue && generation != captureGeneration)
                {
                    tracking.Reset();
                    coarseMotion.Reset();
                }
                generation = captureGeneration;
                var offset = evidence.CapturedAt - timeBase.Start;
                while (eventIndex < source.Events.Count && source.Events[eventIndex].Offset <= offset)
                {
                    var item = source.Events[eventIndex++];
                    var bit = item.Button.ToInputButton();
                    held = item.Transition == RecordedInputTransition.Pressed ? held | bit : held & ~bit;
                }
                var buttons = ResolveMovement(input, held);
                var frame = new BgraFrame(image.Width, image.Height, image.Stride, image.Pixels, image.CaptureId,
                    source.CreatedAt.AddSeconds(offset));
                var signal = FrameRegionRenderer.SignalProfile(frame);
                var coarse = coarseMotion.Observe(signal.MotionGrid, evidence.CaptureTimestamp);
                var admission = admissionPolicy.Observe(signal, input is not null, captureGeneration);
                if (admission.ResetMotionReference) tracking.ResetMotionReference();
                var masks = RestoreMasks(input, frame);
                var prepared = tracking.Prepare(frame, masks, admission.AdmitsTracking,
                    tuning: input?.Tuning, semanticTuning: input?.SemanticTuning);
                var tracked = tracking.Track(frame, prepared, masks.GroundKnight, admission, signal, evidence.CaptureTimestamp,
                    captureGeneration, knownRoom: null, atlas.TileCount > 0, buttons,
                    resetWorldCoordinatesForCorrection: true, captureWorldEvidence: true);
                var observation = tracked.Observation;
                var telemetry = observation.Telemetry;
                var localCamera = new AtlasPoint(telemetry.CameraX, telemetry.CameraY);
                if (observation.GroundVerified) groundVerified++;
                lostTracking |= tracked.RequiresWorldRecovery;
                WindowsLiveWorldPipelineResult? placement = null;
                if (admission.AllowsAtlasWrite && tracked.AllowsAtlasWrite && tracked.RoomAllowsAtlasWrite)
                {
                    if (lostTracking) world.NotifyTrackingLoss(evidence.CaptureGeneration);
                    lostTracking = false;
                    var exclusions = LiveAtlasTransitionMaskPolicy.Merge(masks.AtlasExclusions,
                        frame.Width, frame.Height, signal, tracked.MotionBridgeActive);
                    var composition = exclusions.Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
                    var alignment = GameplayTrackingPipeline.CreateGroundAlignmentExclusions(frame, localCamera,
                        frame.Width, prepared.Terrain?.Floors ?? [], masks.GroundExclusions, signal,
                        observation, tracked.MotionBridgeActive, tracked.Reference);
                    placement = world.Process(new WindowsLiveWorldFrame(frame, FrameRegionRenderer.MapFrame(frame, masks.AtlasRegions),
                        timeBase.WorldSeconds + evidence.CaptureTimestamp - timeBase.Start, evidence.CaptureTimestamp,
                        localCamera, frame.Width, composition, alignment, evidence.CaptureGeneration, tracked.RoomId,
                        atlas.PixelsPerWorldUnit, atlas.TileSize, observation.GroundVerified)
                    {
                        WalkedGround = tracked.WalkedGround,
                        WalkedCoordinateEpoch = tracked.WalkedCoordinateEpoch,
                        RoomIdentityIsVisual = true,
                        DoorwayCut = tracked.RecentDoorwayCut,
                        WorldEvidence = tracked.WorldEvidence
                    });
                    if (placement.State == WindowsLiveWorldPipelineState.Failed)
                        throw new IOException(placement.Diagnostic);
                    if (placement.AllowsAtlasWrite)
                    {
                        committed++;
                        if (placement.RebuiltAtlas is { } rebuilt) atlas = rebuilt;
                        else atlas.AddFrame(frame, telemetry with {
                            CameraX = placement.CameraPose.X, CameraY = placement.CameraPose.Y,
                            CameraTargetX = placement.CameraPose.X, CameraTargetY = placement.CameraPose.Y
                        }, exclusions, roomId: tracked.RoomId, captureGeneration: evidence.CaptureGeneration);
                    }
                }

                var basis = placement?.WorldBasisOffset ?? default;
                var publishedCamera = new AtlasPoint(localCamera.X + basis.X, localCamera.Y + basis.Y);
                var roomId = WindowsLiveWorldPipeline.RoomIdentifier(tracked.RoomId);
                var revision = (ulong)Math.Max(0, placement?.Revision ?? 0);
                var floorless = observation.FloorlessEvidence;
                replayTrace.RecordTracking(InputPathTrackingObservation.FromGround(tracked.Ground), evidence.CaptureTimestamp,
                    publishedCameraPosition: publishedCamera, poseSource: observation.Source,
                    motionBridgeConfidence: observation.MotionBridgeConfidence,
                    motionBridgeSource: tracked.MotionBridgeActive ? observation.Source : null,
                    signalMeanPeak: signal.MeanPeak, signalVisibleFraction: signal.VisibleFraction,
                    roomId: roomId, roomRevision: revision, renderedGameFrame: evidence.RenderedGameFrame,
                    placementRecoveryState: camera.PlacementRecoveryState,
                    floorlessCoarsePosition: floorless?.CoarsePosition, floorlessMaskedPosition: floorless?.MaskedPosition,
                    floorlessExpectedDirection: floorless?.ExpectedDirection, floorlessSelectionSource: floorless?.SelectionName,
                    sceneMotion: floorless?.Scene,
                    recoveredAtlasReferenceTimestamp: observation.RecoveredAtlasReferenceTimestamp,
                    observedAt: evidence.CapturedAt);
                replayTrace.RecordAtlasAdmission(evidence.CaptureTimestamp, placement?.AllowsAtlasWrite == true);
                replayTrace.RecordCoarseMotion(evidence.CaptureTimestamp, evidence.RenderedGameFrame, publishedCamera,
                    tracked.MotionBridgeActive ? publishedCamera : null, coarse, tracked.MotionBridgeActive,
                    !admission.AdmitsTracking, roomId, revision, signal.MeanPeak, signal.VisibleFraction,
                    motionGrid: signal.MotionGrid, observedAt: evidence.CapturedAt);

                var visual = new {
                    evidence.Index, evidence.CaptureTimestamp, evidence.CaptureGeneration, evidence.RenderedGameFrame,
                    admission.AdmitsTracking, admission.Transition.ResumedAfterTransition, observation.Source,
                    observation.GroundVerified, tracked.AllowsAtlasWrite, tracked.HasMeasuredSceneryContinuity,
                    tracked.RequiresWorldRecovery,
                    observation.RecoveredAtlasReferenceTimestamp,
                    trackerState = camera.PlacementRecoveryState, localCamera, tracked.RoomId,
                    floorless,
                    tracked.RoomAllowsAtlasWrite, tracked.SceneCut,
                    worldCamera = placement?.CameraPose, worldState = placement?.State.ToString(),
                    worldDiagnostic = placement?.Diagnostic,
                    groundSupport = observation.GroundSupport, groundError = observation.GroundError,
                    tracked.Ground.HasConfirmedGround, tracked.Ground.InlierCount, tracked.Ground.ResidualRms,
                    tracked.Ground.GlobalCorrection,
                    floors = prepared.Terrain?.Floors.Select(floor => new { floor.Row, floor.XSpan, floor.EvidenceSpans }),
                    walls = tracked.Terrain.Walls,
                    trackingPatches = tracked.PlatformPatches,
                    walkedGround = tracked.WalkedGround
                };
                var line = JsonSerializer.SerializeToUtf8Bytes(visual, Json);
                diagnostics.WriteLine(System.Text.Encoding.UTF8.GetString(line));
                visualDigest.AppendData(line);
                coarseDigest.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { evidence.Index, coarse }, Json));
            }
        }
        finally
        {
            tracking.Reset();
        }

        var elapsed = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var snapshot = new LiveWorldSessionStore(worldDirectory).Load().Snapshot;
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        AtomicWrite(snapshotPath, snapshotBytes);
        var trace = replayTrace.Stop(frames[^1].CapturedAt)
            ?? throw new InvalidDataException("Visual replay lost its output input path.");
        var tracePath = new RecordedInputPathStore(output).Save(trace);
        var tileDigest = WriteAtlas(atlas, Path.Combine(output, "atlas"));
        var report = new VisualProductionReplayReport(3, source.Id, frames.Length, manifest.Frames.Count, selected.Length,
            available.Length - frames.Length, missing, archive.Complete, masksUsed, groundVerified, committed,
            snapshot.Observations.Count, atlas.TileCount, elapsed, frames.Length / Math.Max(.001, elapsed),
            Hex(visualDigest.GetHashAndReset()), Hex(coarseDigest.GetHashAndReset()), Hex(SHA256.HashData(snapshotBytes)),
            tileDigest, worldDirectory, tracePath, snapshotPath, diagnosticsPath, "capture-uptime",
            [
                "Tracking and world publication run serially using the Windows production ground, camera and atlas code.",
                "Only archived images, visual masks, tuning and input transitions feed tracking. Room identity is derived from visual transitions.",
                "Archives with vision-stage events replay the frames delivered to that worker. Missing images retain their original indices.",
                timeBase.Note
            ]);
        AtomicWrite(Path.Combine(output, "report.json"), JsonSerializer.SerializeToUtf8Bytes(report, Json));
        return report;
    }

    private static (double Start, double WorldSeconds, string Note) StartTimestamp(RecordedInputPath source, VisualReplayManifest manifest)
    {
        var worldStart = (source.CreatedAt - DateTimeOffset.UnixEpoch).TotalSeconds;
        if (manifest.Configuration.TryGetValue("inputStartedAt", out var saved)
            && double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
            && double.IsFinite(exact))
            return (exact, worldStart, "Input transition offsets use the recorded start uptime.");

        var captured = manifest.Frames.First(frame => frame.FileName is not null);
        var firstCoarse = source.CoarseMotionTrace.FirstOrDefault();
        return firstCoarse is not null
            ? (captured.CapturedAt - firstCoarse.Offset, worldStart, "Legacy input timing uses the first archived frame and coarse-trace offset.")
            : (captured.CapturedAt, worldStart, "Legacy input timing uses the first readable capture as its start.");
    }

    private static InputButtons ResolveMovement(VisualReplayGroundInput? input, InputButtons held)
    {
        if (input?.WindowsMovementButtons is { } exact) return exact;
        if (input?.HorizontalDrive is { } drive)
        {
            held &= ~(InputButtons.Left | InputButtons.Right);
            if (drive < 0) held |= InputButtons.Left;
            if (drive > 0) held |= InputButtons.Right;
        }
        return input?.RecentJump == true ? held | InputButtons.ActionZ : held;
    }

    private static GameplaySceneMasks RestoreMasks(VisualReplayGroundInput? input, BgraFrame frame)
    {
        StencilRect Rect(double[][] value)
        {
            if (value is not [{ Length: 2 }, { Length: 2 }]
                || !value.All(pair => pair.All(double.IsFinite)))
                throw new InvalidDataException("Visual replay contains an invalid recorded mask.");
            return new(value[0][0], frame.Height - value[0][1] - value[1][1], value[1][0], value[1][1]);
        }

        var omissions = input?.ExcludingRects?.Select(Rect).ToArray() ?? [];
        var atlas = input?.WindowsAtlasExcludingRects?.Select(Rect).ToArray() ?? omissions;
        var knight = input?.KnightRect is { } box
            ? SceneRegions.PixelRectangles(frame.Width, frame.Height, [Rect(box)]) : [];
        return new(SceneRegions.Startup() with { HudStencilRects = atlas },
            SceneRegions.Startup() with { HudStencilRects = omissions },
            SceneRegions.PixelRectangles(frame.Width, frame.Height, atlas),
            SceneRegions.PixelRectangles(frame.Width, frame.Height, omissions),
            knight.Count > 0 ? knight[0] : null);
    }

    private static string WriteAtlas(GroundTruthAtlasMosaic atlas, string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> coordinate = stackalloc byte[8];
        foreach (var tile in atlas.Snapshot().OrderBy(tile => tile.Coordinate.Y).ThenBy(tile => tile.Coordinate.X))
        {
            BinaryPrimitives.WriteInt32LittleEndian(coordinate, tile.Coordinate.X);
            BinaryPrimitives.WriteInt32LittleEndian(coordinate[4..], tile.Coordinate.Y);
            hash.AppendData(coordinate);
            hash.AppendData(tile.Frame.Pixels);
            PngWriter.Write(tile.Frame, Path.Combine(directory,
                FormattableString.Invariant($"tile_{tile.Coordinate.X}_{tile.Coordinate.Y}.png")));
        }
        return Hex(hash.GetHashAndReset());
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        // The directory belongs to this run. Publish completed JSON only once.
        var staging = path + ".tmp";
        File.WriteAllBytes(staging, bytes);
        File.Move(staging, path);
    }

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
