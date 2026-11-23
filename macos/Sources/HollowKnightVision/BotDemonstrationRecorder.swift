import AppKit
import Combine
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct BotDemonstrationManifest: Codable {
    let schemaVersion: Int
    let id: UUID
    let createdAt: Date
    let startedAt: Double
    let gameMode: String
    let detectorVersion: String
    let inputSource: String
    let featureNames: [String]
    let actionSizes: [Int]
    let historyLength: Int
    let sampleInterval: Double
    var status: String
    var stopReason: String?
    var duration: Double
    var observationCount: Int
    var usableObservationCount: Int
    var inputEventCount: Int
    var imageCount: Int
    var recordedSkillControls: Bool? = nil
    var botDomain: BotDomain? = nil
    var replayID: UUID? = nil
    var pairedClipID: UUID? = nil
    var sourceReplayID: UUID { replayID ?? id }
    var terrainAuditCount: Int? = nil
    var terrainPairedFrameCount: Int? = nil

    func canTrain(hasOccludedKnightFrames: Bool) -> Bool {
        status == "completed" && (usableObservationCount > 0 || hasOccludedKnightFrames)
    }
}

struct BotDemonstrationInputEvent: Encodable {
    let type = "input"
    let timestamp: Double
    let button: String
    let isPressed: Bool
}

