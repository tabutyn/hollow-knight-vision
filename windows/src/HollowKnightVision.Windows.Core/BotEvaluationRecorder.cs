using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HollowKnightVision.Windows.Core;

/// <summary>Stores policy playback in its own archive at the observation cadence.</summary>
public sealed class BotEvaluationRecorder
{
    private const string Kind = "bot-evaluation";
    private const string SkillEventFile = "skill-events.jsonl";
    private const double SkillSampleInterval = .075;
    private const int MaximumPendingFrames = 8;
    private const int MaximumUnpairedHackerFrames = 3;
    private sealed record TerrainAuditFrame(double CaptureTimestamp, double CapturedAt, ulong CaptureGeneration,
        int Width, int Height, string? SceneName, bool ReferenceAvailable,
        IReadOnlyList<BotFloor> Detected, IReadOnlyList<BotFloor> Authored);
    private sealed record Sample(BgraFrame Image, BotCaptureObservation? Observation, TerrainAuditFrame Audit,
        VisualReplayGroundInput? GroundInput, int? RenderedFrame, bool ReceiverMatched);
    private sealed record SkillEvent(ThinkingPlan Plan, ThinkingCandidate Choice, string Context, string Event, double Timestamp);

    private readonly object gate = new();
    private readonly string root, domain, modelName;
    private readonly string? modelSha256;
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
    private readonly Queue<Sample> unmatchedHackerFrames = new();
    private readonly SortedDictionary<double, (IReadOnlyList<BotFloor> Floors, VisualReplayGroundInput Input)> hackerVisual = new();
    private readonly List<SkillEvent> skillEvents = [];
    private readonly BotAtlasRun atlasRun = new();
    private readonly Task<string?> writer;
    private InputButtons held;
    private (double Time, string Reason)? stop;
    private double lastCapture = double.NegativeInfinity;
    private double lastWrittenCapture = double.NegativeInfinity;
    private double lastControl = double.NegativeInfinity;
    private double lastSkillSample = double.NegativeInfinity;
    private double lastAvailable;
    private int droppedSamples;

