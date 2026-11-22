import CoreGraphics

/// A footprint belongs to the measured island on which the Knight walked.
/// Atlas image admission runs at a different cadence from contact detection.
struct WalkedGroundMemory {
    private struct Bucket {
        let origin: LiveIslandCapture
        var walked = [GroundHypothesisAtlasLine]()
        var committed = [GroundHypothesisAtlasLine]()
    }
    private var buckets = [Int: Bucket]()

    init(snapshot: LiveWorldSnapshot? = nil) {
        guard let snapshot else { return }
        let observed = Dictionary(uniqueKeysWithValues: snapshot.observations.compactMap { observation in
            observation.groundGeometry == nil ? nil : (observation.id, observation)
        })
        for island in snapshot.trackingIslands {
            for member in island.members where member.measured {
                if let observation = observed[member.observationID] {
                    accept(observation, island: island, member: member)
                }
            }
        }
    }

    mutating func acceptLatest(_ snapshot: LiveWorldSnapshot) {
        guard let latest = snapshot.observations.last,
              latest.groundGeometry != nil,
              let island = snapshot.trackingIslands.last(where: { $0.members.last?.observationID == latest.id }),
              let member = island.members.last, member.measured else { return }
        accept(latest, island: island, member: member)
    }

    private mutating func accept(_ observation: LiveWorldObservation, island: LiveTrackingIsland, member: LiveIslandMember) {
        guard let geometry = observation.groundGeometry else { return }
        for walk in geometry.walkedIslands ?? [] where walk.owner.roomID == island.roomID {
            for span in walk.spans {
                insert(.init(segmentID: span.segmentID, atlasStart: span.start, atlasEnd: span.end), owner: walk.owner, committed: true)
            }
        }
        let capture = LiveIslandCapture(id: island.id, roomID: island.roomID, startedAt: island.startedAt,
            worldFromLocal: island.worldFromLocal, localPose: member.localPose, placement: island.placement,
            uncertainty: island.uncertainty, breakReason: island.breakReason, measured: true)
        for line in geometry.lines where line.segmentID == LiveGroundLineEvidence.walkedSegmentID {
            record(left: line.start.x, right: line.end.x, y: line.start.y, capture: capture, committed: true)
        }
    }

    mutating func record(_ floor: CleanFloorLine?, capture: LiveIslandCapture, pixelsPerSolve: CGFloat, height: Int) {
        guard let floor, capture.measured, pixelsPerSolve > 0 else { return }
        record(left: CGFloat(floor.xRange.lowerBound) / pixelsPerSolve,
               right: CGFloat(floor.xRange.upperBound + 1) / pixelsPerSolve,
               y: CGFloat(height - floor.row) / pixelsPerSolve, capture: capture, committed: false)
    }

    private mutating func record(left: CGFloat, right: CGFloat, y: CGFloat, capture: LiveIslandCapture, committed: Bool) {
        let row = capture.localPose.y + y
        let line = GroundHypothesisAtlasLine(segmentID: LiveGroundLineEvidence.walkedSegmentID,
            atlasStart: .init(x: capture.localPose.x + left, y: row), atlasEnd: .init(x: capture.localPose.x + right, y: row))
        insert(line, owner: capture, committed: committed)
    }

    private mutating func insert(_ line: GroundHypothesisAtlasLine, owner: LiveIslandCapture, committed: Bool) {
        if buckets[owner.id]?.origin.startedAt != owner.startedAt { buckets[owner.id] = Bucket(origin: owner) }
        func merge(into lines: inout [GroundHypothesisAtlasLine]) {
            guard !lines.contains(where: { covers($0, line) }) else { return }
            lines = GroundStrokeGeometry.coalesced(lines + [line])
        }
        merge(into: &buckets[owner.id]!.walked)
        if committed { merge(into: &buckets[owner.id]!.committed) }
    }

    func pending(in room: Int) -> [IslandWalkedGround] {
        buckets.values.filter { $0.origin.roomID == room }.sorted { $0.origin.id < $1.origin.id }.compactMap { bucket in
            let spans = bucket.walked.compactMap { line -> LiveGroundLineEvidence? in
                guard !bucket.committed.contains(where: { covers($0, line) }) else { return nil }
                return .init(segmentID: line.segmentID, start: line.atlasStart, end: line.atlasEnd)
            }
            return spans.isEmpty ? nil : .init(owner: bucket.origin, spans: spans)
        }
    }

    private func forEachWorldLine(room: Int, projection: LiveIslandWorldProjection?,
                                  _ visit: (LiveWorldGroundLine) -> Void) {
        for bucket in buckets.values where bucket.origin.roomID == room {
            let owner = bucket.origin
            let translation: CGPoint
            if let projection {
                guard let pose = projection.worldPose(for: owner) else { continue }
                translation = CGPoint(x: pose.x - owner.localPose.x, y: pose.y - owner.localPose.y)
            } else {
                translation = owner.worldFromLocal
            }
            for line in bucket.walked {
                let y = translation.y + line.atlasStart.y
                visit(.init(observationID: owner.id, islandID: owner.id, roomID: room, segmentID: line.segmentID,
                    start: .init(x: translation.x + line.atlasStart.x, y: y),
                    end: .init(x: translation.x + line.atlasEnd.x, y: y), placement: owner.placement))
            }
        }
    }

    /// Use the full footprint, including ground outside the current image. Reproject
    /// from the owner on every chart handoff so an estimated placement can be corrected.
    func pathPlatforms(room: Int, projection: LiveIslandWorldProjection?, scale: CGFloat, anchor: CGPoint) -> [PathPlatform] {
        var lines = [LiveWorldGroundLine]()
        forEachWorldLine(room: room, projection: projection) { lines.append($0) }
        return LiveGroundOverlayGeometry.make(lines, scale: scale, anchor: anchor).compactMap(PathPlatform.init(stroke:))
    }

    func visible(in capture: LiveIslandCapture, projection: LiveIslandWorldProjection?, pixelsPerSolve: CGFloat,
                 width: Int, height: Int) -> [CleanFloorLine] {
        guard width > 0, height > 0, pixelsPerSolve > 0 else { return [] }
        let camera: CGPoint
        if let projection {
            guard let pose = projection.worldPose(for: capture) else { return [] }
            camera = pose
        } else {
            camera = capture.worldPose
        }
        var lines = [CleanFloorLine]()
        forEachWorldLine(room: capture.roomID, projection: projection) { line in
            let left = max(0, Int(((line.start.x - camera.x) * pixelsPerSolve).rounded()))
            let right = min(width, Int(((line.end.x - camera.x) * pixelsPerSolve).rounded()))
            let row = height - Int(((line.start.y - camera.y) * pixelsPerSolve).rounded())
            if left < right, (0..<height).contains(row) {
                lines.append(.init(row: row, xRange: left...(right - 1)))
            }
        }
        return GroundStrokeGeometry.coalescedFloors(lines, frameWidth: width)
    }
}

private func covers(_ known: GroundHypothesisAtlasLine, _ additional: GroundHypothesisAtlasLine) -> Bool {
    abs(known.atlasStart.y - additional.atlasStart.y) <= 2 &&
        known.atlasStart.x <= additional.atlasStart.x + 0.5 && known.atlasEnd.x >= additional.atlasEnd.x - 0.5
}
