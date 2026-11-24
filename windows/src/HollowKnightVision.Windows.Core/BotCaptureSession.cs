using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace HollowKnightVision.Windows.Core;

public sealed record BotCaptureManifest(
    int SchemaVersion, Guid Id, DateTimeOffset CreatedAt, double StartedAt, string GameMode, string DetectorVersion,
    string InputSource, string Status, double Duration, int ObservationCount, int UsableObservationCount,
    int InputEventCount, int ImageCount, string BotDomain,
    [property: JsonPropertyName("replayID")] Guid? ReplayId,
    [property: JsonPropertyName("pairedClipID")] Guid? PairedClipId)
{
    public IReadOnlyList<string> FeatureNames => BotRecordingContract.FeatureNames;
    public IReadOnlyList<int> ActionSizes => BotRecordingContract.ActionSizes;
    public int HistoryLength => BotRecordingContract.HistoryLength;
    public double SampleInterval => BotRecordingContract.SampleInterval;
    public string StopReason { get; init; } = "user-stop";
    public bool? ExpertDemonstration { get; init; }
    public int? TerrainAuditCount { get; init; }
    public int? TerrainPairedFrameCount { get; init; }
}

public sealed record BotCaptureInput(double Timestamp, string Button, bool IsPressed)
{
    public string Type => "input";
}

public sealed record BotCaptureReviewFrame(string? ImageFile, double Offset, BotScreenTerrain? Terrain, bool Authored,
    IReadOnlyList<double> Features);

