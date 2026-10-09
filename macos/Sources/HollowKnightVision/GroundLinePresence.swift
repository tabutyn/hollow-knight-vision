import CoreGraphics
import Foundation

enum GroundLinePresenceState: String, Codable {
    case observing
    case confirmed
    case rejected
}

struct GroundLinePresenceReview: Equatable {
    let id: Int
    let line: CleanFloorLine
    let state: GroundLinePresenceState
    let visibleSeconds: Double
    let detectedFraction: Double
}

/// Counts elapsed, observable screen time in world coordinates. Occlusions,
/// off-screen time and uncertain camera poses contribute neither hits nor misses.
/// Classification follows all accumulated observable time. A line initially
/// rejected during an uncertain entrance can recover only by accumulating
/// enough later, pose-verified detections to cross the same 50% rule.
final class GroundLinePresence {
    private struct Record {
        let id: Int
        var minimumX: CGFloat
        var maximumX: CGFloat
        let worldY: CGFloat
        var visibleSeconds: Double = 0
        var detectedSeconds: Double = 0
        var state = GroundLinePresenceState.observing
        var lastObservable = false
        var lastDetected = false
        var missingObservableSeconds: Double = 0
    }
    private var records = [Record]()
    // World Y buckets preserve the complete evidence ledger while restricting
    // each live query to nearby surfaces. Indices remain stable until reset.
    private var rowBuckets = [Int: [Int]]()
    private var lastObservableIndices = [Int]()

    private func indices(in range: ClosedRange<CGFloat>) -> [Int] {
        let lower = Int(floor(range.lowerBound / 32))
        let upper = Int(floor(range.upperBound / 32))
        guard upper >= lower else { return [] }
        return (lower...upper).flatMap { rowBuckets[$0] ?? [] }.sorted()
    }
    private var nextID = 1
    private var lastTimestamp: Double?
    let minimumObservationSeconds: Double

    private let confirmedLossGraceSeconds: Double

    init(minimumObservationSeconds: Double = 2, confirmedLossGraceSeconds: Double = 0) {
        self.minimumObservationSeconds = max(0, minimumObservationSeconds)
        self.confirmedLossGraceSeconds = max(0, confirmedLossGraceSeconds)
    }

    func reset() {
        records = []
        rowBuckets = [:]
        lastObservableIndices = []
        nextID = 1
        lastTimestamp = nil
    }

