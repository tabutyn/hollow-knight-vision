using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public static class BotRecordingContract
{
    public const int SchemaVersion = 2;
    public const int HistoryLength = 4;
    public const int LegacyWidth = 144;
    public const double SampleInterval = .1;
    public const double MaximumDetectionAge = .2;
    public const double MaximumCaptureAge = .35;
    public static IReadOnlyList<int> ActionSizes { get; } = Array.AsReadOnly<int>([3, 3, 2, 2]);
    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly<string>(
    [
        "knight_x", "knight_y", "knight_width", "knight_height", "knight_confidence",
        "detection_age", "terrain_valid", "capture_age", "room_changed",
        "previous_horizontal", "previous_aim", "previous_jump", "previous_attack",
        "target_x", "target_y", "target_valid",
        ..Slots("terrain", 16, ["start_x", "end_x", "y", "confidence", "present"]),
        ..Slots("enemy", 8, ["x", "y", "width", "height", "confidence", "present"]),
        "walls_analyzed",
        ..new[] { "left", "right" }.SelectMany(side => new[] { "x", "bottom_y", "top_y", "solid_side", "present" }
            .Select(field => $"wall_{side}_{field}"))
    ]);

    private static IEnumerable<string> Slots(string name, int count, string[] fields) =>
        Enumerable.Range(0, count).SelectMany(i => fields.Select(field => $"{name}_{i}_{field}"));
}

public sealed record BotFloor(double MinimumX, double MaximumX, double Y, double Confidence);
public sealed record BotWall(double X, double BottomY, double TopY, int SolidSide);
public sealed record BotLine(double X1, double Y1, double X2, double Y2);
public sealed record BotScreenTerrain(double Width, double Height, IReadOnlyList<BotFloor> Floors,
    IReadOnlyList<BotWall> Walls, IReadOnlyList<BotLine>? Doors = null, IReadOnlyList<BotLine>? Spikes = null);

/// <summary>Actor coordinates and dimensions are normalized by image width in the recording.</summary>
public sealed record BotObjectBounds(double X, double Y, double Width, double Height);
public sealed record BotVisualContext(double FrameWidth, double FrameHeight, double? CameraX, double? CameraY)
{
    internal AtlasPoint? Camera => CameraX is { } x && CameraY is { } y && double.IsFinite(x) && double.IsFinite(y)
        ? new(x, y) : null;
}
public sealed record BotAtlasView(double Left, double Right, IReadOnlyList<double> Camera, double Width,
    int CoveredCells, int EarnedCells, int Revision, int? Health, IReadOnlyList<double>? Missing = null);
public sealed record BotRoadHint(double Dx, double Dy, double Clearance, double Distance, bool Frontier);
public sealed record BotBlockadeMark(string Space, double X, double Y, int Direction, double ViewWidth, bool Crossed);
public sealed record BotBlockadeEvidence(BotBlockadeMark Mark, double Dx, double Dy);

public sealed record BotCaptureTiming(double CaptureTimestamp, double AvailableAt, long Generation, int RoomId, ulong RoomRevision);
public enum BotCaptureDomain { Gameplay, Hacker }

