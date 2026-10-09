import XCTest
@testable import HollowKnightVision

final class FeatureRefinementCoordinatorTests: XCTestCase {
    func testSlowRefinementDoesNotBlockRegistrationPublication() {
        let owner = DispatchQueue(label: "feature-refinement-owner")
        let coordinator = FeatureRefinementCoordinator<Int>(ownerQueue: owner)
        let started = expectation(description: "slow refinement started")
        let completed = expectation(description: "slow refinement completed")
        let request = FeatureRefinementRequest(generation: 1, timestamp: 1)
        let returnedImmediately = expectation(description: "registration returned")

        owner.async {
            coordinator.didPublishRegistration(request)
            coordinator.submit(request, work: {
                started.fulfill()
                Thread.sleep(forTimeInterval: 0.15)
                return 7
            }, receive: { _ in completed.fulfill() })
            // This executes on the registration owner while refinement sleeps
            // on its independent worker.
            returnedImmediately.fulfill()
        }

        wait(for: [started, returnedImmediately], timeout: 0.08)
        wait(for: [completed], timeout: 1)
    }

    func testSlowCorrectionReturnsInCurrentGenerationThenRunsNewestWork() {
        let owner = DispatchQueue(label: "feature-refinement-owner")
        let coordinator = FeatureRefinementCoordinator<Int>(ownerQueue: owner)
        let started = expectation(description: "slow refinement started")
        let oldDelivered = expectation(description: "old correction delivered")
        let newestDelivered = expectation(description: "newest correction delivered")
        let old = FeatureRefinementRequest(generation: 4, timestamp: 10)
        let newer = FeatureRefinementRequest(generation: 4, timestamp: 11)
        let releaseOldWork = DispatchSemaphore(value: 0)

        owner.sync {
            coordinator.didPublishRegistration(old)
            coordinator.submit(old, work: {
                started.fulfill()
                _ = releaseOldWork.wait(timeout: .now() + 1)
                return 1
            }, receive: { _ in oldDelivered.fulfill() })
        }
        wait(for: [started], timeout: 1)
        owner.sync {
            coordinator.didPublishRegistration(newer)
            coordinator.submit(newer, work: { 2 }, receive: { _ in
                newestDelivered.fulfill()
            })
        }
        releaseOldWork.signal()
        wait(for: [oldDelivered, newestDelivered], timeout: 1)
    }

    func testOutOfOrderCurrentGenerationResultIsRejected() {
        let owner = DispatchQueue(label: "feature-refinement-owner")
        let coordinator = FeatureRefinementCoordinator<Int>(ownerQueue: owner)
        let newerDelivered = expectation(description: "newer result delivered")
        let oldDelivered = expectation(description: "out of order result rejected")
        oldDelivered.isInverted = true
        let newer = FeatureRefinementRequest(generation: 4, timestamp: 11)
        let old = FeatureRefinementRequest(generation: 4, timestamp: 10)

        owner.sync {
            coordinator.didPublishRegistration(newer)
            coordinator.submit(newer, work: { 2 }, receive: { _ in newerDelivered.fulfill() })
        }
        wait(for: [newerDelivered], timeout: 1)
        owner.sync {
            coordinator.submit(old, work: { 1 }, receive: { _ in oldDelivered.fulfill() })
        }
        wait(for: [oldDelivered], timeout: 0.15)
    }

    func testGenerationInvalidationRejectsCompletedCorrection() {
        let owner = DispatchQueue(label: "feature-refinement-owner")
        let coordinator = FeatureRefinementCoordinator<Int>(ownerQueue: owner)
        let started = expectation(description: "refinement started")
        let rejected = expectation(description: "invalidated correction rejected")
        rejected.isInverted = true
        let request = FeatureRefinementRequest(generation: 3, timestamp: 1)
        let releaseWork = DispatchSemaphore(value: 0)

        owner.sync {
            coordinator.didPublishRegistration(request)
            coordinator.submit(request, work: {
                started.fulfill()
                _ = releaseWork.wait(timeout: .now() + 1)
                return 1
            }, receive: { _ in rejected.fulfill() })
        }
        wait(for: [started], timeout: 1)
        owner.sync { coordinator.invalidate(before: 4) }
        releaseWork.signal()
        wait(for: [rejected], timeout: 0.2)
    }
}
