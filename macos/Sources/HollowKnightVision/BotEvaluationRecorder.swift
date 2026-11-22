import CoreGraphics
import Foundation

struct BotTerrainAuditFrame: Codable {
    let captureTimestamp: Double
    let capturedAt: Double
    let captureGeneration: UInt64
    let width: Int
    let height: Int
    let sceneName: String?
    let referenceAvailable: Bool
    let detected: [BotTerrainSegment]
    let authored: [BotTerrainSegment]
}

/// Policy play is recorded separately from expert demonstrations. The raw frame
/// archive supports offline detector replay and never becomes training labels.
final class BotEvaluationRecorder: @unchecked Sendable {
    private struct Session {
        let recorder: BotDemonstrationRecorder
        let frames: VisualReplayArchiveWriter
        let directory: URL
        let startedAt: Double
        var held = Set<String>()
    }
    private let lock = NSLock()
    private let root: URL
    private var session: Session?
    init(root: URL = BotModelStore.defaultRoot.appendingPathComponent("evaluations")) { self.root = root }
    var isRecording: Bool { lock.withLock { session != nil } }
    private(set) var lastDirectory: URL?
    @discardableResult func start(domain: BotDomain, model: BotNativePolicy, reference: GroundTruthLineDocument) -> Bool {
        stop()
        let id = UUID(), startedAt = ProcessInfo.processInfo.systemUptime
        let recorder = BotDemonstrationRecorder(rootURL: root.appendingPathComponent(domain.rawValue))
        do {
            let directory = try recorder.start(gameMode: "modded", detectorVersion: model.document.modelName, id: id, at: startedAt, domain: domain)
            recorder.attachEvidence(reference: reference, domain: domain, kind: "bot-evaluation")
            let metadata: [String: Any] = ["kind": "bot-evaluation", "domain": domain.rawValue,
                "modelName": model.document.modelName, "modelSHA256": model.document.sourceCheckpointSHA256,
                "actionSource": "general-policy", "expertDemonstration": false]
            try JSONSerialization.data(withJSONObject: metadata, options: [.prettyPrinted, .sortedKeys])
                .write(to: directory.appendingPathComponent("evaluation.json"), options: .atomic)
            let frames = VisualReplayArchiveWriter(rootURL: directory, pathID: id,
                configuration: ["kind": "bot-evaluation", "botDomain": domain.rawValue,
                    "frameSelection": "terrain-audit-cadence"], maximumPendingImages: 8)
            lock.withLock { session = Session(recorder: recorder, frames: frames, directory: directory, startedAt: startedAt); lastDirectory = directory }
            return true
        } catch { try? recorder.stop(reason: "write-error"); return false }
    }
    func stop(atlasScore: BotAtlasScore? = nil) {
        let prior = lock.withLock { let prior = session; session = nil; return prior }
        guard let prior else { return }
        let now = ProcessInfo.processInfo.systemUptime
        for button in prior.held { prior.recorder.recordInput(button: button, isPressed: false, at: now) }
        if let atlasScore {
            try? JSONEncoder().encode(atlasScore).write(to: prior.directory.appendingPathComponent("atlas-score.json"), options: .atomic)
        }
        _ = prior.frames.finish()
        try? prior.recorder.stop(reason: "user-stop", at: now)
    }
    func action(_ action: BotAction, at time: Double) {
        let next = Set(zip(["left", "right", "up", "down", "z", "x"],
            [action.values[0] == 1, action.values[0] == 2, action.values[1] == 1,
             action.values[1] == 2, action.values[2] == 1, action.values[3] == 1]).filter { $0.1 }.map { $0.0 })
        lock.withLock {
            guard var value = session else { return }
            for button in value.held.subtracting(next) { value.recorder.recordInput(button: button, isPressed: false, at: time) }
            for button in next.subtracting(value.held) { value.recorder.recordInput(button: button, isPressed: true, at: time) }
            value.held = next; session = value
        }
    }
    func observe(_ observation: BotObservation, image: CGImage) { lock.withLock { session?.recorder }?.record(observation, image: image) }
    func audit(_ frame: BotTerrainAuditFrame, image: CGImage, solveWidth: Double) {
        let current = lock.withLock { session }
        guard let current, frame.capturedAt >= current.startedAt else { return }
        current.recorder.recordTerrainAudit(frame)
        // Archive the exact images used by the visual ground analysis at its
        // audit cadence, rather than saturating disk with 60 Hz duplicate frames.
        current.frames.append(image, captureTimestamp: frame.captureTimestamp, capturedAt: frame.capturedAt,
            captureGeneration: frame.captureGeneration, solveWidth: solveWidth,
            renderedGameFrame: RenderedFrameMarker.decode(image))
    }
    func groundInput(_ input: VisualReplayGroundInput) { lock.withLock { session?.frames }?.recordGroundInput(input) }
    func stage(_ stage: VisualReplayStageEvent.Stage, timestamp: Double, generation: UInt64) {
        lock.withLock { session?.frames }?.recordStage(stage, captureTimestamp: timestamp, captureGeneration: generation)
    }
}
