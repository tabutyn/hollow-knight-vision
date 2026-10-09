import CoreGraphics
import Foundation

/// A pose retained by the translation-only graph. `raw` is deliberately kept
/// separate from solve-space positions so callers can retain original capture
/// coordinates without the optimizer rewriting them.
struct TranslationPoseNode: Equatable {
    let id: Int
    let raw: CGPoint
    let initial: CGPoint

    init(id: Int, raw: CGPoint, initial: CGPoint) {
        self.id = id
        self.raw = raw
        self.initial = initial
    }

    init(id: Int, rawPosition: CGPoint, initialPosition: CGPoint) {
        self.init(id: id, raw: rawPosition, initial: initialPosition)
    }

    var rawPosition: CGPoint { raw }
    var initialPosition: CGPoint { initial }
}

enum TranslationPoseConstraintKind: Equatable {
    case motion
    case loopClosure
}

/// A measured translation from `from` to `to`, expressed in solve units.
struct TranslationPoseConstraint: Equatable {
    let id: Int
    let from: Int
    let to: Int
    let measuredDelta: CGPoint
    let weight: Double
    let kind: TranslationPoseConstraintKind

    init(
        id: Int,
        from: Int,
        to: Int,
        measuredDelta: CGPoint,
        weight: Double,
        kind: TranslationPoseConstraintKind
    ) {
        self.id = id
        self.from = from
        self.to = to
        self.measuredDelta = measuredDelta
        self.weight = weight
        self.kind = kind
    }

    init(
        id: Int,
        fromID: Int,
        toID: Int,
        measuredDelta: CGPoint,
        weight: Double,
        kind: TranslationPoseConstraintKind
    ) {
        self.init(id: id, from: fromID, to: toID, measuredDelta: measuredDelta, weight: weight, kind: kind)
    }

    var fromID: Int { from }
    var toID: Int { to }
}

struct TranslationPoseSolution: Equatable {
    let revision: Int
    let baseRevision: Int
    let positions: [Int: CGPoint]
}

enum TranslationPoseGraphError: Error, Equatable {
    case duplicateNodeID(Int)
    case duplicateConstraintID(Int)
    case nonFiniteNodePosition(Int)
    case nonFiniteConstraintDelta(Int)
    case invalidWeight(Int)
    case missingAnchor(Int)
    case danglingConstraint(Int)
    case invalidConstraint(Int)
    case disconnectedGraph
    case invalidBaseRevision
    case numericalFailure
}

/// Deterministic weighted least-squares optimizer for translation-only poses.
/// Each coordinate is solved independently with the anchor removed from the
/// unknowns, which keeps the selected anchor exact rather than approximately
/// constrained by a large artificial weight.
struct TranslationPoseGraph {
    typealias Node = TranslationPoseNode
    typealias Constraint = TranslationPoseConstraint
    typealias ConstraintKind = TranslationPoseConstraintKind
    typealias Solution = TranslationPoseSolution
    typealias SolveError = TranslationPoseGraphError

    let nodes: [Node]
    let constraints: [Constraint]
    let anchorID: Int
    let baseRevision: Int

    init(
        nodes: [Node],
        constraints: [Constraint],
        anchorID: Int,
        baseRevision: Int = 0
    ) throws {
        self.nodes = nodes
        self.constraints = constraints
        self.anchorID = anchorID
        self.baseRevision = baseRevision
        try Self.validate(nodes: nodes, constraints: constraints, anchorID: anchorID, baseRevision: baseRevision)
    }

    static func solve(
        nodes: [Node],
        constraints: [Constraint],
        anchorID: Int,
        baseRevision: Int = 0
    ) throws -> Solution {
        try TranslationPoseGraph(
            nodes: nodes, constraints: constraints, anchorID: anchorID, baseRevision: baseRevision
        ).solve()
    }

    func solve() throws -> Solution {
        let orderedNodes = nodes.sorted { $0.id < $1.id }
        let orderedConstraints = constraints.sorted { $0.id < $1.id }
        guard let anchor = orderedNodes.first(where: { $0.id == anchorID }) else {
            throw SolveError.missingAnchor(anchorID)
        }

        let unknownIDs = orderedNodes.map(\.id).filter { $0 != anchorID }
        let unknownIndex = Dictionary(uniqueKeysWithValues: unknownIDs.enumerated().map { ($0.element, $0.offset) })
        let maximumWeight = orderedConstraints.map(\.weight).max() ?? 1
        let x = try solveCoordinate(
            unknownIndex: unknownIndex,
            constraints: orderedConstraints,
            anchorPosition: anchor.initial.x,
            coordinate: \.x,
            maximumWeight: maximumWeight
        )
        let y = try solveCoordinate(
            unknownIndex: unknownIndex,
            constraints: orderedConstraints,
            anchorPosition: anchor.initial.y,
            coordinate: \.y,
            maximumWeight: maximumWeight
        )

        var positions = [Int: CGPoint]()
        positions.reserveCapacity(orderedNodes.count)
        positions[anchorID] = anchor.initial
        for id in unknownIDs {
            guard let index = unknownIndex[id], x[index].isFinite, y[index].isFinite else {
                throw SolveError.numericalFailure
            }
            positions[id] = CGPoint(x: x[index], y: y[index])
        }
        return Solution(revision: baseRevision + 1, baseRevision: baseRevision, positions: positions)
    }

