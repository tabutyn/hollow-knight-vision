using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

/// <summary>Capture-time coordinates missing from older observations, joined by the original frame identity.</summary>
internal sealed class BotBlockadeReplaySource
{
    internal sealed record Island(
        [property: JsonRequired] int Id,
        [property: JsonRequired, JsonPropertyName("roomID")] int RoomId,
        [property: JsonRequired] double StartedAt,
        [property: JsonRequired, JsonConverter(typeof(SwiftPointConverter))] AtlasPoint WorldFromLocal,
        [property: JsonRequired, JsonConverter(typeof(SwiftPointConverter))] AtlasPoint LocalPose,
        [property: JsonRequired] bool Measured, string? Placement)
    {
        internal string Space =>
            FormattableString.Invariant($"visual/{RoomId}/{Id}/{BitConverter.DoubleToUInt64Bits(StartedAt)}");

        internal bool IsMeasured => Measured && Id >= 0 && RoomId >= 0 && double.IsFinite(StartedAt)
            && LiveWorldSnapshot.Finite(WorldFromLocal) && LiveWorldSnapshot.Finite(LocalPose);
        internal bool IsPlaced => IsMeasured && Placement is "connected" or "reference";
    }

    private sealed record Context(string? ReplayRoot, [property: JsonPropertyName("pathID")] Guid? PathId);
    private sealed record VisualSample([property: JsonRequired] double CaptureTimestamp, Island? Island);
    private sealed record Trace(Guid Id, VisualSample[]? TrackingSamples, RecordedGroundTruthSample[]? GroundTruthSamples);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILookup<double, VisualSample> visual;
    private readonly ILookup<int, RecordedGroundTruthSample> hacker;

    private BotBlockadeReplaySource(Trace trace)
    {
        visual = (trace.TrackingSamples ?? []).Where(sample => sample is not null && double.IsFinite(sample.CaptureTimestamp))
            .ToLookup(sample => sample.CaptureTimestamp);
        hacker = (trace.GroundTruthSamples ?? []).Where(sample => sample?.Sample is not null && double.IsFinite(sample.ReceivedTimestamp))
            .ToLookup(sample => HackerFrameSynchronizer.Key(sample.Sample.UnityFrame));
    }

    internal static BotBlockadeReplaySource? Open(string folder, Guid replayId, string? replayRoot)
    {
        replayRoot = LinkedRoot(folder, replayId) ?? replayRoot;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var roots = replayRoot is not null ? new[] { replayRoot } : new[]
        {
            Path.Combine(documents, "HollowKnightVision", "paths"),
            new RecordedInputPathStore().RootDirectory
        };
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "path-*.json")
                .Where(path => Path.GetFileName(path).EndsWith($"-{replayId:D}.json", StringComparison.OrdinalIgnoreCase)))
            .Distinct(comparison).Take(2).ToArray();
        if (candidates is not [var file]) return null;
        var trace = JsonSerializer.Deserialize<Trace>(File.ReadAllBytes(file), Json);
        if (trace is null || trace.Id != replayId) throw new InvalidDataException("The saved input path belongs to another replay.");
        return new(trace);
    }

    internal static string? LinkedRoot(string folder, Guid replayId)
    {
        var contextPath = Path.Combine(folder, "audit-context.json");
        if (!File.Exists(contextPath)) return null;
        var context = JsonSerializer.Deserialize<Context>(File.ReadAllBytes(contextPath), Json);
        if (context?.PathId is { } pathId && pathId != replayId)
            throw new InvalidDataException("The audit context belongs to another replay.");
        return context?.ReplayRoot is { } archivedRoot && Path.IsPathFullyQualified(archivedRoot) && Directory.Exists(archivedRoot)
            ? archivedRoot : null;
    }

    internal (Island? Island, bool HasIslandRecord) Visual(BotCaptureObservation row)
    {
        var matching = visual[row.CaptureTimestamp].Take(2).ToArray();
        return matching switch
        {
            [{ Island: { IsMeasured: true } island }] when island.RoomId == row.RoomId => (island, true),
            [{ Island: null }] or [] => (null, false),
            _ => (null, true)
        };
    }

    internal (BotBlockadePose? Pose, bool? Grounded) Hacker(BotCaptureObservation row)
    {
        if (row.DetectionFrameId is not >= 0) return default;
        var key = HackerFrameSynchronizer.Key(row.DetectionFrameId.Value);
        var matching = hacker[key].Where(sample => Math.Abs(sample.ReceivedTimestamp - row.CapturedAt) <= .25)
            .Take(2).ToArray();
        if (matching is not [{ Sample: { CameraAvailable: true, HeroAvailable: true, AtlasGameplayActive: not false } sample }]
            || !sample.HasFiniteCoordinates() || string.IsNullOrWhiteSpace(sample.SceneName)
            || sample.ProjectionPixelWidth is not > 0 || sample.ProjectionPixelHeight is not > 0
            || sample.PixelsPerWorldUnitX is not > 0 || sample.PixelsPerWorldUnitY is not > 0) return default;
        var size = new WorldSize(sample.ProjectionPixelWidth.Value, sample.ProjectionPixelHeight.Value);
        var camera = GroundTruthCameraTransform.FromSample(sample, row.CapturedAt, size);
        return (camera is null ? null : BotBlockadePose.Hacker(camera, size), sample.Grounded);
    }
}
