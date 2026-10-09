import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundFeatureEligibilityTests: XCTestCase {
    func testOnlyDescriptorSquaresFullyBelowSupportedSegmentsAreEligible() {
        let ground = GroundReferenceEstimate(
            worldY: 100,
            minimumWorldX: 40,
            maximumWorldX: 260,
            segments: [
                GroundReferenceSegment(minimumWorldX: 40, maximumWorldX: 120),
                GroundReferenceSegment(minimumWorldX: 180, maximumWorldX: 260),
            ],
            notchWorldXs: [40, 80, 120, 180, 220, 260],
            supportingObservationCount: 6,
            confidence: 0.8
        )
        let allowed = GroundFeatureEligibility.allowedRects(
            frameSize: CGSize(width: 320, height: 180),
            cameraPosition: .zero,
            solveWidth: 320,
            ground: ground
        )
        XCTAssertEqual(allowed, [
            CGRect(x: 52, y: 80, width: 56, height: 8),
            CGRect(x: 192, y: 80, width: 56, height: 8),
        ])

        let exclusions = GroundFeatureEligibility.alignmentExclusions(
            frameSize: CGSize(width: 320, height: 180),
            cameraPosition: .zero,
            solveWidth: 320,
            ground: ground,
            existing: []
        )
        XCTAssertFalse(exclusions.contains { $0.contains(CGPoint(x: 80, y: 84)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 150, y: 84)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 80, y: 50)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 80, y: 100)) })
    }

    func testMissingGroundExcludesWholeFrame() {
        XCTAssertEqual(
            GroundFeatureEligibility.alignmentExclusions(
                frameSize: CGSize(width: 320, height: 180),
                cameraPosition: .zero,
                solveWidth: 320,
                ground: nil,
                existing: []
            ),
            [CGRect(x: 0, y: 0, width: 320, height: 180)]
        )
    }

    func testSeparatePlatformHeightsCreateSeparateEligibleBands() {
        let ground = GroundReferenceEstimate(
            worldY: 100,
            minimumWorldX: 40,
            maximumWorldX: 260,
            segments: [
                GroundReferenceSegment(
                    minimumWorldX: 40,
                    maximumWorldX: 120,
                    worldY: 100
                ),
                GroundReferenceSegment(
                    minimumWorldX: 180,
                    maximumWorldX: 260,
                    worldY: 140
                ),
            ],
            notchWorldXs: [40, 120, 180, 260],
            supportingObservationCount: 4,
            confidence: 0.8
        )
        let exclusions = GroundFeatureEligibility.alignmentExclusions(
            frameSize: CGSize(width: 320, height: 180),
            cameraPosition: .zero,
            solveWidth: 320,
            ground: ground,
            existing: []
        )
        XCTAssertFalse(exclusions.contains { $0.contains(CGPoint(x: 80, y: 84)) })
        XCTAssertFalse(exclusions.contains { $0.contains(CGPoint(x: 220, y: 124)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 80, y: 124)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 220, y: 84)) })
        XCTAssertTrue(exclusions.contains { $0.contains(CGPoint(x: 150, y: 104)) })
    }
}
