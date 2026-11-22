import CoreGraphics
import Foundation

/// Source-image ground evidence in solve pixels, with a bottom-left origin.
/// Camera/world corrections never rewrite these coordinates. Each line travels
/// with its owning observation and therefore with that observation's island.
struct LiveGroundLineEvidence: Codable, Equatable, Sendable {
    static let walkedSegmentID = Int.max
    let segmentID: Int
    let start: CGPoint
    let end: CGPoint
}

struct LiveWorldGroundGeometry: Codable, Equatable, Sendable {
    let lines: [LiveGroundLineEvidence]
    /// Walks measured between admitted images, expressed in the original
    /// tracking island's coordinates, including portions now off screen.
    var walkedIslands: [IslandWalkedGround]? = nil

    static func captured(reviews: [GroundLinePresenceReview], detected: [CleanFloorLine],
                         frameSize: CGSize, solveWidth: CGFloat,
                         measured: Bool, supportedFloor: CleanFloorLine? = nil,
                         walkedIslands: [IslandWalkedGround] = []) -> Self? {
        guard measured, frameSize.width > 0, frameSize.height > 0,
              solveWidth.isFinite, solveWidth > 0 else { return nil }
        let scale = solveWidth / frameSize.width
        var lines = [LiveGroundLineEvidence]()
        for review in reviews where review.state == .confirmed {
            for detection in detected where abs(detection.row - review.line.row) <= 2 {
                // Persist only a currently observed span. A confirmed old line
                // projected into a black/occluded area is not new evidence.
                for evidence in detection.evidenceRanges {
                    let minimumX = max(evidence.lowerBound, review.line.xRange.lowerBound, 0)
                    let maximumX = min(evidence.upperBound, review.line.xRange.upperBound,
                                       Int(frameSize.width) - 1)
                    guard maximumX - minimumX >= 16,
                          detection.row >= 0, CGFloat(detection.row) < frameSize.height else { continue }
                    let y = (frameSize.height - CGFloat(detection.row)) * scale
                    let line = LiveGroundLineEvidence(segmentID: review.id,
                        start: CGPoint(x: CGFloat(minimumX) * scale, y: y),
                        end: CGPoint(x: CGFloat(maximumX + 1) * scale, y: y))
                    if !lines.contains(line) { lines.append(line) }
                }
            }
        }
        if let floor = supportedFloor, floor.row >= 0 && CGFloat(floor.row) < frameSize.height {
            let left = max(0, floor.xRange.lowerBound)
            let right = min(Int(frameSize.width)-1, floor.xRange.upperBound)
            if left < right {
                // Contact is an independent measurement, so no detector review is
                // needed. The reserved segment identity cannot collide with a review.
                let y = (frameSize.height-CGFloat(floor.row))*scale
                lines.append(.init(segmentID: LiveGroundLineEvidence.walkedSegmentID,
                    start: CGPoint(x: CGFloat(left)*scale, y: y),
                    end: CGPoint(x: CGFloat(right+1)*scale, y: y)))
            }
        }
        return lines.isEmpty && walkedIslands.isEmpty ? nil
            : Self(lines: lines, walkedIslands: walkedIslands.isEmpty ? nil : walkedIslands)
    }

    func isValid(solveWidth: Double, sourceWidth: Int, sourceHeight: Int) -> Bool {
        guard solveWidth.isFinite, solveWidth > 0, sourceWidth > 0, sourceHeight > 0 else { return false }
        let height = Double(sourceHeight) / Double(sourceWidth) * solveWidth
        return (walkedIslands ?? []).allSatisfy(\.isValid) && lines.allSatisfy { line in
            line.segmentID >= 0
                && [line.start.x, line.start.y, line.end.x, line.end.y].allSatisfy(\.isFinite)
                && line.start.x >= 0 && line.end.x <= solveWidth + 0.000001
                && line.end.x > line.start.x && line.start.y == line.end.y
                && line.start.y >= 0 && line.start.y <= height + 0.000001
        }
    }
}

struct IslandWalkedGround: Codable, Equatable, Sendable {
    let owner: LiveIslandCapture
    let spans: [LiveGroundLineEvidence]

