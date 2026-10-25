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

enum GameLossCountdown {
    static let initialSeconds = 5

    static func message(remainingSeconds: Int) -> String {
        "Hollow Knight lost. Closing app in \(max(0, remainingSeconds)) seconds"
    }
}

/// One game session can span multiple OS processes during Steam startup.
/// Only treat an early exit as final after a bounded replacement window. A
/// confirmed Quit or an exit after stable capture keeps normal close behavior.
struct GameProcessLifetime {
    enum Observation: Equatable {
        case waitingForGame
        case watching
        case waitingForRelaunch
        case exited
    }

    static let startupSettlingSeconds: TimeInterval = 15
    static let relaunchGraceSeconds: TimeInterval = 60

    private(set) var processID: Int32?
    private(set) var observation: Observation = .waitingForGame
    private var capturedSince: TimeInterval?
    private var replacementDeadline: TimeInterval?
    private var hasStableCapture = false

    mutating func observe(
        processID: Int32?,
        captureReady: Bool,
        quitConfirmed: Bool,
        at now: TimeInterval
    ) -> Observation {
        guard observation != .exited else { return .exited }
        if let processID {
            if self.processID != processID {
                self.processID = processID
                capturedSince = nil
                hasStableCapture = false
            }
            if captureReady {
                if capturedSince == nil { capturedSince = now }
                if now - (capturedSince ?? now) >= Self.startupSettlingSeconds {
                    hasStableCapture = true
                    replacementDeadline = nil
                }
            } else if !hasStableCapture {
                capturedSince = nil
            }
            observation = .watching
        } else if self.processID != nil || replacementDeadline != nil {
            self.processID = nil
            capturedSince = nil
            if quitConfirmed || hasStableCapture {
                observation = .exited
            } else {
                // Keep the first deadline through repeated short-lived PIDs;
                // a broken startup cannot postpone shutdown forever.
                if replacementDeadline == nil {
                    replacementDeadline = now + Self.relaunchGraceSeconds
                }
                observation = now < (replacementDeadline ?? now)
                    ? .waitingForRelaunch : .exited
            }
        }
        return observation
    }
}
