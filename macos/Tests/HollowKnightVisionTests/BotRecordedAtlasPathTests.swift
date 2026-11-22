import XCTest
import CryptoKit
@testable import HollowKnightVision

final class BotRecordedAtlasPathTests: XCTestCase {
    private func frame(_ time: Double, x: Double, domain: BotDomain = .gameplay, hasPose: Bool = true,
                       stale: Bool = false) -> BotObservation {
        var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
        features[0] = x / 640
        features[1] = 96.0 / 640
        features[2] = 18.0 / 640
        features[3] = 32.0 / 640
        features[4] = 0.9
        features[6] = 1
        var row = BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
            availableAt: time, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: stale ? time-1 : time,
            detectionFrameID: UInt64(time * 1000), modelVersion: "v1", bestKnightConfidence: 0.9,
            features: features, issues: [])
        let geometry = BotScreenTerrain(width: 640, height: 360,
            floors: [.init(minimumX: 0, maximumX: 640, y: 80, confidence: 1)], walls: [])
        if domain == .gameplay {
            row.terrainSource = "detected"
            row.detectedTerrain = geometry
            row.knightSource = "visual"
            if hasPose { row.blockadePose = .init(space: "visual/1/1/1", x: 0, y: 0, width: 640) }
        } else {
            row.terrainSource = "human-authored"
            row.authoredTerrain = geometry
            row.knightSource = "hacker-frame"
            if hasPose { row.blockadePose = .init(space: "hacker/room", x: 0, y: 0, width: 32) }
            row.features[1] = 112.0 / 640
        }
        return row
    }

    func testReplaySidecarRequiresMatchingClipChecksumAndFrameIdentity() throws {
        let rows = [frame(1, x: 150), frame(1.1, x: 160)]
        let id = UUID(), replay = UUID(), events = Data("recorded events".utf8)
        let hash = SHA256.hash(data: events).map { String(format: "%02x", $0) }.joined()
        let hint: [String: Any] = ["dx": 1, "dy": 0, "clearance": 0.1, "distance": 0.3, "frontier": false]
        let document: [String: Any] = ["schemaVersion": 1, "id": id.uuidString, "replayID": replay.uuidString,
            "eventsSHA256": hash, "rows": [
                ["index": 0, "generation": 1, "captureTimestamp": 1.0, "hackerPathVersion": 1, "roadHint": hint],
                ["index": 1, "generation": 1, "captureTimestamp": 1.2, "hackerPathVersion": 1, "roadHint": hint]
            ]]
        let data = try JSONSerialization.data(withJSONObject: document)
        let found = try BotClipPathSidecar.hints(sidecar: data, events: events, id: id, replayID: replay, observations: rows)
        XCTAssertEqual(found.keys.sorted(), [0])
        XCTAssertEqual(found[0]?.dx, 1)
        XCTAssertTrue(try BotClipPathSidecar.hints(sidecar: data, events: Data("changed".utf8), id: id,
            replayID: replay, observations: rows).isEmpty)
        XCTAssertTrue(try BotClipPathSidecar.hints(sidecar: data, events: events, id: id,
            replayID: UUID(), observations: rows).isEmpty)
    }

    func testUnregisteredChartsCannotAddTheSameExplorationCredit() {
        let map = AtlasPathMap()
        for chart in ["a","b"] { map.setBase(room:chart,floors:[],fallbackHeight:20) }
        var row = frame(1,x:100)
        func credit(_ amount: Double, _ chart: String) -> Double? {
            row.pathProgress = amount
            map.limitUnplacedCredit(room:"room",chart:chart,row:&row)
            return row.pathProgress
        }
        XCTAssertEqual(credit(1.5,"a"),1.5)
        XCTAssertEqual(credit(1.0,"b"),0)
        XCTAssertEqual(credit(1.0,"b"),0.5)
        XCTAssertEqual(credit(0.25,"a"),0)
    }

    func testRecoveryIsCausalAndUsesRecordedTerrain() throws {
        let input = [frame(1, x: 150), frame(1.1, x: 160), frame(1.2, x: 170)]
        let full = BotRecordedAtlasPath.recover(input, domain: .gameplay)
        let prefix = BotRecordedAtlasPath.recover(Array(input.prefix(2)), domain: .gameplay)
        XCTAssertEqual(full.map(\.index), [0, 1, 2])
        XCTAssertNotNil(full.first?.roadHint)
        XCTAssertEqual(full.prefix(2).map(\.roadHint), prefix.map(\.roadHint))
        XCTAssertEqual(full.prefix(2).map(\.pathProgress), prefix.map(\.pathProgress))
        XCTAssertTrue(full.allSatisfy { ($0.pathProgress ?? 0) >= 0 })
    }

    func testMissingPoseBreaksProgressAndCurrentPathIsPreserved() {
        var input = [frame(1, x: 150), frame(1.1, x: 160, hasPose: false), frame(1.2, x: 170)]
        input[0].atlasPathVersion = 1
        let result = BotRecordedAtlasPath.recover(input, domain: .gameplay)
        XCTAssertEqual(result.map(\.index), [2])
        XCTAssertEqual(result[0].pathProgress, 0)
    }

    func testUnreliableTerrainAndStaleDetectionDoNotCarryIntoLaterFrames() {
        var invalid = frame(1.1, x: 160)
        invalid.features[6] = 0
        let frames = [frame(1, x: 150), invalid, frame(1.2, x: 170), frame(1.3, x: 180, stale: true),
                      frame(1.4, x: 190)]
        let result = BotRecordedAtlasPath.recover(frames, domain: .gameplay)
        XCTAssertEqual(result.map(\.index), [0, 2, 4])
        XCTAssertEqual(result[1].pathProgress, 0)
        XCTAssertEqual(result[2].pathProgress, 0)
    }

    func testHackerUsesTheRecordedGroundAndWorldPose() {
        let input = [frame(1, x: 150, domain: .hacker), frame(1.1, x: 160, domain: .hacker)]
        let result = BotRecordedAtlasPath.recover(input, domain: .hacker)
        XCTAssertEqual(result.map(\.index), [0, 1])
        XCTAssertNotNil(result.first?.roadHint)
    }
}
