import XCTest
@testable import HollowKnightVision

final class BotModelStoreTests: XCTestCase {
    private func makeVersion(_ store: BotModelStore, name: String) throws -> URL {
        let root = try store.directory(named: name)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let checkpoint = Data(name.utf8), digest = BotNativePolicy.sha256(checkpoint)
        let document = BotRuntimePolicyDocument(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: name, featureNames: BotObservationContract.legacyFeatureNames, historyLength: 4,
            actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: digest,
            layers: [BotRuntimeLayer(inputSize: 576, outputSize: 10, activation: "linear",
                weights: .init(repeating: 0, count: 5760), biases: .init(repeating: 0, count: 10))])
        let raw = try JSONEncoder().encode(document)
        try checkpoint.write(to: root.appendingPathComponent("model.zip"))
        try raw.write(to: root.appendingPathComponent("runtime-policy.json"))
        try JSONSerialization.data(withJSONObject: ["schemaVersion": 1, "policyFile": "runtime-policy.json",
            "policySHA256": BotNativePolicy.sha256(raw), "sourceCheckpointSHA256": digest])
            .write(to: root.appendingPathComponent("runtime-manifest.json"))
        try JSONSerialization.data(withJSONObject: ["name": name]).write(to: root.appendingPathComponent("manifest.json"))
        return root
    }

    private func writeCompletedTraining(to directory: URL, id: String, createdAt: String, accuracy: Double,
                                        baselineSHA256: String? = nil) throws {
        let score: [String: Any] = ["negativeLogLikelihood": 0.5, "exactActionAccuracy": accuracy, "heads": []]
        var result: [String: Any] = [
            "id": id, "name": "Candidate", "modelDirectory": directory.lastPathComponent, "createdAt": createdAt,
            "trainingExamples": 100, "validationExamples": 20, "candidate": score, "baseline": score,
            "eligibleForActivation": false, "reason": "Below baseline", "nativeParity": true
        ]
        result["baselineSHA256"] = baselineSHA256
        try JSONSerialization.data(withJSONObject: result).write(to: directory.appendingPathComponent("training-result.json"))
    }