/// Training captures stream to a temporary directory on the library volume.
/// Only a finished, eligible clip is published to the training library.
final class BotDemonstrationRecorder {
    static var defaultRootURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HollowKnightVision/bot-v1/demonstrations", isDirectory: true)
    }
    let rootURL: URL
    private let queue = DispatchQueue(label: "com.ballroller.hollow-knight-vision.bot-demonstrations", qos: .utility)
    private var manifest: BotDemonstrationManifest?
    private var sessionURL: URL?
    private var trainingDestination: URL?
    private var handle: FileHandle?
    private var auditHandle: FileHandle?
    private var lastSampleAt = -Double.infinity
    private var lastImageAt = -Double.infinity
    private var hasOccludedKnightFrames = false
    private var held = Set<String>()
    private(set) var failure: String?

    init(rootURL: URL = defaultRootURL) { self.rootURL = rootURL }

    @discardableResult
    func start(gameMode: String, detectorVersion: String, id: UUID = UUID(),
               at timestamp: Double = ProcessInfo.processInfo.systemUptime, domain: BotDomain? = nil,
               replayID: UUID? = nil, pairedClipID: UUID? = nil, forTraining: Bool = false, skillControls: Bool = false) throws -> URL {
        try queue.sync {
            guard manifest == nil else { throw CocoaError(.fileWriteFileExists) }
            let destination = rootURL.appendingPathComponent("demo-\(id.uuidString.lowercased())", isDirectory: true)
            let manager = FileManager.default
            try manager.createDirectory(at: rootURL, withIntermediateDirectories: true)
            let url: URL
            if forTraining {
                guard !manager.fileExists(atPath: destination.path) else { throw CocoaError(.fileWriteFileExists) }
                url = try manager.url(for: .itemReplacementDirectory, in: .userDomainMask,
                                      appropriateFor: destination, create: true)
            } else {
                url = destination
                try manager.createDirectory(at: url, withIntermediateDirectories: false)
            }
            var started = false
            defer {
                if !started {
                    try? handle?.close(); try? auditHandle?.close()
                    handle = nil; auditHandle = nil; manifest = nil; sessionURL = nil
                    try? manager.removeItem(at: url)
                }
            }
            try manager.createDirectory(at: url.appendingPathComponent("frames"), withIntermediateDirectories: false)
            let events = url.appendingPathComponent("events.jsonl")
            guard FileManager.default.createFile(atPath: events.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
            handle = try FileHandle(forWritingTo: events)
            let auditURL = url.appendingPathComponent("terrain-audit.jsonl")
            FileManager.default.createFile(atPath: auditURL.path, contents: nil)
            auditHandle = try FileHandle(forWritingTo: auditURL)
            sessionURL = url
            failure = nil
            held = []
            hasOccludedKnightFrames = false
            lastSampleAt = -.infinity
            lastImageAt = -.infinity
            manifest = BotDemonstrationManifest(schemaVersion: BotObservationContract.schemaVersion,
                id: id, createdAt: Date(), startedAt: timestamp, gameMode: gameMode,
                detectorVersion: detectorVersion,
                inputSource: gameMode == "normal" ? "native-posted" : "receiver-sent",
                featureNames: BotObservationContract.featureNames, actionSizes: BotObservationContract.actionSizes,
                historyLength: BotObservationContract.historyLength, sampleInterval: BotObservationContract.sampleInterval,
                status: "recording", duration: 0, observationCount: 0, usableObservationCount: 0,
                inputEventCount: 0, imageCount: 0, recordedSkillControls: skillControls, botDomain: domain, replayID: replayID, pairedClipID: pairedClipID, terrainAuditCount: 0, terrainPairedFrameCount: 0)
            try writeManifest()
            trainingDestination = forTraining ? destination : nil
            started = true
            return url
        }
    }

    func recordInput(button: String, isPressed: Bool, at timestamp: Double) {
        queue.async { [self] in
            guard let current = manifest, failure == nil, timestamp.isFinite,
                  timestamp >= current.startedAt else { return }
            if isPressed {
                guard held.insert(button).inserted else { return }
            } else {
                guard held.remove(button) != nil else { return }
            }
            do {
                try append(BotDemonstrationInputEvent(timestamp: timestamp, button: button, isPressed: isPressed))
                manifest?.inputEventCount += 1
            } catch { failure = error.localizedDescription }
        }
    }

    func record(_ observation: BotObservation, image: CGImage?) {
        queue.async { [self] in
            guard let current = manifest, failure == nil,
                  observation.capturedAt >= current.startedAt, observation.availableAt >= current.startedAt,
                  observation.captureTimestamp - lastSampleAt >= BotObservationContract.sampleInterval - 0.005 else { return }
            lastSampleAt = observation.captureTimestamp
            var recorded = observation
            do {
                if let image, let sessionURL,
                   observation.availableAt - lastImageAt >= 1 {
                    let path = "frames/\(current.observationCount).jpg"
                    guard let destination = CGImageDestinationCreateWithURL(
                        sessionURL.appendingPathComponent(path) as CFURL, UTType.jpeg.identifier as CFString, 1, nil)
                    else { throw CocoaError(.fileWriteUnknown) }
                    CGImageDestinationAddImage(destination, image,
                        [kCGImageDestinationLossyCompressionQuality: 0.75] as CFDictionary)
                    guard CGImageDestinationFinalize(destination) else { throw CocoaError(.fileWriteUnknown) }
                    recorded.imagePath = path
                    lastImageAt = observation.availableAt
                    manifest?.imageCount += 1
                }
                try append(recorded)
                manifest?.observationCount += 1
                if observation.isUsable { manifest?.usableObservationCount += 1 }
                else if !hasOccludedKnightFrames, BotKnightOcclusionWindow.deadline(for: observation) != nil {
                    hasOccludedKnightFrames = true
                }
                if manifest!.observationCount.isMultiple(of: 20) {
                    try handle?.synchronize()
                    try writeManifest()
                }
            } catch { failure = error.localizedDescription }
        }
    }

    func summary() -> (BotDemonstrationManifest?, String?) {
        queue.sync { (manifest, failure) }
    }

    func attachEvidence(reference: GroundTruthLineDocument, domain: BotDomain, kind: String, replayRoot: URL? = nil) {
        queue.async { [self] in
            guard let sessionURL, manifest != nil else { return }
            do {
                let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
                let referenceData = try encoder.encode(reference)
                try referenceData.write(to: sessionURL.appendingPathComponent("authored-ground.json"), options: .atomic)
                var context: [String: Any] = ["schemaVersion": 1, "domain": domain.rawValue, "kind": kind,
                    "pathID": manifest!.sourceReplayID.uuidString.lowercased(), "referenceCoverage": "positive-authored-segments-only",
                    "authoredGroundSHA256": BotNativePolicy.sha256(referenceData)]
                if let replayRoot { context["replayRoot"] = replayRoot.path }
                try JSONSerialization.data(withJSONObject: context, options: [.prettyPrinted, .sortedKeys])
                    .write(to: sessionURL.appendingPathComponent("audit-context.json"), options: .atomic)
            } catch { failure = error.localizedDescription }
        }
    }

    func recordTerrainAudit(_ frame: BotTerrainAuditFrame) {
        queue.async { [self] in
            guard let current = manifest, failure == nil, frame.capturedAt >= current.startedAt else { return }
            do {
                var data = try JSONEncoder().encode(frame); data.append(0x0A)
                try auditHandle?.write(contentsOf: data)
                let count = manifest?.terrainAuditCount ?? 0
                manifest?.terrainAuditCount = count + 1
                if frame.referenceAvailable {
                    let paired = manifest?.terrainPairedFrameCount ?? 0
                    manifest?.terrainPairedFrameCount = paired + 1
                }
            } catch { failure = error.localizedDescription }
        }
    }

    @discardableResult
    func stop(reason: String, at timestamp: Double = ProcessInfo.processInfo.systemUptime) throws -> URL? {
        try queue.sync {
            guard var current = manifest, let url = sessionURL else { return nil }
            defer {
                try? handle?.close(); try? auditHandle?.close()
                handle = nil; auditHandle = nil; manifest = nil; sessionURL = nil; trainingDestination = nil; held = []
            }
            current.duration = max(0, timestamp - current.startedAt)
            current.status = failure == nil ? (reason == "user-stop" ? "completed" : "interrupted") : "failed"
            current.stopReason = failure ?? reason
            manifest = current
            let keep = trainingDestination == nil || current.canTrain(hasOccludedKnightFrames: hasOccludedKnightFrames)
            let manager = FileManager.default
            do {
                if keep {
                    try handle?.synchronize()
                    try auditHandle?.synchronize()
                    try writeManifest()
                }
                if let failure { throw NSError(domain: "HollowKnightBot", code: 1,
                                               userInfo: [NSLocalizedDescriptionKey: failure]) }
                if let trainingDestination {
                    guard keep else {
                        try manager.removeItem(at: url)
                        return nil
                    }
                    try manager.moveItem(at: url, to: trainingDestination)
                    return trainingDestination
                }
                return url
            } catch {
                if trainingDestination != nil, manager.fileExists(atPath: url.path) {
                    try manager.removeItem(at: url)
                }
                throw error
            }
        }
    }

    private func append<T: Encodable>(_ value: T) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        var data = try encoder.encode(value)
        data.append(0x0A)
        try handle?.write(contentsOf: data)
    }

    private func writeManifest() throws {
        guard let manifest, let sessionURL else { return }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try encoder.encode(manifest).write(to: sessionURL.appendingPathComponent("manifest.json"), options: .atomic)
    }
}