public sealed record BotCaptureObservation(
    double CaptureTimestamp, double CapturedAt, double AvailableAt, long Generation,
    [property: JsonPropertyName("roomID")] int RoomId, ulong RoomRevision,
    IReadOnlyList<double> Features, IReadOnlyList<string> Issues, string TerrainSource)
{
    public string Type => "observation";
    public double? DetectionTimestamp { get; init; }
    [JsonPropertyName("detectionFrameID")] public long? DetectionFrameId { get; init; }
    // Runtime identity for the same detection batch before its clock is translated.
    [JsonIgnore] internal double? DetectionSourceTimestamp { get; init; }
    public string? ModelVersion { get; init; }
    public double? BestKnightConfidence { get; init; }
    public int? HumanTerrainCount { get; init; }
    public string? KnightSource { get; init; }
    public string EnemySource { get; init; } = "visual";
    public BotScreenTerrain? AuthoredTerrain { get; init; }
    public BotScreenTerrain? DetectedTerrain { get; init; }
    public IReadOnlyList<BotObjectBounds>? GeoCaches { get; init; }
    public IReadOnlyList<BotObjectBounds>? JoniBlessings { get; init; }
    public BotAtlasView? AtlasView { get; init; }
    public BotRoadHint? RoadHint { get; init; }
    public int? HackerPathVersion { get; init; }
    public int? AtlasPathVersion { get; init; }
    public double? PathProgress { get; init; }
    public double? PathAlignment { get; init; }
    public int? BlockadeVersion { get; init; }
    public BotBlockadePose? BlockadePose { get; init; }
    public BotBlockadeEvidence? Blockade { get; init; }
    public IReadOnlyList<BotObjectBounds>? LifebloodCocoons { get; init; }
    public BotEnemyOutcomes? EnemyOutcomes { get; init; }
    public BotBlueHealthOutcomes? BlueHealthOutcomes { get; init; }
    public BotVisualContext? VisualContext { get; init; }
    public BotKnightPrediction? KnightPrediction { get; init; }

    [JsonIgnore]
    public double KnightPredictionDeadline => KnightPrediction is null && KnightSource != "visual-predicted"
        ? double.PositiveInfinity
        : KnightPrediction is { } estimate && KnightSource == "visual-predicted" && TerrainSource == "detected"
        && VisualContext is { FrameWidth: > 0 and < double.PositiveInfinity, FrameHeight: > 0 and < double.PositiveInfinity }
        && (!estimate.CameraCompensated || VisualContext.Camera.HasValue)
        && estimate.Age > 0 && estimate.Age <= estimate.MaximumAge + 1e-6
        && Math.Abs(CaptureTimestamp - estimate.LastSeenAt - estimate.Age) <= 1e-6
        && estimate.Uncertainty is >= 0 and <= .06 && estimate.ObservedIssues is { } issues
        && issues.Contains("missing-knight") && issues.All(BotKnightPrediction.Bridgeable)
        ? CapturedAt + estimate.MaximumAge - estimate.Age : double.NegativeInfinity;
    public int? MeasuredHealth { get; init; }
    public int? DriveDirection { get; init; }
    public bool? Grounded { get; init; }
    public string? ImagePath { get; init; }

    [JsonIgnore]
    public bool CanTrain => Issues.Count == 0 && AvailableAt < KnightPredictionDeadline || HasMaskedKnightEvidence;

    [JsonIgnore]
    public bool HasMaskedKnightEvidence => TerrainSource == "detected" && Issues is ["missing-knight"]
        && KnightSource is null && KnightPrediction is null && Features.Count >= BotRecordingContract.LegacyWidth
        && Features.Take(5).All(value => value == 0) && Features[6] == 1
        && DetectionTimestamp is { } detection && CaptureTimestamp - detection is >= 0 and <= BotRecordingContract.MaximumDetectionAge
        && AvailableAt - CapturedAt is >= 0 and <= BotRecordingContract.MaximumCaptureAge
        && (Enumerable.Range(0, 16).Any(i => Features[20 + 5 * i] == 1) || DetectedTerrain?.Walls.Count > 0);

    public BotCaptureObservation WithSharedHackerPath(BotCaptureObservation? authored)
    {
        if (authored is not { TerrainSource: "human-authored", HackerPathVersion: 1 }
            || authored.Generation != Generation || authored.CaptureTimestamp != CaptureTimestamp)
            authored = null;
        return this with { HackerPathVersion = 1, RoadHint = authored?.RoadHint,
            PathProgress = authored?.PathProgress, PathAlignment = authored?.PathAlignment };
    }
}

public static class BotObservationBuilder
{
    private readonly record struct Box(double X, double Y, double Width, double Height, double Confidence);

