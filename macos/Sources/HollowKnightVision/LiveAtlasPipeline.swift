import CoreGraphics
import CoreImage
import Foundation
import OSLog

struct LiveAtlasCadence {
    let minimumPresentationInterval: Double
    let minimumIntegrationInterval: Double
    private(set) var lastPresentationTimestamp: Double?
    private(set) var lastIntegrationTimestamp: Double?

    init(
        presentationFramesPerSecond: Double = 15,
        atlasFramesPerSecond: Double = 5
    ) {
        minimumPresentationInterval = 1 / presentationFramesPerSecond
        minimumIntegrationInterval = 1 / atlasFramesPerSecond
    }

    mutating func decision(at timestamp: Double, canIntegrate: Bool) -> (present: Bool, integrate: Bool) {
        if let last = lastPresentationTimestamp, timestamp < last {
            reset()
        }
        if let last = lastPresentationTimestamp,
           timestamp - last + 0.000_001 < minimumPresentationInterval {
            return (false, false)
        }
        lastPresentationTimestamp = timestamp
        guard canIntegrate else { return (true, false) }
        if let last = lastIntegrationTimestamp,
           timestamp - last + 0.000_001 < minimumIntegrationInterval {
            return (true, false)
        }
        lastIntegrationTimestamp = timestamp
        return (true, true)
    }

    mutating func reset() {
        lastPresentationTimestamp = nil
        lastIntegrationTimestamp = nil
    }
}

struct LiveAtlasOutput {
    struct AtlasLayer {
        let id: Int
        let image: CGImage
        let bounds: CGRect
        let revision: UInt64
        let contentBounds: CGRect?

        init(
            id: Int = 0, image: CGImage, bounds: CGRect, revision: UInt64,
            contentBounds: CGRect? = nil
        ) {
            self.id = id
            self.image = image
            self.bounds = bounds
            self.revision = revision
            self.contentBounds = contentBounds
        }
    }

    /// The atlas is a persistent texture. The current capture is a separate
    /// texture positioned in the same world coordinate system.
    let atlas: AtlasLayer?
    let atlasTiles: [AtlasLayer]
    let liveImage: CGImage
    let liveBounds: CGRect
    let focusPoint: CGPoint
    let integratedFrame: Bool
    /// Revision of the graph that owns these atlas pixels.
    let worldRevision: Int?
    let anchorPosition: CGPoint

    init(
        atlas: AtlasLayer?,
        atlasTiles: [AtlasLayer] = [],
        liveImage: CGImage,
        liveBounds: CGRect,
        focusPoint: CGPoint,
        integratedFrame: Bool,
        worldRevision: Int? = nil,
        anchorPosition: CGPoint = .zero
    ) {
        self.atlas = atlas
        self.atlasTiles = atlasTiles.isEmpty ? atlas.map { [$0] } ?? [] : atlasTiles
        self.liveImage = liveImage
        self.liveBounds = liveBounds
        self.focusPoint = focusPoint
        self.integratedFrame = integratedFrame
        self.worldRevision = worldRevision
        self.anchorPosition = anchorPosition
    }
}

struct LiveWorldPipelineUpdate {
    let captureGeneration: UInt64
    let captureTimestamp: Double
    let worldRevision: Int
    let correction: CGVector
    let acceptedClosure: LiveWorldLoopClosureEdge?
    let confirmedRecoveryPose: CGPoint?
    let basisTicket: WorldBasisController.ObservationTicket?
    let review: LiveWorldTrackerReview
    let snapshot: LiveWorldSnapshot
    let atlasTiles: [LiveAtlasOutput.AtlasLayer]
}

private final class LiveWorldSourceProviderBox {
    let patchMatcher = LivePatchClosureMatcher()
    var store: LiveWorldSessionStore? { didSet { patchMatcher.reset() } }

    init(store: LiveWorldSessionStore?) {
        self.store = store
    }

    func source(for observationID: Int) -> CGImage? {
        try? store?.source(for: observationID)
    }
}

enum LiveAtlasStateError: LocalizedError {
    case activeWorldUnavailable
    case loadRollbackFailed

    var errorDescription: String? {
        switch self {
        case .activeWorldUnavailable: "The active atlas is unavailable."
        case .loadRollbackFailed: "Atlas load failed and its archive could not be restored."
        }
    }
}

struct LiveAtlasLoadedState {
    let snapshot: LiveWorldSnapshot
    let atlasTiles: [LiveAtlasOutput.AtlasLayer]
    var archivedPreviousWorldURL: URL? = nil
}

/// Small live-only pipeline. Camera pose is already solved when a frame enters.
/// It retains a flat atlas and draws the boxed current frame at that same pose.
final class LiveAtlasPipeline {
    private struct AtlasIntegrationRequest {
        let observationID: Int
        let frame: CGImage
        let regions: SceneRegions
        let alignmentExclusions: [CGRect]
        let compositionOmittedRects: [CGRect]
        let cameraPosition: CGPoint
        let solveWidth: CGFloat
        let captureTimestamp: Double
        let worldTimestamp: Double
        let captureGeneration: UInt64
        let atlasGeneration: UInt64
        let roomID: Int
        let basisTicket: WorldBasisController.ObservationTicket?
        /// Ground identity fixed this capture's absolute pose. Later basis
        /// changes must not move this older frame to a newer camera pose.
        let groundAnchoredPose: Bool
        /// Temporary non-ground motion bridged a floorless room boundary. Its
        /// pose is capture-fixed, closure search is deferred, and transition
        /// matte pixels must preserve prior atlas content.
        let transitionBridgePose: Bool
        let islandCapture: LiveIslandCapture?
        let groundGeometry: LiveWorldGroundGeometry?
        let observedFloorCandidates: LiveObservedFloorCandidates?
        let observedPlayerBounds: CGRect?
        let doorwayTransition: LiveDoorwayTransition?
    }

    private struct RecoveryRequest {
        let frame: CGImage
        let solveWidth: CGFloat
        let exclusions: [CGRect]
        let captureTimestamp: Double
        let captureGeneration: UInt64
        let atlasGeneration: UInt64
        let roomID: Int
        let basisTicket: WorldBasisController.ObservationTicket
    }

