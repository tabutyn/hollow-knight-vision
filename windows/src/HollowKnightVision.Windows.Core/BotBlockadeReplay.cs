using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record BotRecoveredBlockade(long Generation, double CaptureTimestamp, BotBlockadeEvidence Blockade);
public sealed record BotRecoveredBlockades(Guid Id, IReadOnlyList<BotRecoveredBlockade> Rows, IReadOnlyList<BotBlockadeMark> Marks)
{
    [JsonIgnore] internal BotCaptureDomain Domain { get; init; }
    [JsonIgnore] internal IReadOnlySet<string> VerifiedSpaces { get; init; } = new HashSet<string>();
}

/// <summary>Replays eligible saved measurements in clock order without changing the clip.</summary>
public static class BotBlockadeReplay
{
    private sealed record Identity(Guid Id, [property: JsonPropertyName("replayID")] Guid? ReplayId, string? BotDomain);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BotRecoveredBlockades Recover(string folder)
    {
        var manifest = JsonSerializer.Deserialize<Identity>(File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json);
        if (manifest is null || manifest.Id == Guid.Empty) throw new InvalidDataException("The capture identity is missing.");
        var eventsPath = Path.Combine(folder, "events.jsonl");
        var inputs = new List<BotCaptureInput>();
        var rows = new List<BotCaptureObservation>();
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var record = JsonDocument.Parse(line);
            switch (record.RootElement.GetProperty("type").GetString())
            {
                case "input":
                    var input = record.RootElement.Deserialize<BotCaptureInput>(Json);
                    if (input is not null && double.IsFinite(input.Timestamp)) inputs.Add(input);
                    break;
                case "observation":
                    rows.Add(record.RootElement.Deserialize<BotCaptureObservation>(Json)
                        ?? throw new InvalidDataException("The capture has an empty observation."));
                    break;
            }
        }
        return Recover(folder, manifest.Id, manifest.ReplayId, manifest.BotDomain, rows, inputs);
    }

    internal static BotRecoveredBlockades Recover(string folder, Guid id, Guid? replayId, string? domainName,
        IReadOnlyList<BotCaptureObservation> rows, IReadOnlyList<BotCaptureInput> inputs, string? replayRoot = null,
        CancellationToken cancellationToken = default)
    {
        var domain = domainName switch
        {
            "gameplay" => BotCaptureDomain.Gameplay,
            "hacker" => BotCaptureDomain.Hacker,
            null => rows.Any(row => row.TerrainSource == "human-authored") ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay,
            _ => throw new InvalidDataException("Unknown capture camera domain.")
        };
        var source = rows.Any(row => row.BlockadeVersion is null && row.BlockadePose is null)
            ? BotBlockadeReplaySource.Open(folder, replayId ?? id, replayRoot) : null;
        var orderedInputs = inputs.OrderBy(input => input.Timestamp).ToArray();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observations = new List<BotRecoveredBlockade>();
        var marks = new List<BotBlockadeMark>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var origins = new Dictionary<string, AtlasPoint>(StringComparer.Ordinal);
        var cursor = 0;
        var last = double.NegativeInfinity;
        (long Generation, int Room)? scope = null;
        var visit = new BotBlockadeVisit(marks.AddRange);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(row.CapturedAt) || !double.IsFinite(row.CaptureTimestamp) || row.CapturedAt <= last)
                throw new InvalidDataException("Bot capture observations must be in capture order.");
            last = row.CapturedAt;
            if (scope != (row.Generation, row.RoomId))
            {
                origins.Clear();
                scope = (row.Generation, row.RoomId);
            }
            while (cursor < orderedInputs.Length && orderedInputs[cursor].Timestamp <= row.CapturedAt)
            {
                var edge = orderedInputs[cursor++];
                if (edge.IsPressed) held.Add(edge.Button);
                else held.Remove(edge.Button);
            }
            var drive = row.DriveDirection ?? ((held.Contains("right") ? 1 : 0) - (held.Contains("left") ? 1 : 0));
            var pose = row.BlockadePose;
            var grounded = row.Grounded;
            var island = ResolveVisual(row, ref pose);
            if (domain == BotCaptureDomain.Hacker && pose is null && row.BlockadeVersion is null && source is not null)
            {
                var match = source.Hacker(row);
                pose = match.Pose;
                grounded ??= match.Grounded;
            }
            if (pose is { IsValid: true } && domain == BotCaptureDomain.Hacker && pose.Space.StartsWith("hacker/", StringComparison.Ordinal))
                verified.Add(pose.Space);
            if (pose is { IsValid: true, Width: > 1 } && domain == BotCaptureDomain.Gameplay && row.BlockadeVersion == 1
                && pose.Space.StartsWith(FormattableString.Invariant($"visual/{row.RoomId}/"), StringComparison.Ordinal))
                verified.Add(pose.Space);
            if (island is { IsPlaced: true } placed && pose?.Space == placed.Space)
            {
                verified.Add(placed.Space);
                origins[placed.Space] = placed.WorldFromLocal;
            }
            var sample = row with { DriveDirection = drive, Grounded = grounded };
            var inferred = domain == BotCaptureDomain.Gameplay && source is not null
                ? visit.ObserveRecorded(sample, pose, space => island is { IsPlaced: true } current
                    && origins.TryGetValue(space, out var owner)
                    ? new AtlasPoint(owner.X - current.WorldFromLocal.X, owner.Y - current.WorldFromLocal.Y) : null)
                : visit.Observe(sample, pose);
            if (row.BlockadeVersion is null && row.Blockade is null && inferred is not null
                && (row.BlockadePose is null || row.DriveDirection is null))
                observations.Add(new(row.Generation, row.CaptureTimestamp, inferred));
        }
        visit.Reset();
        return new(id, observations, marks.ToArray()) { Domain = domain, VerifiedSpaces = verified };

        BotBlockadeReplaySource.Island? ResolveVisual(BotCaptureObservation row, ref BotBlockadePose? pose)
        {
            if (domain != BotCaptureDomain.Gameplay) return null;
            var (owner, hasRecord) = source?.Visual(row) ?? default;
            if (pose is not null || row.BlockadeVersion is not null || row.TerrainSource is not (null or "detected")) return owner;
            if (owner is not null && row.AtlasView is { Width: > 0 and < double.PositiveInfinity } measured)
                pose = new(owner.Space, owner.LocalPose.X, owner.LocalPose.Y, measured.Width);
            else if (!hasRecord)
            {
                var local = FormattableString.Invariant($"clip/{id:D}/{row.RoomId}");
                pose = BotBlockadePose.Visual(row) is { } visual ? visual with { Space = local + "/normalized" }
                    : row.AtlasView is { Camera: [var x, var y], Width: > 0 and < double.PositiveInfinity } view
                        ? new(local, x, y, view.Width) : null;
            }
            return owner;
        }
    }

    public static void WriteCrossed(BotRecoveredBlockades evidence, string botRoot)
    {
        var domain = evidence.Domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        var path = Path.Combine(botRoot, "blockade-map", domain, evidence.Id.ToString("D").ToUpperInvariant() + ".json");
        var marks = evidence.Marks.Where(mark => mark.Crossed && evidence.VerifiedSpaces.Contains(mark.Space)).ToArray();
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(marks, Json));
    }
}
