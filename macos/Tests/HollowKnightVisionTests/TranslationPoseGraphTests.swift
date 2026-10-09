import CoreGraphics
import XCTest

@testable import HollowKnightVision

final class TranslationPoseGraphTests: XCTestCase {
    func testStrongLoopClosureSmoothlyDistributesOutwardReturnDriftInBothCoordinates() throws {
        let nodes = [
            node(0, raw: CGPoint(x: 100, y: 200), initial: .zero),
            node(1, raw: CGPoint(x: 110, y: 205), initial: CGPoint(x: 10, y: 5)),
            node(2, raw: CGPoint(x: 120, y: 210), initial: CGPoint(x: 20, y: 10)),
            node(3, raw: CGPoint(x: 130, y: 215), initial: CGPoint(x: 30, y: 15)),
        ]
        let graph = try TranslationPoseGraph(
            nodes: nodes,
            constraints: [
                edge(0, 0, 1, CGPoint(x: 10, y: 5), 1, .motion),
                edge(1, 1, 2, CGPoint(x: 10, y: 5), 1, .motion),
                edge(2, 2, 3, CGPoint(x: 10, y: 5), 1, .motion),
                edge(3, 3, 0, CGPoint(x: -24, y: -12), 80, .loopClosure),
            ],
            anchorID: 0,
            baseRevision: 41
        )
        let solution = try graph.solve()

        assertPoint(solution.positions[0], CGPoint.zero)
        assertPoint(solution.positions[1], CGPoint(x: 8, y: 4), tolerance: 0.03)
        assertPoint(solution.positions[2], CGPoint(x: 16, y: 8), tolerance: 0.03)
        assertPoint(solution.positions[3], CGPoint(x: 24, y: 12), tolerance: 0.03)
        XCTAssertEqual(solution.baseRevision, 41)
        XCTAssertEqual(solution.revision, 42)
        XCTAssertEqual(nodes[2].raw, CGPoint(x: 120, y: 210))
    }

    func testAnchorRemainsExactAndConstraintOrderDoesNotChangeSolution() throws {
        let nodes = [
            node(7, raw: .zero, initial: CGPoint(x: 3.25, y: -7.5)),
            node(3, raw: .zero, initial: .zero),
            node(11, raw: .zero, initial: .zero),
        ]
        let edges = [
            edge(20, 7, 3, CGPoint(x: 4, y: 1), 2, .motion),
            edge(10, 3, 11, CGPoint(x: -2, y: 8), 3, .motion),
            edge(30, 11, 7, CGPoint(x: -2, y: -9), 2, .loopClosure),
        ]
        let first = try TranslationPoseGraph(nodes: nodes, constraints: edges, anchorID: 7).solve()
        let second = try TranslationPoseGraph(nodes: Array(nodes.reversed()), constraints: Array(edges.reversed()), anchorID: 7).solve()

        assertPoint(first.positions[7], CGPoint(x: 3.25, y: -7.5))
        XCTAssertEqual(first.positions.keys.sorted(), second.positions.keys.sorted())
        for id in first.positions.keys {
            assertPoint(first.positions[id], try XCTUnwrap(second.positions[id]), tolerance: 0.000_000_1)
        }
    }

    func testConsistentMotionChainNeedsNoLoopClosure() throws {
        let graph = try TranslationPoseGraph(
            nodes: [
                node(0, raw: CGPoint(x: 50, y: 90), initial: CGPoint(x: 5, y: -3)),
                node(1, raw: CGPoint(x: 54, y: 92), initial: .zero),
                node(2, raw: CGPoint(x: 59, y: 88), initial: .zero),
            ],
            constraints: [
                edge(1, 0, 1, CGPoint(x: 4, y: 2), 1, .motion),
                edge(2, 1, 2, CGPoint(x: 5, y: -4), 1, .motion),
            ],
            anchorID: 0
        )
        let solution = try graph.solve()
        assertPoint(solution.positions[0], CGPoint(x: 5, y: -3))
        assertPoint(solution.positions[1], CGPoint(x: 9, y: -1))
        assertPoint(solution.positions[2], CGPoint(x: 14, y: -5))
    }

    func testInvalidDanglingDisconnectedAndNonFiniteGraphsAreRejected() throws {
        let validNodes = [node(0, raw: .zero, initial: .zero), node(1, raw: .zero, initial: .zero)]
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: [validNodes[0], validNodes[0]], constraints: [], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .duplicateNodeID(0))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [
            edge(1, 0, 1, .zero, 1, .motion), edge(1, 1, 0, .zero, 1, .loopClosure),
        ], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .duplicateConstraintID(1))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [
            edge(2, 0, 9, .zero, 1, .motion),
        ], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .danglingConstraint(2))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [
            edge(3, 0, 1, CGPoint(x: CGFloat.infinity, y: 0), 1, .motion),
        ], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .nonFiniteConstraintDelta(3))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: [
            node(0, raw: .zero, initial: .zero),
            node(1, raw: CGPoint(x: CGFloat.nan, y: 0), initial: .zero),
        ], constraints: [edge(31, 0, 1, .zero, 1, .motion)], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .nonFiniteNodePosition(1))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [
            edge(4, 0, 1, .zero, 0, .motion),
        ], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .invalidWeight(4))
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [], anchorID: 0)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .disconnectedGraph)
        }
        XCTAssertThrowsError(try TranslationPoseGraph(nodes: validNodes, constraints: [
            edge(5, 0, 1, .zero, 1, .motion),
        ], anchorID: 8)) {
            XCTAssertEqual($0 as? TranslationPoseGraphError, .missingAnchor(8))
        }
    }

    private func node(_ id: Int, raw: CGPoint, initial: CGPoint) -> TranslationPoseNode {
        TranslationPoseNode(id: id, raw: raw, initial: initial)
    }

    private func edge(
        _ id: Int,
        _ from: Int,
        _ to: Int,
        _ delta: CGPoint,
        _ weight: Double,
        _ kind: TranslationPoseConstraintKind
    ) -> TranslationPoseConstraint {
        TranslationPoseConstraint(id: id, from: from, to: to, measuredDelta: delta, weight: weight, kind: kind)
    }

    private func assertPoint(
        _ actual: CGPoint?,
        _ expected: CGPoint,
        tolerance: CGFloat = 0.000_001,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        guard let actual else {
            XCTFail("Expected a position.", file: file, line: line)
            return
        }
        XCTAssertEqual(actual.x, expected.x, accuracy: tolerance, file: file, line: line)
        XCTAssertEqual(actual.y, expected.y, accuracy: tolerance, file: file, line: line)
    }
}
