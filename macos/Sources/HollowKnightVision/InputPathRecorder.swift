import CoreGraphics
import Foundation

enum RecordedGameButton: String, Codable, CaseIterable, Sendable {
    case left
    case right
    case down
    case up
    case a
    case z
    case x
    case inventory
    case pause

    var bridgeButton: InputBridgeButtons {
        switch self {
        case .left: .left
        case .right: .right
        case .down: .down
        case .up: .up
        case .a: .actionA
        case .z: .actionZ
        case .x: .actionX
        case .inventory: .inventory
        case .pause: .pauseMenu
        }
    }

    static func from(_ button: InputBridgeButtons) -> Self? {
        switch button {
        case .left: .left
        case .right: .right
        case .down: .down
        case .up: .up
        case .actionA: .a
        case .actionZ: .z
        case .actionX: .x
        case .inventory: .inventory
        case .pauseMenu: .pause
        default: nil
        }
    }
}

enum RecordedInputTransition: String, Codable, Sendable {
    case pressed
    case released
}

struct RecordedInputPathEvent: Codable, Equatable, Sendable {
    let offset: TimeInterval
    let button: RecordedGameButton
    let transition: RecordedInputTransition
}

struct RecordedTrackingSample: Codable, Equatable, Sendable {
    /// Time when Vision completed this solve, relative to path start.
    let offset: TimeInterval
    /// Original ScreenCaptureKit presentation timestamp for frame correlation.
    let captureTimestamp: TimeInterval
    let cameraX: Double?
    let cameraY: Double?
    /// Pose actually presented/submitted after ground and fallback selection.
    /// Optional keeps paths recorded before fallback instrumentation readable.
    let publishedCameraX: Double?
    let publishedCameraY: Double?
    let poseSource: String?
    let motionBridgeConfidence: Double?
    let motionBridgeSource: String?
    let poseVerified: Bool
    let hasConfirmedGround: Bool
    let globalCorrectionX: Double?
    let globalCorrectionY: Double?
    let globalMatchCount: Int
    let groundSegmentCount: Int
    let inlierCount: Int
    let residualRMS: Double?
    let visibleLineIDs: [Int]
    let atlasLineIDs: [Int]
    /// End-to-end GroundHypothesisTracker update cost for this frame.
    let groundTrackingMilliseconds: Double?
    /// Low-cost visual transition evidence captured with this solve.
    let signalMeanPeak: Double?
    let signalVisibleFraction: Double?
    /// Stable room ownership; absent in paths recorded before room mapping.
    let roomID: Int?
    let roomRevision: UInt64?
    var captureOffset: TimeInterval? = nil
    /// Diagnostic pixel barcode, never an input to camera tracking.
    var renderedGameFrame: Int? = nil
    var localTextureSupport: Int? = nil
    var localTextureError: Double? = nil
    /// Visual-reference provenance for offline exact-frame diagnosis only.
    var localMatchSource: String? = nil
    var provisionalContinuityActive: Bool? = nil
    var localReferenceCaptureTimestamp: Double? = nil
    var localReferenceImageX: Double? = nil
    var localReferenceImageY: Double? = nil
    var localMatchImageDX: Double? = nil
    var localMatchImageDY: Double? = nil
    var placementRecoveryState: String? = nil
    /// Submission admission, not proof that the atlas worker committed pixels.
    var atlasWriteAllowed: Bool? = nil
    /// Both independently measured floorless candidates are recorded before
    /// selection. Hacker truth can then diagnose the losing candidate offline;
    /// these values never feed tracking.
    var floorlessCoarseX: Double? = nil
    var floorlessCoarseY: Double? = nil
    var floorlessMaskedX: Double? = nil
    var floorlessMaskedY: Double? = nil
    var floorlessExpectedDirection: Int? = nil
    var floorlessSelectionSource: String? = nil
}

struct RecordedLoopClosureEvent: Codable, Equatable, Sendable {
    let offset: TimeInterval
    let captureTimestamp: TimeInterval
    let worldRevision: Int
    let correctionX: Double
    let correctionY: Double
    let fromKeyframeID: Int
    let toKeyframeID: Int
    let support: Int
    let ambiguity: Double
}