extension Notification.Name {
    static let botTrainingCapturePublished = Notification.Name("BotTrainingCapturePublished")
}

/// UI lifecycle stays on the main thread; frame/input evidence stays on the
/// recorder's serial queue. Preview readiness never blocks capture on disk IO.
final class BotDemonstrationController: ObservableObject {
    @Published private(set) var isRecording = false
    @Published private(set) var observationCount = 0
    @Published private(set) var usableCount = 0
    @Published private(set) var pairedGameplayUsableCount: Int?
    @Published private(set) var inputCount = 0
    @Published private(set) var elapsed: Double = 0
    @Published private(set) var readiness = "Waiting for fresh gameplay detections."
    @Published private(set) var canRecord = false
    @Published private(set) var issue: String?
    @Published private(set) var lastSessionURL: URL?
    private let recorder: BotDemonstrationRecorder
    private let gameplayRecorder: BotDemonstrationRecorder
    private var primaryDomain: BotDomain?
    private var pairedRun: UUID?
    var pairedRecordingID: UUID? { lock.withLock { pairedRun } }
    private let lock = NSLock()
    private var latest: BotObservation?
    private var lastPreviewAt = -Double.infinity
    private var collecting = false
    private var timer: Timer?
    private var focusObserver: NSObjectProtocol?
    var onStop: ((String) -> Void)?

