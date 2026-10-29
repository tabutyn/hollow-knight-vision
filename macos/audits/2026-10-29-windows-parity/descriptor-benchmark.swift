import Foundation
@main struct Benchmark {
    static func main() {
        var seed: UInt64 = 927
        func next() -> Int16 { seed = seed &* 6364136223846793005 &+ 1; return Int16((seed >> 32) % 48) - 24 }
        let bank = (0..<20020).map { _ in (0..<192).map { _ in next() } }
        let query = (0..<192).map { _ in next() }
        let segments = (0..<bank.count).map { $0 / 100 }
        func legacy() -> [Int] {
            let ranked = bank.enumerated().map { item -> (Int, CGFloat) in
                let total = zip(query, item.element).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
                return (item.offset, CGFloat(total) / CGFloat(query.count))
            }.sorted { $0.1 < $1.1 }
            let primary = ranked.prefix(6).filter { $0.1 <= 18 }
            var seen = Set(primary.map { segments[$0.0] })
            var alternatives = [(Int, CGFloat)]()
            for match in ranked where alternatives.count < 4 {
                guard match.1 <= 18 else { break }
                if seen.insert(segments[match.0]).inserted { alternatives.append(match) }
            }
            var canonical = [(Int, CGFloat)]()
            for segment in 0..<4 where !seen.contains(segment) {
                if let candidate = ranked.first(where: { segments[$0.0] == segment && $0.1 <= 18 }) { canonical.append(candidate) }
            }
            return (primary + alternatives + canonical).map { $0.0 }
        }
        func bounded() -> [Int] {
            GroundDescriptorMatcher.select(count: bank.count, canonicalSegmentIDs: [0,1,2,3], threshold: 18,
                segmentID: { segments[$0] }, error: { GroundDescriptorMatcher.distance(query, bank[$0]) }).map(\.index)
        }
        precondition(legacy() == bounded())
        var oldTimes = [Double](), newTimes = [Double](), checksum = 0
        for _ in 0..<20 {
            let start = ProcessInfo.processInfo.systemUptime
            checksum += legacy().reduce(0,+)
            let middle = ProcessInfo.processInfo.systemUptime
            checksum += bounded().reduce(0,+)
            let end = ProcessInfo.processInfo.systemUptime
            oldTimes.append((middle-start)*1000); newTimes.append((end-middle)*1000)
        }
        let old = oldTimes.sorted()[10], new = newTimes.sorted()[10]
        print("bank=20020 descriptor=192 iterations=20 oldMedianMs=\(old) newMedianMs=\(new) speedup=\(old/new) checksum=\(checksum)")
    }
}
