namespace HollowKnightVision.Windows.App;

internal enum AtlasPathFrameState { Unrecognized, Gameplay, Hidden }

internal sealed class AtlasPathFrameEvidence
{
    private const int VisualCapacity = 24;
    private readonly Queue<(long Id, AtlasPathFrameState State)> hackerVisual = new();
    private long visualGeneration = -1;
    private long latestVisualId = -1, lastHiddenId = -1;
    private long gameplayHiddenThrough = -1;

    private readonly object receiverLock = new();
    private Guid? receiverSession;
    private bool? receiverGameplay;
    private double receiverObservedAt = double.NegativeInfinity;
    private double receiverResumedAt = double.NegativeInfinity;

    internal static AtlasPathFrameState Classify(bool hasSceneSignal, string? recognition) =>
        !hasSceneSignal ? AtlasPathFrameState.Hidden : recognition switch
        {
            null => AtlasPathFrameState.Unrecognized,
            "game" or "Gameplay" => AtlasPathFrameState.Gameplay,
            _ => AtlasPathFrameState.Hidden
        };

    internal void ObserveGameplayPreview(long id, bool hasSceneSignal)
    {
        if (!hasSceneSignal) gameplayHiddenThrough = Math.Max(gameplayHiddenThrough, id);
    }

    internal bool AdmitsGameplay(long id, double capturedAt, bool modded) =>
        id > gameplayHiddenThrough && (!modded || ReceiverAdmits(capturedAt));

    internal void ResetGameplayPreview() => gameplayHiddenThrough = -1;

    // The presentation dispatcher owns the visual history.
    internal void ObserveHacker(long generation, long id, AtlasPathFrameState state)
    {
        if (generation < visualGeneration || (generation == visualGeneration && id <= latestVisualId)) return;
        if (generation != visualGeneration)
        {
            visualGeneration = generation;
            hackerVisual.Clear();
            lastHiddenId = -1;
        }
        latestVisualId = id;
        if (state == AtlasPathFrameState.Hidden) lastHiddenId = id;
        if (hackerVisual.Count == VisualCapacity) hackerVisual.Dequeue();
        hackerVisual.Enqueue((id, state));
    }

    internal bool AdmitsHacker(long generation, long id, double capturedAt, Guid session, bool hasSceneSignal,
        bool? exactReceiverGameplay)
    {
        if (!hasSceneSignal || !ReceiverAdmits(capturedAt, session)) return false;
        if (generation == visualGeneration && id <= lastHiddenId) return false;
        var recognized = generation == visualGeneration
            ? hackerVisual.FirstOrDefault(item => item.Id == id).State : AtlasPathFrameState.Unrecognized;
        return exactReceiverGameplay ?? (recognized == AtlasPathFrameState.Gameplay);
    }

    // Receiver state arrives even when the game has stopped producing camera matches.
    internal bool ObserveReceiver(Guid session, bool? gameplay, double observedAt)
    {
        lock (receiverLock)
        {
            if (!double.IsFinite(observedAt) || observedAt < receiverObservedAt) return false;
            receiverObservedAt = observedAt;
            var changedSession = receiverSession != session;
            if (!changedSession && (gameplay is null || gameplay == receiverGameplay)) return false;
            if ((changedSession && receiverSession.HasValue) || (receiverGameplay == false && gameplay != false))
                receiverResumedAt = observedAt;
            receiverSession = session;
            receiverGameplay = gameplay;
            return true;
        }
    }

    internal bool ReceiverAdmits(double capturedAt, Guid? session = null)
    {
        lock (receiverLock)
        {
            return receiverGameplay != false && double.IsFinite(capturedAt) && capturedAt >= receiverResumedAt
                && (session is null || receiverSession is null || receiverSession == session);
        }
    }
}
