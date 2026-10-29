using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public sealed record ReceiverControlledFrameCommand(
    int Version, string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    ulong Sequence, string Kind, double DeltaSeconds, InputButtons HeldButtons,
    ReceiverPlayerPoseCommand? Pose);

public sealed record ReceiverControlledFrameAcknowledgement(
    int Version, string Type,
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("commandID")] Guid CommandId,
    bool Accepted, int UnityFrame, double DeltaSeconds, string? Failure)
{
    [JsonIgnore] public double GameWaitMilliseconds { get; init; }
}

public sealed record ControlledReplayFrame(int Index, double Offset, double DeltaSeconds,
    InputButtons Buttons, ReceiverGroundTruthSample Pose);

public static class ControlledFrameReplay
{
    public const string Capability = "controlled-frame-replay-v1";

    // Sample the full source, including poses bracketing each segment endpoint.
    // Only hero coordinates/velocity are interpolated and transmitted to Unity.
    public static ControlledReplayFrame[] Frames(RecordedInputPath source, int second)
    {
        var poses = InputPathPlayback.Poses(source);
        var duration = Math.Min(1, source.Duration - second);
        if (second < 0 || duration <= 0 || poses.Length == 0) throw new InvalidDataException("Controlled replay needs recorded hero poses.");
        var count = (int)Math.Ceiling(duration * 30 - 1e-6);
        var states = source.PlaybackStates().ToArray();
        return Enumerable.Range(0, count).Select(i => {
            var offset = Math.Min(duration, (i + 1) / 30.0);
            var at = second + offset;
            var right = Array.FindIndex(poses, p => p.Offset >= at);
            if (right < 0) right = poses.Length - 1;
            var left = right > 0 && poses[right].Offset > at ? right - 1 : right;
            var a = poses[left]; var b = poses[right];
            if (a.Sample.SceneName != b.Sample.SceneName) throw new InvalidDataException("Controlled replay cannot interpolate across a scene transition.");
            var t = b.Offset > a.Offset ? Math.Clamp((at - a.Offset) / (b.Offset - a.Offset), 0, 1) : 0;
            double Mix(double x, double y) => x + (y - x) * t;
            var pose = a.Sample with {
                HeroX = Mix(a.Sample.HeroX, b.Sample.HeroX), HeroY = Mix(a.Sample.HeroY, b.Sample.HeroY),
                HeroZ = Mix(a.Sample.HeroZ, b.Sample.HeroZ), VelocityX = Mix(a.Sample.VelocityX, b.Sample.VelocityX),
                VelocityY = Mix(a.Sample.VelocityY, b.Sample.VelocityY) };
            // Input is sampled at the beginning of this tick; the authoritative
            // hero pose is applied at its end. Synthetic slice releases never
            // erase keys held across a one-second boundary.
            var buttons = states.LastOrDefault(s => s.Offset <= second + i / 30.0).HeldButtons;
            return new ControlledReplayFrame(i, offset, offset - i / 30.0, buttons, pose);
        }).ToArray();
    }
}