    init(recorder: BotDemonstrationRecorder = BotDemonstrationRecorder()) {
        self.recorder = recorder
        gameplayRecorder = BotDemonstrationRecorder(rootURL: recorder.rootURL)
    }

    func observe(_ observation: BotObservation, image: CGImage?) {
        lock.lock()
        latest = observation
        if collecting { recordForDomain(observation, image: image) }
        let publish = observation.availableAt - lastPreviewAt >= 0.5
        if publish { lastPreviewAt = observation.availableAt }
        lock.unlock()
        if publish { DispatchQueue.main.async { [weak self] in self?.refresh() } }
    }

    func recordInput(button: String, isPressed: Bool, at timestamp: Double) {
        lock.withLock {
            guard collecting else { return }
            recorder.recordInput(button: button, isPressed: isPressed, at: timestamp)
            if pairedRun != nil { gameplayRecorder.recordInput(button: button, isPressed: isPressed, at: timestamp) }
        }
    }

    // Caller holds the controller lock, keeping routing and session changes atomic.
    private func recordForDomain(_ observation: BotObservation, image: CGImage?) {
        let domain = BotDomain.of(observation)
        if primaryDomain == nil || primaryDomain == domain {
            recorder.record(observation, image: image)
        } else if pairedRun != nil, domain == .gameplay {
            gameplayRecorder.record(observation, image: image)
        }
    }

    func recordPairedObservation(_ observation: BotObservation, image: CGImage?, run: UUID) {
        lock.withLock {
            guard pairedRun == run else { return }
            recordForDomain(observation, image: image)
        }
    }

