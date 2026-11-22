import CoreGraphics
import CoreImage
import CryptoKit
import Foundation
import XCTest
@testable import HollowKnightVision

/// Opt-in, pixel-only replay of the production global matcher and island solve.
/// This isolates closure quality before live ground/camera publication is enabled.
/// It reads an isolated visual replay world; reference telemetry is never input.
final class LiveIslandReplayAuditTests: XCTestCase {
    func testRecordedOwnedWorldReopensWithIdenticalPixels() throws {
        guard let inputPath = ProcessInfo.processInfo.environment["HKV_WORLD_REOPEN_AUDIT_INPUT"] else {
            throw XCTSkip("Set HKV_WORLD_REOPEN_AUDIT_INPUT to verify a saved replay atlas")
        }
        let root = URL(fileURLWithPath: inputPath)
        let worldRoot = root.appendingPathComponent("world")
        let expected = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: Data(contentsOf: root.appendingPathComponent("world-snapshot.json")))
        let diskURL = worldRoot.appendingPathComponent("scene.snapshot")
        let diskBefore = try Data(contentsOf: diskURL)
        let manifestURL = worldRoot.appendingPathComponent("manifest.json")
        let manifestBefore = try Data(contentsOf: manifestURL)
        let store = try LiveWorldSessionStore(rootURL: worldRoot)
        let observation = try XCTUnwrap(expected.observations.last)
        let frame = try store.source(for: observation.sourceObservationID)
        var priorPixels: [String]?
        for _ in 0..<2 {
            let pipeline = LiveAtlasPipeline(context: CIContext(options: [.cacheIntermediates: false]),
                worldRootURL: worldRoot)
            pipeline.drainVisualReplay()
            XCTAssertNil(pipeline.worldPersistenceIssue)
            let restored = pipeline.currentWorldSnapshot()
            XCTAssertEqual(restored, expected)
            XCTAssertEqual(restored.ownedGroundLines, expected.ownedGroundLines)
            let output = try XCTUnwrap(pipeline.process(frame: frame,
                regions: SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero),
                cameraPosition: observation.optimizedPose, solveWidth: observation.solveWidth,
                timestamp: observation.timestamp + 1, hasGameplaySignal: true, allowAtlasWrite: false))
            XCTAssertFalse(output.atlasTiles.isEmpty)
            XCTAssertEqual(output.worldRevision, expected.mapRevision)
            let pixels = try output.atlasTiles.sorted { $0.id < $1.id }.map { tile -> String in
                let data = try XCTUnwrap(tile.image.dataProvider?.data) as Data
                let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
                return "\(tile.id):\(tile.bounds):\(digest)"
            }
            if let priorPixels { XCTAssertEqual(pixels, priorPixels) }
            priorPixels = pixels
        }
        XCTAssertEqual(try Data(contentsOf: diskURL), diskBefore)
        XCTAssertEqual(try Data(contentsOf: manifestURL), manifestBefore)
        print("Atlas reopen verified: \(expected.observations.count) screenshots, \(expected.ownedGroundLines.count) ground spans")
    }

    func testRecordedWorldIslandClosures() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let inputPath = environment["HKV_ISLAND_AUDIT_INPUT"],
              let outputPath = environment["HKV_ISLAND_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set HKV_ISLAND_AUDIT_INPUT/OUTPUT to run the saved visual-world audit.")
        }
        let input = URL(fileURLWithPath: inputPath, isDirectory: true)
        let output = URL(fileURLWithPath: outputPath, isDirectory: true)
        XCTAssertFalse(FileManager.default.fileExists(atPath: output.path))
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let store = try LiveWorldSessionStore(rootURL: input.appendingPathComponent("world"))
        let saved = try store.load().snapshot
        let owners = Dictionary(uniqueKeysWithValues: saved.trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, (island, $0)) }
        })
        let broadFeatures = environment["HKV_ISLAND_AUDIT_BROAD_FEATURES"] == "1"
        let verifyGround = environment["HKV_ISLAND_AUDIT_VERIFY_GROUND"] == "1"
        var verified = [[String: Any]]()
        var verificationAttempts = 0
        let tracker = LiveWorldTracker()
        let started = ProcessInfo.processInfo.systemUptime
        var counts = [String: Int]()
        var closures = [[String: Any]]()
        var lastSearch: Double?
        for (index, observation) in saved.observations.enumerated() {
            try autoreleasepool {
                let (island, member) = try XCTUnwrap(owners[observation.id])
                let image = try store.source(for: observation.sourceObservationID)
                let capture = LiveIslandCapture(id: island.id, roomID: island.roomID,
                    startedAt: island.startedAt, worldFromLocal: island.worldFromLocal,
                    localPose: member.localPose, placement: island.placement,
                    uncertainty: island.uncertainty, breakReason: island.breakReason,
                    measured: member.measured)
                // Match at the same bounded 2 Hz cadence as the live global worker.
                let search = lastSearch.map { observation.timestamp - $0 >= 0.5 } ?? true
                let update = try tracker.ingest(observationID: observation.id,
                    sourceObservationID: observation.sourceObservationID,
                    timestamp: observation.timestamp, frame: image,
                    proposedCameraPose: observation.rawPose, solveWidth: observation.solveWidth,
                    exclusions: broadFeatures ? [] : observation.excludedRects, searchForClosure: search,
                    localPose: observation.localPose, captureGeneration: observation.captureGeneration,
                    localEpoch: observation.localEpoch, basisRevision: observation.basisRevision,
                    roomID: observation.roomID, islandCapture: capture,
                    groundGeometry: observation.groundGeometry,
                    verifyClosure: verifyGround ? { source, proposal in
                        verificationAttempts += 1
                        guard source.solveWidth == observation.solveWidth,
                              let sourceGround = source.groundGeometry,
                              let currentGround = observation.groundGeometry,
                              let sourceImage = try? store.source(for: source.sourceObservationID),
                              let result = GroundPlaneClosureVerifier.verify(reference: sourceImage,
                                  referenceGround: sourceGround, referencePose: source.optimizedPose,
                                  current: image, currentGround: currentGround,
                                  proposedPose: proposal, solveWidth: observation.solveWidth) else { return nil }
                        verified.append(["referenceID": source.id, "observationID": observation.id,
                            "proposal": [proposal.x, proposal.y],
                            "verifiedPose": [result.cameraPose.x, result.cameraPose.y],
                            "patches": result.supportedPatches, "span": result.horizontalSpan,
                            "correlation": result.meanCorrelation])
                        return result.cameraPose
                    } : nil)
                if search { lastSearch = observation.timestamp }
                for rejection in update.review.rejections { counts[String(describing: rejection), default: 0] += 1 }
                if let closure = update.acceptedClosure {
                    closures.append(["id": closure.id, "observationID": observation.id,
                        "islandID": island.id, "timestamp": observation.timestamp,
                        "correction": [update.correction.x, update.correction.y],
                        "support": closure.support, "residualPixels": closure.residualPixels ?? -1])
                }
            }
            if (index + 1).isMultiple(of: 100) {
                print("island closure audit: \(index + 1)/\(saved.observations.count)")
            }
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(tracker.snapshot)
        try data.write(to: output.appendingPathComponent("world-snapshot.json"), options: .atomic)
        let report: [String: Any] = ["schemaVersion": 1, "sourceWorld": input.path,
            "observations": saved.observations.count, "broadFeatures": broadFeatures,
            "elapsedSeconds": ProcessInfo.processInfo.systemUptime - started,
            "rejections": counts, "closures": closures,
            "groundVerificationRequired": verifyGround, "verificationAttempts": verificationAttempts,
            "groundVerifications": verified,
            "worldDigest": SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined(),
            "notes": ["Production global matcher and rigid-island correction; immutable masked source images.",
                      "This audits closure geometry only; live ground/camera corrections remain gated."]]
        try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys, .prettyPrinted])
            .write(to: output.appendingPathComponent("island-audit.json"), options: .atomic)
        XCTAssertEqual(tracker.snapshot.observations.count, saved.observations.count)
        XCTAssertEqual(tracker.snapshot.observations.map(\.rawPose), saved.observations.map(\.rawPose))
        XCTAssertEqual(try JSONDecoder().decode(LiveWorldSnapshot.self, from: data), tracker.snapshot)
    }
}
