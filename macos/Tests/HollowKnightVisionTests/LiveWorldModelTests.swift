import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveWorldModelTests: XCTestCase {
    func testRoundTripAndPoseReplacementPreserveRawEvidence() throws {
        let original = try fixture()
        let decoded = try JSONDecoder().decode(LiveWorldSnapshot.self, from: JSONEncoder().encode(original))
        XCTAssertEqual(decoded, original)

        let replaced = try original.replacingOptimizedPoses(
            [10: CGPoint(x: 0, y: 0), 11: CGPoint(x: 5, y: -2)], baseRevision: 7
        )
        XCTAssertEqual(replaced.mapRevision, 8)
        XCTAssertEqual(replaced.observations.map(\.rawPose), original.observations.map(\.rawPose))
        XCTAssertEqual(replaced.observations.map(\.localPose), original.observations.map(\.localPose))
        XCTAssertEqual(replaced.observations.map(\.localEpoch), original.observations.map(\.localEpoch))
        XCTAssertEqual(replaced.observations.map(\.sourceObservationID), original.observations.map(\.sourceObservationID))
        XCTAssertEqual(replaced.keyframes, original.keyframes)
        XCTAssertEqual(replaced.landmarks.map(\.descriptor), original.landmarks.map(\.descriptor))
        XCTAssertEqual(replaced.landmarks[2].worldPoint, CGPoint(x: 40, y: 20))
        XCTAssertEqual(replaced.landmarks[3].worldPoint, CGPoint(x: 44.5, y: 18))
        XCTAssertEqual(replaced.relativeMotionEdges, original.relativeMotionEdges)
        XCTAssertEqual(replaced.loopClosureEdges, original.loopClosureEdges)
        XCTAssertEqual(replaced.observations.map(\.optimizedPose), [CGPoint.zero, CGPoint(x: 5, y: -2)])
    }

    func testLegacyObservationDecodesIntoEpochZeroWithoutMovingItsWorldPose() throws {
        let current = try fixture()
        var object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(current)) as? [String: Any]
        )
        var observations = try XCTUnwrap(object["observations"] as? [[String: Any]])
        for index in observations.indices {
            observations[index].removeValue(forKey: "localPose")
            observations[index].removeValue(forKey: "captureGeneration")
            observations[index].removeValue(forKey: "localEpoch")
            observations[index].removeValue(forKey: "basisRevision")
        }
        object["observations"] = observations

        let migrated = try JSONDecoder().decode(
            LiveWorldSnapshot.self,
            from: JSONSerialization.data(withJSONObject: object)
        )

        XCTAssertEqual(migrated.observations.map(\.localPose), current.observations.map(\.rawPose))
        XCTAssertEqual(migrated.observations.map(\.captureGeneration), [0, 0])
        XCTAssertEqual(migrated.observations.map(\.localEpoch), [0, 0])
        XCTAssertEqual(migrated.observations.map(\.basisRevision), [0, 0])
    }

    func testInvalidModelsAreRejected() throws {
        let observation = makeObservation(id: 10, sourceID: 100)
        XCTAssertThrowsError(try LiveWorldSnapshot(schemaVersion: 99)) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .unsupportedSchema(99))
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(observations: [observation])) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .unanchoredWorld)
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(anchoredObservationID: 10, observations: [observation, observation])) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .duplicateObservationID(10))
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(
            anchoredObservationID: 10, observations: [observation],
            keyframes: [LiveWorldKeyframe(id: 1, observationID: 99, sourceObservationID: 100)]
        )) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .danglingObservationID(99))
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(
            anchoredObservationID: 10, observations: [observation],
            keyframes: [LiveWorldKeyframe(id: 1, observationID: 10, sourceObservationID: 999)]
        )) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .invalidGeometry)
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(
            anchoredObservationID: 10, observations: [observation],
            relativeMotionEdges: [LiveWorldRelativeMotionEdge(id: 1, fromObservationID: 10, toObservationID: 99, deltaX: 1, deltaY: 0, support: 1)]
        )) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .danglingObservationID(99))
        }
        XCTAssertThrowsError(try LiveWorldSnapshot(
            anchoredObservationID: 10,
            observations: [LiveWorldObservation(id: 10, sourceObservationID: 100, timestamp: .nan, sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 240, excludedRects: [], rawPose: .zero, optimizedPose: .zero)]
        )) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .invalidGeometry)
        }
    }

    func testStaleOrIncompleteReplacementIsRejected() throws {
        let world = try fixture()
        XCTAssertThrowsError(try world.replacingOptimizedPoses([10: .zero, 11: .zero], baseRevision: 6)) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .staleRevision(expected: 7, actual: 6))
        }
        XCTAssertThrowsError(try world.replacingOptimizedPoses([10: .zero], baseRevision: 7)) { error in
            XCTAssertEqual(error as? LiveWorldModelError, .invalidRevision)
        }
    }

    func testRoomTranslationMovesOnlySelectedRoomOptimizedEvidence() throws {
        let roomOne = LiveWorldObservation(
            id: 1, sourceObservationID: 1, timestamp: 1,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640,
            sampleWidth: 240, excludedRects: [], roomID: 0,
            rawPose: CGPoint(x: -180, y: 0),
            optimizedPose: CGPoint(x: -180, y: 0)
        )
        let roomTwo = LiveWorldObservation(
            id: 2, sourceObservationID: 2, timestamp: 2,
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640,
            sampleWidth: 240, excludedRects: [], roomID: 1,
            rawPose: CGPoint(x: -820, y: 0),
            optimizedPose: CGPoint(x: -820, y: 0)
        )
        let world = try LiveWorldSnapshot(
            anchoredObservationID: 1,
            observations: [roomOne, roomTwo]
        )

        let translated = try world.translatingRoom(
            1, by: CGVector(dx: 320, dy: 5)
        )
        XCTAssertEqual(translated.mapRevision, 1)
        XCTAssertEqual(translated.observations[0], roomOne)
        XCTAssertEqual(
            translated.observations[1].rawPose,
            roomTwo.rawPose
        )
        XCTAssertEqual(
            translated.observations[1].optimizedPose,
            CGPoint(x: -500, y: 5)
        )
        XCTAssertEqual(translated.observations[1].localPose, roomTwo.localPose)
    }

    private func fixture() throws -> LiveWorldSnapshot {
        try LiveWorldSnapshot(
            mapRevision: 7,
            anchoredObservationID: 10,
            observations: [
                makeObservation(id: 10, sourceID: 100),
                makeObservation(id: 11, sourceID: 101, raw: CGPoint(x: 4, y: 0), optimized: CGPoint(x: 4.5, y: 0))
            ],
            keyframes: [
                LiveWorldKeyframe(id: 20, observationID: 10, sourceObservationID: 100),
                LiveWorldKeyframe(id: 21, observationID: 11, sourceObservationID: 101)
            ],
            landmarks: [
                LiveWorldLandmark(id: 30, keyframeID: 20, sourceObservationID: 100, samplePoint: CGPoint(x: 20, y: 30), worldPoint: CGPoint(x: 20, y: 30), descriptor: [0.1, 0.2], depth: 1, depthConfidence: 0.8),
                LiveWorldLandmark(id: 31, keyframeID: 20, sourceObservationID: 100, samplePoint: CGPoint(x: 25, y: 30), worldPoint: CGPoint(x: 25, y: 30), descriptor: [0.3, 0.4], depth: 1, depthConfidence: 0.8),
                LiveWorldLandmark(id: 32, keyframeID: 20, sourceObservationID: 100, samplePoint: CGPoint(x: 40, y: 20), worldPoint: CGPoint(x: 40, y: 20), descriptor: [0.5, 0.6], depth: 1, depthConfidence: 0.8),
                LiveWorldLandmark(id: 33, keyframeID: 21, sourceObservationID: 101, samplePoint: CGPoint(x: 40, y: 20), worldPoint: CGPoint(x: 44, y: 20), descriptor: [0.7, 0.8], depth: 1, depthConfidence: 0.8)
            ],
            relativeMotionEdges: [LiveWorldRelativeMotionEdge(id: 40, fromObservationID: 10, toObservationID: 11, deltaX: 4, deltaY: 0, support: 8)],
            loopClosureEdges: [LiveWorldLoopClosureEdge(id: 50, fromKeyframeID: 20, toKeyframeID: 21, landmarkIDs: [30, 31, 32], deltaX: 4, deltaY: 0, support: 3, ambiguity: 0.1)]
        )
    }

    private func makeObservation(
        id: Int,
        sourceID: Int,
        raw: CGPoint = .zero,
        optimized: CGPoint = .zero
    ) -> LiveWorldObservation {
        LiveWorldObservation(
            id: id, sourceObservationID: sourceID, timestamp: Double(id),
            sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 240,
            excludedRects: [CGRect(x: 1, y: 2, width: 3, height: 4)], rawPose: raw, optimizedPose: optimized
        )
    }
}
