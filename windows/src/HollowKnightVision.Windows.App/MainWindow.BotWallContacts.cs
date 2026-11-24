using System.Windows.Controls;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    internal sealed record BotWallContactsFrame(WeakReference<object> Owner, int RouteEpoch, long CaptureGeneration,
        long CaptureId, double CapturedAt, int RoomId, int? PlacedRoom, Guid? ReceiverSession, string? Scene,
        IReadOnlyList<BotBlockadeMark> Marks);

    private BotWallContactsFrame? gameplayWallContacts;
    private BotWallContactsFrame? hackerWallContacts;
    private WindowsLiveWorldFrameScope? gameplayWallContactScope;
    private (IReadOnlyList<BotBlockadeMark>, LiveWorldSnapshot, int, GroundFeatureOverlayPresentation.AtlasProjection)?
        drawnGameplayWallContacts;
    private (IReadOnlyList<BotBlockadeMark>, GroundTruthCameraTransform, ViewportRect)? drawnHackerWallContacts;

    private BotWallContactsFrame? CurrentBotWallContacts(BotWallContactsFrame? accepted)
    {
        return Volatile.Read(ref atlasPathsEnabled) && accepted is { } contacts
            && contacts.CaptureGeneration == Interlocked.Read(ref recentCaptureGeneration)
            && contacts.RouteEpoch == Volatile.Read(ref botRouteEpoch)
            && contacts.Owner.TryGetTarget(out var owner)
            && ReferenceEquals(owner, (object?)Volatile.Read(ref botCapture) ?? Volatile.Read(ref botPlayback))
            ? contacts : null;
    }

    private void RenderGameplayWallContacts()
    {
        if (workspace != "Gameplay" || visionCancellation is null || !gameplayFrameRegistered
            || labelCapture.SuspendsInference || gameplayPlacementFrame is not { } placed
            || !atlasPathEvidence.AdmitsGameplay(placed.CaptureId, placed.CapturedAt, gameMode == GameRuntimeMode.Modded)
            || CurrentBotWallContacts(Volatile.Read(ref gameplayWallContacts)) is not { PlacedRoom: { } room } contacts
            || contacts.CaptureId > placed.CaptureId || contacts.CapturedAt > placed.CapturedAt
            || gameplayWallContactScope is not { } scope || CurrentWorldFrame(scope) is not { } completed
            || completed.Result.CommittedRoomId != room || completed.Result.CommittedGraph is not { } graph)
        {
            drawnGameplayWallContacts = null;
            BotWallContactPresentation.Clear(GameplayWallContactsCanvas);
            return;
        }

        var projection = SavedGroundProjection;
        var display = (contacts.Marks, graph, room, projection);
        if (drawnGameplayWallContacts == display) return;
        BotWallContactPresentation.DrawGameplay(GameplayWallContactsCanvas, contacts.Marks, graph, room, projection);
        drawnGameplayWallContacts = display;
    }

    private void RenderHackerWallContacts()
    {
        if (!ShowsHackerPaths || labelCapture.SuspendsInference || hackerPathPreview is not { } preview
            || CurrentBotWallContacts(Volatile.Read(ref hackerWallContacts)) is not { } contacts
            || contacts.ReceiverSession != preview.Prepared.Sample.SessionId || contacts.Scene != preview.Prepared.SceneName
            || contacts.CaptureId > preview.Prepared.LiveImage.CaptureId || contacts.CapturedAt > preview.Prepared.CapturedAt)
        {
            drawnHackerWallContacts = null;
            BotWallContactPresentation.Clear(HackerWallContactsCanvas);
            return;
        }
        var live = new ViewportRect(Canvas.GetLeft(HackerLiveImage), Canvas.GetTop(HackerLiveImage),
            HackerLiveImage.Width, HackerLiveImage.Height);
        var camera = preview.Prepared.CameraTransform;
        var display = (contacts.Marks, camera, live);
        if (drawnHackerWallContacts == display) return;
        BotWallContactPresentation.DrawHacker(HackerWallContactsCanvas, contacts.Marks, camera, live);
        drawnHackerWallContacts = display;
    }
}
