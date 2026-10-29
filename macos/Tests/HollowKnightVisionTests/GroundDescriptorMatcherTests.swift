import XCTest
@testable import HollowKnightVision

final class GroundDescriptorMatcherTests: XCTestCase {
    func testBoundedSelectionMatchesFullSortAcrossTiesAndCanonicalSegments() {
        var seed: UInt64 = 71
        func random(_ limit: Int) -> Int {
            seed = seed &* 6364136223846793005 &+ 1
            return Int(seed >> 32) % limit
        }
        for count in [0, 1, 6, 11, 100, 2000] {
            for _ in 0..<20 {
                let segments = (0..<count).map { _ in random(50) }
                let errors = (0..<count).map { _ in CGFloat(random(30)) }
                let canonical = Array(Set(segments)).sorted().prefix(4)
                let ranked = (0..<count).sorted {
                    errors[$0] == errors[$1] ? $0 < $1 : errors[$0] < errors[$1]
                }
                var expected = Array(ranked.prefix(6).filter { errors[$0] <= 18 })
                var seen = Set(expected.map { segments[$0] })
                var alternatives = 0
                for index in ranked where errors[index] <= 18 && alternatives < 4 {
                    if seen.insert(segments[index]).inserted {
                        expected.append(index)
                        alternatives += 1
                    }
                }
                for segment in canonical where !seen.contains(segment) {
                    if let index = ranked.first(where: { segments[$0] == segment && errors[$0] <= 18 }) {
                        expected.append(index)
                        seen.insert(segment)
                    }
                }
                let actual = GroundDescriptorMatcher.select(count: count,
                    canonicalSegmentIDs: Array(canonical), threshold: 18,
                    segmentID: { segments[$0] }, error: { errors[$0] })
                XCTAssertEqual(actual.map(\.index), expected)
                XCTAssertEqual(actual.map(\.error), expected.map { errors[$0] })
            }
        }
    }

    func testDistanceWidensSignedValuesAndRejectsIncompatibleDescriptors() {
        XCTAssertEqual(GroundDescriptorMatcher.distance([.min, .max], [.max, .min]), 65535)
        XCTAssertEqual(GroundDescriptorMatcher.distance([-127, 0, 127], [127, 0, -127]), 508.0 / 3, accuracy: 0.000001)
        XCTAssertEqual(GroundDescriptorMatcher.distance([1, 2, 3], [1, 2, 3]), 0)
        XCTAssertEqual(GroundDescriptorMatcher.distance([], []), .infinity)
        XCTAssertEqual(GroundDescriptorMatcher.distance([0], []), .infinity)
    }
}
