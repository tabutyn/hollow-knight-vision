import Foundation
import XCTest
@testable import HollowKnightVision

final class LoadingGapReplayAuditTests: XCTestCase {
    /// Measurement-only audit of immutable image grids and recorded directions.
    /// No reference camera pose or game scene identity is decoded by the tracker.
    func testArchivedLoadingGapsWhenRequested() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_LOADING_GAP_AUDIT_INPUT"],
              let output = env["HKV_LOADING_GAP_AUDIT_OUTPUT"],
              let expected = env["HKV_LOADING_GAP_AUDIT_WINDOWS"] else {
            throw XCTSkip("Set loading-gap audit input, output, and expected arrival windows")
        }
        struct Frame: Decodable {
            let captureTimestamp: Double
            let width: Int
            let height: Int
            let luma: Data
        }
        struct Direction: Decodable { let horizontalInput: Int? }
        struct Input: Decodable { let captureTimestamp: Double; let input: Direction? }
        struct Archive: Decodable {
            let lowResolutionFrames: [Frame]
            let coarseMotionSamples: [Input]
        }
        struct Cut: Codable {
            let departureSeconds: Double
            let arrivalSeconds: Double
            let direction: Int
        }
        struct Trace: Codable {
            let seconds: Double
            let direction: Int?
            let before: VisualSceneDiscontinuityTracker.Diagnostic
            let after: VisualSceneDiscontinuityTracker.Diagnostic
        }
        let archive = try JSONDecoder().decode(Archive.self,
            from: Data(contentsOf: URL(fileURLWithPath: input)))
        let windows = try JSONDecoder().decode([[Double]].self, from: Data(expected.utf8))
        XCTAssertFalse(windows.isEmpty)
        let start = try XCTUnwrap(archive.lowResolutionFrames.first?.captureTimestamp)
        let inputs = Dictionary(archive.coarseMotionSamples.map {
            ($0.captureTimestamp, $0.input?.horizontalInput)
        }, uniquingKeysWith: { _, latest in latest })
        var tracker = VisualSceneDiscontinuityTracker()
        var cuts = [Cut]()
        var traces = [Trace]()
        let traceWindow = try env["HKV_LOADING_GAP_AUDIT_TRACE_WINDOW"].map {
            try JSONDecoder().decode([Double].self, from: Data($0.utf8))
        }
        for frame in archive.lowResolutionFrames {
            let raw = inputs[frame.captureTimestamp] ?? nil
            let grid = LowResolutionMotionGrid(width: frame.width, height: frame.height, luma: Array(frame.luma))
            let seconds = frame.captureTimestamp - start
            let tracing = traceWindow.map { $0.count == 2 && ($0[0]...$0[1]).contains(seconds) } ?? false
            let before = tracing ? tracker.diagnostic(for: grid) : nil
            if let result = tracker.observe(grid: grid, timestamp: frame.captureTimestamp,
                direction: raw.flatMap(VisualRoomDirection.init(rawValue:)), worldPose: .zero) {
                cuts.append(.init(departureSeconds: result.departureTimestamp - start,
                    arrivalSeconds: result.arrivalTimestamp - start, direction: result.direction.rawValue))
            }
            if let before {
                traces.append(.init(seconds: seconds, direction: raw,
                    before: before, after: tracker.diagnostic(for: grid)))
            }
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(cuts).write(to: URL(fileURLWithPath: output), options: .atomic)
        if !traces.isEmpty {
            try encoder.encode(traces).write(to: URL(fileURLWithPath: output + ".trace.json"), options: .atomic)
        }
        print("Archived loading cuts: \(cuts.map(\.arrivalSeconds))")
        for window in windows {
            XCTAssertEqual(window.count, 2)
            guard window.count == 2 else { continue }
            XCTAssertTrue(cuts.contains { (window[0]...window[1]).contains($0.arrivalSeconds) },
                "Missing image-confirmed arrival in \(window)")
        }
    }
}
