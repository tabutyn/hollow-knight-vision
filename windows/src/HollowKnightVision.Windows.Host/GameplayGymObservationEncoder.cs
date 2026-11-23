using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

/// <summary>Combines the Windows visual tracker with the shared authored route for a Game Play Gym episode.</summary>
public static class GameplayGymObservationEncoder
{
    private const int FrameWidth = 640, FrameHeight = 360;
    private static readonly string[] LocalizerClasses = ["enemies.crawlid", "game.playable-knight", "world.geo-deposit"];
    private sealed record Request(int Ticks, int Width, int Height, int[] Previous, ReceiverGroundTruthSample Sample);
    private sealed record Reply(IReadOnlyList<string> Issues, bool PathReachable, string Knight, int Floors, int Walls,
        bool Atlas, bool HealthKnown, bool CrawlidLocalizerActive, bool KnightLocalizerActive, bool DepositLocalizerActive,
        float? KnightConfidence = null, int? VisibleMasks = null, string? Input = null,
        double? PathProgress = null, double? PathAlignment = null, BotVisualCameraDiagnostic? PathDebug = null);

    public static void Run(int version, string rgbPath, bool allowMaskedKnight, string supportRoot,
        string labelsPath, TextReader input, TextWriter output, string? detectorDirectory = null,
        IReadOnlyList<string>? localizerDirectories = null, string? connectionsPath = null, AtlasPoint? fixedObjective = null)
    {
        if (BotPolicyContract.Names(version) is null || localizerDirectories?.Count > LocalizerClasses.Length)
            throw new InvalidDataException("Unknown observation schema or visual model configuration.");
        var (directories, artifacts) = DetectorRuns(supportRoot, detectorDirectory);
        using var detector = new OnnxObjectDetectorSet(directories, artifacts);
        using var localizers = new Localizers(localizerDirectories);
        var stencils = AutoNavigationRunner.CreateGymStencilSource(supportRoot);
        var visual = new BotVisualGymObservationSource();
        var route = new GymHackerRoute(labelsPath, connectionsPath, fixedObjective);
        var history = new BotObservationHistory(version);
        var trace = Environment.GetEnvironmentVariable("HKV_ATLAS_PATH_TRACE") == "1";
        Guid? episode = null;
        var lastTicks = -1;

        while (input.ReadLine() is { } line)
        {
            var request = HackerGymObservationEncoder.Read<Request>(line);
            var previous = HackerGymObservationEncoder.VerifyFrame(request.Sample, request.Previous,
                request.Ticks, ref lastTicks, ref episode);
            if (request.Width != FrameWidth || request.Height != FrameHeight)
                throw new InvalidDataException("Unexpected Game Play Gym image dimensions.");
            var now = request.Ticks / 60d;
            var frame = ReadFrame(rgbPath, request.Ticks, now);
            var evidence = stencils(frame);
            var result = visual.Observe(frame, now, previous.Buttons, Detect, evidence.Hud, evidence.Menu);

            // Receiver measurements can label the shared objective and score
            // movement along it. No receiver actors, poses, or health enter perception.
            var authored = route.Observe(request.Sample, request.Ticks, health: null, previous);
            var row = result.Observation;
            if (row is not null && version >= 12)
                row = row.WithSharedHackerPath(authored);
            if (row is null) history.Reset();
            var vector = row is null ? null : history.Append(row, previous, now, allowMaskedKnight);
            var visibleHealth = row?.AtlasView?.Health;
            var reply = new Reply(result.Issues, route.ObjectiveReachable, row?.KnightSource ?? "missing",
                row?.DetectedTerrain?.Floors.Count ?? 0, row?.DetectedTerrain?.Walls.Count ?? 0,
                row?.AtlasView is not null, visibleHealth.HasValue, localizers.Active(0),
                localizers.Active(1), localizers.Active(2), result.KnightConfidence, visibleHealth,
                HackerGymObservationEncoder.Encode(vector), authored?.PathProgress, authored?.PathAlignment, trace ? result.Camera : null);
            HackerGymObservationEncoder.Write(output, reply);
        }

        IReadOnlyList<ObjectDetection> Detect(BgraFrame frame) =>
            localizers.ReplaceClasses(detector.Detect(frame), frame);
    }

