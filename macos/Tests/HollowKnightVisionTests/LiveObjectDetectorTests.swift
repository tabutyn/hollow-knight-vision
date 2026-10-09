import CoreGraphics
import CoreML
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveObjectDetectorTests: XCTestCase {
    func testTensorAccessorReadsContiguousFloat32Output() throws {
        let values = try MLMultiArray(shape: [1, 2, 2, 2], dataType: .float32)
        for index in 0..<values.count {
            values[index] = NSNumber(value: Float(index) + 0.25)
        }
        let tensor = try XCTUnwrap(LiveTensor4D(values))

        XCTAssertEqual(tensor.shape, [1, 2, 2, 2])
        XCTAssertEqual(tensor.value(channel: 0, y: 1, x: 1), 3.25)
        XCTAssertEqual(tensor.value(channel: 1, y: 0, x: 0), 4.25)
    }

    func testPostprocessingConvertsVisionCoordinatesFiltersAndSuppressesOverlap() {
        let artifact = LabelingActiveModelArtifact(
            classIdentifier: LabelingModelIdentity.sharedObjectModel,
            version: LabelingModelSemanticVersion(major: 1, minor: 0),
            modelURL: URL(fileURLWithPath: "/tmp/Detector.mlmodel")
        )
        let predictions = [
            LiveRawObjectPrediction(
                classIdentifier: "enemies.crawlid",
                normalizedVisionRect: CGRect(x: 0.10, y: 0.20, width: 0.30, height: 0.40),
                confidence: 0.9
            ),
            LiveRawObjectPrediction(
                classIdentifier: "enemies.crawlid",
                normalizedVisionRect: CGRect(x: 0.11, y: 0.21, width: 0.30, height: 0.40),
                confidence: 0.8
            ),
            LiveRawObjectPrediction(
                classIdentifier: "world.sign",
                normalizedVisionRect: CGRect(x: 0.10, y: 0.20, width: 0.30, height: 0.40),
                confidence: 0.75
            ),
            LiveRawObjectPrediction(
                classIdentifier: "enemies.vengfly",
                normalizedVisionRect: CGRect(x: 0.70, y: 0.70, width: 0.10, height: 0.10),
                confidence: 0.7
            ),
            LiveRawObjectPrediction(
                normalizedVisionRect: CGRect(x: 0.40, y: 0.40, width: 0.10, height: 0.10),
                confidence: 0.49
            ),
            LiveRawObjectPrediction(
                classIdentifier: "game.playable-knight",
                normalizedVisionRect: CGRect(x: 0.45, y: 0.10, width: 0.05, height: 0.08),
                confidence: 0.08
            ),
        ]

        let detections = LiveObjectDetectionPostprocessor.detections(
            from: predictions,
            artifact: artifact,
            sourceFrameIdentifier: 42
        )

        XCTAssertEqual(detections.count, 4)
        XCTAssertEqual(detections[0].classIdentifier, "enemies.crawlid")
        XCTAssertEqual(detections[1].classIdentifier, "world.sign")
        XCTAssertTrue(detections.contains {
            $0.classIdentifier == "game.playable-knight" && $0.confidence == 0.08
        })
        XCTAssertEqual(detections[0].modelVersion.displayName, "v1.0")
        XCTAssertEqual(detections[0].sourceFrameIdentifier, 42)
        XCTAssertEqual(detections[0].normalizedRect.minX, 0.10, accuracy: 0.0001)
        XCTAssertEqual(detections[0].normalizedRect.minY, 0.40, accuracy: 0.0001)
        XCTAssertEqual(detections[0].normalizedRect.width, 0.30, accuracy: 0.0001)
        XCTAssertEqual(detections[0].normalizedRect.height, 0.40, accuracy: 0.0001)
    }

    func testTopLeftDetectionMapsBackIntoCoreImageCoordinates() throws {
        let detection = LiveObjectDetection(
            classIdentifier: "game.mana",
            normalizedRect: CGRect(x: 0.10, y: 0.40, width: 0.30, height: 0.40),
            confidence: 0.9,
            sourceFrameIdentifier: 42,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )

        let imageRect = try XCTUnwrap(detection.imageRect(width: 100, height: 50))
        XCTAssertEqual(imageRect.minX, 10, accuracy: 0.0001)
        XCTAssertEqual(imageRect.minY, 10, accuracy: 0.0001)
        XCTAssertEqual(imageRect.width, 30, accuracy: 0.0001)
        XCTAssertEqual(imageRect.height, 20, accuracy: 0.0001)
    }

    func testDetectionBatchExpiresAndCannotCrossCaptureGeneration() {
        let detection = LiveObjectDetection(
            classIdentifier: "game.health",
            normalizedRect: CGRect(x: 0.1, y: 0.1, width: 0.2, height: 0.2),
            confidence: 0.8,
            sourceFrameIdentifier: 7,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
        let batch = LiveObjectDetectionBatch(
            detections: [detection],
            sourceFrameIdentifier: 7,
            sourceTimestamp: 10,
            captureGeneration: 3,
            inferenceDuration: 0.02
        )

        XCTAssertEqual(LiveObjectDetectionFreshness.detections(
            from: batch,
            captureGeneration: 3,
            timestamp: 10.399
        ), [detection])
        XCTAssertEqual(LiveObjectDetectionFreshness.detections(
            from: batch,
            captureGeneration: 3,
            timestamp: 10.401
        ), [])
        XCTAssertEqual(LiveObjectDetectionFreshness.detections(
            from: batch,
            captureGeneration: 4,
            timestamp: 10.1
        ), [])
    }

    func testTemporalTrackerSmoothsJitterAndBridgesBriefMisses() throws {
        var tracker = LiveObjectDetectionTracker()
        let first = objectDetection(x: 0.10, confidence: 0.9, frame: 1)
        XCTAssertEqual(tracker.update(detections: [first], captureGeneration: 2).count, 1)

        let second = objectDetection(x: 0.20, confidence: 0.9, frame: 2)
        let smoothed = try XCTUnwrap(
            tracker.update(detections: [second], captureGeneration: 2).first
        )
        XCTAssertGreaterThan(smoothed.normalizedRect.minX, 0.10)
        XCTAssertLessThan(smoothed.normalizedRect.minX, 0.20)

        for _ in 0..<4 {
            XCTAssertEqual(tracker.update(detections: [], captureGeneration: 2).count, 1)
        }
        XCTAssertTrue(tracker.update(detections: [], captureGeneration: 2).isEmpty)
    }

    func testTemporalTrackerDoesNotCrossCaptureGeneration() {
        var tracker = LiveObjectDetectionTracker()
        _ = tracker.update(
            detections: [objectDetection(x: 0.1, confidence: 0.9, frame: 1)],
            captureGeneration: 2
        )
        XCTAssertTrue(tracker.update(
            detections: [objectDetection(x: 0.1, confidence: 0.7, frame: 2)],
            captureGeneration: 3
        ).isEmpty)
    }

    func testPixelRefinementFindsContrastingObjectBoundary() throws {
        let width = 100
        let height = 100
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(CGRect(x: 20, y: 60, width: 21, height: 21))
        let image = try XCTUnwrap(context.makeImage())
        let raw = LiveObjectDetection(
            classIdentifier: "game.mana",
            normalizedRect: CGRect(x: 0.22, y: 0.62, width: 0.16, height: 0.16),
            confidence: 0.9,
            sourceFrameIdentifier: 1,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )

        let result = LiveObjectPixelRefiner.refine(
            [raw], in: image, extensionFraction: 0.6
        )

        XCTAssertNotNil(result.diagnosticImage)
        XCTAssertGreaterThan(result.detections[0].normalizedRect.width, raw.normalizedRect.width)
    }

    private func objectDetection(
        x: CGFloat,
        confidence: Double,
        frame: UInt64
    ) -> LiveObjectDetection {
        LiveObjectDetection(
            classIdentifier: "game.mana",
            normalizedRect: CGRect(x: x, y: 0.1, width: 0.2, height: 0.2),
            confidence: confidence,
            sourceFrameIdentifier: frame,
            modelVersion: LabelingModelSemanticVersion(major: 1, minor: 0)
        )
    }
}
