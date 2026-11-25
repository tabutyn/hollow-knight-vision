using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class BotCaptureTests
{
    private const double Start = 30;
    private const long Generation = 12;
    private static readonly Guid BlueEpoch = Guid.Parse("8f76cac5-bf61-40d9-9aeb-a47dcad636ca");
    private static readonly Guid EnemyEpoch = Guid.Parse("bbc5a743-fd5d-4abe-9bfc-db806add7d44");

    internal static void TestExactHackerSourcesStaySeparateFromVisualSources()
    {
        var input = Input(0);
        var source = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(ReceiverWireCodec.EncodeLine(input.Mod));
        Check(source.Actors?.GeoCaches?.Single().Id == 3 && source.Actors.EnemyOutcomes?.Epoch == EnemyEpoch
            && source.BlueHealthOutcomes?.Epoch == BlueEpoch && source.AtlasGameplayActive == true,
            "The Windows receiver must preserve the frame's actor and outcome payload.");
        var hacker = Hacker(input, source)!;
        var gameplay = Visual(input);
        Check(hacker.Issues.Count == 0 && gameplay.Issues.Count == 0, "Complete dual-domain evidence should be usable.");
        Check(hacker.DetectedTerrain is null && hacker.AuthoredTerrain is { Floors.Count: 1, Walls.Count: 1, Doors.Count: 1, Spikes.Count: 1 }
            && hacker.AuthoredTerrain.Walls[0].SolidSide == 0 && hacker.GeoCaches?.Single().X == .625
            && hacker.LifebloodCocoons?.Count == 1, "Hacker must use authored markings and the mod's actor boxes.");
        Check(gameplay.AuthoredTerrain is null && gameplay.DetectedTerrain is { Floors.Count: 1, Walls.Count: 1 }
            && gameplay.DetectedTerrain.Walls[0].SolidSide == 1 && gameplay.GeoCaches?.Count == 0
            && Math.Abs(gameplay.Features[0] - .45) < 1e-9 && gameplay.Features[144] == 1,
            "Game Play must use pixel detections and terrain in bottom-left pixels.");
        Check(hacker.Features[0] == .5 && hacker.Features[96] > 0 && gameplay.Features[96] < 0,
            "Each observation must use its own Knight and enemy sources.");
        Check(Hacker(input, source with { UnityFrame = 8 }) is null && Hacker(input, source with { AtlasGameplayActive = false }) is null,
            "Stale rendered-frame markers and menu samples must not enter the Hacker clip.");
        var bad = source with { Actors = source.Actors! with { Enemies = [source.Actors.Enemies![0], source.Actors.Enemies[0]] } };
        Check(Hacker(input, bad) is { CanTrain: false }, "Duplicate mod identities must not produce valid observations.");
    }

    internal static void TestVisualPortalProjectionReachesGameplayPolicyOnlyInItsRoom()
    {
        var placement = LiveWorldPlacementProjection.Empty with
        {
            RoomDoorways = new Dictionary<int, IReadOnlyList<LiveWorldDoorwayStroke>>
            {
                [1] = [new(new(110, 60), new(110, 100))],
                [2] = [new(new(115, 70), new(115, 110))]
            }
        };
        var doors = BotObservationBuilder.ProjectVisualDoors(placement, 1, new(100, 50), 120, new(240, 140));
        Check(doors.SequenceEqual([new(20, 20, 20, 100)]), "Project only the current room and apply the solve-to-image scale.");
        Check(BotObservationBuilder.ProjectVisualDoors(placement, 2, new(100, 85), 120, new(240, 40))
            .SequenceEqual([new(30, 0, 30, 40)]), "The other side owns its own opening and clips to the current frame.");
        Check(BotObservationBuilder.ProjectVisualDoors(placement, 3, new(100, 50), 120, new(240, 140)).Count == 0
            && BotObservationBuilder.ProjectVisualDoors(placement, 1, new(120, 50), 120, new(240, 140)).Count == 0
            && BotObservationBuilder.ProjectVisualDoors(placement, 1, new(double.NaN, 50), 120, new(240, 140)).Count == 0,
            "A different room, an offscreen door or an invalid camera must supply no markings.");

        var input = Input(0);
        var scaled = BotObservationBuilder.ProjectVisualDoors(placement, 1, new(100, 50), 80,
            new(input.Frame.Width, input.Frame.Height));
        var gameplay = Visual(input, scaled);
        Check(gameplay.DetectedTerrain?.Doors?.SequenceEqual([new(10, 10, 10, 48)]) == true
            && gameplay.AuthoredTerrain is null, "The real observation builder must emit clipped visual terrain.");
        var expected = BotPolicyGeometry.Raster(gameplay.DetectedTerrain, doors: true).Select(value => (float)value);
        var legacy = new BotObservationHistory(11).Append(gameplay, default, input.Time.AvailableAt);
        Check(legacy is not null && legacy.TakeLast(BotPolicyGeometry.Cells).SequenceEqual(expected)
            && legacy.TakeLast(BotPolicyGeometry.Cells).Any(value => value > 0),
            "The detected portal must reach the legacy actor's door channel.");
        var compact = BotPolicyContract.LandingVersion;
        var vector = new BotObservationHistory(compact).Append(gameplay, default, input.Time.AvailableAt);
        var markings = BotPolicyGeometry.Markings(gameplay.DetectedTerrain, gameplay.Features[0], gameplay.Features[1], compact);
        var fields = BotPolicyContract.Names(compact)!.ToList();
        var markingOffset = fields.IndexOf("marking_ground_known");
        Check(vector is not null && vector.Skip(vector.Length - fields.Count + markingOffset)
            .Take(markings.Length).SequenceEqual(markings.Select(value => (float)value)) && markings[7] > 0,
            "The current bot observation must include the door as a compact marking.");
        var otherDomain = new BotObservationHistory(11).Append(gameplay with { TerrainSource = "human-authored" },
            default, input.Time.AvailableAt);
        Check(otherDomain is not null && otherDomain.TakeLast(BotPolicyGeometry.Cells).All(value => value == 0),
            "The Hacker policy reads authored terrain independently of the visual door.");
        Check(Visual(input, []).DetectedTerrain?.Doors is null, "Unknown doors retain the legacy missing-source representation.");
    }

    internal static void TestFreshTerrainMakesOccludedKnightUsable()
    {
        var input = Input(0);
        var hidden = Visual(input with { Detections = input.Detections with { Detections = [] } });
        Check(hidden.Issues.SequenceEqual(new[] { "missing-knight" }) && hidden.CanTrain,
            "A missed Knight with fresh detections and visible terrain should retain the motion evidence.");
        var stale = Visual(input with { Detections = input.Detections with { SourceTimestamp = input.Detections.SourceTimestamp - .21 } });
        Check(!stale.CanTrain && stale.Issues.Contains("stale-or-missing-detection"), "Stale detector output must remain ineligible.");
    }

    internal static void TestPairedEvidencePublishesOnceAndHazardClipsAreDiscarded()
    {
        WithLibrary(root =>
        {
            var result = WriteFixture(root);
            Check(result.Published.Count == 2 && result.Discarded == 0, "Both valid domains should publish.");
            var clips = BotCaptureSession.Library(root);
            Check(clips.Count == 2 && clips[0].PairedClipId == clips[1].Id && clips[1].PairedClipId == clips[0].Id
                && clips[0].ReplayId == clips[1].ReplayId && clips.All(c => c.ObservationCount == 6 && c.InputEventCount == 6),
                "A single run should publish two reciprocal manifests with the exact same input edges.");
            var pending = Directory.GetDirectories(root, ".pending-demo-*");
            Check(pending.Length == 0 && clips.All(clip => File.Exists(Path.Combine(root,
                BotCaptureSession.DirectoryName(clip.Id), "frames", "0.png"))), "Saved PNG evidence should be readable immediately.");

            var input = Input(0);
            var insufficient = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            var invalid = Visual(input with { Detections = input.Detections with { CaptureGeneration = -1 } });
            Check(insufficient.TryRecord(input.Frame, invalid, null), "The recorder should log the hazard during capture.");
            var failure = insufficient.FinishAsync(Start + 1, null, completed: true).GetAwaiter().GetResult();
            Check(failure.Published.Count == 0 && failure.Discarded == 1 && BotCaptureSession.Library(root).Count == 2,
                "A clip with no eligible observations should disappear without entering Bot Summary.");
        });
    }

    internal static void TestNativeControlsAndInterruptedCapture()
    {
        WithLibrary(root =>
        {
            var input = Input(0);
            var active = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            active.ObserveNativeInput(InputButtons.Right | InputButtons.ActionX, Start);
            active.ObserveNativeInput(InputButtons.Right, Start + .18);
            Check(active.TryRecord(input.Frame, Visual(input), null), "A visual-only clip should be accepted.");
            Check(!active.TryRecord(input.Frame, Visual(input), null), "Duplicate capture frames should not be appended.");
            Check(!active.TryRecord(input.Frame, Visual(input) with { Generation = Generation + 1 }, null),
                "Previous capture work must not cross a live generation.");
            var result = active.FinishAsync(Start + 1, null, completed: true).GetAwaiter().GetResult();
            var clip = result.Published.Single();
            Check(clip is { InputEventCount: 3, BotDomain: "gameplay", ReplayId: null, ObservationCount: 1 },
                "Native held keys should be stored as explicit input edges.");
            var interrupted = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            interrupted.TryRecord(input.Frame, Visual(input), null);
            Check(interrupted.FinishAsync(Start + 1, null, completed: false).GetAwaiter().GetResult().Discarded == 1,
                "Interrupted captures should be discarded.");
        });
    }

    internal static void TestReviewUsesTheSavedDomainAndSafeLocalImage()
    {
        WithLibrary(root =>
        {
            var pair = WriteFixture(root);
            foreach (var clip in pair.Published)
            {
                var folder = Path.Combine(root, BotCaptureSession.DirectoryName(clip.Id));
                var frames = BotCaptureSession.Review(folder);
                var frame = frames.Single();
                Check(frame.Authored == (clip.BotDomain == "hacker") && frame.Features.Count == BotRecordingContract.FeatureNames.Count
                    && File.Exists(frame.ImageFile) && frame.Offset > 0,
                    "Review should use the image and source domain stored in its own clip.");
                Check(frame.Terrain?.Walls.Single().SolidSide == (frame.Authored ? 0 : 1),
                    "Review must show the saved wall source for its domain.");
                File.AppendAllText(Path.Combine(folder, "events.jsonl"), """{"imagePath":"../outside.png"}""" + "\n");
                Check(BotCaptureSession.Review(folder).Count == 1, "Review should only open paths inside the capture.");
            }
        });
    }

    internal static void TestDeletionKeepsSharedReplayUntilLastClipInEitherOrder()
    {
        foreach (var firstDomain in new[] { "hacker", "gameplay" })
        {
            WithLibrary(root =>
            {
                var pair = WriteFixture(root).Published;
                var first = pair.Single(clip => clip.BotDomain == firstDomain);
                var last = pair.Single(clip => clip.Id != first.Id);
                var paths = Path.Combine(root, "paths");
                var replay = first.ReplayId!.Value;
                var archive = new VisualReplayArchiveWriter(paths, replay, new Dictionary<string, string>());
                archive.Append(Input(0).Frame, Start + .01, Start + .01, (ulong)Generation, 80, null);
                _ = archive.FinishAsync().GetAwaiter().GetResult();
                var linked = Path.Combine(paths, $"path-fixture-{replay:D}.json");
                File.WriteAllText(linked, "input");
                var unrelated = Path.Combine(paths, $"path-fixture-{Guid.NewGuid():D}.json");
                File.WriteAllText(unrelated, "keep");
                var model = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
                File.WriteAllText(Path.Combine(model, "snapshot.json"), "keep");

                BotClipDeletion.Delete(root, paths, first);
                var survivor = Path.Combine(root, BotCaptureSession.DirectoryName(last.Id));
                Check(BotCaptureSession.Library(root).Single().Id == last.Id && File.Exists(linked)
                    && BotClipTimeline.Open(survivor, paths).ReadSourceFrame(0).Width == Input(0).Frame.Width,
                    "The surviving domain must keep both original input and playable frames.");

                if (OperatingSystem.IsWindows())
                {
                    using var locked = File.Open(linked, FileMode.Open, FileAccess.Read, FileShare.Read);
                    try { BotClipDeletion.Delete(root, paths, last); throw new InvalidOperationException("Locked input path was deleted."); }
                    catch (IOException) { }
                    Check(Directory.Exists(survivor) && File.Exists(linked) && Directory.Exists(Path.Combine(paths, $"frames-{replay:D}")),
                        "A denied move must restore the clip and the replay.");
                }

                BotClipDeletion.Delete(root, paths, last);
                Check(BotCaptureSession.Library(root).Count == 0 && !File.Exists(linked)
                    && !Directory.Exists(Path.Combine(paths, $"frames-{replay:D}"))
                    && File.ReadAllText(unrelated) == "keep" && File.ReadAllText(Path.Combine(model, "snapshot.json")) == "keep",
                    "Remove the clip and its last shared replay, preserving other runs and trained models.");
            });
        }

        WithLibrary(root =>
        {
            var pair = WriteFixture(root).Published;
            var paths = Directory.CreateDirectory(Path.Combine(root, "paths")).FullName;
            var linked = Path.Combine(paths, $"path-fixture-{pair[0].ReplayId:D}.json");
            File.WriteAllText(linked, "input");
            using var ready = new Barrier(2);
            var removals = pair.Select(clip => Task.Run(() =>
            {
                if (!ready.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Concurrent removals did not start.");
                BotClipDeletion.Delete(root, paths, clip);
            })).ToArray();
            Task.WaitAll(removals);
            Check(BotCaptureSession.Library(root).Count == 0 && !File.Exists(linked),
                "Concurrent removals must still clean up the final shared replay.");
        });
    }

    internal static void TestDeletionRejectsChangedClipsAndRemovesEligibleSingleDomain()
    {
        WithLibrary(root =>
        {
            var clip = WriteFixture(root, observeHacker: false).Published.Single();
            Check(clip.ReplayId is { } replayId && replayId != clip.Id && clip.PairedClipId is null,
                "The fixture must use a discarded Hacker side and a surviving Game Play clip.");
            var folder = Path.Combine(root, BotCaptureSession.DirectoryName(clip.Id));
            var paths = Directory.CreateDirectory(Path.Combine(root, "paths")).FullName;
            var linked = Path.Combine(paths, $"path-fixture-{clip.ReplayId:D}.json");
            File.WriteAllText(linked, "input");
            var manifest = Path.Combine(folder, "manifest.json");
            var original = File.ReadAllText(manifest);
            Reject(clip with { CreatedAt = clip.CreatedAt.AddSeconds(1) });
            var recording = JsonNode.Parse(original)!;
            recording["status"] = "recording";
            File.WriteAllText(manifest, recording.ToJsonString());
            Reject(clip);
            File.WriteAllText(manifest, original);
            var invalidFrames = Path.Combine(paths, $"frames-{clip.ReplayId:D}");
            File.WriteAllText(invalidFrames, "wrong file type");
            Reject(clip);
            File.Delete(invalidFrames);
            if (!OperatingSystem.IsWindows())
            {
                var other = Directory.CreateDirectory(Path.Combine(root, "outside-replay")).FullName;
                Directory.CreateSymbolicLink(invalidFrames, other);
                Reject(clip);
                Directory.Delete(invalidFrames);
                Check(Directory.Exists(other), "Rejecting a linked archive must preserve its target.");
            }
            BotClipDeletion.Delete(root, paths, clip);
            Check(!Directory.Exists(folder) && Directory.GetFileSystemEntries(paths).Length == 0,
                "A modded run with only a Game Play clip still owns its original replay.");

            var ordinary = new BotCaptureSession(root, "normal", "visual", Generation, Start, Labels());
            var sample = Input(0);
            Check(ordinary.TryRecord(sample.Frame, Visual(sample), null), "The legacy recording needs a valid frame.");
            var legacy = ordinary.FinishAsync(Start + 1, null, completed: true).GetAwaiter().GetResult().Published.Single();
            var legacyPath = Path.Combine(paths, $"path-legacy-{legacy.Id:D}.json");
            var legacyFrames = Directory.CreateDirectory(Path.Combine(paths, $"frames-{legacy.Id:D}")).FullName;
            File.WriteAllText(legacyPath, "input");
            BotClipDeletion.Delete(root, paths, legacy);
            Check(!File.Exists(legacyPath) && !Directory.Exists(legacyFrames) && BotCaptureSession.Library(root).Count == 0,
                "Legacy recordings without an explicit replay ID use their clip ID to clean up owned evidence.");

            void Reject(BotCaptureManifest selected)
            {
                try { BotClipDeletion.Delete(root, paths, selected); throw new InvalidOperationException("Unowned or active evidence was deleted."); }
                catch (InvalidDataException) { }
                Check(Directory.Exists(folder) && File.Exists(linked), "Validation must finish before changing any owned entry.");
            }
        });
    }

    internal static void TestClipTimelineUsesExactSourceRowsAndFallsBackToStills()
    {
        WithLibrary(root =>
        {
            var paired = WriteFixture(root).Published;
            var replay = paired[0].ReplayId!.Value;
            var paths = Path.Combine(root, "paths");
            var writer = new VisualReplayArchiveWriter(paths, replay, new Dictionary<string, string>());
            var image = Input(0).Frame;
            Write(-.01, Generation);
            Write(.01, Generation);
            Write(.15, Generation);
            Write(.16, Generation + 1);
            Write(.21, Generation, solveWidth: 0);
            Write(.88, Generation);
            Write(1, Generation);
            _ = writer.FinishAsync(incompleteReason: "fixture has a failed frame").GetAwaiter().GetResult();

            foreach (var clip in paired)
            {
                var folder = Path.Combine(root, BotCaptureSession.DirectoryName(clip.Id));
                var timeline = BotClipTimeline.Open(folder, paths);
                Check(timeline.UsesSourceFrames && timeline.Count == 4 && Math.Abs(timeline.Duration - .9) < 1e-8,
                    "Use the playable frames within the clip from its incomplete original archive.");
                Check(timeline.FrameAt(0) == 0 && timeline.FrameAt(.152) == 1 && timeline.FrameAt(.7) == 2
                    && timeline.FrameAt(.89) == 3 && timeline.FrameAt(double.NaN) == -1,
                    "Seeking must select a causal source frame without jumping past the playhead.");
                var overlay = timeline.Overlay(1);
                Check(overlay is { ImageFile: null } && Math.Abs(overlay.Offset - .12) < 1e-8
                    && overlay.Authored == (clip.BotDomain == "hacker") && overlay.Terrain?.Walls.Count == 1,
                    "The image must use the latest observation in this domain, including observations without stills.");
                Check(timeline.Overlay(0) is null && timeline.Overlay(2) is null && timeline.Overlay(3) is null,
                    "Do not display future, foreign-generation, or stale terrain evidence.");
                Check(timeline.HeldButtons(0) == InputButtons.Right
                    && timeline.HeldButtons(1) == (InputButtons.Right | InputButtons.ActionX)
                    && timeline.HeldButtons(3) == InputButtons.ActionX
                    && timeline.HeldButtons(0) == InputButtons.Right,
                    "Display the recorded presses and releases at each image, including after seeking backwards.");
                Check(timeline.ReadSourceFrame(1).Pixels.SequenceEqual(image.Pixels), "Display the original archived pixels.");
                var missing = BotClipTimeline.Open(folder, Path.Combine(root, "missing-archive"));
                Check(!missing.UsesSourceFrames && missing.Count == 1 && File.Exists(missing.StillPath(0))
                    && missing.Overlay(0)?.Offset == missing.Offset(0) && missing.HeldButtons(0) == InputButtons.Right,
                    "Old clips must retain their matched stills and input states.");
            }

            var archive = Path.Combine(paths, $"frames-{replay:D}");
            var original = BotClipTimeline.Open(Path.Combine(root, BotCaptureSession.DirectoryName(paired[0].Id)), paths);
            File.AppendAllText(Path.Combine(archive, "00000002.png"), "corrupt");
            try { _ = original.ReadSourceFrame(1); throw new InvalidOperationException("Modified archive image was accepted."); }
            catch (InvalidDataException) { }
            File.WriteAllText(Path.Combine(archive, "manifest.json"), "{");
            var fallback = BotClipTimeline.Open(Path.Combine(root, BotCaptureSession.DirectoryName(paired[0].Id)), paths);
            Check(!fallback.UsesSourceFrames && fallback.Count == 1, "An unreadable archive must leave the captured still available.");
            var events = Path.Combine(root, BotCaptureSession.DirectoryName(paired[0].Id), "events.jsonl");
            var rows = File.ReadAllLines(events);
            File.WriteAllLines(events, rows.Select(row => row.Contains("\"button\":\"x\"", StringComparison.Ordinal)
                ? row.Replace("\"button\":\"x\"", "\"button\":null", StringComparison.Ordinal) : row));
            var missingInputs = BotClipTimeline.Open(Path.GetDirectoryName(events)!, paths);
            Check(missingInputs.Count == 1 && missingInputs.HeldButtons(0) is null,
                "Unavailable input evidence must not appear as released buttons or prevent image review.");

            void Write(double offset, long generation, double solveWidth = 80) =>
                writer.Append(image, Start + offset, Start + offset, (ulong)generation, solveWidth, null);
        });
    }

    internal static void TestPublishedCapturesLinkSourceArchiveAndFrozenReference()
    {
        WithLibrary(root =>
        {
            var paths = Path.Combine(root, "paths");
            var paired = WriteFixture(root, visualArchiveRoot: paths).Published;
            Check(paired.Count == 2, "The same run must publish both domains.");
            foreach (var clip in paired)
            {
                var folder = Path.Combine(root, BotCaptureSession.DirectoryName(clip.Id));
                var context = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "audit-context.json")))!.AsObject();
                var reference = Path.Combine(folder, "authored-ground.json");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reference))).ToLowerInvariant();
                Check(context["replayRoot"]!.GetValue<string>() == paths && context["pathID"]!.GetValue<Guid>() == clip.ReplayId
                    && context["domain"]!.GetValue<string>() == clip.BotDomain && context["authoredGroundSHA256"]!.GetValue<string>() == hash
                    && Directory.Exists(Path.Combine(paths, $"frames-{clip.ReplayId:D}")),
                    "Both recordings must link to the same saved frames and authenticate their frozen authored reference.");
                var observation = BotCaptureSession.Review(folder).Single();
                Check(observation.Authored == (clip.BotDomain == "hacker"),
                    "The frozen authored reference must never replace the Game Play detector's observations.");
            }
        });
    }

    internal static void TestReadsMacAndPreviousWindowsCaptureDates()
    {
        WithLibrary(root =>
        {
            var pair = WriteFixture(root);
            var recent = pair.Published[0];
            var legacy = pair.Published[1];
            var manifest = Path.Combine(root, BotCaptureSession.DirectoryName(legacy.Id), "manifest.json");
            var date = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            Check(date["createdAt"]!.GetValue<string>().EndsWith('Z')
                && !date["createdAt"]!.GetValue<string>().Contains('.'), "New dates must use the Mac's ISO 8601 format.");
            date["createdAt"] = 3600;
            date.Remove("botDomain");
            File.WriteAllText(manifest, date.ToJsonString());
            var manifests = BotCaptureSession.ReadLibraryManifests(root);
            Check(manifests.Count == 2 && manifests[1].Manifest.BotDomain is null,
                "The fast manifest scan should include domainless metadata before opening its events.");
            var clips = BotCaptureSession.Library(root);
            Check(clips.Count == 2 && clips[0].Id == recent.Id && clips[1].Id == legacy.Id
                && clips[1].BotDomain == legacy.BotDomain
                && clips[1].CreatedAt == new DateTimeOffset(2001, 1, 1, 1, 0, 0, TimeSpan.Zero),
                "Legacy clips should recover their domain and sort by actual capture time.");
            Check(BotCaptureSession.Review(Path.GetDirectoryName(manifest)!).Count == 1,
                "Older Windows clips must still open for review.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "events.jsonl"), "{}\n");
            Check(BotCaptureSession.ResolveLibraryDomains(manifests).Single().Id == recent.Id,
                "A damaged legacy event should not hide other captures.");
        });
    }

    internal static void TestSkillButtonsPreserveWireNamesAndEvidence()
    {
        WithLibrary(root =>
        {
            var verified = WriteFixture(root, skillControls: true);
            var interrupted = WriteFixture(root, skillControls: true, interruptSkillControls: true);
            var expected = new[] { "dash", "superDash", "quickCast", "dreamNail" };
            foreach (var capture in verified.Published)
            {
                var folder = Path.Combine(root, BotCaptureSession.DirectoryName(capture.Id));
                var (manifest, _, events) = BotCaptureSession.ReadReviewEvidence(folder, default);
                Check(manifest.RecordedSkillControls == true, "Skill capability evidence is missing from the paired clip.");
                var wire = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
                var paired = verified.Published.Single(clip => clip.Id != capture.Id);
                var pairedWire = JsonNode.Parse(File.ReadAllText(Path.Combine(root, BotCaptureSession.DirectoryName(paired.Id), "manifest.json")))!;
                Check(wire["id"]!.GetValue<string>() == capture.Id.ToString("D").ToUpperInvariant()
                    && wire["pairedClipID"]!.GetValue<string>() == pairedWire["id"]!.GetValue<string>()
                    && wire["replayID"]!.GetValue<string>() == pairedWire["replayID"]!.GetValue<string>(),
                    "Capture references must match the named session on the trainer wire.");
                foreach (var button in expected)
                    Check(events.Where(item => item.Button == button).Select(item => item.IsPressed).SequenceEqual([true, false]),
                        $"Missing original press/release or wrong wire case for {button}.");
            }
            Check(interrupted.Published.Count == 2 && interrupted.Published.All(clip => clip.RecordedSkillControls == false),
                "An interrupted capability session may not train missing abilities as releases.");
        });
    }

    internal static BotCaptureResult WriteFixture(string root, bool observeHacker = true,
        bool skillControls = false, bool interruptSkillControls = false, string? visualArchiveRoot = null)
    {
        var id = Guid.NewGuid();
        var labels = Labels();
        var record = new InputPathRecorder();
        record.Start(Start, id: id, metadata: skillControls
            ? new Dictionary<string, string> { [RecordedInputPath.SkillControlsMetadata] = "true" } : null, visualArchiveRoot: visualArchiveRoot);
        record.RecordInput(RecordedGameButton.Right, true, Start);
        record.RecordInput(RecordedGameButton.X, true, Start + .08);
        if (skillControls)
        {
            record.RecordInput(RecordedGameButton.Dash, true, Start + .20);
            record.RecordInput(RecordedGameButton.Dash, false, Start + .30);
        }
        record.RecordInput(RecordedGameButton.X, false, Start + .36);
        record.RecordInput(RecordedGameButton.X, true, Start + .39);
        if (skillControls)
        {
            record.RecordInput(RecordedGameButton.SuperDash, true, Start + .40);
            record.RecordInput(RecordedGameButton.SuperDash, false, Start + .50);
            record.RecordInput(RecordedGameButton.QuickCast, true, Start + .64);
            record.RecordInput(RecordedGameButton.QuickCast, false, Start + .66);
            record.RecordInput(RecordedGameButton.DreamNail, true, Start + .68);
            record.RecordInput(RecordedGameButton.DreamNail, false, Start + .70);
        }
        if (interruptSkillControls) record.InvalidateSkillControls();
        var active = new BotCaptureSession(root, "modded", "visual", Generation, Start, labels, id, paired: true, replayRoot: visualArchiveRoot);
        for (var i = 0; i < 6; i++)
        {
            var input = Input(i);
            record.RecordVisualFrame(input.Frame, input.Time.CaptureTimestamp, input.Time.CaptureTimestamp, Generation, null);
            Check(active.TryRecord(input.Frame, Visual(input), observeHacker ? Hacker(input, input.Mod) : null), "A current paired sample was dropped.");
        }
        // Keep attack held through stop and release right independently. The path recorder supplies the final releases.
        record.RecordInput(RecordedGameButton.Right, false, Start + .72);
        var path = record.Stop(Start + .9);
        if (skillControls)
        {
            var wired = path!.StateChanges().Select(item => item.HeldButtons).Aggregate((a, b) => a | b);
            Check((wired & InputButtons.SkillAbilities) == InputButtons.SkillAbilities, "Replay dropped the new receiver bits.");
        }
        return active.FinishAsync(Start + .9, path, completed: true).GetAwaiter().GetResult();
    }

    private static GroundTruthLineDocument Labels() => new()
    {
        Lines = [
            new("Tutorial_01", -40, 40, 0, GroundTruthLineKind.Positive, false),
            new("Tutorial_01", -30, 30, 1, GroundTruthLineKind.Positive, true),
            new("Other", -30, 30, 1, GroundTruthLineKind.Positive, false)
        ],
        Walls = [
            new GroundTruthWorldLine("Tutorial_01", -8, -8, -3, GroundTruthLineKind.Wall, false) { EndWorldY = 8 }
        ],
        Doors = [new("Tutorial_01", 10, 12, 0, GroundTruthLineKind.Door, false)],
        Spikes = [new("Tutorial_01", 5, 6, 0, GroundTruthLineKind.Spike, false)]
    };

    private sealed record Fixture(BgraFrame Frame, BotCaptureTiming Time, ReceiverGroundTruthSample Mod,
        LiveObjectDetectionBatch Detections);

    private static Fixture Input(int index)
    {
        const int w = 80;
        const int h = 48;
        var now = Start + .02 + index * .1;
        var frame = new BgraFrame(w, h, w * 4, new byte[w * h * 4], index, DateTimeOffset.UnixEpoch.AddSeconds(now));
        var clock = new BotCaptureTiming(now, now + .001, Generation, 1, 0);
        var mod = new ReceiverGroundTruthSample(2, "groundTruth", Guid.NewGuid(), 7, index, now, "Tutorial_01",
            true, 0, 0, 0, 0, 0, true, true, true, 0, 0, 0, 0, 0, 0, 24, 2, 2, 80, 36, 160, 96, 160, 96)
        {
            AtlasGameplayActive = true,
            Actors = new(1, new(1, "Knight", 80, 36, 10, 12),
                [new(2, "Crawlid", 128, 32, 12, 8)], [new(3, "Geo", 100, 32, 12, 18)],
                [new(4, "Cocoon", 110, 42, 10, 10)], new(EnemyEpoch, index >= 4 ? 1 : 0, 0), HeroHealth: 5),
            BlueHealthOutcomes = new(BlueEpoch, index >= 2 ? 2 : 0)
        };
        var detections = new LiveObjectDetectionBatch(
        [
            new ObjectDetection("game.playable-knight", new(.4, .4, .1, .2), .9f) { SourceFrameIdentifier = index },
            new ObjectDetection("enemies.crawlid", new(.1, .5, .08, .1), .8f) { SourceFrameIdentifier = index },
        ], index, (frame.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds, Generation, 0);
        return new(frame, clock, mod, detections);
    }

    private static BotCaptureObservation? Hacker(Fixture input, ReceiverGroundTruthSample source) =>
        BotObservationBuilder.Hacker(input.Frame, (int)input.Frame.CaptureId, source, Labels(), input.Time, InputButtons.Right);

    private static BotCaptureObservation Visual(Fixture input, IReadOnlyList<BotLine>? doors = null) =>
        BotObservationBuilder.Gameplay(input.Frame, input.Time, input.Detections, [new(36, new(5, 62))],
            [new(70, 10, 42, true)], terrainAnalyzed: true, new(0, 0), null, new(30, 36), InputButtons.Right,
            input.Mod.BlueHealthOutcomes, doors);

    private static void WithLibrary(Action<string> run)
    {
        var directory = Directory.CreateTempSubdirectory("hkv-bot-capture-test-");
        try { run(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
