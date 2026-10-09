import XCTest
@testable import HollowKnightVision

final class TrackingStabilityTests: XCTestCase {
    func testFastDescentOntoNewPlatformUsesIndependentTextureSupport() {
        XCTAssertTrue(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: 3, y: -26), current: .zero, solveWidth: 640,
            globalMatchCount: 0, hasGlobalCorrection: false, localInlierCount: 0,
            localTextureSupport: 12, localTextureError: 7))
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: 3, y: -26), current: .zero, solveWidth: 640,
            globalMatchCount: 0, hasGlobalCorrection: false, localInlierCount: 0,
            localTextureSupport: 3, localTextureError: 7))
        XCTAssertFalse(GroundPoseContinuityGate.accepts(
            candidate: CGPoint(x: 3, y: -100), current: .zero, solveWidth: 640,
            globalMatchCount: 0, hasGlobalCorrection: false, localInlierCount: 0,
            localTextureSupport: 12, localTextureError: 7))
    }

    func testMaskUsesUnsmoothBodyAndCoversPredictedMotion() throws {
        func batch(x: CGFloat, t: Double, generation: UInt64 = 1) -> LiveObjectDetectionBatch {
            .init(detections: [.init(classIdentifier: "game.playable-knight",
                normalizedRect: CGRect(x: x, y: 0.5, width: 0.05, height: 0.1),
                confidence: 0.9, sourceFrameIdentifier: 1,
                modelVersion: .init(major: 1, minor: 0))], sourceFrameIdentifier: 1,
                sourceTimestamp: t, captureGeneration: generation, inferenceDuration: 0.03)
        }
        let prior = batch(x: 0.1, t: 1)
        let current = batch(x: 0.15, t: 1.1)
        let mask = try XCTUnwrap(MovingObjectMask.detections(displayed: prior.detections,
            current: current, previous: prior, generation: 1, timestamp: 1.2).first)
        XCTAssertLessThan(mask.normalizedRect.minX, 0.15)
        XCTAssertGreaterThan(mask.normalizedRect.maxX, 0.25)
        XCTAssertEqual(MovingObjectMask.detections(displayed: [], current: current,
            previous: prior, generation: 2, timestamp: 1.2), [])
    }

    func testLineIndexPreservesOffscreenEvidenceWithoutCountingMisses() {
        let ledger = GroundLinePresence(minimumObservationSeconds: 0.1)
        let line = CleanFloorLine(row: 40, xRange: 16...143)
        for i in 0..<10 {
            ledger.update(lines: [line], camera: .zero, width: 160, height: 100,
                timestamp: Double(i) * 0.02, poseVerified: true, exclusions: [])
        }
        let before = ledger.reviews(camera: .zero, width: 160, height: 100)
        for i in 10..<30 {
            ledger.update(lines: [], camera: CGPoint(x: 0, y: 1_000), width: 160,
                height: 100, timestamp: Double(i) * 0.02, poseVerified: true, exclusions: [])
        }
        let after = ledger.reviews(camera: .zero, width: 160, height: 100)
        XCTAssertEqual(before, after)
        XCTAssertEqual(after.first?.state, .confirmed)
    }
}
