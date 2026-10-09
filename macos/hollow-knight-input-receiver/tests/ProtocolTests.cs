using System;

namespace HollowKnightVisionInputReceiver
{
    // Kept dependency-free so it can run before Unity/mod-loader setup.
    internal static class ProtocolTests
    {
        private static int Main()
        {
            InputSnapshot snapshot;
            Assert("valid snapshot", InputSnapshot.TryParse("{\"version\":1,\"type\":\"state\",\"sessionID\":\"test\",\"sequence\":9,\"enabled\":true,\"heldButtons\":33}", out snapshot));
            Assert("snapshot fields", snapshot.Session == "test" && snapshot.Sequence == 9 && snapshot.Enabled);
            Assert("left and Z bitset", (snapshot.Buttons & VisionButtons.Left) != 0 && (snapshot.Buttons & VisionButtons.ActionZ) != 0);
            Assert("down bit", (VisionButtons.Down & (VisionButtons)4) != 0);
            Assert("up bit", (VisionButtons.Up & (VisionButtons)8) != 0);
            Assert("inventory bit", (VisionButtons.Inventory & (VisionButtons)128) != 0);
            Assert("pause menu bit", (VisionButtons.PauseMenu & (VisionButtons)256) != 0);
            Assert("version rejected", !InputSnapshot.TryParse("{\"version\":2,\"type\":\"state\"}", out snapshot));
            Assert("negative sequence rejected", !InputSnapshot.TryParse("{\"version\":1,\"type\":\"state\",\"sessionID\":\"x\",\"sequence\":-1,\"enabled\":true,\"heldButtons\":0}", out snapshot));
            var ack = InputSnapshot.Ack("test", 9, true, VisionButtons.Right | VisionButtons.ActionX);
            Assert("ack", ack.Contains("\"ack\"") && ack.Contains("\"appliedButtons\":66") && ack.Contains("\"enabled\":true"));
            Assert("enabled neutral ack", InputSnapshot.Ack("test", 10, true, VisionButtons.None).Contains("\"enabled\":true"));
            Assert("disabled ack", InputSnapshot.Ack("test", 11, false, VisionButtons.ActionX).Contains("\"appliedButtons\":0"));
            Assert("opposite directions neutral", InputSnapshot.Normalize(VisionButtons.Left | VisionButtons.Right | VisionButtons.ActionA) == VisionButtons.ActionA);
            TestHeartbeatCoalescingAndTapDelivery();
            TestWatchdogAndDisconnectPriority();
            TestNewSessionInitialNeutralIsDelivered();
            TestBoundedAcknowledgements();
            TestCommitAcknowledgementGate();
            TestCapabilityAndPauseProtocol();
            TestPauseDisconnectPriority();
            TestPointerProtocolAndQueue();
            TestCheckpointProtocolAndQueue();
            TestPlayerPoseProtocolAndQueue();
            TestPlayerOpsProtocolAndQueue();
            TestGroundTruthProtocol();
            Console.WriteLine("Protocol and receiver-state tests passed");
            return 0;
        }

        private static void Assert(string name, bool value)
        {
            if (!value) throw new InvalidOperationException("Protocol assertion failed: " + name);
        }

        private static InputSnapshot State(long sequence, bool enabled, VisionButtons buttons)
        {
            return new InputSnapshot { Session = "test", Sequence = sequence, Enabled = enabled, Buttons = buttons };
        }

        private static void TestHeartbeatCoalescingAndTapDelivery()
        {
            var states = new InputStateQueue();
            Assert("initial state", states.Accept(State(0, true, VisionButtons.Right), 100));
            for (var sequence = 1; sequence <= 100; sequence++) Assert("heartbeat accepted", states.Accept(State(sequence, true, VisionButtons.Right), 100 + sequence));
            Assert("heartbeats do not queue", states.PendingTransitions == 1);

            InputSnapshot ignored;
            Assert("take hold", states.TryTake(out ignored) && ignored.Buttons == VisionButtons.Right);
            var sequenceNumber = 101L;
            for (var cycle = 0; cycle < 100; cycle++) {
                Assert("tap press accepted", states.Accept(State(sequenceNumber++, true, VisionButtons.ActionX), sequenceNumber));
                Assert("tap release accepted", states.Accept(State(sequenceNumber++, true, VisionButtons.None), sequenceNumber));
            }
            var presses = 0;
            var releases = 0;
            while (states.TryTake(out ignored)) {
                if ((ignored.Buttons & VisionButtons.ActionX) != 0) presses++;
                if (ignored.Enabled && ignored.Buttons == VisionButtons.None) releases++;
            }
            Assert("all bounded tap edges survive", presses == 100 && releases == 100);
        }

