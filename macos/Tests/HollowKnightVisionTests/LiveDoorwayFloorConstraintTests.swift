import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveDoorwayFloorConstraintTests: XCTestCase {
    func testNewRoomLayoutSeparatesFootprintsWithoutCreatingHorizontalEvidence() throws {
        let original = try world()
        let observations = Array(original.observations.prefix(4))
        let firstTarget = original.trackingIslands[1]
        let target = LiveTrackingIsland(id: firstTarget.id, roomID: 1, startedAt: firstTarget.startedAt,
            worldFromLocal: firstTarget.worldFromLocal, placement: .estimated, uncertainty: .unknown,
            breakReason: firstTarget.breakReason, members: Array(firstTarget.members.prefix(1)))
        let initial = try LiveWorldSnapshot(anchoredObservationID: 0, observations: observations,
            trackingIslands: [original.trackingIslands[0], target])
        for direction: VisualRoomDirection in [.left, .right] {
            let transition = LiveDoorwayTransition(sourceRoomID: 0, targetRoomID: 1,
                departureTimestamp: 0.45, arrivalTimestamp: 2, direction: direction)
            let laidOut = try XCTUnwrap(LiveEstimatedRoomLayout.placingNewRoom(for: transition, in: initial))
            XCTAssertEqual(laidOut.observations.last?.optimizedPose.x, direction == .right ? 704 : -704)
            XCTAssertEqual(laidOut.observations.last?.optimizedPose.y, -18)
            XCTAssertEqual(laidOut.observations.map(\.rawPose), initial.observations.map(\.rawPose))
            XCTAssertTrue(laidOut.loopClosureEdges.isEmpty)
            XCTAssertTrue(laidOut.doorwayFloorConstraints.isEmpty)
            XCTAssertEqual(laidOut.trackingIslands[1].placement, .estimated)
            XCTAssertEqual(laidOut.trackingIslands[1].uncertainty, .unknown)
            XCTAssertNil(try LiveEstimatedRoomLayout.placingNewRoom(for: transition, in: original),
                         "A continued/reopened room must keep its established layout")
        }
    }

    func testEvidenceRequiresStableMeasuredUnambiguousFloorsAndRecentCut() throws {
        let transition = LiveDoorwayTransition(sourceRoomID: 0, targetRoomID: 1,
            departureTimestamp: 0.45, arrivalTimestamp: 2, direction: .right)
        let original = try world()
        let proposal = try XCTUnwrap(LiveDoorwayFloorEvidence.proposal(for: transition, in: original))
        XCTAssertEqual(proposal.departureSupport, [0, 1, 2])
        XCTAssertEqual(proposal.arrivalSupport, [3, 4, 5])
        XCTAssertEqual(proposal.departureFloor.y, 39)
        XCTAssertEqual(proposal.arrivalFloor.y, 95)
        let aligned = try original.addingDoorwayFloorConstraint(proposal)
        XCTAssertNil(LiveDoorwayFloorEvidence.proposal(for: transition, in: aligned))
        for candidate in [try world(measured: false), try world(missingGround: true),
                          try world(fallingDeparture: true), try world(playerEvidence: false),
                          try world(airborneDeparture: true), try world(playerAtExit: false)] {
            XCTAssertNil(LiveDoorwayFloorEvidence.proposal(for: transition, in: candidate))
        }
        let stale = LiveDoorwayTransition(sourceRoomID: 0, targetRoomID: 1,
            departureTimestamp: 1.5, arrivalTimestamp: 2, direction: .right)
        XCTAssertNil(LiveDoorwayFloorEvidence.proposal(for: stale, in: original))
        let future = transition.offsettingTimestamps(by: 100)
        XCTAssertNil(LiveDoorwayFloorEvidence.proposal(for: future, in: original))
        var encoded = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(original)) as? [String: Any])
        var observations = try XCTUnwrap(encoded["observations"] as? [[String: Any]])
        for index in observations.indices {
            var geometry = try XCTUnwrap(observations[index]["groundGeometry"] as? [String: Any])
            var lines = try XCTUnwrap(geometry["lines"] as? [[String: Any]])
            lines.append(["segmentID": 100, "start": [0, 120], "end": [640, 120]])
            geometry["lines"] = lines
            observations[index]["groundGeometry"] = geometry
        }
        encoded["observations"] = observations
        let ambiguous = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONSerialization.data(withJSONObject: encoded))
        XCTAssertNil(LiveDoorwayFloorEvidence.proposal(for: transition, in: ambiguous))
    }

    func testOwnedOverlayUsesCorrectedFloorAndProvisionalFootprintWithoutLiveFrame() throws {
        let aligned = try world().addingDoorwayFloorConstraint(edge())
        let reopened = try JSONDecoder().decode(LiveWorldSnapshot.self, from: JSONEncoder().encode(aligned))
        let overlay = LiveIslandOverlayGeometry.make(snapshot: reopened, scale: 2, anchor: CGPoint(x: 10, y: 5))
        XCTAssertEqual(overlay.lines.count, 5, "One connector, one estimated island outline; reference island is solid evidence")
        let connector = try XCTUnwrap(overlay.lines.first)
        XCTAssertEqual(connector.kind, .doorwayFloor)
        XCTAssertEqual(connector.start, CGPoint(x: 780, y: 68))
        XCTAssertEqual(connector.end, CGPoint(x: 1780, y: 68))
        let lowerEdge = overlay.lines[1]
        XCTAssertEqual(lowerEdge.kind, .provisionalIsland)
        XCTAssertEqual(lowerEdge.start, CGPoint(x: 1580, y: -122))
        XCTAssertEqual(lowerEdge.end, CGPoint(x: 2860, y: -122))
        XCTAssertTrue(LiveIslandOverlayGeometry.make(snapshot: reopened, scale: .nan, anchor: .zero).lines.isEmpty)
        for language in HollowKnightMenuLanguage.allCases {
            XCTAssertNotNil(StencilGuidanceText.catalog[language.rawValue]?["estimatedIslandPlacement"])
        }
    }

    func testMeasuredFloorAlignmentMovesWholeArrivalIslandAndPreservesHorizontalSpacing() throws {
        let original = try world()
        let result = try original.addingDoorwayFloorConstraint(edge())
        XCTAssertEqual(result.mapRevision, original.mapRevision + 1)
        XCTAssertEqual(result.observations.map(\.optimizedPose.x), [0, 0, 0, 800, 800, 800])
        XCTAssertEqual(result.observations.map(\.optimizedPose.y), [0, 0, 0, -56, -56, -56])
        XCTAssertEqual(result.observations.map(\.rawPose), original.observations.map(\.rawPose))
        XCTAssertEqual(result.observations.map(\.groundGeometry), original.observations.map(\.groundGeometry))
        XCTAssertEqual(result.observations.map(\.observedPlayerBounds), original.observations.map(\.observedPlayerBounds))
        XCTAssertEqual(result.trackingIslands[1].placement, .estimated)
        XCTAssertEqual(result.doorwayFloorConstraints, [edge()])
        XCTAssertTrue(result.ownedGroundLines.allSatisfy { $0.start.y == 39 })
        XCTAssertEqual(result.ownedDoorwayConnectors.first?.start, CGPoint(x: 400, y: 39))
        XCTAssertEqual(result.ownedDoorwayConnectors.first?.end, CGPoint(x: 900, y: 39))
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: JSONEncoder().encode(result)), result)
    }

    func testVisualLoopMovesFloorPartnerOnlyInElevationAndKeepsItsXUnlocalized() throws {
        let base = try world()
        let anchor = LiveWorldObservation(id: 6, sourceObservationID: 6, timestamp: 3,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
            excludedRects: [], rawPose: CGPoint(x: 100, y: 10), optimizedPose: CGPoint(x: 100, y: 10))
        var islands = base.trackingIslands.map {
            $0.replacingTranslation($0.worldFromLocal, placement: .estimated, uncertainty: .unknown)
        }
        islands.append(LiveTrackingIsland(id: 2, roomID: 0, startedAt: 3,
            worldFromLocal: anchor.rawPose, placement: .reference, uncertainty: .reference,
            breakReason: "reference", members: [.init(observationID: 6, localPose: .zero, measured: true)]))
        let keyframes = [LiveWorldKeyframe(id: 0, observationID: 0, sourceObservationID: 0),
                         LiveWorldKeyframe(id: 6, observationID: 6, sourceObservationID: 6)]
        let landmarks = (60..<63).map {
            LiveWorldLandmark(id: $0, keyframeID: 6, sourceObservationID: 6,
                samplePoint: CGPoint(x: $0, y: 20), worldPoint: CGPoint(x: $0 + 100, y: 30),
                descriptor: [0, 1, 2], depth: 1, depthConfidence: 1)
        }
        let initial = try LiveWorldSnapshot(anchoredObservationID: 6,
            observations: base.observations + [anchor], keyframes: keyframes,
            landmarks: landmarks, trackingIslands: islands)
        let floorAligned = try initial.addingDoorwayFloorConstraint(edge())
        let loop = LiveWorldLoopClosureEdge(id: 0, fromKeyframeID: 6, toKeyframeID: 0,
            landmarkIDs: [60, 61, 62], deltaX: -90, deltaY: -3, support: 3, ambiguity: 0)
        let proposed = try LiveWorldSnapshot(mapRevision: floorAligned.mapRevision,
            anchoredObservationID: 6, observations: floorAligned.observations,
            keyframes: keyframes, landmarks: landmarks, loopClosureEdges: [loop],
            trackingIslands: floorAligned.trackingIslands, doorwayFloorConstraints: [edge()])
        let corrected = try LiveIslandCorrection.applying(0, to: proposed)
        XCTAssertEqual(corrected.observations[0].optimizedPose, CGPoint(x: 10, y: 7))
        XCTAssertEqual(corrected.observations[5].optimizedPose, CGPoint(x: 800, y: -49))
        XCTAssertEqual(corrected.trackingIslands[0].placement, .connected)
        XCTAssertEqual(corrected.trackingIslands[1].placement, .estimated)
        XCTAssertEqual(corrected.trackingIslands[1].uncertainty.x, nil)
        XCTAssertEqual(corrected.doorwayFloorConstraints, [edge()])
        XCTAssertTrue(corrected.ownedGroundLines.allSatisfy { $0.start.y == 46 })
        XCTAssertEqual(corrected.ownedDoorwayConnectors.first?.start, CGPoint(x: 410, y: 46))
        XCTAssertEqual(corrected.ownedDoorwayConnectors.first?.end, CGPoint(x: 900, y: 46))
    }

    func testQueuedCaptureUsesCorrectedFloorIslandAndRetainsConstraint() throws {
        let aligned = try world().addingDoorwayFloorConstraint(edge())
        let tracker = try LiveWorldTracker(snapshot: aligned)
        let context = try XCTUnwrap(CGContext(data: nil, width: 640, height: 360,
            bitsPerComponent: 8, bytesPerRow: 640 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        let frame = try XCTUnwrap(context.makeImage())
        let queued = LiveIslandCapture(id: 1, roomID: 1, startedAt: 2,
            worldFromLocal: CGPoint(x: 800, y: -18), localPose: .zero,
            placement: .estimated, uncertainty: .unknown, breakReason: "room-cut", measured: true)
        let update = try tracker.ingest(observationID: 6, timestamp: 3, frame: frame,
            proposedCameraPose: CGPoint(x: 800, y: -18), solveWidth: 640,
            searchForClosure: false, roomID: 1, islandCapture: queued)
        XCTAssertEqual(update.snapshot.observations.last?.rawPose, CGPoint(x: 800, y: -18))
        XCTAssertEqual(update.snapshot.observations.last?.optimizedPose, CGPoint(x: 800, y: -56))
        XCTAssertEqual(update.snapshot.doorwayFloorConstraints, aligned.doorwayFloorConstraints)
        XCTAssertEqual(update.snapshot.ownedDoorwayConnectors, aligned.ownedDoorwayConnectors)
    }

    func testIndependentRoomTranslationCannotTearPersistedFloorAlignment() throws {
        let connected = try world().addingDoorwayFloorConstraint(edge())
        XCTAssertThrowsError(try connected.translatingIslands([1: CGVector(dx: 0, dy: 8)],
            baseRevision: connected.mapRevision))
        let horizontal = try connected.translatingIslands([1: CGVector(dx: 200, dy: 0)],
            baseRevision: connected.mapRevision)
        XCTAssertEqual(horizontal.observations.last?.optimizedPose, CGPoint(x: 1000, y: -56))
        XCTAssertEqual(horizontal.doorwayFloorConstraints, connected.doorwayFloorConstraints)
    }

    func testPredictionMissingGroundAndFallingDepartureCannotProveWalkingDoorway() throws {
        for candidate in [try world(measured: false), try world(missingGround: true),
                          try world(fallingDeparture: true)] {
            XCTAssertThrowsError(try candidate.addingDoorwayFloorConstraint(edge()))
        }
    }

    func testDuplicateTooBriefOrWrongSideEvidenceIsRejected() throws {
        let original = try world()
        let invalid = [
            edge(sourceSupport: [0, 0, 2]), edge(sourceSupport: [1, 2]),
            edge(sourceFloor: CGPoint(x: 100, y: 39)),
            edge(sourceFloor: CGPoint(x: 400, y: 40)),
        ]
        for constraint in invalid {
            XCTAssertThrowsError(try original.addingDoorwayFloorConstraint(constraint))
        }
        let accepted = try original.addingDoorwayFloorConstraint(edge())
        XCTAssertThrowsError(try accepted.addingDoorwayFloorConstraint(edge()))
    }

    func testOlderSnapshotImportDoesNotInventFloorConnection() throws {
        let current = try world()
        for version in [1, 2] {
            var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(current)) as? [String: Any])
            object["schemaVersion"] = version
            object.removeValue(forKey: "doorwayFloorConstraints")
            let restored = try JSONDecoder().decode(LiveWorldSnapshot.self,
                from: JSONSerialization.data(withJSONObject: object))
            XCTAssertEqual(restored.schemaVersion, 3)
            XCTAssertEqual(restored.observations, current.observations)
            if version == 2 { XCTAssertEqual(restored.trackingIslands, current.trackingIslands) }
            else { XCTAssertTrue(restored.trackingIslands.allSatisfy { $0.placement == .legacyUnknown }) }
            XCTAssertTrue(restored.doorwayFloorConstraints.isEmpty)
        }
    }

    private func edge(sourceSupport: [Int] = [0, 1, 2],
                      sourceFloor: CGPoint = CGPoint(x: 400, y: 39)) -> LiveDoorwayFloorConstraint {
        LiveDoorwayFloorConstraint(id: 0, departureObservationID: 2, arrivalObservationID: 5,
            departureFloor: sourceFloor, arrivalFloor: CGPoint(x: 100, y: 95), direction: 1,
            departureSupport: sourceSupport, arrivalSupport: [3, 4, 5])
    }

    private func world(measured: Bool = true, missingGround: Bool = false,
                       fallingDeparture: Bool = false, playerEvidence: Bool = true,
                       airborneDeparture: Bool = false, playerAtExit: Bool = true) throws -> LiveWorldSnapshot {
        let observations = (0..<6).map { id -> LiveWorldObservation in
            let room = id < 3 ? 0 : 1
            let pose = CGPoint(x: room == 0 ? 0 : 800,
                y: room == 0 ? (fallingDeparture ? -CGFloat(id) * 10 : 0) : -18)
            let floor: CGFloat = room == 0 ? 39 : 95
            return LiveWorldObservation(id: id, sourceObservationID: id,
                timestamp: Double(room) * 2 + Double(id % 3) * 0.2,
                sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
                excludedRects: [], roomID: room, rawPose: pose, optimizedPose: pose,
                groundGeometry: missingGround ? nil : LiveWorldGroundGeometry(lines: [
                    .init(segmentID: room, start: CGPoint(x: 0, y: floor), end: CGPoint(x: 640, y: floor))]),
                observedPlayerBounds: playerEvidence ? CGRect(x: room == 0 ? (playerAtExit ? 566 : 310) : 54,
                    y: floor + (room == 0 && airborneDeparture ? 24 : 0), width: 20, height: 30) : nil)
        }
        let islands = (0..<2).map { id -> LiveTrackingIsland in
            let origin = observations[id * 3].rawPose
            return LiveTrackingIsland(id: id, roomID: id, startedAt: Double(id) * 2,
                worldFromLocal: origin, placement: id == 0 ? .reference : .estimated,
                uncertainty: id == 0 ? .reference : .unknown, breakReason: "room-cut",
                members: observations.filter { $0.roomID == id }.map {
                    .init(observationID: $0.id,
                        localPose: CGPoint(x: $0.rawPose.x - origin.x, y: $0.rawPose.y - origin.y),
                        measured: measured)
                })
        }
        return try LiveWorldSnapshot(anchoredObservationID: 0, observations: observations,
                                     trackingIslands: islands)
    }
}
