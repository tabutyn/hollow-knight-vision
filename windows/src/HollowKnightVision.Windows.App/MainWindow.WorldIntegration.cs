using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly SerialWorldWorker worldWorker = new();
    // Snapshot the rebuilt pixels and their world placement together. Disk
    // writes, mosaic snapshots, and WPF rendering stay outside this lock.
    private readonly object gameplayWorldPublicationGate = new();
    private sealed record CompletedWorld(WindowsLiveWorldFrame Frame, WindowsLiveWorldPipelineResult Result);
    private CompletedWorld? completedWorld;
    private long pendingWorldTrackingLoss = -1;

    private void QueueWorldFrame(WindowsLiveWorldFrame frame)
    {
        var pipeline = liveWorldPipeline;
        if (pipeline is null) return;
        worldWorker.Submit(() => {
            var lostGeneration = Interlocked.Exchange(ref pendingWorldTrackingLoss, -1);
            if (lostGeneration >= 0) pipeline.NotifyTrackingLoss((ulong)lostGeneration);
            var result = pipeline.Process(frame, Volatile.Read(ref gameplayAtlas));
            lock (gameplayWorldPublicationGate)
            {
                if (result.Discarded || result.CaptureGeneration != (ulong)Interlocked.Read(ref recentCaptureGeneration)) return;
                if (result.AllowsAtlasComposition && result.RebuiltAtlas is { } replacement)
                    StageGameplayAtlasReplacement(replacement);
                Volatile.Write(ref completedWorld, new(frame, result));
            }
            if (result.AcceptedClosure is { } closure)
                gameControls.RecordLoopClosure(frame.Timestamp, result.Revision, result.AppliedCorrection, closure);
        });
    }

    private CompletedWorld? CurrentWorldFrame(WindowsLiveWorldFrameScope frame) =>
        CurrentWorldFrame(frame, Volatile.Read(ref completedWorld));

    private static CompletedWorld? CurrentWorldFrame(WindowsLiveWorldFrameScope frame, CompletedWorld? completed) =>
        completed?.Frame.CaptureGeneration == frame.CaptureGeneration && completed.Frame.RoomName == frame.Room
            ? completed : null;

    private VisionAnalysisResult ReprojectWorldFrame(VisionAnalysisResult analysis, CompletedWorld? publication)
    {
        if (analysis.WorldFrame is not { } frame || CurrentWorldFrame(frame, publication) is not { } completed
            || completed.Result.Revision < analysis.LiveWorldRevision)
            return analysis;
        var placement = completed.Result.ProjectFrame(completed.Frame, frame);
        var offset = placement?.WorldFromLocal;
        var confirmed = placement?.IsPlaced == true;
        var saved = completed.Result.SavedWorld ?? analysis.SavedWorld;
        if (offset == analysis.WorldBasisOffset && confirmed == analysis.WorldPlacementConfirmed)
            return analysis with { SavedWorld = saved };

        var old = analysis.WorldBasisOffset ?? default;
        var next = offset ?? default;
        var walked = confirmed
            ? ProjectWalkedGround(completed.Result, frame, analysis.LocalWalkedGround, next)
            : analysis.LocalWalkedGround;
        return analysis with
        {
            GameplayTelemetry = OffsetGameplayCamera(analysis.GameplayTelemetry, new AtlasPoint(next.X - old.X, next.Y - old.Y)),
            WorldBasisOffset = offset,
            WorldBasisTimestamp = offset.HasValue ? frame.CaptureTimestamp : null,
            WorldPlacementConfirmed = confirmed,
            SavedWorld = saved,
            GroundDisplayLines = analysis.AdmitsTracking
                ? GroundStrokeGeometry.VisibleLines(analysis.GroundHypotheses.AtlasLines,
                    analysis.GroundHypotheses.LineReviews, walked, analysis.GroundDisplayCamera,
                    analysis.Frame.Height, analysis.GroundDisplayKnight) : [],
            PathPresentation = null
        };
    }

    private static IReadOnlyList<GroundHypothesisAtlasLine> ProjectWalkedGround(WindowsLiveWorldPipelineResult result,
        WindowsLiveWorldFrameScope frame, IReadOnlyList<GroundHypothesisAtlasLine> local, AtlasPoint basis)
    {
        var recorded = result.WalkedGroundLineage == frame.RetainedLineage ? result.RecordedLocalWalk : [];
        return LiveWorldWalkedGround.MergeForDisplay(local, recorded, result.WalkedGround, basis);
    }

    private static ReceiverGroundTruthSample OffsetGameplayCamera(ReceiverGroundTruthSample camera, AtlasPoint offset) =>
        camera with
        {
            CameraX = camera.CameraX + offset.X,
            CameraY = camera.CameraY + offset.Y,
            CameraTargetX = camera.CameraTargetX + offset.X,
            CameraTargetY = camera.CameraTargetY + offset.Y
        };
}
