using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HollowKnightVision.Windows.Core;

/// <summary>Stores policy playback in its own archive at the observation cadence.</summary>
public sealed class BotEvaluationRecorder
{
    private const string Kind = "bot-evaluation";
    private const int MaximumPendingFrames = 8;
    private sealed record TerrainAuditFrame(double CaptureTimestamp, double CapturedAt, ulong CaptureGeneration,
        int Width, int Height, string? SceneName, bool ReferenceAvailable,
        IReadOnlyList<BotFloor> Detected, IReadOnlyList<BotFloor> Authored);
    private sealed record Sample(BgraFrame Image, BotCaptureObservation? Observation, TerrainAuditFrame Audit,
        VisualReplayGroundInput? GroundInput, int? RenderedFrame);

    private readonly object gate = new();
    private readonly string root, domain, modelName, modelSha256;
    private readonly Guid id = Guid.NewGuid();
    private readonly DateTimeOffset createdAt = DateTimeOffset.UtcNow;
    private readonly GroundTruthLineDocument reference;
    private readonly HashSet<string> labelledScenes;
    private readonly long generation;
    private readonly double startedAt;
    private readonly Channel<Sample> samples = Channel.CreateBounded<Sample>(new BoundedChannelOptions(MaximumPendingFrames)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly List<BotCaptureInput> controls = [];
    private readonly Task<string?> writer;
    private InputButtons held;
    private (double Time, string Reason)? stop;
    private double lastCapture = double.NegativeInfinity;
    private double lastControl = double.NegativeInfinity;
    private double lastAvailable;
    private int droppedSamples;

    public BotEvaluationRecorder(string root, BotCaptureDomain domain, string modelName, string modelSha256,
        GroundTruthLineDocument reference, long generation, double startedAt)
    {
        if (!double.IsFinite(startedAt) || startedAt < 0) throw new ArgumentOutOfRangeException(nameof(startedAt));
        this.root = Path.Combine(Path.GetFullPath(root), domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay");
        this.domain = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        this.modelName = modelName;
        this.modelSha256 = modelSha256;
        this.reference = reference.Clone();
        this.generation = generation;
        this.startedAt = lastAvailable = startedAt;
        labelledScenes = this.reference.AuthoredLines.Where(line => line.Kind == GroundTruthLineKind.Positive)
            .Select(line => line.SceneName).ToHashSet(StringComparer.Ordinal);
        writer = Task.Run(WriteAsync);
    }

    public void RecordFrame(BgraFrame image, BotCaptureTiming timing, BotCaptureObservation? observation, string? scene,
        IReadOnlyList<BotFloor> detected, BotScreenTerrain? authored, VisualReplayGroundInput? groundInput, int? renderedFrame)
    {
        lock (gate)
        {
            if (stop is not null || writer.IsCompleted || timing.Generation != generation
                || !double.IsFinite(timing.CaptureTimestamp) || timing.CaptureTimestamp < startedAt
                || timing.CaptureTimestamp <= lastCapture || !double.IsFinite(timing.AvailableAt)
                || timing.AvailableAt < timing.CaptureTimestamp)
                return;
            var paired = authored is not null && scene is not null && labelledScenes.Contains(scene);
            var audit = new TerrainAuditFrame(timing.CaptureTimestamp, timing.CaptureTimestamp, (ulong)generation,
                image.Width, image.Height, scene, paired, detected, paired ? authored!.Floors : []);
            if (observation is not null && (observation.Generation != generation
                || observation.CaptureTimestamp != timing.CaptureTimestamp
                || observation.TerrainSource != (domain == "hacker" ? "human-authored" : "detected")))
                observation = null;
            if (groundInput is not null && (groundInput.CaptureTimestamp != timing.CaptureTimestamp
                || groundInput.CaptureGeneration != (ulong)generation))
                groundInput = null;
            lastCapture = timing.CaptureTimestamp;
            lastAvailable = Math.Max(lastAvailable, timing.AvailableAt);
            if (!samples.Writer.TryWrite(new(image, observation, audit, groundInput, renderedFrame))) droppedSamples++;
        }
    }

    public void RecordControls(InputButtons buttons, double time)
    {
        lock (gate)
        {
            if (stop is not null || !double.IsFinite(time) || time < startedAt || time < lastControl) return;
            BotCaptureSession.AppendInputEdges(controls, held, buttons, time);
            held = buttons;
            lastControl = time;
        }
    }

    public Task<string?> FinishAsync(double stoppedAt, string reason)
    {
        lock (gate)
        {
            if (stop is not null) return writer;
            var time = Math.Max(double.IsFinite(stoppedAt) ? stoppedAt : startedAt, Math.Max(lastAvailable, lastControl));
            BotCaptureSession.AppendInputEdges(controls, held, InputButtons.None, time);
            stop = (time, reason);
            samples.Writer.TryComplete();
            return writer;
        }
    }

    private async Task<string?> WriteAsync()
    {
        if (!await samples.Reader.WaitToReadAsync().ConfigureAwait(false)) return null;
        var staging = Path.Combine(root, $".pending-evaluation-{id:N}");
        try
        {
            using var clip = new BotCaptureSession.Clip(staging, id, domain);
            var referencePath = Path.Combine(clip.Directory, "authored-ground.json");
            new GroundTruthLineStore(referencePath).Save(reference);
            WriteJson(clip.Directory, "audit-context.json", new
            {
                schemaVersion = 1, domain, kind = Kind, pathID = id,
                referenceCoverage = "positive-authored-segments-only",
                authoredGroundSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(referencePath))).ToLowerInvariant()
            });

            var archive = new VisualReplayArchiveWriter(clip.Directory, id, new Dictionary<string, string>
            {
                ["kind"] = Kind, ["botDomain"] = domain, ["frameSelection"] = "terrain-audit-cadence"
            }, MaximumPendingFrames);
            var auditCount = 0;
            var pairedCount = 0;
            RecordedVisualFrameArchive source;
            try
            {
                using (var auditLog = new StreamWriter(Path.Combine(clip.Directory, "terrain-audit.jsonl"),
                    append: false, new UTF8Encoding(false)) { NewLine = "\n" })
                {
                    await foreach (var sample in samples.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if (sample.Observation is { } row) _ = clip.Record(row, sample.Image);
                        auditLog.WriteLine(JsonSerializer.Serialize(sample.Audit, BotCaptureSession.Json));
                        auditCount++;
                        if (sample.Audit.ReferenceAvailable) pairedCount++;
                        var stamp = sample.Audit;
                        archive.Append(sample.Image, stamp.CaptureTimestamp, stamp.CapturedAt, stamp.CaptureGeneration,
                            stamp.Width, sample.RenderedFrame);
                        archive.RecordStage(new("visionStarted", stamp.CaptureTimestamp, stamp.CaptureGeneration));
                        archive.RecordStage(new("visionCompleted", stamp.CaptureTimestamp, stamp.CaptureGeneration));
                        if (sample.GroundInput is { } input) archive.RecordGroundInput(input);
                    }
                }
                source = await archive.FinishAsync(incompleteReason: droppedSamples > 0 ? "evaluationBackpressure" : null)
                    .ConfigureAwait(false);
            }
            catch
            {
                await archive.FinishAsync(cancelled: true).ConfigureAwait(false);
                throw;
            }

            var (stoppedAt, reason) = stop!.Value;
            foreach (var input in controls) clip.Append(input);
            clip.Close();
            WriteJson(clip.Directory, "manifest.json", new BotCaptureManifest(BotRecordingContract.SchemaVersion, id,
                createdAt, startedAt, "modded", modelName, "receiver-sent",
                reason == "user-stop" ? "completed" : "interrupted", stoppedAt - startedAt,
                clip.Count, clip.UsableCount, controls.Count, clip.ImageCount, domain, null, null)
            {
                StopReason = reason, ExpertDemonstration = false,
                TerrainAuditCount = auditCount, TerrainPairedFrameCount = pairedCount
            });
            WriteJson(clip.Directory, "evaluation.json", new
            {
                kind = Kind, domain, modelName, modelSHA256 = modelSha256, actionSource = "general-policy",
                expertDemonstration = false, droppedSamples, visualFrameArchive = source
            });
            var destination = Path.Combine(root, BotCaptureSession.DirectoryName(id));
            Directory.Move(clip.Directory, destination);
            return destination;
        }
        finally
        {
            samples.Writer.TryComplete();
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void WriteJson<T>(string directory, string name, T value) =>
        AtomicFile.Write(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(value, BotCaptureSession.Json));
}
