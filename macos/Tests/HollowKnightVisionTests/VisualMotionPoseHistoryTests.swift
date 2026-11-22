import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class VisualMotionPoseHistoryTests: XCTestCase {
    func testHistoryIsBoundedAndDelayedSolvesReplaceOnlyTheirExactCapture() {
        var history = VisualMotionPoseHistory()
        for time in 0..<100 {
            history.record(timestamp: Double(time), estimatedPose: CGPoint(x: time, y: 0), completed: nil)
        }
        XCTAssertNil(history.pose(at: 35))
        XCTAssertEqual(history.pose(at: 36), CGPoint(x: 36, y: 0))
        history.record(timestamp: 99, estimatedPose: CGPoint(x: 500, y: 0),
            completed: VisualCameraPoseSample(timestamp: 98, cameraPosition: CGPoint(x: 490, y: 2)))
        XCTAssertEqual(history.pose(at: 98), CGPoint(x: 490, y: 2))
        XCTAssertEqual(history.pose(at: 99), CGPoint(x: 500, y: 0))
        XCTAssertEqual(history.pose(at: 97), CGPoint(x: 97, y: 0))
        history.reset()
        XCTAssertNil(history.pose(at: 98))
    }
}