struct RecordedGroundTruthSample: Codable, Equatable, Sendable {
    /// Same local monotonic clock used by input events and visual solve timing.
    let offset: TimeInterval
    /// Absolute local receipt time. ScreenCaptureKit presentation timestamps
    /// use the same uptime domain, permitting nearest-frame correlation.
    let receivedTimestamp: TimeInterval
    let sample: ReceiverGroundTruthSample
}

/// Capture-rate trace of the inexpensive motion bridge. Unlike ground solves,
/// these samples continue through frames rejected as dark transitions, which
/// makes the exact intervals that need bridging observable offline.
struct RecordedCoarseMotionSample: Codable, Equatable, Sendable {
    let offset: TimeInterval
    let captureTimestamp: TimeInterval
    let renderedGameFrame: Int?
    let presentedCameraX: Double
    let presentedCameraY: Double
    let coarseCameraX: Double?
    let coarseCameraY: Double?
    let direction: Int?
    let screenShift: Int?
    let confidence: Double?
    let placeMatchKeyframeID: Int?
    let placeMatchScore: Double?
    let placeMatchMargin: Double?
    let isControlling: Bool
    let isTransitioning: Bool
    let roomID: Int
    let roomRevision: UInt64
    let signalMeanPeak: Double
    let signalVisibleFraction: Double
    /// Revision of the capture-rate floorless trajectory. Changes identify a
    /// place reanchor, masked-registration correction, or reset.
    var coarsePoseRevision: UInt64? = nil
}

/// Sparse raw input to the low-resolution bridge. Full-resolution world
/// observations stop when ground tracking stops; this 64x36 trace continues
/// at 20 Hz so the missing interval can be reproduced and tuned offline.
struct RecordedLowResolutionFrame: Codable, Equatable, Sendable {
    let offset: TimeInterval
    let captureTimestamp: TimeInterval
    let renderedGameFrame: Int?
    let roomID: Int
    let width: Int
    let height: Int
    let luma: Data
    var groundTrackingReliable: Bool
}

struct RecordedInputPath: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 1

    let schemaVersion: Int
    let id: UUID
    let createdAt: Date
    let duration: TimeInterval
    let sourcePathID: UUID?
    let replayIteration: Int?
    /// Exact in-game start state captured by the receiver when K begins.
    /// Optional keeps pre-checkpoint path files readable for analysis.
    let startCheckpoint: RecordedGameCheckpoint?
    let events: [RecordedInputPathEvent]
    let trackingSamples: [RecordedTrackingSample]
    /// Optional keeps paths recorded before receiver telemetry readable.
    let groundTruthSamples: [RecordedGroundTruthSample]?
    /// Optional keeps recordings made before capture-rate coarse diagnostics
    /// readable without a schema migration.
    let coarseMotionSamples: [RecordedCoarseMotionSample]?
    /// Optional keeps recordings made before low-resolution image evidence
    /// readable without a schema migration.
    let lowResolutionFrames: [RecordedLowResolutionFrame]?
    /// Optional preserves compatibility with paths captured before closure
    /// events became part of the schema-one trace.
    let loopClosures: [RecordedLoopClosureEvent]?
    let runtimeMetadata: [String: String]?

    init(
        schemaVersion: Int = currentSchemaVersion,
        id: UUID,
        createdAt: Date,
        duration: TimeInterval,
        sourcePathID: UUID? = nil,
        replayIteration: Int? = nil,
        startCheckpoint: RecordedGameCheckpoint? = nil,
        events: [RecordedInputPathEvent],
        trackingSamples: [RecordedTrackingSample],
        groundTruthSamples: [RecordedGroundTruthSample] = [],
        coarseMotionSamples: [RecordedCoarseMotionSample] = [],
        lowResolutionFrames: [RecordedLowResolutionFrame] = [],
        loopClosures: [RecordedLoopClosureEvent] = [],
        runtimeMetadata: [String: String]? = nil
    ) {
        self.schemaVersion = schemaVersion
        self.id = id
        self.createdAt = createdAt
        self.duration = duration
        self.sourcePathID = sourcePathID
        self.replayIteration = replayIteration
        self.startCheckpoint = startCheckpoint
        self.events = events
        self.trackingSamples = trackingSamples
        self.groundTruthSamples = groundTruthSamples
        self.coarseMotionSamples = coarseMotionSamples
        self.lowResolutionFrames = lowResolutionFrames
        self.loopClosures = loopClosures
        self.runtimeMetadata = runtimeMetadata
    }

    var unverifiedSampleCount: Int {
        trackingSamples.count { !$0.poseVerified }
    }

    var globalCorrectionCount: Int {
        trackingSamples.count { $0.globalCorrectionX != nil || $0.globalCorrectionY != nil }
    }

    var loopClosureEvents: [RecordedLoopClosureEvent] {
        loopClosures ?? []
    }

    var groundTruthTrace: [RecordedGroundTruthSample] {
        groundTruthSamples ?? []
    }

    var coarseMotionTrace: [RecordedCoarseMotionSample] {
        coarseMotionSamples ?? []
    }

    var lowResolutionFrameTrace: [RecordedLowResolutionFrame] {
        lowResolutionFrames ?? []
    }
}

