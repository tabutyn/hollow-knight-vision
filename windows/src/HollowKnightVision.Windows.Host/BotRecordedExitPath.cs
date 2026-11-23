using HollowKnightVision.Windows.Core;
using System.Text.Json;

namespace HollowKnightVision.Windows.Host;

internal static class BotRecordedExitPath
{
    internal sealed record Frame(int Index, long Generation, double CaptureTimestamp,
        BotRoadHint? RoadHint, double? PathProgress, double? PathAlignment)
    {
        public int HackerPathVersion => 1;
    }

    internal static IReadOnlyList<Frame> Recover(string folder, string replayPath, string labels, string links, AtlasPoint objective)
    {
        var capture = BotRecordedAtlasPath.ReadCapture(folder);
        var id = capture.Manifest.ReplayId ?? capture.Manifest.Id;
        var replay = new RecordedInputPathStore().Load(replayPath);
        if (replay.Id != id) throw new InvalidDataException("The archived replay belongs to another capture.");

        var root = Path.GetDirectoryName(Path.GetFullPath(replayPath))!;
        var archiveSummary = replay.VisualFrameArchive;
        if (archiveSummary is null)
        {
            var manifest = Path.Combine(root, "frames-" + id.ToString("D"), "manifest.json");
            using var stream = File.OpenRead(manifest);
            archiveSummary = JsonSerializer.Deserialize<VisualReplayManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Summary
                ?? throw new InvalidDataException("The visual replay archive has no summary.");
        }
        var archive = new VisualReplayArchiveReader(root, archiveSummary, id, requireComplete: false);
        var frames = archive.Manifest.Frames.ToLookup(frame => (frame.CaptureGeneration, frame.CaptureTimestamp));
        var actors = (replay.GroundTruthSamples ?? throw new InvalidDataException("The archived replay has no actor samples."))
            .ToLookup(row => HackerFrameSynchronizer.Key(row.Sample.UnityFrame));
        var guide = new GymHackerRoute(labels, links, objective);
        (long Generation, int Tick)? previous = null;
        var result = new List<Frame>(capture.Observations.Count);
        for (var index = 0; index < capture.Observations.Count; index++)
        {
            var row = capture.Observations[index];
            var observed = row.Generation >= 0
                ? Unique(frames[((ulong)row.Generation, row.CaptureTimestamp)]) : null;
            BotCaptureObservation? route = null;
            if (observed is { RenderedGameFrame: { } marker, Width: >= 2 and <= 8192, Height: >= 2 and <= 8192 }
                && Math.Round(row.CaptureTimestamp * 60, MidpointRounding.AwayFromZero) is >= 0 and <= int.MaxValue)
            {
                var actor = Unique(actors[marker].Where(sample => Math.Abs(sample.ReceivedTimestamp - row.CapturedAt) <= .25));
                if (actor?.Sample.SceneName == "Tutorial_01")
                {
                    var tick = (int)Math.Round(row.CaptureTimestamp * 60, MidpointRounding.AwayFromZero);
                    if (previous is { } prior && (prior.Generation != row.Generation || tick <= prior.Tick))
                        guide = new GymHackerRoute(labels, links, objective);
                    var horizontal = row.DriveDirection switch
                    {
                        -1 => InputButtons.Left,
                        1 => InputButtons.Right,
                        _ => InputButtons.None
                    };
                    route = guide.Observe(actor.Sample, tick, health: null, BotPolicyAction.FromButtons(horizontal),
                        new WorldSize(observed.Width, observed.Height));
                    previous = (row.Generation, tick);
                }
            }
            if (route is null) guide.Interrupt();
            result.Add(new(index, row.Generation, row.CaptureTimestamp, route?.RoadHint, route?.PathProgress, route?.PathAlignment));
        }
        return result;
    }

    private static T? Unique<T>(IEnumerable<T> candidates) where T : class
    {
        using var matches = candidates.GetEnumerator();
        if (!matches.MoveNext()) return null;
        var first = matches.Current;
        return matches.MoveNext() ? null : first;
    }
}
