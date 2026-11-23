using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

/// <summary>Serves a single no-save Gym episode over the shared JSON-lines protocol.</summary>
public static class HackerGymObservationEncoder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Request(ReceiverGroundTruthSample Sample, int[] Previous, int Health, int Ticks);
    private sealed record Reply(IReadOnlyList<string> Issues, bool PathReachable,
        string? Input = null, double? PathProgress = null, double? PathAlignment = null);

    public static void Run(int version, string labelsPath, TextReader input, TextWriter output,
        string? connectionsPath = null, AtlasPoint? fixedObjective = null)
    {
        if (BotPolicyContract.Names(version) is null)
            throw new InvalidDataException("Unknown bot observation schema.");
        if (!File.Exists(labelsPath))
            throw new FileNotFoundException("The Hacker Gym ground labels are missing.", labelsPath);

        var linksFile = connectionsPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(labelsPath))!, AtlasPathLinkStore.Filename);
        if (connectionsPath is not null && !File.Exists(linksFile))
            throw new FileNotFoundException("The frozen Gym path connections are missing.", linksFile);
        var links = new AtlasPathLinkStore(linksFile);
        if (links.Issue is { } linkIssue) throw new InvalidDataException(linkIssue);

        var labels = new GroundTruthLineStore(labelsPath).Load();
        var visit = new BotHackerVisit(labels, links, fixedObjective);
        var history = new BotObservationHistory(version);
        var rooms = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastTicks = -1;
        Guid? episode = null;
        while (input.ReadLine() is { } line)
        {
            if (line.Length > ReceiverProtocol.MaximumLineLength)
                throw new InvalidDataException("The Gym telemetry line is too large.");
            var request = JsonSerializer.Deserialize<Request>(line, Json);
            if (request?.Sample is not { } sample || sample.Version != ReceiverProtocol.ControlVersion
                || sample.Type != "groundTruth" || sample.SessionId == Guid.Empty
                || episode.HasValue && episode.Value != sample.SessionId || request.Ticks <= lastTicks
                || request.Health is < 0 or > 32 || !sample.HasFiniteCoordinates())
                throw new InvalidDataException("Invalid Gym episode identity, frame ordering, or telemetry.");
            if (request.Previous is not { Length: 4 } previous
                || previous.Where((action, index) => action < 0 || action >= BotRecordingContract.ActionSizes[index]).Any())
                throw new InvalidDataException("Invalid previous Gym action.");
            episode = sample.SessionId;
            lastTicks = request.Ticks;
            var action = new BotPolicyAction(previous[0], previous[1], previous[2], previous[3]);
            var now = request.Ticks / 60d;
            var reply = Observe(sample, request.Ticks, now, request.Health, action);
            output.WriteLine(JsonSerializer.Serialize(reply, Json));
            output.Flush();
        }

        Reply Observe(ReceiverGroundTruthSample sample, int ticks, double now, int health, BotPolicyAction previous)
        {
            if (sample.ProjectionPixelWidth is not (>= 2 and <= 8192)
                || sample.ProjectionPixelHeight is not (>= 2 and <= 8192) || string.IsNullOrWhiteSpace(sample.SceneName))
            {
                visit.Interrupt();
                history.Reset();
                return new(["missing-camera"], false);
            }
            var size = new WorldSize(sample.ProjectionPixelWidth.Value, sample.ProjectionPixelHeight.Value);
            if (!rooms.TryGetValue(sample.SceneName, out var room)) rooms[sample.SceneName] = room = rooms.Count + 1;
            var timing = new BotCaptureTiming(now, now, 1, room, 0);
            var row = BotObservationBuilder.Hacker(size, HackerFrameSynchronizer.Key(sample.UnityFrame), sample, labels, timing, previous.Buttons);
            if (row is null)
            {
                visit.Interrupt();
                history.Reset();
                return new(["missing-camera"], false);
            }
            row = visit.Extend(row, sample, size, ticks, health);
            var vector = history.Append(row, previous, now);
            var data = vector is null ? null : Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()));
            return new(row.Issues, visit.ObjectiveReachable, data, row.PathProgress, row.PathAlignment);
        }
    }

    internal static AtlasPoint? FixedObjective(string? x, string? y)
    {
        if (x is null && y is null) return null;
        if (!double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            || !double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
            || !double.IsFinite(px) || !double.IsFinite(py))
            throw new InvalidDataException("Invalid fixed Gym exit objective.");
        return new(px, py);
    }
}
