import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingModelReviewStoreTests: XCTestCase {
    func testCompactRunRecoversPromotesAndRetainsCheckpointAfterSourceRemoval() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("hkv-compact-run-\(UUID())")
        defer { try? FileManager.default.removeItem(at: root) }
        let runs = root.appendingPathComponent("runs")
        try FileManager.default.createDirectory(at: runs, withIntermediateDirectories: true)
        let id = UUID()
        let run = try writeRun(id: id, datasetID: UUID(), completedAt: "2026-10-29T12:00:00Z",
            predictionsFilename: nil, classIdentifier: LabelingModelIdentity.sharedObjectModel, runsRoot: runs)
        let manifest = run.appendingPathComponent("training.json")
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: manifest)) as? [String: Any])
        json["id"] = id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        json["checkpointFilename"] = "Detector.pt"
        try JSONSerialization.data(withJSONObject: json).write(to: manifest)
        try Data("model".utf8).write(to: run.appendingPathComponent("Detector.mlmodel"))
        try Data("checkpoint".utf8).write(to: run.appendingPathComponent("Detector.pt"))
        let reviews = LabelingModelReviewStore(runsRootURL: runs, datasetsRootURL: root.appendingPathComponent("datasets"))
        let candidate = try XCTUnwrap(reviews.loadLatestCandidate())
        XCTAssertEqual(candidate.id, id)
        let versions = LabelingModelVersionStore(rootURL: root.appendingPathComponent("models"))
        try versions.synchronizeCandidates([candidate])
        let restored = try versions.promote(candidate)
        XCTAssertEqual(restored.sourceRunIdentifier, id)
        XCTAssertEqual(restored.version.displayName, "v1.0")
        try FileManager.default.removeItem(at: run)
        let checkpoint = try XCTUnwrap(versions.trainingCheckpointURL(for: LabelingModelIdentity.sharedObjectModel, candidates: []))
        XCTAssertEqual(try String(contentsOf: checkpoint), "checkpoint")
        let restoreJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf:
            checkpoint.deletingLastPathComponent().appendingPathComponent("restore-point.json"))) as? [String: Any])
        XCTAssertEqual((restoreJSON["sourceRunIdentifier"] as? String)?.lowercased(), id.uuidString.lowercased())
    }

    func testRunIdentifierAcceptsOnlyCanonicalOrCompactUUIDs() throws {
        let decoder = JSONDecoder()
        let id = UUID()
        for valid in [id.uuidString, id.uuidString.lowercased(), id.uuidString.replacingOccurrences(of: "-", with: ""), id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()] {
            let decoded = try decoder.decode(TrainingRunIdentifier.self, from: JSONEncoder().encode(valid))
            XCTAssertEqual(decoded.wrappedValue, id)
        }
        for invalid in ["", " ", "not-a-run-id", "../run", String(repeating: "x", count: 32), String(repeating: "1", count: 36), "{" + id.uuidString + "}", String(repeating: "é", count: 16)] {
            XCTAssertThrowsError(try decoder.decode(TrainingRunIdentifier.self, from: JSONEncoder().encode(invalid)))
        }
        for invalid in ["null", "42", "{}"] {
            XCTAssertThrowsError(try decoder.decode(TrainingRunIdentifier.self, from: Data(invalid.utf8)))
        }
    }

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
