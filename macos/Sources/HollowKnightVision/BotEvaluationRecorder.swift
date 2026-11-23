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
    @discardableResult func start(domain: BotDomain, model: BotNativePolicy?, reference: GroundTruthLineDocument) -> Bool {
        stop()
        let id = UUID(), startedAt = ProcessInfo.processInfo.systemUptime
        let recorder = BotDemonstrationRecorder(rootURL: root.appendingPathComponent(domain.rawValue))
        let name = model?.document.modelName ?? "Built-in playing policies"
        do {
            let directory = try recorder.start(gameMode: "modded", detectorVersion: name, id: id, at: startedAt, domain: domain)
            recorder.attachEvidence(reference: reference, domain: domain, kind: "bot-evaluation")
            var metadata: [String: Any] = ["kind": "bot-evaluation", "domain": domain.rawValue,
                "modelName": name,
                "actionSource": model == nil ? "starter-policy" : "general-policy", "expertDemonstration": false]
            if let model { metadata["modelSHA256"] = model.document.sourceCheckpointSHA256 }
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
    func recordSkill(_ plan: BotSkillPlan, choice: BotSkillChoice, context: String, event: String, at time: Double) {
        struct Entry: Encodable {
            let plan: BotSkillPlan
            let choice: BotSkillChoice
            let context: String
            let event: String
            let timestamp: Double
        }
        guard let directory = lock.withLock({ session?.directory }) else { return }
        let url = directory.appendingPathComponent("skill-events.jsonl")
        do {
            var line = try JSONEncoder().encode(Entry(plan: plan, choice: choice, context: context, event: event, timestamp: time))
            line.append(0x0A)
            if !FileManager.default.fileExists(atPath: url.path) {
                try line.write(to: url, options: .atomic)
                let metadata = directory.appendingPathComponent("evaluation.json")
                if var info = try JSONSerialization.jsonObject(with: Data(contentsOf: metadata)) as? [String: Any] {
                    info["actionSource"] = "skill-plan"
                    info["skillEvents"] = url.lastPathComponent
                    try JSONSerialization.data(withJSONObject: info, options: [.sortedKeys, .prettyPrinted]).write(to: metadata, options: .atomic)
                }
                return
            }
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: line)
        } catch { NSLog("Could not record bot skill event: %@", error.localizedDescription) }
    }

    func action(_ action: BotAction, at time: Double) {
        let next = Set(RecordedGameButton.allCases.filter { action.buttons.contains($0.bridgeButton) }.map(\.rawValue))
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
