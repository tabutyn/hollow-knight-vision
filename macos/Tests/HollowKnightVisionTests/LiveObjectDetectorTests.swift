import CoreGraphics
import CoreML
import Foundation
import XCTest
@testable import HollowKnightVision

final class LiveObjectDetectorTests: XCTestCase {
    func testDepositHeatmapRequiresMatchingModelRunAndCalibratedConfidence() throws {
        let run = UUID()
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        func write(_ confidence: Double) throws {
            let recipe: [String: Any] = [
                "sourceRunIdentifier": run.uuidString,
                "localizer": ["modelFilename": "GeoDeposit.mlmodel", "sha256": String(repeating: "a", count: 64),
                              "standaloneConfidence": confidence, "boxWidth": 0.058, "boxHeight": 0.105]
            ]
            try JSONSerialization.data(withJSONObject: recipe).write(to: url)
        }
        try write(0.95)
        XCTAssertNotNil(LiveGeoDepositConfirmation.load(from: url, run: run))
        XCTAssertNil(LiveGeoDepositConfirmation.load(from: url, run: UUID()))
        try write(0.6)
        XCTAssertNil(LiveGeoDepositConfirmation.load(from: url, run: run))
    }

    func testGeoDepositPeakRejectsSoulVesselButAllowsTheNearbyRoom() {
        func sample(_ x: CGFloat, _ y: CGFloat) -> LiveObjectDetection {
            .init(classIdentifier: LiveGeoDepositConfirmation.identifier,
                  normalizedRect: CGRect(x: x - 0.025, y: y - 0.05, width: 0.05, height: 0.1),
                  confidence: 0.98, sourceFrameIdentifier: 1, modelVersion: .init(major: 8, minor: 0))
        }
        XCTAssertFalse(LiveGeoDepositConfirmation.visibleDeposit(sample(60 / 640, 40 / 360)))
        XCTAssertTrue(LiveGeoDepositConfirmation.visibleDeposit(sample(80 / 640, 40 / 360)))
        XCTAssertTrue(LiveGeoDepositConfirmation.visibleDeposit(sample(60 / 640, 61 / 360)))
    }

    func testKnightReferenceRecoversMissingMeasurementsAndChecksStrongHUDDisagreements() {
        let recipe = LiveKnightConfirmation(sourceRunIdentifier: UUID(), weakConfidence: 0.25,
            referenceConfidence: 0.975, correctHUDMatches: true,
            localizer: .init(modelFilename: "Knight.mlmodel", sha256: String(repeating: "a", count: 64),
                standaloneConfidence: 0.98, boxWidth: 0.034, boxHeight: 0.12))
        func knight(_ x: CGFloat, _ y: CGFloat, _ confidence: Double) -> LiveObjectDetection {
            .init(classIdentifier: LiveKnightConfirmation.identifier, normalizedRect: CGRect(x: x, y: y, width: 0.034, height: 0.12),
                  confidence: confidence, sourceFrameIdentifier: 42, modelVersion: .init(major: 8, minor: 0))
        }
        let strong = knight(0.48, 0.56, 0.78), weak = knight(0.48, 0.56, 0.35)
        let corroborated = knight(0.49, 0.56, 0.976), independent = knight(0.49, 0.56, 0.997)
        let hud = knight(0.1, 0.04, 0.85)
        XCTAssertFalse(recipe.needsReference([strong]))
        XCTAssertEqual(recipe.supplement([strong], reference: [independent]), [strong])
        XCTAssertEqual(recipe.supplement([], reference: [corroborated]), [])
        XCTAssertEqual(recipe.supplement([weak], reference: [corroborated]), [corroborated])
        XCTAssertEqual(recipe.supplement([], reference: [independent]), [independent])
        XCTAssertEqual(recipe.supplement([hud], reference: [independent]), [independent])
        XCTAssertEqual(recipe.supplement([hud], reference: [knight(0.12, 0.03, 0.999)]), [hud])
    }