        private static void TestWatchdogAndDisconnectPriority()
        {
            var states = new InputStateQueue();
            states.Accept(State(0, true, VisionButtons.Left), 1000);
            Assert("fresh stream does not expire", !states.ExpireIfStale(1249, 250));
            Assert("monotonic watchdog expires", states.ExpireIfStale(1251, 250));
            InputSnapshot neutral;
            Assert("watchdog neutral wins", states.TryTake(out neutral) && !neutral.Enabled && neutral.Buttons == VisionButtons.None);

            states.Accept(State(1, true, VisionButtons.ActionZ), 2000);
            states.Disconnect();
            Assert("disconnect neutral wins", states.TryTake(out neutral) && !neutral.Enabled && neutral.Buttons == VisionButtons.None);
        }

        private static void TestNewSessionInitialNeutralIsDelivered()
        {
            var states = new InputStateQueue();
            var oldNeutral = new InputSnapshot { Session = "old", Sequence = 0, Enabled = false, Buttons = VisionButtons.None };
            var newNeutral = new InputSnapshot { Session = "new", Sequence = 0, Enabled = false, Buttons = VisionButtons.None };
            Assert("old session initial neutral accepted", states.Accept(oldNeutral, 100));
            InputSnapshot delivered;
            Assert("old initial neutral delivered", states.TryTake(out delivered) && delivered.Session == "old" && delivered.Sequence == 0);

            // This used to coalesce with the old disconnected neutral state,
            // leaving a new Vision session permanently waiting for its ACK.
            states.Disconnect();
            Assert("new session initial neutral accepted", states.Accept(newNeutral, 200));
            Assert("new session initial neutral delivered", states.TryTake(out delivered)
                && delivered.Session == "new" && delivered.Sequence == 0
                && !delivered.Enabled && delivered.Buttons == VisionButtons.None);
        }

        private static void TestBoundedAcknowledgements()
        {
            var acknowledgements = new AckQueue();
            for (var value = 0; value < AckQueue.MaxPending + 20; value++) acknowledgements.Enqueue(value.ToString());
            Assert("slow reader queue bounded", acknowledgements.Count == AckQueue.MaxPending);
            string first;
            Assert("slow reader drops oldest", acknowledgements.TryTake(out first) && first == "20");
        }

        private static void TestCommitAcknowledgementGate()
        {
            var gate = new CommitAckGate();
            string session;
            long sequence;
            bool enabled;
            VisionButtons buttons;
            gate.Set("test", 10, true, VisionButtons.ActionX);
            Assert("new state acknowledges after commit", gate.TryTake(out session, out sequence, out enabled, out buttons) && session == "test" && sequence == 10 && enabled && buttons == VisionButtons.ActionX);
            Assert("unchanged updates do not duplicate ack", !gate.TryTake(out session, out sequence, out enabled, out buttons));
            Assert("many unchanged updates do not duplicate ack", !gate.TryTake(out session, out sequence, out enabled, out buttons) && !gate.TryTake(out session, out sequence, out enabled, out buttons));
            gate.Set("test", 11, true, VisionButtons.None);
            Assert("next transition acknowledges once", gate.TryTake(out session, out sequence, out enabled, out buttons) && sequence == 11 && enabled && buttons == VisionButtons.None);
            Assert("next transition stops after one ack", !gate.TryTake(out session, out sequence, out enabled, out buttons));
        }

