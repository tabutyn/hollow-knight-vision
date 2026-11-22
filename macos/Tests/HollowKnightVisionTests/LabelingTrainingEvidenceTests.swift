import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingTrainingEvidenceTests: XCTestCase {
    func testLegacyEvidenceRequiresPositiveTrainingBoxes() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let datasetID = UUID(), runID = UUID()
        let run = root.appendingPathComponent("runs/\(runID.uuidString.lowercased())")
        let dataset = root.appendingPathComponent("datasets/\(datasetID.uuidString.lowercased())")
        try FileManager.default.createDirectory(at: run, withIntermediateDirectories: true)
        for split in ["training", "validation"] {
            try FileManager.default.createDirectory(at: dataset.appendingPathComponent(split), withIntermediateDirectories: true)
        }
        let summary = ["datasetIdentifier": datasetID.uuidString,
                       "classIdentifiers": ["game.playable-knight", "enemies.shade", "enemies.vengfly"]] as [String: Any]
        try JSONSerialization.data(withJSONObject: summary).write(to: run.appendingPathComponent("training.json"))
        func annotation(_ label: String, negative: Bool = false, width: Double = 20) -> CreateMLObjectAnnotation {
            .init(label: label, coordinates: .init(x: 30, y: 40, width: width, height: 40), isNegative: negative)
        }
        let encoder = JSONEncoder()
        try encoder.encode([CreateMLImageAnnotations(image: "training.png", annotations: [
            annotation("game.playable-knight"), annotation("enemies.shade", negative: true),
            annotation("enemies.vengfly", width: 0)])])
            .write(to: dataset.appendingPathComponent("training/annotations.json"))
        try encoder.encode([CreateMLImageAnnotations(image: "validation.png", annotations: [annotation("enemies.shade")])])
            .write(to: dataset.appendingPathComponent("validation/annotations.json"))
        XCTAssertEqual(LabelingTrainingEvidence.trainedClasses(runIdentifier: runID, trainingRootURL: root),
                       ["game.playable-knight"])
        try FileManager.default.removeItem(at: dataset)
        XCTAssertTrue(LabelingTrainingEvidence.trainedClasses(runIdentifier: runID, trainingRootURL: root).isEmpty)
        let certified = ["datasetIdentifier": datasetID.uuidString,
                         "trainedClassIdentifiers": ["game.playable-knight"]] as [String: Any]
        try JSONSerialization.data(withJSONObject: certified).write(to: run.appendingPathComponent("training.json"))
        XCTAssertEqual(LabelingTrainingEvidence.trainedClasses(runIdentifier: runID, trainingRootURL: root),
                       ["game.playable-knight"])
    }
}