    var isValid: Bool {
        owner.measured && owner.id >= 0 && owner.roomID >= 0 && owner.startedAt.isFinite
            && [owner.worldFromLocal.x, owner.worldFromLocal.y, owner.localPose.x, owner.localPose.y].allSatisfy(\.isFinite)
            && !spans.isEmpty && spans.allSatisfy {
                $0.segmentID == LiveGroundLineEvidence.walkedSegmentID
                    && [$0.start.x, $0.start.y, $0.end.x, $0.end.y].allSatisfy(\.isFinite)
                    && $0.start.x < $0.end.x && $0.start.y == $0.end.y
            }
    }
}

struct LiveWorldGroundLine: Equatable, Sendable {
    let observationID: Int
    let islandID: Int
    let roomID: Int
    let segmentID: Int
    let start: CGPoint
    let end: CGPoint
    let placement: LiveIslandPlacement
}

extension LiveWorldSnapshot {
    /// Derived in one snapshot revision with screenshots and landmarks. No
    /// independently corrected global ground bank can lag behind island moves.
    var ownedGroundLines: [LiveWorldGroundLine] {
        let islands = Dictionary(uniqueKeysWithValues: trackingIslands.map { ($0.id, $0) })
        let owners = Dictionary(uniqueKeysWithValues: trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, (island, $0)) }
        })
        return observations.flatMap { observation -> [LiveWorldGroundLine] in
            guard let (island, member) = owners[observation.id], member.measured,
                  let geometry = observation.groundGeometry else { return [] }
            var result = geometry.lines.map { line in
                LiveWorldGroundLine(observationID: observation.id, islandID: island.id,
                    roomID: observation.roomID, segmentID: line.segmentID,
                    start: CGPoint(x: observation.optimizedPose.x + line.start.x,
                                   y: observation.optimizedPose.y + line.start.y),
                    end: CGPoint(x: observation.optimizedPose.x + line.end.x,
                                 y: observation.optimizedPose.y + line.end.y),
                    placement: island.placement)
            }
            for walk in geometry.walkedIslands ?? [] where walk.owner.roomID == observation.roomID {
                let owner = walk.owner
                let current = islands[owner.id]
                let matching = current?.startedAt == owner.startedAt && current?.roomID == owner.roomID ? current : nil
                let translation = matching?.worldFromLocal ?? owner.worldFromLocal
                for span in walk.spans {
                    result.append(.init(observationID: observation.id, islandID: owner.id,
                        roomID: owner.roomID, segmentID: span.segmentID,
                        start: .init(x: translation.x + span.start.x, y: translation.y + span.start.y),
                        end: .init(x: translation.x + span.end.x, y: translation.y + span.end.y),
                        placement: matching?.placement ?? owner.placement))
                }
            }
            return result
        }
    }
}

/// Compact vector presentation only. Repeated measurements of one owned floor
/// contribute to one median-height estimate instead of a stack of strokes.
/// Original capture evidence and camera poses remain untouched.
enum LiveGroundOverlayGeometry {
    private struct Key: Hashable {
        let islandID: Int
        let roomID: Int
        let segmentID: Int
    }
    private struct Span {
        let observationID: Int
        let start: CGFloat
        let end: CGFloat
        let y: CGFloat
    }
    // The edge kernel spans eight source rows. A bounded cluster avoids
    // chaining small differences into a merge of widely separated surfaces.
    private static let maximumHeightSpread: CGFloat = 8

    static func make(_ lines: [LiveWorldGroundLine], scale: CGFloat,
                     anchor: CGPoint, clearanceHeight: CGFloat = 0) -> [GroundHypothesisAtlasLine] {
        guard scale.isFinite, scale > 0, anchor.x.isFinite, anchor.y.isFinite else { return [] }
        let rooms = Dictionary(grouping: lines, by: \.roomID)
        return rooms.keys.sorted().flatMap { room -> [GroundHypothesisAtlasLine] in
            let evidence = rooms[room]!
            let walk = groupedStrokes(evidence.filter { $0.segmentID == LiveGroundLineEvidence.walkedSegmentID },
                                      scale: scale, anchor: anchor, clearanceHeight: 0, acrossIslands: true)
            let edges = groupedStrokes(evidence.filter { $0.segmentID != LiveGroundLineEvidence.walkedSegmentID },
                                       scale: scale, anchor: anchor, clearanceHeight: clearanceHeight, acrossIslands: false)
            guard !walk.isEmpty else { return edges }
            return walk + WalkedFloorCoverage(walk).filtered(edges, clearance: max(6 * scale, clearanceHeight))
        }
    }

