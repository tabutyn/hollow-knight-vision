using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record VisualRouteSample(
    int Index,
    double Timestamp,
    string SceneName,
    double CameraX,
    double CameraY,
    double PixelsPerWorldUnitX,
    double PixelsPerWorldUnitY,
    LowResolutionMotionGrid Grid);

public sealed record VisualRouteSession
{
    public const int CurrentSchemaVersion = 1;

    public VisualRouteSession(
        int schemaVersion,
        Guid id,
        DateTimeOffset createdAt,
        int frameWidth,
        int frameHeight,
        IReadOnlyList<VisualRouteSample> samples)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        CreatedAt = createdAt;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Samples = samples;
        Validate();
    }

    public int SchemaVersion { get; }
    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public IReadOnlyList<VisualRouteSample> Samples { get; }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported visual-route schema {SchemaVersion}.");
        }
        if (Id == Guid.Empty || FrameWidth <= 0 || FrameHeight <= 0 || Samples.Count < 2)
        {
            throw new InvalidDataException("Visual route requires an id, dimensions, and two samples.");
        }
        for (var index = 0; index < Samples.Count; index++)
        {
            var sample = Samples[index];
            if (sample.Index != index || !double.IsFinite(sample.Timestamp)
                || index > 0 && sample.Timestamp < Samples[index - 1].Timestamp
                || string.IsNullOrWhiteSpace(sample.SceneName)
                || !double.IsFinite(sample.CameraX) || !double.IsFinite(sample.CameraY)
                || !double.IsFinite(sample.PixelsPerWorldUnitX)
                || !double.IsFinite(sample.PixelsPerWorldUnitY)
                || sample.PixelsPerWorldUnitX <= 0 || sample.PixelsPerWorldUnitY <= 0
                || sample.Grid.Width <= LowResolutionRoomMotionTracker.MaximumShift * 2 + 4
                || sample.Grid.Height <= 7)
            {
                throw new InvalidDataException($"Visual route sample {index} is invalid.");
            }
        }
        var first = Samples[0].Grid;
        if (Samples.Any(sample =>
                sample.Grid.Width != first.Width || sample.Grid.Height != first.Height))
        {
            throw new InvalidDataException("Visual route grids must share dimensions.");
        }
    }
}

public sealed class VisualRouteStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        LabelingExampleStore.CreateJsonOptions();

    public VisualRouteStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string SessionPath => Path.Combine(RootDirectory, "visual-route.json");

    public void Save(VisualRouteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        Directory.CreateDirectory(RootDirectory);
        AtomicFile.Write(SessionPath, JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions));
    }

    public VisualRouteSession Load()
    {
        var session = JsonSerializer.Deserialize<VisualRouteSession>(
            File.ReadAllBytes(SessionPath), JsonOptions)
            ?? throw new InvalidDataException("Visual route is empty.");
        session.Validate();
        return session;
    }
}

public sealed record VisualRouteReplayOptions(
    int RepeatCount = 3,
    double MaximumMeanErrorPixels = 12,
    double MaximumStepErrorPixels = 32,
    bool ReopenSessionBetweenPasses = true);

public sealed record VisualRouteReplayPass(
    int PassIndex,
    int ComparableSteps,
    int AcceptedSteps,
    int StationarySteps,
    int SceneTransitionSteps,
    int RejectedSteps,
    double MeanErrorPixels,
    double MaximumErrorPixels,
    CameraVector EstimatedTravelPixels,
    CameraVector ExpectedTravelPixels,
    IReadOnlyDictionary<string, int> Rejections,
    bool Passed);

public sealed record VisualRouteReplayReport(
    int SchemaVersion,
    Guid SessionId,
    int RepeatCount,
    double MaximumMeanErrorPixels,
    double MaximumStepErrorPixels,
    bool ReopenedSessionBetweenPasses,
    IReadOnlyList<VisualRouteReplayPass> Passes,
    bool Passed,
    IReadOnlyList<string> FailureReasons)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Replays saved 64×36 visual evidence against receiver camera truth. Route
/// samples never mutate; each pass reruns the exact CPU matcher.
/// </summary>
public sealed class VisualRouteReplayEvaluator
{
    private readonly VisualRouteStore store;

