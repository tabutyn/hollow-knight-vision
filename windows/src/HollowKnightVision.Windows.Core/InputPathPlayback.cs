using System.Diagnostics;

namespace HollowKnightVision.Windows.Core;

/// <summary>One clock for button transitions, lease renewal and recorded hero poses.</summary>
public static class InputPathPlayback
{
    public static RecordedGroundTruthSample[] Poses(RecordedInputPath path)
    {
        path.Validate();
        if (!path.IsValidPlaybackPath()) throw new InvalidDataException("Replay exceeds the 15-minute limits or contains invalid poses.");
        return path.GroundTruthTrace.Where(p => p.Sample.HeroAvailable
            && !string.IsNullOrWhiteSpace(p.Sample.SceneName)).ToArray();
    }

    public static async Task RunAsync(RecordedInputPath path,
        Func<InputButtons, CancellationToken, Task> sendState,
        Func<ReceiverGroundTruthSample, CancellationToken, Task> sendPose,
        CancellationToken cancellationToken)
    {
        var poses = Poses(path);
        var states = path.PlaybackStates().ToArray();
        var clock = Stopwatch.StartNew();
        int state = 0, pose = 0;
        while (state < states.Length || pose < poses.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stateAt = state < states.Length ? states[state].Offset : double.PositiveInfinity;
            var poseAt = pose < poses.Length ? poses[pose].Offset : double.PositiveInfinity;
            var remaining = Math.Min(stateAt, poseAt) - clock.Elapsed.TotalSeconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), cancellationToken).ConfigureAwait(false);
            if (stateAt <= poseAt)
            {
                await sendState(states[state++].HeldButtons, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Match the receiver's newest-pose semantics. Never flood it with a stale
                // backlog after a scheduler delay, or skip an input/scene boundary.
                while (pose + 1 < poses.Length && poses[pose + 1].Offset < stateAt
                    && poses[pose + 1].Offset <= clock.Elapsed.TotalSeconds
                    && poses[pose + 1].Sample.SceneName == poses[pose].Sample.SceneName) pose++;
                await sendPose(poses[pose++].Sample, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