        private static void TestCapabilityAndPauseProtocol()
        {
            CapabilityRequest capability;
            Assert("capability request", CapabilityRequest.TryParse("{\"version\":2,\"type\":\"hello\",\"sessionID\":\"test\"}", out capability));
            Assert("marker defaults off", !capability.RenderFrameMarker);
            Assert("marker opt in", CapabilityRequest.TryParse("{\"version\":2,\"type\":\"hello\",\"sessionID\":\"test\",\"renderFrameMarker\":true}", out capability) && capability.RenderFrameMarker);
            var capabilityAck = CapabilityRequest.Ack(capability.Session);
            Assert("capability ack advertises pause", capabilityAck.Contains("\"pause-lease-v1\"") && capabilityAck.Contains("\"pauseLeaseMilliseconds\":2000"));
            Assert("capability ack advertises menu shortcuts", capabilityAck.Contains("\"menu-shortcuts-v1\""));
            Assert("capability ack advertises pointer input", capabilityAck.Contains("\"pointer-events-v1\""));
            Assert("capability ack advertises player checkpoints", capabilityAck.Contains("\"player-checkpoint-v1\""));
            Assert("capability ack advertises player pose playback", capabilityAck.Contains("\"player-pose-playback-v1\""));
            Assert("capability ack advertises ground truth", capabilityAck.Contains("\"ground-truth-telemetry-v1\""));
            Assert("capability ack advertises player ops", capabilityAck.Contains("\"player-ops-v1\""));

            PauseRequest pause;
            Assert("pause request", PauseRequest.TryParse("{\"version\":2,\"type\":\"pause\",\"sessionID\":\"test\",\"commandID\":\"one\",\"leaseMilliseconds\":9000}", out pause));
            Assert("pause lease capped", pause.Kind == PauseCommandKind.Pause && pause.LeaseMilliseconds == 2000);
            Assert("pause ack", PauseRequest.Ack(pause, true).Contains("\"pauseAck\"") && PauseRequest.Ack(pause, true).Contains("\"paused\":true"));
            Assert("zero lease rejected", !PauseRequest.TryParse("{\"version\":2,\"type\":\"pause\",\"sessionID\":\"test\",\"commandID\":\"bad\",\"leaseMilliseconds\":0}", out pause));
            Assert("old control protocol rejected", !CapabilityRequest.TryParse("{\"version\":1,\"type\":\"hello\",\"sessionID\":\"test\"}", out capability));
        }

        private static void TestPauseDisconnectPriority()
        {
            var queue = new PauseRequestQueue();
            PauseRequest pause;
            Assert("parse pause for queue", PauseRequest.TryParse("{\"version\":2,\"type\":\"pause\",\"sessionID\":\"test\",\"commandID\":\"one\",\"leaseMilliseconds\":2000}", out pause));
            queue.Accept(pause);
            queue.Disconnect("test");
            Assert("disconnect replaces pending pause", queue.Count == 1);
            queue.Disconnect(null);
            Assert("new connection setup preserves forced resume", queue.Count == 1);
            PauseRequest release;
            Assert("disconnect queues forced resume", queue.TryTake(out release) && release.IsDisconnect && release.Kind == PauseCommandKind.Resume);

            queue.Accept(pause);
            PauseRequest resume;
            Assert("parse resume", PauseRequest.TryParse("{\"version\":2,\"type\":\"resume\",\"sessionID\":\"test\",\"commandID\":\"two\"}", out resume));
            queue.Accept(resume);
            Assert("resume replaces queued renewals", queue.Count == 1 && queue.TryTake(out release) && release.Kind == PauseCommandKind.Resume);
        }

        private static void TestPointerProtocolAndQueue()
        {
            PointerRequest pointer;
            Assert("pointer request", PointerRequest.TryParse("{\"version\":2,\"type\":\"pointer\",\"sessionID\":\"test\",\"sequence\":3,\"kind\":\"moved\",\"normalizedX\":0.25,\"normalizedY\":0.75,\"clickCount\":1}", out pointer));
            Assert("pointer coordinates", pointer.Sequence == 3 && pointer.NormalizedX == 0.25 && pointer.NormalizedY == 0.75);
            Assert("pointer bounds", !PointerRequest.TryParse("{\"version\":2,\"type\":\"pointer\",\"sessionID\":\"test\",\"sequence\":4,\"kind\":\"moved\",\"normalizedX\":2,\"normalizedY\":0.5}", out pointer));

            var queue = new PointerRequestQueue();
            Assert("pointer down parse", PointerRequest.TryParse("{\"version\":2,\"type\":\"pointer\",\"sessionID\":\"test\",\"sequence\":5,\"kind\":\"leftDown\",\"normalizedX\":0.5,\"normalizedY\":0.5}", out pointer));
            Assert("pointer down queued", queue.Accept(pointer));
            Assert("stale pointer rejected", !queue.Accept(pointer));
            queue.Disconnect("test");
            PointerRequest disconnected;
            Assert("disconnect replaces pointer edges", queue.Count == 1 && queue.TryTake(out disconnected) && disconnected.IsDisconnect);
        }

