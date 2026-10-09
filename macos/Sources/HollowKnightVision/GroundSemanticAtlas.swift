import CoreGraphics
import Foundation

/// Chooses which stable tracker lines are semantic ground. The camera tracker
/// retains every texture line it needs for pose identity; weaker labeled edge
/// tuning can therefore change the green map without changing camera motion.
final class GroundSemanticAtlas {
    private struct Record {
        var visibleFrames = 0
        var supportedFrames = 0
        var geometry: GroundHypothesisAtlasLine

        var approved: Bool {
            visibleFrames >= 6 && supportedFrames * 10 > visibleFrames * 3
        }
    }
    private var records = [Int: Record]()

    func reset() {
        records = [:]
    }

    func update(
        trackingLines: [GroundHypothesisAtlasLine],
        semanticLines: [CleanFloorLine],
        camera: CGPoint?,
        frameWidth: Int,
        atlasHeight: CGFloat,
        poseVerified: Bool
    ) -> [GroundHypothesisAtlasLine] {
        guard atlasHeight > 0 else { return [] }

        // Segment reassociation can replace an ID. Transfer its evidence only
        // when the replacement occupies the same stable atlas geometry.
        let previous = records
        for line in trackingLines where records[line.segmentID] == nil {
            if let inherited = previous.values.first(where: {
                verticallyMatches(line, $0.geometry)
                    && horizontalOverlap(line, $0.geometry) >= 16
            }) {
                records[line.segmentID] = Record(
                    visibleFrames: inherited.visibleFrames,
                    supportedFrames: inherited.supportedFrames,
                    geometry: line
                )
            } else {
                records[line.segmentID] = Record(geometry: line)
            }
        }

        if poseVerified, let camera {
            let observations = semanticLines.map { line in
                GroundHypothesisAtlasLine(
                    segmentID: -1,
                    atlasStart: CGPoint(
                        x: camera.x + CGFloat(line.xRange.lowerBound),
                        y: atlasHeight - (CGFloat(line.row) - camera.y)
                    ),
                    atlasEnd: CGPoint(
                        x: camera.x + CGFloat(line.xRange.upperBound + 1),
                        y: atlasHeight - (CGFloat(line.row) - camera.y)
                    )
                )
            }
            for line in trackingLines {
                let screenY = atlasHeight - line.atlasStart.y + camera.y
                let screenX0 = line.atlasStart.x - camera.x
                let screenX1 = line.atlasEnd.x - camera.x
                let visibleWidth = max(
                    0,
                    min(CGFloat(frameWidth), screenX1) - max(0, screenX0)
                )
                guard screenY >= 0, screenY < atlasHeight, visibleWidth >= 16,
                      var record = records[line.segmentID]
                else { continue }
                record.visibleFrames += 1
                if observations.contains(where: {
                    verticallyMatches(line, $0)
                        && horizontalOverlap(line, $0) >= 8
                }) {
                    record.supportedFrames += 1
                }
                record.geometry = line
                records[line.segmentID] = record
            }
        }

        let currentIDs = Set(trackingLines.map(\.segmentID))
        records = records.filter { currentIDs.contains($0.key) }
        return trackingLines.filter {
            records[$0.segmentID]?.approved == true
        }
    }

    private func verticallyMatches(
        _ left: GroundHypothesisAtlasLine,
        _ right: GroundHypothesisAtlasLine
    ) -> Bool {
        abs(left.atlasStart.y - right.atlasStart.y) <= 4
    }

    private func horizontalOverlap(
        _ left: GroundHypothesisAtlasLine,
        _ right: GroundHypothesisAtlasLine
    ) -> CGFloat {
        max(0, min(left.atlasEnd.x, right.atlasEnd.x)
            - max(left.atlasStart.x, right.atlasStart.x))
    }
}
