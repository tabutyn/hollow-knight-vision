using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

/// <summary>Serves a single no-save Gym episode over the shared JSON-lines protocol.</summary>
public static class HackerGymObservationEncoder
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
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
        var route = new GymHackerRoute(labelsPath, connectionsPath, fixedObjective);
        var history = new BotObservationHistory(version);
        var lastTicks = -1;
        Guid? episode = null;
        while (input.ReadLine() is { } line)
        {
            var request = Read<Request>(line);
            var action = VerifyFrame(request.Sample, request.Previous, request.Ticks, ref lastTicks, ref episode);
            if (request.Health is < 0 or > 32 || !request.Sample.HasFiniteCoordinates())
                throw new InvalidDataException("Invalid Gym telemetry.");
            var now = request.Ticks / 60d;
            var row = route.Observe(request.Sample, request.Ticks, request.Health, action);
            if (row is null) history.Reset();
            var data = row is null ? null : history.Append(row, action, now);
            Write(output, new Reply(row?.Issues ?? ["missing-camera"], route.ObjectiveReachable,
                Encode(data), row?.PathProgress, row?.PathAlignment));
        }
    }

    internal static T Read<T>(string line) where T : class
    {
        if (line.Length > ReceiverProtocol.MaximumLineLength)
            throw new InvalidDataException("The Gym telemetry line is too large.");
        return JsonSerializer.Deserialize<T>(line, Json) ?? throw new InvalidDataException("Missing Gym request.");
    }

    internal static BotPolicyAction VerifyFrame(ReceiverGroundTruthSample? sample, int[]? previous, int ticks,
        ref int lastTicks, ref Guid? episode)
    {
        if (sample is null || sample.Version != ReceiverProtocol.ControlVersion || sample.Type != "groundTruth"
            || sample.SessionId == Guid.Empty || episode.HasValue && episode.Value != sample.SessionId
            || ticks <= lastTicks || ticks > 10_000_000)
            throw new InvalidDataException("Invalid Gym episode identity or frame ordering.");
        if (previous is not { Length: 4 }
            || previous.Where((action, index) => action < 0 || action >= BotRecordingContract.ActionSizes[index]).Any())
            throw new InvalidDataException("Invalid previous Gym action.");
        episode = sample.SessionId;
        lastTicks = ticks;
        return new(previous[0], previous[1], previous[2], previous[3]);
    }

    internal static string? Encode(float[]? vector) =>
        vector is null ? null : Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan()));

    internal static void Write<T>(TextWriter output, T reply)
    {
        output.WriteLine(JsonSerializer.Serialize(reply, Json));
        output.Flush();
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


/// <summary>Owns the authored route used by both observation domains in one Gym episode.</summary>
internal sealed class GymHackerRoute
{
    private readonly GroundTruthLineDocument labels;
    private readonly BotHackerVisit visit;
    private readonly Dictionary<string, int> rooms = new(StringComparer.Ordinal);

    internal bool ObjectiveReachable => visit.ObjectiveReachable;

    internal GymHackerRoute(string labelsPath, string? connectionsPath, AtlasPoint? fixedObjective)
    {
        if (!File.Exists(labelsPath))
            throw new FileNotFoundException("The Gym ground labels are missing.", labelsPath);
        var linksFile = connectionsPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(labelsPath))!, AtlasPathLinkStore.Filename);
        if (connectionsPath is not null && !File.Exists(linksFile))
            throw new FileNotFoundException("The frozen Gym path connections are missing.", linksFile);
        var links = new AtlasPathLinkStore(linksFile);
        if (links.Issue is { } linkIssue) throw new InvalidDataException(linkIssue);
        labels = new GroundTruthLineStore(labelsPath).Load();
        visit = new BotHackerVisit(labels, links, fixedObjective);
    }

    internal BotCaptureObservation? Observe(ReceiverGroundTruthSample sample, int ticks, int? health, BotPolicyAction previous)
    {
        if (sample.ProjectionPixelWidth is not (>= 2 and <= 8192)
            || sample.ProjectionPixelHeight is not (>= 2 and <= 8192) || string.IsNullOrWhiteSpace(sample.SceneName))
        {
            visit.Interrupt();
            return null;
        }
        var now = ticks / 60d;
        var size = new WorldSize(sample.ProjectionPixelWidth.Value, sample.ProjectionPixelHeight.Value);
        if (!rooms.TryGetValue(sample.SceneName, out var room)) rooms[sample.SceneName] = room = rooms.Count + 1;
        var timing = new BotCaptureTiming(now, now, 1, room, 0);
        var row = BotObservationBuilder.Hacker(size, HackerFrameSynchronizer.Key(sample.UnityFrame), sample, labels, timing, previous.Buttons);
        if (row is null)
        {
            visit.Interrupt();
            return null;
        }
        return visit.Extend(row, sample, size, ticks, health);
    }
}