        private static void TestCheckpointProtocolAndQueue()
        {
            CheckpointRequest request;
            Assert("capture checkpoint request", CheckpointRequest.TryParse(
                "{\"version\":2,\"type\":\"captureCheckpoint\",\"sessionID\":\"test\",\"commandID\":\"one\"}",
                out request));
            Assert("capture checkpoint kind", request.Kind == CheckpointCommandKind.Capture
                && request.Checkpoint == null);

            const string checkpoint = "{\"sceneName\":\"Crossroads_01\",\"entryGateName\":\"left1\",\"heroX\":10.5,\"heroY\":4.25,\"heroZ\":0,\"velocityX\":0,\"velocityY\":0,\"facingRight\":true,\"grounded\":true,\"cameraX\":11,\"cameraY\":6,\"cameraZ\":-38.1,\"cameraTargetX\":11,\"cameraTargetY\":6,\"cameraTargetZ\":0}";
            Assert("restore checkpoint request", CheckpointRequest.TryParse(
                "{\"version\":2,\"type\":\"restoreCheckpoint\",\"sessionID\":\"test\",\"commandID\":\"two\",\"checkpoint\":" + checkpoint + "}",
                out request));
            Assert("restore checkpoint fields", request.Kind == CheckpointCommandKind.Restore
                && request.Checkpoint.SceneName == "Crossroads_01"
                && request.Checkpoint.EntryGateName == "left1"
                && request.Checkpoint.HeroX == 10.5
                && request.Checkpoint.FacingRight);
            Assert("restore requires checkpoint", !CheckpointRequest.TryParse(
                "{\"version\":2,\"type\":\"restoreCheckpoint\",\"sessionID\":\"test\",\"commandID\":\"bad\"}",
                out request));

            var queue = new CheckpointRequestQueue();
            Assert("capture reparsed", CheckpointRequest.TryParse(
                "{\"version\":2,\"type\":\"captureCheckpoint\",\"sessionID\":\"test\",\"commandID\":\"three\"}",
                out request));
            Assert("checkpoint queued", queue.Accept(request) && queue.Count == 1);
            queue.Disconnect();
            Assert("disconnect clears checkpoint", queue.Count == 0);

            var ackCheckpoint = new PlayerCheckpoint {
                SceneName = "Crossroads_01", EntryGateName = "left1",
                HeroX = 10.5, HeroY = 4.25,
                CameraZ = -38.1, Grounded = true, FacingRight = true
            };
            var ack = CheckpointRequest.Ack(request, true, ackCheckpoint, null);
            Assert("checkpoint ack", ack.Contains("\"captureCheckpointAck\"")
                && ack.Contains("\"accepted\":true") && ack.Contains("\"Crossroads_01\"")
                && ack.Contains("\"entryGateName\":\"left1\""));
        }

        private static void TestGroundTruthProtocol()
        {
            var telemetry = new GroundTruthTelemetry {
                Sequence = 7, UnityFrame = 99, UnityRealtime = 12.5,
                SceneName = "Tutorial_01", HeroAvailable = true,
                HeroX = 36.25, HeroY = 11.5, Grounded = true,
                CameraAvailable = true, CameraX = 37.25, CameraY = 14.1,
                OrthographicSize = 480, PixelsPerWorldUnitX = 57.6,
                PixelsPerWorldUnitY = 57.6, HeroScreenX = 1000, HeroScreenY = 420,
                ProjectionPixelWidth = 1920, ProjectionPixelHeight = 1080,
                ScreenWidth = 1920, ScreenHeight = 1080
            };
            var json = telemetry.ToJson("test");
            Assert("ground truth type", json.Contains("\"type\":\"groundTruth\""));
            Assert("ground truth scene", json.Contains("\"sceneName\":\"Tutorial_01\""));
            Assert("ground truth camera", json.Contains("\"cameraX\":37.25"));
            Assert("ground truth projection", json.Contains("\"pixelsPerWorldUnitY\":57.6")
                && json.Contains("\"projectionPixelHeight\":1080"));
        }

