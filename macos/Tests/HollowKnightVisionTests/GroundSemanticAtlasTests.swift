import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GroundSemanticAtlasTests: XCTestCase {
    func testSemanticEvidenceApprovesOnlyMatchingStableLineAndPersists() {
        let atlas = GroundSemanticAtlas()
        let ground = GroundHypothesisAtlasLine(
            segmentID: 1,
            atlasStart: CGPoint(x: 20, y: 65),
            atlasEnd: CGPoint(x: 140, y: 65)
        )
        let decoration = GroundHypothesisAtlasLine(
            segmentID: 2,
            atlasStart: CGPoint(x: 20, y: 25),
            atlasEnd: CGPoint(x: 140, y: 25)
        )

        var approved = [GroundHypothesisAtlasLine]()
        for _ in 0..<6 {
            approved = atlas.update(
                trackingLines: [ground, decoration],
                semanticLines: [CleanFloorLine(row: 40, xRange: 16...143)],
                camera: CGPoint(x: 10, y: 5),
                frameWidth: 160,
                atlasHeight: 100,
                poseVerified: true
            )
        }
        XCTAssertEqual(approved.map(\.segmentID), [1])

        let persisted = atlas.update(
            trackingLines: [ground, decoration],
            semanticLines: [],
            camera: CGPoint(x: 500, y: 0),
            frameWidth: 160,
            atlasHeight: 100,
            poseVerified: true
        )
        XCTAssertEqual(persisted.map(\.segmentID), [1])
    }

    func testReassociatedIDInheritsApprovalFromSameGeometry() {
        let atlas = GroundSemanticAtlas()
        let original = GroundHypothesisAtlasLine(
            segmentID: 1,
            atlasStart: CGPoint(x: 20, y: 65),
            atlasEnd: CGPoint(x: 140, y: 65)
        )
        for _ in 0..<6 {
            _ = atlas.update(
                trackingLines: [original],
                semanticLines: [CleanFloorLine(row: 40, xRange: 16...143)],
                camera: CGPoint(x: 10, y: 5),
                frameWidth: 160,
                atlasHeight: 100,
                poseVerified: true
            )
        }
        let replacement = GroundHypothesisAtlasLine(
            segmentID: 9,
            atlasStart: CGPoint(x: 24, y: 66),
            atlasEnd: CGPoint(x: 144, y: 66)
        )
        XCTAssertEqual(atlas.update(
            trackingLines: [replacement],
            semanticLines: [],
            camera: nil,
            frameWidth: 160,
            atlasHeight: 100,
            poseVerified: false
        ).map(\.segmentID), [9])
    }
}
