using System.Security.Cryptography;
using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

internal static class BotClipPathSidecar
{
    private sealed record Document(int SchemaVersion, Guid Id, Guid ReplayId, string EventsSha256, IReadOnlyList<Row> Rows);
    private sealed record Row(int Index, long Generation, double CaptureTimestamp, int HackerPathVersion, BotRoadHint? RoadHint);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Dictionary<int, BotRoadHint> Read(string folder, Stream events, BotCaptureManifest manifest,
        IReadOnlyList<BotCaptureObservation> observations, CancellationToken cancellationToken)
    {
        var root = Directory.GetParent(Path.GetFullPath(folder))?.Parent;
        if (root is null) return [];
        var path = Path.Combine(root.FullName, "exit-paths-v1", $"{BotCaptureSession.DirectoryName(manifest.Id)}.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var sidecar = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<Document>(sidecar, Json);
            if (document is not { SchemaVersion: 1, Rows: not null } || document.Id != manifest.Id
                || document.ReplayId != (manifest.ReplayId ?? manifest.Id)) return [];
            cancellationToken.ThrowIfCancellationRequested();
            events.Position = 0;
            var checksum = Convert.ToHexString(SHA256.HashData(events));
            if (!string.Equals(document.EventsSha256, checksum, StringComparison.OrdinalIgnoreCase)) return [];

            var hints = new Dictionary<int, BotRoadHint>();
            foreach (var row in document.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row is not { HackerPathVersion: 1, RoadHint: { } hint } || row.Index < 0 || row.Index >= observations.Count
                    || !double.IsFinite(hint.Dx) || !double.IsFinite(hint.Dy)) continue;
                var observation = observations[row.Index];
                if (observation.Generation == row.Generation && observation.CaptureTimestamp == row.CaptureTimestamp)
                    hints[row.Index] = hint;
            }
            return hints;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
