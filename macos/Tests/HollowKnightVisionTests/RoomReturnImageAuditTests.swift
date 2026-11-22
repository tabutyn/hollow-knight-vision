import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Pixel-only measurement of possible room revisits. Does not change live IDs.
final class RoomReturnImageAuditTests: XCTestCase {
    func testRecordedRoomReturnImagesWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_ROOM_VIEW_AUDIT_INPUT"],
              let cutsPath = env["HKV_ROOM_VIEW_AUDIT_CUTS"],
              let output = env["HKV_ROOM_VIEW_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set room-view audit input, native cuts and output")
        }
        struct Frame: Decodable {
            let captureTimestamp: Double
            let width: Int
            let height: Int
            let luma: Data
            var grid: LowResolutionMotionGrid {
                .init(width: width, height: height, luma: Array(luma))
            }
        }
        struct Source: Decodable { let lowResolutionFrames: [Frame] }
        struct Cut: Decodable {
            let departureSeconds: Double
            let arrivalSeconds: Double
            let direction: Int
        }
        struct View { let frame: Frame; let role: String }
        struct Match: Codable {
            let room: Int
            let similarity: Double
            let referenceSeconds: Double
            let role: String
        }
        struct Decision: Codable {
            let arrivalSeconds: Double
            let departureSeconds: Double
            let sourceRoom: Int
            let chosenRoom: Int
            let reusedRoom: Bool
            let margin: Double?
            let ranked: [Match]
            let decisionSeconds: Double
        }
        let frames = try JSONDecoder().decode(Source.self,
            from: Data(contentsOf: URL(fileURLWithPath: input))).lowResolutionFrames
        let cuts = try JSONDecoder().decode([Cut].self,
            from: Data(contentsOf: URL(fileURLWithPath: cutsPath)))
        let start = try XCTUnwrap(frames.first?.captureTimestamp)
        func frame(at seconds: Double, exact: Bool = true) throws -> Frame {
            let result = try XCTUnwrap(frames.min {
                abs($0.captureTimestamp-start-seconds) < abs($1.captureTimestamp-start-seconds)
            })
            if exact { XCTAssertLessThan(abs(result.captureTimestamp-start-seconds), 0.000_001) }
            return result
        }
        func similarity(_ first: Frame, _ second: Frame) -> Double? {
            var probe = VisualSceneDiscontinuityTracker()
            _ = probe.observe(grid: first.grid, timestamp: 0, direction: .right, worldPose: .zero)
            return probe.diagnostic(for: second.grid).similarity
        }
        var bank = [0: [View(frame: try XCTUnwrap(frames.first), role: "start")]]
        var currentRoom = 0, nextRoom = 1
        var enteredAt = 0.0
        var decisions = [Decision]()
        let stableViews = env["HKV_ROOM_VIEW_AUDIT_STABLE_VIEWS"] == "1"
        for cut in cuts {
            let departure = try frame(at: cut.departureSeconds)
            let arrival = try frame(at: cut.arrivalSeconds)
            if stableViews {
                var lastTime = -Double.infinity
                for candidate in frames where candidate.captureTimestamp-start >= enteredAt
                    && candidate.captureTimestamp-start <= cut.departureSeconds {
                    guard candidate.captureTimestamp-lastTime >= 0.28,
                          VisualSceneDiscontinuityTracker().diagnostic(for: candidate.grid).recoveredImage else { continue }
                    bank[currentRoom, default: []].append(View(frame: candidate, role: "stable visit view"))
                    lastTime = candidate.captureTimestamp
                    if bank[currentRoom, default: []].count > 32 { bank[currentRoom]?.remove(at: 1) }
                }
            }
            bank[currentRoom, default: []].append(View(frame: departure, role: "departure"))
            let queries = stableViews ? [try frame(at: cut.arrivalSeconds + 0.5, exact: false),
                                         try frame(at: cut.arrivalSeconds + 1, exact: false)] : [arrival]
            let ranked = bank.keys.sorted().filter { $0 != currentRoom }.compactMap { room -> Match? in
                let matches = queries.compactMap { query in
                    bank[room]?.compactMap { view -> Match? in
                        guard let score = similarity(view.frame, query) else { return nil }
                        return Match(room: room, similarity: score,
                            referenceSeconds: view.frame.captureTimestamp-start, role: view.role)
                    }.max { $0.similarity < $1.similarity }
                }
                guard matches.count == queries.count else { return nil }
                // Both separated arrival images must agree with this room.
                return matches.min { $0.similarity < $1.similarity }
            }.sorted { $0.similarity == $1.similarity ? $0.room < $1.room : $0.similarity > $1.similarity }
            let margin = ranked.count >= 2 ? ranked[0].similarity-ranked[1].similarity : nil
            let reuse = ranked.first.map { $0.similarity >= 0.9 && (margin ?? 1) >= 0.1 } ?? false
            let chosen = reuse ? ranked[0].room : nextRoom
            if !reuse { nextRoom += 1 }
            decisions.append(Decision(arrivalSeconds: cut.arrivalSeconds,
                departureSeconds: cut.departureSeconds, sourceRoom: currentRoom,
                chosenRoom: chosen, reusedRoom: reuse, margin: margin, ranked: Array(ranked.prefix(5)),
                decisionSeconds: queries.map { $0.captureTimestamp-start }.max() ?? cut.arrivalSeconds))
            currentRoom = chosen
            enteredAt = cut.arrivalSeconds
            bank[currentRoom, default: []].append(View(frame: arrival, role: "arrival"))
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(decisions).write(to: URL(fileURLWithPath: output), options: .atomic)
        print("Room image candidates: \(decisions.filter(\.reusedRoom).count) possible revisits; diagnostic only")
    }
}
