namespace HollowKnightVision.Windows.Core;

/// <summary>Keeps a jump separate from the actual buttons reported to the game and policy.</summary>
public sealed class JumpContactGate
{
    private const double CooldownSeconds = .4;
    private double lastJump = double.NegativeInfinity;

    public void Record(double timestamp)
    {
        if (double.IsFinite(timestamp) && timestamp > lastJump) lastJump = timestamp;
    }

    public bool Observe(bool jumpDown, double timestamp)
    {
        if (jumpDown) Record(timestamp);
        return timestamp - lastJump is >= 0 and <= CooldownSeconds;
    }

    public void Reset() => lastJump = double.NegativeInfinity;
}