        private static void TestPlayerOpsProtocolAndQueue()
        {
            PlayerOpsRequest request;
            const string state = "{\"maxHealth\":9,\"health\":4,\"lifebloodSeed\":3,\"mana\":120,\"extraManaSlots\":1,\"geo\":4567,\"invincible\":false}";
            Assert("player ops query", PlayerOpsRequest.TryParse(
                "{\"version\":2,\"type\":\"playerOps\",\"sessionID\":\"test\",\"commandID\":\"one\",\"operation\":\"query\"}", out request)
                && request.Kind == PlayerOpsKind.Query);
            Assert("player ops apply", PlayerOpsRequest.TryParse(
                "{\"version\":2,\"type\":\"playerOps\",\"sessionID\":\"test\",\"commandID\":\"two\",\"operation\":\"apply\",\"state\":" + state + "}", out request)
                && request.State.Health == 4 && request.State.Mana == 120
                && !request.State.Invincible);
            Assert("legacy player ops defaults invincibility on", PlayerOpsRequest.TryParse(
                "{\"version\":2,\"type\":\"playerOps\",\"sessionID\":\"test\",\"commandID\":\"legacy\",\"operation\":\"apply\",\"state\":" + state.Replace(",\"invincible\":false", "") + "}", out request)
                && request.State.Invincible);
            Assert("player ops rejects invalid health", !PlayerOpsRequest.TryParse(
                "{\"version\":2,\"type\":\"playerOps\",\"sessionID\":\"test\",\"commandID\":\"bad\",\"operation\":\"apply\",\"state\":" + state.Replace("\"health\":4", "\"health\":10") + "}", out request));
            Assert("player ops restore enemies", PlayerOpsRequest.TryParse(
                "{\"version\":2,\"type\":\"playerOps\",\"sessionID\":\"test\",\"commandID\":\"three\",\"operation\":\"restoreEnemies\"}", out request)
                && request.Kind == PlayerOpsKind.RestoreEnemies);
            var queue = new PlayerOpsRequestQueue();
            Assert("player ops queued", queue.Accept(request) && queue.Count == 1);
            var ack = PlayerOpsRequest.Ack(request, true, null, 3, null);
            Assert("player ops ack", ack.Contains("\"playerOpsAck\"")
                && ack.Contains("\"enemiesRestored\":3"));
        }

        private static void TestPlayerPoseProtocolAndQueue()
        {
            PlayerPoseRequest pose;
            const string json = "{\"version\":2,\"type\":\"playerPose\",\"sessionID\":\"test\",\"sequence\":7,\"sceneName\":\"Tutorial_01\",\"heroX\":36.25,\"heroY\":11.5,\"heroZ\":0.004,\"velocityX\":-2,\"velocityY\":0,\"facingRight\":false,\"grounded\":true}";
            Assert("player pose request", PlayerPoseRequest.TryParse(json, out pose));
            Assert("player pose fields", pose.Sequence == 7 && pose.SceneName == "Tutorial_01"
                && pose.HeroX == 36.25 && pose.VelocityX == -2 && !pose.FacingRight);
            Assert("player pose rejects nonfinite coordinates", !PlayerPoseRequest.TryParse(
                json.Replace("36.25", "1e999"), out pose));

            var queue = new PlayerPoseRequestQueue();
            Assert("first player pose reparsed", PlayerPoseRequest.TryParse(json, out pose));
            Assert("player pose queued", queue.Accept(pose) && queue.Count == 1);
            PlayerPoseRequest newer;
            Assert("newer player pose parsed", PlayerPoseRequest.TryParse(
                json.Replace("\"sequence\":7", "\"sequence\":8").Replace("36.25", "37.5"),
                out newer));
            Assert("newest player pose replaces old", queue.Accept(newer) && queue.Count == 1);
            PlayerPoseRequest delivered;
            Assert("newest player pose delivered", queue.TryTake(out delivered)
                && delivered.Sequence == 8 && delivered.HeroX == 37.5);
            Assert("stale player pose rejected", !queue.Accept(pose));
            queue.Disconnect();
            Assert("disconnect clears player pose", queue.Count == 0);
        }

    }
}