    private static func groupedStrokes(_ lines: [LiveWorldGroundLine], scale: CGFloat, anchor: CGPoint,
                                       clearanceHeight: CGFloat, acrossIslands: Bool) -> [GroundHypothesisAtlasLine] {
        var groups = [Key: [Span]]()
        for line in lines {
            guard [line.start.x, line.start.y, line.end.x, line.end.y].allSatisfy(\.isFinite),
                  line.end.x > line.start.x, line.start.y == line.end.y else { continue }
            // Measurements already carry the owning island's current optimized
            // pose. Only contact may join across islands, after projection.
            groups[Key(islandID: acrossIslands ? 0 : line.islandID, roomID: line.roomID,
                       segmentID: line.segmentID), default: []].append(Span(
                observationID: line.observationID, start: line.start.x, end: line.end.x, y: line.start.y))
        }
        let ordered = groups.keys.sorted {
            if $0.islandID != $1.islandID { return $0.islandID < $1.islandID }
            if $0.roomID != $1.roomID { return $0.roomID < $1.roomID }
            return $0.segmentID < $1.segmentID
        }
        func median(_ values: [CGFloat]) -> CGFloat {
            let sorted = values.sorted()
            let middle = sorted.count / 2
            return sorted.count.isMultiple(of: 2)
                ? (sorted[middle-1] + sorted[middle]) / 2 : sorted[middle]
        }
        var result = [GroundHypothesisAtlasLine]()
        var roomLines = [GroundHypothesisAtlasLine]()
        var previous: Key?
        for key in ordered {
            if let previous, previous.roomID != key.roomID || previous.islandID != key.islandID {
                result += GroundStrokeGeometry.coalesced(roomLines, clearanceHeight: clearanceHeight)
                roomLines.removeAll(keepingCapacity: true)
            }
            previous = key
            let spans = groups[key]!.sorted {
                if $0.y != $1.y { return $0.y < $1.y }
                if $0.start != $1.start { return $0.start < $1.start }
                return $0.observationID < $1.observationID
            }
            func appendCluster(_ cluster: [Span]) {
                guard !cluster.isEmpty else { return }
                let sorted = cluster.sorted {
                    $0.start == $1.start ? $0.end < $1.end : $0.start < $1.start
                }
                var component = [Span](), low: CGFloat = 0, high: CGFloat = 0
                func appendComponent() {
                    guard !component.isEmpty else { return }
                    // One image gets one height vote regardless of how many
                    // visible fragments it contributed around occluders.
                    let byObservation = Dictionary(grouping: component, by: \.observationID)
                    let height = median(byObservation.values.map { median($0.map(\.y)) })
                    let y = (((height-anchor.y)*scale)*2).rounded()/2
                    let start = (low-anchor.x)*scale, end = (high-anchor.x)*scale
                    guard start.isFinite, end.isFinite, y.isFinite else { return }
                    roomLines.append(GroundHypothesisAtlasLine(segmentID: key.segmentID,
                        atlasStart: CGPoint(x: start, y: y), atlasEnd: CGPoint(x: end, y: y)))
                }
                for span in sorted {
                    if component.isEmpty || span.start > high {
                        appendComponent()
                        component = [span]; low = span.start; high = span.end
                    } else {
                        component.append(span); high = max(high, span.end)
                    }
                }
                appendComponent()
            }
            var cluster = [Span]()
            for span in spans {
                if let first = cluster.first, span.y-first.y > maximumHeightSpread {
                    appendCluster(cluster); cluster.removeAll(keepingCapacity: true)
                }
                cluster.append(span)
            }
            appendCluster(cluster)
        }
        return result + GroundStrokeGeometry.coalesced(roomLines, clearanceHeight: clearanceHeight)
    }
}

