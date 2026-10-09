import Foundation
import XCTest
@testable import HollowKnightVision

final class LabelingModelVersionStoreTests: XCTestCase {
    func testCandidatesPromoteIntoDurableRestorePointsAndCanRestoreDefault() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-model-versions-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let modelsRoot = temporaryRoot.appendingPathComponent("models", isDirectory: true)
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)

        let first = try candidate(
            classIdentifier: "game.mana",
            completedAt: Date(timeIntervalSince1970: 100),
            modelContents: "first",
            runsRoot: runsRoot
        )
        let second = try candidate(
            classIdentifier: "game.mana",
            completedAt: Date(timeIntervalSince1970: 200),
            modelContents: "second",
            runsRoot: runsRoot
        )
        let store = LabelingModelVersionStore(rootURL: modelsRoot)

        try store.synchronizeCandidates([second, first])
        XCTAssertEqual(
            try store.candidateVersion(for: first),
            LabelingModelSemanticVersion(major: 0, minor: 1)
        )
        XCTAssertEqual(
            try store.candidateVersion(for: second),
            LabelingModelSemanticVersion(major: 0, minor: 2)
        )
        XCTAssertEqual(try store.activeSelection(for: "game.mana"), .defaultModel)

        let firstRestorePoint = try store.promote(
            second,
            now: Date(timeIntervalSince1970: 300)
        )
        XCTAssertEqual(firstRestorePoint.version.displayName, "v1.0")
        XCTAssertEqual(try store.activeSelection(for: "game.mana"), .promoted(1))
        let firstModelURL = try XCTUnwrap(store.modelURL(
            for: .promoted(1),
            classIdentifier: "game.mana"
        ))
        XCTAssertEqual(try store.activeModelArtifacts(), [
            LabelingActiveModelArtifact(
                classIdentifier: "game.mana",
                version: LabelingModelSemanticVersion(major: 1, minor: 0),
                modelURL: firstModelURL
            )
        ])
        XCTAssertEqual(try String(contentsOf: firstModelURL, encoding: .utf8), "second")

        let third = try candidate(
            classIdentifier: "game.mana",
            completedAt: Date(timeIntervalSince1970: 400),
            modelContents: "third",
            runsRoot: runsRoot
        )
        try store.synchronizeCandidates([first, second, third])
        XCTAssertEqual(
            try store.candidateVersion(for: third),
            LabelingModelSemanticVersion(major: 1, minor: 1)
        )

        let secondRestorePoint = try store.promote(
            third,
            now: Date(timeIntervalSince1970: 500)
        )
        XCTAssertEqual(secondRestorePoint.version.displayName, "v2.0")
        XCTAssertEqual(
            try store.restorePoints(for: "game.mana").map(\.version.displayName),
            ["v1.0", "v2.0"]
        )

        try store.activate(.promoted(1), for: "game.mana")
        let reloaded = LabelingModelVersionStore(rootURL: modelsRoot)
        XCTAssertEqual(try reloaded.activeSelection(for: "game.mana"), .promoted(1))
        try reloaded.activate(.defaultModel, for: "game.mana")
        XCTAssertEqual(try reloaded.activeSelection(for: "game.mana"), .defaultModel)
    }

    func testObjectsKeepIndependentVersionsAndSelections() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-model-objects-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let modelsRoot = temporaryRoot.appendingPathComponent("models", isDirectory: true)
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)
        let mana = try candidate(
            classIdentifier: "game.mana",
            completedAt: Date(timeIntervalSince1970: 100),
            modelContents: "mana",
            runsRoot: runsRoot
        )
        let health = try candidate(
            classIdentifier: "game.health",
            completedAt: Date(timeIntervalSince1970: 200),
            modelContents: "health",
            runsRoot: runsRoot
        )
        let store = LabelingModelVersionStore(rootURL: modelsRoot)

        try store.synchronizeCandidates([mana, health])
        _ = try store.promote(mana)

        XCTAssertEqual(try store.activeSelection(for: "game.mana"), .promoted(1))
        XCTAssertEqual(try store.activeSelection(for: "game.health"), .defaultModel)
        XCTAssertEqual(try store.restorePoints(for: "game.health"), [])
        XCTAssertEqual(
            try store.candidateVersion(for: health),
            LabelingModelSemanticVersion(major: 0, minor: 1)
        )
    }

    func testActiveSharedModelReplacesLegacyPerObjectPasses() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-model-shared-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let modelsRoot = temporaryRoot.appendingPathComponent("models", isDirectory: true)
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)
        let legacy = try candidate(
            classIdentifier: "game.mana",
            completedAt: Date(timeIntervalSince1970: 100),
            modelContents: "legacy",
            runsRoot: runsRoot
        )
        let shared = try candidate(
            classIdentifier: LabelingModelIdentity.sharedObjectModel,
            completedAt: Date(timeIntervalSince1970: 200),
            modelContents: "shared",
            runsRoot: runsRoot
        )
        let store = LabelingModelVersionStore(rootURL: modelsRoot)
        try store.synchronizeCandidates([legacy, shared])
        _ = try store.promote(legacy)
        _ = try store.promote(shared)

        let artifacts = try store.activeModelArtifacts()
        XCTAssertEqual(artifacts.count, 1)
        XCTAssertEqual(artifacts.first?.classIdentifier, LabelingModelIdentity.sharedObjectModel)
    }

    func testIncrementalCheckpointFollowsDevelopmentCandidateAndRestorePoint() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-model-checkpoints-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let modelsRoot = temporaryRoot.appendingPathComponent("models", isDirectory: true)
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)
        let candidate = try candidate(
            classIdentifier: LabelingModelIdentity.sharedObjectModel,
            completedAt: Date(timeIntervalSince1970: 100),
            modelContents: "model",
            checkpointContents: "checkpoint",
            runsRoot: runsRoot
        )
        let store = LabelingModelVersionStore(rootURL: modelsRoot)
        try store.synchronizeCandidates([candidate])

        XCTAssertNil(try store.trainingCheckpointURL(
            for: LabelingModelIdentity.sharedObjectModel,
            candidates: [candidate]
        ))
        try store.recordDevelopmentCandidate(candidate)
        let candidateCheckpoint = try XCTUnwrap(store.trainingCheckpointURL(
            for: LabelingModelIdentity.sharedObjectModel,
            candidates: [candidate]
        ))
        XCTAssertEqual(try String(contentsOf: candidateCheckpoint), "checkpoint")

        _ = try store.promote(candidate)
        try FileManager.default.removeItem(at: candidate.runURL)
        let restoreCheckpoint = try XCTUnwrap(store.trainingCheckpointURL(
            for: LabelingModelIdentity.sharedObjectModel,
            candidates: []
        ))
        XCTAssertEqual(try String(contentsOf: restoreCheckpoint), "checkpoint")

        try store.activate(.defaultModel, for: LabelingModelIdentity.sharedObjectModel)
        XCTAssertNil(try store.trainingCheckpointURL(
            for: LabelingModelIdentity.sharedObjectModel,
            candidates: []
        ))
    }

    func testRegressedCandidateRemainsReviewableButDoesNotAdvanceTrainingLineage() throws {
        let temporaryRoot = FileManager.default.temporaryDirectory
            .appendingPathComponent("hkv-model-retention-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: temporaryRoot) }
        let modelsRoot = temporaryRoot.appendingPathComponent("models", isDirectory: true)
        let runsRoot = temporaryRoot.appendingPathComponent("runs", isDirectory: true)
        try FileManager.default.createDirectory(at: runsRoot, withIntermediateDirectories: true)
        let base = try candidate(
            classIdentifier: LabelingModelIdentity.sharedObjectModel,
            completedAt: Date(timeIntervalSince1970: 100),
            modelContents: "base-model",
            checkpointContents: "base-checkpoint",
            runsRoot: runsRoot,
            classIdentifiers: ["title", "decoration"],
            averagePrecisionByClass: ["title": 1, "decoration": 1]
        )
        let regressed = try candidate(
            classIdentifier: LabelingModelIdentity.sharedObjectModel,
            completedAt: Date(timeIntervalSince1970: 200),
            modelContents: "regressed-model",
            checkpointContents: "regressed-checkpoint",
            runsRoot: runsRoot,
            baseRunIdentifier: base.id.uuidString,
            classIdentifiers: ["title", "decoration", "mana"],
            averagePrecisionByClass: ["title": 0, "decoration": 0.5, "mana": 1]
        )
        let store = LabelingModelVersionStore(rootURL: modelsRoot)
        try store.synchronizeCandidates([base, regressed])
        try store.recordDevelopmentCandidate(regressed)
        try store.reconcileDevelopmentCandidates([base, regressed])

        let checkpoint = try XCTUnwrap(store.trainingCheckpointURL(
            for: LabelingModelIdentity.sharedObjectModel,
            candidates: [base, regressed]
        ))
        XCTAssertEqual(try String(contentsOf: checkpoint), "base-checkpoint")
        XCTAssertEqual(
            try store.candidateVersion(for: regressed),
            LabelingModelSemanticVersion(major: 0, minor: 2)
        )
    }

    private func candidate(
        classIdentifier: String,
        completedAt: Date,
        modelContents: String,
        checkpointContents: String? = nil,
        runsRoot: URL,
        baseRunIdentifier: String? = nil,
        classIdentifiers: [String]? = nil,
        averagePrecisionByClass: [String: Double]? = nil
    ) throws -> LabelingModelCandidate {
        let id = UUID()
        let runURL = runsRoot.appendingPathComponent(id.uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: runURL, withIntermediateDirectories: false)
        try Data(modelContents.utf8).write(to: runURL.appendingPathComponent("Detector.mlmodel"))
        if let checkpointContents {
            try Data(checkpointContents.utf8).write(
                to: runURL.appendingPathComponent("Detector.pt")
            )
        }
        let precisionByClass = averagePrecisionByClass ?? [classIdentifier: 0.5]
        let meanPrecision = precisionByClass.values.reduce(0, +)
            / Double(precisionByClass.count)
        let metrics = LabelingTrainingMetricSummary(
            isValid: true,
            meanAveragePrecision: meanPrecision,
            meanAveragePrecisionAt50PercentIOU: meanPrecision,
            averagePrecisionByClass: precisionByClass,
            averagePrecisionAt50PercentIOUByClass: precisionByClass,
            error: nil
        )
        let summary = LabelingTrainingRunSummary(
            id: id,
            datasetIdentifier: UUID(),
            classIdentifier: classIdentifier,
            completedAt: completedAt,
            maximumIterations: 10,
            gridSize: nil,
            trainingAnnotationCount: 1,
            validationAnnotationCount: 0,
            isPreliminary: true,
            modelFilename: "Detector.mlmodel",
            predictionsFilename: nil,
            trainingMetrics: metrics,
            validationMetrics: averagePrecisionByClass == nil ? nil : metrics,
            reviewMetrics: nil,
            checkpointFilename: checkpointContents == nil ? nil : "Detector.pt",
            baseRunIdentifier: baseRunIdentifier,
            classIdentifiers: classIdentifiers
        )
        return LabelingModelCandidate(
            runURL: runURL,
            datasetURL: temporaryRootForDataset(runURL),
            summary: summary,
            predictionReview: nil
        )
    }

    private func temporaryRootForDataset(_ runURL: URL) -> URL {
        runURL.deletingLastPathComponent().appendingPathComponent("dataset", isDirectory: true)
    }
}
