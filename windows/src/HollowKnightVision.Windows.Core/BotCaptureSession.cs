using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace HollowKnightVision.Windows.Core;

public sealed record BotCaptureManifest(
    int SchemaVersion, [property: JsonConverter(typeof(CaptureIdConverter))] Guid Id,
    DateTimeOffset CreatedAt, double StartedAt, string GameMode, string DetectorVersion,
    string InputSource, string Status, double Duration, int ObservationCount, int UsableObservationCount,
    int InputEventCount, int ImageCount, string BotDomain,
    [property: JsonPropertyName("replayID"), JsonConverter(typeof(CaptureIdConverter))] Guid? ReplayId,
    [property: JsonPropertyName("pairedClipID"), JsonConverter(typeof(CaptureIdConverter))] Guid? PairedClipId)
{
    public IReadOnlyList<string> FeatureNames => SchemaVersion == 1
        ? BotRecordingContract.FeatureNames.Take(BotRecordingContract.LegacyWidth).ToArray() : BotRecordingContract.FeatureNames;
    public IReadOnlyList<int> ActionSizes => BotRecordingContract.ActionSizes;
    public int HistoryLength => BotRecordingContract.HistoryLength;
    public double SampleInterval => BotRecordingContract.SampleInterval;
    public string StopReason { get; init; } = "user-stop";
    public bool? ExpertDemonstration { get; init; }
    public bool? RecordedSkillControls { get; init; }
    public int? TerrainAuditCount { get; init; }
    public int? TerrainPairedFrameCount { get; init; }
}

// The shared exporter uses the macOS UUID spelling as its observation session key.
internal sealed class CaptureIdConverter : JsonConverter<Guid>
{
    public CaptureIdConverter() { }
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var id) ? id : throw new JsonException("Invalid capture ID.");
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

public sealed record BotCaptureInput(double Timestamp, string Button, bool IsPressed)
{
    public string Type => "input";
}

