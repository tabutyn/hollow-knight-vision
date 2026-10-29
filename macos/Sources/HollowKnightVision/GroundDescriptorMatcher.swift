import Foundation

/// Bounded, stable selection from the global bank. Preserve the old full-sort
/// ordering (including ties), without sorting thousands of descriptors per feature.
enum GroundDescriptorMatcher {
    struct Match: Equatable {
        let index: Int
        let segmentID: Int
        let error: CGFloat
    }

    static func select(
        count: Int, canonicalSegmentIDs: [Int], threshold: CGFloat,
        segmentID: (Int) -> Int, error: (Int) -> CGFloat
    ) -> [Match] {
        func precedes(_ a: Match, _ b: Match) -> Bool {
            a.error < b.error || (a.error == b.error && a.index < b.index)
        }
        func insert(_ match: Match, into matches: inout [Match], limit: Int) {
            let position = matches.firstIndex { precedes(match, $0) } ?? matches.count
            guard position < limit else { return }
            matches.insert(match, at: position)
            if matches.count > limit { matches.removeLast() }
        }
        var primary = [Match]()
        primary.reserveCapacity(7)
        var bestBySegment = [Int: Match]()
        for index in 0..<count {
            let distance = error(index)
            guard distance <= threshold else { continue }
            let match = Match(index: index, segmentID: segmentID(index), error: distance)
            insert(match, into: &primary, limit: 6)
            if bestBySegment[match.segmentID].map({ precedes(match, $0) }) ?? true {
                bestBySegment[match.segmentID] = match
            }
        }
        var seen = Set(primary.map(\.segmentID))
        var alternatives = [Match]()
        alternatives.reserveCapacity(5)
        for match in bestBySegment.values where !seen.contains(match.segmentID) {
            insert(match, into: &alternatives, limit: 4)
        }
        seen.formUnion(alternatives.map(\.segmentID))
        var result = primary + alternatives
        for segment in canonicalSegmentIDs where !seen.contains(segment) {
            if let match = bestBySegment[segment] {
                result.append(match)
                seen.insert(segment)
            }
        }
        return result
    }

    static func distance(_ left: [Int16], _ right: [Int16]) -> CGFloat {
        guard !left.isEmpty, left.count == right.count else { return .infinity }
        let total = left.withUnsafeBufferPointer { lhs in
            right.withUnsafeBufferPointer { rhs in
                var sum = 0
                for index in lhs.indices {
                    sum += abs(Int(lhs[index]) - Int(rhs[index]))
                }
                return sum
            }
        }
        return CGFloat(total) / CGFloat(left.count)
    }
}