    public static BotCaptureObservation? Hacker(BgraFrame image, int? renderedFrame, ReceiverGroundTruthSample? sample,
        GroundTruthLineDocument authored, BotCaptureTiming timing, InputButtons? input) =>
        Hacker(new WorldSize(image.Width, image.Height), renderedFrame, sample, authored, timing, input);

    public static BotCaptureObservation? Hacker(WorldSize size, int? renderedFrame, ReceiverGroundTruthSample? sample,
        GroundTruthLineDocument authored, BotCaptureTiming timing, InputButtons? input)
    {
        if (sample is null || renderedFrame != HackerFrameSynchronizer.Key(sample.UnityFrame)
            || sample.AtlasGameplayActive == false || !sample.HasFiniteCoordinates()
            || GroundTruthCameraTransform.FromSample(sample, timing.CaptureTimestamp, size) is not { } camera)
            return null;
        var terrain = ProjectAuthored(authored, camera);
        var issues = BaseIssues(timing);
        var mod = sample.Actors;
        var fresh = mod is { Version: 1, Enemies: { Count: <= 64 } };
        var valid = fresh && mod!.Enemies!.All(actor => actor is { IsValid: true })
            && mod.Enemies!.Select(actor => actor.Id).Distinct().Count() == mod.Enemies!.Count
            && (mod.Hero is null || mod.Hero.IsValid);
        if (!fresh) issues.Add("missing-hacker-actor-telemetry");
        else if (!valid) issues.Add("invalid-hacker-actors");
        Box? knight = valid && sample.HeroAvailable && mod!.Hero is { } hero ? ProjectActor(hero) : null;
        if (knight is { } k && (k.X < 0 || k.X > size.Width || k.Y < 0 || k.Y > size.Height)) knight = null;
        if (knight is null) issues.Add("missing-hacker-knight");
        if (!valid) issues.Add("missing-hacker-enemies");
        var terrainValid = terrain.Floors.Count > 0 || terrain.Walls.Count > 0;
        if (!terrainValid) issues.Add("missing-terrain-analysis");
        var enemies = valid ? mod!.Enemies!.Select(ProjectActor) : [];
        return Make(timing, terrain, knight, enemies, terrainValid, 0, issues, "human-authored") with
        {
            DetectionTimestamp = timing.CaptureTimestamp,
            DetectionFrameId = renderedFrame,
            ModelVersion = "receiver-actors-v1",
            HumanTerrainCount = terrain.Floors.Count,
            KnightSource = "hacker-frame",
            EnemySource = "hacker-frame",
            AuthoredTerrain = terrain,
            GeoCaches = valid ? Normalize(mod!.GeoCaches?.Where(actor => actor is { IsValid: true }).Select(ProjectActor), terrain) : null,
            LifebloodCocoons = valid ? Normalize(mod!.LifebloodCocoons?.Where(actor => actor is { IsValid: true }).Select(ProjectActor), terrain) : null,
            EnemyOutcomes = valid ? Validate(mod!.EnemyOutcomes) : null,
            BlueHealthOutcomes = Validate(sample.BlueHealthOutcomes),
            MeasuredHealth = mod?.HeroHealth is >= 0 and <= 32 ? mod.HeroHealth : null,
            DriveDirection = input is { } held ? KnightLevelWalk.Direction(held) : null,
            Grounded = sample.HeroAvailable ? sample.Grounded : null
        };

        Box ProjectActor(ReceiverActorState actor) => new(
            actor.ScreenX * size.Width / sample.ProjectionPixelWidth!.Value,
            actor.ScreenY * size.Height / sample.ProjectionPixelHeight!.Value,
            actor.Width * size.Width / sample.ProjectionPixelWidth!.Value,
            actor.Height * size.Height / sample.ProjectionPixelHeight!.Value, 1);
    }

