import XCTest
@testable import HollowKnightVision

final class GroundPoseRefinementSearchTests: XCTestCase {
    func testFindsSubCellXYOffset() {
        let expected = GroundPoseRefinementSearch.Offset(dx: 4, dy: -5)
        XCTAssertEqual(GroundPoseRefinementSearch.offset { dx, dy in
            .init(tested: 20, inliers: 18, horizontalSpread: 320,
                  error: CGFloat(abs(dx - expected.dx) + abs(dy - expected.dy)) * 2)
        }, expected)
    }

    func testRepeatedTextureCannotAuthorizePoseChange() {
        XCTAssertNil(GroundPoseRefinementSearch.offset { _, _ in
            .init(tested: 20, inliers: 18, horizontalSpread: 320, error: 2)
        })
    }

    func testSmallOccludedConsensusIsRejected() {
        XCTAssertNil(GroundPoseRefinementSearch.offset { dx, dy in
            .init(tested: 20, inliers: 5, horizontalSpread: 320,
                  error: CGFloat(abs(dx - 3) + abs(dy + 2)))
        })
    }
}