/// Thread-safe recorder sharing the monotonic uptime clock with input forwarding.
/// Tracking capture may arrive from Vision's worker queue while K is handled on main.
final class InputPathRecorder: @unchecked Sendable {
    private let runtimeMetadata: [String: String] = {
        let url = Bundle.main.url(forResource: "runtime-build-info", withExtension: "json")
        var metadata = url.flatMap { try? Data(contentsOf: $0) }.flatMap {
            try? JSONDecoder().decode([String: String].self, from: $0)
        } ?? [:]
        metadata["captureWidth"] = String(HollowKnightCaptureConfiguration.outputWidth)
        metadata["captureQueueDepth"] = String(HollowKnightCaptureConfiguration.queueDepth)
        metadata["capturePixelPath"] = "core-image"
        metadata["interactiveVisionWorkers"] = "false"
        metadata["captureUserActivity"] = "false"
        metadata["presentationDrawableCount"] = "2"
        metadata["captureTargetFPS"] = "native"
        metadata["renderFrameMarker"] = String(RenderedFrameMarker.enabled)
        metadata["groundPlacementRecovery"] = "deferred-ground-corrections-v1"
        metadata["groundRelocalization"] =
            "floorless-background-search-continuity-v1"
        metadata["groundTextureRegistration"] = "keyframe-subpixel-v1"
        metadata["groundImageMotionScale"] = "hacker-cohort-v4-anchor-and-recent-distance"
        metadata["lowResolutionMotionBridge"] =
            "tentative-transition-handoff-v14"
        metadata["floorlessPosePriority"] = "place-match-masked-registration-coarse-rebase-v2"
        metadata["lowResolutionPlaceMemory"] = "room-local-64x36-v1"
        metadata["lowResolutionFrameTrace"] =
            "64x36-20hz-measured-ground-reliability-v3"
        metadata["localMatchDiagnostics"] = "reference-v1"
        metadata["playbackDivergenceGuard"] =
            "hacker-exact-frame-v2-\(Int(PlaybackDivergenceMonitor.thresholdPixels))px-\(PlaybackDivergenceMonitor.requiredConsecutiveSamples)samples-\(Int(PlaybackDivergenceMonitor.maximumVisionSilence * 1_000))ms-silence"
        metadata["pathPlaybackPreparation"] =
            "purge-single-restore-settle-repurge-v2"
        metadata["backgroundPathPlayback"] = String(
            ProcessInfo.processInfo.arguments.contains("--enable-automation-control")
                && ProcessInfo.processInfo.arguments.contains("--allow-background-path-playback"))
        metadata["globalSearchRows"] = "raw"
        metadata["groundTrace"] = String(ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking"))
        metadata["rawGroundAudit"] = String(ProcessInfo.processInfo.arguments.contains { $0.hasPrefix("--ground-raw-audit-directory=") })
        return metadata
    }()
    private struct Session {
        let id: UUID
        let createdAt: Date
        let startedAt: TimeInterval
        let sourcePathID: UUID?
        let replayIteration: Int?
        let startCheckpoint: RecordedGameCheckpoint?
        var events = [RecordedInputPathEvent]()
        var trackingSamples = [RecordedTrackingSample]()
        var groundTruthSamples = [RecordedGroundTruthSample]()
        var coarseMotionSamples = [RecordedCoarseMotionSample]()
        var lowResolutionFrames = [RecordedLowResolutionFrame]()
        var lowResolutionFrameIndices = [TimeInterval: Int]()
        var lastLowResolutionFrameAt: TimeInterval?
        var loopClosures = [RecordedLoopClosureEvent]()
        var heldButtons = Set<RecordedGameButton>()
    }

    private let lock = NSLock()
    private var session: Session?

    var isRecording: Bool {
        lock.withLock { session != nil }
    }

    @discardableResult
    func start(
        at timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime,
        createdAt: Date = Date(),
        id: UUID = UUID(),
        sourcePathID: UUID? = nil,
        replayIteration: Int? = nil,
        startCheckpoint: RecordedGameCheckpoint? = nil
    ) -> Bool {
        lock.withLock {
            guard session == nil else { return false }
            session = Session(
                id: id,
                createdAt: createdAt,
                startedAt: timestamp,
                sourcePathID: sourcePathID,
                replayIteration: replayIteration,
                startCheckpoint: startCheckpoint
            )
            return true
        }
    }

    func recordInput(
        button: RecordedGameButton,
        isPressed: Bool,
        at timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        lock.withLock {
            guard var current = session else { return }
            if isPressed {
                guard current.heldButtons.insert(button).inserted else { return }
            } else {
                guard current.heldButtons.remove(button) != nil else { return }
            }
            current.events.append(RecordedInputPathEvent(
                offset: Self.offset(timestamp, from: current.startedAt),
                button: button,
                transition: isPressed ? .pressed : .released
            ))
            session = current
        }
    }

    func recordTracking(
        _ result: GroundHypothesisTrackingResult,
        captureTimestamp: TimeInterval,
        groundTrackingMilliseconds: Double? = nil,
        publishedCameraPosition: CGPoint? = nil,
        poseSource: String? = nil,
        motionBridgeConfidence: Double? = nil,
        motionBridgeSource: String? = nil,
        signalMeanPeak: Double? = nil,
        signalVisibleFraction: Double? = nil,
        roomID: Int? = nil,
        roomRevision: UInt64? = nil,
        renderedGameFrame: Int? = nil,
        placementRecoveryState: String? = nil,
        floorlessCoarsePosition: CGPoint? = nil,
        floorlessMaskedPosition: CGPoint? = nil,
        floorlessExpectedDirection: VisualRoomDirection? = nil,
        floorlessSelectionSource: String? = nil,
        observedAt timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        lock.withLock {
            guard var current = session else { return }
            current.trackingSamples.append(RecordedTrackingSample(
                offset: Self.offset(timestamp, from: current.startedAt),
                captureTimestamp: captureTimestamp,
                cameraX: result.cameraPosition.map { Double($0.x) },
                cameraY: result.cameraPosition.map { Double($0.y) },
                publishedCameraX: publishedCameraPosition.map { Double($0.x) },
                publishedCameraY: publishedCameraPosition.map { Double($0.y) },
                poseSource: poseSource,
                motionBridgeConfidence: motionBridgeConfidence,
                motionBridgeSource: motionBridgeSource,
                poseVerified: result.poseVerified,
                hasConfirmedGround: result.hasConfirmedGround,
                globalCorrectionX: result.globalCorrection.map { Double($0.dx) },
                globalCorrectionY: result.globalCorrection.map { Double($0.dy) },
                globalMatchCount: result.globalMatchCount,
                groundSegmentCount: result.groundSegmentCount,
                inlierCount: result.inlierCount,
                residualRMS: result.residualRMS.map(Double.init),
                visibleLineIDs: Array(Set(result.lineReviews.map(\.id))).sorted(),
                atlasLineIDs: Array(Set(result.atlasLines.map(\.segmentID))).sorted(),
                groundTrackingMilliseconds: groundTrackingMilliseconds,
                signalMeanPeak: signalMeanPeak,
                signalVisibleFraction: signalVisibleFraction,
                roomID: roomID,
                roomRevision: roomRevision,
                captureOffset: captureTimestamp - current.startedAt,
                renderedGameFrame: renderedGameFrame,
                localTextureSupport: result.localTextureSupport,
                localTextureError: result.localTextureError.map(Double.init),
                localMatchSource: result.localMatchSource,
                provisionalContinuityActive: result.provisionalContinuityActive,
                localReferenceCaptureTimestamp: result.localReferenceTimestamp,
                localReferenceImageX: result.localReferencePosition.map { Double($0.dx) },
                localReferenceImageY: result.localReferencePosition.map { Double($0.dy) },
                localMatchImageDX: result.localMatchedImageDisplacement.map { Double($0.dx) },
                localMatchImageDY: result.localMatchedImageDisplacement.map { Double($0.dy) },
                placementRecoveryState: placementRecoveryState,
                floorlessCoarseX: floorlessCoarsePosition.map { Double($0.x) },
                floorlessCoarseY: floorlessCoarsePosition.map { Double($0.y) },
                floorlessMaskedX: floorlessMaskedPosition.map { Double($0.x) },
                floorlessMaskedY: floorlessMaskedPosition.map { Double($0.y) },
                floorlessExpectedDirection: floorlessExpectedDirection?.rawValue,
                floorlessSelectionSource: floorlessSelectionSource
            ))
            // This asynchronous solve belongs to one exact captured frame.
            // Never carry the prior frame's reliability into a newly dark or
            // floorless interval, where it could seed place memory incorrectly.
            if let frameIndex = current.lowResolutionFrameIndices[captureTimestamp] {
                current.lowResolutionFrames[frameIndex].groundTrackingReliable =
                    GroundMotionEvidence.isMeasured(result)
            }
            session = current
        }
    }

    func recordAtlasAdmission(captureTimestamp: TimeInterval, allowed: Bool) {
        lock.withLock {
            guard let index = session?.trackingSamples.indices.last,
                  session?.trackingSamples[index].captureTimestamp == captureTimestamp else { return }
            session?.trackingSamples[index].atlasWriteAllowed = allowed
        }
    }

    func recordLoopClosure(
        update: LiveWorldPipelineUpdate,
        closure: LiveWorldLoopClosureEdge,
        observedAt timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        lock.withLock {
            guard var current = session else { return }
            current.loopClosures.append(RecordedLoopClosureEvent(
                offset: Self.offset(timestamp, from: current.startedAt),
                captureTimestamp: update.captureTimestamp,
                worldRevision: update.worldRevision,
                correctionX: Double(update.correction.dx),
                correctionY: Double(update.correction.dy),
                fromKeyframeID: closure.fromKeyframeID,
                toKeyframeID: closure.toKeyframeID,
                support: closure.support,
                ambiguity: closure.ambiguity
            ))
            session = current
        }
    }

    func recordGroundTruth(
        _ sample: ReceiverGroundTruthSample,
        observedAt timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        lock.withLock {
            guard var current = session else { return }
            current.groundTruthSamples.append(RecordedGroundTruthSample(
                offset: Self.offset(timestamp, from: current.startedAt),
                receivedTimestamp: timestamp,
                sample: sample
            ))
            session = current
        }
    }

    func recordCoarseMotion(
        captureTimestamp: TimeInterval,
        renderedGameFrame: Int?,
        presentedCameraPosition: CGPoint,
        coarseCameraPosition: CGPoint?,
        estimate: LowResolutionRoomMotionEstimate?,
        placeMatch: LowResolutionPlaceMatch? = nil,
        motionGrid: LowResolutionMotionGrid? = nil,
        isControlling: Bool,
        isTransitioning: Bool,
        roomID: Int,
        roomRevision: UInt64,
        coarsePoseRevision: UInt64? = nil,
        signalMeanPeak: Double,
        signalVisibleFraction: Double,
        observedAt timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        lock.withLock {
            guard var current = session else { return }
            current.coarseMotionSamples.append(RecordedCoarseMotionSample(
                offset: Self.offset(timestamp, from: current.startedAt),
                captureTimestamp: captureTimestamp,
                renderedGameFrame: renderedGameFrame,
                presentedCameraX: presentedCameraPosition.x,
                presentedCameraY: presentedCameraPosition.y,
                coarseCameraX: coarseCameraPosition.map { Double($0.x) },
                coarseCameraY: coarseCameraPosition.map { Double($0.y) },
                direction: estimate?.direction.rawValue,
                screenShift: estimate?.screenShift,
                confidence: estimate?.confidence,
                placeMatchKeyframeID: placeMatch?.keyframeID,
                placeMatchScore: placeMatch?.score,
                placeMatchMargin: placeMatch?.margin,
                isControlling: isControlling,
                isTransitioning: isTransitioning,
                roomID: roomID,
                roomRevision: roomRevision,
                signalMeanPeak: signalMeanPeak,
                signalVisibleFraction: signalVisibleFraction,
                coarsePoseRevision: coarsePoseRevision
            ))
            if let motionGrid,
               motionGrid.width > 0, motionGrid.height > 0,
               motionGrid.luma.count == motionGrid.width * motionGrid.height,
               current.lastLowResolutionFrameAt.map({
                   timestamp - $0 >= 0.045
               }) ?? true {
                let frameIndex = current.lowResolutionFrames.count
                current.lowResolutionFrames.append(RecordedLowResolutionFrame(
                    offset: Self.offset(timestamp, from: current.startedAt),
                    captureTimestamp: captureTimestamp,
                    renderedGameFrame: renderedGameFrame,
                    roomID: roomID,
                    width: motionGrid.width,
                    height: motionGrid.height,
                    luma: Data(motionGrid.luma),
                    groundTrackingReliable: false
                ))
                current.lowResolutionFrameIndices[captureTimestamp] = frameIndex
                current.lastLowResolutionFrameAt = timestamp
            }
            session = current
        }
    }

    func stop(
        at timestamp: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) -> RecordedInputPath? {
        lock.withLock {
            guard var current = session else { return nil }
            let duration = Self.offset(timestamp, from: current.startedAt)
            for button in current.heldButtons.sorted(by: { $0.rawValue < $1.rawValue }) {
                current.events.append(RecordedInputPathEvent(
                    offset: duration,
                    button: button,
                    transition: .released
                ))
            }
            session = nil
            return RecordedInputPath(
                id: current.id,
                createdAt: current.createdAt,
                duration: duration,
                sourcePathID: current.sourcePathID,
                replayIteration: current.replayIteration,
                startCheckpoint: current.startCheckpoint,
                events: current.events,
                trackingSamples: current.trackingSamples,
                groundTruthSamples: current.groundTruthSamples,
                coarseMotionSamples: current.coarseMotionSamples,
                lowResolutionFrames: current.lowResolutionFrames,
                loopClosures: current.loopClosures,
                runtimeMetadata: runtimeMetadata
            )
        }
    }

    func cancel() {
        lock.withLock { session = nil }
    }

    private static func offset(_ timestamp: TimeInterval, from start: TimeInterval) -> TimeInterval {
        guard timestamp.isFinite, start.isFinite else { return 0 }
        return max(0, timestamp - start)
    }
}

final class InputPathStore {
    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = InputPathStore.defaultRootURL(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL
        self.fileManager = fileManager
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        let applicationSupport = fileManager.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support", isDirectory: true)
        return applicationSupport
            .appendingPathComponent("HollowKnightVision", isDirectory: true)
            .appendingPathComponent("input-paths-v1", isDirectory: true)
    }

    @discardableResult
    func save(_ path: RecordedInputPath) throws -> URL {
        try fileManager.createDirectory(
            at: rootURL,
            withIntermediateDirectories: true
        )
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        let name = "path-\(formatter.string(from: path.createdAt))-\(path.id.uuidString.lowercased()).json"
        let url = rootURL.appendingPathComponent(name, isDirectory: false)
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try encoder.encode(path).write(to: url, options: .atomic)
        return url
    }

    func load(from url: URL) throws -> RecordedInputPath {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(RecordedInputPath.self, from: Data(contentsOf: url))
    }

    func load(named fileName: String) throws -> RecordedInputPath {
        guard fileName == (fileName as NSString).lastPathComponent,
              fileName.hasPrefix("path-"), fileName.hasSuffix(".json") else {
            throw CocoaError(.fileReadInvalidFileName)
        }
        return try load(from: rootURL.appendingPathComponent(fileName, isDirectory: false))
    }
}