    func update(
        lines: [CleanFloorLine], camera: CGPoint, width: Int, height: Int,
        timestamp: Double, poseVerified: Bool, exclusions: [CGRect]
    ) {
        let dt = lastTimestamp.map { timestamp - $0 } ?? 0
        lastTimestamp = timestamp
        guard poseVerified else {
            for index in lastObservableIndices { records[index].lastObservable = false }
            lastObservableIndices = []
            return
        }
        // A long gap provides no evidence about intervening screen contents.
        let elapsed = dt > 0 && dt <= 0.25 ? dt : 0
        var seen = Set<Int>()
        for line in lines {
            let x0 = camera.x + CGFloat(line.xRange.lowerBound)
            let x1 = camera.x + CGFloat(line.xRange.upperBound)
            let worldY = CGFloat(line.row) - camera.y
            let matching = indices(in: (worldY - 4)...(worldY + 4)).filter {
                abs(records[$0].worldY - worldY) <= 4
                    && min(records[$0].maximumX, x1) - max(records[$0].minimumX, x0) >= 8
            }.sorted { records[$0].minimumX < records[$1].minimumX }
            var uncoveredStart = x0
            var additions = [ClosedRange<CGFloat>]()
            for index in matching {
                seen.insert(records[index].id)
                if records[index].minimumX - uncoveredStart >= 16 {
                    additions.append(uncoveredStart...(records[index].minimumX - 1))
                }
                uncoveredStart = max(uncoveredStart, records[index].maximumX + 1)
            }
            if x1 - uncoveredStart + 1 >= 16 { additions.append(uncoveredStart...x1) }
            for span in additions {
                // Extensions earn their own screen-time evidence; one long
                // false detection cannot enlarge an established platform.
                let record = Record(id: nextID, minimumX: span.lowerBound,
                                    maximumX: span.upperBound, worldY: worldY)
                rowBuckets[Int(floor(worldY / 32)), default: []].append(records.count)
                records.append(record)
                seen.insert(nextID)
                nextID += 1
            }
        }
        let visibleIndices = indices(in: (-camera.y)...(CGFloat(height) - camera.y))
        let updateIndices = Set(visibleIndices).union(lastObservableIndices).sorted()
        lastObservableIndices = []
        for index in updateIndices {
            var record = records[index]
            let y = record.worldY + camera.y
            let x0 = max(0, record.minimumX - camera.x)
            let x1 = min(CGFloat(width - 1), record.maximumX - camera.x)
            var visible = CGFloat.zero
            if y >= 0, y + 12 <= CGFloat(height), x1 >= x0 {
                for x in stride(from: Int(x0), through: Int(x1), by: 8) {
                    let sample = CGRect(x: CGFloat(x), y: y - 1, width: 8, height: 13)
                    if !exclusions.contains(where: { $0.intersects(sample) }) { visible += 8 }
                }
            }
            let observable = visible >= min(32, record.maximumX - record.minimumX + 1)
            let detected = seen.contains(record.id)
            if observable && record.lastObservable {
                record.visibleSeconds += elapsed
                // Trapezoidal integration avoids bias from which endpoint
                // first observed a changing detection.
                record.detectedSeconds += elapsed
                    * Double((record.lastDetected ? 1 : 0) + (detected ? 1 : 0)) / 2
            }
            if detected { record.missingObservableSeconds = 0 }
            else if observable && record.lastObservable {
                record.missingObservableSeconds += elapsed
            }
            // Once confirmed, brief foreground cover must not erase geometry
            // or its IDs. Persistent observable misses still reject it.
            let retainingConfirmed = record.state == .confirmed
                && confirmedLossGraceSeconds > 0
                && record.missingObservableSeconds < confirmedLossGraceSeconds
            if !retainingConfirmed,
               record.visibleSeconds + 0.000_001 >= minimumObservationSeconds {
                let fraction = record.visibleSeconds > 0
                    ? record.detectedSeconds / record.visibleSeconds : 1
                if fraction > 0.5 + 0.000_001 { record.state = .confirmed }
                else if fraction < 0.5 - 0.000_001 { record.state = .rejected }
                else { record.state = .observing }
            }
            record.lastObservable = observable
            record.lastDetected = detected
            records[index] = record
            if observable { lastObservableIndices.append(index) }
        }
    }

    func state(minimumX: CGFloat, maximumX: CGFloat, worldY: CGFloat) -> GroundLinePresenceState? {
        indices(in: (worldY - 4)...(worldY + 4)).map { records[$0] }.filter {
            abs($0.worldY - worldY) <= 4
                && min($0.maximumX, maximumX) - max($0.minimumX, minimumX) >= 8
        }.min { abs($0.worldY - worldY) < abs($1.worldY - worldY) }?.state
    }

    func reviews(camera: CGPoint, width: Int, height: Int) -> [GroundLinePresenceReview] {
        indices(in: (-camera.y - 1)...(CGFloat(height) - camera.y + 1)).compactMap { index in
            let record = records[index]
            let row = Int((record.worldY + camera.y).rounded())
            let lower = max(0, Int((record.minimumX - camera.x).rounded()))
            let upper = min(width - 1, Int((record.maximumX - camera.x).rounded()))
            guard row >= 0, row < height, upper >= lower else { return nil }
            return GroundLinePresenceReview(
                id: record.id,
                line: CleanFloorLine(row: row, xRange: lower...upper),
                state: record.state,
                visibleSeconds: record.visibleSeconds,
                detectedFraction: record.visibleSeconds > 0
                    ? record.detectedSeconds / record.visibleSeconds : 1
            )
        }
    }
}