    public BotEvaluationRecorder(string root, BotCaptureDomain domain, string modelName, string? modelSha256,
        GroundTruthLineDocument reference, long generation, double startedAt, bool prepareImmediately = false)
    {
        if (!double.IsFinite(startedAt) || startedAt < 0) throw new ArgumentOutOfRangeException(nameof(startedAt));
        this.root = Path.Combine(Path.GetFullPath(root), domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay");
        this.domain = domain == BotCaptureDomain.Hacker ? "hacker" : "gameplay";
        this.modelName = modelName;
        this.modelSha256 = string.IsNullOrEmpty(modelSha256) ? null : modelSha256;
        this.reference = reference.Clone();
        this.generation = generation;
        this.startedAt = lastAvailable = startedAt;
        labelledScenes = this.reference.AuthoredLines.Where(line => line.Kind == GroundTruthLineKind.Positive)
            .Select(line => line.SceneName).ToHashSet(StringComparer.Ordinal);
        var ready = prepareImmediately ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        writer = Task.Run(() => WriteAsync(ready));
        Preparation = ready is null ? Task.CompletedTask : WaitForPreparationAsync(ready.Task, writer);
    }

    /// <summary>Completes when immediate preparation opens the archive, or fails if the writer cannot open it.</summary>
    public Task Preparation { get; }

    private static async Task WaitForPreparationAsync(Task ready, Task writer)
    {
        var setup = await Task.WhenAny(ready, writer).ConfigureAwait(false);
        await setup.ConfigureAwait(false);
    }

    public bool WantsSkillSample(double capturedAt)
    {
        lock (gate) return stop is null && capturedAt > lastCapture
            && capturedAt - lastSkillSample >= SkillSampleInterval;
    }

    public void RecordFrame(BgraFrame image, BotCaptureTiming timing, BotCaptureObservation? observation, string? scene,
        IReadOnlyList<BotFloor> detected, BotScreenTerrain? authored, VisualReplayGroundInput? groundInput, int? renderedFrame,
        bool skill = false, bool awaitHackerVisual = false)
    {
        lock (gate)
        {
            if (stop is not null || writer.IsCompleted || timing.Generation != generation
                || !double.IsFinite(timing.CaptureTimestamp) || timing.CaptureTimestamp < startedAt
                || timing.CaptureTimestamp <= lastCapture || !double.IsFinite(timing.AvailableAt)
                || timing.AvailableAt < timing.CaptureTimestamp
                || skill && timing.CaptureTimestamp - lastSkillSample < SkillSampleInterval)
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
            if (skill) lastSkillSample = lastCapture;
            lastAvailable = Math.Max(lastAvailable, timing.AvailableAt);
            var sample = new Sample(image, observation, audit, groundInput, renderedFrame, awaitHackerVisual);
            if (awaitHackerVisual)
            {
                unmatchedHackerFrames.Enqueue(sample);
                FlushHackerFrames(force: false);
            }
            else
            {
                FlushHackerFrames(force: true);
                WriteSample(sample);
            }
        }
    }

    public void RecordHackerVisualEvidence(VisualReplayGroundInput input, IReadOnlyList<BotFloor> detected)
    {
        lock (gate)
        {
            if (domain != "hacker" || stop is not null || writer.IsCompleted || input.CaptureGeneration != (ulong)generation
                || !double.IsFinite(input.CaptureTimestamp) || input.CaptureTimestamp < startedAt
                || input.CaptureTimestamp <= lastWrittenCapture) return;
            hackerVisual[input.CaptureTimestamp] = (detected, input);
            FlushHackerFrames(force: false);
            // At most a few frames can arrive while the other worker is busy.
            while (hackerVisual.Count > MaximumPendingFrames)
                hackerVisual.Remove(hackerVisual.First().Key);
        }
    }

    private void FlushHackerFrames(bool force)
    {
        while (unmatchedHackerFrames.TryPeek(out var sample))
        {
            var hasVisual = hackerVisual.TryGetValue(sample.Audit.CaptureTimestamp, out var visual);
            if (!hasVisual && !force && unmatchedHackerFrames.Count <= MaximumUnpairedHackerFrames) break;
            unmatchedHackerFrames.Dequeue();
            WriteSample(hasVisual
                ? sample with { Audit = sample.Audit with { Detected = visual.Floors }, GroundInput = visual.Input }
                : sample with { Audit = sample.Audit with { ReferenceAvailable = false } });
        }
    }

    private void WriteSample(Sample sample)
    {
        lastWrittenCapture = sample.Audit.CaptureTimestamp;
        while (hackerVisual.Count > 0 && hackerVisual.First().Key <= lastWrittenCapture)
            hackerVisual.Remove(hackerVisual.First().Key);
        if (!samples.Writer.TryWrite(sample)) droppedSamples++;
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

    public void RecordDecision(BotCaptureObservation row, InputButtons action, InputButtons previous, string? scene)
    {
        lock (gate)
        {
            if (stop is null && row.Generation == generation && row.CaptureTimestamp >= startedAt)
                atlasRun.Observe(row, action, previous, scene);
        }
    }

    public void RecordSkill(ThinkingPlan plan, ThinkingCandidate choice, string context, string outcome, double time)
    {
        lock (gate)
        {
            if (stop is not null || writer.IsCompleted || plan.Scope.Generation != generation
                || !double.IsFinite(time) || time < startedAt
                || skillEvents.Count > 0 && time < skillEvents[^1].Timestamp) return;
            skillEvents.Add(new(plan, choice, context, outcome, time));
            if (outcome == "selected") lastSkillSample = double.NegativeInfinity;
        }
    }

    public Task<string?> FinishAsync(double stoppedAt, string reason)
    {
        lock (gate)
        {
            if (stop is not null) return writer;
            var time = Math.Max(double.IsFinite(stoppedAt) ? stoppedAt : startedAt, Math.Max(lastAvailable, lastControl));
            if (skillEvents.Count > 0) time = Math.Max(time, skillEvents[^1].Timestamp);
            BotCaptureSession.AppendInputEdges(controls, held, InputButtons.None, time);
            FlushHackerFrames(force: true);
            stop = (time, reason);
            samples.Writer.TryComplete();
            return writer;
        }
    }

    private async Task<string?> WriteAsync(TaskCompletionSource? ready)
    {
        if (ready is null && !await samples.Reader.WaitToReadAsync().ConfigureAwait(false)) return null;
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
                    ready?.SetResult();
                    await foreach (var sample in samples.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        if (sample.Observation is { } row) _ = clip.Record(row, sample.Image);
                        auditLog.WriteLine(JsonSerializer.Serialize(sample.Audit, BotCaptureSession.Json));
                        auditCount++;
                        if (sample.Audit.ReferenceAvailable) pairedCount++;
                        var stamp = sample.Audit;
                        archive.Append(sample.Image, stamp.CaptureTimestamp, stamp.CapturedAt, stamp.CaptureGeneration,
                            stamp.Width, sample.RenderedFrame);
                        if (sample.ReceiverMatched)
                            archive.RecordStage(new("receiverMatched", stamp.CaptureTimestamp, stamp.CaptureGeneration));
                        if (!sample.ReceiverMatched || sample.GroundInput is not null)
                        {
                            archive.RecordStage(new("visionStarted", stamp.CaptureTimestamp, stamp.CaptureGeneration));
                            archive.RecordStage(new("visionCompleted", stamp.CaptureTimestamp, stamp.CaptureGeneration));
                        }
                        if (sample.GroundInput is { } input) archive.RecordGroundInput(input);
                    }
                }
                if (auditCount == 0)
                {
                    await archive.FinishAsync(cancelled: true).ConfigureAwait(false);
                    return null;
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
            if (skillEvents.Count > 0) WriteSkillEvents(clip.Directory);
            WriteJson(clip.Directory, "atlas-score.json", atlasRun.Score);
            WriteJson(clip.Directory, "manifest.json", new BotCaptureManifest(BotRecordingContract.SchemaVersion, id,
                createdAt, startedAt, "modded", modelName, "receiver-sent",
                reason is "user-stop" or "local-control" or "skill-finished" ? "completed" : "interrupted", stoppedAt - startedAt,
                clip.Count, clip.UsableCount, controls.Count, clip.ImageCount, domain, null, null)
            {
                StopReason = reason, ExpertDemonstration = false,
                TerrainAuditCount = auditCount, TerrainPairedFrameCount = pairedCount
            });
            WriteJson(clip.Directory, "evaluation.json", new
            {
                kind = Kind, domain, modelName, modelSHA256 = modelSha256,
                actionSource = skillEvents.Count > 0 ? "skill-plan" : modelSha256 is null ? "starter-policy" : "general-policy",
                skillEvents = skillEvents.Count > 0 ? SkillEventFile : null,
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

    private void WriteSkillEvents(string directory)
    {
        using var log = new StreamWriter(Path.Combine(directory, SkillEventFile), append: false, new UTF8Encoding(false))
            { NewLine = "\n" };
        foreach (var entry in skillEvents)
        {
            var choices = entry.Plan.Scores.Select(score => SkillChoice(score.Candidate, score.Points)).ToArray();
            var points = entry.Plan.Scores.FirstOrDefault(score => score.Candidate.Id == entry.Choice.Id)?.Points ?? 0;
            log.WriteLine(JsonSerializer.Serialize(new
            {
                plan = new { objective = "expandAtlas", success = "Record the policy outcome.", choices,
                    shopping = Array.Empty<object>(), decisionID = entry.Plan.DecisionId },
                choice = SkillChoice(entry.Choice, points), entry.Context, entry.Event, entry.Timestamp
            }, BotCaptureSession.Json));
        }
    }

    private static object SkillChoice(ThinkingCandidate candidate, int points)
    {
        var (policy, control, cue) = candidate.Action switch
        {
            ThinkingAction.Focus => (candidate.Policy, "focus", ""),
            ThinkingAction.Inspect => (candidate.Policy, "inspect", "Inspect"),
            ThinkingAction.Listen => (candidate.Policy, "inspect", "Listen"),
            ThinkingAction.Continue => ("interact", "confirm", "pause.continue"),
            _ => (candidate.Policy, "native", "")
        };
        object? target = candidate.Action switch
        {
            ThinkingAction.Traverse or ThinkingAction.Explore when candidate.Route is { } route => new
            {
                traverse = new { _0 = new { route.Chart, goal = route.Objective, route.Waypoint, hint = route.Road } }
            },
            ThinkingAction.Fight => new { fight = new { _0 = candidate.ActorId } },
            ThinkingAction.Lifeblood => new { lifeblood = new { _0 = candidate.ActorId } },
            ThinkingAction.Focus => new { focus = new Dictionary<string, int>() },
            _ => null
        };
        return new
        {
            policy, control, cue, points, candidateID = candidate.Id, promptFirstSeen = candidate.PromptStart,
            binding = target is null ? null : new { room = candidate.Room, target }
        };
    }

    private static void WriteJson<T>(string directory, string name, T value) =>
        AtomicFile.Write(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(value, BotCaptureSession.Json));
}
