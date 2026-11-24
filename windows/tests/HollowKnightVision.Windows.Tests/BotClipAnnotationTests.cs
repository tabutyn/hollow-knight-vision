using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class BotClipAnnotationTests
{
    internal static void TestSidecarRequiresMatchingClipChecksumAndFrameIdentity()
    {
        var root = Directory.CreateTempSubdirectory("hkv-clip-annotations-");
        try
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var library = Path.Combine(root.FullName, "demonstrations");
            var clip = BotCaptureTests.WriteFixture(library).Published.Single(item => item.BotDomain == "hacker");
            var name = BotCaptureSession.DirectoryName(clip.Id);
            var folder = Path.Combine(library, name);
            var events = Path.Combine(folder, "events.jsonl");
            var rows = File.ReadLines(events).Select(line => JsonNode.Parse(line)!).ToArray();
            var observations = rows.Where(row => (string?)row["type"] == "observation").ToArray();
            var original = new BotRoadHint(-1, 0, .2, .3, false);
            var recovered = original with { Dx = 1 };
            observations[0]["atlasPathVersion"] = 1;
            observations[0]["roadHint"] = JsonSerializer.SerializeToNode(original, json);
            observations[4]["hackerPathVersion"] = 1;
            File.WriteAllLines(events, rows.Select(row => row.ToJsonString()));

            var paths = Path.Combine(root.FullName, "paths");
            var replay = clip.ReplayId!.Value;
            var archive = new VisualReplayArchiveWriter(paths, replay, new Dictionary<string, string>());
            var times = observations.Select(row => (double)row["captureTimestamp"]!).ToArray();
            var generation = (long)observations[0]["generation"]!;
            for (var index = 0; index < times.Length; index++)
            {
                var frame = new BgraFrame(80, 48, 320, new byte[80 * 48 * 4], index, DateTimeOffset.UnixEpoch);
                archive.Append(frame, times[index], times[index], (ulong)generation, 80, null);
            }
            _ = archive.FinishAsync().GetAwaiter().GetResult();

            var sidecars = Directory.CreateDirectory(Path.Combine(root.FullName, "exit-paths-v1")).FullName;
            var sidecar = Path.Combine(sidecars, name + ".json");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(events))).ToLowerInvariant();
            WriteSidecar(clip.Id, replay);
            var timeline = BotClipTimeline.Open(folder, paths);
            Check(timeline.UsesSourceFrames && timeline.Count == times.Length && timeline.Overlay(0)?.PathHint == original
                && timeline.Overlay(1)?.PathHint == recovered && timeline.Overlay(2)?.PathHint is null
                && timeline.Overlay(3)?.PathHint is null && timeline.Overlay(4)?.PathHint is null,
                "Show exact matching recovered routes only where there is no versioned route decision.");
            WriteSidecar(Guid.NewGuid(), replay);
            Check(BotClipTimeline.Open(folder, paths).Overlay(1)?.PathHint is null, "A route from another clip must be ignored.");
            WriteSidecar(clip.Id, Guid.NewGuid());
            Check(BotClipTimeline.Open(folder, paths).Overlay(1)?.PathHint is null, "A route from another replay must be ignored.");
            WriteSidecar(clip.Id, replay);
            File.AppendAllText(events, "{}\n");
            var changed = BotClipTimeline.Open(folder, paths);
            Check(changed.Overlay(0)?.PathHint == original && changed.Overlay(1)?.PathHint is null,
                "A changed event checksum must hide only recovered routes.");
            File.WriteAllText(sidecar, "{");
            Check(BotClipTimeline.Open(folder, paths).Overlay(0)?.PathHint == original, "An unreadable sidecar must leave clip review available.");

            void WriteSidecar(Guid id, Guid replayId) => File.WriteAllText(sidecar, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id, replayID = replayId, eventsSHA256 = hash,
                rows = times.Take(5).Select((time, index) => new
                {
                    index, generation = index == 2 ? generation + 1 : generation,
                    captureTimestamp = index == 3 ? time + .01 : time, hackerPathVersion = 1, roadHint = recovered
                })
            }, json));
        }
        finally { root.Delete(recursive: true); }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
