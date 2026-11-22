import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveIslandAxisSolverTests: XCTestCase {
    func testFloorConstraintMovesElevationWithoutConnectingHorizontalSpacing() throws {
        let result = try LiveIslandAxisSolver.solve(islandIDs: [0, 1], anchored: [0], links: [],
            newLink: .init(from: 0, to: 1, horizontal: false),
            correction: CGVector(dx: 900, dy: -56))
        XCTAssertEqual(result.shifts, [1: CGVector(dx: 0, dy: -56)])
        XCTAssertTrue(result.horizontalComponent.isEmpty)
        XCTAssertEqual(result.verticalComponent, [0, 1])
    }

    func testLoopMovesWholeFloorConnectedGroupOnlyInElevation() throws {
        let result = try LiveIslandAxisSolver.solve(islandIDs: [0, 1, 2, 3, 4], anchored: [0],
            links: [.init(from: 1, to: 2, horizontal: false), .init(from: 2, to: 3)],
            newLink: .init(from: 0, to: 3), correction: CGVector(dx: 5, dy: 7))
        XCTAssertEqual(result.shifts, [1: CGVector(dx: 0, dy: 7),
            2: CGVector(dx: 5, dy: 7), 3: CGVector(dx: 5, dy: 7)])
        XCTAssertEqual(result.horizontalComponent, [0, 2, 3])
        XCTAssertEqual(result.verticalComponent, [0, 1, 2, 3])
        XCTAssertNil(result.shifts[4])
    }

    func testReverseLoopHasIdenticalPhysicalCorrection() throws {
        let result = try LiveIslandAxisSolver.solve(islandIDs: [0, 1, 2, 3], anchored: [0],
            links: [.init(from: 1, to: 2, horizontal: false), .init(from: 2, to: 3)],
            newLink: .init(from: 3, to: 0), correction: CGVector(dx: -5, dy: -7))
        XCTAssertEqual(result.shifts, [1: CGVector(dx: 0, dy: 7),
            2: CGVector(dx: 5, dy: 7), 3: CGVector(dx: 5, dy: 7)])
    }

    func testLoopCannotTearExistingFloorConnectionEvenWithFreeHorizontalAxis() {
        XCTAssertThrowsError(try LiveIslandAxisSolver.solve(islandIDs: [0, 1], anchored: [0],
            links: [.init(from: 0, to: 1, horizontal: false)],
            newLink: .init(from: 0, to: 1), correction: CGVector(dx: 50, dy: 9))) { error in
            XCTAssertEqual(error as? LiveIslandAxisSolver.Failure, .inconsistentKnownComponent)
        }
    }

    func testAlreadyConnectedVisualLoopUsesVectorTolerance() {
        XCTAssertThrowsError(try LiveIslandAxisSolver.solve(islandIDs: [0, 1], anchored: [],
            links: [.init(from: 0, to: 1)], newLink: .init(from: 0, to: 1),
            correction: CGVector(dx: 3, dy: 3)))
    }

    func testTwoAnchorsCannotBeReconciledByMovingFloorLinkedIsland() {
        XCTAssertThrowsError(try LiveIslandAxisSolver.solve(islandIDs: [0, 1, 2], anchored: [0, 1],
            links: [.init(from: 1, to: 2, horizontal: false)],
            newLink: .init(from: 0, to: 2), correction: CGVector(dx: 10, dy: 9))) { error in
            XCTAssertEqual(error as? LiveIslandAxisSolver.Failure, .conflictingAnchors)
        }
    }

    func testDisconnectedComponentsKeepOlderPlacementWithoutGainingAnchor() throws {
        let result = try LiveIslandAxisSolver.solve(islandIDs: [5, 6, 8], anchored: [],
            links: [.init(from: 5, to: 6, horizontal: false)],
            newLink: .init(from: 8, to: 6, horizontal: false), correction: CGVector(dx: 0, dy: 12))
        XCTAssertEqual(result.shifts, [8: CGVector(dx: 0, dy: -12)])
        XCTAssertTrue(result.horizontalComponent.isEmpty)
    }
}
