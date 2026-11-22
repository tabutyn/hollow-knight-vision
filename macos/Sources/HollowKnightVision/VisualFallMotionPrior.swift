import CoreGraphics
import Foundation

/// Short player-physics prediction, never a measured camera pose. Calibration is
/// frozen from a DIFFERENT 30 Oct recording: terminal player velocity 20.948 game
/// units/s and 22.22423 reference pixels/unit at 640x360. No telemetry enters this
/// type. Camera movement is inferred separately from the current visible Knight;
/// screen movement naturally accommodates camera lag and room-boundary clamps.
struct VisualFallMotionPrior {
    static let terminalUnitsPerSecond: CGFloat = 20.948
    static let pixelsPerUnitAt640: CGFloat = 22.22423
    static let maximumPredictionAge = 0.08
    struct Prediction {
        let cameraY: CGFloat
        let cameraYRange: ClosedRange<CGFloat>
        let age: TimeInterval
    }
    private struct Observation {
        let timestamp: TimeInterval
        let playerWorldY: CGFloat
        let screenY: CGFloat
        let height: CGFloat
        let solveWidth: CGFloat
    }
    private var previous: Observation?
    private var velocities = [CGFloat]()
    mutating func reset() { previous = nil; velocities = [] }

    /// Only observed ground or vertically calibrated scenery may seed physics.
    /// Estimated global origin is harmless here: only local displacement matters.
    mutating func observeMeasured(cameraY: CGFloat, knight: CGRect?, timestamp: TimeInterval,
                                  frameSize: CGSize, solveWidth: CGFloat) {
        guard cameraY.isFinite, timestamp.isFinite,
              let screen = Self.screen(knight, frameSize: frameSize, solveWidth: solveWidth) else {
            reset(); return
        }
        let current = Observation(timestamp: timestamp, playerWorldY: cameraY + screen.y,
            screenY: screen.y, height: screen.height, solveWidth: solveWidth)
        defer { previous = current }
        guard let prior = previous, solveWidth == prior.solveWidth,
              timestamp > prior.timestamp, timestamp - prior.timestamp <= 0.12,
              abs(screen.height / prior.height - 1) <= 0.15 else { velocities = []; return }
        let velocity = (current.playerWorldY - prior.playerWorldY) / CGFloat(timestamp - prior.timestamp)
        let terminal = Self.terminalUnitsPerSecond * Self.pixelsPerUnitAt640 * solveWidth / 640
        guard velocity < -terminal * 0.95, velocity > -terminal * 1.05 else { velocities = []; return }
        velocities.append(velocity)
        if velocities.count > 3 { velocities.removeFirst() }
    }

    /// A fresh player box is required. Darkness, unknown projection, an old seed,
    /// jump/landing evidence, and changing silhouette scale cannot authorize this.
    /// Predictions never seed another prediction or create trusted atlas geometry.
    func predict(knight: CGRect?, timestamp: TimeInterval, frameSize: CGSize,
                 solveWidth: CGFloat) -> Prediction? {
        guard let prior = previous, velocities.count == 3, solveWidth == prior.solveWidth,
              let screen = Self.screen(knight, frameSize: frameSize, solveWidth: solveWidth),
              abs(screen.height / prior.height - 1) <= 0.15 else { return nil }
        let age = timestamp - prior.timestamp
        guard age > 0, age <= Self.maximumPredictionAge else { return nil }
        let terminal = Self.terminalUnitsPerSecond * Self.pixelsPerUnitAt640 * solveWidth / 640
        let velocity = max(-terminal * 1.05, min(-terminal * 0.95, velocities.reduce(0, +) / 3))
        let tolerance = 2 * solveWidth / 640
        let lower = prior.playerWorldY - terminal * 1.05 * CGFloat(age) - screen.y - tolerance
        let upper = prior.playerWorldY - terminal * 0.95 * CGFloat(age) - screen.y + tolerance
        return Prediction(cameraY: prior.playerWorldY + velocity * CGFloat(age) - screen.y,
                          cameraYRange: lower...upper, age: age)
    }

    private static func screen(_ knight: CGRect?, frameSize: CGSize,
                               solveWidth: CGFloat) -> (y: CGFloat, height: CGFloat)? {
        guard frameSize.width.isFinite, frameSize.height.isFinite, solveWidth.isFinite,
              frameSize.width > 0, frameSize.height > 0, solveWidth > 0,
              abs(frameSize.width / frameSize.height - 16 / 9) <= 0.02,
              let knight, !knight.isNull, !knight.isEmpty,
              [knight.minX, knight.minY, knight.width, knight.height].allSatisfy(\.isFinite),
              CGRect(origin: .zero, size: frameSize).contains(knight) else { return nil }
        let scale = solveWidth / frameSize.width
        return (knight.midY * scale, knight.height * scale)
    }
}