    @discardableResult
    func start(gameMode: String, detectorVersion: String, id: UUID = UUID(),
               at timestamp: Double = ProcessInfo.processInfo.systemUptime,
               requiresUsableObservation: Bool = true, domain: BotDomain? = nil,
               pairedGameplayDetector: String? = nil, skillControls: Bool = false) -> Bool {
        refresh()
        guard !isRecording, !requiresUsableObservation || canRecord else { return false }
        do {
            let visualID = domain == .hacker && pairedGameplayDetector != nil ? UUID() : nil
            lastSessionURL = try lock.withLock {
                let primary = try recorder.start(gameMode: gameMode, detectorVersion: detectorVersion, id: id,
                    at: timestamp, domain: domain, replayID: visualID == nil ? nil : id, pairedClipID: visualID, forTraining: true, skillControls: skillControls)
                if let visualID, let pairedGameplayDetector {
                    do {
                        try gameplayRecorder.start(gameMode: gameMode, detectorVersion: pairedGameplayDetector,
                            id: visualID, at: timestamp, domain: .gameplay, replayID: id, pairedClipID: id, forTraining: true, skillControls: skillControls)
                    } catch {
                        _ = try recorder.stop(reason: "paired-start-failed", at: timestamp)
                        throw error
                    }
                }
                primaryDomain = domain
                pairedRun = visualID == nil ? nil : id
                collecting = true
                return primary
            }
            pairedGameplayUsableCount = visualID == nil ? nil : 0
            isRecording = true
            issue = nil
            observationCount = 0; usableCount = 0; inputCount = 0; elapsed = 0
            timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.refresh() }
            // A popover can still own the key window during its dismissal
            // animation. Bind to the dashboard, whose focus we actually need.
            if let window = NSApp.mainWindow ?? NSApp.windows.first(where: { $0 is NormalVisionPanel && $0.isVisible }) {
                focusObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                    object: window, queue: .main) { [weak self] _ in self?.stop(reason: "focus-lost") }
            }
            return true
        } catch { issue = "Could not start demonstration: \(error.localizedDescription)"; return false }
    }

    func stop(reason: String = "user-stop") {
        guard isRecording else { return }
        let hadPair = lock.withLock {
            let active = pairedRun != nil
            collecting = false; pairedRun = nil; primaryDomain = nil
            return active
        }
        let stoppedAt = ProcessInfo.processInfo.systemUptime
        let (summary, _) = recorder.summary()
        if let summary {
            observationCount = summary.observationCount
            usableCount = summary.usableObservationCount
            inputCount = summary.inputEventCount
            elapsed = max(0, ProcessInfo.processInfo.systemUptime - summary.startedAt)
        }
        isRecording = false
        refresh()
        lastSessionURL = nil
        var published = [URL]()
        if hadPair {
            pairedGameplayUsableCount = gameplayRecorder.summary().0?.usableObservationCount
            do {
                lastSessionURL = try gameplayRecorder.stop(reason: reason, at: stoppedAt)
                if let lastSessionURL { published.append(lastSessionURL) }
            }
            catch { issue = "Gameplay clip save failed: \(error.localizedDescription)" }
        }
        do {
            if let primary = try recorder.stop(reason: reason, at: stoppedAt) {
                lastSessionURL = primary
                published.append(primary)
            }
        } catch { issue = "Demonstration save failed: \(error.localizedDescription)" }
        timer?.invalidate(); timer = nil
        if let focusObserver { NotificationCenter.default.removeObserver(focusObserver) }
        focusObserver = nil
        if !published.isEmpty { NotificationCenter.default.post(name: .botTrainingCapturePublished, object: published) }
        onStop?(reason)
    }

    func refresh() {
        lock.lock(); let observation = latest; lock.unlock()
        let now = ProcessInfo.processInfo.systemUptime
        let fresh = observation.map { now - $0.availableAt >= 0 && now - $0.availableAt < 0.75 } ?? false
        canRecord = fresh && observation?.isUsable == true
        readiness = canRecord ? "Ready to collect. You control the Knight." :
            (fresh ? "Waiting: \(observation!.issues.joined(separator: ", "))." : "Waiting for fresh gameplay detections.")
        if fresh, observation?.issues.contains("missing-knight") == true,
           let confidence = observation?.bestKnightConfidence {
            readiness = "Knight confidence is \(Int(confidence * 100))%; collection needs 50%. Add Knight labels or improve its detector in Model."
        }
        if isRecording {
            if NSApp.keyWindow == nil { stop(reason: "focus-lost"); return }
            let (summary, failure) = recorder.summary()
            if let summary {
                observationCount = summary.observationCount
                usableCount = summary.usableObservationCount
                inputCount = summary.inputEventCount
                elapsed = max(0, now - summary.startedAt)
            }
            let pairedSummary = pairedRecordingID == nil ? nil : gameplayRecorder.summary()
            pairedGameplayUsableCount = pairedSummary?.0?.usableObservationCount
            if let failure = failure ?? pairedSummary?.1 { issue = "Recording stopped: \(failure)"; stop(reason: "write-error") }
        }
    }

    func attachEvidence(reference: GroundTruthLineDocument, domain: BotDomain, replayRoot: URL) {
        recorder.attachEvidence(reference: reference, domain: domain, kind: "human-demonstration", replayRoot: replayRoot)
        if pairedRecordingID != nil {
            gameplayRecorder.attachEvidence(reference: reference, domain: .gameplay, kind: "human-demonstration", replayRoot: replayRoot)
        }
    }
    func recordTerrainAudit(_ frame: BotTerrainAuditFrame) {
        lock.withLock {
            guard collecting else { return }
            recorder.recordTerrainAudit(frame)
            if pairedRun != nil { gameplayRecorder.recordTerrainAudit(frame) }
        }
    }
}