/// Remove only the columns a verified walk covers at this elevation.
struct WalkedFloorCoverage {
    let spans: [GroundHypothesisAtlasLine]

    init(_ walked: [GroundHypothesisAtlasLine]) {
        spans = walked.sorted { $0.atlasStart.x < $1.atlasStart.x }
    }

    init(floors: [CleanFloorLine]) {
        self.init(floors.map { .init(segmentID: LiveGroundLineEvidence.walkedSegmentID,
            atlasStart: .init(x: $0.xRange.lowerBound, y: $0.row), atlasEnd: .init(x: $0.xRange.upperBound + 1, y: $0.row)) })
    }

    func filtered(_ lines: [GroundHypothesisAtlasLine], clearance: CGFloat) -> [GroundHypothesisAtlasLine] {
        lines.flatMap { line in
            uncovered(line.atlasStart.x..<line.atlasEnd.x, y: line.atlasStart.y, tolerance: clearance.nextDown).map { range in
                GroundHypothesisAtlasLine(segmentID: line.segmentID,
                    atlasStart: CGPoint(x: range.lowerBound, y: line.atlasStart.y),
                    atlasEnd: CGPoint(x: range.upperBound, y: line.atlasEnd.y))
            }
        }
    }

    /// Camera registration can retain a genuine textured edge at the contact
    /// elevation. Other nearby rows cannot be ground in the columns traversed.
    func filtered(_ floors: [CleanFloorLine], clearance: CGFloat, keepMatchingRows: CGFloat = -1) -> [CleanFloorLine] {
        guard !spans.isEmpty else { return floors }
        return floors.flatMap { floor -> [CleanFloorLine] in
            let extent = CGFloat(floor.xRange.lowerBound)..<CGFloat(floor.xRange.upperBound + 1)
            let ranges = uncovered(extent, y: CGFloat(floor.row), tolerance: clearance.nextDown,
                                   keepMatchingRows: keepMatchingRows)
            if ranges == [extent] { return [floor] }
            return ranges.compactMap { range in
                let left = Int(ceil(range.lowerBound)), right = Int(range.upperBound.rounded(.down)) - 1
                guard left <= right else { return nil }
                let extent = left...right
                let evidence = floor.evidenceRanges.compactMap { $0.overlaps(extent) ? $0.clamped(to: extent) : nil }
                return evidence.isEmpty ? nil : CleanFloorLine(row: floor.row, xRange: extent, evidenceRanges: evidence)
            }
        }
    }

    func uncovered(_ extent: Range<CGFloat>, y: CGFloat, tolerance: CGFloat, keepMatchingRows: CGFloat = -1) -> [Range<CGFloat>] {
        var left = extent.lowerBound
        var remaining = [Range<CGFloat>]()
        for walk in spans {
            if walk.atlasStart.x >= extent.upperBound { break }
            let separation = abs(walk.atlasStart.y - y)
            guard walk.atlasEnd.x > left, separation <= tolerance, separation > keepMatchingRows else { continue }
            if walk.atlasStart.x > left { remaining.append(left..<walk.atlasStart.x) }
            left = max(left, walk.atlasEnd.x)
            if left >= extent.upperBound { break }
        }
        if left < extent.upperBound { remaining.append(left..<extent.upperBound) }
        return remaining
    }
}

/// Reconcile horizontal display strokes within one coordinate space and room.
/// A shared footprint can extend a stroke. Empty columns and abutting steps survive.
enum GroundStrokeGeometry {
    /// Collapse repeated measurements of the same visible surface while retaining
    /// real unobserved gaps. Evidence ranges, not bridged display extents, vote.
    static func coalescedFloors(_ floors: [CleanFloorLine], frameWidth: Int) -> [CleanFloorLine] {
        let scale = max(1, CGFloat(frameWidth)/640)
        let strokes = floors.enumerated().flatMap { index, floor in
            floor.evidenceRanges.map { range in
                GroundHypothesisAtlasLine(segmentID: index,
                    atlasStart: CGPoint(x: CGFloat(range.lowerBound)/scale, y: CGFloat(floor.row)/scale),
                    atlasEnd: CGPoint(x: CGFloat(range.upperBound+1)/scale, y: CGFloat(floor.row)/scale))
            }
        }
        return coalesced(strokes).compactMap { line in
            let left = Int((line.atlasStart.x*scale).rounded())
            let right = Int((line.atlasEnd.x*scale).rounded())-1
            guard left <= right else { return nil }
            return CleanFloorLine(row: Int((line.atlasStart.y*scale).rounded()), xRange: left...right)
        }
    }
    private struct Stroke {
        var left: CGFloat
        var right: CGFloat
        var low: CGFloat
        var high: CGFloat
        var representative: GroundHypothesisAtlasLine

