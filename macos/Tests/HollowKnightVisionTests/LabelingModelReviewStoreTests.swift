import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingModelReviewStoreTests: XCTestCase {
    func testLoadsNewestCandidateAndItsPredictionReview() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-review-store-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        let datasetsRoot = temporaryRoot.appendingPathComponent("datasets", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)
        let olderID = UUID()
        let newerID = UUID()
        let newestOtherClassID = UUID()
        let datasetID = UUID()
        try writeRun(
            id: olderID,
            datasetID: UUID(),
            completedAt: "2026-09-10T12:00:00Z",
            predictionsFilename: nil,
            classIdentifier: "game.playable-knight",
            runsRoot: runsRoot
        )
        let newerURL = try writeRun(
            id: newerID,
            datasetID: datasetID,
            completedAt: "2026-09-11T12:00:00Z",
            predictionsFilename: "predictions.json",
            classIdentifier: "game.playable-knight",
            runsRoot: runsRoot
        )
        try writeRun(
            id: newestOtherClassID,
            datasetID: UUID(),
            completedAt: "2026-09-12T12:00:00Z",
            predictionsFilename: nil,
            classIdentifier: "game.mana",
            runsRoot: runsRoot
        )
        let exampleID = UUID()
        let predictionReview: [String: Any] = [
            "examples": [[
                "exampleIdentifier": exampleID.uuidString,
                "imageFilename": "held-out.png",
                "split": "validation",
                "human": [[
                    "label": "game.playable-knight", "x": 0.1, "y": 0.2,
                    "width": 0.3, "height": 0.4, "confidence": 1.0,
                ]],
                "predictions": [[
                    "label": "game.playable-knight", "x": 0.12, "y": 0.2,
                    "width": 0.3, "height": 0.4, "confidence": 0.8,
                ]],
            ]],
            "metrics": reviewMetrics,
            "baselineExamples": [[
                "exampleIdentifier": exampleID.uuidString,
                "imageFilename": "held-out.png",
                "split": "validation",
                "human": [],
                "predictions": [],
            ]],
            "baselineMetrics": reviewMetrics,
        ]
        try JSONSerialization.data(withJSONObject: predictionReview, options: [.sortedKeys])
            .write(to: newerURL.appendingPathComponent("predictions.json"))

        let store = LabelingModelReviewStore(
            runsRootURL: runsRoot,
            datasetsRootURL: datasetsRoot
        )
        let candidate = try XCTUnwrap(store.loadLatestCandidate(
            classIdentifier: "game.playable-knight"
        ))

        XCTAssertEqual(candidate.id, newerID)
        XCTAssertEqual(candidate.datasetURL, datasetsRoot.appendingPathComponent(
            datasetID.uuidString.lowercased(),
            isDirectory: true
        ))
        XCTAssertEqual(candidate.predictionReview?.examples.first?.id, exampleID)
        XCTAssertEqual(candidate.predictionReview?.examples.first?.predictions.first?.confidence, 0.8)
        XCTAssertEqual(candidate.predictionReview?.metrics.precision, 0.5)
        XCTAssertEqual(candidate.predictionReview?.baselineExamples?.first?.id, exampleID)
        XCTAssertEqual(candidate.predictionReview?.baselineMetrics?.recall, 1)
        XCTAssertNil(try store.loadLatestCandidate(classIdentifier: "game.health"))
        XCTAssertEqual(try store.loadLatestCandidate()?.id, newestOtherClassID)
    }

    @discardableResult
    private func writeRun(
        id: UUID,
        datasetID: UUID,
        completedAt: String,
        predictionsFilename: String?,
        classIdentifier: String,
        runsRoot: URL
    ) throws -> URL {
        let runURL = runsRoot.appendingPathComponent(id.uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: runURL, withIntermediateDirectories: false)
        var run: [String: Any] = [
            "id": id.uuidString,
            "datasetIdentifier": datasetID.uuidString,
            "classIdentifier": classIdentifier,
            "completedAt": completedAt,
            "maximumIterations": 10,
            "trainingAnnotationCount": 1,
            "validationAnnotationCount": 1,
            "isPreliminary": true,
            "modelFilename": "Detector.mlmodel",
            "trainingMetrics": detectorMetrics,
            "validationMetrics": detectorMetrics,
        ]
        if let predictionsFilename {
            run["predictionsFilename"] = predictionsFilename
            run["reviewMetrics"] = reviewMetrics
        }
        try JSONSerialization.data(withJSONObject: run, options: [.sortedKeys])
            .write(to: runURL.appendingPathComponent("training.json"))
        return runURL
    }

    private var detectorMetrics: [String: Any] {
        [
            "isValid": true,
            "meanAveragePrecision": 0.25,
            "meanAveragePrecisionAt50PercentIOU": 0.5,
            "averagePrecisionByClass": ["game.playable-knight": 0.25],
            "averagePrecisionAt50PercentIOUByClass": ["game.playable-knight": 0.5],
        ]
    }

    private var reviewMetrics: [String: Any] {
        [
            "confidenceThreshold": 0.5,
            "intersectionOverUnionThreshold": 0.5,
            "truePositives": 1,
            "falsePositives": 1,
            "falseNegatives": 0,
            "precision": 0.5,
            "recall": 1.0,
            "meanIntersectionOverUnion": 0.75,
        ]
    }
}
