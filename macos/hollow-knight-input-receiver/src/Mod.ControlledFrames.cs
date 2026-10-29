using System;
using System.Diagnostics;
using UnityEngine;

namespace HollowKnightVisionInputReceiver
{
    internal sealed partial class ReceiverBehaviour
    {
        private bool controlledClockOwned;
        private float captureDeltaBefore;
        private ControlledFrameRequest pendingControlledFrame;
        private int controlledArmFrame;
        private long controlledDeadline;
        private string completedControlledFrame;

        private void UpdateControlledFrame()
        {
            if (pendingControlledFrame != null && Stopwatch.GetTimestamp() > controlledDeadline)
                EndControlledFrames("Gameplay camera did not render the controlled frame.");
            ControlledFrameRequest request;
            if (server.TryTakeControlledFrame(out request)) {
                if (!visionOwnsPause || pauseSession != request.Session) {
                    server.QueueControlledFrameAck(request, false, -1, 0, "Controlled replay requires this session's pause lease.");
                } else if (request.End) {
                    EndControlledFrames("Controlled replay ended.");
                    server.QueueControlledFrameAck(request, true, Time.frameCount, 0, null);
                } else if (pendingControlledFrame != null || Time.timeScale != 0f) {
                    server.QueueControlledFrameAck(request, false, -1, 0, "Game is not at a paused frame boundary.");
                } else if (GameManager.instance == null || GameManager.instance.sceneName != request.Pose.SceneName
                    || HeroController.SilentInstance == null || !HeroController.SilentInstance.gameObject.activeInHierarchy) {
                    server.QueueControlledFrameAck(request, false, -1, 0, "Recorded gameplay scene is unavailable.");
                } else {
                    if (!controlledClockOwned) { captureDeltaBefore = Time.captureDeltaTime; controlledClockOwned = true; }
                    completedControlledFrame = null;
                    pendingControlledFrame = request;
                    controlledArmFrame = Time.frameCount;
                    controlledDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
                    // Unity already computed this frame's zero delta. The next
                    // frame is the sole advancing tick, independent of wall time.
                    Time.captureDeltaTime = (float)request.Delta;
                    Time.timeScale = 1f;
                }
            }
            if (pendingControlledFrame != null && Time.frameCount == controlledArmFrame + 1) {
                // Set the edge before InControl consumes the advancing tick.
                // No synthetic input ACK: this command has its own render ACK.
                device.SetState(null, -1, true, pendingControlledFrame.Buttons);
            }
        }

        private void ApplyControlledPose()
        {
            if (pendingControlledFrame == null || Time.frameCount != controlledArmFrame + 1) return;
            ApplyPlayerPose(pendingControlledFrame.Pose);
            var hero = HeroController.SilentInstance;
            if (hero != null && hero.cState != null && !pendingControlledFrame.Pose.Grounded) hero.cState.onGround = false;
        }

        private void CompleteControlledFrame()
        {
            if (pendingControlledFrame == null || Time.frameCount <= controlledArmFrame) return;
            var request = pendingControlledFrame;
            var delta = Time.deltaTime;
            var valid = Time.frameCount == controlledArmFrame + 1 && Math.Abs(delta - request.Delta) < 0.00001;
            Time.timeScale = 0f;
            pendingControlledFrame = null;
            completedControlledFrame = valid ? request.CommandID : null;
            server.QueueControlledFrameAck(request, valid, Time.frameCount, delta,
                valid ? null : "Controlled simulation delta or frame count was incorrect.");
            // Subsequent paused renders keep their own real frame IDs and live
            // camera truth. Never relabel newer pixels with an old frame ID.
        }

        private void EndControlledFrames(string failure)
        {
            if (pendingControlledFrame != null) {
                server.QueueControlledFrameAck(pendingControlledFrame, false, Time.frameCount, 0, failure);
                pendingControlledFrame = null;
            }
            if (controlledClockOwned) {
                Time.timeScale = 0f;
                Time.captureDeltaTime = captureDeltaBefore;
                controlledClockOwned = false;
                device.SetState(null, -1, false, VisionButtons.None);
            }
            completedControlledFrame = null;
        }
    }
}