    func testCrawlidHeatmapKeepsThePeakInTopLeftImageCoordinates() throws {
        let localizer = LiveActorHeatmap(modelFilename: "Crawlid.mlmodel", sha256: String(repeating: "a", count: 64),
            standaloneConfidence: 0.995, boxWidth: 0.05, boxHeight: 20.0 / 360)
        let scores = try MLMultiArray(shape: [1, 1, 90, 160], dataType: .float32)
        for index in 0..<scores.count { scores[index] = 0 }
        scores[30 * 160 + 80] = 0.998
        scores[30 * 160 + 81] = 0.994
        scores[75 * 160 + 35] = 0.992
        let boxes = localizer.detections(scores, identifier: LiveCrawlidConfirmation.identifier, threshold: 0.99, frame: 5, version: .init(major: 8, minor: 0))
        XCTAssertEqual(boxes.count, 2)
        XCTAssertEqual(boxes[0].normalizedRect.midX, 0.5, accuracy: 1e-6)
        XCTAssertEqual(boxes[0].normalizedRect.midY, 1.0 / 3, accuracy: 1e-6)
        var calibration = LiveCrawlidConfirmation(sourceRunIdentifier: UUID(), weakConfidence: 0.1,
            referenceConfidence: 0.99, minimumOverlap: 0.25)
        calibration.localizer = localizer
        XCTAssertEqual(calibration.supplement([], weak: [], reference: boxes), [boxes[0]])
    }

    func testCrawlidConfirmationRequiresSpatialAgreementAndPreservesExistingDetections() {
        let calibration = LiveCrawlidConfirmation(sourceRunIdentifier: UUID(), weakConfidence: 0.18,
                                                   referenceConfidence: 0.65, minimumOverlap: 0.6)
        func box(_ x: CGFloat, _ confidence: Double, _ name: String = "enemies.crawlid") -> LiveObjectDetection {
            .init(classIdentifier: name, normalizedRect: CGRect(x: x, y: 0.6, width: 0.05, height: 0.08),
                  confidence: confidence, sourceFrameIdentifier: 1, modelVersion: .init(major: 8, minor: 0))
        }
        let original = box(0.2, 0.86), knight = box(0.4, 0.51, "game.playable-knight"), agreed = box(0.601, 0.93)
        let detections = calibration.supplement([original, knight], weak: [box(0.2, 0.24), box(0.6, 0.23)],
            reference: [box(0.201, 0.8), agreed, box(0.8, 0.97), box(0.6, 0.53)])
        XCTAssertEqual(detections, [original, knight, agreed])
    }

    func testUntrainedHeadsCannotPublishEvenAtFullConfidence() {
        var artifact = LabelingActiveModelArtifact(classIdentifier: LabelingModelIdentity.sharedObjectModel,
            version: .init(major: 3, minor: 0), modelURL: URL(fileURLWithPath: "/tmp/Detector.mlmodel"),
            trainedClassIdentifiers: ["game.playable-knight", "enemies.crawlid"])
        let predictions = ["enemies.shade", "game.playable-knight", "enemies.crawlid"].map {
            LiveRawObjectPrediction(classIdentifier: $0,
                normalizedVisionRect: CGRect(x: 0.2, y: 0.2, width: 0.1, height: 0.2), confidence: 1)
        }
        XCTAssertEqual(Set(LiveObjectDetectionPostprocessor.detections(from: predictions, artifact: artifact,
            sourceFrameIdentifier: 1).map(\.classIdentifier)), ["game.playable-knight", "enemies.crawlid"])
        artifact.trainedClassIdentifiers = []
        XCTAssertTrue(LiveObjectDetectionPostprocessor.detections(from: predictions, artifact: artifact,
            sourceFrameIdentifier: 1).isEmpty)
    }

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
            modelURL: URL(fileURLWithPath: "/tmp/Detector.mlmodel"),
            trainedClassIdentifiers: ["enemies.crawlid", "world.sign", "enemies.vengfly", "game.playable-knight"]
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

        let result = LiveObjectPixelRefiner.refine([raw], in: image)

        XCTAssertNotNil(result.diagnosticImage)
        XCTAssertLessThanOrEqual(result.detections[0].normalizedRect.width, raw.normalizedRect.width)
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
