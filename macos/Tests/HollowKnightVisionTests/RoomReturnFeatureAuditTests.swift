import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Diagnostic only: build appearance banks from already-seen archived pixels.
/// No game identity, reference pose or production ownership decision is read.
final class RoomReturnFeatureAuditTests: XCTestCase {
    func testRecordedRoomReturnFeaturesWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_ROOM_FEATURE_AUDIT_INPUT"],
              let cutsPath = env["HKV_ROOM_FEATURE_AUDIT_CUTS"],
              let output = env["HKV_ROOM_FEATURE_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set room-feature audit input, native cuts and output")
        }
        struct Clock: Decodable { let captureTimestamp: Double }
        struct Source: Decodable {
            let id: UUID
            let visualFrameArchive: RecordedVisualFrameArchive
            let lowResolutionFrames: [Clock]
        }
        struct Cut: Decodable { let departureSeconds: Double; let arrivalSeconds: Double }
        struct View { let reference: GlobalFeatureReference; let seconds: Double }
        struct Match: Codable {
            let room: Int; let referenceSeconds: Double
            let confidence: Double; let inliers: Int; let residual: Double
            let ambiguity: Double; let translation: CGPoint
        }
        struct Query: Codable { let seconds: Double; let ranked: [Match] }
        struct Decision: Codable {
            let arrivalSeconds: Double; let decisionSeconds: Double
            let sourceRoom: Int; let queries: [Query]
        }
        let url = URL(fileURLWithPath: input)
        let source = try JSONDecoder().decode(Source.self, from: Data(contentsOf: url))
        let start = try XCTUnwrap(source.lowResolutionFrames.first?.captureTimestamp)
        let cuts = try JSONDecoder().decode([Cut].self,
            from: Data(contentsOf: URL(fileURLWithPath: cutsPath)))
        let reader = try VisualReplayArchiveReader(rootURL: url.deletingLastPathComponent(),
            summary: source.visualFrameArchive, expectedPathID: source.id, requireComplete: false)
        let frames = reader.manifest.frames.filter { $0.fileName != nil }
        let inputs = Dictionary(reader.manifest.groundInputs.map { ($0.captureTimestamp, $0) },
            uniquingKeysWith: { _, latest in latest })
        let matcher = GlobalFeatureMatcher()
        var bank = [Int: [View]](), decisions = [Decision]()
        var enteredAt = 0.0
        func masks(_ frame: VisualReplayFrame) -> [CGRect] {
            // Always shield the captured header, even if the archived detector
            // did not run on this exact image. Actor masks remain image-local.
            [CGRect(x: 0, y: Double(frame.height) * 0.76,
                    width: Double(frame.width), height: Double(frame.height) * 0.24)]
                + (inputs[frame.captureTimestamp]?.excludingRects ?? [])
        }
        for (room, cut) in cuts.enumerated() {
            let visit = frames.filter { $0.captureTimestamp-start >= enteredAt + 0.5
                && $0.captureTimestamp-start <= cut.departureSeconds }
            // Compact the already-seen visit to a bounded, uniform view bank.
            // Query images are never included before this arrival is decided.
            if !visit.isEmpty {
                let selected = Set((0..<min(32, visit.count)).map {
                    $0 * max(0, visit.count-1) / max(1, min(32, visit.count)-1)
                }).sorted()
                for index in selected {
                    let frame = visit[index]
                    try autoreleasepool {
                        let image = try reader.image(for: frame)
                        if let reference = matcher.makeReference(frame: image, cameraPose: .zero,
                            solveWidth: frame.solveWidth, excluding: masks(frame)) {
                            bank[room, default: []].append(.init(reference: reference,
                                seconds: frame.captureTimestamp-start))
                        }
                    }
                }
            }
            var queries = [Query]()
            for delay in [0.5, 1.0] {
                let requested = start + cut.arrivalSeconds + delay
                let frame = try XCTUnwrap(frames.min {
                    abs($0.captureTimestamp-requested) < abs($1.captureTimestamp-requested)
                })
                let ranked: [Match] = try autoreleasepool {
                    let image = try reader.image(for: frame)
                    return bank.keys.sorted().filter { $0 != room }.compactMap { candidateRoom in
                        bank[candidateRoom]?.compactMap { view -> Match? in
                            guard let match = matcher.match(frame: image, references: [view.reference],
                                currentSolveWidth: frame.solveWidth, excluding: masks(frame)) else { return nil }
                            return Match(room: candidateRoom, referenceSeconds: view.seconds,
                                confidence: match.confidence, inliers: match.inlierReferenceFeatureIDs.count,
                                residual: match.residual, ambiguity: match.ambiguityRatio,
                                translation: match.measuredTranslation)
                        }.max { $0.confidence < $1.confidence }
                    }.sorted { $0.confidence == $1.confidence ? $0.room < $1.room : $0.confidence > $1.confidence }
                }
                queries.append(.init(seconds: frame.captureTimestamp-start, ranked: Array(ranked.prefix(5))))
            }
            decisions.append(.init(arrivalSeconds: cut.arrivalSeconds,
                decisionSeconds: queries.map(\.seconds).max() ?? cut.arrivalSeconds,
                sourceRoom: room, queries: queries))
            enteredAt = cut.arrivalSeconds
            print("Room feature audit arrival \(room+1)/\(cuts.count): \(queries.map { $0.ranked.first?.room ?? -1 })")
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(decisions).write(to: URL(fileURLWithPath: output), options: .atomic)
    }
}
