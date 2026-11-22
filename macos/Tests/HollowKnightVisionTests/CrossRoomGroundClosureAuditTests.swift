import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Measures possible cross-room closures without changing production ownership.
/// Reference camera poses and game telemetry are deliberately unavailable.
final class CrossRoomGroundClosureAuditTests: XCTestCase {
    func testEarlierRoomGroundClosuresWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_CROSS_ROOM_AUDIT_INPUT"],
              let worldPath = env["HKV_CROSS_ROOM_AUDIT_WORLD"],
              let output = env["HKV_CROSS_ROOM_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set cross-room input, immutable world snapshot and output")
        }
        struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
        struct Reference { let observation: LiveWorldObservation; let feature: GlobalFeatureReference }
        struct Match: Codable {
            let referenceRoom: Int; let referenceObservation: Int; let referenceTimestamp: Double
            let appearanceConfidence: Double; let appearanceInliers: Int
            let floorPatches: Int; let floorCorrelation: Double; let floorSpan: Double
            let measuredDisplacement: CGPoint
        }
        struct Query: Codable {
            let room: Int; let observation: Int; let timestamp: Double
            let sourceFrame: Int; let searchedReferences: Int; let candidates: [Match]
        }
        let url = URL(fileURLWithPath: input)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: url))
        let reader = try VisualReplayArchiveReader(rootURL: url.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id, requireComplete: false)
        let world = try JSONDecoder().decode(LiveWorldSnapshot.self,
            from: Data(contentsOf: URL(fileURLWithPath: worldPath)))
        let frames = Dictionary(uniqueKeysWithValues: reader.manifest.frames.filter {
            $0.fileName != nil
        }.map { ($0.captureTimestamp, $0) })
        let observations = world.observations.filter {
            $0.groundGeometry?.lines.isEmpty == false && $0.captureTimestamp.flatMap { frames[$0] } != nil
        }.sorted { $0.captureTimestamp! < $1.captureTimestamp! }
        let grouped = Dictionary(grouping: observations, by: \.roomID)
        let rooms = grouped.keys.sorted { grouped[$0]!.first!.captureTimestamp! < grouped[$1]!.first!.captureTimestamp! }
        let matcher = GlobalFeatureMatcher()
        var references = [Int: [Reference]](), results = [Query]()
        var imageCache = [Int: CGImage](), imageOrder = [Int]()
        func image(_ observation: LiveWorldObservation) throws -> CGImage {
            if let saved = imageCache[observation.id] { return saved }
            let frame = try XCTUnwrap(observation.captureTimestamp.flatMap { frames[$0] })
            let image = try reader.image(for: frame)
            imageCache[observation.id] = image; imageOrder.append(observation.id)
            while imageOrder.count > 64 { imageCache.removeValue(forKey: imageOrder.removeFirst()) }
            return image
        }
        for room in rooms {
            let visit = grouped[room]!
            let start = visit.first!.captureTimestamp!
            var queried = Set<Int>()
            for delay in [0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0] {
                guard let current = visit.min(by: {
                    abs($0.captureTimestamp!-start-delay) < abs($1.captureTimestamp!-start-delay)
                }), abs(current.captureTimestamp!-start-delay) <= 0.25,
                      queried.insert(current.id).inserted else { continue }
                let result: Query = try autoreleasepool {
                    let currentImage = try image(current)
                    var candidates = [Match](), searched = 0
                    for earlierRoom in rooms where earlierRoom != room {
                        for prior in references[earlierRoom] ?? [] {
                            guard prior.observation.captureTimestamp! < start else { continue }
                            searched += 1
                            guard let proposal = matcher.match(frame: currentImage, references: [prior.feature],
                                currentSolveWidth: current.solveWidth, excluding: current.excludedRects),
                                  let floor = GroundPlaneClosureVerifier.verify(reference: try image(prior.observation),
                                    referenceGround: prior.observation.groundGeometry!, referencePose: .zero,
                                    current: currentImage, currentGround: current.groundGeometry!,
                                    proposedPose: proposal.proposedCameraPose, solveWidth: current.solveWidth) else { continue }
                            candidates.append(Match(referenceRoom: earlierRoom,
                                referenceObservation: prior.observation.id,
                                referenceTimestamp: prior.observation.captureTimestamp!,
                                appearanceConfidence: proposal.confidence,
                                appearanceInliers: proposal.inlierReferenceFeatureIDs.count,
                                floorPatches: floor.supportedPatches, floorCorrelation: floor.meanCorrelation,
                                floorSpan: floor.horizontalSpan, measuredDisplacement: floor.cameraPose))
                        }
                    }
                    candidates.sort {
                        if $0.appearanceConfidence != $1.appearanceConfidence {
                            return $0.appearanceConfidence > $1.appearanceConfidence
                        }
                        return $0.referenceObservation < $1.referenceObservation
                    }
                    return Query(room: room, observation: current.id, timestamp: current.captureTimestamp!,
                        sourceFrame: frames[current.captureTimestamp!]!.index,
                        searchedReferences: searched, candidates: Array(candidates.prefix(12)))
                }
                results.append(result)
            }
            // Only after this visit has been queried may it enter the bank.
            let selected = Set((0..<min(32, visit.count)).map {
                $0 * max(0, visit.count-1) / max(1, min(32, visit.count)-1)
            }).sorted()
            for index in selected {
                let observation = visit[index]
                try autoreleasepool {
                    if let feature = matcher.makeReference(frame: try image(observation), cameraPose: .zero,
                        solveWidth: observation.solveWidth, excluding: observation.excludedRects) {
                        references[room, default: []].append(.init(observation: observation, feature: feature))
                    }
                }
            }
            print("Cross-room floor audit room \(room): \(results.filter { $0.room == room && !$0.candidates.isEmpty }.count) supported queries")
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(results).write(to: URL(fileURLWithPath: output), options: .atomic)
    }
}
