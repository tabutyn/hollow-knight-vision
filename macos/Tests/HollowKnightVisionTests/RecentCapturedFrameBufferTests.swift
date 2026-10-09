import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class RecentCapturedFrameBufferTests: XCTestCase {
    func testKeepsSixtyFourNewestFramesInNewestFirstOrder() throws {
        var buffer = RecentCapturedFrameBuffer()
        for identifier in 1...70 {
            buffer.append(try frame(identifier: UInt64(identifier)))
        }

        XCTAssertEqual(buffer.frames.count, 64)
        XCTAssertEqual(
            buffer.frames.map(\.sourceFrameIdentifier),
            Array((7...70).reversed()).map(UInt64.init)
        )
    }

    func testNewCaptureGenerationClearsOldFramesAndRejectsDelayedOldFrame() throws {
        var buffer = RecentCapturedFrameBuffer()
        buffer.append(try frame(identifier: 10, generation: 1))
        buffer.append(try frame(identifier: 1, generation: 2))
        buffer.append(try frame(identifier: 11, generation: 1))

        XCTAssertEqual(buffer.frames.map(\.captureGeneration), [2])
        XCTAssertEqual(buffer.frames.map(\.sourceFrameIdentifier), [1])
    }

    func testExactInferenceCanReplacePredictionsForBufferedFrame() throws {
        var buffer = RecentCapturedFrameBuffer()
        buffer.append(try frame(identifier: 7))
        let prediction = detection("game.mana", sourceFrameIdentifier: 7)

        buffer.replaceDetections(
            [prediction],
            captureGeneration: 1,
            sourceFrameIdentifier: 7
        )

        XCTAssertEqual(buffer.frames.first?.detections, [prediction])
    }

    func testCapturedPredictionsInitializeContextAndRepeatedBoxes() throws {
        let captured = try frame(identifier: 8, detections: [
            detection("main-title.hollow-knight-logo", sourceFrameIdentifier: 8),
            detection("main-title.select-decoration", sourceFrameIdentifier: 8, x: 0.40),
            detection("main-title.select-decoration", sourceFrameIdentifier: 8, x: 0.58),
            detection("main-title.options", sourceFrameIdentifier: 8, x: 0.46),
            detection("game.mana", sourceFrameIdentifier: 8, x: 0.05),
        ])

        let state = RecentCapturedFrameLabelingPolicy.stateForOpening(
            captured,
            fallbackContext: .game,
            fallbackClassIdentifier: "game.health"
        )

        XCTAssertEqual(state.context, .mainTitle)
        XCTAssertEqual(state.selectedClassIdentifier, "main-title.hollow-knight-logo")
        XCTAssertEqual(state.draft.rectangles.count, 5)
        XCTAssertEqual(
            state.draft.rectangles.filter {
                $0.classID == LabelingClassIdentity.selectDecoration
            }.count,
            2
        )
        XCTAssertTrue(state.draft.rectangles.contains { $0.classID == "game.mana" })
    }

    func testCapturedFrameWithoutPredictionsUsesCurrentSelection() throws {
        let state = RecentCapturedFrameLabelingPolicy.stateForOpening(
            try frame(identifier: 9),
            fallbackContext: .game,
            fallbackClassIdentifier: "game.health"
        )

        XCTAssertEqual(state.context, .game)
        XCTAssertEqual(state.selectedClassIdentifier, "game.health")
        XCTAssertTrue(state.draft.rectangles.isEmpty)
    }

    private func frame(
        identifier: UInt64,
        generation: UInt64 = 1,
        detections: [LiveObjectDetection] = []
    ) throws -> RecentCapturedFrame {
        RecentCapturedFrame(
            captureGeneration: generation,
            sourceFrameIdentifier: identifier,
            image: try XCTUnwrap(CGContext(
                data: nil,
                width: 2,
                height: 2,
                bitsPerComponent: 8,
                bytesPerRow: 8,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            )?.makeImage()),
            detections: detections
        )
    }

    private func detection(
        _ classIdentifier: String,
        sourceFrameIdentifier: UInt64,
        x: CGFloat = 0.2
    ) -> LiveObjectDetection {
        LiveObjectDetection(
            classIdentifier: classIdentifier,
            normalizedRect: CGRect(x: x, y: 0.2, width: 0.1, height: 0.1),
            confidence: 0.9,
            sourceFrameIdentifier: sourceFrameIdentifier,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
    }
}
