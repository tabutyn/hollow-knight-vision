import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class FeatureCorrectionPolicyTests: XCTestCase {
    func testSlowCorrectionRebasesNewerQueuedWorkAndAppliesOnce() {
        let policy = FeatureCorrectionPolicy(generation: 7)
        let slow = FeatureRefinementRequest(generation: 7, timestamp: 10)
        let newer = FeatureRefinementRequest(generation: 7, timestamp: 13)
        let snapshot = try! XCTUnwrap(policy.captureSnapshot(for: slow))

        XCTAssertTrue(policy.apply(CGVector(dx: -8, dy: 3), for: slow))
        XCTAssertFalse(policy.apply(CGVector(dx: -8, dy: 3), for: slow))
        XCTAssertFalse(policy.apply(CGVector(dx: 1, dy: 1), for: FeatureRefinementRequest(
            generation: 7,
            timestamp: 9
        )))

        // The next tracker job was queued before the slow correction returned.
        // It therefore starts in the corrected world coordinates once it runs.
        XCTAssertEqual(
            policy.rebasedPosition(CGPoint(x: 40, y: 20), from: snapshot),
            CGPoint(x: 32, y: 23)
        )

        let afterSlowCorrection = try! XCTUnwrap(policy.captureSnapshot(for: newer))
        XCTAssertEqual(
            policy.rebasedPosition(CGPoint(x: 32, y: 23), from: afterSlowCorrection),
            CGPoint(x: 32, y: 23)
        )
    }

    func testGenerationResetRejectsSlowOldCorrection() {
        let policy = FeatureCorrectionPolicy(generation: 2)
        let old = FeatureRefinementRequest(generation: 2, timestamp: 4)
        policy.beginGeneration(3)

        XCTAssertFalse(policy.apply(CGVector(dx: 5, dy: 0), for: old))
        XCTAssertNil(policy.captureSnapshot(for: old))
        XCTAssertTrue(policy.apply(
            CGVector(dx: 2, dy: -1),
            for: FeatureRefinementRequest(generation: 3, timestamp: 1)
        ))
    }

    func testGenerationTransitionWaitsForAcceptedAccumulatorMutation() {
        let policy = FeatureCorrectionPolicy(generation: 7)
        let request = FeatureRefinementRequest(generation: 7, timestamp: 10)
        let enteredAcceptance = expectation(description: "accepted mutation entered")
        let completedAcceptance = expectation(description: "accepted mutation completed")
        let releaseAcceptance = DispatchSemaphore(value: 0)
        let transitionReturned = DispatchSemaphore(value: 0)

        DispatchQueue.global().async {
            XCTAssertTrue(policy.apply(CGVector(dx: 3, dy: 0), for: request, onAcceptance: {
                enteredAcceptance.fulfill()
                _ = releaseAcceptance.wait(timeout: .now() + 1)
            }))
            completedAcceptance.fulfill()
        }
        wait(for: [enteredAcceptance], timeout: 1)

        DispatchQueue.global().async {
            policy.beginGeneration(8)
            transitionReturned.signal()
        }
        // beginGeneration must wait; otherwise an old worker could mutate the
        // accumulator after its tracking state was replaced.
        XCTAssertEqual(transitionReturned.wait(timeout: .now() + 0.05), .timedOut)
        releaseAcceptance.signal()
        XCTAssertEqual(transitionReturned.wait(timeout: .now() + 1), .success)
        wait(for: [completedAcceptance], timeout: 1)
    }
}