    internal static (IReadOnlyList<string> Directories, IReadOnlyList<LabelingActiveModelArtifact> Artifacts)
        DetectorRuns(string supportRoot, string? detectorDirectory)
    {
        if (detectorDirectory is not null) return ([detectorDirectory], []);
        var artifacts = new LabelingModelVersionStore(Path.Combine(supportRoot, "models-v1")).ActiveModelArtifacts();
        if (artifacts.Count == 0)
            throw new InvalidDataException("Game Play Gym requires an active Windows ONNX detector.");
        return (artifacts.Select(artifact => Path.GetDirectoryName(artifact.ModelPath)!).ToArray(), artifacts);
    }

    internal static BgraFrame ReadFrame(string path, int ticks, double now)
    {
        const int sourceStride = FrameWidth * 3;
        const int targetStride = FrameWidth * BgraFrame.BytesPerPixel;
        var raw = new byte[FrameHeight * sourceStride];
        using var file = File.OpenRead(path);
        if (file.Length != raw.Length) throw new InvalidDataException("Incomplete or oversized Game Play Gym RGB frame.");
        file.ReadExactly(raw);
        var pixels = new byte[FrameHeight * targetStride];
        for (var y = 0; y < FrameHeight; y++)
        {
            var source = raw.AsSpan((FrameHeight - 1 - y) * sourceStride, sourceStride);
            var target = pixels.AsSpan(y * targetStride, targetStride);
            for (var x = 0; x < FrameWidth; x++)
            {
                target[x * 4] = source[x * 3 + 2];
                target[x * 4 + 1] = source[x * 3 + 1];
                target[x * 4 + 2] = source[x * 3];
                target[x * 4 + 3] = 255;
            }
        }
        return new(FrameWidth, FrameHeight, targetStride, pixels, ticks + 1, DateTimeOffset.UnixEpoch.AddSeconds(now));
    }

    private sealed class Localizers : IDisposable
    {
        private readonly OnnxObjectDetector?[] models = new OnnxObjectDetector?[LocalizerClasses.Length];

        public Localizers(IReadOnlyList<string>? paths)
        {
            if (paths is null) return;
            try
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    if (paths[i] == "-") continue;
                    if (!Directory.Exists(paths[i])) throw new InvalidDataException(
                        $"Supply a Windows ONNX run directory for {LocalizerClasses[i]}.");
                    var model = new OnnxObjectDetector(paths[i]);
                    models[i] = model;
                    if (!model.ClassIdentifiers.Any(id => Canonical(id) == LocalizerClasses[i]))
                        throw new InvalidDataException($"The ONNX localizer does not include {LocalizerClasses[i]}.");
                }
            }
            catch { Dispose(); throw; }
        }

        public bool Active(int index) => models[index] is not null;

        public IReadOnlyList<ObjectDetection> ReplaceClasses(IReadOnlyList<ObjectDetection> baseline, BgraFrame frame)
        {
            if (models.All(model => model is null)) return baseline;
            var result = baseline.ToList();
            for (var i = 0; i < models.Length; i++)
            {
                if (models[i] is not { } model) continue;
                var kind = LocalizerClasses[i];
                result.RemoveAll(item => Canonical(item.ClassIdentifier) == kind);
                result.AddRange(model.Detect(frame).Where(item => Canonical(item.ClassIdentifier) == kind));
            }
            return result;
        }

        public void Dispose()
        {
            foreach (var model in models) model?.Dispose();
        }

        private static string Canonical(string id) => LabelingClassIdentity.CanonicalIdentifier(id);
    }
}