    public static BotCaptureObservation Gameplay(BgraFrame image, BotCaptureTiming timing, LiveObjectDetectionBatch? batch,
        IReadOnlyList<DetectedFloorLine> floors, IReadOnlyList<DetectedWall> walls, bool terrainAnalyzed,
        AtlasPoint? camera, HudStencilResult? hud, AtlasPoint? measuredFoot, InputButtons? input,
        BotBlueHealthOutcomes? blueHealth = null)
    {
        var terrain = new BotScreenTerrain(image.Width, image.Height,
            floors.Where(line => line.Row >= 0 && line.Row < image.Height && line.XSpan.Start >= 0
                    && line.XSpan.EndInclusive < image.Width)
                .Select(line => new BotFloor(line.XSpan.Start, line.XSpan.EndInclusive + 1, image.Height - line.Row, 1)).ToArray(),
            walls.Where(wall => wall.X >= 0 && wall.X <= image.Width && wall.Top >= 0
                    && wall.Bottom <= image.Height && wall.Top < wall.Bottom)
                .Select(wall => new BotWall(wall.X, image.Height - wall.Bottom, image.Height - wall.Top, wall.SolidOnRight ? 1 : -1)).ToArray());
        var issues = BaseIssues(timing);
        var age = batch is null ? double.PositiveInfinity : (image.ObservedAt - DateTimeOffset.UnixEpoch).TotalSeconds - batch.SourceTimestamp;
        var fresh = batch is not null && batch.CaptureGeneration == timing.Generation
            && batch.SourceFrameIdentifier <= image.CaptureId && age is >= 0 and <= BotRecordingContract.MaximumDetectionAge;
        if (!fresh) issues.Add("stale-or-missing-detection");
        var predictions = fresh ? batch!.Detections.Where(detection => detection.Confidence is >= .5f and <= 1
            && detection.SourceFrameIdentifier == batch.SourceFrameIdentifier && ValidBox(detection.Box)).ToArray() : [];
        var best = predictions.Where(d => LabelingClassIdentity.CanonicalIdentifier(d.ClassIdentifier) == LiveObjectDetectionPostprocessor.Knight)
            .MaxBy(d => d.Confidence);
        Box? knight = best is not null ? ProjectBox(best) : null;
        if (knight is null) issues.Add("missing-knight");
        if (!terrainAnalyzed) issues.Add("missing-terrain-analysis");
        var enemies = predictions.Where(d => LabelingClassIdentity.CanonicalIdentifier(d.ClassIdentifier)
            .StartsWith("enemies.", StringComparison.Ordinal)).Select(ProjectBox);
        var ground = measuredFoot is { } foot && terrain.Floors.Any(line =>
            line.MinimumX <= foot.X && foot.X <= line.MaximumX && Math.Abs(image.Height - foot.Y - line.Y) <= Math.Max(2, image.Height / 180d));
        bool? grounded = ground ? true : null;
        if (!ground && measuredFoot is { } visible && input is { } applied && applied.HasFlag(InputButtons.ActionZ)
            && terrain.Floors.Any(floor => floor.MinimumX <= visible.X && visible.X <= floor.MaximumX
                && image.Height - visible.Y - floor.Y > Math.Max(2, image.Height / 180d)))
            grounded = false;
        return Make(timing, terrain, knight, enemies, terrainAnalyzed, age, issues, "detected") with
        {
            DetectionTimestamp = fresh ? timing.CaptureTimestamp - age : null,
            DetectionFrameId = fresh ? batch!.SourceFrameIdentifier : null,
            DetectionSourceTimestamp = fresh ? batch!.SourceTimestamp : null,
            ModelVersion = best?.ModelVersion?.DisplayName,
            BestKnightConfidence = best?.Confidence,
            HumanTerrainCount = 0,
            KnightSource = best is not null ? "visual" : null,
            DetectedTerrain = terrain,
            GeoCaches = Normalize(fresh ? predictions.Where(d => LabelingClassIdentity.CanonicalIdentifier(d.ClassIdentifier) == "world.geo-deposit").Select(ProjectBox) : null, terrain),
            LifebloodCocoons = Normalize(fresh ? predictions.Where(d => LabelingClassIdentity.CanonicalIdentifier(d.ClassIdentifier) == "world.lifeblood-cacoon").Select(ProjectBox) : null, terrain),
            VisualContext = new(image.Width, image.Height / (double)image.Width, camera?.X / image.Width, camera?.Y / image.Width),
            BlueHealthOutcomes = Validate(blueHealth),
            MeasuredHealth = hud is { EstablishesGameplay: true, HoldsHealthMask: false }
                ? hud.Health.Count(item => item.Kind != "empty") : null,
            DriveDirection = input is { } held ? KnightLevelWalk.Direction(held) : null,
            Grounded = grounded
        };

        Box ProjectBox(ObjectDetection detection)
        {
            var b = detection.Box;
            return new((b.X + b.Width / 2) * image.Width, (1 - b.Y - b.Height / 2) * image.Height,
                b.Width * image.Width, b.Height * image.Height, detection.Confidence);
        }
    }