public sealed record BotCaptureResult(IReadOnlyList<BotCaptureManifest> Published, int Discarded);

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
        InputButtons.ActionA, InputButtons.ActionZ, InputButtons.ActionX, InputButtons.Inventory, InputButtons.PauseMenu
    ];
    private readonly record struct Sample(BgraFrame Image, BotCaptureObservation Gameplay, BotCaptureObservation? Hacker);

    private readonly object gate = new();
    private readonly string root;
    private readonly string staging;
    private readonly string gameMode;
    private readonly string detector;
    private readonly Guid? replay;
    private readonly long generation;
    private readonly DateTimeOffset created;
    private readonly Channel<Sample> pending = Channel.CreateBounded<Sample>(new BoundedChannelOptions(8)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Clip gameplay;
    private readonly Clip? hacker;
    private readonly Task writer;
    private readonly List<BotCaptureInput> nativeInput = [];
    private double lastSample = double.NegativeInfinity;
    private double lastAvailable = double.NegativeInfinity;
    private InputButtons held;
    private bool finished;

    public double StartedAt { get; }
    public GroundTruthLineDocument Authored { get; }
    public bool CapturesHacker => hacker is not null;

    public BotCaptureSession(string root, string mode, string detectorVersion, long generation, double startedAt,
        GroundTruthLineDocument authored, Guid? replayId = null, bool paired = false)
    {
        if (!double.IsFinite(startedAt) || startedAt < 0) throw new ArgumentOutOfRangeException(nameof(startedAt));
        if (paired && replayId is null) throw new ArgumentException("Paired observations require the matching input path.");
        this.root = Path.GetFullPath(root);
        StartedAt = startedAt;
        this.generation = generation;
        gameMode = mode;
        detector = detectorVersion;
        replay = replayId;
        Authored = authored.Clone();
        created = DateTimeOffset.UtcNow;
        staging = Path.Combine(this.root, $".pending-demo-{Guid.NewGuid():N}");

        Clip? visual = null;
        Clip? authoredClip = null;
        try
        {
            visual = new Clip(staging, paired ? Guid.NewGuid() : replayId ?? Guid.NewGuid(), "gameplay");
            if (paired) authoredClip = new Clip(staging, replayId!.Value, "hacker");
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
        lock (gate) return CanTake(captureGeneration, timestamp);
    }

    public bool TryRecord(BgraFrame image, BotCaptureObservation visual, BotCaptureObservation? exact)
    {
        lock (gate)
        {
            if (!CanTake(visual.Generation, visual.CaptureTimestamp)
                || visual.TerrainSource != "detected"
                || exact is not null && (hacker is null || exact.TerrainSource != "human-authored"
                    || exact.CaptureTimestamp != visual.CaptureTimestamp || exact.Generation != visual.Generation
                    || exact.AvailableAt != visual.AvailableAt)) return false;
            if (!pending.Writer.TryWrite(new(image, visual, exact))) return false;
            lastSample = visual.CaptureTimestamp;
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
            if (double.IsFinite(stoppedAt)) stoppedAt = Math.Max(stoppedAt, lastAvailable);
            pending.Writer.TryComplete();
        }
        var moved = new List<string>();
        try
        {
            await writer.ConfigureAwait(false);
            if (!completed || !double.IsFinite(stoppedAt) || stoppedAt < StartedAt
                || replay is { } expected && path?.Id != expected)
                return new([], hacker is null ? 1 : 2);
            var events = path is null ? nativeInput.Where(e => e.Timestamp <= stoppedAt).ToArray()
                : path.Events.Select(e => new BotCaptureInput(StartedAt + e.Offset, Name(e.Button),
                    e.Transition == RecordedInputTransition.Pressed)).Where(e => e.Timestamp <= stoppedAt + .001).ToArray();
            var clips = new[] { gameplay, hacker }.OfType<Clip>().ToArray();
            // A training decision requires one full subsequent action window.
            var eligible = clips.Where(clip => clip.FirstTrainableAt is { } time
                && time + BotRecordingContract.SampleInterval <= stoppedAt).ToArray();
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
                    replay, other?.Id);
                AtomicFile.Write(Path.Combine(clip.Directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
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

    public static IReadOnlyList<BotCaptureManifest> Library(string root)
    {
        if (!Directory.Exists(root)) return [];
        var captures = new List<BotCaptureManifest>();
        foreach (var folder in Directory.EnumerateDirectories(root, "demo-*"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<BotCaptureManifest>(
                    File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json);
                if (manifest is { Status: "completed", SchemaVersion: BotRecordingContract.SchemaVersion }
                    && manifest.ExpertDemonstration != false
                    && string.Equals(Path.GetFileName(folder), DirectoryName(manifest.Id), StringComparison.OrdinalIgnoreCase))
                    captures.Add(manifest);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return captures.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    internal sealed record ReviewObservation(long Generation, BotCaptureReviewFrame Frame);

    public static IReadOnlyList<BotCaptureReviewFrame> Review(string folder, CancellationToken cancellationToken = default) =>
        ReadReviewEvidence(folder, cancellationToken, requireImage: true).Observations.Select(item => item.Frame).ToArray();

    internal static (BotCaptureManifest Manifest, IReadOnlyList<ReviewObservation> Observations, IReadOnlyList<BotCaptureInput> Inputs) ReadReviewEvidence(
        string folder, CancellationToken cancellationToken, bool requireImage = false)
    {
        var manifest = JsonSerializer.Deserialize<BotCaptureManifest>(File.ReadAllBytes(Path.Combine(folder, "manifest.json")), Json)
            ?? throw new InvalidDataException("The capture has no manifest.");
        if (manifest is not { Status: "completed", SchemaVersion: BotRecordingContract.SchemaVersion }) return (manifest, [], []);
        var authored = manifest.BotDomain == "hacker";
        var frames = new List<ReviewObservation>();
        var inputs = new List<BotCaptureInput>();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var line in File.ReadLines(Path.Combine(folder, "events.jsonl")))
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
            var terrain = authored ? observation.AuthoredTerrain : observation.DetectedTerrain;
            frames.Add(new(observation.Generation, new(image, Math.Max(0, offset), terrain, authored, observation.Features)));
        }
        return (manifest, frames, inputs);

        string? PreviewPath(JsonElement row)
        {
            if (!row.TryGetProperty("imagePath", out var node) || node.ValueKind != JsonValueKind.String
                || node.GetString() is not { Length: > 0 } relative || Path.IsPathRooted(relative)
                || relative.Split('/', '\\').Contains("..")) return null;
            var image = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return image.StartsWith(fullRoot, StringComparison.Ordinal) && File.Exists(image) ? image : null;
        }
    }

    private bool CanTake(long captureGeneration, double timestamp) => !finished && !writer.IsCompleted
        && generation == captureGeneration && double.IsFinite(timestamp) && timestamp >= StartedAt
        && timestamp - lastSample >= BotRecordingContract.SampleInterval - SamplingTolerance;

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var sample in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // Both domains archive the same pixels. Reuse the already encoded PNG when their sampling coincides.
                var preview = gameplay.Record(sample.Gameplay, sample.Image);
                if (sample.Hacker is { } exact) hacker!.Record(exact, sample.Image, preview);
            }
        }
        catch (Exception error)
        {
            pending.Writer.TryComplete(error);
            throw;
        }
    }

    private static string Name(RecordedGameButton button) => button.ToString().ToLowerInvariant();

    internal static void AppendInputEdges(List<BotCaptureInput> inputs, InputButtons prior, InputButtons current, double time)
    {
        var changed = current ^ prior;
        foreach (var bit in ButtonBits)
            if ((changed & bit) != 0 && bit.ToRecordedGameButton() is { } button)
                inputs.Add(new(time, Name(button), (current & bit) != 0));
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
        internal double? FirstTrainableAt { get; private set; }
        private readonly StreamWriter stream;
        private double lastImage = double.NegativeInfinity;
        private double lastAvailable = double.NegativeInfinity;
        private bool closed;

        internal Clip(string staging, Guid id, string domain)
        {
            Id = id;
            Domain = domain;
            Directory = Path.Combine(staging, DirectoryName(id));
            System.IO.Directory.CreateDirectory(Directory);
            stream = new StreamWriter(new FileStream(Path.Combine(Directory, "events.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
        }

        internal string? Record(BotCaptureObservation observation, BgraFrame image, string? encoded = null)
        {
            if (!double.IsFinite(observation.AvailableAt) || observation.AvailableAt < observation.CapturedAt
                || observation.AvailableAt <= lastAvailable) return null;
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
            if (observation.CanTrain) FirstTrainableAt ??= observation.AvailableAt;
            return path;
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
