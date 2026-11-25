using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotBlockadeReplayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Clip(string Folder, string ReplayRoot, string Trace, Guid Id, IReadOnlyList<BotCaptureObservation> Rows)
    {
        internal string Events => Path.Combine(Folder, "events.jsonl");
    }

    internal static void TestVisualReplayProjectsContactsThroughMeasuredIslands()
    {
        var root = Directory.CreateTempSubdirectory("hkv-legacy-blockade-visual-");
        try
        {
            var clip = WriteClip(root.FullName, hacker: false);
            var before = File.ReadAllBytes(clip.Events);
            var report = BotBlockadeReplay.Recover(clip.Folder);
            var mark = report.Marks.Single();
            Require(mark is { Crossed: true, Direction: 1 } && mark.Space.StartsWith("visual/1/2/", StringComparison.Ordinal),
                "Passage must retire the old-island contact after a measured coordinate change.");
            Require(report.Rows.Select(item => item.CaptureTimestamp).SequenceEqual([.375, .5])
                && report.Rows[1].Blockade.Mark.Space == mark.Space
                && Math.Abs(report.Rows[1].Blockade.Dx - .015) < 1e-10,
                "The exact original pose should keep the contact at the same distance through the island handoff.");
            var loaded = BotCaptureSession.Library(root.FullName).Single();
            var frames = new VisualReplayArchiveWriter(clip.ReplayRoot, clip.Id, new Dictionary<string, string>());
            var image = new BgraFrame(100, 60, 400, new byte[24_000], 1, DateTimeOffset.UnixEpoch);
            foreach (var timestamp in new[] { .375, .5 }) frames.Append(image, timestamp, timestamp, 7, 100, null);
            _ = frames.FinishAsync().GetAwaiter().GetResult();
            var timeline = BotClipTimeline.Open(clip.Folder, Path.Combine(root.FullName, "default-paths"));
            Require(loaded is { SchemaVersion: 1, BotDomain: "gameplay" }
                && loaded.FeatureNames.Count == BotRecordingContract.LegacyWidth
                && timeline.UsesSourceFrames && timeline.Count == 2 && timeline.Overlay(0)?.Blockade == report.Rows[0].Blockade
                && timeline.Overlay(1)?.Blockade == report.Rows[1].Blockade,
                "A first-version clip should appear in Bot Summary and show its original visual evidence.");
            BotBlockadeReplay.WriteCrossed(report, root.FullName);
            using var registered = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.FullName, "blockade-map", "gameplay",
                clip.Id.ToString("D").ToUpperInvariant() + ".json")));
            Require(registered.RootElement.GetArrayLength() == 1 && registered.RootElement[0].GetProperty("crossed").GetBoolean(),
                "Register only proven crossings in a separate diagnostic file.");
            Require(File.ReadAllBytes(clip.Events).SequenceEqual(before), "Review and registration must preserve the original evidence.");

            WriteVisualTrace(clip, placement: "estimated");
            Require(BotBlockadeReplay.Recover(clip.Folder).Marks.All(item => !item.Crossed),
                "An unplaced second island must not register a crossing of the old one.");
            WriteVisualTrace(clip, duplicateCapture: true);
            var ambiguous = BotBlockadeReplay.Recover(clip.Folder);
            Require(ambiguous.Rows.All(item => item.CaptureTimestamp != .5) && ambiguous.Marks.All(item => !item.Crossed),
                "Two island records for a frame must never be treated as an observed position.");

            WriteVisualTrace(clip);
            File.WriteAllText(Path.Combine(clip.Folder, "audit-context.json"),
                JsonSerializer.Serialize(new { pathID = Guid.NewGuid(), replayRoot = clip.ReplayRoot }, Json));
            try { _ = BotBlockadeReplay.Recover(clip.Folder); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Foreign replay identity was accepted.");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestHackerReplayUsesUniqueReceiverFrameAndGrounding()
    {
        var root = Directory.CreateTempSubdirectory("hkv-legacy-blockade-hacker-");
        try
        {
            var clip = WriteClip(root.FullName, hacker: true);
            var report = BotBlockadeReplay.Recover(clip.Folder);
            Require(report.Rows.Select(item => item.CaptureTimestamp).SequenceEqual([.375, .5])
                && report.Marks is [{ Space: "hacker/Tutorial_01", Crossed: true, ViewWidth: 20 }],
                "The replay must recover legacy Hacker poses and combine the wrapped receiver frame marker with the saved input.");
            var loaded = BotCaptureSession.Library(root.FullName).Single();
            var preview = BotClipTimeline.Open(clip.Folder, clip.ReplayRoot);
            Require(loaded.BotDomain == "hacker" && preview.Overlay(0) is { Authored: true, Blockade.Mark.Space: "hacker/Tutorial_01" },
                "Older captures without an explicit domain must preserve their authored camera in the library and preview.");
            WriteHackerTrace(clip, ambiguousFrame: true);
            Require(BotBlockadeReplay.Recover(clip.Folder).Rows.Count == 0,
                "An ambiguous match breaks push continuity even when one receiver sample is usable.");
            WriteHackerTrace(clip, airborne: true);
            Require(BotBlockadeReplay.Recover(clip.Folder).Marks.Count == 0,
                "Archived airborne samples must not create stationary wall contacts.");
            BotClipDeletion.Delete(root.FullName, clip.ReplayRoot, loaded);
            Require(!Directory.Exists(clip.Folder) && !File.Exists(clip.Trace),
                "The validated legacy capture and its owned linked replay must remain deletable.");
        }
        finally { root.Delete(recursive: true); }
    }

    internal static void TestHostExecutable(string host, string? output)
    {
        var root = output is null ? Directory.CreateTempSubdirectory("hkv-blockade-host-") : Directory.CreateDirectory(output);
        try
        {
            var visual = WriteClip(Directory.CreateDirectory(Path.Combine(root.FullName, "visual")).FullName, hacker: false);
            var hacker = WriteClip(Directory.CreateDirectory(Path.Combine(root.FullName, "hacker")).FullName, hacker: true);
            foreach (var clip in new[] { visual, hacker })
            {
                var raw = Call(0, "--recover-bot-blockades", clip.Folder);
                using var report = JsonDocument.Parse(raw);
                Require(report.RootElement.GetProperty("id").GetGuid() == clip.Id
                    && report.RootElement.GetProperty("rows").GetArrayLength() == 2
                    && report.RootElement.GetProperty("marks")[0].GetProperty("crossed").GetBoolean(),
                    "The installed Host must emit the legacy evidence on the production training command.");
                if (output is not null)
                {
                    var evidence = Directory.CreateDirectory(Path.Combine(root.FullName, "sidecars")).FullName;
                    var eventsSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(clip.Events))).ToLowerInvariant();
                    File.WriteAllText(Path.Combine(evidence, Path.GetFileName(clip.Folder) + ".json"), JsonSerializer.Serialize(
                        new { id = clip.Id, eventsSHA256, rows = report.RootElement.GetProperty("rows") }, Json));
                }
            }
            _ = Call(64, "--recover-bot-blockades");
            _ = Call(1, "--recover-bot-blockades", Path.Combine(root.FullName, "missing"));
        }
        finally { if (output is null) root.Delete(recursive: true); }

        string Call(int expected, params string[] arguments)
        {
            var assembly = Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(assembly ? "dotnet" : host)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (assembly) start.ArgumentList.Add(host);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("The recorded blockade Host probe timed out.");
            }
            Require(process.ExitCode == expected, stderr.GetAwaiter().GetResult());
            return stdout.GetAwaiter().GetResult();
        }
    }

    internal static void VerifyRecordedDoorPolicies(string library)
    {
        var firstDoor = Open(2, "hacker");
        var visit = new BotBlockadeVisit();
        double? arrival = null, contact = null;
        var crossed = false;
        foreach (var row in firstDoor.Rows)
        {
            if (row.BlockadePose is not { Space: "hacker/Tutorial_01" } pose) continue;
            var x = pose.X + row.Features[0] * pose.Width;
            var evidence = visit.Observe(row, pose);
            if (Math.Abs(x - 58.75) < .02 && row.DriveDirection == 1)
                arrival ??= row.CapturedAt;
            if (arrival is null)
                Require(visit.ActiveMarks.Count == 0, "The uninterrupted first door approach should have no measured blockade.");
            if (evidence?.Mark is { Direction: 1, X: > 58.4 and < 59.6 })
                contact ??= row.CapturedAt;
            if (x <= 61) continue;
            crossed = evidence is null && visit.ActiveMarks.Count == 0;
            break;
        }
        Require(contact - arrival is >= 0 and <= .3 && crossed, "The first door should be measured promptly and clear on passage.");
        var firstReport = BotBlockadeReplay.Recover(firstDoor.Folder);
        Require(firstReport.Marks.Any(mark => mark is { Direction: 1, X: > 58.4 and < 59.6, Crossed: true }),
            "The first door crossing must be reproducible from the saved evidence.");
        Console.WriteLine(FormattableString.Invariant($"Hacker clip 2 first door measured after {contact - arrival:F3} s, then retired on passage."));

        foreach (var domain in new[] { BotCaptureDomain.Gameplay, BotCaptureDomain.Hacker })
        {
            var name = domain.ToString().ToLowerInvariant();
            var clip = Open(20, name);
            var modelRoot = Path.Combine(library, "models", name);
            using var selection = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(modelRoot, "active.json")));
            var selected = selection.RootElement.GetProperty("directory").GetString()!;
            var policy = BotNativePolicy.Load(Path.Combine(modelRoot, selected));
            var recovered = BotBlockadeReplay.Recover(clip.Folder).Rows
                .ToDictionary(frame => (frame.Generation, frame.CaptureTimestamp), frame => frame.Blockade);
            var playback = new BotPolicyPlayback(policy, domain);
            BotPlaybackDecision? previous = null;
            var decisionsAtDoor = 0;
            var attackPressesAtDoor = 0;
            foreach (var saved in clip.Rows)
            {
                var row = saved with { Blockade = saved.Blockade ?? recovered.GetValueOrDefault((saved.Generation, saved.CaptureTimestamp)) };
                var held = previous is { } prior && row.AvailableAt < prior.Deadline ? prior.Action.Buttons : InputButtons.None;
                var update = playback.Evaluate(row, held, row.AvailableAt);
                if (update is null) continue;
                previous = update.Decision;
                if (previous is not { } decision || row.Blockade is null) continue;
                decisionsAtDoor++;
                if (!held.HasFlag(InputButtons.ActionX) && decision.Action.Attack == 1) attackPressesAtDoor++;
            }
            Require(decisionsAtDoor > 0, $"The selected {name} policy must make decisions while receiving recorded blockade evidence.");
            Console.WriteLine($"{name} clip 20, schema {policy.Version}. {decisionsAtDoor} decisions with obstacle evidence and {attackPressesAtDoor} fresh attack presses.");
        }

        (string Folder, BotCaptureObservation[] Rows) Open(int number, string domain)
        {
            var numbers = JsonSerializer.Deserialize<Dictionary<Guid, int>>(
                File.ReadAllBytes(Path.Combine(library, $"clip-numbers-by-domain-{domain}.json")), Json)!;
            var id = numbers.Single(item => item.Value == number).Key;
            var folder = Path.Combine(library, "demonstrations", BotCaptureSession.DirectoryName(id));
            var rows = File.ReadLines(Path.Combine(folder, "events.jsonl")).Select(line =>
            {
                using var payload = JsonDocument.Parse(line);
                return payload.RootElement.GetProperty("type").GetString() == "observation"
                    ? payload.RootElement.Deserialize<BotCaptureObservation>(Json) : null;
            }).OfType<BotCaptureObservation>().ToArray();
            return (folder, rows);
        }
    }

    private static Clip WriteClip(string root, bool hacker)
    {
        var id = Guid.NewGuid();
        var folder = Directory.CreateDirectory(Path.Combine(root, BotCaptureSession.DirectoryName(id))).FullName;
        var replayRoot = Directory.CreateDirectory(Path.Combine(root, "paths")).FullName;
        var rows = Enumerable.Range(0, 6).Select(index =>
        {
            var time = index * .125;
            var features = new double[BotRecordingContract.LegacyWidth];
            features[0] = index == 5 ? .56 : .5;
            features[1] = .2;
            features[2] = .03;
            features[3] = .05;
            features[4] = features[6] = 1;
            return new BotCaptureObservation(time, time, time + .001, 7, 1, 0, features, [],
                hacker ? "human-authored" : "detected")
            {
                DetectionTimestamp = time, DetectionFrameId = 100 + index,
                AtlasView = hacker ? null : new(0, 1, [index >= 4 ? 20 : 0, 0], 100, 0, 0, 0, null),
                ImagePath = index is 3 or 4 ? $"{index}.png" : null
            };
        }).ToArray();
        var manifest = new BotCaptureManifest(1, id, DateTimeOffset.UnixEpoch, 0, "modded", "fixture", "receiver-sent",
            "completed", 1, rows.Length, rows.Length, 1, 2, hacker ? null! : "gameplay", null, null);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, BotCaptureSession.Json));
        File.WriteAllText(Path.Combine(folder, "audit-context.json"), JsonSerializer.Serialize(new { pathID = id, replayRoot }, Json));
        var clip = new Clip(folder, replayRoot, Path.Combine(replayRoot, $"path-fixture-{id:D}.json"), id, rows);
        File.WriteAllLines(clip.Events, rows.Select(row => JsonSerializer.Serialize(row, BotCaptureSession.Json))
            .Append(JsonSerializer.Serialize(new BotCaptureInput(.0625, "right", true), Json)));
        File.WriteAllText(Path.Combine(folder, "3.png"), "fixture");
        File.WriteAllText(Path.Combine(folder, "4.png"), "fixture");
        if (hacker) WriteHackerTrace(clip);
        else WriteVisualTrace(clip);
        return clip;
    }

    private static void WriteVisualTrace(Clip clip, string placement = "connected", bool duplicateCapture = false)
    {
        var samples = clip.Rows.Select((row, index) => new
        {
            row.CaptureTimestamp,
            island = new
            {
                id = index >= 4 ? 3 : 2, roomID = 1, startedAt = index >= 4 ? .5 : 0, measured = true,
                placement = index >= 4 ? placement : "reference",
                worldFromLocal = new[] { index >= 4 ? 20.0 : 0, 0 },
                localPose = new[] { index >= 4 ? -20.0 : 0, 0 }
            }
        }).ToList();
        if (duplicateCapture) samples.Add(samples[4]);
        File.WriteAllText(clip.Trace, JsonSerializer.Serialize(new { clip.Id, trackingSamples = samples }, Json));
    }

    private static void WriteHackerTrace(Clip clip, bool ambiguousFrame = false, bool airborne = false)
    {
        var samples = clip.Rows.Select((row, index) => new RecordedGroundTruthSample(row.CaptureTimestamp,
            row.CapturedAt + .03, new(2, "ground_truth", clip.Id, (ulong)index + 1, (1L << 24) + 100 + index,
                row.CaptureTimestamp, "Tutorial_01", true, 10, 4, 0, 0, 0, true, !airborne, true, 10, 6, 0,
                10, 6, 0, 6, 10, 10, 100, 40, 200, 120, 200, 120))).ToList();
        if (ambiguousFrame) samples.Add(samples[2]);
        // A duplicate from a different time window does not interfere with the exact frame.
        samples.Add(samples[3] with { ReceivedTimestamp = 60 });
        File.WriteAllText(clip.Trace, JsonSerializer.Serialize(new { clip.Id, groundTruthSamples = samples }, Json));
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
