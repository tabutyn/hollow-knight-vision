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
    private (string Scene, ulong FirstSequence)? receiverRoom;
    private double receiverObservedAt = double.NegativeInfinity;
    private double receiverGameplayObservedAt = double.NegativeInfinity;
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
        bool? exactReceiverGameplay, string? scene = null, ulong sequence = 0)
    {
        if (!hasSceneSignal || !ReceiverAdmitsHacker(capturedAt, session, scene, sequence)) return false;
        if (generation == visualGeneration && id <= lastHiddenId) return false;
        var recognized = generation == visualGeneration
            ? hackerVisual.FirstOrDefault(item => item.Id == id).State : AtlasPathFrameState.Unrecognized;
        return exactReceiverGameplay ?? (recognized == AtlasPathFrameState.Gameplay);
    }

    // Receiver state arrives even when the game has stopped producing camera matches.
    internal bool ObserveReceiver(Guid session, bool? gameplay, double observedAt, string? scene = null, ulong sequence = 0)
    {
        lock (receiverLock)
        {
            if (!double.IsFinite(observedAt) || observedAt < receiverObservedAt) return false;
            receiverObservedAt = observedAt;
            var changedSession = receiverSession != session;
            if (changedSession || gameplay.HasValue) receiverGameplayObservedAt = observedAt;
            if (changedSession || gameplay == false) receiverRoom = null;
            var changedRoom = false;
            if (gameplay == true && scene is { Length: > 0 } && scene != receiverRoom?.Scene)
            {
                receiverRoom = (scene, sequence);
                changedRoom = true;
            }
            if (!changedSession && !changedRoom && (gameplay is null || gameplay == receiverGameplay)) return false;
            if ((changedSession && receiverSession.HasValue) || (receiverGameplay == false && gameplay != false))
                receiverResumedAt = observedAt;
            receiverSession = session;
            receiverGameplay = gameplay;
            return true;
        }
    }

    internal bool ReceiverAdmits(double capturedAt, Guid? session = null)
    {
        lock (receiverLock) return ReceiverAdmitsLocked(capturedAt, session);
    }

    internal bool ReceiverAdmitsHacker(double capturedAt, Guid session, string? scene, ulong sequence)
    {
        lock (receiverLock) return ReceiverAdmitsLocked(capturedAt, session)
            && (receiverRoom is not { } room || room.Scene == scene && sequence >= room.FirstSequence);
    }

    private bool ReceiverAdmitsLocked(double capturedAt, Guid? session) =>
        receiverGameplay != false && double.IsFinite(capturedAt) && capturedAt >= receiverResumedAt
        && (session is null || receiverSession is null || receiverSession == session);

    internal bool ReceiverConfirmsGameplay(double now)
    {
        lock (receiverLock)
            return receiverGameplay == true && now - receiverGameplayObservedAt is >= 0 and <= .25;
    }
}