    public VisualRouteReplayEvaluator(VisualRouteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    public VisualRouteReplayReport Evaluate(
        VisualRouteReplayOptions? options = null,
        string? reportPath = null)
    {
        options ??= new VisualRouteReplayOptions();
        if (options.RepeatCount <= 0 || !double.IsFinite(options.MaximumMeanErrorPixels)
            || options.MaximumMeanErrorPixels <= 0
            || !double.IsFinite(options.MaximumStepErrorPixels)
            || options.MaximumStepErrorPixels < options.MaximumMeanErrorPixels)
        {
            throw new ArgumentException("Visual replay thresholds or repeat count are invalid.");
        }
        var initial = store.Load();
        var passes = new List<VisualRouteReplayPass>();
        for (var passIndex = 0; passIndex < options.RepeatCount; passIndex++)
        {
            var session = passIndex > 0 && options.ReopenSessionBetweenPasses
                ? store.Load()
                : initial;
            passes.Add(EvaluatePass(session, options, passIndex));
        }
        var failures = passes.Where(pass => !pass.Passed)
            .Select(pass =>
                $"pass {pass.PassIndex}: mean={pass.MeanErrorPixels:F2}px, "
                + $"max={pass.MaximumErrorPixels:F2}px, rejected={pass.RejectedSteps}")
            .ToArray();
        var report = new VisualRouteReplayReport(
            VisualRouteReplayReport.CurrentSchemaVersion,
            initial.Id,
            options.RepeatCount,
            options.MaximumMeanErrorPixels,
            options.MaximumStepErrorPixels,
            options.ReopenSessionBetweenPasses,
            passes,
            failures.Length == 0,
            failures);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            AtomicFile.Write(
                reportPath,
                JsonSerializer.SerializeToUtf8Bytes(
                    report,
                    LabelingExampleStore.CreateJsonOptions()));
        }
        return report;
    }

    private static VisualRouteReplayPass EvaluatePass(
        VisualRouteSession session,
        VisualRouteReplayOptions options,
        int passIndex)
    {
        var errors = new List<double>();
        var accepted = 0;
        var stationary = 0;
        var transitions = 0;
        var rejected = 0;
        var estimatedX = 0.0;
        var estimatedY = 0.0;
        var expectedX = 0.0;
        var expectedY = 0.0;
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 1; index < session.Samples.Count; index++)
        {
            var previous = session.Samples[index - 1];
            var current = session.Samples[index];
            if (!previous.SceneName.Equals(current.SceneName, StringComparison.Ordinal))
            {
                transitions++;
                continue;
            }
            var expectedStepX = (current.CameraX - previous.CameraX)
                * ((previous.PixelsPerWorldUnitX + current.PixelsPerWorldUnitX) * 0.5);
            var expectedStepY = -(current.CameraY - previous.CameraY)
                * ((previous.PixelsPerWorldUnitY + current.PixelsPerWorldUnitY) * 0.5);
            expectedX += expectedStepX;
            expectedY += expectedStepY;
            var diagnostic = LowResolutionRoomMotionTracker.DiagnoseTranslation(
                previous.Grid,
                current.Grid);
            double estimatedStepX;
            double estimatedStepY;
            if (diagnostic.Motion is { } motion)
            {
                estimatedStepX = -motion.ScreenShiftX * session.FrameWidth / current.Grid.Width;
                estimatedStepY = -motion.ScreenShiftY * session.FrameHeight / current.Grid.Height;
                accepted++;
            }
            else if (CameraMath.Length(expectedStepX, expectedStepY) <= 0.5)
            {
                estimatedStepX = 0;
                estimatedStepY = 0;
                stationary++;
            }
            else
            {
                rejected++;
                var reason = diagnostic.FinalRejection?.ToString() ?? "Unknown";
                rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
                continue;
            }
            estimatedX += estimatedStepX;
            estimatedY += estimatedStepY;
            errors.Add(CameraMath.Length(
                estimatedStepX - expectedStepX,
                estimatedStepY - expectedStepY));
        }
        if (errors.Count == 0)
        {
            throw new InvalidDataException("Visual route has no comparable same-scene steps.");
        }
        var mean = errors.Average();
        var maximum = errors.Max();
        var passed = rejected == 0
            && mean <= options.MaximumMeanErrorPixels
            && maximum <= options.MaximumStepErrorPixels;
        return new VisualRouteReplayPass(
            passIndex,
            errors.Count,
            accepted,
            stationary,
            transitions,
            rejected,
            mean,
            maximum,
            new CameraVector(estimatedX, estimatedY),
            new CameraVector(expectedX, expectedY),
            rejections,
            passed);
    }
}
