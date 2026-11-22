import AppKit
import XCTest
@testable import HollowKnightVision

final class BotPairedRecordingTests: XCTestCase {
    private func observation(_ time: Double, domain: BotDomain, missing: Bool = false) -> BotObservation {
        var features = Array(repeating: 0.0, count: BotObservationContract.featureCount)
        features[4] = missing ? 0 : 1
        features[6] = 1
        features[144] = 1
        features[domain == .hacker ? 149 : 154] = 1
        return .init(type: "observation", captureTimestamp: time, capturedAt: time,
            availableAt: time + 0.01, generation: 1, roomID: domain == .hacker ? 1000000 : 1,
            roomRevision: 0, detectionTimestamp: time, detectionFrameID: 2,
            modelVersion: domain.rawValue, bestKnightConfidence: nil,
            features: features, issues: missing ? ["missing-knight"] : [],
            terrainSource: domain == .hacker ? "human-authored" : "detected",
            knightSource: missing ? nil : domain == .hacker ? "hacker-frame" : "visual")
    }

    @MainActor
    func testOneManualRunProducesIndependentClipsWithIdenticalControlsAndSharedReplay() throws {
        _ = NSApplication.shared
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let demos = root.appendingPathComponent("demonstrations")
        let replay = root.appendingPathComponent("replays")
        try FileManager.default.createDirectory(at: replay, withIntermediateDirectories: true)
        let controller = BotDemonstrationController(recorder: BotDemonstrationRecorder(rootURL: demos))
        for deleteHackerFirst in [true, false] {
            let run = UUID(), start = ProcessInfo.processInfo.systemUptime - 1
            XCTAssertTrue(controller.start(gameMode: "modded", detectorVersion: "receiver", id: run, at: start,
                requiresUsableObservation: false, domain: .hacker, pairedGameplayDetector: "visual-model"))
            XCTAssertEqual(controller.pairedRecordingID, run)
            controller.attachEvidence(reference: GroundTruthLineDocument(), domain: .hacker, replayRoot: replay)
            controller.recordInput(button: "right", isPressed: true, at: start + 0.05)
            let hackerFrame = observation(start + 0.1, domain: .hacker)
            let visualFrame = observation(start + 0.1, domain: .gameplay)
            let missing = observation(start + 0.22, domain: .gameplay, missing: true)
            if deleteHackerFirst {
                controller.observe(hackerFrame, image: nil)
                controller.recordPairedObservation(visualFrame, image: nil, run: run)
                controller.recordPairedObservation(missing, image: nil, run: run)
            } else {
                controller.observe(visualFrame, image: nil)
                controller.observe(missing, image: nil)
                controller.recordPairedObservation(hackerFrame, image: nil, run: run)
            }
            for domain in BotDomain.allCases {
                controller.recordPairedObservation(observation(start + 0.4, domain: domain), image: nil, run: UUID())
            }
            controller.recordInput(button: "right", isPressed: false, at: start + 0.35)
            let published = expectation(description: "both saved clips are published")
            let observer = NotificationCenter.default.addObserver(forName: .botTrainingCapturePublished, object: nil, queue: nil) { notification in
                guard let directories = notification.object as? [URL] else { return }
                XCTAssertEqual(directories.count, 2)
                for directory in directories {
                    XCTAssertTrue(FileManager.default.fileExists(atPath: directory.appendingPathComponent("manifest.json").path))
                }
                published.fulfill()
            }
            controller.stop()
            NotificationCenter.default.removeObserver(observer)
            wait(for: [published], timeout: 1)
            XCTAssertNil(controller.pairedRecordingID)
            let hacker = try BotSnippet.load(XCTUnwrap(controller.lastSessionURL))
            let visualID = try XCTUnwrap(hacker.manifest.pairedClipID)
            let visual = try BotSnippet.load(demos.appendingPathComponent("demo-\(visualID.uuidString.lowercased())"))
            XCTAssertEqual(visual.manifest.pairedClipID, hacker.id)
            XCTAssertNotEqual(hacker.id, visual.id)
            XCTAssertTrue(hacker.canTrain && visual.canTrain)
            XCTAssertEqual(hacker.domain, .hacker); XCTAssertEqual(visual.domain, .gameplay)
            XCTAssertEqual(hacker.manifest.detectorVersion, "receiver")
            XCTAssertEqual(visual.manifest.detectorVersion, "visual-model")
            XCTAssertEqual(hacker.manifest.startedAt, visual.manifest.startedAt)
            XCTAssertEqual(hacker.manifest.duration, visual.manifest.duration)
            XCTAssertEqual(hacker.observations.map(\.knightSource), ["hacker-frame"])
            XCTAssertEqual(visual.observations.map(\.knightSource), ["visual", nil])
            XCTAssertEqual(visual.observations.last?.issues, ["missing-knight"])
            XCTAssertEqual(hacker.observations.first?.features[149], 1)
            XCTAssertEqual(visual.observations.first?.features[154], 1)
            for clip in [visual, hacker] {
                XCTAssertEqual(clip.manifest.sourceReplayID, run)
                XCTAssertEqual(clip.inputs.map(\.timestamp), [start + 0.05, start + 0.35])
                XCTAssertEqual(clip.inputs.map(\.isPressed), [true, false])
                let data = try Data(contentsOf: clip.directory.appendingPathComponent("audit-context.json"))
                let context = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
                XCTAssertEqual(context["pathID"] as? String, run.uuidString.lowercased())
            }
            let path = replay.appendingPathComponent("path-test-\(run.uuidString.lowercased()).json")
            try Data().write(to: path)
            try BotClipDeletion.delete(snippet: deleteHackerFirst ? hacker : visual, root: root, replayRoot: replay)
            XCTAssertTrue(FileManager.default.fileExists(atPath: path.path))
            try BotClipDeletion.delete(snippet: deleteHackerFirst ? visual : hacker, root: root, replayRoot: replay)
            XCTAssertFalse(FileManager.default.fileExists(atPath: path.path))
        }
        XCTAssertTrue(controller.start(gameMode: "modded", detectorVersion: "visual", requiresUsableObservation: false, domain: .gameplay))
        XCTAssertNil(controller.pairedRecordingID)
        controller.stop(reason: "focus-lost")
        XCTAssertNil(controller.lastSessionURL)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: demos.path).isEmpty)
    }

    @MainActor
    func testDiscardInterruptedPairAndKeepOnlyTheEligibleSideOfACompletedRun() throws {
        _ = NSApplication.shared
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let controller = BotDemonstrationController(recorder: BotDemonstrationRecorder(rootURL: root))
        func beginPair() -> (UUID, Double) {
            let run = UUID(), start = ProcessInfo.processInfo.systemUptime - 1
            XCTAssertTrue(controller.start(gameMode: "modded", detectorVersion: "receiver", id: run, at: start,
                requiresUsableObservation: false, domain: .hacker, pairedGameplayDetector: "visual"))
            return (run, start)
        }
        let (first, time) = beginPair()
        let pending = try XCTUnwrap(controller.lastSessionURL)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        controller.observe(observation(time + 0.1, domain: .hacker), image: nil)
        controller.recordPairedObservation(observation(time + 0.1, domain: .gameplay), image: nil, run: first)
        controller.stop(reason: "focus-lost")
        XCTAssertNil(controller.lastSessionURL)
        XCTAssertFalse(FileManager.default.fileExists(atPath: pending.path))
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)

        let (second, next) = beginPair()
        var occluded = observation(next + 0.1, domain: .gameplay, missing: true)
        occluded.features[20] = 1
        controller.recordPairedObservation(occluded, image: nil, run: second)
        controller.stop()
        let kept = try BotSnippet.load(XCTUnwrap(controller.lastSessionURL))
        XCTAssertEqual(kept.domain, .gameplay)
        XCTAssertTrue(kept.canTrain)
        XCTAssertEqual(kept.manifest.usableObservationCount, 0)
        XCTAssertEqual(kept.maskedKnightFrames, [0])
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [kept.directory.lastPathComponent])

        _ = beginPair()
        controller.stop()
        XCTAssertNil(controller.lastSessionURL)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [kept.directory.lastPathComponent])
    }

}