public sealed record BotCaptureReviewFrame(string? ImageFile, double Offset, BotScreenTerrain? Terrain, bool Authored,
    IReadOnlyList<double> Features)
{
    public BotRoadHint? PathHint { get; init; }
    public BotBlockadeEvidence? Blockade { get; init; }
    public IReadOnlyList<BotObjectBounds> GeoCaches { get; init; } = [];
    public IReadOnlyList<BotObjectBounds> LifebloodCocoons { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
    public bool HasMaskedKnightEvidence { get; init; }
}

public sealed record BotCaptureResult(IReadOnlyList<BotCaptureManifest> Published, int Discarded);
public sealed record BotCaptureLibraryEntry(string Folder, BotCaptureManifest Manifest);

public sealed record BotHackerCaptureFrame(HackerMatchedFrame Match, long Generation, int RoomId, string RoomKey,
    BotAtlasObjective? Objective = null, bool AutomaticObjective = false, HackerAtlasRoom? RegisteredRoom = null);

/// <summary>Streams a single run into separate camera domains. An eight-frame queue bounds pending image memory.</summary>
public sealed class BotCaptureSession
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new CaptureDateConverter() }
    };
    private static readonly DateTimeOffset SwiftDateOrigin = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const double SamplingTolerance = .005;
    private static readonly InputButtons[] ButtonBits =
    [
        InputButtons.Left, InputButtons.Right, InputButtons.Up, InputButtons.Down,
        InputButtons.ActionA, InputButtons.ActionZ, InputButtons.ActionX, InputButtons.Inventory, InputButtons.PauseMenu,
        InputButtons.QuickCast, InputButtons.Dash, InputButtons.SuperDash, InputButtons.DreamNail
    ];
    private static readonly IReadOnlySet<string> StandardTrainingButtons =
        new HashSet<string>(["left", "right", "up", "down", "z", "x"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SkillTrainingButtons =
        Enum.GetValues<RecordedGameButton>().Select(button => button.WireName()).ToHashSet(StringComparer.Ordinal);
    private readonly record struct Sample(BgraFrame Image, BotCaptureObservation? Gameplay, BotCaptureObservation? Hacker,
        BotHackerCaptureFrame? MatchedHacker = null);

    private readonly object gate = new();
    private readonly string root;
    private readonly string staging;
    private readonly string gameMode;
    private readonly string detector;
    private readonly Guid? replay;
    private readonly string? replayRoot;
    private readonly long generation;
    private readonly DateTimeOffset created;
    private readonly Channel<Sample> pending = Channel.CreateBounded<Sample>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Clip gameplay;
    private readonly Clip? hacker;
    private readonly BotHackerVisit? hackerVisit;
    private readonly Func<double> observationClock;
    private readonly Task writer;
    private readonly List<BotCaptureInput> nativeInput = [];
    private double lastSample = double.NegativeInfinity;
    private double lastHackerSample = double.NegativeInfinity;
    private double lastAvailable = double.NegativeInfinity;
    private InputButtons held;
    private bool finished;

    public double StartedAt { get; }
    public GroundTruthLineDocument Authored { get; }
    public bool CapturesHacker => hacker is not null;

    public BotCaptureSession(string root, string mode, string detectorVersion, long generation, double startedAt,
        GroundTruthLineDocument authored, Guid? replayId = null, bool paired = false, string? replayRoot = null,
        AtlasPathLinkStore? hackerPathLinks = null, Func<double>? observationClock = null)
    {
        if (!double.IsFinite(startedAt) || startedAt < 0) throw new ArgumentOutOfRangeException(nameof(startedAt));
        if (paired && replayId is null) throw new ArgumentException("Paired observations require the matching input path.");
        this.root = Path.GetFullPath(root);
        StartedAt = startedAt;
        this.generation = generation;
        gameMode = mode;
        detector = detectorVersion;
        replay = replayId;
        this.replayRoot = replayRoot is null ? null : Path.GetFullPath(replayRoot);
        Authored = authored.Clone();
        hackerVisit = paired ? new(Authored, hackerPathLinks) : null;
        this.observationClock = observationClock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        created = DateTimeOffset.UtcNow;
        staging = Path.Combine(this.root, $".pending-demo-{Guid.NewGuid():N}");

        Clip? visual = null;
        Clip? authoredClip = null;
        try
        {
            visual = new Clip(staging, paired ? Guid.NewGuid() : replayId ?? Guid.NewGuid(), "gameplay", trackTraining: true);
            if (paired) authoredClip = new Clip(staging, replayId!.Value, "hacker", trackTraining: true);
            gameplay = visual;
            hacker = authoredClip;
            if (hacker is not null)
                new GroundTruthLineStore(Path.Combine(hacker.Directory, "authored-ground.json")).Save(Authored);
            writer = Task.Run(WriteAsync);
        }
        catch
        {
            visual?.Dispose();
            authoredClip?.Dispose();
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public bool WantsFrame(long captureGeneration, double timestamp)
    {
        lock (gate) return CanTake(captureGeneration, timestamp, lastSample);
    }

    public bool TryQueueHacker(BotHackerCaptureFrame frame)
    {
        var match = frame.Match;
        var timestamp = match.Capture.CapturedAt;
        lock (gate)
        {
            if (hacker is null || match.Sample.AtlasGameplayActive != true
                || match.Capture.UnityFrame != HackerFrameSynchronizer.Key(match.Sample.UnityFrame)
                || !CanTake(frame.Generation, timestamp, lastHackerSample)
                || !pending.Writer.TryWrite(new(match.Capture.Image, null, null, frame))) return false;
            lastHackerSample = timestamp;
            return true;
        }
    }

    public bool TryRecord(BgraFrame image, BotCaptureObservation visual, BotCaptureObservation? exact)
    {
        lock (gate)
        {
            if (!CanTake(visual.Generation, visual.CaptureTimestamp, lastSample)
                || visual.TerrainSource != "detected"
                || exact is not null && (hacker is null || exact.TerrainSource != "human-authored"
                    || exact.CaptureTimestamp != visual.CaptureTimestamp || exact.Generation != visual.Generation
                    || exact.AvailableAt != visual.AvailableAt
                    || !CanTake(exact.Generation, exact.CaptureTimestamp, lastHackerSample))) return false;
            if (!pending.Writer.TryWrite(new(image, visual, exact))) return false;
            lastSample = visual.CaptureTimestamp;
            if (exact is not null) lastHackerSample = exact.CaptureTimestamp;
            if (double.IsFinite(visual.AvailableAt)) lastAvailable = Math.Max(lastAvailable, visual.AvailableAt);
            return true;
        }
    }

    public void ObserveNativeInput(InputButtons? input, double time)
    {
        lock (gate)
        {
            if (finished || replay is not null || !double.IsFinite(time) || time < StartedAt) return;
            var next = input ?? InputButtons.None;
            AppendInputEdges(nativeInput, held, next, time);
            held = next;
        }
    }

    public async Task<BotCaptureResult> FinishAsync(double stoppedAt, RecordedInputPath? path, bool completed)
    {
        lock (gate)
        {
            if (finished) throw new InvalidOperationException("This capture has already ended.");
            finished = true;
            pending.Writer.TryComplete();
        }
        var moved = new List<string>();
        try
        {
            await writer.ConfigureAwait(false);
            if (double.IsFinite(stoppedAt)) stoppedAt = Math.Max(stoppedAt, lastAvailable);
            if (!completed || !double.IsFinite(stoppedAt) || stoppedAt < StartedAt
                || replay is { } expected && path?.Id != expected)
                return new([], hacker is null ? 1 : 2);
            var events = path is null ? nativeInput.Where(e => e.Timestamp <= stoppedAt).ToArray()
                : path.Events.Select(e => new BotCaptureInput(StartedAt + e.Offset, e.Button.WireName(),
                    e.Transition == RecordedInputTransition.Pressed)).Where(e => e.Timestamp <= stoppedAt + .001).ToArray();
            var orderedEvents = events.OrderBy(input => input.Timestamp).ToArray();
            var recordedSkillControls = path?.RuntimeMetadata?.GetValueOrDefault(RecordedInputPath.SkillControlsMetadata) == "true";
            var supportedButtons = recordedSkillControls ? SkillTrainingButtons : StandardTrainingButtons;
            var clips = new[] { gameplay, hacker }.OfType<Clip>().ToArray();
            var eligible = clips.Where(clip => clip.HasTrainableActionWindow(orderedEvents, supportedButtons, stoppedAt)).ToArray();
            var manifests = new List<BotCaptureManifest>();
            foreach (var clip in eligible)
            {
                foreach (var input in events) clip.Append(input);
                clip.Close();
                var other = eligible.FirstOrDefault(item => item.Id != clip.Id);
                var manifest = new BotCaptureManifest(BotRecordingContract.SchemaVersion,
                    clip.Id, created, StartedAt, gameMode, clip.Domain == "hacker" ? "receiver-actors-v1" : detector,
                    replay is null ? "native-posted" : "receiver-sent", "completed",
                    stoppedAt - StartedAt, clip.Count, clip.UsableCount, events.Length, clip.ImageCount, clip.Domain,
                    replay, other?.Id)
                {
                    RecordedSkillControls = recordedSkillControls
                };
                AtomicFile.Write(Path.Combine(clip.Directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                if (path?.VisualFrameArchive is not null && replayRoot is not null)
                {
                    var reference = Path.Combine(clip.Directory, "authored-ground.json");
                    if (clip.Domain == "gameplay") new GroundTruthLineStore(reference).Save(Authored);
                    var referenceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reference))).ToLowerInvariant();
                    AtomicFile.Write(Path.Combine(clip.Directory, "audit-context.json"), JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            schemaVersion = 1, kind = "human-demonstration", domain = clip.Domain, pathID = path.Id,
                            replayRoot, referenceCoverage = "positive-authored-segments-only", authoredGroundSHA256 = referenceHash
                        }, Json));
                }
                manifests.Add(manifest);
            }
            foreach (var clip in eligible)
            {
                var destination = Path.Combine(root, DirectoryName(clip.Id));
                Directory.Move(clip.Directory, destination);
                moved.Add(destination);
            }
            return new(manifests, clips.Length - eligible.Length);
        }
        catch
        {
            foreach (var folder in moved) Directory.Delete(folder, recursive: true);
            throw;
        }
        finally
        {
            gameplay.Dispose();
            hacker?.Dispose();
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static string DirectoryName(Guid id) => $"demo-{id:D}";

    public static IReadOnlyList<BotCaptureManifest> Library(string root) =>
        ResolveLibraryDomains(ReadLibraryManifests(root));

    public static IReadOnlyList<BotCaptureLibraryEntry> ReadLibraryManifests(string root)
    {
        if (!Directory.Exists(root)) return [];
        var captures = new List<BotCaptureLibraryEntry>();
        foreach (var folder in Directory.EnumerateDirectories(root, "demo-*"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<BotCaptureManifest>(
                    File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json);
                if (manifest is { Status: "completed", SchemaVersion: 1 or BotRecordingContract.SchemaVersion }
                    && manifest.ExpertDemonstration != false
                    && string.Equals(Path.GetFileName(folder), DirectoryName(manifest.Id), StringComparison.OrdinalIgnoreCase))
                    captures.Add(new(folder, manifest));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
        return captures.OrderByDescending(item => item.Manifest.CreatedAt).ToArray();
    }

    public static IReadOnlyList<BotCaptureManifest> ResolveLibraryDomains(IReadOnlyList<BotCaptureLibraryEntry> entries)
    {
        var captures = new List<BotCaptureManifest>(entries.Count);
        foreach (var (folder, manifest) in entries)
        {
            if (manifest.BotDomain is not null)
            {
                captures.Add(manifest);
                continue;
            }
            try
            {
                var domain = BotRecordedAtlasPath.ReadCapture(folder).Domain;
                captures.Add(manifest with { BotDomain = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay" });
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return captures;
    }

    internal sealed record ReviewObservation(long Generation, BotCaptureReviewFrame Frame);

    public static IReadOnlyList<BotCaptureReviewFrame> Review(string folder, CancellationToken cancellationToken = default) =>
        ReadReviewEvidence(folder, cancellationToken, requireImage: true).Observations.Select(item => item.Frame).ToArray();

    internal static (BotCaptureManifest Manifest, IReadOnlyList<ReviewObservation> Observations, IReadOnlyList<BotCaptureInput> Inputs) ReadReviewEvidence(
        string folder, CancellationToken cancellationToken, bool requireImage = false, string? replayRoot = null)
    {
        var manifest = JsonSerializer.Deserialize<BotCaptureManifest>(File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json)
            ?? throw new InvalidDataException("The capture has no manifest.");
        if (manifest is not { Status: "completed", SchemaVersion: 1 or BotRecordingContract.SchemaVersion }) return (manifest, [], []);
        var frames = new List<ReviewObservation>();
        var observations = new List<BotCaptureObservation>();
        var inputs = new List<BotCaptureInput>();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        using var events = File.OpenRead(Path.Combine(folder, "events.jsonl"));
        using var reader = new StreamReader(events);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) continue;
            if (!requireImage && type.GetString() == "input")
            {
                if (row.TryGetProperty("timestamp", out var time) && time.ValueKind == JsonValueKind.Number
                    && time.TryGetDouble(out var seconds) && double.IsFinite(seconds)
                    && row.TryGetProperty("button", out var button) && button.ValueKind == JsonValueKind.String
                    && button.GetString() is { Length: > 0 } name && row.TryGetProperty("isPressed", out var state)
                    && state.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    inputs.Add(new(seconds, name, state.GetBoolean()));
                continue;
            }
            if (type.GetString() != "observation") continue;
            var image = PreviewPath(row);
            if (requireImage && image is null || row.Deserialize<BotCaptureObservation>(Json) is not { } observation) continue;
            var offset = observation.CaptureTimestamp - manifest.StartedAt;
            if (!double.IsFinite(offset)) continue;
            var authored = manifest.BotDomain == "hacker"
                || manifest.BotDomain is null && observation.TerrainSource == "human-authored";
            var terrain = authored ? observation.AuthoredTerrain : observation.DetectedTerrain;
            frames.Add(new(observation.Generation, new(image, Math.Max(0, offset), terrain, authored, observation.Features ?? [])
            {
                PathHint = observation.HackerPathVersion == 1 || observation.AtlasPathVersion == 1 ? observation.RoadHint : null,
                Blockade = observation.Blockade,
                GeoCaches = observation.GeoCaches ?? [],
                LifebloodCocoons = observation.LifebloodCocoons ?? [],
                Issues = observation.Issues ?? [],
                HasMaskedKnightEvidence = observation.Features is not null && observation.HasMaskedKnightEvidence
            }));
            if (!requireImage) observations.Add(observation);
        }
        if (!requireImage && observations.Count == manifest.ObservationCount)
            ApplyRecoveredAnnotations();
        return (manifest, frames, inputs);

        void ApplyRecoveredAnnotations()
        {
            var hints = observations.Any(row => row.HackerPathVersion != 1 && row.AtlasPathVersion != 1)
                ? BotClipPathSidecar.Read(folder, events, manifest, observations, cancellationToken) : [];
            var blockades = new Dictionary<(long Generation, double CaptureTimestamp), BotBlockadeEvidence>();
            if (inputs.Count == manifest.InputEventCount && observations.Any(row => row.DriveDirection is null
                || row is { BlockadeVersion: null, BlockadePose: null, TerrainSource: "detected" }))
            {
                try
                {
                    var recovered = BotBlockadeReplay.Recover(folder, manifest.Id, manifest.ReplayId, manifest.BotDomain,
                        observations, inputs, replayRoot, cancellationToken);
                    foreach (var row in recovered.Rows)
                        blockades[(row.Generation, row.CaptureTimestamp)] = row.Blockade;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
            }
            for (var index = 0; index < observations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = observations[index];
                var hint = row.HackerPathVersion != 1 && row.AtlasPathVersion != 1 ? hints.GetValueOrDefault(index) : null;
                var blockade = blockades.GetValueOrDefault((row.Generation, row.CaptureTimestamp));
                if (hint is null && blockade is null) continue;
                var frame = frames[index];
                frames[index] = frame with { Frame = frame.Frame with
                {
                    PathHint = frame.Frame.PathHint ?? hint,
                    Blockade = frame.Frame.Blockade ?? blockade
                } };
            }
        }

        string? PreviewPath(JsonElement row)
        {
            if (!row.TryGetProperty("imagePath", out var node) || node.ValueKind != JsonValueKind.String
                || node.GetString() is not { Length: > 0 } relative || Path.IsPathRooted(relative)
                || relative.Split('/', '\\').Contains("..")) return null;
            var image = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return image.StartsWith(fullRoot, StringComparison.Ordinal) && File.Exists(image) ? image : null;
        }
    }

    private bool CanTake(long captureGeneration, double timestamp, double previous) => !finished && !writer.IsCompleted
        && generation == captureGeneration && double.IsFinite(timestamp) && timestamp >= StartedAt
        && timestamp - previous >= BotRecordingContract.SampleInterval - SamplingTolerance;

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var sample in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Both domains archive the same pixels. Reuse the already encoded PNG when their sampling coincides.
                var preview = sample.Gameplay is { } visual ? gameplay.Record(visual, sample.Image) : null;
                var exact = sample.MatchedHacker is { } matched
                    ? hackerVisit!.ObserveMatched(matched, observationClock()) : sample.Hacker;
                if (exact is null) continue;
                hacker!.Record(exact, sample.Image, preview);
                if (sample.MatchedHacker is not null && double.IsFinite(exact.AvailableAt))
                {
                    lock (gate) lastAvailable = Math.Max(lastAvailable, exact.AvailableAt);
                }
            }
        }
        catch (Exception error)
        {
            pending.Writer.TryComplete(error);
            throw;
        }
    }

    internal static void AppendInputEdges(List<BotCaptureInput> inputs, InputButtons prior, InputButtons current, double time)
    {
        var changed = current ^ prior;
        foreach (var bit in ButtonBits)
            if ((changed & bit) != 0 && bit.ToRecordedGameButton() is { } button)
                inputs.Add(new(time, button.WireName(), (current & bit) != 0));
    }

    private sealed class CaptureDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var date)) return date;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var seconds) && double.IsFinite(seconds))
            {
                // Earlier Windows clips wrote seconds since Apple's 2001 reference date.
                try { return SwiftDateOrigin.AddSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { }
            }
            throw new JsonException("Invalid bot capture date.");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    internal sealed class Clip : IDisposable
    {
        internal Guid Id { get; }
        internal string Directory { get; }
        internal string Domain { get; }
        internal int Count { get; private set; }
        internal int UsableCount { get; private set; }
        internal int ImageCount { get; private set; }
        private readonly List<double>? trainingCandidateTimes;
        private readonly StreamWriter stream;
        private (long Generation, int Room, double SeenAt)? trainingVisualAnchor;
        private double lastImage = double.NegativeInfinity;
        private double lastAvailable = double.NegativeInfinity;
        private bool closed;

        internal Clip(string staging, Guid id, string domain, bool trackTraining = false)
        {
            Id = id;
            Domain = domain;
            trainingCandidateTimes = trackTraining ? [] : null;
            Directory = Path.Combine(staging, DirectoryName(id));
            System.IO.Directory.CreateDirectory(Directory);
            stream = new StreamWriter(new FileStream(Path.Combine(Directory, "events.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
        }

        internal string? Record(BotCaptureObservation observation, BgraFrame image, string? encoded = null)
        {
            if (!double.IsFinite(observation.AvailableAt) || observation.AvailableAt < observation.CapturedAt
                || observation.AvailableAt <= lastAvailable) return null;
            // The trainer validates the recording as a whole before selecting examples.
            if (trainingCandidateTimes is not null && (!double.IsFinite(observation.CapturedAt)
                || observation.Generation < 0 || observation.RoomId < 0
                || observation.Features.Count != BotRecordingContract.FeatureNames.Count
                || observation.Features.Any(value => !double.IsFinite(value) || Math.Abs(value) > 4))) return null;
            string? path = null;
            if (observation.AvailableAt - lastImage >= 1)
            {
                var relative = $"frames/{Count}.png";
                path = Path.Combine(Directory, relative);
                if (encoded is null) PngWriter.Write(image, path);
                else
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(encoded, path);
                }
                observation = observation with { ImagePath = relative };
                lastImage = observation.AvailableAt;
                ImageCount++;
            }
            Append(observation);
            lastAvailable = observation.AvailableAt;
            Count++;
            if (observation.Issues.Count == 0) UsableCount++;
            if (trainingCandidateTimes is not null && HasTrainablePerception(observation))
                trainingCandidateTimes.Add(observation.AvailableAt);
            return path;
        }

        private bool HasTrainablePerception(BotCaptureObservation observation)
        {
            var masked = Domain == "gameplay" && observation.HasMaskedKnightEvidence;
            var predicted = observation.KnightSource == "visual-predicted";
            var valid = observation.CanTrain && BotObservationHistory.IsFresh(observation, observation.AvailableAt)
                && observation.Features[6] == 1 && (masked || observation.Features[4] >= (predicted ? .2 : .5))
                && (!predicted || observation.KnightPrediction is { } estimate && trainingVisualAnchor is { } anchor
                    && anchor.Generation == observation.Generation && anchor.Room == observation.RoomId
                    && Math.Abs(anchor.SeenAt - estimate.LastSeenAt) <= 1e-6);

            // A live prediction may refer to an image captured before K was pressed.
            // The exporter requires that measurement to be present in this clip.
            if (!valid || masked) trainingVisualAnchor = null;
            else if (observation.KnightSource == "visual" && observation.DetectionTimestamp is { } seenAt)
                trainingVisualAnchor = (observation.Generation, observation.RoomId, seenAt);
            return valid;
        }

        internal bool HasTrainableActionWindow(IReadOnlyList<BotCaptureInput> inputs, IReadOnlySet<string> supported,
            double stoppedAt)
        {
            if (trainingCandidateTimes is not { Count: > 0 } times) return false;
            var held = new HashSet<string>(StringComparer.Ordinal);
            var window = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var start in times)
            {
                var end = start + BotRecordingContract.SampleInterval;
                if (end > stoppedAt) break;
                while (index < inputs.Count && inputs[index].Timestamp <= start)
                {
                    var edge = inputs[index++];
                    if (edge.IsPressed) held.Add(edge.Button);
                    else held.Remove(edge.Button);
                }

                // Match the shared trainer's held controls and taps in [start, end).
                window.Clear();
                window.UnionWith(held);
                for (var tap = index; tap < inputs.Count && inputs[tap].Timestamp < end; tap++)
                    if (inputs[tap].IsPressed) window.Add(inputs[tap].Button);

                if (window.IsSubsetOf(supported)
                    && !(window.Contains("left") && window.Contains("right"))
                    && !(window.Contains("up") && window.Contains("down"))) return true;
            }
            return false;
        }

        internal void Append<T>(T record) => stream.WriteLine(JsonSerializer.Serialize(record, Json));

        internal void Close()
        {
            if (closed) return;
            stream.Flush();
            if (stream.BaseStream is FileStream file) file.Flush(flushToDisk: true);
            Dispose();
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            stream.Dispose();
        }
    }
}
