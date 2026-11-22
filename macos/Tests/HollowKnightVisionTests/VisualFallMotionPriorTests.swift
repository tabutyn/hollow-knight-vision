import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class VisualFallMotionPriorTests: XCTestCase {
    private let size = CGSize(width: 640, height: 360)
    private let speed = VisualFallMotionPrior.terminalUnitsPerSecond * VisualFallMotionPrior.pixelsPerUnitAt640
    private func box(_ y: CGFloat) -> CGRect { CGRect(x: 300, y: y - 12, width: 16, height: 24) }

    func testPlayerFallAndCameraClampAreSeparate() throws {
        var prior = VisualFallMotionPrior()
        for i in 0..<4 {
            let t = Double(i) * 0.025
            prior.observeMeasured(cameraY: 100, knight: box(250 - speed * t), timestamp: t,
                frameSize: size, solveWidth: 640)
        }
        let predicted = try XCTUnwrap(prior.predict(knight: box(250 - speed * 0.1),
            timestamp: 0.1, frameSize: size, solveWidth: 640))
        XCTAssertEqual(predicted.cameraY, 100, accuracy: 0.001)
        XCTAssertTrue(predicted.cameraYRange.contains(100))
    }

    func testFollowingCameraCanMoveFasterThanPlayerWithoutViolatingPlayerCap() throws {
        var prior = VisualFallMotionPrior()
        for i in 0..<4 {
            let t = Double(i) * 0.025
            let camera = 100 - speed * 1.4 * t
            let screen = 180 + speed * 0.4 * t
            prior.observeMeasured(cameraY: camera, knight: box(screen), timestamp: t,
                frameSize: size, solveWidth: 640)
        }
        let predicted = try XCTUnwrap(prior.predict(knight: box(180 + speed * 0.4 * 0.1),
            timestamp: 0.1, frameSize: size, solveWidth: 640))
        XCTAssertEqual(predicted.cameraY, 100 - speed * 1.4 * 0.1, accuracy: 0.001)
        XCTAssertTrue(predicted.cameraYRange.contains(predicted.cameraY))
        XCTAssertNil(prior.predict(knight: nil, timestamp: 0.1, frameSize: size, solveWidth: 640))
        XCTAssertNil(prior.predict(knight: box(180), timestamp: 0.2, frameSize: size, solveWidth: 640))
        XCTAssertNil(prior.predict(knight: box(180), timestamp: 0.1, frameSize: CGSize(width: 640, height: 480), solveWidth: 640))
        prior.observeMeasured(cameraY: 100, knight: box(180), timestamp: 0.11, frameSize: size, solveWidth: 640)
        XCTAssertNil(prior.predict(knight: box(180), timestamp: 0.13, frameSize: size, solveWidth: 640))
    }

    func testInsufficientTerminalEvidenceAndBlackoutCannotStartPrediction() {
        var prior = VisualFallMotionPrior()
        prior.observeMeasured(cameraY: 0, knight: box(180), timestamp: 0, frameSize: size, solveWidth: 640)
        prior.observeMeasured(cameraY: 0, knight: box(170), timestamp: 0.025, frameSize: size, solveWidth: 640)
        XCTAssertNil(prior.predict(knight: box(160), timestamp: 0.05, frameSize: size, solveWidth: 640))
        prior.observeMeasured(cameraY: 0, knight: nil, timestamp: 0.06, frameSize: size, solveWidth: 640)
        XCTAssertNil(prior.predict(knight: box(150), timestamp: 0.075, frameSize: size, solveWidth: 640))
    }
}
