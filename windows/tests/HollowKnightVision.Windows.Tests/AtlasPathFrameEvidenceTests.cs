using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class AtlasPathFrameEvidenceTests
{
    internal static void TestGameplayPathFrameRequiresPositiveRecognitionAndSceneSignal()
    {
        var frames = new (bool Signal, string? Recognition)[]
        {
            (true, "game"), (true, null), (true, "pause"), (true, "inventory"), (false, "Gameplay"), (true, "Gameplay")
        };
        Require(frames.Select(frame => AtlasPathFrameEvidence.Classify(frame.Signal, frame.Recognition)
                == AtlasPathFrameState.Gameplay).SequenceEqual([true, false, false, false, false, true]),
            "Only positive recognition from a frame with scene signal should publish a Game Play path.");
    }

    internal static void TestHackerUsesExactPreviewEvidenceAndDropsOldResults()
    {
        var state = new AtlasPathFrameEvidence();
        var session = Guid.NewGuid();
        state.ObserveHacker(1, 10, AtlasPathFrameState.Gameplay);
        state.ObserveHacker(1, 12, AtlasPathFrameState.Gameplay);
        Require(Enumerable.Range(9, 5).Select(id => Admits(id)).SequenceEqual([false, true, false, true, false]),
            "A visually admitted frame cannot authorize another Hacker bitmap.");
        state.ObserveHacker(1, 13, AtlasPathFrameState.Hidden);
        state.ObserveHacker(1, 12, AtlasPathFrameState.Gameplay);
        Require(Enumerable.Range(9, 5).All(id => !Admits(id)),
            "A late frame from before a menu cannot revive the preview.");
        state.ObserveHacker(1, 14, AtlasPathFrameState.Gameplay);
        Require(Admits(14), "A newly recognized gameplay preview should show its route.");
        for (var id = 15; id < 50; id++) state.ObserveHacker(1, id, AtlasPathFrameState.Gameplay);
        Require(!Admits(14) && Admits(49), "Visual evidence must be bounded to recent previews.");
        state.ObserveHacker(2, 1, AtlasPathFrameState.Unrecognized);
        state.ObserveHacker(1, 51, AtlasPathFrameState.Gameplay);
        Require(!state.AdmitsHacker(2, 1, 52, session, true, null),
            "Late analysis from another generation cannot seed the new preview.");

        bool Admits(int id) => state.AdmitsHacker(1, id, id, session, true, null);
    }

    internal static void TestMatchedHackerGameplaySurvivesMissesUntilPauseOrBlackout()
    {
        var state = new AtlasPathFrameEvidence();
        var session = Guid.NewGuid();
        state.ObserveReceiver(session, true, 0);
        for (var id = 1; id <= 90; id++)
        {
            var recognized = id % 2 == 0 ? "game" : null;
            state.ObserveHacker(1, id, AtlasPathFrameEvidence.Classify(true, recognized));
            Require(state.AdmitsHacker(1, id, id, session, true, true),
                "Exact receiver gameplay should tolerate a missed visual recognition.");
        }
        Require(!state.AdmitsHacker(1, 90, 90, session, true, false)
            && !state.AdmitsHacker(1, 90, 90, session, false, true),
            "A matched menu sample or a black frame must hide the path despite visual Gameplay.");
        state.ObserveHacker(1, 91, AtlasPathFrameEvidence.Classify(true, "pause"));
        Require(!state.AdmitsHacker(1, 91, 91, session, true, true),
            "A positively recognized pause must hide the matching preview.");
        Require(state.ObserveReceiver(session, false, 92)
            && !state.AdmitsHacker(1, 91, 91, session, true, true)
            && !state.AdmitsHacker(1, 93, 93, session, true, true),
            "Mod pause should hide the overlay immediately, including when no matching capture arrives.");
        Require(state.ObserveReceiver(session, true, 94)
            && !state.AdmitsHacker(1, 93, 93, session, true, true)
            && state.AdmitsHacker(1, 95, 95, session, true, true),
            "Recovery must use a frame acquired after receiver gameplay resumes.");
    }

    internal static void TestReceiverStateVetoesGameplayAndSurvivesCaptureRestart()
    {
        var state = new AtlasPathFrameEvidence();
        var session = Guid.NewGuid();
        Require(state.AdmitsGameplay(10, 10, modded: true));
        Require(!state.ObserveReceiver(session, true, double.NaN));
        Require(state.ObserveReceiver(session, true, 10));
        Require(!state.ObserveReceiver(session, true, 10.5));
        Require(state.ObserveReceiver(session, false, 11));
        Require(!state.AdmitsGameplay(11, 11, modded: true)
            && !state.AdmitsHacker(1, 10, 10, session, true, true)
            && !state.ObserveReceiver(session, null, 11.5));
        state.ResetGameplayPreview();
        Require(!state.AdmitsGameplay(1, 12, modded: true)
            && state.AdmitsGameplay(1, 12, modded: false));
        Require(state.ObserveReceiver(session, true, 13));
        Require(!state.ObserveReceiver(session, false, 12)
            && !state.AdmitsGameplay(1, 12, modded: true)
            && state.AdmitsGameplay(2, 14, modded: true));
        Require(!state.AdmitsGameplay(2, double.PositiveInfinity, modded: true));
        var replacement = Guid.NewGuid();
        Require(state.ObserveReceiver(replacement, null, 15)
            && !state.AdmitsHacker(1, 15, 15, session, true, true)
            && state.AdmitsHacker(2, 1, 16, replacement, true, true));
    }

    internal static void TestBlackoutRetiresThePreviousRegisteredPath()
    {
        var state = new AtlasPathFrameEvidence();
        state.ObserveGameplayPreview(1, true);
        Require(state.AdmitsGameplay(1, 10, modded: false));
        state.ObserveGameplayPreview(2, false);
        state.ObserveGameplayPreview(3, true);
        Require(!state.AdmitsGameplay(1, 10, modded: false)
            && !state.AdmitsGameplay(2, 11, modded: false)
            && state.AdmitsGameplay(3, 12, modded: false));
        state.ResetGameplayPreview();
        Require(state.AdmitsGameplay(1, 13, modded: false));
    }

    internal static void TestReceiverGameplayStartRequiresFreshPositiveEvidence()
    {
        var state = new AtlasPathFrameEvidence();
        var session = Guid.NewGuid();
        Require(!state.ReceiverConfirmsGameplay(10));
        state.ObserveReceiver(session, true, 10);
        Require(!state.ReceiverConfirmsGameplay(9.99) && state.ReceiverConfirmsGameplay(10.25));
        state.ObserveReceiver(session, null, 10.3);
        Require(!state.ReceiverConfirmsGameplay(10.3), "A payload without gameplay status must not renew the old proof.");
        state.ObserveReceiver(session, true, 11);
        Require(state.ReceiverConfirmsGameplay(11.1));
        state.ObserveReceiver(session, false, 11.15);
        Require(!state.ReceiverConfirmsGameplay(11.15), "Receiver menu evidence must immediately veto a gameplay start.");
        state.ObserveReceiver(Guid.NewGuid(), null, 12);
        Require(!state.ReceiverConfirmsGameplay(12));
    }

    internal static void TestPreviewSignalMatchesTheFullTrackingProfile()
    {
        const int width = 64, height = 36;
        var random = new Random(42);
        foreach (var count in new[] { 0, 8, width * height / 20 - 1, width * height / 20, width * height })
        {
            var pixels = new byte[width * height * 4];
            foreach (var i in Enumerable.Range(0, width * height).OrderBy(_ => random.Next()).Take(count))
                pixels[i * 4 + i % 3] = 5;
            var frame = new BgraFrame(width, height, width * 4, pixels, count, DateTimeOffset.UnixEpoch);
            var direct = FrameRegionRenderer.ContainsGameplaySignal(frame);
            Require(direct == FrameRegionRenderer.SignalProfile(frame).HasGameplaySignal
                && direct == (count >= width * height / 20), "Preview and tracking must agree at the signal cutoff.");
        }
    }

    private static void Require(bool condition, string message = "Unexpected Atlas path presentation evidence.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