    func testPlaybackSelectsVerifiedLatestDespiteLowerScoreAndRemembersRollback() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = BotModelStore(root: root)
        let original = try makeVersion(store, name: "v1"), candidate = try makeVersion(store, name: "bot-test")
        XCTAssertEqual(try store.activeDirectory(), original)
        try writeCompletedTraining(to: candidate, id: "test", createdAt: "2026-11-18", accuracy: 0.8,
                                   baselineSHA256: "older-comparison-baseline")
        let version = try XCTUnwrap(store.versions().first { $0.id == "bot-test" })
        XCTAssertTrue(store.canActivate(version))
        try Data("corrupt".utf8).write(to: candidate.appendingPathComponent("model.zip"))
        XCTAssertThrowsError(try store.activate(version))
        XCTAssertEqual(try store.playbackDirectory(), original)
        XCTAssertEqual(try store.activeDirectory(), original)
        try Data("bot-test".utf8).write(to: candidate.appendingPathComponent("model.zip"))
        XCTAssertEqual(try store.playbackDirectory(), candidate)
        XCTAssertEqual(try store.activeDirectory(), candidate)
        try store.restorePrevious()
        XCTAssertEqual(try store.activeDirectory(), original)
        let reopened = BotModelStore(root: root)
        XCTAssertEqual(try reopened.playbackDirectory(), original)
        let next = try makeVersion(store, name: "bot-next")
        try writeCompletedTraining(to: next, id: "next", createdAt: "2026-11-19", accuracy: 0.8)
        XCTAssertEqual(try reopened.playbackDirectory(), next)
        XCTAssertEqual(try reopened.selection().previousDirectory, "v1")
        XCTAssertEqual(try Data(contentsOf: original.appendingPathComponent("model.zip")), Data("v1".utf8))
    }

    func testRollbackAndPlaybackUseNewestIntactCheckpoint() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = BotModelStore(root: root)
        let original = try makeVersion(store, name: "v1")
        let intact = try makeVersion(store, name: "bot-intact")
        let newest = try makeVersion(store, name: "bot-newest")
        try writeCompletedTraining(to: intact, id: "intact", createdAt: "2026-11-21", accuracy: 0.8)
        try writeCompletedTraining(to: newest, id: "newest", createdAt: "2026-11-22", accuracy: 0.9)
        try Data("interrupted copy".utf8).write(to: newest.appendingPathComponent("model.zip"))

        XCTAssertEqual(try store.playbackDirectory(), intact)
        try store.restorePrevious()
        XCTAssertEqual(try store.playbackDirectory(), original)
        XCTAssertEqual(try store.selection().manualChoiceThrough, "bot-intact")
        try Data("invalidated rollback".utf8).write(to: original.appendingPathComponent("runtime-policy.json"))
        XCTAssertEqual(try store.playbackDirectory(), intact)
        try Data("bot-newest".utf8).write(to: newest.appendingPathComponent("model.zip"))
        XCTAssertEqual(try store.playbackDirectory(), newest)
    }

    func testPlaybackAcceptsVerifiedCheckpointWithPartialValidationScores() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = BotModelStore(root: root, domain: .gameplay)
        _ = try makeVersion(store, name: "v1")
        let trained = try makeVersion(store, name: "bot-trained")
        var report: [String: Any] = [
            "id": NSNull(), "modelDirectory": "bot-trained", "createdAt": "2026-11-22T01:00:00Z",
            "botDomain": "gameplay", "nativeParity": true, "eligibleForActivation": false
        ]
        func catalogEntry() throws -> BotModelVersion {
            try JSONSerialization.data(withJSONObject: report).write(to: trained.appendingPathComponent("training-result.json"))
            return try XCTUnwrap(store.versions().first { $0.id == "bot-trained" })
        }
        XCTAssertEqual(try catalogEntry().result?.scores, [])
        report["candidate"] = ["exactActionAccuracy": 0.72, "heads": [NSNull(), ["name": "jump", "balancedAccuracy": 0.65]]]
        report["baseline"] = ["exactActionAccuracy": 0.58, "heads": [["name": "jump", "balancedAccuracy": 0.42]]]
        report["candidateSequential"] = ["exactActionAccuracy": 0.69]
        report["baselineSequential"] = ["exactActionAccuracy": 0.53]
        XCTAssertEqual(try catalogEntry().result?.scores, [
            .init(label: "Action agreement", value: 0.72, baseline: 0.58),
            .init(label: "Jump balanced accuracy", value: 0.65, baseline: 0.42),
            .init(label: "Own-action agreement", value: 0.69, baseline: 0.53)
        ])

        report["candidate"] = ["exactActionAccuracy": 0.87, "heads": [NSNull(), ["name": "jump", "balancedAccuracy": 1.5]]]
        report["baseline"] = true
        report["candidateSequential"] = ["exactActionAccuracy": "invalid"]
        let version = try catalogEntry()
        XCTAssertEqual(version.result?.scores, [.init(label: "Action agreement", value: 0.87, baseline: nil)])
        XCTAssertTrue(store.canActivate(version))
        XCTAssertEqual(try store.playbackDirectory(), trained)

        report["candidate"] = ["exactActionAccuracy": true, "heads": [["name": "jump", "balancedAccuracy": 0]]]
        report["baseline"] = ["heads": [["name": "jump", "balancedAccuracy": 1]]]
        XCTAssertEqual(try catalogEntry().result?.scores, [.init(label: "Jump balanced accuracy", value: 0, baseline: 1)])
    }

    func testPartialReportsStillRequireMatchingDomainDirectoryAndNativeParity() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = BotModelStore(root: root, domain: .hacker)
        let original = try makeVersion(store, name: "v1")
        let candidate = try makeVersion(store, name: "bot-next")
        let matching: [String: Any] = ["modelDirectory": "bot-next", "createdAt": "2026-11-22", "nativeParity": true]
        for invalid: [String: Any] in [["botDomain": "gameplay"], ["botDomain": "unknown"],
                                       ["modelDirectory": "bot-other"], ["nativeParity": false], ["nativeParity": "true"]] {
            let report = matching.merging(invalid) { _, new in new }
            try JSONSerialization.data(withJSONObject: report).write(to: candidate.appendingPathComponent("training-result.json"))
            XCTAssertEqual(try store.playbackDirectory(), original)
        }
        try JSONSerialization.data(withJSONObject: matching).write(to: candidate.appendingPathComponent("training-result.json"))
        try JSONEncoder().encode(["botDomain": "gameplay"]).write(to: candidate.appendingPathComponent("manifest.json"))
        XCTAssertFalse(try store.versions().contains { $0.id == "bot-next" })
        XCTAssertEqual(try store.playbackDirectory(), original)
    }

    func testSelectionCannotEscapeModelsDirectoryAndPartialRunsAreHidden() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = BotModelStore(root: root)
        _ = try makeVersion(store, name: "v1"); _ = try makeVersion(store, name: "bot-partial")
        XCTAssertEqual(try store.versions().map(\.id), ["v1"])
        for name in ["..", "../outside", "/tmp/model", ""] { XCTAssertThrowsError(try store.directory(named: name)) }
        try JSONEncoder().encode(BotModelStore.Selection(directory: "../outside", previousDirectory: nil))
            .write(to: store.selectionURL)
        XCTAssertThrowsError(try store.activeDirectory())
    }

    @MainActor
    func testNewClipBadgeTracksCompletedTrainingIndependentlyOfPlayback() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let recorder = BotDemonstrationRecorder(rootURL: root.appendingPathComponent("demonstrations"))
        let workspace = BotTrainingWorkspaceController(root: root)
        func capture(_ id: UUID, domain: BotDomain, replay: UUID) throws {
            _ = try recorder.start(gameMode: "modded", detectorVersion: "test", id: id, at: 100, domain: domain, replayID: replay)
            recorder.record(BotObservation(type: "observation", captureTimestamp: 100.2, capturedAt: 100.2,
                availableAt: 100.21, generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: 100.2,
                detectionFrameID: 1, modelVersion: nil, bestKnightConfidence: nil,
                features: .init(repeating: 0, count: BotObservationContract.featureCount), issues: []), image: nil)
            _ = try recorder.stop(reason: "user-stop", at: 101)
        }
        func refresh(_ workspace: BotTrainingWorkspaceController) async throws {
            workspace.refresh()
            for _ in 0..<500 {
                if !workspace.hacker.isRefreshing && !workspace.gameplay.isRefreshing { return }
                try await Task.sleep(nanoseconds: 10_000_000)
            }
            XCTFail("Timed out loading clips")
        }
        let hacker = UUID(), visual = UUID()
        try capture(hacker, domain: .hacker, replay: hacker)
        try capture(visual, domain: .gameplay, replay: hacker)
        try await refresh(workspace)
        XCTAssertEqual(workspace.hacker.newClipCount, 1)
        XCTAssertEqual(workspace.gameplay.newClipCount, 1)
        XCTAssertEqual(workspace.newClipCount, 1)
        XCTAssertEqual(BotModelTab.summary.newCaptureLabel(workspace.newClipCount), "1 new run")
        XCTAssertEqual(BotModelTab.gameplay.newCaptureLabel(workspace.gameplay.newClipCount), "1 new Gameplay clip")
        XCTAssertEqual(BotModelTab.hacker.newCaptureLabel(workspace.hacker.newClipCount), "1 new Hacker clip")

        for (controller, clip) in [(workspace.hacker, hacker), (workspace.gameplay, visual)] {
            _ = try makeVersion(controller.store, name: "v1")
            let candidate = try makeVersion(controller.store, name: "bot-next")
            try JSONEncoder().encode(["validationSessions": [clip.uuidString.lowercased()]])
                .write(to: candidate.appendingPathComponent("manifest.json"))
            controller.refreshVersions()
            XCTAssertEqual(controller.newClipCount, 1, "A partial run must leave the clip pending")
            try writeCompletedTraining(to: candidate, id: "next", createdAt: "2026-11-18", accuracy: 0.5)
            controller.refreshVersions()
            XCTAssertEqual(controller.activeID, "v1")
            XCTAssertEqual(controller.newClipCount, 0)
            XCTAssertEqual(workspace.newClipCount, controller.store.domain == .hacker ? 1 : 0)
        }
        XCTAssertEqual(BotModelTab.summary.newCaptureLabel(workspace.newClipCount), "0 new runs")
        XCTAssertEqual(BotModelTab.hacker.newCaptureLabel(workspace.hacker.newClipCount), "0 new Hacker clips")
        let candidate = try XCTUnwrap(workspace.gameplay.versions.first)
        var reloads = 0
        for controller in [workspace.gameplay, workspace.hacker] {
            controller.useTrainedVersion(runID: "missing") { XCTFail("An incomplete run cannot reload playback") }
            XCTAssertEqual(controller.activeID, "v1")
            controller.useTrainedVersion(runID: "next") { reloads += 1 }
            XCTAssertEqual(controller.activeID, candidate.id, "A lower comparison score must still activate the new export")
            XCTAssertEqual(try controller.store.activeDirectory().lastPathComponent, candidate.id)
        }
        XCTAssertEqual(reloads, 2)
        workspace.gameplay.restore(reload: {})
        XCTAssertEqual(workspace.gameplay.activeID, "v1")
        XCTAssertEqual(workspace.hacker.activeID, candidate.id)
        XCTAssertEqual(workspace.newClipCount, 0)

        let next = UUID()
        try capture(next, domain: .hacker, replay: next)
        try capture(UUID(), domain: .gameplay, replay: next)
        let reopened = BotTrainingWorkspaceController(root: root)
        try await refresh(reopened)
        XCTAssertEqual(reopened.hacker.newClipCount, 1)
        XCTAssertEqual(reopened.gameplay.newClipCount, 1)
        XCTAssertEqual(reopened.newClipCount, 1)
    }
}