        init(_ line: GroundHypothesisAtlasLine) {
            left = line.atlasStart.x
            right = line.atlasEnd.x
            low = line.atlasStart.y
            high = low
            representative = line
        }

        func overlaps(_ other: Self) -> Bool {
            guard max(high, other.high) - min(low, other.low) <= 3 else { return false }
            let shared = min(right, other.right) - max(left, other.left)
            if representative.atlasStart.y == other.representative.atlasStart.y { return shared >= 0 }
            return shared >= min(3, right - left, other.right - other.left)
        }

        mutating func absorb(_ other: Self) {
            let a = representative, b = other.representative
            let widthA = a.atlasEnd.x - a.atlasStart.x, widthB = b.atlasEnd.x - b.atlasStart.x
            if widthB > widthA || (widthB == widthA && b.atlasStart.y < a.atlasStart.y) {
                representative = b
            }
            left = min(left, other.left)
            right = max(right, other.right)
            low = min(low, other.low)
            high = max(high, other.high)
        }

        var line: GroundHypothesisAtlasLine {
            let y = representative.atlasStart.y
            return .init(segmentID: representative.segmentID, atlasStart: CGPoint(x: left, y: y),
                atlasEnd: CGPoint(x: right, y: y))
        }
    }

    static func coalesced(_ lines: [GroundHypothesisAtlasLine], clearanceHeight: CGFloat = 0,
                          endpointTolerance: CGFloat = 0) -> [GroundHypothesisAtlasLine] {
        let valid = lines.filter {
            $0.atlasStart.x.isFinite && $0.atlasEnd.x.isFinite && $0.atlasStart.y.isFinite
                && $0.atlasStart.y == $0.atlasEnd.y && $0.atlasStart.x < $0.atlasEnd.x
        }
        var visible = valid
        if clearanceHeight > 0, valid.count > 1 {
            let spans = valid.map { line in
                let extent = line.atlasStart.x..<line.atlasEnd.x
                return GroundClearance.Span(row: line.atlasStart.y, extent: extent, evidence: [extent])
            }
            let ranges = GroundClearance.extents(for: spans, height: clearanceHeight, endpointTolerance: endpointTolerance)
            visible = zip(valid, ranges).compactMap { line, range in
                guard let range else { return nil }
                return GroundHypothesisAtlasLine(segmentID: line.segmentID,
                    atlasStart: CGPoint(x: range.lowerBound, y: line.atlasStart.y),
                    atlasEnd: CGPoint(x: range.upperBound, y: line.atlasEnd.y))
            }
        }
        let ordered = visible.sorted {
            if $0.atlasStart.x != $1.atlasStart.x { return $0.atlasStart.x < $1.atlasStart.x }
            if $0.atlasStart.y != $1.atlasStart.y { return $0.atlasStart.y < $1.atlasStart.y }
            if $0.atlasEnd.x != $1.atlasEnd.x { return $0.atlasEnd.x > $1.atlasEnd.x }
            return $0.segmentID < $1.segmentID
        }
        var active = [Stroke](), finished = [GroundHypothesisAtlasLine]()
        for line in ordered {
            var next = Stroke(line)
            active.removeAll { prior in
                if prior.right < line.atlasStart.x {
                    finished.append(prior.line)
                    return true
                }
                guard next.overlaps(prior) else { return false }
                next.absorb(prior)
                return true
            }
            active.append(next)
        }
        return finished + active.map(\.line)
    }
}
