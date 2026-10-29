using System.Windows.Controls;
using System.Windows.Media;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private ReplayFrameAudit? replayFrameAudit;
    private ReplayFrameAuditSample? latestAuditCapture;
    private TimeSpan lastAuditRenderingTime;

    private void BeginReplayEvaluation(double startedAt)
    {
        inputPathDivergenceMonitor.BeginEvaluation(startedAt);
        Dispatcher.Invoke(() => {
            lastAuditRenderingTime = TimeSpan.MinValue;
            Volatile.Write(ref replayFrameAudit, new(startedAt, workspace));
            CompositionTarget.Rendering += AuditReplayRendering;
        });
    }

    private void CaptureReplayAudit(BgraFrame frame, int? key, double capturedAt)
    {
        latestAuditCapture = new("capture", workspace, key, frame.CaptureId, capturedAt,
            ReplayFrameAudit.Now(), frame.Width, frame.Height);
        Volatile.Read(ref replayFrameAudit)?.Append(latestAuditCapture);
    }

    private void AuditReplayRendering(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref replayFrameAudit) is not { } audit || args is not RenderingEventArgs rendering
            || rendering.RenderingTime == lastAuditRenderingTime) return;
        lastAuditRenderingTime = rendering.RenderingTime;
        if (workspace == "Gameplay" && latestAuditCapture is { } capture)
        {
            var ppu = gameplayAtlas.PixelsPerWorldUnit;
            var x = (Canvas.GetLeft(GameplayImage) + GameplayImage.Width / 2
                + (gameplayAtlasMinimumTileX ?? 0) * gameplayAtlas.TileSize) / ppu;
            var y = -(Canvas.GetTop(GameplayImage) + GameplayImage.Height / 2
                + (gameplayAtlasMinimumTileY ?? 0) * gameplayAtlas.TileSize) / ppu;
            audit.Append(capture with { Kind = "present", Workspace = workspace, ObservedAt = ReplayFrameAudit.Now(),
                CameraX = double.IsFinite(x) ? x : null, CameraY = double.IsFinite(y) ? y : null, Registered = gameplayFrameRegistered });
        }
        else if (workspace == "Hacker" && hackerLivePrepared is { } prepared)
        {
            var room = hackerAtlasOutput?.Rooms.FirstOrDefault(r => r.SceneName == prepared.SceneName);
            var position = HackerScenePosition(prepared.SceneName);
            var x = Canvas.GetLeft(HackerLiveImage) + (hackerAtlasMinimumTileX ?? 0) * HackerAtlasTileSize
                - position.X + (room?.Position.X ?? 0) + prepared.AnchorPosition.X;
            var y = -(Canvas.GetTop(HackerLiveImage) + (hackerAtlasMinimumTileY ?? 0) * HackerAtlasTileSize)
                - HackerLiveImage.Height - position.Y + (room?.Position.Y ?? 0) + prepared.AnchorPosition.Y;
            audit.Append(new("present", workspace, prepared.UnityFrame, prepared.LiveImage.CaptureId, prepared.CapturedAt,
                ReplayFrameAudit.Now(), prepared.LiveImage.Width, prepared.LiveImage.Height,
                double.IsFinite(x) ? x : null, double.IsFinite(y) ? y : null, prepared.RoomPlacementReady));
        }
    }

    private void EndReplayFrameAudit(string directory)
    {
        CompositionTarget.Rendering -= AuditReplayRendering;
        Interlocked.Exchange(ref replayFrameAudit, null)?.Save(directory);
    }
}
