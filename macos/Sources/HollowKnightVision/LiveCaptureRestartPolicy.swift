import Foundation

/// Restarts only when capture is still desired and no capture attempt owns the
/// pipeline. Delayed retry callbacks use this guard to avoid racing a newer
/// stream or reviving an intentionally stopped capture.
enum LiveCaptureRestartPolicy {
    static func shouldRestart(
        wantsCapture: Bool,
        hasStream: Bool,
        operationInFlight: Bool,
        isCapturing: Bool
    ) -> Bool {
        wantsCapture && !hasStream && !operationInFlight && !isCapturing
    }
}