    private static func validate(
        nodes: [Node],
        constraints: [Constraint],
        anchorID: Int,
        baseRevision: Int
    ) throws {
        guard baseRevision >= 0, baseRevision < Int.max else { throw SolveError.invalidBaseRevision }
        var nodeIDs = Set<Int>()
        for node in nodes {
            guard nodeIDs.insert(node.id).inserted else { throw SolveError.duplicateNodeID(node.id) }
            guard node.raw.x.isFinite, node.raw.y.isFinite, node.initial.x.isFinite, node.initial.y.isFinite else {
                throw SolveError.nonFiniteNodePosition(node.id)
            }
        }
        guard nodeIDs.contains(anchorID) else { throw SolveError.missingAnchor(anchorID) }

        var constraintIDs = Set<Int>()
        for constraint in constraints {
            guard constraintIDs.insert(constraint.id).inserted else {
                throw SolveError.duplicateConstraintID(constraint.id)
            }
            guard constraint.measuredDelta.x.isFinite, constraint.measuredDelta.y.isFinite else {
                throw SolveError.nonFiniteConstraintDelta(constraint.id)
            }
            guard constraint.weight.isFinite, constraint.weight > 0 else {
                throw SolveError.invalidWeight(constraint.id)
            }
            guard constraint.from != constraint.to else { throw SolveError.invalidConstraint(constraint.id) }
            guard nodeIDs.contains(constraint.from), nodeIDs.contains(constraint.to) else {
                throw SolveError.danglingConstraint(constraint.id)
            }
        }

        guard Self.isConnected(nodeIDs: nodeIDs, constraints: constraints, anchorID: anchorID) else {
            throw SolveError.disconnectedGraph
        }
    }

    private static func isConnected(nodeIDs: Set<Int>, constraints: [Constraint], anchorID: Int) -> Bool {
        var neighbors = [Int: [Int]]()
        for constraint in constraints {
            neighbors[constraint.from, default: []].append(constraint.to)
            neighbors[constraint.to, default: []].append(constraint.from)
        }
        var visited = Set([anchorID])
        var pending = [anchorID]
        while let node = pending.popLast() {
            for neighbor in neighbors[node, default: []] where visited.insert(neighbor).inserted {
                pending.append(neighbor)
            }
        }
        return visited == nodeIDs
    }

    private func solveCoordinate(
        unknownIndex: [Int: Int],
        constraints: [Constraint],
        anchorPosition: CGFloat,
        coordinate: KeyPath<CGPoint, CGFloat>,
        maximumWeight: Double
    ) throws -> [CGFloat] {
        let count = unknownIndex.count
        guard count > 0 else { return [] }
        var normal = Array(repeating: Array(repeating: 0.0, count: count), count: count)
        var rhs = Array(repeating: 0.0, count: count)

        for constraint in constraints {
            let weight = constraint.weight / maximumWeight
            let measured = Double(constraint.measuredDelta[keyPath: coordinate])
            let fromIndex = unknownIndex[constraint.from]
            let toIndex = unknownIndex[constraint.to]
            let fromAnchor = fromIndex == nil
            let toAnchor = toIndex == nil

            if let fromIndex {
                normal[fromIndex][fromIndex] += weight
                rhs[fromIndex] -= weight * measured
            }
            if let toIndex {
                normal[toIndex][toIndex] += weight
                rhs[toIndex] += weight * measured
            }
            if let fromIndex, let toIndex {
                normal[fromIndex][toIndex] -= weight
                normal[toIndex][fromIndex] -= weight
            }

            // Move the fixed anchor coefficient to the right side. A from
            // anchor has coefficient -1; a to anchor has coefficient +1.
            if fromAnchor, let toIndex {
                rhs[toIndex] += weight * Double(anchorPosition)
            }
            if toAnchor, let fromIndex {
                rhs[fromIndex] += weight * Double(anchorPosition)
            }
        }

        let values = try Self.solveDenseSystem(normal, rhs)
        return values.map { CGFloat($0) }
    }

    private static func solveDenseSystem(_ matrix: [[Double]], _ vector: [Double]) throws -> [Double] {
        var matrix = matrix
        var vector = vector
        let count = vector.count
        let scale = matrix.flatMap { $0 }.map(abs).max() ?? 0
        let tolerance = max(1e-14, scale * 1e-12)

        for column in 0..<count {
            var pivot = column
            for row in (column + 1)..<count where abs(matrix[row][column]) > abs(matrix[pivot][column]) {
                pivot = row
            }
            guard matrix[pivot][column].isFinite, abs(matrix[pivot][column]) > tolerance else {
                throw SolveError.numericalFailure
            }
            if pivot != column {
                matrix.swapAt(pivot, column)
                vector.swapAt(pivot, column)
            }
            let pivotValue = matrix[column][column]
            for row in (column + 1)..<count {
                let factor = matrix[row][column] / pivotValue
                guard factor.isFinite else { throw SolveError.numericalFailure }
                matrix[row][column] = 0
                for index in (column + 1)..<count {
                    matrix[row][index] -= factor * matrix[column][index]
                }
                vector[row] -= factor * vector[column]
            }
        }

        var result = Array(repeating: 0.0, count: count)
        for row in stride(from: count - 1, through: 0, by: -1) {
            var value = vector[row]
            if row + 1 < count {
                for column in (row + 1)..<count {
                    value -= matrix[row][column] * result[column]
                }
            }
            result[row] = value / matrix[row][row]
            guard result[row].isFinite else { throw SolveError.numericalFailure }
        }
        return result
    }
}
