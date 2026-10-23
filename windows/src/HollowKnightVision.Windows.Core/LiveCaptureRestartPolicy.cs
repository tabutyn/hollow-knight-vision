namespace HollowKnightVision.Windows.Core;

/// <summary>
/// Restarts only when capture is still desired and no capture attempt owns the
/// pipeline. Delayed retry callbacks use this guard to avoid racing a newer
/// stream or reviving an intentionally stopped capture.
/// </summary>
public static class LiveCaptureRestartPolicy
{
    public static bool ShouldRestart(
        bool wantsCapture,
        bool hasStream,
        bool operationInFlight,
        bool isCapturing) =>
        wantsCapture && !hasStream && !operationInFlight && !isCapturing;
}
