using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class RecordedIslandClosureAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Closure(int ObservationId, int IslandId, double Timestamp, AtlasPoint Correction,
        LiveWorldLoopClosureEdge Edge);
    private sealed record Vote(int ObservationId, int IslandId, int ReferenceId, double Timestamp,
        LiveWorldTrackerRejection[] Rejections);
    private sealed record CameraArchive(Guid Id, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record Scan(int Id, bool Searched, bool ConfirmationProbe, int? ProposedReference,
        string[] Rejections, bool Accepted);
    private sealed record CadenceResult(string Policy, Scan[] Rows, IReadOnlyList<LiveWorldLoopClosureEdge> Edges);

    internal static void TestRecordedUnmeasuredIslandMemberDoesNotCreateKeyframe()
    {
        var tracker = new LiveWorldTracker();
        var frame = new BgraFrame(64, 36, 256, new byte[64 * 36 * 4], 1, DateTimeOffset.UnixEpoch);
        var anchor = new LiveIslandCapture(4, 0, 1, default, default, LiveIslandPlacement.Reference, default, "reference", true);
        var unmeasured = new LiveIslandCapture(9, 0, 2, new(12, -3), new(1, 2),
            LiveIslandPlacement.Estimated, default, "unmeasured-motion", false);
        var geometry = new LiveWorldGroundGeometry([new(1, new(0, 4), new(32, 4))]);
        tracker.Ingest(0, 1, frame, default, 64, captureEvidence: new(1, geometry), recordedIsland: anchor);
        var rejectedVote = tracker.Ingest(1, 2, frame, new(50, 12), 64,
            captureEvidence: new(2, geometry, new(geometry.Lines)), recordedIsland: unmeasured);
        tracker.Ingest(2, 3, frame, new(1, 4), 64, captureEvidence: new(3, geometry),
            recordedIsland: anchor with { LocalPose = new(1, 4), WorldFromLocal = new(100, 100) });
        Check(rejectedVote.AcceptedClosure is null && tracker.Snapshot.Keyframes.All(keyframe => keyframe.ObservationId != 1)
            && tracker.Snapshot.Observations[1] is { GroundGeometry: null, ObservedFloorCandidates: null, CaptureTimestamp: 2 }
            && tracker.Snapshot.Observations[1].RawPose == new AtlasPoint(50, 12)
            && tracker.Snapshot.Observations[1].OptimizedPose == new AtlasPoint(13, -1)
            && tracker.Snapshot.Observations[2].OptimizedPose == new AtlasPoint(1, 4)
            && tracker.Snapshot.TrackingIslands.Single(island => island.Id == 9).Members.Single().Measured == false,
            "An archived unmeasured capture retains its image and original owner without floor evidence or a keyframe.");
    }

    internal static void TestRecordedWorldIslandClosures()
    {
        var input = Path.Combine(Path.GetFullPath(Setting("HKV_ISLAND_AUDIT_INPUT")), "world");
        var output = Path.GetFullPath(Setting("HKV_ISLAND_AUDIT_OUTPUT"));
        if (Path.Exists(output)) throw new IOException("The recorded closure audit needs a new output directory.");
        var relativeOutput = Path.GetRelativePath(Path.GetDirectoryName(input)!, output);
        if (!Path.IsPathRooted(relativeOutput) && !relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Keep the recorded closure reports outside the input archive.");
        var snapshotPath = Path.Combine(input, "scene.snapshot");
        var originalBytes = File.ReadAllBytes(snapshotPath);
        var original = LiveWorldSessionStore.ValidateExisting(input).Snapshot;
        var broad = Environment.GetEnvironmentVariable("HKV_ISLAND_AUDIT_BROAD_FEATURES") == "1";
        var verifyGround = Environment.GetEnvironmentVariable("HKV_ISLAND_AUDIT_VERIFY_GROUND") == "1";
        var useLiveCadence = Environment.GetEnvironmentVariable("HKV_ISLAND_AUDIT_USE_LIVE_CADENCE") == "1";
        if (useLiveCadence && !verifyGround)
            throw new InvalidDataException("The live closure search cadence requires ground verification.");
        using var archive = SceneSessionStore.OpenReadOnly(input);
        var savedFrames = archive.Manifest.Observations.ToDictionary(frame => frame.Id);
        var sourceObservations = original.Observations.ToDictionary(observation => observation.SourceObservationId);
        var owners = original.TrackingIslands
            .SelectMany(island => island.Members.Select(member => (Island: island, Member: member)))
            .ToDictionary(owner => owner.Member.ObservationId);
        var tracker = new LiveWorldTracker(new LiveWorldSnapshot(schemaVersion: original.SchemaVersion));
        var images = new Dictionary<int, (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions)>();
        var imageOrder = new Queue<int>();
        var rejections = new Dictionary<string, int>();
        var closures = new List<Closure>();
        var votes = new List<Vote>();
        var lastSearch = double.NegativeInfinity;
        var scheduledSearches = 0;
        var replayedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (var observation in original.Observations)
        {
            var (island, member) = owners[observation.Id];
            var capture = new LiveIslandCapture(island.Id, island.RoomId, island.StartedAt, island.WorldFromLocal,
                member.LocalPose, island.Placement, island.Uncertainty, island.BreakReason, member.Measured);
            var (image, exclusions) = Source(observation.SourceObservationId);
            var featureMasks = broad ? [] : exclusions.Select(rect => LiveWorldJson.FlipMask(
                new StencilRect(rect.X, rect.Y, rect.Width, rect.Height), image.Height)).ToArray();
            var search = member.Measured && (useLiveCadence || observation.Timestamp - lastSearch >= .5);
            if (search)
            {
                lastSearch = observation.Timestamp;
                scheduledSearches++;
            }
            var evidence = observation.CaptureTimestamp is { } time ? new LiveWorldCaptureEvidence(time,
                observation.GroundGeometry, observation.ObservedFloorCandidates, observation.ObservedPlayerBounds) : null;
            var update = tracker.Ingest(observation.Id, observation.Timestamp, image, observation.RawPose, observation.SolveWidth,
                sourceObservationId: observation.SourceObservationId, exclusions: featureMasks, searchForClosure: search,
                localPose: observation.LocalPose, captureGeneration: observation.CaptureGeneration, localEpoch: observation.LocalEpoch,
                basisRevision: observation.BasisRevision, roomId: observation.RoomId, captureEvidence: evidence,
                loadGroundSource: verifyGround ? Source : null, recordedIsland: capture);
            foreach (var rejection in update.Review.Rejections)
            {
                var key = rejection.ToString();
                rejections[key] = rejections.GetValueOrDefault(key) + 1;
            }
            if (update.AcceptedClosure is { } accepted)
                closures.Add(new(observation.Id, island.Id, observation.Timestamp, update.Correction, accepted));
            else if (update.Review.Rejections.Any(rejection => rejection is LiveWorldTrackerRejection.ConfirmationRequired
                or LiveWorldTrackerRejection.InconsistentConfirmation) && update.Review.MatchLines.LastOrDefault() is { } proposal)
                votes.Add(new(observation.Id, island.Id, proposal.FromObservationId, observation.Timestamp,
                    update.Review.Rejections.ToArray()));
            if (++replayedCount % 100 == 0)
                Console.WriteLine($"Island replay processed {replayedCount}/{original.Observations.Count} recorded images.");
        }
        var solved = tracker.Snapshot;
        // The appearance search can use an experimental mask. The rebuilt world always uses the saved composition mask.
        var observations = solved.Observations.Select(observation => observation with
            { ExcludedRects = sourceObservations[observation.SourceObservationId].ExcludedRects }).ToArray();
        var replayed = new LiveWorldSnapshot(solved.SchemaVersion, solved.MapRevision, solved.AnchoredObservationId,
            observations, solved.Keyframes, solved.Landmarks, solved.RelativeMotionEdges, solved.LoopClosureEdges,
            solved.TrackingIslands, solved.DoorwayFloorConstraints);
        var payload = LiveWorldJson.WriteSnapshot(replayed);
        var unmeasured = owners.Where(owner => !owner.Value.Member.Measured).Select(owner => owner.Key).ToHashSet();
        var measuredMembership = replayed.TrackingIslands.SelectMany(island => island.Members
            .Select(member => (island.Id, member))).ToDictionary(owner => owner.member.ObservationId);
        Check(original.Observations.Select(observation => (observation.Id, observation.SourceObservationId, observation.RawPose, observation.CaptureTimestamp))
            .SequenceEqual(replayed.Observations.Select(observation => (observation.Id, observation.SourceObservationId, observation.RawPose, observation.CaptureTimestamp)))
            && owners.All(owner => measuredMembership[owner.Key] == (owner.Value.Island.Id, owner.Value.Member))
            && replayed.Keyframes.All(keyframe => !unmeasured.Contains(keyframe.ObservationId))
            && replayed.Observations.Where(observation => unmeasured.Contains(observation.Id)).All(observation =>
                observation.GroundGeometry is null && observation.ObservedFloorCandidates is null)
            && LiveWorldJson.WriteSnapshot(LiveWorldJson.ReadSnapshot(payload)).AsSpan().SequenceEqual(payload)
            && File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(originalBytes),
            "Replay must preserve raw image identity, measured ownership, and a valid read-only source.");
        Directory.CreateDirectory(output);
        AtomicFile.Write(Path.Combine(output, "world-snapshot.json"), payload, overwrite: false);
        AtomicFile.Write(Path.Combine(output, "island-audit.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            observations = replayed.Observations.Count,
            islands = replayed.TrackingIslands.Count,
            unmeasured = unmeasured.Count,
            archivedClosures = original.LoopClosureEdges.Count,
            broadFeatures = broad,
            groundVerificationRequired = verifyGround,
            cadence = useLiveCadence ? "production floor scheduling" : "fixed 2 Hz requests",
            searchRequests = scheduledSearches,
            rejections,
            votes,
            closures,
            worldDigest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds
        }, Json), overwrite: false);
        Console.WriteLine($"Replayed {replayed.Observations.Count} images in {replayed.TrackingIslands.Count} recorded islands, "
            + $"{unmeasured.Count} unmeasured, {scheduledSearches} search requests, {closures.Count} accepted closures.");

        (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions) Source(int id)
        {
            if (images.TryGetValue(id, out var evidence)) return evidence;
            var frame = archive.Source(savedFrames[id]);
            var masks = original.CompositionMasks(sourceObservations[id]).Select(rect =>
                new WorldRect(new(rect.X, rect.Y), new(rect.Width, rect.Height))).ToArray();
            if (imageOrder.Count == 12) images.Remove(imageOrder.Dequeue());
            imageOrder.Enqueue(id);
            images.Add(id, evidence = (frame, masks));
            return evidence;
        }
    }

    internal static void TestInspectRecordedClosureSearchCadenceWhenRequested()
    {
        var root = Path.Combine(Path.GetFullPath(Setting("HKV_CLOSURE_CADENCE_INPUT")), "world");
        var output = Path.GetFullPath(Setting("HKV_CLOSURE_CADENCE_OUTPUT"));
        var snapshotPath = Path.Combine(root, "scene.snapshot");
        var before = File.ReadAllBytes(snapshotPath);
        var world = LiveWorldSessionStore.ValidateExisting(root).Snapshot;
        var maximumId = int.Parse(Environment.GetEnvironmentVariable("HKV_CLOSURE_CADENCE_END_ID") ?? "830",
            System.Globalization.CultureInfo.InvariantCulture);
        var observations = world.Observations.Where(observation => observation.Id < maximumId).ToArray();
        if (observations.Length == 0) throw new InvalidDataException("Set a replay window containing saved observations.");
        using var archive = SceneSessionStore.OpenReadOnly(root);
        var manifest = archive.Manifest.Observations.ToDictionary(frame => frame.Id);
        var sources = world.Observations.ToDictionary(observation => observation.SourceObservationId);
        var owners = world.TrackingIslands.SelectMany(island => island.Members
            .Select(member => (Island: island, Member: member))).ToDictionary(owner => owner.Member.ObservationId);
        VisualReplayArchiveReader? camera = null;
        ILookup<double, VisualReplayFrame>? cameraFrames = null;
        if (Environment.GetEnvironmentVariable("HKV_SAME_VIEW_AUDIT_RECORDING") is { Length: > 0 } path)
        {
            path = Path.GetFullPath(path);
            var input = JsonSerializer.Deserialize<CameraArchive>(File.ReadAllBytes(path), Json);
            if (input?.VisualFrameArchive is not { } visualArchive)
                throw new InvalidDataException("Archive input does not contain original camera frames.");
            camera = new(Path.GetDirectoryName(path)!, visualArchive, input.Id);
            cameraFrames = camera.Manifest.Frames.Where(frame => frame.FileName is not null).ToLookup(frame => frame.CaptureTimestamp);
        }
        var results = new List<CadenceResult>();
        foreach (var (policy, confirmFirst, allowEarly, retainProbe) in new[]
        {
            ("current", false, true, false), ("confirmed-first", true, true, false),
            ("fixed-cadence", false, false, false), ("bounded-probes", false, true, true)
        })
        {
            var tracker = new LiveWorldTracker(new LiveWorldSnapshot(schemaVersion: world.SchemaVersion));
            var lastSearch = double.NegativeInfinity;
            var rows = new List<Scan>(observations.Length);
            var frames = new Dictionary<int, (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions)>();
            var order = new Queue<int>();
            foreach (var observation in observations)
            {
                var time = observation.CaptureTimestamp ?? throw new InvalidDataException("Capture timestamp is missing.");
                var (island, member) = owners[observation.Id];
                var floorEvidence = observation.GroundGeometry is { Lines.Count: > 0 }
                    || observation.ObservedFloorCandidates is { Lines.Count: > 0 };
                var periodicDue = observation.Timestamp - lastSearch >= .5;
                var shouldSearch = member.Measured && floorEvidence && (periodicDue || (allowEarly && tracker.HasPendingClosure));
                var probe = retainProbe && shouldSearch && !periodicDue;
                tracker.ClosureScanOverride = new(probe, confirmFirst);
                var (masked, masks) = Source(observation.SourceObservationId);
                var frame = masked;
                if (camera is not null)
                {
                    if (cameraFrames![time].Take(2).ToArray() is not [var source]
                        || source.Width != masked.Width || source.Height != masked.Height || source.SolveWidth != observation.SolveWidth)
                        throw new InvalidDataException($"No unique original image for saved observation {observation.Id}.");
                    frame = camera.ReadImage(source);
                }
                var placement = island.Placement == LiveIslandPlacement.Reference ? LiveIslandPlacement.Reference : LiveIslandPlacement.Estimated;
                var basis = new AtlasPoint(observation.RawPose.X - member.LocalPose.X, observation.RawPose.Y - member.LocalPose.Y);
                var capture = new LiveIslandCapture(island.Id, island.RoomId, island.StartedAt, basis,
                    member.LocalPose, placement, placement == LiveIslandPlacement.Reference ? island.Uncertainty : default,
                    island.BreakReason, member.Measured);
                var exclusions = masks.Select(rect => LiveWorldJson.FlipMask(new StencilRect(rect.X, rect.Y, rect.Width, rect.Height),
                    masked.Height)).ToArray();
                var update = tracker.Ingest(observation.Id, observation.Timestamp, frame, observation.RawPose, observation.SolveWidth,
                    sourceObservationId: observation.SourceObservationId, exclusions: exclusions, searchForClosure: shouldSearch,
                    localPose: observation.LocalPose, captureGeneration: observation.CaptureGeneration, localEpoch: observation.LocalEpoch,
                    basisRevision: observation.BasisRevision, roomId: observation.RoomId,
                    captureEvidence: new(time, observation.GroundGeometry, observation.ObservedFloorCandidates, observation.ObservedPlayerBounds),
                    loadGroundSource: Source, recordedIsland: capture);
                var reasons = update.Review.Rejections.Select(rejection => rejection.ToString()).ToArray();
                var proposal = update.Review.MatchLines.LastOrDefault(line => line.ToObservationId == observation.Id)?.FromObservationId;
                rows.Add(new(observation.Id, shouldSearch, probe, proposal, reasons, update.AcceptedClosure is not null));
                if (shouldSearch && !probe) lastSearch = observation.Timestamp;
                if (rows.Count % 100 == 0)
                    Console.WriteLine($"Closure cadence {policy}: {rows.Count}/{observations.Length} images.");
            }
            Check(rows.Any(row => row.Searched) && tracker.Snapshot.Observations.Select(observation => observation.RawPose)
                .SequenceEqual(observations.Select(observation => observation.RawPose)), "Cadence replay must preserve raw positions.");
            results.Add(new(policy, rows.ToArray(), tracker.Snapshot.LoopClosureEdges));
            Console.WriteLine($"Closure cadence {policy}: {rows.Count(row => row.Searched)} search requests, "
                + $"{rows.Count(row => row.ConfirmationProbe)} probes, {tracker.Snapshot.LoopClosureEdges.Count} closures.");

            (BgraFrame Image, IReadOnlyList<WorldRect> Exclusions) Source(int id)
            {
                if (frames.TryGetValue(id, out var evidence)) return evidence;
                var image = archive.Source(manifest[id]);
                var masks = world.CompositionMasks(sources[id]).Select(rect =>
                    new WorldRect(new(rect.X, rect.Y), new(rect.Width, rect.Height))).ToArray();
                if (order.Count == 12) frames.Remove(order.Dequeue());
                order.Enqueue(id);
                frames.Add(id, evidence = (image, masks));
                return evidence;
            }
        }
        Check(File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(before), "Cadence audit must preserve the saved world.");
        AtomicFile.Write(output, JsonSerializer.SerializeToUtf8Bytes(new
        {
            imageSource = camera is null ? "saved Atlas capture images" : "original camera archive",
            results
        }, Json), overwrite: false);
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} to run the saved island replay.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
