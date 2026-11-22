import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveWorldGroundGeometryTests: XCTestCase {
    func testWalkSurvivesOccludedIdleFrameAndCommitsAtNextCameraAdmission() throws {
        func capture(_ dx: CGFloat, room: Int = 1) -> LiveIslandCapture {
            .init(id: room, roomID: room, startedAt: 1, worldFromLocal: .init(x: 120, y: 20),
                  localPose: .init(x: dx, y: 0), placement: .estimated, uncertainty: .unknown,
                  breakReason: "capture", measured: true)
        }
        var memory = WalkedGroundMemory()
        memory.record(.init(row: 260, xRange: 20...100), capture: capture(0), pixelsPerSolve: 1, height: 360)
        memory.record(nil, capture: capture(10), pixelsPerSolve: 1, height: 360)
        let visible = memory.visible(in: capture(10), projection: nil, pixelsPerSolve: 1, width: 640, height: 360)
        XCTAssertEqual(visible, [.init(row: 260, xRange: 10...90)])
        XCTAssertTrue(memory.visible(in: capture(10, room: 2), projection: nil, pixelsPerSolve: 1, width: 640, height: 360).isEmpty)
        XCTAssertTrue(memory.pending(in: 2).isEmpty)
        let carrier = LiveIslandCapture(id: 5, roomID: 1, startedAt: 5, worldFromLocal: .init(x: 1400, y: 0),
            localPose: .zero, placement: .estimated, uncertainty: .unknown, breakReason: "new tracking island", measured: true)
        XCTAssertTrue(memory.visible(in: carrier, projection: nil, pixelsPerSolve: 1, width: 640, height: 360).isEmpty)
        let geometry = try XCTUnwrap(LiveWorldGroundGeometry.captured(reviews: [], detected: [], frameSize: .init(width: 640, height: 360),
            solveWidth: 640, measured: true, walkedIslands: memory.pending(in: 1)))
        let observation = LiveWorldObservation(id: 0, sourceObservationID: 0, timestamp: 1, sourceWidth: 640, sourceHeight: 360,
            solveWidth: 640, sampleWidth: 160, excludedRects: [], roomID: 1, rawPose: carrier.worldPose,
            optimizedPose: carrier.worldPose, groundGeometry: geometry)
        let saved = try LiveWorldSnapshot(anchoredObservationID: 0, observations: [observation], trackingIslands: [carrier.island(observationID: 0)])
        XCTAssertEqual(saved.ownedGroundLines.map(\.start), [.init(x: 140, y: 120)])
        XCTAssertEqual(saved.ownedGroundLines.map(\.islandID), [capture(0).id])
        memory.acceptLatest(saved)
        XCTAssertTrue(memory.pending(in: 1).isEmpty)
        let restored = WalkedGroundMemory(snapshot: try JSONDecoder().decode(LiveWorldSnapshot.self, from: JSONEncoder().encode(saved)))
        XCTAssertEqual(restored.visible(in: capture(10), projection: nil, pixelsPerSolve: 1, width: 640, height: 360), visible)
        let projection = LiveIslandWorldProjection(snapshot: saved)
        XCTAssertEqual(restored.pathPlatforms(room: 1, projection: projection, scale: 1, anchor: carrier.worldFromLocal),
                       [.init(left: -1260, right: -1179, y: 120)])
        XCTAssertTrue(restored.pathPlatforms(room: 2, projection: projection, scale: 1, anchor: .zero).isEmpty)
    }

    func testWalkInvalidatesTraversedColumnsThroughKnightBodyAndPreservesOtherLedges() {
        let coverage = WalkedFloorCoverage(floors: [.init(row: 100, xRange: 30...90)])
        let falseLedge = CleanFloorLine(row: 81, xRange: 0...100, evidenceRanges: [0...39, 54...100])
        let other = CleanFloorLine(row: 60, xRange: 0...100)
        let same = CleanFloorLine(row: 101, xRange: 30...90)
        let filtered = coverage.filtered([falseLedge, other, same], clearance: 40, keepMatchingRows: 2)
        XCTAssertEqual(filtered, [.init(row: 81, xRange: 0...29), .init(row: 81, xRange: 91...100), other, same])
        XCTAssertEqual(coverage.filtered([same], clearance: 40), [])
    }
    func testContactGroundPersistsOnlyWithMeasuredCamera() throws {
        let floor = CleanFloorLine(row: 260, xRange: 150...158)
        let geometry = try XCTUnwrap(LiveWorldGroundGeometry.captured(reviews: [], detected: [],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640, measured: true, supportedFloor: floor))
        XCTAssertEqual(geometry.lines.map(\.start), [CGPoint(x: 150, y: 100)])
        XCTAssertEqual(geometry.lines.map(\.end), [CGPoint(x: 159, y: 100)])
        XCTAssertTrue(geometry.isValid(solveWidth: 640, sourceWidth: 640, sourceHeight: 360))
        XCTAssertNil(LiveWorldGroundGeometry.captured(reviews: [], detected: [],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640, measured: false, supportedFloor: floor))
    }

    func testStackedRowsMergeWithoutConnectingGapOrStep() {
        let floors = [CleanFloorLine(row: 100, xRange: 10...90),
                      CleanFloorLine(row: 101, xRange: 25...89),
                      CleanFloorLine(row: 102, xRange: 50...100),
                      CleanFloorLine(row: 100, xRange: 110...150),
                      CleanFloorLine(row: 109, xRange: 90...150)]
        let merged = GroundStrokeGeometry.coalescedFloors(floors, frameWidth: 640)
        XCTAssertEqual(merged.count, 3)
        XCTAssertTrue(merged.contains { $0.xRange == 10...100 })
        XCTAssertTrue(merged.contains { $0.xRange == 110...150 })
        XCTAssertTrue(merged.contains { $0.row == 109 && $0.xRange == 90...150 })
    }
    private func overlayLine(_ observation: Int, y: CGFloat, start: CGFloat = 0, end: CGFloat = 100,
                             island: Int = 1, segment: Int = 1, room: Int = 0) -> LiveWorldGroundLine {
        LiveWorldGroundLine(observationID: observation, islandID: island, roomID: room, segmentID: segment,
            start: CGPoint(x: start, y: y), end: CGPoint(x: end, y: y), placement: .estimated)
    }

    func testWalkedFootprintsJoinAcrossPlacedIslandsAndKeepRoomBoundariesAndGaps() {
        let walk = LiveGroundLineEvidence.walkedSegmentID
        let evidence = [
            overlayLine(0, y: 80, start: 10, end: 60, segment: walk),
            overlayLine(1, y: 82, start: 55, end: 95, island: 2, segment: walk),
            overlayLine(2, y: 81, start: 100, end: 110, island: 2, segment: walk),
            overlayLine(3, y: 83, start: 0, end: 125, island: 3),
            overlayLine(4, y: 97, start: 40, end: 70, island: 3),
            overlayLine(5, y: 80, start: 0, end: 90, segment: walk, room: 1)]
        let overlay = LiveGroundOverlayGeometry.make(evidence, scale: 1, anchor: .zero, clearanceHeight: 20)
        let walked = overlay.filter { $0.segmentID == walk }
        XCTAssertEqual(walked.map(\.atlasStart.x), [10, 100, 0])
        XCTAssertEqual(walked.map(\.atlasEnd.x), [95, 110, 90])
        let edge = overlay.filter { $0.atlasStart.y == 83 }
        XCTAssertEqual(edge.map(\.atlasStart.x), [0, 95, 110])
        XCTAssertEqual(edge.map(\.atlasEnd.x), [10, 100, 125])
    }

    func testEightNearbyMeasurementsProduceOneFloorEstimate() {
        let repeated = (0..<8).map { overlayLine($0, y: 20+CGFloat($0)) }
        let overlay = LiveGroundOverlayGeometry.make(repeated, scale: 2, anchor: .zero)
        XCTAssertEqual(overlay.count, 1)
        XCTAssertEqual(overlay.first?.atlasStart, CGPoint(x: 0, y: 47))
        XCTAssertEqual(overlay.first?.atlasEnd, CGPoint(x: 200, y: 47))
        XCTAssertEqual(LiveGroundOverlayGeometry.make(repeated.reversed(), scale: 2, anchor: .zero), overlay)
        XCTAssertEqual(repeated.map(\.start.y), (0..<8).map { 20+CGFloat($0) })
    }

    func testNearbyDistinctPlatformsAndDisconnectedEvidenceRemainSeparate() {
        let lines = [overlayLine(0, y: 20, end: 20), overlayLine(1, y: 22, start: 30, end: 50),
            overlayLine(2, y: 21, start: 50, end: 90, segment: 2), overlayLine(3, y: 20, island: 2)]
        let overlay = LiveGroundOverlayGeometry.make(lines, scale: 1, anchor: .zero)
        XCTAssertEqual(overlay.count, 4)
        XCTAssertEqual(overlay[0].atlasEnd.x, 20)
        XCTAssertEqual(overlay[1].atlasStart.x, 30)
        XCTAssertEqual(overlay[0].atlasStart.y, 20)
        XCTAssertEqual(overlay[1].atlasStart.y, 22)
    }

    func testOverlappingSegmentIDsShareOneStrokeOnlyWithinTheirRoomAndIsland() {
        let lines = [overlayLine(0, y: 20, end: 50), overlayLine(1, y: 22, start: 40, end: 100, segment: 2),
            overlayLine(2, y: 21, end: 100, island: 2)]
        let overlay = LiveGroundOverlayGeometry.make(lines, scale: 1, anchor: .zero)
        XCTAssertEqual(overlay.count, 2)
        XCTAssertEqual(overlay[0].atlasStart, CGPoint(x: 0, y: 22))
        XCTAssertEqual(overlay[0].atlasEnd, CGPoint(x: 100, y: 22))
        XCTAssertEqual(overlay[1].atlasStart.y, 21)
        XCTAssertEqual(LiveGroundOverlayGeometry.make(lines.reversed(), scale: 1, anchor: .zero), overlay)
    }

    func testHeightClusteringCannotChainAcrossAnArbitrarilyTallStack() {
        let lines = [0, 4, 8, 12].enumerated().map { overlayLine($0.offset, y: CGFloat($0.element)) }
        let overlay = LiveGroundOverlayGeometry.make(lines, scale: 1, anchor: .zero)
        XCTAssertEqual(overlay.map(\.atlasStart.y), [4, 12])
    }

    func testOneFragmentedObservationCannotOutvoteRepeatedFloorHeight() {
        let manyFragments = (0..<20).map { _ in overlayLine(0, y: 20) }
        let lines = manyFragments + [overlayLine(1, y: 26), overlayLine(2, y: 26)]
        let overlay = LiveGroundOverlayGeometry.make(lines, scale: 1, anchor: .zero)
        XCTAssertEqual(overlay.count, 1)
        XCTAssertEqual(overlay.first?.atlasStart.y, 26)
    }

    func testSavedOverlayMeasurementsWhenRequested() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_GROUND_OVERLAY_AUDIT_WORLD"] else {
            throw XCTSkip("Set saved world JSON to measure overlay consolidation")
        }
        let world = try JSONDecoder().decode(LiveWorldSnapshot.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
        let lines = world.ownedGroundLines
        let before = Dictionary(grouping: lines, by: { "\($0.islandID):\($0.segmentID)" })
            .values.reduce(0) { count, group in
                count + Set(group.map { ($0.start.y*2).rounded()/2 }).count
            }
        let overlay = LiveGroundOverlayGeometry.make(lines, scale: 1, anchor: .zero)
        print("Saved ground overlay: \(lines.count) captured spans, \(before) distinct owned heights, \(overlay.count) consensus strokes")
        let repeated = lines.filter { $0.islandID == 1341 && $0.segmentID == 458 }
        let selected = LiveGroundOverlayGeometry.make(repeated, scale: 1, anchor: .zero)
        print("Reported dense floor: \(Set(repeated.map { ($0.start.y*2).rounded()/2 }).count) prior heights, \(selected.count) consensus strokes")
        XCTAssertLessThan(overlay.count, before)
        XCTAssertEqual(selected.count, 1)
    }

    func testObservedCandidatesRemainSeparateAcrossCorrectionAndLegacyDecode() throws {
        let candidates = try XCTUnwrap(LiveObservedFloorCandidates.captured(
            detected: [CleanFloorLine(row: 300, xRange: 10...600)],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640, measured: true))
        XCTAssertEqual(candidates.lines.first?.start, CGPoint(x: 10, y: 60))
        let observation = LiveWorldObservation(id: 0, sourceObservationID: 0, timestamp: 0,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], rawPose: .zero, optimizedPose: .zero,
            observedFloorCandidates: candidates)
        let island = LiveTrackingIsland(id: 0, roomID: 0, startedAt: 0, worldFromLocal: .zero,
            placement: .estimated, uncertainty: .unknown, breakReason: "gap",
            members: [.init(observationID: 0, localPose: .zero, measured: true)])
        let world = try LiveWorldSnapshot(anchoredObservationID: 0, observations: [observation],
            trackingIslands: [island])
        let shifted = try world.translatingIslands([0: CGVector(dx: 12, dy: 8)], baseRevision: world.mapRevision)
        let data = try JSONEncoder().encode(shifted)
        let reopened = try JSONDecoder().decode(LiveWorldSnapshot.self, from: data)
        XCTAssertEqual(reopened.observations[0].observedFloorCandidates, candidates)
        XCTAssertNil(reopened.observations[0].groundGeometry)
        XCTAssertTrue(reopened.ownedGroundLines.isEmpty)
        XCTAssertEqual(reopened.observations[0].optimizedPose, CGPoint(x: 12, y: 8))
        var old = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        var observations = try XCTUnwrap(old["observations"] as? [[String: Any]])
        observations[0].removeValue(forKey: "observedFloorCandidates"); old["observations"] = observations
        let legacy = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONSerialization.data(withJSONObject: old))
        XCTAssertNil(legacy.observations[0].observedFloorCandidates)
        XCTAssertEqual(legacy.observations[0].optimizedPose, CGPoint(x: 12, y: 8))
        XCTAssertNil(LiveObservedFloorCandidates.captured(detected: [CleanFloorLine(row: 300, xRange: 10...600)],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640, measured: false))
    }

    func testCaptureKeepsOnlyObservedConfirmedSpansAtSolveScale() throws {
        let line = CleanFloorLine(row: 600, xRange: 32...400,
            evidenceRanges: [32...159, 288...400])
        let review = GroundLinePresenceReview(id: 7, line: line, state: .confirmed,
            visibleSeconds: 2, detectedFraction: 1)
        let result = try XCTUnwrap(LiveWorldGroundGeometry.captured(reviews: [review],
            detected: [line], frameSize: CGSize(width: 1280, height: 720), solveWidth: 640,
            measured: true))
        XCTAssertEqual(result.lines, [
            LiveGroundLineEvidence(segmentID: 7, start: CGPoint(x: 16, y: 60), end: CGPoint(x: 80, y: 60)),
            LiveGroundLineEvidence(segmentID: 7, start: CGPoint(x: 144, y: 60), end: CGPoint(x: 200.5, y: 60))])
        XCTAssertTrue(result.isValid(solveWidth: 640, sourceWidth: 1280, sourceHeight: 720))
        XCTAssertNil(LiveWorldGroundGeometry.captured(reviews: [review], detected: [line],
            frameSize: CGSize(width: 1280, height: 720), solveWidth: 640, measured: false))
        XCTAssertNil(LiveWorldGroundGeometry.captured(reviews: [review], detected: [],
            frameSize: CGSize(width: 1280, height: 720), solveWidth: 640, measured: true))
    }

    func testUnconfirmedGroundDoesNotBecomePersistentGeometry() {
        let line = CleanFloorLine(row: 300, xRange: 10...600)
        let review = GroundLinePresenceReview(id: 7, line: line, state: .observing,
            visibleSeconds: 0.1, detectedFraction: 1)
        XCTAssertNil(LiveWorldGroundGeometry.captured(reviews: [review], detected: [line],
            frameSize: CGSize(width: 640, height: 360), solveWidth: 640, measured: true))
    }

    func testOverlayMergesRepeatedSpansWithoutJoiningIslandsOrFillingGaps() {
        func line(_ id: Int, _ island: Int, _ start: CGFloat, _ end: CGFloat) -> LiveWorldGroundLine {
            LiveWorldGroundLine(observationID: id, islandID: island, roomID: 0, segmentID: 1,
                start: CGPoint(x: start, y: 20), end: CGPoint(x: end, y: 20), placement: .estimated)
        }
        let evidence = [line(0, 1, 0, 50), line(1, 1, 25, 70), line(2, 1, 80, 90),
                        line(3, 2, 0, 70)]
        let overlay = LiveGroundOverlayGeometry.make(evidence, scale: 2, anchor: CGPoint(x: 5, y: 10))
        XCTAssertEqual(overlay.count, 3)
        XCTAssertEqual(overlay.map(\.atlasStart), [CGPoint(x: -10, y: 20),
            CGPoint(x: 150, y: 20), CGPoint(x: -10, y: 20)])
        XCTAssertEqual(overlay.map(\.atlasEnd), [CGPoint(x: 130, y: 20),
            CGPoint(x: 170, y: 20), CGPoint(x: 130, y: 20)])
        XCTAssertEqual(evidence[0].end, CGPoint(x: 50, y: 20))
    }

    func testInvalidGeometryCannotEnterSavedWorld() {
        let geometry = LiveWorldGroundGeometry(lines: [LiveGroundLineEvidence(segmentID: 1,
            start: CGPoint(x: 1, y: CGFloat.nan), end: CGPoint(x: 30, y: 100))])
        let observation = LiveWorldObservation(id: 0, sourceObservationID: 0, timestamp: 0,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], rawPose: .zero, optimizedPose: .zero, groundGeometry: geometry)
        XCTAssertThrowsError(try LiveWorldSnapshot(anchoredObservationID: 0, observations: [observation]))
    }

    func testPredictionOnlyIngestCannotRetainGroundGeometry() throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360,
            bitsPerComponent: 8, bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let frame = try XCTUnwrap(context.makeImage())
        let geometry = LiveWorldGroundGeometry(lines: [LiveGroundLineEvidence(segmentID: 1,
            start: CGPoint(x: 1, y: 30), end: CGPoint(x: 200, y: 30))])
        let capture = LiveIslandCapture(id: 0, roomID: 0, startedAt: 0, worldFromLocal: .zero,
            localPose: .zero, placement: .estimated, uncertainty: .unknown,
            breakReason: "prediction", measured: false)
        let tracker = LiveWorldTracker()
        let update = try tracker.ingest(observationID: 0, timestamp: 0, frame: frame,
            proposedCameraPose: .zero, solveWidth: 640, islandCapture: capture, groundGeometry: geometry,
            observedFloorCandidates: LiveObservedFloorCandidates(lines: geometry.lines))
        XCTAssertNil(update.snapshot.observations.first?.groundGeometry)
        XCTAssertNil(update.snapshot.observations.first?.observedFloorCandidates)
        XCTAssertTrue(update.snapshot.ownedGroundLines.isEmpty)
        XCTAssertTrue(update.snapshot.keyframes.isEmpty)
    }
}
