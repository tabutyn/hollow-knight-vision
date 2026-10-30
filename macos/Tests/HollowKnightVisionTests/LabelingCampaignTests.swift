import CoreGraphics
import Foundation
import XCTest
import SwiftUI
import ImageIO
@testable import HollowKnightVision

final class LabelingCampaignTests: XCTestCase {
    func testBatchesAdvanceOnlyAfterCompletedTraining() {
        let first = LabelingCampaignProgress(id: "knight", count: 9, completedCount: 0)
        XCTAssertEqual(first.target, 10)
        XCTAssertEqual(first.remaining, 1)
        XCTAssertFalse(first.needsTraining)
        let ready = LabelingCampaignProgress(id: "knight", count: 14, completedCount: 2)
        XCTAssertEqual(ready.target, 10, "Small experimental runs must not increase the initial target.")
        XCTAssertTrue(ready.needsTraining)
        XCTAssertEqual(ready.guidanceKey(labeling: false, inModel: true), "campaignTrainHere")
        let trained = LabelingCampaignProgress(id: "knight", count: 14, completedCount: 14)
        XCTAssertEqual(trained.target, 20)
        XCTAssertEqual(trained.remaining, 6)
        XCTAssertEqual(trained.guidanceKey(labeling: true, inModel: false), "campaignDraw")
        XCTAssertEqual(LabelingCampaignProgress(id: "crawlid", count: 0, completedCount: 0).target, 10)
    }

    func testCaptureCountsAndImmutableTrainingMilestones() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = LabelingExampleStore(rootURL: root.appendingPathComponent("examples"))
        let exporter = LabelingDatasetExporter(rootURL: root.appendingPathComponent("datasets"))
        let group = UUID()
        var examples = [SavedLabelingExample]()
        for index in 0..<11 {
            let c = try XCTUnwrap(CGContext(data: nil, width: 64, height: 36, bitsPerComponent: 8,
                bytesPerRow: 256, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
            c.setFillColor(CGColor(red: CGFloat(index) / 12, green: 0.3, blue: 0.4, alpha: 1))
            c.fill(CGRect(x: 0, y: 0, width: 64, height: 36))
            examples.append(try store.saveDraft(image: XCTUnwrap(c.makeImage()), context: .game, rectangles: [
                box("game.playable-knight"), box("game.playable-knight"),
                box("enemies.crawlid", negative: true)
            ], captureGroupIdentifier: index < 2 ? group : UUID()))
        }
        let counts = LabelingCampaignPolicy.counts(examples)
        XCTAssertEqual(counts["game.playable-knight"], 10)
        XCTAssertNil(counts["enemies.crawlid"])
        let dataset = try exporter.exportSharedModel(classIdentifiers: ["game.playable-knight", "enemies.crawlid"], examples: examples)
        XCTAssertEqual(try LabelingCampaignPolicy.completedCounts(datasetURL: dataset.directoryURL), counts)
        let reduced = Array(examples.prefix(2))
        XCTAssertEqual(LabelingCampaignPolicy.counts(reduced)["game.playable-knight"], 1)
        XCTAssertEqual(try LabelingCampaignPolicy.completedCounts(datasetURL: dataset.directoryURL)["game.playable-knight"], 10,
                       "Later deletions must not rewrite completed-run history.")
        XCTAssertThrowsError(try LabelingCampaignPolicy.completedCounts(datasetURL: root.appendingPathComponent("missing")))
    }

    func testPlotRejectsTrainingOnlyAndInvalidMetricsButKeepsRealZero() throws {
        func candidate(run: Int, validation: Int, accuracy: Double?, valid: Bool = true) throws -> LabelingModelCandidate {
            var json: [String: Any] = ["id": UUID().uuidString, "datasetIdentifier": UUID().uuidString,
                "classIdentifier": LabelingSharedModelPolicy.modelIdentifier, "completedAt": "2026-10-30T12:00:0\(run)Z",
                "maximumIterations": 10, "trainingAnnotationCount": 10, "validationAnnotationCount": validation,
                "isPreliminary": true, "modelFilename": "Detector.mlmodel", "trainingMetrics": [
                    "isValid": true, "averagePrecisionByClass": [:], "averagePrecisionAt50PercentIOUByClass": [:]]]
            if let accuracy { json["validationMetrics"] = ["isValid": valid, "meanAveragePrecisionAt50PercentIOU": accuracy,
                "averagePrecisionByClass": [:], "averagePrecisionAt50PercentIOUByClass": [:]] }
            let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
            let summary = try decoder.decode(LabelingTrainingRunSummary.self, from: JSONSerialization.data(withJSONObject: json))
            return LabelingModelCandidate(runURL: URL(fileURLWithPath: "/unused"), datasetURL: URL(fileURLWithPath: "/unused"),
                                          summary: summary, predictionReview: nil)
        }
        let points = LabelingCampaignPolicy.history(try [candidate(run: 1, validation: 0, accuracy: 1),
            candidate(run: 2, validation: 3, accuracy: 0), candidate(run: 3, validation: 3, accuracy: 0.6),
            candidate(run: 4, validation: 3, accuracy: 1.5), candidate(run: 5, validation: 3, accuracy: 1, valid: false)])
        XCTAssertEqual(points.map(\.run), [2, 3])
        XCTAssertEqual(points.map(\.accuracy), [0, 0.6])
    }

    @MainActor func testRenderCampaignPreview() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_CAMPAIGN_PREVIEW_PATH"] else {
            throw XCTSkip("Set HKV_CAMPAIGN_PREVIEW_PATH to inspect campaign chart rendering.")
        }
        let progress = LabelingSharedModelPolicy.classIdentifiers.sorted().enumerated().map { index, identifier in
            LabelingCampaignProgress(id: identifier, count: index == 3 ? 14 : index, completedCount: index == 3 ? 10 : 0)
        }
        let view = LabelingCampaignView(progress: progress, history: [
            LabelingCampaignPoint(id: UUID(), run: 1, accuracy: 0.2, validationCount: 5),
            LabelingCampaignPoint(id: UUID(), run: 2, accuracy: 0.5, validationCount: 8),
            LabelingCampaignPoint(id: UUID(), run: 3, accuracy: 0.65, validationCount: 10)
        ], historyUnavailable: false).frame(width: 1100).background(Color.black).environment(\.colorScheme, .dark)
        let renderer = ImageRenderer(content: view)
        let image = try XCTUnwrap(renderer.cgImage)
        let target = try XCTUnwrap(CGImageDestinationCreateWithURL(URL(fileURLWithPath: path) as CFURL,
            "public.png" as CFString, 1, nil))
        CGImageDestinationAddImage(target, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(target))
        XCTAssertGreaterThan(image.width, 1000)
    }

    private func box(_ identifier: String, negative: Bool = false) -> LabelingDraftRectangle {
        LabelingDraftRectangle(id: UUID(), classID: identifier,
            normalizedRect: CGRect(x: 0.1, y: 0.2, width: 0.2, height: 0.3), isNegative: negative)
    }
}