    public static BotScreenTerrain ProjectAuthored(GroundTruthLineDocument document, GroundTruthCameraTransform camera)
    {
        var size = camera.FrameSize;
        var frame = new GroundTruthRect(0, 0, size.Width, size.Height);
        var floors = new List<BotFloor>();
        var walls = new List<BotWall>();
        var doors = new List<BotLine>();
        var spikes = new List<BotLine>();
        foreach (var line in document.AuthoredLines.Where(line => line.SceneName == camera.SceneName))
        {
            var a = camera.WorldToAtlas(line.Start, frame);
            var b = camera.WorldToAtlas(line.End, frame);
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y)) continue;
            if (line.IsVertical)
            {
                var low = Math.Max(0, Math.Min(a.Y, b.Y));
                var high = Math.Min(size.Height, Math.Max(a.Y, b.Y));
                if (a.X < 0 || a.X > size.Width || low >= high) continue;
                if (line.Kind == GroundTruthLineKind.Wall) walls.Add(new(a.X, low, high, 0));
                else doors.Add(new(a.X, low, a.X, high));
                continue;
            }
            var left = Math.Max(0, Math.Min(a.X, b.X));
            var right = Math.Min(size.Width, Math.Max(a.X, b.X));
            if (a.Y < 0 || a.Y > size.Height || left >= right) continue;
            switch (line.Kind)
            {
                case GroundTruthLineKind.Positive: floors.Add(new(left, right, a.Y, 1)); break;
                case GroundTruthLineKind.Spike: spikes.Add(new(left, a.Y, right, a.Y)); break;
                case GroundTruthLineKind.Door: doors.Add(new(left, a.Y, right, a.Y)); break;
            }
        }
        return new(size.Width, size.Height, floors, walls, doors, spikes);
    }

    private static BotCaptureObservation Make(BotCaptureTiming timing, BotScreenTerrain terrain, Box? knight,
        IEnumerable<Box> enemies, bool terrainValid, double detectionAge, List<string> issues, string source)
    {
        var feature = new double[BotRecordingContract.FeatureNames.Count];
        var width = terrain.Width;
        var cx = knight?.X ?? 0;
        var cy = knight?.Y ?? 0;
        if (knight is { } actor)
        {
            feature[0] = cx / width;
            feature[1] = cy / width;
            feature[2] = actor.Width / width;
            feature[3] = actor.Height / width;
            feature[4] = actor.Confidence;
        }
        feature[5] = Math.Clamp(detectionAge / BotRecordingContract.MaximumDetectionAge, 0, 1);
        feature[6] = terrainValid ? 1 : 0;
        feature[7] = Math.Clamp((timing.AvailableAt - timing.CaptureTimestamp) / BotRecordingContract.MaximumCaptureAge, 0, 1);
        WriteFloorFeatures(feature, terrain.Floors, cx, cy, width);
        var i = 96;
        foreach (var enemy in enemies.OrderBy(e => (Distance(e.X - cx, e.Y - cy), e.Y - e.Height / 2, e.X - e.Width / 2)).Take(8))
        {
            feature[i++] = (enemy.X - cx) / width;
            feature[i++] = (enemy.Y - cy) / width;
            feature[i++] = enemy.Width / width;
            feature[i++] = enemy.Height / width;
            feature[i++] = enemy.Confidence;
            feature[i++] = 1;
        }
        if (knight.HasValue && terrainValid) WriteWallFeatures(feature, terrain, cx, cy);
        for (var index = 0; index < feature.Length; index++)
            feature[index] = double.IsFinite(feature[index]) ? Math.Clamp(feature[index], -4, 4) : 0;
        return new(timing.CaptureTimestamp, timing.CaptureTimestamp, timing.AvailableAt, timing.Generation,
            timing.RoomId, timing.RoomRevision, feature, issues, source);
    }

    internal static void WriteFloorFeatures(double[] features, IEnumerable<BotFloor> floors, double x, double y, double width)
    {
        Array.Clear(features, 16, 80);
        var i = 16;
        foreach (var floor in floors.OrderBy(f => (Distance((f.MinimumX + f.MaximumX) / 2 - x, f.Y - y), f.Y, f.MinimumX)).Take(16))
        {
            features[i++] = (floor.MinimumX - x) / width;
            features[i++] = (floor.MaximumX - x) / width;
            features[i++] = (floor.Y - y) / width;
            features[i++] = floor.Confidence;
            features[i++] = 1;
        }
    }

    internal static void WriteWallFeatures(double[] features, BotScreenTerrain terrain, double x, double y)
    {
        Array.Clear(features, BotRecordingContract.LegacyWidth, 11);
        features[144] = 1;
        FillWall(145, left: true);
        FillWall(150, left: false);

        void FillWall(int index, bool left)
        {
            var wall = terrain.Walls.Where(w => (w.X < x) == left)
                .MinBy(w => Distance(w.X - x, Math.Max(0, Math.Max(w.BottomY - y, y - w.TopY))));
            if (wall is null) return;
            features[index] = (wall.X - x) / terrain.Width;
            features[index + 1] = (wall.BottomY - y) / terrain.Width;
            features[index + 2] = (wall.TopY - y) / terrain.Width;
            features[index + 3] = wall.SolidSide;
            features[index + 4] = 1;
        }
    }

    private static List<string> BaseIssues(BotCaptureTiming stamp) =>
        !double.IsFinite(stamp.AvailableAt) || !double.IsFinite(stamp.CaptureTimestamp)
            || stamp.AvailableAt - stamp.CaptureTimestamp is < 0 or > BotRecordingContract.MaximumCaptureAge ? ["stale-capture"] : [];

    private static double Distance(double x, double y) => x * x + y * y;

    private static bool ValidBox(NormalizedBox b) =>
        double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height)
        && b.X is >= 0 and <= 1 && b.Y is >= 0 and <= 1 && b.Width > 0 && b.Height > 0
        && b.X + b.Width <= 1.000001 && b.Y + b.Height <= 1.000001;

    private static IReadOnlyList<BotObjectBounds>? Normalize(IEnumerable<Box>? source, BotScreenTerrain terrain) =>
        source?.Where(box => box.X >= 0 && box.X <= terrain.Width && box.Y >= 0 && box.Y <= terrain.Height)
            .Select(box => new BotObjectBounds(box.X / terrain.Width, box.Y / terrain.Width,
                box.Width / terrain.Width, box.Height / terrain.Width)).ToArray();

    private static BotEnemyOutcomes? Validate(BotEnemyOutcomes? value) =>
        value is { Damage: >= 0, Kills: >= 0 } && value.Epoch != Guid.Empty ? value : null;

    private static BotBlueHealthOutcomes? Validate(BotBlueHealthOutcomes? value) =>
        value is { Gained: >= 0 } && value.Epoch != Guid.Empty ? value : null;

}
