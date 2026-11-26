using System.IO;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    private void QueueMatchedHackerPlayback(HackerMatchedFrame match, long generation)
    {
        if (Volatile.Read(ref botPlayback) is { HackerFrames: { } frames } run
            && run.Generation == generation && match.Sample.AtlasGameplayActive == true
            && match.Capture.CapturedAt >= run.StartedAt)
            frames.Writer.TryWrite(match);
    }

    private async Task RunMatchedHackerPlaybackAsync(BotRun run)
    {
        var visit = new BotHackerVisit(run.Authored, hackerPathLinks);
        Guid? previousSession = null;
        var lastCapture = double.NegativeInfinity;
        try
        {
            await foreach (var match in run.HackerFrames!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (thinkingControlLock)
                {
                    if (!ReferenceEquals(Volatile.Read(ref botPlayback), run) || !gameControls.OwnsBotControl(run.Owner)) return;
                    var capture = match.Capture;
                    var sample = match.Sample;
                    // Menu navigation still uses the visual stencils. Once it has
                    // handed off, Game Play vision cannot reset Hacker playback.
                    if (thinkingLease is not null || !run.StartedInGameplay && !run.Playing
                        || capture.CapturedAt <= lastCapture
                        || Volatile.Read(ref run.Loaded)?.Policy is not { } playback
                        || !AdmitsMatchedHackerPlayback(match)) continue;
                    lastCapture = capture.CapturedAt;
                    if (!run.UsesReceiverFrames || previousSession != sample.SessionId)
                    {
                        Volatile.Write(ref run.UsesReceiverFrames, true);
                        previousSession = sample.SessionId;
                        playback.ResetHistory();
                    }
                    if (run.PlaybackUpdate.Status == "Bot waiting for gameplay") visit.Interrupt();
                    if (ObserveBotStartupLocked(run, capture.Image, run.Generation, capture.CapturedAt, admitted: true,
                        recognizedGameplay: false, sample, menu: null, hud: null) is null
                        || !playback.WantsObservation(capture.CapturedAt)) continue;
                    var projected = MatchedHackerBotFrame(match, run.Generation);
                    var availableAt = ReadBotObservationTime();
                    var row = visit.ObserveMatched(projected, availableAt);
                    if (!AdmitsMatchedHackerPlayback(match))
                    {
                        PauseMatchedHackerPlayback(run);
                        continue;
                    }

                    var contacts = visit.ActiveWallContacts;
                    Volatile.Write(ref hackerWallContacts, contacts.Count == 0 ? null
                        : new(new(run), Volatile.Read(ref botRouteEpoch), run.Generation, capture.Image.CaptureId,
                            capture.CapturedAt, projected.RoomId, null, sample.SessionId, sample.SceneName, contacts));
                    Volatile.Read(ref run.Evaluation)?.RecordFrame(capture.Image,
                        new(capture.CapturedAt, availableAt, run.Generation, projected.RoomId, 0),
                        row, sample.SceneName, [], row?.AuthoredTerrain, null, capture.UnityFrame, awaitHackerVisual: true);
                    DriveBotFrame(run, playback, row, capture.MovementButtons, sample.SceneName);
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            EndBotFromWorker(run, $"Bot playback stopped. {error.Message}");
        }
    }

    private bool AdmitsMatchedHackerPlayback(HackerMatchedFrame match) =>
        match.Sample is { AtlasGameplayActive: true, SceneName.Length: > 0 }
        && match.Capture.UnityFrame == HackerFrameSynchronizer.Key(match.Sample.UnityFrame)
        && !Volatile.Read(ref profileAtlasHeld)
        && BotCaptureClock() - match.Capture.CapturedAt is >= 0 and <= BotRecordingContract.MaximumCaptureAge
        && atlasPathEvidence.ReceiverAdmitsHacker(match.Capture.CapturedAt, match.Sample.SessionId,
            match.Sample.SceneName, match.Sample.Sequence);

    private void ObserveHackerReceiverTransition()
    {
        if (Volatile.Read(ref botPlayback) is not { UsesReceiverFrames: true } run) return;
        lock (thinkingControlLock)
            if (ReferenceEquals(Volatile.Read(ref botPlayback), run) && thinkingLease is null)
                PauseMatchedHackerPlayback(run);
    }

    private void PauseMatchedHackerPlayback(BotRun run)
    {
        Volatile.Read(ref run.Loaded)?.Policy?.ResetHistory();
        Volatile.Write(ref hackerWallContacts, null);
        gameControls.NeutralizeBotControl(run.Owner);
        Volatile.Write(ref run.PlaybackUpdate, new("Bot waiting for gameplay"));
    }
}
