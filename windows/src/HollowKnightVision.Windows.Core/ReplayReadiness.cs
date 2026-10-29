namespace HollowKnightVision.Windows.Core;

public sealed class ReplayReadiness
{
    private int consecutive;
    private double lastCheck = double.NegativeInfinity, lastVision = double.NegativeInfinity;
    private long lastFrame = -1;
    public string WaitingFor { get; private set; } = "fresh gameplay";
    public bool Observe(RecordedGameCheckpoint expected, ReceiverGroundTruthSample? actual,
        double now, double visionTimestamp, bool verifiedGround, bool committedWorld)
    {
        if (now - lastCheck < .099) return false;
        lastCheck = now;
        var fresh = actual is not null && actual.UnityFrame != lastFrame && visionTimestamp > lastVision
            && now - visionTimestamp is >= 0 and < .5;
        var matches = actual is not null && Matches(expected, actual);
        WaitingFor = !fresh ? "fresh game and vision frames" : !matches ? "recorded player/camera pose"
            : !verifiedGround ? "verified ground features" : !committedWorld ? "committed world evidence" : "stable samples";
        consecutive = fresh && matches && verifiedGround && committedWorld ? consecutive + 1 : 0;
        if (actual is not null) lastFrame = actual.UnityFrame;
        lastVision = visionTimestamp;
        return consecutive >= 4;
    }

    public static bool Matches(RecordedGameCheckpoint expected, ReceiverGroundTruthSample actual)
    {
        static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= .05;
        return actual.HeroAvailable && actual.CameraAvailable && actual.HasFiniteCoordinates()
            && actual.SceneName == expected.SceneName && actual.FacingRight == expected.FacingRight && actual.Grounded == expected.Grounded
            && Near(actual.HeroX, expected.HeroX) && Near(actual.HeroY, expected.HeroY) && Near(actual.HeroZ, expected.HeroZ)
            && Near(actual.VelocityX, expected.VelocityX) && Near(actual.VelocityY, expected.VelocityY)
            && Near(actual.CameraX, expected.CameraX) && Near(actual.CameraY, expected.CameraY) && Near(actual.CameraZ, expected.CameraZ)
            && Near(actual.CameraTargetX, expected.CameraTargetX) && Near(actual.CameraTargetY, expected.CameraTargetY) && Near(actual.CameraTargetZ, expected.CameraTargetZ);
    }
}
