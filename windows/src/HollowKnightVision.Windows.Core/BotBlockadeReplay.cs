using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record BotRecoveredBlockade(long Generation, double CaptureTimestamp, BotBlockadeEvidence Blockade);
public sealed record BotRecoveredBlockades(Guid Id, IReadOnlyList<BotRecoveredBlockade> Rows);

/// <summary>Replays eligible saved measurements in clock order without changing the clip.</summary>
public static class BotBlockadeReplay
{
    private sealed record Identity(Guid Id);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BotRecoveredBlockades Recover(string folder)
    {
        var manifest = JsonSerializer.Deserialize<Identity>(File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json);
        if (manifest is null || manifest.Id == Guid.Empty) throw new InvalidDataException("The capture identity is missing.");
        var eventsPath = Path.Combine(folder, "events.jsonl");
        var inputs = new List<BotCaptureInput>();
        foreach (var line in File.ReadLines(eventsPath))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("type").GetString() != "input") continue;
            var input = record.RootElement.Deserialize<BotCaptureInput>(Json);
            if (input is not null && double.IsFinite(input.Timestamp)) inputs.Add(input);
        }
        var orderedInputs = inputs.OrderBy(input => input.Timestamp).ToArray();
        var held = new HashSet<string>(StringComparer.Ordinal);
        var observations = new List<BotRecoveredBlockade>();
        var cursor = 0;
        var last = double.NegativeInfinity;
        var visit = new BotBlockadeVisit();
        foreach (var line in File.ReadLines(eventsPath))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.GetProperty("type").GetString() != "observation") continue;
            var row = record.RootElement.Deserialize<BotCaptureObservation>(Json);
            if (row is null || !double.IsFinite(row.CapturedAt) || row.CapturedAt <= last)
                throw new InvalidDataException("Bot capture observations must be in capture order.");
            last = row.CapturedAt;
            while (cursor < orderedInputs.Length && orderedInputs[cursor].Timestamp <= row.CapturedAt)
            {
                var edge = orderedInputs[cursor++];
                if (edge.IsPressed) held.Add(edge.Button);
                else held.Remove(edge.Button);
            }
            var drive = row.DriveDirection ?? ((held.Contains("right") ? 1 : 0) - (held.Contains("left") ? 1 : 0));
            var pose = row.BlockadePose;
            if (pose is null && row.BlockadeVersion is null && row.TerrainSource == "detected")
                pose = BotBlockadePose.Visual(row);
            var inferred = visit.Observe(row with { DriveDirection = drive }, pose);
            if (row.BlockadeVersion is null && row.Blockade is null && inferred is not null
                && (row.BlockadePose is null || row.DriveDirection is null))
                observations.Add(new(row.Generation, row.CaptureTimestamp, inferred));
        }
        return new(manifest.Id, observations);
    }
}
