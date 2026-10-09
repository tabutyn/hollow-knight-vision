using System.Threading;

namespace HollowKnightVision.Windows.Core;

public readonly record struct LatestFrameStatistics(
    long Published,
    long Taken,
    long ReplacedBeforeTake);

public sealed class LatestFrameSlot
{
    private BgraFrame? latest;
    private long published;
    private long taken;
    private long replacedBeforeTake;

    public void Publish(BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var replaced = Interlocked.Exchange(ref latest, frame);
        Interlocked.Increment(ref published);
        if (replaced is not null) Interlocked.Increment(ref replacedBeforeTake);
    }

    public bool TryTake(out BgraFrame? frame)
    {
        frame = Interlocked.Exchange(ref latest, null);
        if (frame is null) return false;
        Interlocked.Increment(ref taken);
        return true;
    }

    public LatestFrameStatistics Statistics => new(
        Interlocked.Read(ref published),
        Interlocked.Read(ref taken),
        Interlocked.Read(ref replacedBeforeTake));
}
