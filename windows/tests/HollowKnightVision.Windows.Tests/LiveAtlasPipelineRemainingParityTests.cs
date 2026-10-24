using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LiveAtlasPipelineRemainingParityTests
{
    public static void TestCadencePresentsAtCaptureRateAndWritesAtlasAtFiveFPS()
    {
        var cadence = new LiveAtlasCadence(
            presentationFramesPerSecond: 15,
            atlasFramesPerSecond: 5);

        var first = cadence.Decision(0, canIntegrate: true);
        Check(first.Present && first.Integrate,
            "The first capture was not both presented and integrated.");
        Check(!cadence.Decision(.03, canIntegrate: true).Present,
            "A capture inside the presentation interval was presented.");
        var nextPresentation = cadence.Decision(.07, canIntegrate: true);
        Check(nextPresentation.Present && !nextPresentation.Integrate,
            "The next presentation incorrectly wrote the atlas.");
        var nextAtlasWrite = cadence.Decision(.21, canIntegrate: true);
        Check(nextAtlasWrite.Present && nextAtlasWrite.Integrate,
            "The five-FPS atlas interval did not admit the next write.");
    }

    public static void TestAtlasWriteCanBeSkippedWhilePresentationContinues()
    {
        var pipeline = new LiveAtlasPresentationPipeline(
            presentationFramesPerSecond: 30,
            atlasFramesPerSecond: 30);
        var frame = Solid(40, 30, red: 255);

        var output = pipeline.Process(
                frame,
                new(0, 0),
                40,
                timestamp: 0,
                observationPoseTimestamp: 0,
                hasGameplaySignal: true,
                allowAtlasWrite: false)
            ?? throw new InvalidOperationException("Live presentation was skipped.");

        Check(!output.IntegratedFrame,
            "A disabled atlas write was reported as integrated.");
        Check(output.Atlas is null,
            "A disabled atlas write created an atlas.");
        Check(output.LiveImage.Width == 40,
            "The live frame was not presented while atlas writes were disabled.");
    }

    public static void TestStalePosePresentsFrameButCannotWriteAtlas()
    {
        var pipeline = new LiveAtlasPresentationPipeline(
            presentationFramesPerSecond: 30,
            atlasFramesPerSecond: 30);
        var frame = Solid(40, 30, red: 255);

        var output = pipeline.Process(
                frame,
                new(50, 0),
                40,
                timestamp: 2,
                observationPoseTimestamp: 1,
                hasGameplaySignal: true)
            ?? throw new InvalidOperationException("Stale-pose live frame was not presented.");

        Check(!output.IntegratedFrame,
            "A stale pose was integrated into the atlas.");
        Check(output.Atlas is null,
            "A stale pose created an atlas.");
        Close(20, output.FocusPoint.X, .001, "stale-pose focus X");
    }

    public static void TestDelayedExactPoseFrameCannotReplaceNewerLiveWindowOrEnterAtlas()
    {
        var workerStarts = 0;
        var committedTimestamps = new List<double>();
        var pipeline = new LiveAtlasPresentationPipeline(
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            integrationStarted: () => workerStarts++,
            integrationCompleted: completion =>
                committedTimestamps.Add(completion.Timestamp));
        var red = Solid(40, 20, red: 255);
        var blue = Solid(40, 20, blue: 255);

        var first = pipeline.Process(
                red,
                new(0, 0),
                40,
                timestamp: 10,
                observationPoseTimestamp: null,
                hasGameplaySignal: true)
            ?? throw new InvalidOperationException("Initial exact-pose frame was not presented.");
        Check(!first.IntegratedFrame && first.Atlas is null,
            "Live presentation entered the atlas before an exact observation was submitted.");
        Check(Pixel(first.LiveImage, 20, 10).Red > 180,
            "Initial red live frame was not presented.");

        Check(pipeline.SubmitAtlasObservation(
                red,
                new(0, 0),
                40,
                timestamp: 10,
                observationPoseTimestamp: 10,
                hasGameplaySignal: true,
                submittedAt: 1),
            "The newest exact-pose observation was not admitted.");
        Check(pipeline.SubmitAtlasObservation(
                blue,
                new(300, 0),
                40,
                timestamp: 9,
                observationPoseTimestamp: 9,
                hasGameplaySignal: true,
                submittedAt: 2),
            "The delayed exact-pose observation was rejected before ordered worker evaluation.");

        var latest = pipeline.Process(
                red,
                new(4, 0),
                40,
                timestamp: 11,
                observationPoseTimestamp: null,
                hasGameplaySignal: true)
            ?? throw new InvalidOperationException("Latest live frame was not presented.");
        Check(Pixel(latest.LiveImage, 20, 10).Red > 180,
            "The delayed blue frame replaced the latest red live image.");
        Close(24, latest.FocusPoint.X, .001, "latest live focus X");
        Check(!latest.IntegratedFrame,
            "A frame without an exact observation pose entered the atlas.");
        var atlas = latest.Atlas
            ?? throw new InvalidOperationException("Initial exact-pose atlas was missing.");
        Check(atlas.Tiles.Count == 1,
            $"Delayed work changed the atlas tile count to {atlas.Tiles.Count}.");
        var atlasPixel = Pixel(atlas, 10, 10);
        Check(atlasPixel.Red > 180 && atlasPixel.Blue < 80,
            $"Delayed blue pixels entered the atlas: {atlasPixel}.");
        Check(pipeline.IntegratedObservationCount == 1,
            "The delayed or inexact frame changed the integrated observation count.");
        Check(workerStarts == 2,
            "The delayed exact observation was not evaluated by the ordered worker.");
        Check(committedTimestamps.SequenceEqual([10d]),
            "The ordered worker committed the delayed observation.");
    }

    public static void TestRejectedAtlasSubmissionsCannotPoisonCadenceOrAnchor()
    {
        var pipeline = new LiveAtlasPresentationPipeline(
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 1,
            runsIntegrationSynchronously: true);
        var menu = Solid(40, 20, blue: 255);
        var gameplay = Solid(40, 20, red: 255);

        Check(!pipeline.SubmitAtlasObservation(
                menu,
                new(300, 0),
                40,
                timestamp: 1,
                observationPoseTimestamp: 1,
                hasGameplaySignal: false,
                submittedAt: 10),
            "Menu evidence was admitted to the atlas worker.");
        Check(!pipeline.SubmitAtlasObservation(
                menu,
                new(400, 0),
                40,
                timestamp: 1,
                observationPoseTimestamp: .5,
                hasGameplaySignal: true,
                submittedAt: 10),
            "An inexact observation pose was admitted to the atlas worker.");
        Check(!pipeline.SubmitAtlasObservation(
                menu,
                new(500, 0),
                40,
                timestamp: 1,
                observationPoseTimestamp: 1,
                hasGameplaySignal: true,
                submittedAt: 10,
                allowAtlasWrite: false),
            "A write-disabled observation was admitted to the atlas worker.");

        Check(pipeline.SubmitAtlasObservation(
                gameplay,
                new(0, 0),
                40,
                timestamp: 2,
                observationPoseTimestamp: 2,
                hasGameplaySignal: true,
                submittedAt: 10),
            "Rejected work poisoned cadence for the first valid observation.");
        var output = pipeline.Process(
                gameplay,
                new(4, 0),
                40,
                timestamp: 3,
                observationPoseTimestamp: null,
                hasGameplaySignal: true)
            ?? throw new InvalidOperationException("Valid gameplay was not presented.");
        Close(24, output.FocusPoint.X, .001, "unpoisoned gameplay focus X");
        Check(output.Atlas?.Tiles.Count == 1,
            "Rejected work blocked the valid atlas observation.");
        Check(Pixel(output.Atlas!, 10, 10).Red > 180,
            "Rejected menu pixels entered the atlas.");
        Check(pipeline.IntegratedObservationCount == 1,
            "Rejected submissions changed the integrated observation count.");
    }

    public static void TestSlowAtlasWorkerDoesNotDelayLatestLivePresentation()
    {
        using var secondInsertionEntered = new ManualResetEventSlim();
        using var releaseSecondInsertion = new ManualResetEventSlim();
        using var firstIntegrationCompleted = new ManualResetEventSlim();
        using var secondIntegrationCompleted = new ManualResetEventSlim();
        var insertionCount = 0;
        var completionCount = 0;
        var pipeline = new LiveAtlasPresentationPipeline(
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: false,
            integrationCompleted: _ =>
            {
                if (Interlocked.Increment(ref completionCount) == 1)
                    firstIntegrationCompleted.Set();
                else
                    secondIntegrationCompleted.Set();
            },
            atlasInsertionEntered: () =>
            {
                if (Interlocked.Increment(ref insertionCount) != 2) return;
                secondInsertionEntered.Set();
                if (!releaseSecondInsertion.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The deterministic atlas gate was not released.");
            });
        var red = Solid(40, 20, red: 255);
        var blue = Solid(40, 20, blue: 255);
        var green = Solid(40, 20, green: 255);

        try
        {
            _ = pipeline.Process(
                    red,
                    new(0, 0),
                    40,
                    timestamp: 0,
                    observationPoseTimestamp: null,
                    hasGameplaySignal: true)
                ?? throw new InvalidOperationException("Initial live frame was not presented.");
            Check(pipeline.SubmitAtlasObservation(
                    red,
                    new(0, 0),
                    40,
                    timestamp: 0,
                    observationPoseTimestamp: 0,
                    hasGameplaySignal: true,
                    submittedAt: 1),
                "The initial exact-pose observation was not admitted.");
            Check(firstIntegrationCompleted.Wait(TimeSpan.FromSeconds(2)),
                "The initial red observation did not commit.");
            Check(SpinWait.SpinUntil(
                    () => !pipeline.IsIntegrating,
                    TimeSpan.FromSeconds(2)),
                "The initial atlas worker did not become idle.");

            var firstPresentation = pipeline.Process(
                    red,
                    new(0, 0),
                    40,
                    timestamp: .04,
                    observationPoseTimestamp: null,
                    hasGameplaySignal: true)
                ?? throw new InvalidOperationException("Committed red atlas was not presented.");
            var firstAtlas = firstPresentation.Atlas
                ?? throw new InvalidOperationException("Initial red atlas snapshot was missing.");
            Check(Pixel(firstAtlas, 10, 10).Red > 180,
                "The initial cached atlas did not contain red pixels.");

            Check(pipeline.SubmitAtlasObservation(
                    blue,
                    new(8, 0),
                    40,
                    timestamp: 1,
                    observationPoseTimestamp: 1,
                    hasGameplaySignal: true,
                    submittedAt: 2),
                "The slow second exact-pose observation was not admitted.");
            Check(secondInsertionEntered.Wait(TimeSpan.FromSeconds(2)),
                "The second insertion did not enter the live atlas gate.");
            Thread.Sleep(210);
            Check(!releaseSecondInsertion.IsSet
                  && !secondIntegrationCompleted.IsSet
                  && pipeline.IsIntegrating,
                "The live atlas gate did not hold the second insertion for 200 ms.");

            var stopwatch = Stopwatch.StartNew();
            var latest = pipeline.Process(
                    green,
                    new(8, 0),
                    40,
                    timestamp: 1.04,
                    observationPoseTimestamp: null,
                    hasGameplaySignal: true)
                ?? throw new InvalidOperationException("Latest live frame was not presented.");
            stopwatch.Stop();

            Check(!releaseSecondInsertion.IsSet
                  && !secondIntegrationCompleted.IsSet
                  && pipeline.IsIntegrating,
                "Latest live presentation waited for the insertion holding the atlas gate.");
            Check(stopwatch.Elapsed < TimeSpan.FromMilliseconds(80),
                $"Latest live presentation took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
            Check(Pixel(latest.LiveImage, 20, 10).Green > 180,
                "The latest green pixels were not presented while atlas work was blocked.");
            Close(28, latest.FocusPoint.X, .001, "latest nonblocking focus X");
            var cachedAtlas = latest.Atlas
                ?? throw new InvalidOperationException("Prior cached atlas was not presented.");
            Check(cachedAtlas.Revision == firstAtlas.Revision,
                "Blocked insertion replaced the prior cached atlas revision.");
            var cachedPixel = Pixel(cachedAtlas, 10, 10);
            Check(cachedPixel.Red > 180 && cachedPixel.Blue < 80,
                $"Blocked insertion changed prior cached atlas pixels: {cachedPixel}.");

            releaseSecondInsertion.Set();
            Check(secondIntegrationCompleted.Wait(TimeSpan.FromSeconds(2)),
                "The second atlas integration did not complete after its gate was released.");
            Check(SpinWait.SpinUntil(
                    () => !pipeline.IsIntegrating,
                    TimeSpan.FromSeconds(2)),
                "The asynchronous atlas worker did not become idle.");
            Check(pipeline.IntegrationFailure is null,
                $"The asynchronous atlas worker failed: {pipeline.IntegrationFailure}");
            Check(pipeline.IntegratedObservationCount == 2 && completionCount == 2,
                "The worker did not commit exactly both exact-pose observations.");
        }
        finally
        {
            releaseSecondInsertion.Set();
        }
    }

    public static void TestResetInvalidatesActiveTicketBeforeItCanPersistOrPublish()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(), $"hkv-reset-active-ticket-{Guid.NewGuid():N}");
        var root = Path.Combine(workspace, "live-world");
        using var appendEntered = new ManualResetEventSlim(false);
        using var releaseAppend = new ManualResetEventSlim(false);
        Task<LiveWorldPipelineReflectionResult>? processing = null;
        Task? invalidation = null;
        var stages = new List<string>();

        try
        {
            var pipeline = new LiveWorldPipelineReflection(
                root,
                stages.Add,
                stage =>
                {
                    if (stage != "append") return false;
                    appendEntered.Set();
                    if (!releaseAppend.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The deterministic append barrier was not released.");
                    return false;
                });
            var red = Solid(40, 20, red: 255);
            processing = Task.Run(() => pipeline.Process(
                red,
                wallTimestamp: 1_700_000_000,
                monotonicTimestamp: 1,
                captureGeneration: 1,
                roomName: "Tutorial_01"));
            Check(appendEntered.Wait(TimeSpan.FromSeconds(2)),
                "The active durable ticket did not reach its pre-append barrier.");

            invalidation = Task.Run(pipeline.InvalidatePendingWork);
            Check(SpinWait.SpinUntil(
                    () => !pipeline.AcceptsWork,
                    TimeSpan.FromSeconds(2)),
                "Invalidation did not close work admission before waiting for the transaction.");
            Check(!invalidation.IsCompleted,
                "Invalidation did not wait for the active transaction lock.");

            releaseAppend.Set();
            Check(processing.Wait(TimeSpan.FromSeconds(2)),
                "The invalidated durable ticket did not finish.");
            Check(invalidation.Wait(TimeSpan.FromSeconds(2)),
                "Invalidation did not finish after the transaction barrier was released.");
            var cancelled = processing.GetAwaiter().GetResult();
            Check(cancelled.State == "PendingRecovery",
                $"Invalidated work returned {cancelled.State} instead of PendingRecovery.");
            Check(cancelled.Diagnostic.Contains(
                    "active atlas generation changed",
                    StringComparison.Ordinal),
                $"Invalidated work returned the wrong diagnostic: {cancelled.Diagnostic}");
            Check(!cancelled.AllowsAtlasWrite && !cancelled.HasRebuiltAtlas,
                "Invalidated work remained publishable.");
            Check(stages.Count == 0,
                "Invalidated work emitted a transaction stage callback.");

            var manifest = new SceneSessionStore(root).Manifest;
            Check(manifest.Observations.Count == 0,
                "Invalidated work appended scene-session evidence.");
            var emptySnapshot = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(emptySnapshot.Observations.Count == 0,
                "Invalidated work persisted a live-world observation.");
            Check(emptySnapshot.RelativeMotionEdges.Count == 0
                  && emptySnapshot.LoopClosureEdges.Count == 0,
                "Invalidated work persisted a live-world edge.");
            Check(!Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Any(),
                "Invalidated work persisted evidence pixels.");

            _ = new LiveWorldSessionStore(root).ArchiveAndReset(
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_001));
            Check(pipeline.Reopen(),
                "The live-world pipeline did not reopen after reset.");
            Check(pipeline.AcceptsWork,
                "Reopen did not admit work for the fresh generation.");

            stages.Clear();
            var green = Solid(40, 20, green: 255);
            var committed = pipeline.Process(
                green,
                wallTimestamp: 1_700_000_002,
                monotonicTimestamp: 2,
                captureGeneration: 2,
                roomName: "Tutorial_01");
            Check(committed.State == "Committed",
                $"Fresh work returned {committed.State} instead of Committed: {committed.Diagnostic}");
            Check(stages.SequenceEqual(["append", "ingest", "commit", "publish"]),
                $"Fresh work emitted the wrong transaction order: {string.Join(",", stages)}");
            var freshSnapshot = new LiveWorldSessionStore(root).Load().Snapshot;
            Check(freshSnapshot.Observations.Count == 1,
                "Fresh work did not persist exactly one observation.");
            Check(new SceneSessionStore(root).Manifest.Observations.Count == 1,
                "Fresh work did not persist exactly one source manifest entry.");
            Check(Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories).Count() == 1,
                "Fresh work did not persist exactly one evidence PNG.");
        }
        finally
        {
            releaseAppend.Set();
            if (processing is not null)
                _ = processing.Wait(TimeSpan.FromSeconds(2));
            if (invalidation is not null)
                _ = invalidation.Wait(TimeSpan.FromSeconds(2));
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }

    public static void TestResetCancelsPersistedAtlasRestoration()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(), $"hkv-reset-restoration-{Guid.NewGuid():N}");
        var root = Path.Combine(workspace, "live-world");
        using var restorationStarted = new ManualResetEventSlim(false);
        using var releaseRestoration = new ManualResetEventSlim(false);
        Task? restoration = null;
        try
        {
            var owner = new LiveWorldSessionStore(root);
            var stored = Enumerable.Range(0, 12).Select(index => owner.Append(
                Solid(
                    32,
                    18,
                    red: checked((byte)(index * 17)),
                    green: checked((byte)(255 - index * 11)),
                    blue: checked((byte)(index * 7))),
                timestamp: index + 1,
                rawCameraPose: new AtlasPoint(index * 3, -index),
                solveWidth: 32)).ToArray();
            var observations = stored.Select((source, index) =>
                LiveWorldObservation.Create(
                    index,
                    source.Id,
                    source.Timestamp,
                    32,
                    18,
                    source.SolveWidth,
                    16,
                    [],
                    source.CameraPosition,
                    source.CameraPosition,
                    roomId: 0)).ToArray();
            var snapshot = new LiveWorldSnapshot(
                mapRevision: 1,
                anchoredObservationId: 0,
                observations: observations);
            Check(owner.Commit(snapshot, expectedRevision: 0, newRevision: 1),
                "The persisted restoration fixture did not commit.");

            var staleReader = new LiveWorldSessionStore(root);
            Exception? restorationFailure = null;
            var restoredSourceCount = 0;
            restoration = Task.Run(() =>
            {
                try
                {
                    var persisted = staleReader.Load().Snapshot.Observations
                        .OrderBy(value => value.Id)
                        .ToArray();
                    Check(persisted.Length == 12,
                        "Restoration did not load all twelve persisted observations.");
                    Check(staleReader.Source(persisted[0].SourceObservationId).Width == 32,
                        "Restoration did not read its first persisted source.");
                    Interlocked.Increment(ref restoredSourceCount);
                    restorationStarted.Set();
                    if (!releaseRestoration.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Persisted restoration barrier timed out.");
                    foreach (var observation in persisted.Skip(1))
                    {
                        _ = staleReader.Source(observation.SourceObservationId);
                        Interlocked.Increment(ref restoredSourceCount);
                    }
                }
                catch (Exception error)
                {
                    restorationFailure = error;
                }
            });
            Check(restorationStarted.Wait(TimeSpan.FromSeconds(2)),
                "Persisted restoration did not reach the post-first-source barrier.");
            Check(restoredSourceCount == 1,
                "Restoration passed its deterministic first-source barrier.");

            var stopwatch = Stopwatch.StartNew();
            var archive = owner.ArchiveAndReset(
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
            stopwatch.Stop();
            Check(stopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
                $"Reset waited {stopwatch.Elapsed.TotalMilliseconds:F1} ms for the blocked restoration reader.");
            Check(!releaseRestoration.IsSet && !restoration.IsCompleted,
                "Reset released or waited for the blocked restoration reader.");

            releaseRestoration.Set();
            Check(restoration.Wait(TimeSpan.FromSeconds(5)),
                "Stale restoration did not finish after its barrier was released.");
            Check(restorationFailure is LiveWorldSessionStoreException
                { Error: LiveWorldSessionStoreError.SessionInvalidated },
                $"Stale restoration failed with the wrong result: {restorationFailure}");
            Check(restoredSourceCount == 1,
                "A stale restoration source was read from the fresh generation.");

            LiveWorldSessionStoreException? staleAppendFailure = null;
            try
            {
                _ = staleReader.Append(
                    Solid(32, 18, red: 255),
                    timestamp: 20,
                    rawCameraPose: new AtlasPoint(99, 0),
                    solveWidth: 32);
            }
            catch (LiveWorldSessionStoreException error)
            {
                staleAppendFailure = error;
            }
            Check(staleAppendFailure?.Error == LiveWorldSessionStoreError.SessionInvalidated,
                "A stale restoration store appended into the fresh generation.");

            var fresh = archive.FreshStore.Load();
            var freshManifest = new SceneSessionStore(root).Manifest;
            Check(fresh.SceneRevision == 0
                  && fresh.Snapshot.Observations.Count == 0,
                "Reset retained a stale world snapshot in the fresh generation.");
            Check(freshManifest.Observations.Count == 0
                  && freshManifest.NextObservationId == 0,
                "Reset retained or recreated stale manifest observations.");
            var freshFrames = Path.Combine(root, "frames");
            Check(Directory.Exists(freshFrames)
                  && !Directory.EnumerateFileSystemEntries(freshFrames).Any(),
                "Stale restoration repopulated fresh source evidence.");
        }
        finally
        {
            releaseRestoration.Set();
            if (restoration is not null && !restoration.IsCompleted)
                _ = restoration.Wait(TimeSpan.FromSeconds(5));
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    public static void TestGameplayAtlasUsesNewestPixelImmediately()
    {
        var atlas = new GroundTruthAtlasMosaic(
            pixelsPerWorldUnit: 1,
            tileSize: 64,
            sourceTopInsetFraction: 0);
        var bright = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);

        Check(atlas.AddFrame(bright, Telemetry(bright, timestamp: 1)),
            "Initial white atlas observation was rejected.");
        foreach (var timestamp in new[] { 1.1 })
        {
            Check(atlas.AddFrame(dark, Telemetry(dark, timestamp)),
                $"Dark atlas observation at {timestamp} was rejected.");
        }

        Check(atlas.TryReadPixel(
                0, 0, out var blue, out var green, out var red, out var observations),
            "The production gameplay atlas did not publish the sampled world pixel.");
        Check(observations == 2,
            $"The gameplay atlas lost lifetime observations: {observations}.");
        Check(red < 40 && green < 40 && blue < 40,
            $"The previous white pixel survived the latest dark observation: ({red}, {green}, {blue}).");
        Check(red == 26 && green == 26 && blue == 26,
            $"Newest-pixel composition changed the observed color: ({red}, {green}, {blue}).");
        Check(atlas.RetainedObservationCount == 0
              && atlas.RetainedObservationBytes == 0
              && atlas.OutstandingObservationBufferCount == 0,
            "Newest-pixel composition retained temporal pixel history.");
    }

    public static void TestTransitionBridgeRectangularEdgeMaskPreservesMappedRoom()
    {
        const int width = 40;
        const int height = 30;
        const string roomId = "mapped-room";
        const ulong captureGeneration = 17;
        var existing = new PixelRect(39, 29, 1, 1);
        var edgeSignal = new FrameSignalProfile(
            1,
            1,
            1,
            255,
            EdgeBlackBands: new FrameEdgeBlackBands(
                new FrameEdgeBlackBand(9d / width, new FrameFractionRange(0, 1)),
                null));

        var inactive = LiveAtlasTransitionMaskPolicy.Merge(
            [existing, existing], width, height, edgeSignal,
            transitionBridgeActive: false);
        Check(inactive.SequenceEqual([existing]),
            "An inactive transition bridge added an edge band or lost/doubled an existing exclusion.");

        var transitionExclusions = LiveAtlasTransitionMaskPolicy.Merge(
            [existing, existing], width, height, edgeSignal,
            transitionBridgeActive: true);
        Check(transitionExclusions.SequenceEqual(
                [existing, new PixelRect(0, 0, 10, height)]),
            "The active transition bridge did not preserve/deduplicate the existing top-origin mask and add exactly the clipped ten-pixel edge band.");

        static BgraFrame Capture(
            long captureId,
            double timestamp,
            byte red,
            byte green)
        {
            var source = Solid(width, height, red: red, green: green);
            return new BgraFrame(
                source.Width,
                source.Height,
                source.Stride,
                source.Pixels,
                captureId,
                DateTimeOffset.UnixEpoch.AddSeconds(timestamp));
        }

        var atlas = Mosaic();
        var initial = Capture(101, 1, red: 255, green: 0);
        Check(atlas.AddFrame(
                initial,
                Telemetry(initial, 1, cameraX: width / 2d, cameraY: -height / 2d),
                roomId: roomId,
                captureGeneration: captureGeneration,
                captureIdentity: "mapped-room:red:101"),
            "The initial opaque red room frame was rejected.");

        var timestamps = new[] { 2d, 2.1, 2.2 };
        for (var index = 0; index < timestamps.Length; index++)
        {
            var timestamp = timestamps[index];
            var transition = Capture(102 + index, timestamp, red: 0, green: 255);
            Check(atlas.AddFrame(
                    transition,
                    Telemetry(
                        transition,
                        timestamp,
                        cameraX: width / 2d,
                        cameraY: -height / 2d),
                    transitionExclusions,
                    roomId,
                    captureGeneration,
                    captureIdentity: $"mapped-room:green:{102 + index}"),
                $"Transition-bridge capture {index + 1} was rejected.");
        }

        var preserved = GroundPixel(atlas, 5, 10);
        Check(preserved.Red > 180 && preserved.Green < 80
              && preserved.Observations == 1,
            $"The mapped room under the rectangular edge matte was overwritten: {preserved}.");
        var replaced = GroundPixel(atlas, 20, 10);
        Check(replaced.Red < 80 && replaced.Green > 180
              && replaced.Observations == 4,
            $"Unmasked transition pixels did not replace red after temporal consensus: {replaced}.");
        Check(atlas.AcceptedFrameCount == 4,
            "Distinct transition capture identities were not all admitted.");
    }

    public static void TestGroundTruthTemporalAgreementDeduplicatesCaptureIdentity()
    {
        var atlas = Mosaic();
        var white = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);
        Check(atlas.AddFrame(white, Telemetry(white, 1), captureIdentity: "white"),
            "Initial observation was rejected.");
        Check(atlas.AddFrame(dark, Telemetry(dark, 1.1), captureIdentity: "dark-1"),
            "First dark observation was rejected.");
        Check(atlas.AddFrame(dark, Telemetry(dark, 1.2), captureIdentity: "dark-1"),
            "First retry was not accepted idempotently.");
        Check(atlas.AddFrame(dark, Telemetry(dark, 1.3), captureIdentity: "dark-1"),
            "Second retry was not accepted idempotently.");

        var before = GroundPixel(atlas, 0, 0);
        Check(before.Red == 255 && before.Observations == 2,
            $"Duplicate retries manufactured consensus or weight: {before}.");
        Check(atlas.AcceptedFrameCount == 2 && atlas.RetainedObservationCount == 2,
            "Duplicate retries changed accepted or retained observation counts.");

        Check(atlas.AddFrame(dark, Telemetry(dark, 1.4), captureIdentity: "dark-2"));
        Check(atlas.AddFrame(dark, Telemetry(dark, 1.5), captureIdentity: "dark-3"));
        var after = GroundPixel(atlas, 0, 0);
        Check(after.Red == 26 && after.Observations == 4,
            $"Three distinct dark captures did not reach consensus: {after}.");
    }

    public static void TestGroundTruthTemporalAgreementClearsAcrossContextDiscontinuities()
    {
        var white = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);
        foreach (var discontinuity in new[] { "room", "generation", "dimensions" })
        {
            var atlas = Mosaic();
            Check(atlas.AddFrame(
                white, Telemetry(white, 1), roomId: "A", captureGeneration: 1,
                captureIdentity: $"{discontinuity}-white"));
            Check(atlas.AddFrame(
                dark, Telemetry(dark, 1.1), roomId: "A", captureGeneration: 1,
                captureIdentity: $"{discontinuity}-dark-1"));
            Check(atlas.AddFrame(
                dark, Telemetry(dark, 1.2), roomId: "A", captureGeneration: 1,
                captureIdentity: $"{discontinuity}-dark-2"));

            var nextFrame = discontinuity == "dimensions"
                ? Solid(42, 30, red: 26, green: 26, blue: 26)
                : dark;
            var nextRoom = discontinuity == "room" ? "B" : "A";
            var nextGeneration = discontinuity == "generation" ? 2UL : 1UL;
            Check(atlas.AddFrame(
                nextFrame, Telemetry(nextFrame, 1.3), roomId: nextRoom,
                captureGeneration: nextGeneration,
                captureIdentity: $"{discontinuity}-after"));

            Check(atlas.RetainedObservationCount == 1,
                $"{discontinuity} discontinuity retained prior temporal evidence.");
            Check(GroundPixel(atlas, 0, 0).Red == 255,
                $"{discontinuity} discontinuity allowed prior evidence to manufacture consensus.");
        }
    }

    public static void TestGroundTruthTemporalAgreementHonorsMasksAndAlpha()
    {
        var atlas = Mosaic();
        var white = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);
        var transparentDark = Solid(40, 30, red: 26, green: 26, blue: 26, alpha: 0);
        var translucentDark = Solid(40, 30, red: 26, green: 26, blue: 26, alpha: 128);
        Check(atlas.AddFrame(white, Telemetry(white, 1), captureIdentity: "white"));
        var targetMask = new[] { new PixelRect(19, 14, 3, 3) };
        for (var index = 0; index < 3; index++)
        {
            var timestamp = 1.1 + index * .1;
            Check(atlas.AddFrame(
                dark, Telemetry(dark, timestamp), targetMask,
                captureIdentity: $"masked-{index}"));
        }
        var masked = GroundPixel(atlas, 0, 0);
        Check(masked.Red == 255 && masked.Observations == 1,
            $"Masked samples changed the target atlas pixel: {masked}.");

        for (var index = 0; index < 3; index++)
        {
            var timestamp = 1.4 + index * .1;
            Check(atlas.AddFrame(
                transparentDark, Telemetry(transparentDark, timestamp),
                captureIdentity: $"transparent-{index}"));
        }
        var transparent = GroundPixel(atlas, 0, 0);
        Check(transparent.Red == 255 && transparent.Observations == 1,
            $"Alpha-zero samples became temporal evidence: {transparent}.");

        for (var index = 0; index < 3; index++)
        {
            var timestamp = 1.7 + index * .1;
            Check(atlas.AddFrame(
                translucentDark, Telemetry(translucentDark, timestamp),
                captureIdentity: $"translucent-{index}"));
        }
        var translucent = GroundPixel(atlas, 0, 0);
        Check(translucent.Red == 26 && translucent.Alpha == 128
              && translucent.Observations == 4,
            $"Consensus did not preserve the selected observed alpha: {translucent}.");
    }

    public static void TestGroundTruthTemporalAgreementVotesAcrossTileBoundaries()
    {
        var atlas = Mosaic();
        var initial = SparseColumns(4, 3,
            (1, (byte)255, (byte)255, (byte)255, (byte)255),
            (2, (byte)255, (byte)255, (byte)255, (byte)255));
        Check(atlas.AddFrame(initial, Telemetry(initial, 1), captureIdentity: "initial"));
        var outliers = new[]
        {
            (Red: (byte)255, Green: (byte)0, Blue: (byte)0),
            (Red: (byte)0, Green: (byte)255, Blue: (byte)0),
            (Red: (byte)0, Green: (byte)0, Blue: (byte)255)
        };
        for (var index = 0; index < outliers.Length; index++)
        {
            var outlier = outliers[index];
            var frame = SparseColumns(4, 3,
                (1, outlier.Red, outlier.Green, outlier.Blue, (byte)255),
                (2, (byte)26, (byte)26, (byte)26, (byte)255));
            Check(atlas.AddFrame(
                frame, Telemetry(frame, 1.1 + index * .1),
                captureIdentity: $"edge-{index}"));
        }

        var boundary = GroundPixel(atlas, 0, 0);
        Check(boundary.Red == 255,
            $"Cross-tile non-consensus neighbor failed to block isolated agreement: {boundary}.");
    }

    public static void TestGroundTruthTemporalStorageReturnsOnResetAndImport()
    {
        var atlas = Mosaic();
        var frame = Solid(40, 30, red: 32, green: 48, blue: 64);
        Check(atlas.AddFrame(frame, Telemetry(frame, 1), captureIdentity: "before-reset"));
        Check(atlas.RetainedObservationCount == 1
              && atlas.RetainedObservationBytes == 40L * 30 * 4
              && atlas.RetainedObservationCapacityBytes >= atlas.RetainedObservationBytes,
            "Temporal storage diagnostics did not reflect the retained observation.");
        atlas.Reset();
        Check(atlas.RetainedObservationCount == 0
              && atlas.RetainedObservationBytes == 0
              && atlas.RetainedObservationCapacityBytes == 0
              && atlas.OutstandingObservationBufferCount == 0,
            "Reset retained pooled temporal storage.");

        Check(atlas.AddFrame(frame, Telemetry(frame, 2), captureIdentity: "before-import"));
        var imported = Solid(64, 64, red: 7, green: 8, blue: 9, alpha: 123);
        atlas.ImportTile(new AtlasTileCoordinate(0, 0), imported);
        Check(atlas.RetainedObservationCount == 0
              && atlas.RetainedObservationBytes == 0
              && atlas.RetainedObservationCapacityBytes == 0
              && atlas.OutstandingObservationBufferCount == 0,
            "ImportTile retained pooled temporal storage.");
        var restored = GroundPixel(atlas, 0, 0);
        Check(restored.Red == 7 && restored.Green == 8 && restored.Blue == 9
              && restored.Alpha == 123 && restored.Observations == 1,
            $"Imported persistence state changed: {restored}.");
    }

    public static void TestGroundTruthTemporalCommitFailureRollsBackAtomically()
    {
        var failAtCommit = false;
        var atlas = new GroundTruthAtlasMosaic(
            pixelsPerWorldUnit: 1,
            tileSize: 64,
            sourceTopInsetFraction: 0,
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement,
            temporalCommitCheckpoint: () =>
            {
                if (failAtCommit) throw new ApplicationException("Injected temporal commit failure.");
            });
        var white = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);
        Check(atlas.AddFrame(white, Telemetry(white, 1), captureIdentity: "baseline"));
        var baselineSnapshot = atlas.Snapshot();
        var baselinePixel = GroundPixel(atlas, 0, 0);
        var baselineRevision = atlas.Revision;
        var baselineAccepted = atlas.AcceptedFrameCount;
        var baselineTileCount = atlas.TileCount;
        var baselineRetainedCount = atlas.RetainedObservationCount;
        var baselineRetainedBytes = atlas.RetainedObservationBytes;
        var baselineCapacityBytes = atlas.RetainedObservationCapacityBytes;
        var baselineOutstandingBuffers = atlas.OutstandingObservationBufferCount;

        failAtCommit = true;
        AssertInjectedCommitFailure(() => atlas.AddFrame(
            dark, Telemetry(dark, 1.1), captureIdentity: "failed-existing"));
        AssertGroundTruthStateUnchanged(
            atlas,
            baselineSnapshot,
            baselinePixel,
            baselineRevision,
            baselineAccepted,
            baselineTileCount,
            baselineRetainedCount,
            baselineRetainedBytes,
            baselineCapacityBytes,
            baselineOutstandingBuffers,
            "existing-tile failure");

        AssertInjectedCommitFailure(() => atlas.AddFrame(
            dark, Telemetry(dark, 1.2, cameraX: 100),
            captureIdentity: "failed-new-tiles"));
        AssertGroundTruthStateUnchanged(
            atlas,
            baselineSnapshot,
            baselinePixel,
            baselineRevision,
            baselineAccepted,
            baselineTileCount,
            baselineRetainedCount,
            baselineRetainedBytes,
            baselineCapacityBytes,
            baselineOutstandingBuffers,
            "new-tile failure");

        failAtCommit = false;
        Check(atlas.AddFrame(dark, Telemetry(dark, 1.3), captureIdentity: "recovery"),
            "Atlas did not accept work after an injected commit failure.");
        Check(atlas.AcceptedFrameCount == baselineAccepted + 1
              && atlas.RetainedObservationCount == baselineRetainedCount + 1
              && atlas.OutstandingObservationBufferCount == baselineOutstandingBuffers + 1,
            "Atlas did not recover balanced temporal ownership after rollback.");
    }

    public static void TestGroundTruthTemporalReentrantCheckpointCannotCommitStaleState()
    {
        var white = Solid(40, 30, red: 255, green: 255, blue: 255);
        var dark = Solid(40, 30, red: 26, green: 26, blue: 26);

        GroundTruthAtlasMosaic? resetAtlas = null;
        var resetAtCheckpoint = false;
        resetAtlas = new GroundTruthAtlasMosaic(
            pixelsPerWorldUnit: 1,
            tileSize: 64,
            sourceTopInsetFraction: 0,
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement,
            temporalCommitCheckpoint: () =>
            {
                if (!resetAtCheckpoint) return;
                resetAtCheckpoint = false;
                resetAtlas!.Reset();
            });
        Check(resetAtlas.AddFrame(
            white, Telemetry(white, 1), captureIdentity: "reset-baseline"));
        resetAtCheckpoint = true;
        Check(!resetAtlas.AddFrame(
                dark, Telemetry(dark, 1.1), captureIdentity: "reset-outer"),
            "An outer add committed after its checkpoint reset the atlas.");
        Check(resetAtlas.TileCount == 0
              && resetAtlas.AcceptedFrameCount == 0
              && resetAtlas.RetainedObservationCount == 0
              && resetAtlas.RetainedObservationBytes == 0
              && resetAtlas.RetainedObservationCapacityBytes == 0
              && resetAtlas.OutstandingObservationBufferCount == 0,
            "The stale outer add resurrected state or leaked storage after reentrant reset.");
        Check(!resetAtlas.TryReadPixel(0, 0, out _, out _, out _, out _),
            "The stale outer add republished a pixel after reentrant reset.");

        var imported = Solid(64, 64, red: 7, green: 8, blue: 9, alpha: 123);
        GroundTruthAtlasMosaic? importAtlas = null;
        var importAtCheckpoint = false;
        importAtlas = new GroundTruthAtlasMosaic(
            pixelsPerWorldUnit: 1,
            tileSize: 64,
            sourceTopInsetFraction: 0,
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement,
            temporalCommitCheckpoint: () =>
            {
                if (!importAtCheckpoint) return;
                importAtCheckpoint = false;
                importAtlas!.ImportTile(new AtlasTileCoordinate(0, 0), imported);
            });
        Check(importAtlas.AddFrame(
            white, Telemetry(white, 1), captureIdentity: "import-baseline"));
        importAtCheckpoint = true;
        Check(!importAtlas.AddFrame(
                dark, Telemetry(dark, 1.1), captureIdentity: "import-outer"),
            "An outer add committed after its checkpoint imported a tile.");
        var restored = GroundPixel(importAtlas, 0, 0);
        Check(restored.Red == 7 && restored.Green == 8 && restored.Blue == 9
              && restored.Alpha == 123 && restored.Observations == 1,
            $"The stale outer add overwrote reentrantly imported state: {restored}.");
        Check(importAtlas.AcceptedFrameCount == 1
              && importAtlas.RetainedObservationCount == 0
              && importAtlas.RetainedObservationBytes == 0
              && importAtlas.RetainedObservationCapacityBytes == 0
              && importAtlas.OutstandingObservationBufferCount == 0,
            "The stale outer add resurrected history or leaked storage after reentrant import.");
    }

    public static void TestGroundTruthTemporalFullResolutionHistoryIsBounded()
    {
        const int width = 640;
        const int height = 360;
        var atlas = Mosaic();
        var frame = Solid(width, height, red: 32, green: 48, blue: 64);
        var timings = new List<double>();
        for (var index = 0; index < 7; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            Check(atlas.AddFrame(
                frame, Telemetry(frame, index / 7.5),
                captureIdentity: $"full-{index}"));
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        const long expectedLogicalBytes = 7L * width * height * BgraFrame.BytesPerPixel;
        Check(atlas.RetainedObservationCount == 7,
            "Full-resolution temporal history did not retain exactly seven observations.");
        Check(atlas.OutstandingObservationBufferCount == 7,
            "Full-resolution temporal history has unbalanced pooled-buffer ownership.");
        Check(atlas.RetainedObservationBytes == expectedLogicalBytes,
            $"Expected {expectedLogicalBytes} retained bytes; got {atlas.RetainedObservationBytes}.");
        Check(atlas.RetainedObservationCapacityBytes <= 7L * 1024 * 1024,
            $"Pooled capacity exceeded seven 1 MiB buffers: {atlas.RetainedObservationCapacityBytes}.");
        Console.WriteLine(
            "GROUND_TRUTH_TEMPORAL_640x360_MS runs=7 ms="
            + string.Join(",", timings.Select(value => value.ToString("F3")))
            + $" logicalBytes={atlas.RetainedObservationBytes}"
            + $" capacityBytes={atlas.RetainedObservationCapacityBytes}");
    }

    public static void TestSolvedCameraPosePlacesCurrentFrameOverAtlas()
    {
        var pipeline = new LiveAtlasPresentationPipeline();
        var first = Solid(80, 50, red: 13, green: 191, blue: 38);
        var current = Solid(80, 50, red: 217, green: 20, blue: 10);

        Check(pipeline.SubmitAtlasObservation(
                first, new(0, 0), 80, 0, hasGameplaySignal: true),
            "Initial camera placement was rejected.");
        _ = pipeline.Process(first, new(0, 0), 80);
        Check(pipeline.SubmitAtlasObservation(
                current, new(20, 0), 80, .2, hasGameplaySignal: true),
            "Solved current camera placement was rejected.");
        var output = pipeline.Process(current, new(20, 0), 80);

        var atlas = output.Atlas
            ?? throw new InvalidOperationException("Atlas presentation was missing.");
        var tile = atlas.Tiles.Single();
        EqualRect(new(0, 0, LiveTiledAtlas.TileSize, LiveTiledAtlas.TileSize),
            tile.WorldBounds, "atlas tile bounds");
        EqualRect(new(0, 0, 100, 50), atlas.ContentBounds,
            "registered content bounds");
        EqualRect(new(20, 0, 80, 50), output.LiveBounds, "live-frame bounds");
        Close(60, output.FocusPoint.X, .001, "focus X");
        Check(!ReferenceEquals(output.LiveImage, current),
            "Presentation returned the mutable input frame instead of an owned live image.");

        var retainedAtlas = Pixel(atlas, 10, 25);
        var transformedCurrent = Pixel(output.LiveImage, 10, 25);
        Check(retainedAtlas.Green > 150 && retainedAtlas.Red < 80,
            $"Atlas did not retain the first scene below the current frame: {retainedAtlas}.");
        Check(transformedCurrent.Red > 180 && transformedCurrent.Green < 80,
            $"Current live frame did not remain independently red: {transformedCurrent}.");
    }

    public static void TestStableGameplayFramesRefineAtlasButMenuDoesNot()
    {
        var pipeline = new LiveAtlasPresentationPipeline();
        var first = Solid(40, 30, red: 255);
        var current = Solid(40, 30, green: 255);
        var menu = Solid(40, 30, blue: 255);

        Check(pipeline.SubmitAtlasObservation(
                first, new(0, 0), 40, 1, hasGameplaySignal: true),
            "Initial gameplay frame was rejected.");
        foreach (var timestamp in new[] { 2d, 2.1, 2.2 })
        {
            Check(pipeline.SubmitAtlasObservation(
                    current, new(0, 0), 40, timestamp,
                    hasGameplaySignal: true),
                $"Refining gameplay frame at {timestamp} was rejected.");
        }
        Check(!pipeline.SubmitAtlasObservation(
                menu, new(0, 0), 40, 3, hasGameplaySignal: false),
            "Menu evidence entered the gameplay atlas.");

        var output = pipeline.Process(menu, new(0, 0), 40);
        var atlas = output.Atlas
            ?? throw new InvalidOperationException("Refined atlas was missing.");
        var pixel = Pixel(atlas, 10, 10);
        Check(pixel.Green > 180 && pixel.Red < 80 && pixel.Blue < 80,
            $"Stable gameplay did not replace the provisional scene, or menu blue leaked: {pixel}.");
        var presented = Pixel(output.LiveImage, 10, 10);
        Check(presented.Blue > 180 && presented.Red < 80 && presented.Green < 80,
            $"The menu was not independently presented over the retained gameplay atlas: {presented}.");
        Check(!ReferenceEquals(output.LiveImage, menu),
            "Menu presentation reused the mutable input image.");
    }

    public static void TestMenuPreviewCannotAnchorFirstGameplayAtlasPlacement()
    {
        var pipeline = new LiveAtlasPresentationPipeline();
        var menu = Solid(40, 30, blue: 255);
        var gameplay = Solid(40, 30, green: 255);

        var preview = pipeline.Process(menu, new(300, 0), 40);
        Check(preview.Atlas is null,
            "A menu preview created or anchored an atlas.");
        Check(pipeline.SubmitAtlasObservation(
                gameplay, new(0, 0), 40, 2, hasGameplaySignal: true),
            "The first gameplay placement was rejected.");
        var output = pipeline.Process(gameplay, new(0, 0), 40);
        var atlas = output.Atlas
            ?? throw new InvalidOperationException("First gameplay atlas was missing.");
        EqualRect(new(0, 0, 40, 30), output.LiveBounds,
            "first gameplay live bounds");
        EqualRect(new(0, 0, LiveTiledAtlas.TileSize, LiveTiledAtlas.TileSize),
            atlas.TotalBounds, "first gameplay atlas bounds");
        Check(Pixel(atlas, 10, 10).Green > 180,
            "The first gameplay frame was not retained at the origin.");
    }

    public static void TestAutoSaveAgeUsesAtlasCreationRatherThanLatestManifestWrite()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"hkv-atlas-age-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var createdAt = new DateTimeOffset(
                2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
            var displayNow = createdAt.AddHours(1);
            var metadataPath = Path.Combine(root, "atlas.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
            {
                RegistrationCount = 0,
                CreatedAt = createdAt,
                TileSize = 64,
                PixelsPerWorldUnit = 1d
            }));

            var policy = new LiveAtlasAutoSavePolicy(() => displayNow);
            var beforeMetadata = VisionResetController.ReadPersistedAtlas(root).Metadata;
            var before = policy.Summarize(
                beforeMetadata.RegistrationCount,
                beforeMetadata.CreatedAt);
            File.SetLastWriteTimeUtc(
                metadataPath, displayNow.AddSeconds(-1).UtcDateTime);
            var afterMetadata = VisionResetController.ReadPersistedAtlas(root).Metadata;
            var after = policy.Summarize(
                afterMetadata.RegistrationCount,
                afterMetadata.CreatedAt);

            Check(after.CreatedAt == before.CreatedAt,
                "A manifest write changed the atlas creation timestamp.");
            Check(after.CreatedAt == createdAt,
                "The persisted atlas creation timestamp did not round-trip.");
            Check(after.Age.TotalSeconds > 3_500,
                "Auto Save age followed the latest manifest write.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void TestTemporalAgreementFullResolutionFiveRunBenchmark()
    {
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        var image = Solid(640, 360, red: 32, green: 48, blue: 64);
        var timings = new List<double>();
        for (var id = 0; id < 7; id++)
        {
            Check(atlas.Insert(
                    id,
                    image,
                    640,
                    new(0, 0),
                    timestamp: id / 7.5),
                $"Full-resolution TemporalAgreement sample {id} was rejected.");
            if (id >= 2)
                timings.Add(atlas.TemporalDiagnostics.LastFilterMilliseconds);
        }

        Check(timings.Count == 5,
            "TemporalAgreement benchmark did not measure exactly five runs.");
        Check(timings.Max() < 133,
            "TemporalAgreement exceeded the 7.5 fps atlas interval: "
            + string.Join(", ", timings));
        Check(atlas.TemporalDiagnostics.RetainedDecodedBytes
              <= 7L * 640 * 360 * BgraFrame.BytesPerPixel,
            "TemporalAgreement exceeded its seven-frame decoded-byte bound.");
        Console.WriteLine(
            "TEMPORAL_AGREEMENT_640x360_MS runs=5 ms="
            + string.Join(",", timings.Select(value => value.ToString("F3"))));
    }

    public static void TestTemporalAgreementAgainstSavedFullResolutionEvidence()
    {
        TestReadOnlySceneSessionEvidenceDoesNotMutateTree();

        if (!string.Equals(
                Environment.GetEnvironmentVariable("HKV_RUN_SAVED_ATLAS_EVALUATION"),
                "1",
                StringComparison.Ordinal))
        {
            Console.WriteLine(
                "SKIP LiveTiledAtlasTests.testTemporalAgreementAgainstSavedFullResolutionEvidence: "
                + "saved full-resolution atlas evaluation is opt-in.");
            return;
        }

        var rootPath = Environment.GetEnvironmentVariable("HKV_SAVED_WORLD_ROOT");
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException(
                "HKV_SAVED_WORLD_ROOT must name an existing saved-world session when "
                + "HKV_RUN_SAVED_ATLAS_EVALUATION=1.");

        using var store = SceneSessionStore.OpenReadOnly(rootPath);
        var observations = store.Manifest.Observations;
        IReadOnlyList<StoredFrameObservation> selected = [];
        for (var end = observations.Count - 1; end >= 0; end--)
        {
            var newest = observations[end];
            var candidate = new List<StoredFrameObservation>(7);
            var index = end;
            while (candidate.Count < 7)
            {
                var observation = observations[index];
                if (observation.RoomId == newest.RoomId
                    && observation.VisitId == newest.VisitId
                    && newest.Timestamp - observation.Timestamp <= 1)
                {
                    candidate.Add(observation);
                }
                if (index == 0) break;
                index--;
            }
            if (candidate.Count == 7)
            {
                candidate.Reverse();
                selected = candidate;
                break;
            }
        }

        if (selected.Count != 7)
        {
            Console.WriteLine(
                "SKIP LiveTiledAtlasTests.testTemporalAgreementAgainstSavedFullResolutionEvidence: "
                + "no seven-frame, one-second saved evidence window.");
            return;
        }

        var brighter = new LiveTiledAtlas(
            anchorPosition: selected[0].CameraPosition,
            pixelPolicy: LiveTiledAtlasPixelPolicy.PreserveBrighter);
        var temporal = new LiveTiledAtlas(
            anchorPosition: selected[0].CameraPosition,
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);
        foreach (var observation in selected)
        {
            var image = store.Source(observation);
            Check(brighter.Insert(
                    observation.Id,
                    image,
                    observation.SolveWidth,
                    observation.CameraPosition),
                $"PreserveBrighter rejected saved observation {observation.Id}.");
            Check(temporal.Insert(
                    observation.Id,
                    image,
                    observation.SolveWidth,
                    observation.CameraPosition,
                    captureIdentity: observation.Id,
                    timestamp: observation.Timestamp),
                $"TemporalAgreement rejected saved observation {observation.Id}.");
        }

        var brighterTiles = brighter.Tiles.ToDictionary(tile => tile.Id);
        var comparedPixels = 0;
        var suppressedBrightOutliers = 0;
        var acceptedDarkerCorrections = 0;
        foreach (var temporalTile in temporal.Tiles)
        {
            if (!brighterTiles.TryGetValue(temporalTile.Id, out var brighterTile))
                continue;
            var temporalImage = temporalTile.Image;
            var brighterImage = brighterTile.Image;
            Check(temporalImage.Width == brighterImage.Width
                  && temporalImage.Height == brighterImage.Height,
                $"Matching atlas tile {temporalTile.Id} has mismatched dimensions.");
            for (var y = 0; y < temporalImage.Height; y++)
            {
                for (var x = 0; x < temporalImage.Width; x++)
                {
                    var temporalOffset = checked(
                        y * temporalImage.Stride + x * BgraFrame.BytesPerPixel);
                    var brighterOffset = checked(
                        y * brighterImage.Stride + x * BgraFrame.BytesPerPixel);
                    if (temporalImage.Pixels[temporalOffset + 3] == 0
                        || brighterImage.Pixels[brighterOffset + 3] == 0)
                    {
                        continue;
                    }

                    comparedPixels++;
                    var temporalPeak = Math.Max(
                        temporalImage.Pixels[temporalOffset],
                        Math.Max(
                            temporalImage.Pixels[temporalOffset + 1],
                            temporalImage.Pixels[temporalOffset + 2]));
                    var brighterPeak = Math.Max(
                        brighterImage.Pixels[brighterOffset],
                        Math.Max(
                            brighterImage.Pixels[brighterOffset + 1],
                            brighterImage.Pixels[brighterOffset + 2]));
                    if (brighterPeak - temporalPeak > 24)
                        suppressedBrightOutliers++;
                    if (temporalPeak < brighterPeak)
                        acceptedDarkerCorrections++;
                }
            }
        }

        Check(comparedPixels > 0,
            "Saved-evidence atlas policies had no overlapping opaque pixels.");
        Check(suppressedBrightOutliers > 0,
            "TemporalAgreement suppressed no saved bright outliers above 24.");
        Check(acceptedDarkerCorrections > 0,
            "TemporalAgreement accepted no darker saved-evidence corrections.");
        Console.WriteLine(
            $"SAVED_EVIDENCE_COMPARISON frames=7 pixels={comparedPixels} "
            + $"suppressedBright={suppressedBrightOutliers} "
            + $"darkerCorrections={acceptedDarkerCorrections}");
    }

    public static void TestReadOnlySceneSessionEvidenceDoesNotMutateTree()
    {
        var parent = Path.Combine(
            Path.GetTempPath(), $"hkv-read-only-evidence-{Guid.NewGuid():N}");
        var root = Path.Combine(parent, "session");
        var frames = Path.Combine(root, "frames");
        Directory.CreateDirectory(frames);
        try
        {
            var observation = new StoredFrameObservation(
                7,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                100.25,
                "frames/00000007.png",
                new AtlasPoint(12, -5),
                640,
                [],
                2);
            var manifest = new SessionManifest(
                SessionManifest.CurrentSchemaVersion,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                [observation],
                8);
            var manifestPath = Path.Combine(root, "manifest.json");
            var sourcePath = Path.Combine(frames, "00000007.png");
            File.WriteAllBytes(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
            PngWriter.Write(
                Solid(2, 1, red: 11, green: 22, blue: 33, alpha: 44),
                sourcePath);
            File.SetLastWriteTimeUtc(
                manifestPath,
                new DateTime(2026, 10, 20, 1, 2, 3, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                sourcePath,
                new DateTime(2026, 10, 20, 1, 2, 4, DateTimeKind.Utc));

            var beforeFiles = SnapshotEvidenceTree(root);
            var beforeDirectories = SnapshotEvidenceDirectories(root);
            using (var evidence = SceneSessionStore.OpenReadOnly(root))
            {
                var loadedObservation = evidence.Manifest.Observations.Single();
                Check(evidence.Manifest.SessionId == manifest.SessionId
                      && evidence.Manifest.NextObservationId == 8
                      && loadedObservation.Id == observation.Id
                      && loadedObservation.RoomId == observation.RoomId
                      && loadedObservation.VisitId == observation.VisitId
                      && loadedObservation.Timestamp == observation.Timestamp
                      && loadedObservation.ImagePath == observation.ImagePath
                      && loadedObservation.CameraPosition == observation.CameraPosition
                      && loadedObservation.SolveWidth == observation.SolveWidth
                      && loadedObservation.ExcludedRects.Count == 0
                      && loadedObservation.PoseRevision == observation.PoseRevision,
                    "Read-only scene evidence did not load the existing manifest.");
                var source = evidence.Source(loadedObservation);
                Check(source.Width == 2
                      && source.Height == 1
                      && Pixel(source, 0, 0) == (11, 22, 33, 44),
                    "Read-only scene evidence did not decode the stored PNG exactly.");
            }

            AssertEvidenceTreeEqual(
                beforeFiles,
                SnapshotEvidenceTree(root));
            Check(beforeDirectories.SequenceEqual(SnapshotEvidenceDirectories(root)),
                "Read-only scene evidence changed the directory tree.");
            Check(!File.Exists(Path.Combine(root, "manifest.json.lock"))
                  && !File.Exists(Path.Combine(root, "scene.snapshot.lock")),
                "Read-only scene evidence created a lock file.");
            Check(beforeDirectories.SequenceEqual(["frames"]),
                "The deterministic evidence fixture has unexpected directories.");

            var missingRoot = Path.Combine(parent, "missing");
            AssertThrows<DirectoryNotFoundException>(
                () => SceneSessionStore.OpenReadOnly(missingRoot).Dispose(),
                "Read-only scene evidence did not reject a missing root clearly.");
            Check(!Directory.Exists(missingRoot),
                "Rejecting a missing read-only root created it.");

            var invalidRoot = Path.Combine(parent, "invalid");
            Directory.CreateDirectory(invalidRoot);
            AssertThrows<FileNotFoundException>(
                () => SceneSessionStore.OpenReadOnly(invalidRoot).Dispose(),
                "Read-only scene evidence did not reject a missing manifest clearly.");
            Check(!Directory.EnumerateFileSystemEntries(invalidRoot).Any(),
                "Rejecting an invalid read-only root created evidence or metadata.");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static GroundTruthAtlasMosaic Mosaic() => new(
        pixelsPerWorldUnit: 1,
        tileSize: 64,
        sourceTopInsetFraction: 0,
        pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalAgreement);

    private static void AssertInjectedCommitFailure(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Injected temporal commit failure did not throw.");
        }
        catch (ApplicationException exception) when (
            exception.Message == "Injected temporal commit failure.")
        {
        }
    }

    private static void AssertGroundTruthStateUnchanged(
        GroundTruthAtlasMosaic atlas,
        IReadOnlyList<AtlasTileSnapshot> expectedSnapshot,
        (byte Red, byte Green, byte Blue, byte Alpha, ushort Observations) expectedPixel,
        long expectedRevision,
        long expectedAccepted,
        int expectedTileCount,
        int expectedRetainedCount,
        long expectedRetainedBytes,
        long expectedCapacityBytes,
        int expectedOutstandingBuffers,
        string label)
    {
        Check(atlas.Revision == expectedRevision
              && atlas.AcceptedFrameCount == expectedAccepted
              && atlas.TileCount == expectedTileCount,
            $"{label} changed revision, accepted count, or tile count.");
        Check(atlas.RetainedObservationCount == expectedRetainedCount
              && atlas.RetainedObservationBytes == expectedRetainedBytes
              && atlas.RetainedObservationCapacityBytes == expectedCapacityBytes
              && atlas.OutstandingObservationBufferCount == expectedOutstandingBuffers,
            $"{label} changed temporal history or pooled ownership.");
        Check(GroundPixel(atlas, 0, 0) == expectedPixel,
            $"{label} changed committed pixel bytes or weights.");
        var actualSnapshot = atlas.Snapshot();
        Check(actualSnapshot.Count == expectedSnapshot.Count,
            $"{label} changed snapshot tile count.");
        for (var index = 0; index < expectedSnapshot.Count; index++)
        {
            var expected = expectedSnapshot[index];
            var actual = actualSnapshot[index];
            Check(actual.Coordinate == expected.Coordinate
                  && actual.WrittenPixelCount == expected.WrittenPixelCount
                  && actual.ObservationCount == expected.ObservationCount
                  && actual.Frame.Pixels.SequenceEqual(expected.Frame.Pixels),
                $"{label} changed snapshot tile {expected.Coordinate}.");
        }
    }

    private static BgraFrame SparseColumns(
        int width,
        int height,
        params (int X, byte Red, byte Green, byte Blue, byte Alpha)[] columns)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        foreach (var column in columns)
        {
            for (var y = 0; y < height; y++)
            {
                var offset = checked((y * width + column.X) * BgraFrame.BytesPerPixel);
                pixels[offset] = column.Blue;
                pixels[offset + 1] = column.Green;
                pixels[offset + 2] = column.Red;
                pixels[offset + 3] = column.Alpha;
            }
        }
        return new(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static (
        byte Red,
        byte Green,
        byte Blue,
        byte Alpha,
        ushort Observations) GroundPixel(
            GroundTruthAtlasMosaic atlas,
            int globalX,
            int globalY)
    {
        Check(atlas.TryReadPixel(
                globalX,
                globalY,
                out var blue,
                out var green,
                out var red,
                out var observations),
            $"Ground-truth atlas pixel ({globalX}, {globalY}) was not written.");
        var tileX = FloorDivide(globalX, atlas.TileSize);
        var tileY = FloorDivide(globalY, atlas.TileSize);
        var tile = atlas.Snapshot().Single(item =>
            item.Coordinate == new AtlasTileCoordinate(tileX, tileY));
        var localX = globalX - tileX * atlas.TileSize;
        var localY = globalY - tileY * atlas.TileSize;
        var offset = checked(
            localY * tile.Frame.Stride + localX * BgraFrame.BytesPerPixel);
        return (red, green, blue, tile.Frame.Pixels[offset + 3], observations);
    }

    private static BgraFrame Solid(
        int width,
        int height,
        byte red = 0,
        byte green = 0,
        byte blue = 0,
        byte alpha = 255)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        return new(
            width,
            height,
            checked(width * BgraFrame.BytesPerPixel),
            pixels,
            0,
            DateTimeOffset.UnixEpoch);
    }

    private static ReceiverGroundTruthSample Telemetry(
        BgraFrame frame,
        double timestamp,
        double cameraX = 0,
        double cameraY = 0) => new(
        2,
        "groundTruth",
        Guid.Empty,
        checked((ulong)Math.Round(timestamp * 10)),
        checked((long)Math.Round(timestamp * 10)),
        timestamp,
        "Tutorial_01",
        true,
        0,
        0,
        0,
        0,
        0,
        true,
        true,
        true,
        cameraX,
        cameraY,
        -38,
        cameraX,
        cameraY,
        0,
        frame.Height / 2d,
        1,
        1,
        null,
        null,
        frame.Width,
        frame.Height,
        frame.Width,
        frame.Height);

    private static (byte Red, byte Green, byte Blue, byte Alpha) Pixel(
        LiveTiledAtlasSnapshot atlas,
        int x,
        int y)
    {
        var tileX = FloorDivide(x, LiveTiledAtlas.TileSize);
        var tileY = FloorDivide(y, LiveTiledAtlas.TileSize);
        var tile = atlas.Tiles.Single(value =>
            value.TileX == tileX && value.TileY == tileY);
        var localX = x - tileX * LiveTiledAtlas.TileSize;
        var localY = y - tileY * LiveTiledAtlas.TileSize;
        var topY = LiveTiledAtlas.TileSize - 1 - localY;
        var offset = checked(
            topY * tile.Image.Stride + localX * BgraFrame.BytesPerPixel);
        return (
            tile.Image.Pixels[offset + 2],
            tile.Image.Pixels[offset + 1],
            tile.Image.Pixels[offset],
            tile.Image.Pixels[offset + 3]);
    }

    private static (byte Red, byte Green, byte Blue, byte Alpha) Pixel(
        BgraFrame frame,
        int x,
        int y)
    {
        var offset = checked(y * frame.Stride + x * BgraFrame.BytesPerPixel);
        return (
            frame.Pixels[offset + 2],
            frame.Pixels[offset + 1],
            frame.Pixels[offset],
            frame.Pixels[offset + 3]);
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static IReadOnlyDictionary<
        string,
        (long Length, DateTime LastWriteTimeUtc, string Sha256)> SnapshotEvidenceTree(
            string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
        .ToDictionary(
            path => Path.GetRelativePath(root, path),
            path =>
            {
                var info = new FileInfo(path);
                return (
                    info.Length,
                    info.LastWriteTimeUtc,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            },
            StringComparer.Ordinal);

    private static string[] SnapshotEvidenceDirectories(string root) => Directory
        .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static void AssertEvidenceTreeEqual(
        IReadOnlyDictionary<
            string,
            (long Length, DateTime LastWriteTimeUtc, string Sha256)> expected,
        IReadOnlyDictionary<
            string,
            (long Length, DateTime LastWriteTimeUtc, string Sha256)> actual)
    {
        Check(actual.Count == expected.Count,
            "Read-only scene evidence changed the relative file set.");
        foreach (var pair in expected)
        {
            Check(actual.TryGetValue(pair.Key, out var value),
                $"Read-only scene evidence removed {pair.Key}.");
            Check(value == pair.Value,
                $"Read-only scene evidence changed length, timestamp, or hash for {pair.Key}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void EqualRect(
        StencilRect expected,
        StencilRect actual,
        string label) => Check(
            expected == actual,
            $"Expected {label} {expected}; received {actual}.");

    private static void Close(
        double expected,
        double actual,
        double tolerance,
        string label) => Check(
            Math.Abs(expected - actual) <= tolerance,
            $"Expected {label} {expected}; received {actual}.");

    private static void Check(bool value, string message = "Assertion failed.")
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