    private let context: CIContext
    private let atlasAdmissionLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "atlas-admission"
    )
    private let atlasContext: CIContext
    private let groundTraceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private let groundTraceLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "ground-trace")
    private var lastGroundTraceTimestamp: Double?
    private let composer: LiveTiledAtlas
    private let sourceProviderBox: LiveWorldSourceProviderBox
    private let activeWorldRootURL: URL?
    private let atlasQueue: DispatchQueue
    private let integrationPump = LatestFramePump<AtlasIntegrationRequest>()
    private let recoveryPump = LatestFramePump<RecoveryRequest>()
    /// Serializes reset against the side effects of an active pump ticket.
    /// A ticket may spend time masking a frame, so the generation check alone
    /// is not sufficient to prevent reset from racing its later persistence.
    private let integrationTransactionLock = NSLock()
    private let stateLock = NSLock()
    private let runsIntegrationSynchronously: Bool
    private let integrationDelay: TimeInterval
    private let integrationStarted: (() -> Void)?
    private let compositionShouldCancel: () -> Bool
    private let restorationObservationStarted: ((Int) -> Void)?
    private let persistentAppender: (LiveWorldSessionStore, CGImage, Double, CGPoint, Double, [CGRect]) throws -> StoredFrameObservation
    private let persistentCommitter: (LiveWorldSessionStore, LiveWorldSnapshot, Int, Int) throws -> Bool
    private let requiresPersistentWorld: Bool
    private let minimumAtlasSubmissionInterval: TimeInterval
    private var cadence: LiveAtlasCadence
    private var atlasRevision: UInt64 = 0
    private var atlasSnapshot = [LiveAtlasOutput.AtlasLayer]()
    private var atlasWorldRevision = 0
    private var anchorPosition: CGPoint?
    /// Keeps live motion stable while the first admitted observation is still
    /// pending. Only the compositor can choose the committed atlas origin.
    private var provisionalAnchorPosition: CGPoint?
    private var lastLiveTimestamp: Double?
    private var lastAtlasSubmissionUptime: TimeInterval?
    private var lastRecoverySubmissionUptime: TimeInterval?
    private var lastRecoveryDiagnosticAt: Double?
    private var lastRecoveryCaptureGeneration: UInt64?
    private var lastRecoveryLocalEpoch: UInt64?
    private var generation: UInt64 = 0
    private var nextObservationID = 0
    private var persistentGenerationBase: UInt64 = 1
    private var worldSession: LiveWorldSessionStore?
    private var worldTracker: LiveWorldTracker
    private var worldSceneRevision: Int
    private var lastGlobalMatchTimestamp: Double?
    private var lastWorldTrackerTimestamp: Double?
    private var lastCaptureGeneration: UInt64?
    private var worldUpdateHandler: ((LiveWorldPipelineUpdate) -> Void)?
    private var worldPoseResolver: ((WorldBasisController.ObservationTicket) -> WorldBasisController.ResolvedObservation?)?
    private var roomCompositionBoundsProvider: ((Int) -> VisualRoomCompositionBounds)?
    private var islandClosureSearchAllowed: () -> Bool = { false }
    private var persistenceAvailable: Bool
    private var persistenceIssue: String?
    private var committedObservationCount: Int
    private var resetArchiveURL: URL?

    init(
        context: CIContext,
        // Capture is already limited to 30 Hz. A looser presentation gate
        // admits every capture timestamp despite small delivery jitter.
        presentationFramesPerSecond: Double = 60,
        atlasFramesPerSecond: Double = 7.5,
        runsIntegrationSynchronously: Bool = false,
        integrationDelay: TimeInterval = 0,
        integrationStarted: (() -> Void)? = nil,
        compositionShouldCancel: @escaping () -> Bool = { false },
        restorationObservationStarted: ((Int) -> Void)? = nil,
        worldRootURL: URL? = nil,
        restoresPersistedWorldOnInitialization: Bool = true,
        persistentAppender: @escaping (LiveWorldSessionStore, CGImage, Double, CGPoint, Double, [CGRect]) throws -> StoredFrameObservation = { store, image, timestamp, cameraPosition, solveWidth, excludedRects in
            try store.append(
                maskedImage: image,
                timestamp: timestamp,
                rawCameraPose: cameraPosition,
                solveWidth: solveWidth,
                excludedRects: excludedRects
            )
        },
        persistentCommitter: @escaping (LiveWorldSessionStore, LiveWorldSnapshot, Int, Int) throws -> Bool = { store, snapshot, expectedRevision, newRevision in
            try store.commit(snapshot, expectedRevision: expectedRevision, newRevision: newRevision)
        }
    ) {
        let requiresPersistentWorld = worldRootURL != nil
        var loadedSession: LiveWorldSessionStore?
        var loadedState: LiveWorldSessionState?
        var initialSnapshot = try! LiveWorldSnapshot()
        var initialTracker = LiveWorldTracker()
        var persistenceIssue: String?
        if let worldRootURL {
            do {
                let session = try LiveWorldSessionStore(rootURL: worldRootURL)
                let state = try session.load()
                let tracker = try LiveWorldTracker(snapshot: state.snapshot)
                loadedSession = session
                loadedState = state
                initialSnapshot = state.snapshot
                initialTracker = tracker
            } catch {
                persistenceIssue = "Saved world unavailable: \(error.localizedDescription)"
            }
        }
        let sourceProviderBox = LiveWorldSourceProviderBox(store: loadedSession)
        let atlasContext = CIContext(options: [.cacheIntermediates: false])
        let anchor = initialSnapshot.anchoredObservationID.flatMap { anchorID in
            initialSnapshot.observations.first(where: { $0.id == anchorID })?.optimizedPose
        }
        self.context = context
        self.atlasContext = atlasContext
        composer = LiveTiledAtlas(
            context: atlasContext,
            anchorPosition: anchor,
            sourceProvider: { [weak sourceProviderBox] id in
                guard let source = sourceProviderBox?.source(for: id) else { return nil }
                return GameplayTutorialPromptMask.removing(from: source, context: atlasContext)
            },
            sourceCacheLimit: loadedSession == nil ? 512 : 12,
            pixelPolicy: .newestOpaque
        )
        self.sourceProviderBox = sourceProviderBox
        activeWorldRootURL = worldRootURL?.standardizedFileURL
        atlasQueue = DispatchQueue(
            label: "com.ballroller.hollow-knight-vision.atlas-integration",
            qos: .utility,
        autoreleaseFrequency: .workItem
        )
        self.runsIntegrationSynchronously = runsIntegrationSynchronously
        self.integrationDelay = integrationDelay
        self.integrationStarted = integrationStarted
        self.compositionShouldCancel = compositionShouldCancel
        self.restorationObservationStarted = restorationObservationStarted
        self.persistentAppender = persistentAppender
        self.persistentCommitter = persistentCommitter
        self.requiresPersistentWorld = requiresPersistentWorld
        worldSession = loadedSession
        worldTracker = initialTracker
        worldSceneRevision = loadedState?.sceneRevision ?? 0
        nextObservationID = (initialSnapshot.observations.map(\.id).max() ?? -1) + 1
        persistentGenerationBase = (initialSnapshot.observations.map(\.captureGeneration).max() ?? 0) &+ 1
        persistenceAvailable = !requiresPersistentWorld || loadedSession != nil
        self.persistenceIssue = persistenceIssue
        committedObservationCount = initialSnapshot.observations.count
        resetArchiveURL = nil
        minimumAtlasSubmissionInterval = 1 / max(0.001, atlasFramesPerSecond)
        cadence = LiveAtlasCadence(
            presentationFramesPerSecond: presentationFramesPerSecond,
            atlasFramesPerSecond: atlasFramesPerSecond
        )
        if restoresPersistedWorldOnInitialization,
           !initialSnapshot.observations.isEmpty {
            restorePersistedWorld(initialSnapshot)
        }
    }

    func setWorldUpdateHandler(_ handler: ((LiveWorldPipelineUpdate) -> Void)?) {
        stateLock.lock()
        worldUpdateHandler = handler
        stateLock.unlock()
    }

    func setWorldPoseResolver(
        _ resolver: ((WorldBasisController.ObservationTicket) -> WorldBasisController.ResolvedObservation?)?
    ) {
        stateLock.lock()
        worldPoseResolver = resolver
        stateLock.unlock()
    }

    func setRoomCompositionBoundsProvider(
        _ provider: @escaping (Int) -> VisualRoomCompositionBounds
    ) {
        stateLock.lock()
        roomCompositionBoundsProvider = provider
        stateLock.unlock()
        rebuildAtlasForRoomComposition()
    }

    /// Replays immutable masked sources through the current doorway crop. This
    /// removes pixels previously painted across a newly discovered boundary;
    /// merely masking future frames would leave the old overlap in the atlas.
    func rebuildAtlasForRoomComposition() {
        let rebuildGeneration: UInt64
        stateLock.lock()
        rebuildGeneration = generation
        stateLock.unlock()
        atlasQueue.async { [weak self] in
            guard let self else { return }
            self.integrationTransactionLock.lock()
            defer { self.integrationTransactionLock.unlock() }
            guard self.isGenerationCurrent(rebuildGeneration) else { return }
            let snapshot = self.worldTracker.snapshot
            let anchor = snapshot.anchoredObservationID.flatMap { anchorID in
                snapshot.observations.first(where: { $0.id == anchorID })?.optimizedPose
            }
            self.composer.reset(anchorPosition: anchor)
            for observation in snapshot.observations.sorted(by: { $0.id < $1.id }) {
                guard self.isGenerationCurrent(rebuildGeneration),
                      let source = self.sourceProviderBox.source(
                        for: observation.sourceObservationID
                      ),
                      let image = self.compositionMaskedSource(
                        source, observation: observation
                      ) else { continue }
                _ = self.composer.insert(
                    observationID: observation.id,
                    maskedImage: image,
                    solveWidth: CGFloat(observation.solveWidth),
                    cameraPosition: observation.optimizedPose,
                    captureIdentity: Int64(observation.sourceObservationID),
                    timestamp: observation.timestamp,
                    roomID: observation.roomID,
                    captureGeneration: observation.captureGeneration,
                    shouldCancel: { [weak self] in
                        guard let self else { return true }
                        return !self.isGenerationCurrent(rebuildGeneration)
                    }
                )
            }
            let rebuilt = self.composer.snapshot
            let rebuiltAnchor = self.composer.fixedAnchorPosition
            let layers = rebuilt.tiles.map {
                LiveAtlasOutput.AtlasLayer(
                    id: $0.id,
                    image: $0.image,
                    bounds: $0.worldBounds,
                    revision: rebuilt.revision,
                    contentBounds: rebuilt.contentBounds
                )
            }
            self.stateLock.lock()
            if self.generation == rebuildGeneration {
                self.anchorPosition = rebuiltAnchor
                if rebuiltAnchor != nil { self.provisionalAnchorPosition = nil }
                self.atlasRevision = rebuilt.revision
                self.atlasSnapshot = layers
                self.atlasWorldRevision = self.worldTracker.snapshot.mapRevision
            }
            self.stateLock.unlock()
        }
    }

    var worldPersistenceIssue: String? {
        stateLock.lock()
        defer { stateLock.unlock() }
        return persistenceIssue
    }

    /// Flushes outstanding work for the serial headless replay schedule.
    func drainVisualReplay() {
        atlasQueue.sync {}
    }

    func currentWorldSnapshot() -> LiveWorldSnapshot {
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        return worldTracker.snapshot
    }

    func boundaryEvidence(
        leftRoomID: Int,
        rightRoomID: Int
    ) -> VisualRoomBoundaryEvidence? {
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        let observations = worldTracker.snapshot.observations
        guard let firstLeft = observations
            .filter({ $0.roomID == leftRoomID })
            .min(by: { $0.timestamp < $1.timestamp }) else { return nil }
        let rightCandidates = observations.filter {
            $0.roomID == rightRoomID && $0.timestamp < firstLeft.timestamp
        }
        guard let lastRight = (rightCandidates.isEmpty
            ? observations.filter({ $0.roomID == rightRoomID })
            : rightCandidates).max(by: { $0.timestamp < $1.timestamp }),
              let leftImage = sourceProviderBox.source(
                for: firstLeft.sourceObservationID
              ),
              let rightImage = sourceProviderBox.source(
                for: lastRight.sourceObservationID
              ) else { return nil }
        let leftProfile = FrameRegionRenderer.signalProfile(in: leftImage)
        let rightProfile = FrameRegionRenderer.signalProfile(in: rightImage)
        let solveWidth = CGFloat(firstLeft.solveWidth)
        let solveHeight = solveWidth * CGFloat(firstLeft.sourceHeight)
            / CGFloat(max(1, firstLeft.sourceWidth))
        return VisualRoomBoundaryEvidence(
            leftRoomID: leftRoomID,
            rightRoomID: rightRoomID,
            leftOriginalEntryWorldPose: firstLeft.optimizedPose,
            rightDepartureWorldPose: lastRight.optimizedPose,
            solveWidth: solveWidth,
            solveHeight: solveHeight,
            rightRoomLeftBand: rightProfile.edgeBlackBands?.left,
            leftRoomRightBand: leftProfile.edgeBlackBands?.right
        )
    }

    /// Applies a room-level coordinate correction atomically to the durable
    /// graph and the atlas composer. Relative motion inside the room is
    /// unchanged; only its placement beside the neighboring doorway moves.
    @discardableResult
    func translateRoom(
        roomID: Int,
        by translation: CGVector,
        updatingComposer: Bool = true
    ) -> Bool {
        guard roomID >= 0, translation.dx.isFinite, translation.dy.isFinite,
              abs(translation.dx) > 0.001 || abs(translation.dy) > 0.001 else {
            return true
        }
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        let original = worldTracker.snapshot
        guard original.observations.contains(where: { $0.roomID == roomID }) else {
            atlasAdmissionLog.error(
                "room translation rejected: room \(roomID, privacy: .public) has no observations"
            )
            return false
        }
        let translated: LiveWorldSnapshot
        let tracker: LiveWorldTracker
        do {
            translated = try original.translatingRoom(roomID, by: translation)
            tracker = try LiveWorldTracker(snapshot: translated)
        } catch {
            atlasAdmissionLog.error(
                "room translation model failed: \(error.localizedDescription, privacy: .public)"
            )
            return false
        }
        if let session = worldSession {
            do {
                let committed = try persistentCommitter(
                    session,
                    translated,
                    worldSceneRevision,
                    translated.mapRevision
                )
                guard committed else {
                    atlasAdmissionLog.error("room translation persistence CAS rejected")
                    return false
                }
                worldSceneRevision = translated.mapRevision
            } catch {
                atlasAdmissionLog.error(
                    "room translation persistence failed: \(error.localizedDescription, privacy: .public)"
                )
                return false
            }
        }
        worldTracker = tracker
        guard updatingComposer else { return true }
        let positions = Dictionary(uniqueKeysWithValues: translated.observations.map {
            ($0.id, $0.optimizedPose)
        })
        guard composer.replaceCameraPositions(
            positions,
            baseRevision: composer.revision
        ) else {
            // The graph is authoritative. A full source rebuild below will
            // reconcile a composer that had not finished initial restoration.
            rebuildAtlasForRoomCompositionLocked(snapshot: translated)
            return true
        }
        publishComposerSnapshotLocked()
        return true
    }

    var hasCommittedWorldEvidence: Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return committedObservationCount > 0
    }

    var mostRecentResetArchiveURL: URL? {
        stateLock.lock()
        defer { stateLock.unlock() }
        return resetArchiveURL
    }

    func activeAutoSaveSummary(now: Date = Date()) -> AtlasAutoSaveSummary {
        stateLock.lock()
        let registrationCount = committedObservationCount
        stateLock.unlock()
        let createdAt = activeWorldRootURL.flatMap { url in
            try? FileManager.default.attributesOfItem(atPath: url.path)[.creationDate] as? Date
        } ?? now
        return AtlasAutoSaveSummary(
            registrationCount: registrationCount,
            createdAt: min(createdAt, now),
            profile: activeWorldRootURL.flatMap { try? SaveProfileAtlas.read(from: $0) }
        )
    }

    var activeAutoSaveRootURL: URL? { activeWorldRootURL }

    /// Starts a new ScreenCaptureKit timestamp epoch without disturbing the
    /// persistent world. Replacement streams may restart their presentation
    /// timestamps; retaining the prior stream's timestamp would reject every
    /// frame from the replacement as delayed.
    func beginCaptureGeneration() {
        stateLock.lock()
        cadence.reset()
        lastLiveTimestamp = nil
        stateLock.unlock()
    }

    @discardableResult
    func reset(anchorPosition: CGPoint? = nil, profile: SaveProfileAtlas? = nil) -> Bool {
        // Invalidate first so an active full-atlas correction rebuild can
        // observe cancellation and release the transaction lock promptly.
        stateLock.lock()
        generation &+= 1
        let resetGeneration = generation
        stateLock.unlock()
        integrationPump.invalidate(epoch: resetGeneration)
        recoveryPump.invalidate(epoch: resetGeneration)
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        var replacementSession = worldSession
        var archiveURL: URL?
        if let session = worldSession {
            do {
                let archive = try session.archiveAndReset(profile: profile ?? SaveProfileAtlas.read(from: session.rootURL))
                replacementSession = archive.freshStore
                archiveURL = archive.archiveURL
            } catch {
                stateLock.lock()
                persistenceIssue = "Atlas reset failed: \(error.localizedDescription)"
                stateLock.unlock()
                return false
            }
        } else if requiresPersistentWorld {
            return false
        }
        stateLock.lock()
        cadence.reset()
        atlasRevision &+= 1
        atlasSnapshot = []
        atlasWorldRevision = 0
        self.anchorPosition = anchorPosition
        provisionalAnchorPosition = nil
        lastLiveTimestamp = nil
        lastAtlasSubmissionUptime = nil
        nextObservationID = 0
        persistenceAvailable = !requiresPersistentWorld || replacementSession != nil
        persistenceIssue = nil
        resetArchiveURL = archiveURL
        stateLock.unlock()
        composer.reset(anchorPosition: anchorPosition)
        worldTracker = LiveWorldTracker()
        worldSceneRevision = 0
        lastGlobalMatchTimestamp = nil
        lastWorldTrackerTimestamp = nil
        lastCaptureGeneration = nil
        lastRecoverySubmissionUptime = nil
        lastRecoveryCaptureGeneration = nil
        lastRecoveryLocalEpoch = nil
        worldSession = replacementSession
        sourceProviderBox.store = replacementSession
        stateLock.lock()
        committedObservationCount = 0
        stateLock.unlock()
        return true
    }

    /// Takes a consistent copy while integration is excluded. Saved states
    /// never become the active writer, so they remain true restore points.
    func copyActiveWorld(to destinationURL: URL) throws {
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        guard let session = worldSession else { throw LiveAtlasStateError.activeWorldUnavailable }
        try FileManager.default.copyItem(at: session.rootURL, to: destinationURL)
        _ = try LiveWorldSessionStore(rootURL: destinationURL).load()
    }

    /// Archives the active root before replacing it with a copy of a saved
    /// state. The saved state itself is never modified by future auto-saves.
    func loadSavedWorld(from savedWorldURL: URL) throws -> LiveAtlasLoadedState {
        integrationTransactionLock.lock()
        defer { integrationTransactionLock.unlock() }
        guard let currentSession = worldSession else {
            throw LiveAtlasStateError.activeWorldUnavailable
        }
        let savedState = try LiveWorldSessionStore(rootURL: savedWorldURL).load()
        _ = try LiveWorldTracker(snapshot: savedState.snapshot)
        let fileManager = FileManager.default
        let activeURL = currentSession.rootURL
        let archiveURL = activeURL.deletingLastPathComponent().appendingPathComponent(
            "\(activeURL.lastPathComponent).archive-load-\(UUID().uuidString)", isDirectory: true
        )
        try fileManager.moveItem(at: activeURL, to: archiveURL)
        let replacement: LiveWorldSessionStore
        let state: LiveWorldSessionState
        let tracker: LiveWorldTracker
        do {
            try fileManager.copyItem(at: savedWorldURL, to: activeURL)
            replacement = try LiveWorldSessionStore(rootURL: activeURL)
            state = try replacement.load()
            tracker = try LiveWorldTracker(snapshot: state.snapshot)
        } catch {
            if fileManager.fileExists(atPath: activeURL.path) {
                try? fileManager.removeItem(at: activeURL)
            }
            do {
                try fileManager.moveItem(at: archiveURL, to: activeURL)
            } catch {
                throw LiveAtlasStateError.loadRollbackFailed
            }
            throw error
        }

        let snapshot = state.snapshot
        let anchor = snapshot.anchoredObservationID.flatMap { anchorID in
            snapshot.observations.first(where: { $0.id == anchorID })?.optimizedPose
        }
        stateLock.lock()
        cadence.reset()
        generation &+= 1
        let replacementGeneration = generation
        atlasRevision &+= 1
        atlasSnapshot = []
        atlasWorldRevision = 0
        anchorPosition = anchor
        provisionalAnchorPosition = nil
        lastLiveTimestamp = nil
        lastAtlasSubmissionUptime = nil
        nextObservationID = (snapshot.observations.map(\.id).max() ?? -1) + 1
        persistentGenerationBase = (snapshot.observations.map(\.captureGeneration).max() ?? 0) &+ 1
        persistenceAvailable = true
        persistenceIssue = nil
        committedObservationCount = snapshot.observations.count
        stateLock.unlock()
        integrationPump.invalidate(epoch: replacementGeneration)
        recoveryPump.invalidate(epoch: replacementGeneration)
        worldSession = replacement
        sourceProviderBox.store = replacement
        worldTracker = tracker
        worldSceneRevision = state.sceneRevision
        lastGlobalMatchTimestamp = nil
        lastWorldTrackerTimestamp = nil
        lastCaptureGeneration = nil
        lastRecoverySubmissionUptime = nil
        lastRecoveryCaptureGeneration = nil
        lastRecoveryLocalEpoch = nil
        composer.reset(anchorPosition: anchor)
        for observation in snapshot.observations.sorted(by: { $0.id < $1.id }) {
            guard let source = sourceProviderBox.source(for: observation.sourceObservationID),
                  let image = compositionMaskedSource(source, observation: observation) else {
                continue
            }
            _ = composer.insert(
                observationID: observation.id,
                maskedImage: image,
                solveWidth: CGFloat(observation.solveWidth),
                cameraPosition: observation.optimizedPose,
                captureIdentity: Int64(observation.sourceObservationID),
                timestamp: observation.timestamp,
                roomID: observation.roomID,
                captureGeneration: observation.captureGeneration
            )
        }
        let restored = composer.snapshot
        let restoredAnchor = composer.fixedAnchorPosition
        let layers = restored.tiles.map {
            LiveAtlasOutput.AtlasLayer(
                id: $0.id, image: $0.image, bounds: $0.worldBounds,
                revision: restored.revision, contentBounds: restored.contentBounds
            )
        }
        stateLock.lock()
        anchorPosition = restoredAnchor
        if restoredAnchor != nil { provisionalAnchorPosition = nil }
        atlasRevision = restored.revision
        atlasSnapshot = layers
        atlasWorldRevision = worldTracker.snapshot.mapRevision
        stateLock.unlock()
        return LiveAtlasLoadedState(
            snapshot: snapshot, atlasTiles: layers,
            archivedPreviousWorldURL: archiveURL
        )
    }

    func process(
        frame: CGImage,
        regions: SceneRegions,
        objectDetections: [LiveObjectDetection] = [],
        objectIcons: [String: CGImage] = [:],
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        timestamp: Double,
        observationPoseTimestamp: Double? = nil,
        hasGameplaySignal: Bool,
        allowAtlasWrite: Bool = true
    ) -> LiveAtlasOutput? {
        let boxedFrame = FrameRegionRenderer.annotatedFrame(
            from: frame,
            regions: regions,
            objectDetections: objectDetections,
            objectIcons: objectIcons,
            context: context
        ) ?? frame
        stateLock.lock()
        let isDelayedObservation = lastLiveTimestamp.map { timestamp < $0 } ?? false
        let anchor = anchorPosition ?? provisionalAnchorPosition ?? cameraPosition
        // Previewing the title/profile screen must not choose the atlas origin.
        // The first admitted Gameplay observation owns that decision.
        if isDelayedObservation {
            stateLock.unlock()
            // Only sample-queue frames enter the presentation path. Atlas-only
            // vision observations use submitAtlasObservation below.
            return nil
        }

        let decision = cadence.decision(at: timestamp, canIntegrate: false)
        guard decision.present else {
            stateLock.unlock()
            return nil
        }
        lastLiveTimestamp = timestamp
        let atlasTiles = atlasSnapshot
        let publishedWorldRevision = atlasWorldRevision
        stateLock.unlock()

        let liveBounds = placementBounds(
            image: boxedFrame,
            cameraPosition: cameraPosition,
            anchorPosition: anchor,
            solveWidth: solveWidth
        )
        return LiveAtlasOutput(
            atlas: atlasTiles.count == 1 ? atlasTiles[0] : nil,
            atlasTiles: atlasTiles,
            liveImage: boxedFrame,
            liveBounds: liveBounds,
            focusPoint: CGPoint(x: liveBounds.midX, y: liveBounds.midY),
            integratedFrame: false,
            worldRevision: publishedWorldRevision,
            anchorPosition: anchor
        )
    }

    /// Accepts only an exact-pose observation for the slow atlas worker. This
    /// path never annotates or publishes a live window, so delayed Vision work
    /// cannot replace a newer capture presentation.
    @discardableResult
    func submitAtlasObservation(
        frame: CGImage,
        regions: SceneRegions,
        cameraPosition: CGPoint,
        solveWidth: CGFloat,
        timestamp: Double,
        observationPoseTimestamp: Double?,
        hasGameplaySignal: Bool,
        allowAtlasWrite: Bool = true,
        captureGeneration: UInt64 = 0,
        worldTimestamp: Double? = nil,
        submittedAt: TimeInterval = ProcessInfo.processInfo.systemUptime,
        basisTicket: WorldBasisController.ObservationTicket? = nil,
        groundAnchoredPose: Bool = false,
        transitionBridgePose: Bool = false,
        alignmentExclusions: [CGRect]? = nil,
        compositionOmittedRects: [CGRect] = [],
        roomID: Int = 0,
        islandCapture: LiveIslandCapture? = nil,
        groundGeometry: LiveWorldGroundGeometry? = nil,
        observedFloorCandidates: LiveObservedFloorCandidates? = nil,
        observedPlayerBounds: CGRect? = nil,
        doorwayTransition: LiveDoorwayTransition? = nil
    ) -> Bool {
        guard hasGameplaySignal, allowAtlasWrite, observationPoseTimestamp == timestamp else {
            return false
        }
        stateLock.lock()
        if requiresPersistentWorld && !persistenceAvailable {
            stateLock.unlock()
            return false
        }
        if let lastAtlasSubmissionUptime,
           submittedAt - lastAtlasSubmissionUptime < minimumAtlasSubmissionInterval {
            stateLock.unlock()
            return false
        }
        lastAtlasSubmissionUptime = submittedAt
        if anchorPosition == nil && provisionalAnchorPosition == nil {
            provisionalAnchorPosition = cameraPosition
        }
        let request = AtlasIntegrationRequest(
            observationID: nextObservationID,
            frame: frame,
            regions: regions,
            alignmentExclusions: (alignmentExclusions ?? regions.omittedRects)
                + compositionOmittedRects,
            compositionOmittedRects: compositionOmittedRects,
            cameraPosition: cameraPosition,
            solveWidth: solveWidth,
            captureTimestamp: timestamp,
            worldTimestamp: worldTimestamp ?? timestamp,
            captureGeneration: captureGeneration,
            atlasGeneration: generation,
            roomID: roomID,
            basisTicket: basisTicket,
            groundAnchoredPose: groundAnchoredPose,
            transitionBridgePose: transitionBridgePose,
            islandCapture: islandCapture,
            groundGeometry: groundGeometry,
            observedFloorCandidates: observedFloorCandidates,
            observedPlayerBounds: observedPlayerBounds,
            doorwayTransition: doorwayTransition?.offsettingTimestamps(
                by: (worldTimestamp ?? timestamp) - timestamp)
        )
        nextObservationID += 1
        stateLock.unlock()
        enqueueIntegration(request)
        return true
    }


    /// Runs a bounded, transient lookup against committed keyframes. Recovery
    /// frames are never appended to the session, tracker, or texture atlas.
    @discardableResult
    func submitRecoveryObservation(
        frame: CGImage,
        regions: SceneRegions,
        solveWidth: CGFloat,
        timestamp: Double,
        captureGeneration: UInt64,
        basisTicket: WorldBasisController.ObservationTicket,
        alignmentExclusions: [CGRect]? = nil,
        roomID: Int = 0,
        submittedAt: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) -> Bool {
        guard frame.width > 0, frame.height > 0, solveWidth.isFinite, solveWidth > 0 else { return false }
        stateLock.lock()
        if let lastRecoverySubmissionUptime,
           submittedAt - lastRecoverySubmissionUptime < 0.5 {
            stateLock.unlock()
            return false
        }
        lastRecoverySubmissionUptime = submittedAt
        let request = RecoveryRequest(
            frame: frame,
            solveWidth: solveWidth,
            exclusions: alignmentExclusions ?? regions.omittedRects,
            captureTimestamp: timestamp,
            captureGeneration: captureGeneration,
            atlasGeneration: generation,
            roomID: roomID,
            basisTicket: basisTicket
        )
        stateLock.unlock()
        guard let ticket = recoveryPump.submit(request, epoch: request.atlasGeneration) else { return true }
        if runsIntegrationSynchronously {
            processRecovery(ticket)
        } else {
            atlasQueue.async { [weak self] in self?.processRecovery(ticket) }
        }
        return true
    }

    private func placementBounds(
        image: CGImage,
        cameraPosition: CGPoint,
        anchorPosition: CGPoint,
        solveWidth: CGFloat
    ) -> CGRect {
        let offset = WorldPlacement.offset(
            cameraPosition: cameraPosition,
            anchorPosition: anchorPosition,
            presentationWidth: CGFloat(image.width),
            solveWidth: solveWidth,
            gain: 1
        )
        return CGRect(
            x: offset.x,
            y: offset.y,
            width: CGFloat(image.width),
            height: CGFloat(image.height)
        ).integral
    }

    private func enqueueIntegration(_ request: AtlasIntegrationRequest) {
        guard let ticket = integrationPump.submit(request, epoch: request.atlasGeneration) else { return }
        if runsIntegrationSynchronously {
            processIntegration(ticket)
        } else {
            atlasQueue.async { [weak self] in self?.processIntegration(ticket) }
        }
    }

    /// Unowned legacy portal coordinates cannot move atomically with islands.
    /// Keep their worlds gated until those connectors gain observation ownership.
    func setIslandClosureSearchAllowed(_ provider: @escaping () -> Bool) {
        integrationTransactionLock.lock()
        islandClosureSearchAllowed = provider
        integrationTransactionLock.unlock()
    }

    private func processIntegration(_ ticket: LatestFramePump<AtlasIntegrationRequest>.Ticket) {
        let request = ticket.item
        integrationStarted?()
        if integrationDelay > 0 { Thread.sleep(forTimeInterval: integrationDelay) }
        var committed: [LiveAtlasOutput.AtlasLayer]?
        var pendingPipelineUpdate: LiveWorldPipelineUpdate?
        var committedAnchor: CGPoint?
        if let maskedFrame = FrameRegionRenderer.mapFrame(
            from: request.frame,
            omitting: request.regions,
            additionalOmittedRects: request.compositionOmittedRects,
            context: atlasContext
        ) {
            let mapFrame = GameplayTutorialPromptMask.removing(from: maskedFrame, context: atlasContext)
            integrationTransactionLock.lock()
            if isCurrent(ticket: ticket, request: request) {
                if lastCaptureGeneration != request.captureGeneration {
                    worldTracker.beginVisit()
                    lastGlobalMatchTimestamp = nil
                    lastWorldTrackerTimestamp = nil
                    lastCaptureGeneration = request.captureGeneration
                }

                let resolvedObservation: WorldBasisController.ResolvedObservation?
                if let basisTicket = request.basisTicket {
                    stateLock.lock()
                    let resolver = worldPoseResolver
                    stateLock.unlock()
                    resolvedObservation = resolver?(basisTicket)
                    guard resolvedObservation != nil else {
                        integrationTransactionLock.unlock()
                        finishIntegration(ticket, committed: nil, pipelineUpdate: nil)
                        return
                    }
                } else {
                    resolvedObservation = nil
                }
                // A ground-confirmed pose belongs to this capture timestamp.
                // Re-resolving its ticket after a later ground correction
                // previously placed old pixels at the new camera pose, making
                // atlas and live ground diverge during vertical camera motion.
                let cameraPosition = request.groundAnchoredPose || request.transitionBridgePose
                    ? request.cameraPosition
                    : resolvedObservation?.worldPose ?? request.cameraPosition

                // A late frame cannot join the ordered pose graph safely. Do
                // not retain it as an atlas-only contribution: a later graph
                // correction requires one optimized pose for every retained
                // source. The latest-frame pump will shortly supply current
                // evidence at a fresh timestamp.
                let canIngestWorld = lastWorldTrackerTimestamp.map {
                    request.worldTimestamp > $0
                } ?? true
                guard canIngestWorld else {
                    integrationTransactionLock.unlock()
                    finishIntegration(ticket, committed: nil)
                    return
                }

                let usesIslandClosures = request.islandCapture != nil && worldSession != nil
                    && islandClosureSearchAllowed()
                let hasObservedGround = request.islandCapture?.measured == true
                    && (request.groundGeometry?.lines.isEmpty == false
                        || request.observedFloorCandidates?.lines.isEmpty == false)
                let closureExclusions = usesIslandClosures
                    ? request.regions.omittedRects : request.alignmentExclusions

                // A durable session never falls back to a transient ID. If its
                // evidence append fails, the graph and atlas stay untouched.
                let stored: StoredFrameObservation?
                if let session = worldSession {
                    stored = try? persistentAppender(
                        session,
                        mapFrame,
                        request.worldTimestamp,
                        cameraPosition,
                        Double(request.solveWidth),
                        closureExclusions
                    )
                    guard stored != nil else {
                        integrationTransactionLock.unlock()
                        finishIntegration(ticket, committed: nil)
                        return
                    }
                } else {
                    stored = nil
                }

                let observationID = stored?.id ?? request.observationID
                let worldUpdate: LiveWorldTrackerUpdate
                let integratedPosition: CGPoint
                let shouldSearchGlobally: Bool
                if usesIslandClosures && !hasObservedGround {
                    shouldSearchGlobally = false
                } else if !usesIslandClosures && (request.groundAnchoredPose || request.transitionBridgePose) {
                    // Legacy ground/portal geometry is not island-owned.
                    shouldSearchGlobally = false
                } else if let lastGlobalMatchTimestamp,
                   request.worldTimestamp - lastGlobalMatchTimestamp < 0.5,
                   !worldTracker.hasPendingClosure {
                    shouldSearchGlobally = false
                } else {
                    shouldSearchGlobally = true
                }
                let isConfirmationProbe = shouldSearchGlobally && worldTracker.hasPendingClosure
                    && lastGlobalMatchTimestamp.map { request.worldTimestamp - $0 < 0.5 } == true
                let priorSnapshot = worldTracker.snapshot
                let priorRevision = worldSceneRevision
                do {
                    var update = try worldTracker.ingest(
                        observationID: observationID,
                        sourceObservationID: observationID,
                        timestamp: request.worldTimestamp,
                        captureTimestamp: request.captureTimestamp,
                        frame: request.frame,
                        proposedCameraPose: cameraPosition,
                        solveWidth: Double(request.solveWidth),
                        exclusions: closureExclusions,
                        searchForClosure: shouldSearchGlobally,
                        isClosureConfirmationProbe: isConfirmationProbe,
                        localPose: resolvedObservation?.ticket.localPose,
                        captureGeneration: persistentGenerationBase &+ (resolvedObservation?.ticket.captureGeneration ?? request.captureGeneration),
                        localEpoch: resolvedObservation?.ticket.localEpoch ?? 0,
                        basisRevision: resolvedObservation?.ticket.basisRevision ?? 0,
                        roomID: request.roomID,
                        islandCapture: request.islandCapture,
                        groundGeometry: request.groundGeometry,
                        observedFloorCandidates: request.observedFloorCandidates,
                        observedPlayerBounds: request.observedPlayerBounds.flatMap { bounds -> CGRect? in
                            guard bounds.minX.isFinite, bounds.minY.isFinite,
                                  bounds.width.isFinite, bounds.height.isFinite,
                                  bounds.width > 0, bounds.height > 0 else { return nil }
                            return bounds.applying(CGAffineTransform(scaleX: request.solveWidth / CGFloat(request.frame.width),
                                                                    y: request.solveWidth / CGFloat(request.frame.width)))
                        },
                        verifyClosure: usesIslandClosures ? { [sourceProviderBox] source, proposal in
                            guard source.solveWidth == Double(request.solveWidth),
                                  let sourceGround = source.groundGeometry,
                                  let currentGround = request.observedFloorCandidates?.verificationGeometry
                                    ?? request.groundGeometry,
                                  let referenceImage = sourceProviderBox.source(for: source.sourceObservationID)
                            else { return nil }
                            return GroundPlaneClosureVerifier.verify(reference: referenceImage,
                                referenceGround: sourceGround, referencePose: source.optimizedPose,
                                current: mapFrame, currentGround: currentGround,
                                proposedPose: proposal, solveWidth: request.solveWidth)?.cameraPose
                        } : nil,
                        proposePatchClosure: usesIslandClosures ? { [sourceProviderBox] references in
                            sourceProviderBox.patchMatcher.match(frame: mapFrame,
                                solveWidth: Double(request.solveWidth), excluding: closureExclusions,
                                references: references,
                                sourceProvider: { sourceProviderBox.source(for: $0) })
                        } : nil
                    )
                    var requiresWorldRecomposition = false
                    if let transition = request.doorwayTransition,
                       let laidOut = try worldTracker.layingOutNewRoom(transition, following: update) {
                        update = laidOut
                        requiresWorldRecomposition = true
                    }
                    if let transition = request.doorwayTransition,
                       let constraint = LiveDoorwayFloorEvidence.proposal(for: transition, in: update.snapshot),
                       let connected = try? worldTracker.connectingDoorwayFloor(constraint, following: update) {
                        update = connected
                        requiresWorldRecomposition = true
                    }
                    guard let observation = update.snapshot.observations.first(where: {
                        $0.id == observationID
                    }) else { throw LiveWorldTrackerError.invalidSnapshot }
                    integratedPosition = observation.optimizedPose
                    let shouldCancel = { [weak self] in
                        guard let self else { return true }
                        return !self.isGenerationCurrent(request.atlasGeneration)
                            || self.compositionShouldCancel()
                    }
                    let composed = try composer.transaction {
                        guard composer.insert(
                            observationID: observationID,
                            maskedImage: mapFrame,
                            solveWidth: request.solveWidth,
                            cameraPosition: observation.optimizedPose,
                            captureIdentity: Int64(observation.sourceObservationID),
                            timestamp: observation.timestamp,
                            roomID: observation.roomID,
                            captureGeneration: observation.captureGeneration,
                            shouldCancel: shouldCancel
                        ) else { return false }
                        if update.acceptedClosure != nil || requiresWorldRecomposition {
                            let positions = Dictionary(uniqueKeysWithValues:
                                update.snapshot.observations.map { ($0.id, $0.optimizedPose) })
                            guard composer.replaceCameraPositions(positions,
                                baseRevision: composer.revision,
                                shouldCancel: shouldCancel) else { return false }
                        }
                        guard !shouldCancel() else { return false }
                        if let session = worldSession {
                            guard try persistentCommitter(session, update.snapshot,
                                worldSceneRevision, update.revision) else {
                                throw LiveWorldSessionStoreError.snapshotRevisionMismatch(
                                    snapshot: update.revision, disk: worldSceneRevision)
                            }
                        }
                        return true
                    }
                    guard composed else { throw LiveWorldTrackerError.invalidSnapshot }
                    if worldSession != nil { worldSceneRevision = update.revision }
                    lastWorldTrackerTimestamp = request.worldTimestamp
                    stateLock.lock()
                    committedObservationCount = update.snapshot.observations.count
                    stateLock.unlock()
                    // Extra confirmation probes do not postpone the periodic
                    // search. A failed early view must not change which later
                    // images receive the regular bounded global scan.
                    if shouldSearchGlobally && !isConfirmationProbe {
                        lastGlobalMatchTimestamp = request.worldTimestamp
                    }
                    worldUpdate = update
                } catch {
                    reconcileWorldAfterFailedCommit(
                        fallbackSnapshot: priorSnapshot,
                        fallbackRevision: priorRevision
                    )
                    integrationTransactionLock.unlock()
                    finishIntegration(ticket, committed: nil)
                    return
                }

                let snapshot = composer.snapshot
                if groundTraceEnabled,
                   lastGroundTraceTimestamp.map({ request.captureTimestamp - $0 >= 0.25 }) ?? true {
                    lastGroundTraceTimestamp = request.captureTimestamp
                    let view = WorldPlacement.offset(cameraPosition: integratedPosition,
                        anchorPosition: composer.fixedAnchorPosition ?? .zero,
                        presentationWidth: CGFloat(mapFrame.width), solveWidth: request.solveWidth, gain: 1)
                    let footprint = GroundAtlasFootprint.measure(snapshot.tiles,
                        viewX: view.x, viewWidth: CGFloat(mapFrame.width))
                    let rightEdge = view.x + CGFloat(mapFrame.width)
                    groundTraceLog.info("atlas t=\(request.captureTimestamp, privacy: .public) view=\(Double(view.x), privacy: .public),\(Double(rightEdge), privacy: .public) extent=\(footprint.minimumX, privacy: .public),\(footprint.maximumX, privacy: .public) leftPixels=\(footprint.leftPixels, privacy: .public) rightPixels=\(footprint.rightPixels, privacy: .public) paintedPixels=\(footprint.paintedPixels, privacy: .public)")
                }
                committed = snapshot.tiles.map {
                    LiveAtlasOutput.AtlasLayer(
                        id: $0.id,
                        image: $0.image,
                        bounds: $0.worldBounds,
                        revision: snapshot.revision,
                        contentBounds: snapshot.contentBounds
                    )
                }
                committedAnchor = composer.fixedAnchorPosition
                pendingPipelineUpdate = LiveWorldPipelineUpdate(
                        captureGeneration: request.captureGeneration,
                        captureTimestamp: request.captureTimestamp,
                        worldRevision: worldUpdate.revision,
                        correction: CGVector(dx: worldUpdate.correction.x, dy: worldUpdate.correction.y),
                        acceptedClosure: worldUpdate.acceptedClosure,
                        confirmedRecoveryPose: nil,
                        basisTicket: resolvedObservation?.ticket,
                        review: worldUpdate.review,
                        snapshot: worldUpdate.snapshot,
                        atlasTiles: committed ?? []
                    )
            }
            integrationTransactionLock.unlock()
        }
        finishIntegration(ticket, committed: committed, committedAnchor: committedAnchor,
                          pipelineUpdate: pendingPipelineUpdate)
    }

    private func processRecovery(_ ticket: LatestFramePump<RecoveryRequest>.Ticket) {
        let request = ticket.item
        var update: LiveWorldPipelineUpdate?
        integrationTransactionLock.lock()
        if isCurrentRecovery(ticket: ticket, request: request) {
            if lastCaptureGeneration != request.captureGeneration {
                worldTracker.beginVisit()
                lastGlobalMatchTimestamp = nil
                lastWorldTrackerTimestamp = nil
                lastCaptureGeneration = request.captureGeneration
            }
            if lastRecoveryCaptureGeneration != request.basisTicket.captureGeneration
                || lastRecoveryLocalEpoch != request.basisTicket.localEpoch {
                worldTracker.beginRecoveryEpoch()
                lastRecoveryCaptureGeneration = request.basisTicket.captureGeneration
                lastRecoveryLocalEpoch = request.basisTicket.localEpoch
            }
            stateLock.lock()
            let resolver = worldPoseResolver
            stateLock.unlock()
            if let resolved = resolver?(request.basisTicket),
               let recovered = try? worldTracker.recover(
                   frame: request.frame,
                   proposedCameraPose: resolved.worldPose,
                   solveWidth: Double(request.solveWidth),
                   exclusions: request.exclusions,
                   roomID: request.roomID
               ) {
                let placement = recovered.confirmedPlacement
                update = LiveWorldPipelineUpdate(
                    captureGeneration: request.captureGeneration,
                    captureTimestamp: request.captureTimestamp,
                    worldRevision: recovered.revision,
                    correction: CGVector(
                        dx: placement?.correction.x ?? 0,
                        dy: placement?.correction.y ?? 0
                    ),
                    acceptedClosure: nil,
                    confirmedRecoveryPose: placement?.cameraPose,
                    basisTicket: resolved.ticket,
                    review: recovered.review,
                    snapshot: recovered.snapshot,
                    atlasTiles: currentAtlasTiles()
                )
            }
        }
        integrationTransactionLock.unlock()
        if lastRecoveryDiagnosticAt.map({ request.captureTimestamp - $0 >= 1 }) ?? true {
            lastRecoveryDiagnosticAt = request.captureTimestamp
            let rejections = String(describing: update?.review.rejections)
            atlasAdmissionLog.info(
                "recovery accepted=\(update != nil, privacy: .public) confirmed=\(update?.confirmedRecoveryPose != nil, privacy: .public) rejections=\(rejections, privacy: .public) generation=\(request.captureGeneration, privacy: .public)"
            )
        }
        let completion = recoveryPump.complete(ticket)
        if completion.shouldAcceptResult, let update {
            currentWorldUpdateHandler()?(update)
        }
        if let next = completion.next {
            if runsIntegrationSynchronously { processRecovery(next) }
            else { atlasQueue.async { [weak self] in self?.processRecovery(next) } }
        }
    }

    private func isCurrent(
        ticket: LatestFramePump<AtlasIntegrationRequest>.Ticket,
        request: AtlasIntegrationRequest
    ) -> Bool {
        guard ticket.epoch == request.atlasGeneration else { return false }
        stateLock.lock()
        defer { stateLock.unlock() }
        return generation == request.atlasGeneration
    }

    private func isCurrentRecovery(
        ticket: LatestFramePump<RecoveryRequest>.Ticket,
        request: RecoveryRequest
    ) -> Bool {
        guard ticket.epoch == request.atlasGeneration else { return false }
        stateLock.lock()
        defer { stateLock.unlock() }
        return generation == request.atlasGeneration
    }

    private func reconcileWorldAfterFailedCommit(
        fallbackSnapshot: LiveWorldSnapshot,
        fallbackRevision: Int
    ) {
        // Image transaction has already rolled back. Never silently adopt an
        // externally advanced disk graph while retaining older atlas pixels.
        worldTracker = (try? LiveWorldTracker(snapshot: fallbackSnapshot)) ?? LiveWorldTracker()
        worldSceneRevision = fallbackRevision
        if let session = worldSession {
            let state = try? session.load()
            if state?.snapshot != fallbackSnapshot || state?.sceneRevision != fallbackRevision {
                stateLock.lock()
                persistenceAvailable = false
                persistenceIssue = "Saved world changed during integration. Reload the atlas before recording more observations."
                stateLock.unlock()
            }
        }
        lastGlobalMatchTimestamp = nil
        lastWorldTrackerTimestamp = nil
    }

    private func finishIntegration(
        _ ticket: LatestFramePump<AtlasIntegrationRequest>.Ticket,
        committed: [LiveAtlasOutput.AtlasLayer]?,
        committedAnchor: CGPoint? = nil,
        pipelineUpdate: LiveWorldPipelineUpdate? = nil
    ) {
        let completion = integrationPump.complete(ticket)
        if completion.shouldAcceptResult, let committed {
            var didPublish = false
            stateLock.lock()
            if generation == ticket.item.atlasGeneration {
                // The compositor chooses its origin from the first committed
                // optimized pose. Publish that same origin with its pixels;
                // a submitted frame can fail or resolve to a different pose.
                if let committedAnchor {
                    anchorPosition = committedAnchor
                    provisionalAnchorPosition = nil
                }
                atlasRevision = committed.first?.revision ?? atlasRevision &+ 1
                atlasSnapshot = committed
                atlasWorldRevision = pipelineUpdate?.worldRevision ?? atlasWorldRevision
                didPublish = true
            }
            stateLock.unlock()
            if didPublish, let pipelineUpdate {
                currentWorldUpdateHandler()?(pipelineUpdate)
            }
        }
        if let next = completion.next {
            if runsIntegrationSynchronously {
                processIntegration(next)
            } else {
                atlasQueue.async { [weak self] in self?.processIntegration(next) }
            }
        }
    }

    private func currentAtlasTiles() -> [LiveAtlasOutput.AtlasLayer] {
        stateLock.lock()
        defer { stateLock.unlock() }
        return atlasSnapshot
    }

    private func currentWorldUpdateHandler() -> ((LiveWorldPipelineUpdate) -> Void)? {
        stateLock.lock()
        defer { stateLock.unlock() }
        return worldUpdateHandler
    }

    private func restorePersistedWorld(_ snapshot: LiveWorldSnapshot) {
        let restorationGeneration = generation
        atlasQueue.async { [weak self] in
            guard let self else { return }
            self.integrationTransactionLock.lock()
            defer { self.integrationTransactionLock.unlock() }
            for observation in snapshot.observations.sorted(by: { $0.id < $1.id }) {
                guard self.isGenerationCurrent(restorationGeneration) else { return }
                self.restorationObservationStarted?(observation.id)
                guard self.isGenerationCurrent(restorationGeneration) else { return }
                guard let source = self.sourceProviderBox.source(
                    for: observation.sourceObservationID
                ), let image = self.compositionMaskedSource(
                    source, observation: observation
                ) else {
                    continue
                }
                let inserted = self.composer.insert(
                    observationID: observation.id,
                    maskedImage: image,
                    solveWidth: CGFloat(observation.solveWidth),
                    cameraPosition: observation.optimizedPose,
                    captureIdentity: Int64(observation.sourceObservationID),
                    timestamp: observation.timestamp,
                    roomID: observation.roomID,
                    captureGeneration: observation.captureGeneration,
                    shouldCancel: { [weak self] in
                        guard let self else { return true }
                        return !self.isGenerationCurrent(restorationGeneration)
                    }
                )
                guard inserted || self.isGenerationCurrent(restorationGeneration) else { return }
            }
            let restored = self.composer.snapshot
            let restoredAnchor = self.composer.fixedAnchorPosition
            let layers = restored.tiles.map {
                LiveAtlasOutput.AtlasLayer(
                    id: $0.id,
                    image: $0.image,
                    bounds: $0.worldBounds,
                    revision: restored.revision,
                    contentBounds: restored.contentBounds
                )
            }
            self.stateLock.lock()
            if self.generation == restorationGeneration {
                self.anchorPosition = restoredAnchor
                if restoredAnchor != nil { self.provisionalAnchorPosition = nil }
                self.atlasRevision = restored.revision
                self.atlasSnapshot = layers
                self.atlasWorldRevision = self.worldTracker.snapshot.mapRevision
            }
            self.stateLock.unlock()
        }
    }

    private func isGenerationCurrent(_ expected: UInt64) -> Bool {
        stateLock.lock()
        defer { stateLock.unlock() }
        return generation == expected
    }

    private func compositionMaskedSource(
        _ source: CGImage,
        observation: LiveWorldObservation
    ) -> CGImage? {
        let source = GameplayTutorialPromptMask.removing(from: source, context: atlasContext)
        stateLock.lock()
        let provider = roomCompositionBoundsProvider
        stateLock.unlock()
        guard let provider else { return source }
        let omitted = VisualRoomCompositionMask.omittedRects(
            bounds: provider(observation.roomID),
            cameraPosition: observation.optimizedPose,
            solveWidth: CGFloat(observation.solveWidth),
            frameSize: CGSize(width: source.width, height: source.height)
        )
        guard !omitted.isEmpty else { return source }
        return FrameRegionRenderer.mapFrame(
            from: source,
            omitting: .startup(in: CGRect(
                x: 0, y: 0, width: source.width, height: source.height
            )),
            additionalOmittedRects: omitted,
            context: atlasContext
        )
    }

    /// Caller owns `integrationTransactionLock`.
    private func rebuildAtlasForRoomCompositionLocked(
        snapshot: LiveWorldSnapshot
    ) {
        let anchor = snapshot.anchoredObservationID.flatMap { anchorID in
            snapshot.observations.first(where: { $0.id == anchorID })?.optimizedPose
        }
        composer.reset(anchorPosition: anchor)
        for observation in snapshot.observations.sorted(by: { $0.id < $1.id }) {
            guard let source = sourceProviderBox.source(
                for: observation.sourceObservationID
            ), let image = compositionMaskedSource(
                source, observation: observation
            ) else { continue }
            _ = composer.insert(
                observationID: observation.id,
                maskedImage: image,
                solveWidth: CGFloat(observation.solveWidth),
                cameraPosition: observation.optimizedPose,
                captureIdentity: Int64(observation.sourceObservationID),
                timestamp: observation.timestamp,
                roomID: observation.roomID,
                captureGeneration: observation.captureGeneration
            )
        }
        publishComposerSnapshotLocked()
    }

    /// Caller owns `integrationTransactionLock`.
    private func publishComposerSnapshotLocked() {
        let snapshot = composer.snapshot
        let composerAnchor = composer.fixedAnchorPosition
        let layers = snapshot.tiles.map {
            LiveAtlasOutput.AtlasLayer(
                id: $0.id,
                image: $0.image,
                bounds: $0.worldBounds,
                revision: snapshot.revision,
                contentBounds: snapshot.contentBounds
            )
        }
        stateLock.lock()
        anchorPosition = composerAnchor
        if composerAnchor != nil { provisionalAnchorPosition = nil }
        atlasRevision = snapshot.revision
        atlasSnapshot = layers
        atlasWorldRevision = worldTracker.snapshot.mapRevision
        stateLock.unlock()
    }
}
