using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private readonly SerialWorldWorker worldWorker = new();
    private sealed record CompletedWorld(WindowsLiveWorldFrame Frame, WindowsLiveWorldPipelineResult Result);
    private CompletedWorld? completedWorld;
    private long pendingWorldTrackingLoss = -1;

    private void QueueWorldFrame(WindowsLiveWorldFrame frame, ReceiverGroundTruthSample telemetry,
        IReadOnlyList<PixelRect> exclusions, string roomId, string captureIdentity)
    {
        var pipeline = liveWorldPipeline;
        if (pipeline is null) return;
        worldWorker.Submit(() => {
            var lostGeneration = Interlocked.Exchange(ref pendingWorldTrackingLoss, -1);
            if (lostGeneration >= 0) pipeline.NotifyTrackingLoss((ulong)lostGeneration);
            var result = pipeline.Process(frame);
            if (result.CaptureGeneration != (ulong)Interlocked.Read(ref recentCaptureGeneration)) return;
            if (result.AllowsAtlasWrite)
            {
                if (result.RebuiltAtlas is { } replacement)
                {
                    Interlocked.Exchange(ref gameplayAtlas, replacement);
                    Interlocked.Exchange(ref gameplayPersistedRevision, -1);
                    Interlocked.Exchange(ref gameplayRenderedRevision, -1);
                    Volatile.Write(ref gameplayAtlasRequiresFullRewrite, true);
                    Interlocked.Exchange(ref gameplayAtlasPresentationPending, replacement);
                }
                else gameplayAtlas.AddFrame(frame.Frame, telemetry with {
                    CameraX = result.CameraPose.X, CameraY = result.CameraPose.Y,
                    CameraTargetX = result.CameraPose.X, CameraTargetY = result.CameraPose.Y
                }, exclusions, roomId: roomId, captureGeneration: frame.CaptureGeneration, captureIdentity: captureIdentity);
                if (result.AcceptedClosure is { } closure)
                    gameControls.RecordLoopClosure(frame.Timestamp, result.Revision, result.AppliedCorrection, closure);
            }
            Volatile.Write(ref completedWorld, new(frame, result));
        });
    }

    private WindowsLiveWorldPipelineResult? CurrentWorldResult(ulong generation, string room)
    {
        var completed = Volatile.Read(ref completedWorld);
        return completed?.Frame.CaptureGeneration == generation && completed.Frame.RoomName == room
            ? completed.Result : null;
    }
}
