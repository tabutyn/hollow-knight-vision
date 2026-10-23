using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class LiveSessionPolicyTests
{
    public static void TestCaptureRestartRequiresDesiredIdlePipeline()
    {
        Check(LiveCaptureRestartPolicy.ShouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: false,
            isCapturing: false));
    }

    public static void TestCaptureRestartDoesNotReviveIntentionalStopOrRaceNewCapture()
    {
        Check(!LiveCaptureRestartPolicy.ShouldRestart(
            wantsCapture: false,
            hasStream: false,
            operationInFlight: false,
            isCapturing: false));
        Check(!LiveCaptureRestartPolicy.ShouldRestart(
            wantsCapture: true,
            hasStream: true,
            operationInFlight: false,
            isCapturing: false));
        Check(!LiveCaptureRestartPolicy.ShouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: true,
            isCapturing: false));
        Check(!LiveCaptureRestartPolicy.ShouldRestart(
            wantsCapture: true,
            hasStream: false,
            operationInFlight: false,
            isCapturing: true));
    }

    public static void TestGameLossCountdownUsesRequestedMessage()
    {
        Equal(5, GameLossCountdown.InitialSeconds);
        Equal(
            "Hollow Knight lost. Closing app in 5 seconds",
            GameLossCountdown.Message(5));
        Equal(
            "Hollow Knight lost. Closing app in 0 seconds",
            GameLossCountdown.Message(0));
    }

    private static void Check(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Expected condition to be true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"Expected {expected}; received {actual}.");
        }
    }
}
