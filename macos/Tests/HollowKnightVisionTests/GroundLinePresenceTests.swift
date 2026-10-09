import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundLinePresenceTests: XCTestCase {
    private let line = CleanFloorLine(row: 40, xRange: 16...143)

    private func update(_ presence: GroundLinePresence, _ time: Double,
                        seen: Bool, camera: CGPoint = .zero,
                        verified: Bool = true, exclusions: [CGRect] = []) {
        presence.update(lines: seen ? [line] : [], camera: camera,
            width: 160, height: 100, timestamp: time,
            poseVerified: verified, exclusions: exclusions)
    }

    private func review(_ presence: GroundLinePresence) throws -> GroundLinePresenceReview {
        try XCTUnwrap(presence.reviews(camera: .zero, width: 160, height: 100).first)
    }

    func testThreeFramesAreNotEnoughAndTwoSecondsConfirmStableLine() throws {
        let presence = GroundLinePresence()
        for frame in 0...2 { update(presence, Double(frame) / 60, seen: true) }
        XCTAssertEqual(try review(presence).state, .observing)
        for frame in 3...120 { update(presence, Double(frame) / 60, seen: true) }
        XCTAssertEqual(try review(presence).state, .confirmed)
        XCTAssertEqual(try review(presence).visibleSeconds, 2, accuracy: 0.001)
    }

    func testAboveHalfConfirmsBelowHalfRejectsAndExactlyHalfWaits() throws {
        for (hits, expected) in [(6, GroundLinePresenceState.confirmed),
                                  (4, .rejected), (5, .observing)] {
            let presence = GroundLinePresence()
            for frame in 0...120 {
                update(presence, Double(frame) / 60, seen: frame % 10 < hits)
            }
            let result = try review(presence)
            XCTAssertEqual(result.detectedFraction, Double(hits) / 10, accuracy: 0.001)
            XCTAssertEqual(result.state, expected)
        }
    }

    func testRejectedLineRecoversAfterEnoughVerifiedDetections() throws {
        let presence = GroundLinePresence()
        for frame in 0...120 { update(presence, Double(frame) / 60, seen: frame % 10 < 4) }
        for frame in 121...600 { update(presence, Double(frame) / 60, seen: true) }
        XCTAssertGreaterThan(try review(presence).detectedFraction, 0.5)
        XCTAssertEqual(try review(presence).state, .confirmed)
    }

    func testOcclusionOffscreenUnverifiedAndLongGapDoNotCountAsMisses() throws {
        let presence = GroundLinePresence()
        for frame in 0...60 { update(presence, Double(frame) / 60, seen: true) }
        let before = try review(presence)
        for frame in 61...120 {
            update(presence, Double(frame) / 60, seen: false,
                   exclusions: [CGRect(x: 0, y: 30, width: 160, height: 40)])
        }
        for frame in 121...180 {
            update(presence, Double(frame) / 60, seen: false, camera: CGPoint(x: 500, y: 0))
        }
        for frame in 181...240 { update(presence, Double(frame) / 60, seen: false, verified: false) }
        update(presence, 20, seen: true)
        let after = try review(presence)
        XCTAssertEqual(after.visibleSeconds, before.visibleSeconds)
        XCTAssertEqual(after.detectedFraction, 1)
        XCTAssertEqual(after.state, .observing)
    }

    func testVotesMeasureTimeNotFrameCount() throws {
        let presence = GroundLinePresence()
        // Six sparse hit observations cover one second; 100 missing
        // observations cover only half a second. Frame voting would reject.
        for frame in 0...5 { update(presence, Double(frame) * 0.2, seen: true) }
        for frame in 1...100 { update(presence, 1 + Double(frame) * 0.005, seen: false) }
        for frame in 1...5 { update(presence, 1.5 + Double(frame) * 0.1, seen: true) }
        XCTAssertGreaterThan(try review(presence).detectedFraction, 0.7)
        XCTAssertEqual(try review(presence).state, .confirmed)
    }

    func testConfirmedLineHidesOverlappingStaleRejectedProjection() {
        let green = GroundLinePresenceReview(
            id: 1, line: CleanFloorLine(row: 40, xRange: 32...95),
            state: .confirmed, visibleSeconds: 4, detectedFraction: 1
        )
        let staleYellow = GroundLinePresenceReview(
            id: 2, line: CleanFloorLine(row: 45, xRange: 64...143),
            state: .rejected, visibleSeconds: 4, detectedFraction: 0.1
        )
        let independentYellow = GroundLinePresenceReview(
            id: 3, line: CleanFloorLine(row: 70, xRange: 64...143),
            state: .rejected, visibleSeconds: 4, detectedFraction: 0.1
        )

        let display = GroundLineDetector.displayPresenceReviews(
            [green, staleYellow, independentYellow]
        )

        XCTAssertEqual(display.map(\.id), [3, 1])
    }

    func testFeatureHypothesesRenderOnlyConfirmedLines() {
        let reviews = [
            GroundLinePresenceReview(
                id: 1, line: CleanFloorLine(row: 40, xRange: 0...63),
                state: .confirmed, visibleSeconds: 4, detectedFraction: 0.9
            ),
            GroundLinePresenceReview(
                id: 2, line: CleanFloorLine(row: 60, xRange: 0...63),
                state: .observing, visibleSeconds: 1, detectedFraction: 0.6
            ),
            GroundLinePresenceReview(
                id: 3, line: CleanFloorLine(row: 80, xRange: 0...63),
                state: .rejected, visibleSeconds: 4, detectedFraction: 0.1
            ),
        ]

        XCTAssertEqual(
            GroundLineDetector.featureHypothesisPresenceReviews(reviews).map(\.id),
            [1]
        )
    }
}
