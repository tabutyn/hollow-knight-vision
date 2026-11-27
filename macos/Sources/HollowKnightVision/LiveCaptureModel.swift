import AppKit
import ApplicationServices
import Combine
import CoreImage
import CoreMedia
import OSLog
import ScreenCaptureKit

/// Live capture and workspaces, with independent vision and Hacker atlases.
final class LiveCaptureModel: NSObject, ObservableObject, @unchecked Sendable {
    /// A fresh atlas must establish persistent ground before replay moves the
    /// camera. A fixed delay is insufficient because capture/reset work can be
    /// queued, and this route starts moving only 0.48 seconds after playback.
    private static let pathPlaybackReadinessPollSeconds: TimeInterval = 0.1
    // Restoring from the far side of a large route teleports the Knight first,
    // but Hollow Knight eases its camera back over several seconds. Keep the
    // bootstrap gate closed until that motion really settles instead of making
    // the first replay fail and relying on a second reset.
    private static let pathPlaybackReadinessTimeoutSeconds: TimeInterval = 30
    private static let pathPlaybackRequiredReadyChecks = 4
    private static let pathPlaybackPoseTolerance = 0.05

    @Published private(set) var isCapturing = false
    @Published private(set) var status = "Ready."
    @Published private(set) var gameLostCountdown: Int?
    @Published private(set) var inputControlStatus = "Controls unavailable"
    @Published private(set) var gameMode: HollowKnightRuntimeMode
    private var thinkingMenuLease: UUID?
    private var thinkingScreens = ThinkingScreenHistory()
    var thinkingStateVisit: UInt64? { thinkingScreens.latest == nil ? nil : thinkingScreens.visit }
    var onThinkingStateChange: ((CGImage) -> Void)? {
        didSet { if onThinkingStateChange == nil { thinkingScreens.lostState() } }
    }
    private var latestSkillFrame: BotDemoMenuObservation?
    @Published private(set) var notebookProfile: SaveProfileAtlas?
    private var pendingNotebookProfile: SaveProfileAtlas?
    @Published private(set) var readingOpeningCutscene = false
    private var skillObservation: BotObservation?
    var skillReading: SkillReading?
    var skillContext: String { liveGameStatus == "Gameplay" ? activeRoomStatus : (liveGameStatus ?? "Game capture") }
    var skillGeneration: UInt64 { currentStatusGeneration }

    private var thinkingOutcome: (generation: UInt64, sequence: Int, text: String)?

    func thinkingSignals(profile: UUID?, readsText: Bool) -> ThinkingSignals? {
        guard isCapturing else { return nil }
        let screen = thinkingScreens.latest.flatMap { $0.generation == skillGeneration ? $0 : nil }
        if let screen, let page = screen.page {
            return ThinkingSignals(profile: profile, generation: skillGeneration, room: page.stateID, mode: gameMode.rawValue,
                selectedMenu: page.selected, menuImage: screen.image, menuPage: page, stateID: screen.id, stateVisit: thinkingStateVisit,
                previousStateID: thinkingScreens.previousStateID, previousConfirmedElement: thinkingScreens.previousConfirmedElement)
        }
        let sight = currentSkillSight()
        let lines = readsText ? (sight?.lines ?? []).map(RememberedGameText.key) : []
        let (batch, signObservation) = objectInferenceStateLock.withLock { (latestObjectDetectionBatch, latestSignObservation) }
        let objects: [LiveObjectDetection]
        if sight?.gameplay == true, let batch, batch.captureGeneration == skillGeneration,
           (0...1.5).contains(ProcessInfo.processInfo.systemUptime - batch.sourceTimestamp) {
            objects = batch.detections.filter { $0.sourceFrameIdentifier == batch.sourceFrameIdentifier }
        } else { objects = [] }
        let sign = signObservation.flatMap { observation in
            sight?.gameplay == true && observation.generation == skillGeneration && observation.room == skillContext
                && (0...1.5).contains(ProcessInfo.processInfo.systemUptime - observation.capturedAt) ? observation : nil
        }
        let signs = Set((sign?.detections ?? []).compactMap(\.trackIdentifier))
        let npcs = Set(objects.filter { $0.classIdentifier.split { $0 == "." || $0 == "-" }.contains("npc") }.compactMap(\.trackIdentifier))
        let outcome = thinkingOutcome.flatMap { $0.generation == skillGeneration ? $0 : nil }
        return ThinkingSignals(profile: profile, generation: skillGeneration, room: skillContext, mode: gameMode.rawValue,
            sign: lines.contains("inspect"), npc: lines.contains("listen"), signTracks: signs, npcTracks: npcs, scene: sight?.scene, activeRoute: botPlayer.activeRoute, blocked: sight?.scene != nil && skillObservation?.blockade?.mark.crossed == false,
            outcomeID: outcome?.sequence, outcome: outcome?.text,
            signImage: sign?.image, signBounds: sign?.detections.map(\.normalizedRect) ?? [], signSourceID: sign?.sourceID,
            recognizedGameplay: screen?.id == LabelingContext.game.storageIdentifier,
            stateID: screen?.id, stateVisit: thinkingStateVisit, awaitingRecognition: screen == nil)
    }

    private func publishThinkingScreen(status: LiveGameStatus?, recognition: LiveGameStatus?,
                                       page: ThinkingMenuPage?, stencilImage: CGImage?, frame: CGImage, generation: UInt64) {
        guard let onThinkingStateChange else { return }
        let id: String
        let menu: ThinkingMenuPage?
        let image: CGImage
        if readingOpeningCutscene, page == nil, recognition == nil {
            id = "opening-cutscene"
            menu = ThinkingMenuPage(stateID: id)
            image = frame
        } else {
            guard let status, let context = status.context.labelingContext else {
                thinkingScreens.lostState()
                return
            }
            // A tracker hold may outlast the pixels that established this state.
            guard status.context == recognition?.context else { return }
            id = context.storageIdentifier
            menu = status.context.isGameplay ? nil : (page ?? ThinkingMenuPage(stateID: id))
            image = stencilImage ?? frame
        }
        if thinkingScreens.record(.init(id: id, generation: generation, page: menu, image: image)) {
            onThinkingStateChange(image)
        }
    }

    func currentSkillSight() -> SkillSight? {
        let now = ProcessInfo.processInfo.systemUptime
        guard isCapturing, let frame = latestSkillFrame, frame.generation == currentStatusGeneration,
              (0...0.45).contains(now - frame.capturedAt) else { return nil }
        let reading = skillReading.flatMap { entry in
            entry.context == skillContext && (0...2.5).contains(now - entry.capturedAt) ? entry : nil
        }
        let scene = skillObservation.flatMap { row -> BotPolicyScene? in
            guard row.generation == frame.generation, row.isUsable,
                  (0...BotObservationContract.maximumCaptureAge).contains(now - row.capturedAt),
                  BotDomain.of(row) == (isHackerWorkspaceActive() ? .hacker : .gameplay) else { return nil }
            return row.policyScene
        }
        return SkillSight(context: skillContext, capturedAt: frame.capturedAt, generation: frame.generation,
                          gameplay: liveGameStatus == "Gameplay" && frame.context == nil,
                          menu: frame.context, selected: frame.selectedIdentifier, lines: reading?.visible ?? [],
                          hasAbilityControls: receiverCapabilities.contains("skill-controls-v1"), reading: reading, scene: scene, menuPage: frame.menuPage)
    }

    @Published private(set) var receiverCapabilities = Set<String>()
    @Published private(set) var labelPauseState: GamePauseState = .unavailable
    @Published private(set) var isPathRecording = false
    @Published private(set) var isPathPlaybackActive = false
    @Published private(set) var isPathCheckpointPending = false
    @Published private(set) var pathPlaybackIteration: Int?
    @Published private(set) var pathRecordingStartedAt: Date?
    @Published private(set) var lastRecordedPathURL: URL?
    @Published private(set) var pathRecordingIssue: String?
    @Published var playerTestDraft = PlayerTestState.defaults
    @Published var randomizePlayerStateAfterLabel = true {
        didSet {
            UserDefaults.standard.set(
                randomizePlayerStateAfterLabel,
                forKey: Self.randomizePlayerStateAfterLabelDefaultsKey
            )
        }
    }
    @Published private(set) var playerOpsBusy = false
    @Published private(set) var playerOpsStatus = "Connect to gameplay to load player state."
    private var playerOpsApplyQueued = false
    @Published private(set) var localizationStatus = "Tracking"
    @Published private(set) var activeRoomStatus = "Room 1"
    @Published private(set) var hasGameplayStarted = false
    private var atlasPathFrameEvidence = LiveAtlasPathFrameEvidence()

    func hasAtlasPathEvidence(for image: CGImage?) -> Bool {
        atlasPathFrameEvidence.admits(image.flatMap { LivePresentationTiming.shared.timestamp(for: $0) })
    }
    var hasHackerAtlasPathEvidence: Bool {
        hackerLiveImage != nil && atlasPathFrameEvidence.admitsHackerPreview
    }
    /// Explicit developer review only. Does not set the recognized state,
    /// change input, relax tracking acceptance, or enable atlas writes.
    var groundReviewAvailable: Bool {
        hasGameplayStarted || ProcessInfo.processInfo.arguments.contains("--review-ground-scene")
    }
    @Published private(set) var worldStorageIssue: String?
    @Published private(set) var atlasImage: CGImage?
    @Published private(set) var atlasBounds: CGRect?
    @Published private(set) var atlasRevision: UInt64?
    @Published private(set) var atlasTiles = [LiveAtlasOutput.AtlasLayer]()
    @Published private(set) var liveImage: CGImage?
    @Published private(set) var hackerAtlasTiles = [LiveAtlasOutput.AtlasLayer]()
    @Published private(set) var hackerAtlasRooms = [HackerAtlasRoom]()
    @Published private(set) var hackerRoomConnections = [HackerRoomConnection]()
    @Published private(set) var hackerRoomPositionOverrides = [UUID: CGPoint]()
    @Published private(set) var hackerAtlasBounds: CGRect?
    @Published private(set) var hackerLiveImage: CGImage?
    @Published private(set) var hackerLiveBounds = CGRect.zero
    @Published private(set) var hackerMapFocus = CGPoint.zero
    @Published private(set) var hackerCameraTransform: GroundTruthCameraTransform?
    private var hackerRoomLayout = HackerRoomLayout.empty
    @Published private(set) var hackerAtlasSavedStates = [AtlasSavedState]()
    @Published private(set) var hackerAtlasAutoSave = AtlasAutoSaveSummary(registrationCount: 0, createdAt: Date())
    @Published private(set) var hackerAtlasManagementBusy = false
    private var hackerAtlasBlocksGameplay = false
    @Published private(set) var hackerAtlasManagementIssue: String?
    private var hackerAtlasRestored = false
    private var hackerAutoSaveScheduled = false
    private var hackerAutoSaveAllowed = true
    private var hackerWorkspaceWanted = false
    @Published private(set) var labelImage: CGImage?
    @Published private(set) var liveBounds = CGRect.zero
    @Published private(set) var mapFocus = CGPoint.zero
    @Published private(set) var presentationCorrectionOffset = CGPoint.zero
    @Published var groundEdgeEnabled = false
    @Published private(set) var groundEdgeImage: CGImage?
    @Published var groundDetectEnabled = false
    @Published private(set) var groundDetectImage: CGImage?
    @Published var groundStageEnabled = false
    @Published private(set) var groundStageImage: CGImage?
    @Published var cleanedEnabled = false
    @Published private(set) var cleanedImage: CGImage?
    @Published var groundHypothesesEnabled = false
    @Published private(set) var groundHypothesisImage: CGImage?
    @Published private(set) var groundHypothesisTracking = GroundHypothesisTrackingResult.empty
    /// Debug/evaluation camera truth. The lock-backed getter avoids publishing
    /// every mod sample and therefore adds no display-rate main-queue work.
    var groundTruthCameraTransform: GroundTruthCameraTransform? {
        groundTruthCameraStateLock.lock()
        let latest = latestGroundTruthCameraSample
        groundTruthCameraStateLock.unlock()
        guard let (sample, observedAt) = latest else { return nil }
        let size = liveBounds.width > 1 && liveBounds.height > 1
            ? liveBounds.size
            : CGSize(
                width: CGFloat(HollowKnightCaptureConfiguration.outputWidth),
                height: CGFloat(HollowKnightCaptureConfiguration.outputWidth)
                    / HollowKnightCaptureConfiguration.gameplayAspectRatio
            )
        return GroundTruthCameraTransform(
            sample: sample,
            observedAt: observedAt,
            frameSize: size
        )
    }
    @Published var transitionDiagnosticsEnabled = false
    @Published private(set) var transitionPortals = [VisualRoomPortal]()
    @Published var coarseMotionDiagnosticsEnabled = false
    @Published private(set) var coarseMotionDiagnosticImage: CGImage?
    @Published private(set) var coarseMotionDiagnosticStatus = "Coarse Motion · ready"
    private var coarseDiagnosticPrevious: (grid: LowResolutionMotionGrid, timestamp: Double)?
    @Published var stencilDetectionEnabled = false
    @Published private(set) var menuStencilImage: CGImage?
    // Admission is owned by sampleQueue; recognition is serialized on its
    // worker. Hacker reads recent results without blocking live capture.
    private lazy var menuStencilWorker = LiveMenuStencilWorker(
        tracker: MenuStencilTracker(
            loadsDeployedCatalog: false,
            minimumSearchInterval: 0.1,
            selectorCalibrationURL: MenuStencilCalibration.writableURL(),
            selectorCalibrationMirrorURL: MenuStencilCalibration.projectMirrorURL()
        ),
        didProcess: { [telemetry] elapsed in
            telemetry.record(.menuStencil, seconds: elapsed)
        }
    )
    @Published private(set) var hudStencilImage: CGImage?
    // Owned by sampleQueue. Each result travels with its source capture.
    private var hudStencilTracker = HUDStencilTracker(template: nil)
    @Published var objectDetectionEnabled = false
    @Published var objectDetectionRawEnabled = false
    @Published var objectDetectionPixelsEnabled = false
    @Published private(set) var objectDetectionPixelImage: CGImage?
    private var groundTuning = GroundTheoryTuning.default
    private var semanticGroundTuning = GroundTheoryTuning.semanticDefault
    @Published private(set) var liveWorldReview: LiveWorldTrackerReview?
    @Published private(set) var liveWorldSnapshot: LiveWorldSnapshot?
    @Published private(set) var ownedGroundLines = [LiveWorldGroundLine]()
    @Published private(set) var ownedGroundOverlayLines = [GroundHypothesisAtlasLine]()
    @Published private(set) var islandPlacementOverlay = LayerSceneFeatureReviewOverlay(markers: [], lines: [])
    var hasEstimatedIslandPlacement: Bool {
        liveWorldSnapshot?.trackingIslands.contains(where: \.isProvisional) == true
    }
    var usesIslandGroundProjection: Bool {
        liveWorldSnapshot?.trackingIslands.contains { $0.placement != .legacyUnknown } ?? false
    }
    private var groundOverlayRevision = -1
    private var groundOverlayScale: CGFloat = 0
    private var groundOverlayAnchor = CGPoint.zero
    private(set) lazy var visualPaths = makePathMap()
    private(set) lazy var hackerPaths = makePathMap()
    private var lastHackerPathRoom: String?
    // The synchronizer supplies exact rendered-frame joins. Route evaluation is
    // main-thread serialized and cached so paired domains cannot advance it twice.
    private let sharedPathMatchLock = NSLock()
    private var sharedPathMatches = [HackerMatchedFrame]()
    private var sharedPathResults = [(generation: UInt64, timestamp: Double, row: BotObservation)]()
    private var sharedPathLastTimestamp = -Double.infinity
    private var sharedPathGeneration: UInt64?

    private func sharedHackerPath(for row: BotObservation) -> BotObservation? {
        if let cached = sharedPathResults.last(where: { $0.generation == row.generation && $0.timestamp == row.captureTimestamp }) {
            return cached.row
        }
        sharedPathMatchLock.lock()
        let match = sharedPathMatches.last { $0.capture.captureGeneration == row.generation && $0.capture.capturedAt == row.captureTimestamp }
        sharedPathMatchLock.unlock()
        guard let match else { return nil }
        if sharedPathGeneration != row.generation {
            sharedPathGeneration = row.generation
            sharedPathLastTimestamp = -Double.infinity
            sharedPathResults.removeAll()
            if let previous = lastHackerPathRoom { hackerPaths.interrupt(room: previous) }
            lastHackerPathRoom = nil
        }
        guard row.captureTimestamp > sharedPathLastTimestamp,
              let frame = botHackerObservations.makeFrame(match, now: ProcessInfo.processInfo.systemUptime),
              let camera = GroundTruthCameraTransform(sample: match.sample, observedAt: match.sampleObservedAt,
                  frameSize: CGSize(width: match.capture.image.width, height: match.capture.image.height)) else { return nil }
        sharedPathLastTimestamp = row.captureTimestamp
        var result = frame.observation
        let document = botHumanGround.snapshot
        let room = hackerAtlasRooms.first { $0.sceneName == camera.sceneName }
        let calibration = room.flatMap { AtlasPathCalibration.hacker(room: $0, document: document, camera: camera) }
            ?? .init(scene: camera.sceneName, origin: .zero, scale: .init(x: camera.pixelsPerWorldUnitX, y: camera.pixelsPerWorldUnitY))
        let key = room?.id.uuidString ?? "scene/" + camera.sceneName
        if let previous = lastHackerPathRoom, previous != key { hackerPaths.interrupt(room: previous) }
        lastHackerPathRoom = key
        hackerPaths.setAuthored(room: key, calibration: calibration, document: document)
        let center = calibration.local(camera.cameraWorld)
        let local = CGPoint(x: center.x - camera.frameSize.width / 2, y: center.y - camera.frameSize.height / 2)
        hackerPaths.observe(room: key, camera: local, width: camera.frameSize.width, row: &result, grounded: match.sample.grounded)
        sharedPathResults.append((row.generation, row.captureTimestamp, result))
        if sharedPathResults.count > 60 { sharedPathResults.removeFirst(sharedPathResults.count - 60) }
        return result
    }
    private var visualBaseRooms = Set<Int>()
    private(set) var visualPathRoom = ""
    private(set) var visualPathOffset = CGPoint.zero
    private var visualPathHandoff: (generation: UInt64, room: Int, timestamp: Double)?
    private func makePathMap() -> AtlasPathMap {
        let map = AtlasPathMap()
        map.onChange = { [weak self] in self?.objectWillChange.send() }
        return map
    }

    private func refreshHackerPaths() {
        let document = botHumanGround.snapshot
        for room in hackerAtlasRooms {
            if let calibration = AtlasPathCalibration.hacker(room:room,document:document,camera:hackerCameraTransform) {
                hackerPaths.setAuthored(room:room.id.uuidString,calibration:calibration,document:document)
                hackerPaths.setRegisteredCoverage(room:room.id.uuidString,coverage:room.botCoverage)
            }
        }
    }
    private var botAtlasCoverage = BotAtlasCoverage()
    private var pairedVisualAtlas: (recording: UUID, frame: BotObservation)?
    private var publishedIslandProjection: LiveIslandWorldProjection?
    // Protected by trackingStateLock; capture diagnostics may run off-main.
    private var trackingIslandProjection: LiveIslandWorldProjection?
    private struct PresentedIslandFrame {
        let rawBounds: CGRect
        let rawFocus: CGPoint
        let rawPose: CGPoint
        let island: LiveIslandCapture?
        let scale: CGFloat
        let anchor: CGPoint
    }
    private var presentedIslandFrame: PresentedIslandFrame?
    @Published private(set) var localFeatureTracking = FeatureTrackingResult.empty
    @Published private(set) var groundReference: GroundReferenceEstimate?
    @Published private(set) var activeObjectModelSummary: String?
    @Published private(set) var activeObjectModelVersion: String?
    @Published private(set) var liveObjectDetectionCount = 0
    @Published private(set) var liveObjectDetectionClassIdentifiers = [String]()
    private(set) var objectInferenceMilliseconds: Double?
    @Published private(set) var objectInferenceIssue: String?
    @Published private(set) var liveGameStatus: String?
    @Published private(set) var liveGameContextIdentifier: String?
    @Published private(set) var liveSelectedMenuOption: String?
    // Main-queue diagnostic exposed only through the local automation status
    // reply. It deliberately adds no dashboard text.
    private var liveMenuStencilDiagnostic: String?
    @Published private(set) var atlasSavedStates = [AtlasSavedState]()
    @Published private(set) var atlasAutoSave = AtlasAutoSaveSummary(
        registrationCount: 0,
        createdAt: Date()
    )
    @Published private(set) var atlasManagementBusy = false
    @Published private(set) var atlasManagementIssue: String?

    private static let randomizePlayerStateAfterLabelDefaultsKey =
        "HollowKnightVision.randomizePlayerStateAfterLabel"

    private let sampleQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.samples",
        qos: .userInteractive,
        autoreleaseFrequency: .workItem
    )
    private let menuStencilReloadQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.menu-stencil-reload",
        qos: .utility,
        autoreleaseFrequency: .workItem
    )
    private let renderQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.render",
        qos: .userInitiated,
        autoreleaseFrequency: .workItem
    )
    private let hackerPresentationQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.hacker-presentation",
        qos: .userInteractive,
        autoreleaseFrequency: .workItem
    )
    private let hackerAtlasQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.hacker-atlas",
        qos: .userInitiated,
        autoreleaseFrequency: .workItem
    )
    private let atlasManagementQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.atlas-management",
        qos: .userInitiated,
        autoreleaseFrequency: .workItem
    )
    private let atlasMetadataQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.atlas-metadata",
        qos: .utility,
        autoreleaseFrequency: .workItem
    )
    private let inputPathStoreQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.input-path-store",
        qos: .utility,
        autoreleaseFrequency: .workItem
    )
    private let visionQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.vision",
        qos: .userInitiated,
        autoreleaseFrequency: .workItem
    )
    private let objectInferenceQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.object-inference",
        qos: .userInitiated,
        autoreleaseFrequency: .workItem
    )
    private let poseImageContext = GroundMetalResources.shared.makeImageContext()
    private let framePump = LatestFramePump<RenderFrame>()
    private let visionFramePump = LatestFramePump<VisionFrame>()
    private let objectInferencePump = LatestFramePump<ObjectInferenceFrame>()
    private let hackerPresentationPump = LatestFramePump<HackerMatchedFrame>()
    private let hackerAtlasPump = LatestFramePump<HackerPreparedFrame>()
    private let telemetry = FramePipelineTelemetry()
    private let trackingStateLock = NSLock()
    private let visionStateLock = NSLock()
    private let objectInferenceStateLock = NSLock()
    private let gameStartupDetector = GameStartupDetector()
    private let autoNavigateToGameplay: Bool
    private var gameStartupCoordinator = GameStartupCoordinator()
    private let persistentFeatureTracker = PersistentFeatureTracker()
    // Twelve 60 Hz observations admit motion-verified, cleaned floor evidence
    // before a newly exposed tile travels behind the player.
    private let groundHypothesisTracker: GroundHypothesisTracker
    private var trackingIslandCoordinator = LiveTrackingIslandCoordinator()
    private let gameplayGym: Bool
    private var gymPreviousAction = BotAction.neutral
    private var gymObservation: BotObservation?
    private let visualReplayMode: Bool
    private var visualReplayInput: VisualReplayGroundInput?
    private var visualReplayDirection: VisualRoomDirection?
    private var visualReplayLastJump = -Double.infinity
    private var visualReplayWorldTimestamp: Double?
    /// Semantic ground uses the tracker pose but never participates in its
    /// camera solve. This lets labeled edge tuning change the green map without
    /// changing the feature anchors that make return tracking stable.
    private let semanticGroundAtlas = GroundSemanticAtlas()
    private let transitionMotionBridge = TransitionMotionBridge()
    private let calibratedSceneMotion = CalibratedSceneMotionTracker()
    private var visualFallMotion = VisualFallMotionPrior()
    private var groundPlacementRecovery = GroundPlacementRecovery()
    private let visualRoomTracker = VisualRoomTransitionTracker()
    /// Vision-queue-owned runtime state. Unlike the published diagnostic
    /// snapshot, this updates even when every ground overlay is disabled.
    private var pathPlaybackGroundReadiness = GroundHypothesisTrackingResult.empty
    private let groundReferenceTracker = GroundReferenceTracker()
    private let featureCorrectionPolicy = FeatureCorrectionPolicy()
    private let worldBasis = WorldBasisController()
    private let objectDetector = LiveObjectDetector()
    private let modelVersionStore = LabelingModelVersionStore()
    private lazy var featureRefinement = FeatureRefinementCoordinator<FeatureTrackingResult>(
        ownerQueue: visionQueue
    )
    private let performanceLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "performance"
    )
    private let atlasAdmissionLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "atlas-admission"
    )
    private let groundTraceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private let groundTraceLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "ground-trace")
    private var lastGroundGeometryTraceAt: Double?
    private let groundFrameAudit = GroundFrameAudit.launch()
    private let rawGroundFrameAudit = RawGroundFrameAudit.launch()
    // Vision and the render queue can first receive work at the same time;
    // construct this thread-safe pipeline before either queue starts.
    private let atlasPipeline: LiveAtlasPipeline
    private let roomTopologyURL: URL
    private var roomTopologyWritesEnabled = true
    private let atlasStateStore: AtlasStateStore
    private let hackerAtlasStateStore: AtlasStateStore
    private let hackerAtlasActiveURL: URL
    private let inputPathRecorder = InputPathRecorder()
    let botDemonstrations = BotDemonstrationController()
    let botPlayer: BotPlaybackController
    private let botEvaluation = BotEvaluationRecorder()
    let botDemo = BotDemoController()
    private let botHumanGround = BotHumanGroundTerrainCache()
    private let hackerBotQueue = DispatchQueue(label: "hkv.bot.hacker-walls", qos: .userInitiated)
    private lazy var botHackerObservations = BotHackerObservationSource(ground: botHumanGround)
    private var visualBotGate = BotObservationPublicationGate() // main thread
    private var hackerBotGate = BotObservationPublicationGate() // main thread
    private var botVisualKnightMemory = BotVisualKnightMemory() // main thread
    private var blockadeVisits = [BotDomain: BotBlockadeVisit]()
    private var blockadeResetAt = -Double.infinity

    private func resetLiveBlockades(at timestamp: Double = ProcessInfo.processInfo.systemUptime) {
        guard timestamp > blockadeResetAt else { return }
        objectWillChange.send()
        blockadeVisits.removeAll()
        blockadeResetAt = timestamp
    }

    func blockadeOverlay(_ domain: BotDomain) -> [LayerSceneFeatureReviewOverlay.Line] {
        (blockadeVisits[domain]?.map.marks ?? []).compactMap { mark in
            guard !mark.crossed else { return nil }
            let center: CGPoint, width: Double
            if domain == .hacker {
                guard let camera = hackerCameraTransform, mark.space == "hacker/" + camera.sceneName,
                      !hackerLiveBounds.isEmpty else { return nil }
                center = camera.worldToAtlas(CGPoint(x: mark.x, y: mark.y), liveBounds: hackerLiveBounds)
                width = mark.viewWidth * camera.pixelsPerWorldUnitX
            } else {
                guard let origin = publishedIslandProjection?.blockadeOrigin(mark.space), groundOverlayScale > 0 else { return nil }
                center = CGPoint(x: (origin.x + mark.x - groundOverlayAnchor.x) * groundOverlayScale,
                                 y: (origin.y + mark.y - groundOverlayAnchor.y) * groundOverlayScale)
                width = mark.viewWidth * groundOverlayScale
            }
            let dy = 0.035 * width
            return .init(start: CGPoint(x: center.x, y: center.y - dy), end: CGPoint(x: center.x, y: center.y + dy),
                         kind: .blockedPassage)
        }
    }
    @Published private(set) var botTerrainSource = "detected"
    @Published private(set) var botHumanTerrainCount = 0
    // Protected by objectInferenceStateLock; delayed solves cannot use future boxes.
    private var botDetectionHistory = [LiveObjectDetectionBatch]()
    private var lastBotObservationTimestamp = -Double.infinity // visionQueue
    private var lastBotAuditTimestamp = -Double.infinity // visionQueue
    private var botObservationRoomID: Int?
    private var botObservationRoomStartedAt = -Double.infinity
    private var stoppingUnifiedRecording = false
    private let inputPathStore = InputPathStore()
    private let playbackDivergenceMonitor = PlaybackDivergenceMonitor()
    private var pathPlaybackAbortIssue: String?
    private let inputPathLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "input-path"
    )
    private lazy var gameControlForwarder = GameControlForwarder(
        activityHandler: {},
        connectionStateHandler: { [weak self] status in
            DispatchQueue.main.async { self?.inputControlStatus = status }
        },
        capabilitiesHandler: { [weak self] capabilities in
            guard let self else { return }
            if self.receiverCapabilities.contains(ReceiverControlProtocol.groundTruthCapability),
               !capabilities.contains(ReceiverControlProtocol.groundTruthCapability) {
                self.updateAtlasPathEvidence { $0 = LiveAtlasPathFrameEvidence() }
                self.groundTruthCameraStateLock.withLock { self.latestGroundTruthCameraSample = nil }
            }
            self.receiverCapabilities = capabilities
        },
        pauseStateHandler: { [weak self] state in
            DispatchQueue.main.async { self?.applyLabelPauseState(state) }
        },
        physicalInputHandler: { [weak self] button, isPressed, timestamp in
            self?.inputPathRecorder.recordInput(
                button: button,
                isPressed: isPressed,
                at: timestamp
            )
            DispatchQueue.main.async { [weak self] in
                self?.handleQuitGameButton(button, isPressed: isPressed)
                if isPressed { self?.stopBotDemo(reason: "Manual control") }
            }
        },
        demonstrationInputHandler: { [weak self] button, pressed, timestamp in
            self?.botDemonstrations.recordInput(button: button, isPressed: pressed, at: timestamp)
        },
        botInterruptionHandler: { [weak self] sessionID, reason in
            self?.botPlayer.interrupted(sessionID: sessionID, reason: reason)
            self?.botDemo.interrupted(sessionID: sessionID, reason: reason)
        },
        groundTruthHandler: { [weak self] sample, timestamp in
            self?.inputPathRecorder.recordGroundTruth(sample, observedAt: timestamp)
            self?.recordGroundTruthCameraSample(sample, observedAt: timestamp)
        }
    )

    private var stream: SCStream?
    /// Capture history is read by Label/Recent Frames but does not drive
    /// SwiftUI. Keep it off the main queue so each captured frame does not add
    /// a UI task before the actual presented frame.
    private let capturedFrameStateLock = NSLock()
    private let groundTruthCameraStateLock = NSLock()
    private var latestGroundTruthCameraSample: (ReceiverGroundTruthSample, TimeInterval)?
    private let hackerStateLock = NSLock()
    private let hackerFrameSynchronizer = HackerFrameSynchronizer()
    private let hackerFramePreparer = HackerFramePreparer()
    private let hackerAtlasPipeline = HackerAtlasPipeline()
    private var hackerWorkspaceActive = false
    private var hackerGeneration: UInt64 = 0
    private var hackerPresentedFrameCount: UInt64 = 0
    private var hackerAtlasFrameCount: UInt64 = 0
    private var hackerPresentationWindowStartedAt: TimeInterval?
    private var hackerPresentationWindowCount: UInt64 = 0
    private let hackerAtlasLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "hacker-atlas"
    )
    private var captureProcessingPaused = false
    /// Prevents frames from the prior route endpoint from seeding a freshly
    /// purged atlas before the single checkpoint restore completes.
    private var playbackPreparationCapturePaused = false
    private var latestCleanGameFrame: CGImage?
    private var latestGroundLabelCapture: GroundLabelCapture?
    private var registrationContinuity = LiveRegistrationContinuity()
    private var freshAtlasBootstrapGate = FreshAtlasBootstrapGate()
    private var accumulator = CameraAccumulator()
    private var activeCaptureStreamID: ObjectIdentifier?
    private var acceptedSampleStreamID: ObjectIdentifier?
    private var renderGeneration: UInt64 = 0
    private var sampleGeneration: UInt64 = 0
    private var operationInFlight = false
    private var wantsCapture = false
    private var captureCompleteFrames: UInt64 = 0
    private var captureIncompleteFrames: UInt64 = 0
    private var directPreviewFrames = 0
    private var directPreviewRateWindowStart: Double?
    /// Owned by sampleQueue. Ensures each capture generation can bootstrap
    /// the UI once while the atlas presentation worker catches up.
    private var directPreviewPublishedGeneration: UInt64?
    private var sampleFrameCount = 0
    private var transitionFrameGate = TransitionFrameGate()
    private var lastPublishedRoomStatus = "Room 1"
    private var lastPublishedTransitionPortals = [VisualRoomPortal]()
    private var handledRoomRevision: UInt64 = 0
    private var nextObjectSourceFrameIdentifier: UInt64 = 0
    private var latestPresentationScale: CGFloat = 1
    private var lastAtlasAdmissionDiagnosticAt: Double?
    private var lastStartupDiagnosticAt: Double?
    private var objectInferenceEpoch: UInt64 = 0
    private var objectModelConfigurationID: UInt64 = 0
    private var objectModelsLoaded = false
    private var objectInferenceSuspended = false
    private struct SignObservation {
        let image: CGImage
        let detections: [LiveObjectDetection]
        let room: String
        let capturedAt: Double
        let generation, sourceID: UInt64
    }
    private var latestSignObservation: SignObservation?
    private var latestObjectDetectionBatch: LiveObjectDetectionBatch?
    private var latestRawObjectDetectionBatch: LiveObjectDetectionBatch?
    private var latestMaskDetectionBatch: LiveObjectDetectionBatch?
    private var previousMaskDetectionBatch: LiveObjectDetectionBatch?
    private var objectDetectionTracker = LiveObjectDetectionTracker()
    private var objectDetectionIcons = [String: CGImage]()
    private var gameStatusTracker = LiveGameStatusTracker()
    private var profileLaunchDetector = ProfileLaunchDetector() // sampleQueue
    private var profileLaunchPending = false // sampleQueue
    private var profileLaunchFailed = false // sampleQueue
    private let profileAtlasGate = OSAllocatedUnfairLock(initialState: false)
    private var switchingProfileAtlas = false // main queue
    // Owned by sampleQueue with menu stencil admission.
    private var gameplayMenuEvidenceGate = GameplayMenuEvidenceGate()
    private var quitGameExitWatchGeneration: UInt64 = 0
    private var quitGameExitApplication: NSRunningApplication?
    private var quitGameExitWatchActive = false
    private var gameProcessLifetime = GameProcessLifetime()
    private var capturedGameProcessID: pid_t?
    private var expectedGameApplicationURL: URL?
    private var quitGameExitConfirmationObserved = false
    private var quitGameExitDeadline: TimeInterval?
    private var quitGameExitObserver: NSObjectProtocol?
    private let quitGameExitLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "quit-game"
    )
    private var currentStatusGeneration: UInt64 = 0
    private var recentCapturedFrameBuffer = RecentCapturedFrameBuffer()
    private var trackingState = TrackingState(
        cameraPosition: .zero,
        regions: nil,
        poseTimestamp: nil,
        basisTicket: nil,
        groundAnchored: nil,
        gameplayEnabled: false,
        generation: 0
    )

    private var knightFootLocator = KnightFootLocator()
    private var knightContactTracker = KnightTerrainContactTracker()
    private var walkedGroundMemory = WalkedGroundMemory() // visionQueue
    private struct TerrainReviewFrame {
        let detection: TerrainWallDetection
        let time: Double
        let generation: UInt64
        let roomID: Int
        let camera: CGPoint
        let islandID: Int?
        let walked: [CleanFloorLine]
    }
    @Published private var terrainReviewFrame: TerrainReviewFrame?

    var visibleWalkedGround: [GroundHypothesisAtlasLine] {
        guard let frame = terrainReviewFrame, let presentation = presentedIslandFrame,
              frame.generation == currentStatusGeneration, let island = presentation.island,
              island.id == frame.islandID, island.roomID == frame.roomID else { return [] }
        let x = liveBounds.minX + (frame.camera.x - presentation.rawPose.x) * presentation.scale
        let top = liveBounds.maxY + (frame.camera.y - presentation.rawPose.y) * presentation.scale
        return frame.walked.map { floor in
            let y = top - CGFloat(floor.row)
            return .init(segmentID: LiveGroundLineEvidence.walkedSegmentID,
                atlasStart: .init(x: x + CGFloat(floor.xRange.lowerBound), y: y),
                atlasEnd: .init(x: x + CGFloat(floor.xRange.upperBound + 1), y: y))
        }
    }

    func visualTerrainOverlay() -> LayerSceneFeatureReviewOverlay {
        let empty = LayerSceneFeatureReviewOverlay(markers: [], lines: [])
        guard groundHypothesesEnabled, let frame = terrainReviewFrame,
              frame.generation == currentStatusGeneration, let image = liveImage,
              let time = LivePresentationTiming.shared.timestamp(for: image), abs(time - frame.time) <= 0.25 else { return empty }
        guard let presentation = presentedIslandFrame, presentation.island?.id == frame.islandID,
              presentation.island?.roomID == frame.roomID || time == frame.time else { return empty }
        let dx = (frame.camera.x - presentation.rawPose.x) * presentation.scale
        let dy = (frame.camera.y - presentation.rawPose.y) * presentation.scale
        return frame.detection.overlay { CGPoint(x: self.liveBounds.minX + dx + $0.x, y: self.liveBounds.maxY + dy - $0.y) }
    }

    private struct VisionFrame {
        let presentationFrame: CGImage
        let solveWidth: CGFloat
        let hasGameplaySignal: Bool
        let resumedAfterTransition: Bool
        let signalProfile: FrameSignalProfile
        let room: VisualRoomSnapshot
        let timestamp: Double
        let worldTimestamp: Double
        let capturedAt: TimeInterval
        let generation: UInt64
        var hudStencil: HUDStencilResult? = nil
        var menuStencil: MenuStencilResult? = nil
        var horizontalInput: VisualRoomDirection? = nil
        var jumpHeld = false
        var enqueuedAt: Double = ProcessInfo.processInfo.systemUptime
    }

    private struct TrackingState {
        let cameraPosition: CGPoint
        let regions: SceneRegions?
        let poseTimestamp: Double?
        let basisTicket: WorldBasisController.ObservationTicket?
        /// Nil until the first sampled gameplay frame has made a ground
        /// decision. True means pixels measured the pose; a trusted zero-support
        /// seed remains false so it cannot interrupt capture-rate odometry.
        let groundAnchored: Bool?
        let gameplayEnabled: Bool
        let generation: UInt64
        var cameraVelocity: CGVector = .zero
        /// Last velocity derived from two consecutive, verified ground poses.
        /// It survives a short floorless handoff even though the ordinary
        /// presentation velocity is cleared as soon as ground is lost.
        var reliableGroundCameraVelocity: CGVector? = nil
        var reliableGroundVelocityTimestamp: TimeInterval? = nil
        var islandCapture: LiveIslandCapture? = nil
    }

    private struct RenderFrame {
        let frame: CGImage
        let solveWidth: CGFloat
        let regions: SceneRegions
        let timestamp: Double
        let capturedAt: TimeInterval
        let cameraPosition: CGPoint
        let observationPoseTimestamp: Double?
        let hasGameplaySignal: Bool
        let allowAtlasWrite: Bool
        let objectDetections: [LiveObjectDetection]
        let objectIcons: [String: CGImage]
        let enqueuedAt: TimeInterval
        let generation: UInt64
        var hudStencil: HUDStencilResult? = nil
        var showsHUDStencil: Bool = false
        var menuStencil: MenuStencilResult? = nil
        var showsMenuStencil: Bool = false
        var islandCapture: LiveIslandCapture? = nil
        var islandWorldRevision: Int? = nil
    }

    private struct ObjectInferenceFrame {
        let room: String
        let capturedAt: Double
        /// Untouched presentation capture. Ground-only intensity correction
        /// is created later on the vision queue and must never enter models.
        let rawImage: CGImage
        let sourceFrameIdentifier: UInt64
        let timestamp: Double
        let captureGeneration: UInt64
        let createsPixelDiagnostic: Bool
    }

    init(
        autoNavigateToGameplay: Bool = false,
        gameMode: HollowKnightRuntimeMode = .normal,
        visualReplayMode: Bool = false,
        gameplayGym: Bool = false,
        worldRootOverride: URL? = nil
    ) {
        precondition(!gameplayGym || visualReplayMode)
        self.gameplayGym = gameplayGym
        self.botPlayer = BotPlaybackController(loadModel: !visualReplayMode)
        self.visualReplayMode = visualReplayMode
        groundHypothesisTracker = GroundHypothesisTracker(
            minimumObservationSeconds: 0.2, backgroundGlobalSearch: !visualReplayMode,
            confirmedLossGraceSeconds: 3, productionCameraSolver: true)
        self.autoNavigateToGameplay = autoNavigateToGameplay
        self.gameMode = gameMode
        if UserDefaults.standard.object(
            forKey: Self.randomizePlayerStateAfterLabelDefaultsKey
        ) != nil {
            randomizePlayerStateAfterLabel = UserDefaults.standard.bool(
                forKey: Self.randomizePlayerStateAfterLabelDefaultsKey
            )
        }
        let worldRootURL = worldRootOverride ?? LiveWorldSessionStore.defaultRootURL()
        let storageRoot = worldRootURL.deletingLastPathComponent()
        atlasStateStore = AtlasStateStore(rootURL: storageRoot.appendingPathComponent("atlas-states-v1", isDirectory: true))
        let hackerRoot = HackerAtlasArchiveStore.rootURL(beside: worldRootURL)
        hackerAtlasStateStore = AtlasStateStore(rootURL: hackerRoot.appendingPathComponent("states", isDirectory: true))
        hackerAtlasActiveURL = hackerRoot.appendingPathComponent("active", isDirectory: true)
        roomTopologyURL = worldRootURL.appendingPathComponent("visual-rooms.json")
        atlasPipeline = LiveAtlasPipeline(
            context: GroundMetalResources.shared.makeImageContext(),
            runsIntegrationSynchronously: visualReplayMode,
            worldRootURL: worldRootURL,
            restoresPersistedWorldOnInitialization: false
        )
        super.init()
        atlasAutoSave = atlasPipeline.activeAutoSaveSummary()
        // Construct before the capture and receiver workers can both use it.
        _ = botHackerObservations
        botDemonstrations.onStop = { [weak self] _ in
            guard let self, self.isPathRecording, !self.isPathPlaybackActive else { return }
            self.stopPathRecording()
        }
        botPlayer.skillSight = { [weak self] in self?.currentSkillSight() }
        botPlayer.configure(
            canContinue: { [weak self] in self?.canStartBotPlayback ?? false },
            begin: { [weak self] in self?.gameControlForwarder.beginBotControl() },
            apply: { [weak self] action, deadline, sessionID in
                self?.gameControlForwarder.applyBotAction(action, validUntil: deadline, sessionID: sessionID) ?? false
            },
            neutralize: { [weak self] sessionID in
                self?.gameControlForwarder.neutralizeBotControl(sessionID: sessionID)
            },
            end: { [weak self] sessionID in self?.gameControlForwarder.endBotControl(sessionID: sessionID) }
        )
        botPlayer.onStarted = { [weak self] domain, policy in
            guard let self, self.botEvaluation.start(domain: domain, model: policy, reference: self.botHumanGround.snapshot) else { return false }
            self.gameControlForwarder.setRenderFrameMarkerEnabled(true)
            return true
        }
        botPlayer.onSkillEvent = { [weak self] plan, choice, context, event, time in
            self?.botEvaluation.recordSkill(plan, choice: choice, context: context, event: event, at: time)
            if let self, plan.decisionID != nil, (["Policy window ended", "Bound target expired", "Policy context changed",
                "Target reached", "Explore destination reached", "Waypoint reached", "Focus restored a mask",
                "Focus opening closed"].contains(event) || event.hasPrefix("Remembered ·") || event.hasPrefix("Atlas updated ·") || event.hasSuffix(" · maneuver finished") || event.hasSuffix(" verified")) {
                self.thinkingOutcome = (self.skillGeneration, (self.thinkingOutcome?.sequence ?? 0) + 1, event)
            }
        }
        botPlayer.onActionApplied = { [weak self] action, time in self?.botEvaluation.action(action, at: time) }
        botPlayer.onStopped = { [weak self] in
            guard let self else { return }
            self.botEvaluation.stop(atlasScore: self.botPlayer.atlasScore)
            self.restoreRenderFrameMarkerPolicy()
        }
        botDemo.configure(
            canContinue: { [weak self] in self?.canStartBotDemo ?? false },
            begin: { [weak self] in self?.gameControlForwarder.beginBotControl() },
            press: { [weak self] button, session in
                self?.gameControlForwarder.pulseBotDemoButton(button, sessionID: session) ?? false
            },
            end: { [weak self] session in self?.gameControlForwarder.endBotControl(sessionID: session) },
            botIsPlaying: { [weak self] in self?.botPlayer.isActive ?? false }
        )
        // Compile the patch-search pipeline before capture begins. The first
        // gameplay frame should not pay runtime shader setup cost.
        _ = GroundPatchMatcher.shared.metalAvailable
        quitGameExitObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            self?.handleTerminatedApplication(notification)
        }
        let initialWorld = atlasPipeline.currentWorldSnapshot()
        liveWorldSnapshot = initialWorld
        publishedIslandProjection = LiveIslandWorldProjection(snapshot: initialWorld)
        trackingIslandProjection = publishedIslandProjection
        ownedGroundLines = initialWorld.ownedGroundLines
        refreshOwnedWorldOverlays()
        let restoredRoom = restoreRoomTopology(worldSnapshot: initialWorld)
        atlasPipeline.setRoomCompositionBoundsProvider { [weak visualRoomTracker] roomID in
            visualRoomTracker?.compositionBounds(for: roomID) ?? .unbounded
        }
        atlasPipeline.setIslandClosureSearchAllowed { [weak visualRoomTracker] in
            visualRoomTracker?.snapshot.portals.isEmpty == true
        }
        activeRoomStatus = restoredRoom.statusText
        transitionPortals = restoredRoom.portals
        lastPublishedRoomStatus = restoredRoom.statusText
        lastPublishedTransitionPortals = restoredRoom.portals
        let launchTuning = GroundTheoryTuning.launchTuning(
            arguments: ProcessInfo.processInfo.arguments
        )
        groundTuning = launchTuning
        semanticGroundTuning = GroundTheoryTuning.launchTuning(
            arguments: ProcessInfo.processInfo.arguments, base: .semanticDefault
        )
        atlasPipeline.setWorldPoseResolver { [weak self] ticket in
            self?.worldBasis.rebasedObservation(for: ticket)
        }
        worldStorageIssue = atlasPipeline.worldPersistenceIssue
        atlasPipeline.setWorldUpdateHandler { [weak self] update in
            guard let self else { return }
            self.visionQueue.async { [weak self] in
                guard let self else { return }
                self.visionStateLock.lock()
                defer { self.visionStateLock.unlock() }
                guard update.captureGeneration == self.worldBasis.snapshot.captureGeneration else { return }
                self.applyWorldUpdate(update)
                DispatchQueue.main.async { [weak self] in
                    guard let self,
                          update.captureGeneration == self.worldBasis.snapshot.captureGeneration else { return }
                    guard update.worldRevision >= (self.liveWorldSnapshot?.mapRevision ?? -1) else { return }
                    self.liveWorldReview = update.review
                    self.liveWorldSnapshot = update.snapshot
                    self.publishedIslandProjection = LiveIslandWorldProjection(snapshot: update.snapshot)
                    self.ownedGroundLines = update.snapshot.ownedGroundLines
                    self.publishAtlasTiles(update.atlasTiles)
                    self.reprojectPresentedIslandFrame()
                }
                if let closure = update.acceptedClosure {
                    self.inputPathRecorder.recordLoopClosure(
                        update: update,
                        closure: closure,
                        observedAt: self.visualReplayMode ? update.captureTimestamp : ProcessInfo.processInfo.systemUptime
                    )
                    self.performanceLog.info(
                        "globalLoopCorrection dx=\(update.correction.dx, privacy: .public) dy=\(update.correction.dy, privacy: .public) revision=\(update.worldRevision, privacy: .public) support=\(closure.support, privacy: .public)"
                    )
                }
            }
        }
        accumulator.smoothing = 1
        // A mature local stencil corpus can take minutes to rebuild. Loading
        // it synchronously here prevents AppKit from creating the first window
        // and makes a healthy running game look undiscovered. The existing
        // reload queue publishes the finished catalog onto the sample queue.
        if !visualReplayMode {
            refreshMenuStencilCatalog()
            reloadActiveObjectModels()
            refreshAtlasSavedStates()
        }
        if !visualReplayMode && gameMode == .normal {
            inputControlStatus = visionText("nativeInputReady")
            applyLabelPauseState(.ready)
        }
    }

    deinit {
        if let quitGameExitObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(quitGameExitObserver)
        }
    }

    func reloadActiveObjectModels() {
        botDemonstrations.stop(reason: "detector-changed")
        botPlayer.stop(reason: "Detector changed")
        botDemo.stop(reason: "Detector changed")
        objectInferenceStateLock.lock()
        objectModelConfigurationID &+= 1
        let configurationID = objectModelConfigurationID
        objectInferenceEpoch &+= 1
        let epoch = objectInferenceEpoch
        objectModelsLoaded = false
        latestSignObservation = nil
        latestObjectDetectionBatch = nil
        latestRawObjectDetectionBatch = nil
        latestMaskDetectionBatch = nil
        previousMaskDetectionBatch = nil
        botDetectionHistory = []
        objectDetectionTracker.reset()
        objectInferenceStateLock.unlock()
        objectInferencePump.invalidate(epoch: epoch)

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.liveObjectDetectionCount = 0
            self.liveObjectDetectionClassIdentifiers = []
            self.objectInferenceMilliseconds = nil
            self.objectInferenceIssue = nil
            self.gameStatusTracker.reset(preservingGameplay: true)
            self.liveGameStatus = self.gameStatusTracker.current?.displayText
            self.liveGameContextIdentifier = self.gameStatusTracker.current?
                .context.labelingContext?.storageIdentifier
            self.liveSelectedMenuOption = self.gameStatusTracker.current?.selectedMenuOption
            self.hasGameplayStarted = self.gameStatusTracker.current?.context.isGameplay == true
        }
        objectInferenceQueue.async { [weak self] in
            guard let self else { return }
            do {
                let artifacts = try self.modelVersionStore.activeModelArtifacts()
                try self.objectDetector.load(artifacts)
                self.objectInferenceStateLock.lock()
                let isCurrent = configurationID == self.objectModelConfigurationID
                if isCurrent { self.objectModelsLoaded = !artifacts.isEmpty }
                self.objectInferenceStateLock.unlock()
                guard isCurrent else { return }
                let summary = artifacts.isEmpty ? nil : artifacts.map {
                    "\(self.objectDisplayName($0.classIdentifier)) \($0.version.displayName)"
                }.joined(separator: " · ")
                DispatchQueue.main.async { [weak self] in
                    self?.activeObjectModelSummary = summary
                    self?.activeObjectModelVersion = artifacts.first?.version.displayName
                    self?.objectInferenceIssue = nil
                }
            } catch {
                self.objectInferenceStateLock.lock()
                let isCurrent = configurationID == self.objectModelConfigurationID
                if isCurrent { self.objectModelsLoaded = false }
                self.objectInferenceStateLock.unlock()
                guard isCurrent else { return }
                DispatchQueue.main.async { [weak self] in
                    self?.activeObjectModelSummary = nil
                    self?.activeObjectModelVersion = nil
                    self?.objectInferenceIssue = "Model load failed: \(error.localizedDescription)"
                }
            }
        }
    }

    func toggleCapture() {
        isCapturing ? stopCapture() : startCapture()
    }

    func startCapture() {
        wantsCapture = true
        guard !operationInFlight, !isCapturing else { return }
        operationInFlight = true
        notebookProfile = nil
        pendingNotebookProfile = nil
        status = "Finding the Hollow Knight window…"
        Task { [weak self] in
            guard let self else { return }
            var startingStreamID: ObjectIdentifier?
            var startingGameProcessID: pid_t?
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(
                    false,
                    // Vision activates its own full-screen-sized window before
                    // capture discovery. Hollow Knight can therefore be fully
                    // occluded even though its window is running and directly
                    // capturable. Include those windows so focus is irrelevant.
                    onScreenWindowsOnly: false
                )
                guard let window = self.bestHollowKnightWindow(in: content.windows) else {
                    if let issue = self.runningGameCompatibilityIssue(in: content.windows) {
                        throw CaptureFailure.incompatibleGame(issue)
                    }
                    throw CaptureFailure.windowNotFound
                }
                startingGameProcessID = window.owningApplication?.processID
                let configuration = HollowKnightCaptureConfiguration.make(
                    windowSize: window.frame.size
                )
                let filter = SCContentFilter(desktopIndependentWindow: window)
                let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
                let streamID = ObjectIdentifier(stream)
                startingStreamID = streamID
                await prepareCaptureAttempt(streamID)
                await installAcceptedSampleStream(streamID)
                await MainActor.run {
                    self.stream = stream
                    self.capturedGameProcessID = window.owningApplication?.processID
                }
                try await stream.startCapture()
                await MainActor.run {
                    guard self.stream === stream else { return }
                    self.isCapturing = true
                    self.operationInFlight = false
                    self.status = "Live atlas · waiting for camera lock…"
                    // Position the game once capture is ready. The normal-mode
                    // panel shares the usable display and forwards input into
                    // this window without transferring app focus.
                    self.positionWindows()
                }
            } catch {
                let attemptedStreamID = startingStreamID
                let attemptedGameProcessID = startingGameProcessID
                let ownsAttempt = if let attemptedStreamID {
                    await invalidateCaptureAttempt(attemptedStreamID)
                } else {
                    true
                }
                await MainActor.run {
                    guard ownsAttempt else { return }
                    if let attemptedStreamID,
                       let currentStream = self.stream,
                       ObjectIdentifier(currentStream) != attemptedStreamID { return }
                    self.stream = nil
                    self.capturedGameProcessID = nil
                    self.operationInFlight = false
                    self.isCapturing = false
                    self.status = self.message(for: error)
                    if case CaptureFailure.windowNotFound = error {
                        self.scheduleCaptureRestart(after: 1)
                    } else if case CaptureFailure.incompatibleGame = error,
                              self.gameProcessLifetime.observation == .waitingForGame {
                        self.scheduleCaptureRestart(after: 1)
                    } else if self.gameProcessLifetime.observation == .waitingForRelaunch {
                        self.scheduleCaptureRestart(after: 1)
                    } else if let attemptedGameProcessID,
                              NSRunningApplication(processIdentifier: attemptedGameProcessID)?.isTerminated != false {
                        // The process can exit before the watcher or stream
                        // delegate observes it, including during startCapture.
                        self.scheduleCaptureRestart(after: 1)
                    }
                }
            }
        }
    }

    private func scheduleCaptureRestart(after delay: TimeInterval = 0.5) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self,
                  LiveCaptureRestartPolicy.shouldRestart(
                    wantsCapture: self.wantsCapture,
                    hasStream: self.stream != nil,
                    operationInFlight: self.operationInFlight,
                    isCapturing: self.isCapturing
                  ) else { return }
            self.startCapture()
        }
    }

    func stopCapture() {
        botDemonstrations.stop(reason: "capture-stopped")
        botPlayer.stop(reason: "Capture stopped")
        botDemo.stop(reason: "Capture stopped")
        wantsCapture = false
        guard !operationInFlight else { return }
        guard let activeStream = stream else {
            isCapturing = false
            return
        }
        operationInFlight = true
        status = "Stopping capture…"
        let activeStreamID = ObjectIdentifier(activeStream)
        Task { [weak self] in
            guard let self else { return }
            _ = await invalidateCaptureAttempt(activeStreamID)
            do { try await activeStream.stopCapture() } catch { }
            await MainActor.run {
                guard self.stream === activeStream else { return }
                self.stream = nil
                self.capturedGameProcessID = nil
                self.isCapturing = false
                self.operationInFlight = false
                self.status = "Paused."
            }
        }
    }

    func refreshAtlasSavedStates() {
        atlasManagementQueue.async { [weak self] in
            guard let self else { return }
            do {
                let states = try self.atlasStateStore.list()
                let autoSave = self.atlasPipeline.activeAutoSaveSummary()
                DispatchQueue.main.async {
                    self.atlasSavedStates = states
                    self.atlasAutoSave = autoSave
                }
                self.refreshAtlasAutoSaveSize(for: autoSave)
            } catch {
                DispatchQueue.main.async {
                    self.atlasManagementIssue = "Atlas states unavailable: \(error.localizedDescription)"
                }
            }
        }
    }

    func refreshHackerAtlasSavedStates() {
        atlasManagementQueue.async { [weak self] in
            guard let self else { return }
            do {
                let states = try self.hackerAtlasStateStore.list()
                var summary = self.hackerAtlasQueue.sync { self.hackerAtlasPipeline.archive().autoSaveSummary }
                if let bytes = try? AtlasDiskUsage.allocatedByteCount(at: self.hackerAtlasActiveURL) {
                    summary = summary.withTotalByteCount(bytes)
                }
                DispatchQueue.main.async {
                    self.hackerAtlasSavedStates = states
                    self.hackerAtlasAutoSave = summary
                }
            } catch {
                DispatchQueue.main.async { self.hackerAtlasManagementIssue = error.localizedDescription }
            }
        }
    }

    func saveHackerAtlasState(name: String) {
        performHackerAtlasManagement { [self] archive in
            _ = try hackerAtlasStateStore.save(name: name) { try HackerAtlasArchiveStore.write(archive, to: $0) }
            return nil
        }
    }

    func loadHackerAtlasState(_ state: AtlasSavedState) {
        performHackerAtlasManagement(replacingActive: true) { [self] archive in
            let loaded = try HackerAtlasArchiveStore.read(from: hackerAtlasStateStore.worldURL(for: state))
            try savePreviousHackerAtlas(archive, action: "Load")
            return loaded
        }
    }

    func newHackerAtlas() {
        performHackerAtlasManagement(replacingActive: true) { [self] archive in
            try savePreviousHackerAtlas(archive, action: "New")
            return archive.emptyForCurrentProfile
        }
    }

    func deleteHackerAtlasState(_ state: AtlasSavedState) {
        performHackerAtlasManagement { [self] _ in
            try hackerAtlasStateStore.delete(state)
            return nil
        }
    }

    func deleteHackerAutoSave() {
        performHackerAtlasManagement(replacingActive: true) { $0.emptyForCurrentProfile }
    }

    private func savePreviousHackerAtlas(_ archive: HackerAtlasArchive, action: String) throws {
        guard archive.profile != nil || !archive.rooms.isEmpty else { return }
        _ = try hackerAtlasStateStore.save(name: archive.profile?.name ??
            "Atlas before \(action) · \(Date().formatted(date: .abbreviated, time: .shortened))", profile: archive.profile) {
            try HackerAtlasArchiveStore.write(archive, to: $0)
        }
    }

    private func performHackerAtlasManagement(
        replacingActive: Bool = false, activateAfter: Bool? = nil, blocksGameplay: Bool = true,
        completion: ((Error?) -> Void)? = nil,
        _ operation: @escaping (HackerAtlasArchive) throws -> HackerAtlasArchive?
    ) {
        guard !hackerAtlasManagementBusy else {
            completion?(BotRuntimeError.incompatible("Hacker Atlas is busy."))
            return
        }
        hackerAtlasBlocksGameplay = blocksGameplay
        hackerAtlasManagementBusy = true
        hackerAtlasManagementIssue = nil
        let reactivate = activateAfter ?? isHackerWorkspaceActive()
        if replacingActive {
            setHackerWorkspaceActive(false)
            hackerWorkspaceWanted = reactivate
        }
        let positionOverrides = hackerRoomPositionOverrides
        atlasManagementQueue.async { [weak self] in
            guard let self else { return }
            do {
                var previous = self.hackerAtlasQueue.sync { self.hackerAtlasPipeline.archive() }
                let ids = Set(previous.rooms.map(\.id))
                previous.roomPositionOverrides = positionOverrides.filter { ids.contains($0.key) }
                let loaded = try operation(previous)
                if let loaded {
                    try self.hackerAtlasQueue.sync { try self.hackerAtlasPipeline.restore(loaded) }
                    do { try HackerAtlasArchiveStore.write(loaded, to: self.hackerAtlasActiveURL) }
                    catch {
                        try self.hackerAtlasQueue.sync { try self.hackerAtlasPipeline.restore(previous) }
                        throw error
                    }
                    self.hackerPresentationQueue.sync { self.hackerFramePreparer.restoreAnchors(loaded.anchors) }
                }
                let output = loaded.map { _ in self.hackerAtlasQueue.sync {
                    self.hackerAtlasPipeline.output(sceneName: "", unityFrame: 0)
                } }
                let states = try self.hackerAtlasStateStore.list()
                var summary = (loaded ?? previous).autoSaveSummary
                if let bytes = try? AtlasDiskUsage.allocatedByteCount(at: self.hackerAtlasActiveURL) {
                    summary = summary.withTotalByteCount(bytes)
                }
                DispatchQueue.main.async {
                    if let output {
                        self.hackerPaths.reset()
                        self.hackerAtlasTiles = output.atlasTiles
                        self.hackerAtlasRooms = output.rooms
                        self.refreshHackerPaths()
                        self.hackerRoomConnections = output.connections
                        self.hackerRoomLayout = HackerRoomLayout.make(rooms: output.rooms, connections: output.connections,
                            floorAlignedConnectionIDs: output.floorAlignedConnectionIDs)
                        self.hackerRoomPositionOverrides = loaded?.roomPositionOverrides ?? [:]
                        self.hackerLiveImage = nil
                        self.hackerLiveBounds = .zero
                        self.hackerCameraTransform = nil
                        self.hackerMapFocus = .zero
                        self.updateHackerAtlasBounds()
                        self.hackerAutoSaveAllowed = true
                    }
                    self.hackerAtlasSavedStates = states
                    self.hackerAtlasAutoSave = summary
                    self.hackerAtlasBlocksGameplay = false
                    self.hackerAtlasManagementBusy = false
                    if replacingActive && reactivate && self.hackerWorkspaceWanted { self.setHackerWorkspaceActive(true) }
                    completion?(nil)
                }
            } catch {
                DispatchQueue.main.async {
                    self.hackerAtlasManagementIssue = error.localizedDescription
                    self.hackerAtlasBlocksGameplay = false
                    self.hackerAtlasManagementBusy = false
                    // Preserve an unreadable Auto Save until the user explicitly
                    // replaces it with New or a successfully loaded restore point.
                    if activateAfter == true { self.hackerAutoSaveAllowed = false }
                    if replacingActive && reactivate && self.hackerWorkspaceWanted { self.setHackerWorkspaceActive(true) }
                    completion?(error)
                }
            }
        }
    }

    private func scheduleHackerAutoSave() {
        guard !hackerAutoSaveScheduled, hackerAutoSaveAllowed else { return }
        hackerAutoSaveScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self else { return }
            self.hackerAutoSaveScheduled = false
            guard !self.hackerAtlasManagementBusy, self.hackerAutoSaveAllowed else { return }
            // This copies a snapshot without replacing the live atlas. Its
            // five-second cadence must not interrupt bot play or recording.
            self.performHackerAtlasManagement(blocksGameplay: false) { archive in
                try HackerAtlasArchiveStore.write(archive, to: self.hackerAtlasActiveURL)
                return nil
            }
        }
    }

    func flushHackerAutoSave() {
        guard hackerAtlasRestored, hackerAutoSaveAllowed else { return }
        setHackerWorkspaceActive(false)
        let positionOverrides = hackerRoomPositionOverrides
        atlasManagementQueue.sync {
            var archive = hackerAtlasQueue.sync { hackerAtlasPipeline.archive() }
            let ids = Set(archive.rooms.map(\.id))
            archive.roomPositionOverrides = positionOverrides.filter { ids.contains($0.key) }
            do { try HackerAtlasArchiveStore.write(archive, to: self.hackerAtlasActiveURL) }
            catch { hackerAtlasLog.error("Auto Save failed: \(error.localizedDescription, privacy: .public)") }
        }
    }

    private func refreshAtlasAutoSaveSize(for summary: AtlasAutoSaveSummary) {
        guard let rootURL = atlasPipeline.activeAutoSaveRootURL else { return }
        atlasMetadataQueue.async { [weak self] in
            guard let self,
                  let bytes = try? AtlasDiskUsage.allocatedByteCount(at: rootURL)
            else { return }
            DispatchQueue.main.async {
                guard self.atlasAutoSave.registrationCount == summary.registrationCount,
                      self.atlasAutoSave.createdAt == summary.createdAt, self.atlasAutoSave.profile == summary.profile
                else { return }
                self.atlasAutoSave = summary.withTotalByteCount(bytes)
            }
        }
    }

    func saveAtlasState(name: String) {
        performAtlasManagement { [self] in
            _ = try atlasStateStore.save(name: name) { destination in
                try atlasPipeline.copyActiveWorld(to: destination)
            }
            return nil
        }
    }

    func loadAtlasState(_ state: AtlasSavedState) {
        performAtlasManagement(replacingActiveWorld: true) { [self] in
            let worldURL = try atlasStateStore.worldURL(for: state)
            let loaded = try atlasPipeline.loadSavedWorld(from: worldURL)
            if let archiveURL = loaded.archivedPreviousWorldURL {
                importPreviousAtlasArchive(archiveURL, action: "Load")
            }
            return loaded
        }
    }

    func switchToProfileAtlas(_ launch: ProfileLaunch) {
        if !switchingProfileAtlas { resetLiveBlockades() }
        notebookProfile = nil
        switchingProfileAtlas = true
        guard !atlasManagementBusy, !hackerAtlasManagementBusy else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in self?.switchToProfileAtlas(launch) }
            return
        }
        guard let root = atlasPipeline.activeAutoSaveRootURL else {
            finishProfileAtlasSwitch(LiveAtlasStateError.activeWorldUnavailable)
            return
        }
        // Read durable ownership once per launch, including when Vision has just opened.
        do {
            let current = try SaveProfileAtlas.read(from: root)
            let saved = launch.isNew || current?.slot == launch.slot ? [] : try atlasStateStore.list()
            let target = SaveProfileAtlas.target(for: launch, active: current, saved: saved)
            pendingNotebookProfile = target
            let adoptingUnassigned = !launch.isNew && current == nil && !saved.contains { $0.profile == target }
            if current == target {
                switchHackerProfileAtlas(to: target, adoptingUnassigned: adoptingUnassigned)
                return
            }
            performAtlasManagement(replacingActiveWorld: !adoptingUnassigned, { [self] in
                let loaded = try atlasPipeline.activateProfile(target, in: atlasStateStore, adoptingUnassigned: adoptingUnassigned)
                if let archive = loaded?.archivedPreviousWorldURL { importPreviousAtlasArchive(archive, action: target.name) }
                return loaded
            }) { [weak self] error in
                guard let self else { return }
                if let error { finishProfileAtlasSwitch(error) }
                else {
                    switchHackerProfileAtlas(to: target, adoptingUnassigned: adoptingUnassigned)
                }
            }
        } catch { finishProfileAtlasSwitch(error) }
    }

    private func switchHackerProfileAtlas(to target: SaveProfileAtlas, adoptingUnassigned: Bool) {
        // The slot launch and the durable visual atlas now agree on this playthrough.
        if pendingNotebookProfile == target {
            notebookProfile = target
            pendingNotebookProfile = nil
        }
        guard !hackerAtlasManagementBusy else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in self?.switchHackerProfileAtlas(to: target, adoptingUnassigned: adoptingUnassigned) }
            return
        }
        let readFromDisk = !hackerAtlasRestored || !hackerAutoSaveAllowed
        if !readFromDisk, hackerAtlasAutoSave.profile == target {
            finishProfileAtlasSwitch(nil)
            return
        }
        performHackerAtlasManagement(replacingActive: true, completion: { [weak self] error in
            if error == nil { self?.hackerAtlasRestored = true }
            self?.finishProfileAtlasSwitch(error)
        }) { [self] memory in
            let disk = self.hackerAtlasActiveURL
            let previous = readFromDisk && FileManager.default.fileExists(atPath: disk.path)
                ? try HackerAtlasArchiveStore.read(from: disk) : memory
            return try previous.switching(to: target, library: hackerAtlasStateStore, adoptingUnassigned: adoptingUnassigned) ?? (readFromDisk ? previous : nil)
        }
    }

    private func finishProfileAtlasSwitch(_ error: Error?) {
        switchingProfileAtlas = false
        if let error {
            atlasManagementIssue = "Profile atlas: " + error.localizedDescription
            botDemo.stop(reason: "Profile atlas unavailable", failed: true)
        }
        sampleQueue.async { [weak self] in
            guard let self else { return }
            profileLaunchPending = false
            profileLaunchFailed = error != nil
            profileAtlasGate.withLock { $0 = error != nil }
        }
    }

    func newAtlas() {
        performAtlasManagement(replacingActiveWorld: true) { [self] in
            guard atlasPipeline.reset(anchorPosition: .zero) else {
                throw NSError(
                    domain: "HollowKnightVision.Atlas", code: 1,
                    userInfo: [NSLocalizedDescriptionKey:
                        atlasPipeline.worldPersistenceIssue ?? "Could not create a new atlas."]
                )
            }
            if let archiveURL = atlasPipeline.mostRecentResetArchiveURL {
                importPreviousAtlasArchive(archiveURL, action: "New")
            }
            return LiveAtlasLoadedState(snapshot: try LiveWorldSnapshot(), atlasTiles: [])
        }
    }

    func deleteAutoSave() {
        performAtlasManagement(replacingActiveWorld: true) { [self] in try purgeActiveAtlas() }
    }

    /// Replaces the active auto-save and permanently removes its archived
    /// contents. This is shared by Manage Atlas and deterministic path replay.
    private func purgeActiveAtlas() throws -> LiveAtlasLoadedState {
        guard atlasPipeline.reset(anchorPosition: .zero) else {
            throw NSError(
                domain: "HollowKnightVision.Atlas", code: 2,
                userInfo: [NSLocalizedDescriptionKey:
                    atlasPipeline.worldPersistenceIssue ?? "Could not replace Auto Save."]
            )
        }
        if let archiveURL = atlasPipeline.mostRecentResetArchiveURL {
            try atlasStateStore.deleteActiveArchive(at: archiveURL)
        }
        try atlasStateStore.purgeLegacyTrash()
        return LiveAtlasLoadedState(snapshot: try LiveWorldSnapshot(), atlasTiles: [])
    }

    private func importPreviousAtlasArchive(_ archiveURL: URL, action: String) {
        do {
            _ = try atlasStateStore.importArchive(
                at: archiveURL,
                name: "Atlas before \(action) · \(Date().formatted(date: .abbreviated, time: .shortened))"
            )
        } catch {
            DispatchQueue.main.async {
                self.atlasManagementIssue = "Active atlas changed; prior archive could not be listed: \(error.localizedDescription)"
            }
        }
    }

    func deleteAtlasState(_ state: AtlasSavedState) {
        performAtlasManagement { [self] in
            try atlasStateStore.delete(state)
            try atlasStateStore.purgeLegacyTrash()
            return nil
        }
    }

    /// A nil result means state-library-only change; a non-nil result replaces
    /// the active world and starts a fresh capture/pose epoch.
    private func performAtlasManagement(
        replacingActiveWorld: Bool = false,
        _ operation: @escaping () throws -> LiveAtlasLoadedState?,
        completion: ((Error?) -> Void)? = nil
    ) {
        guard !atlasManagementBusy else {
            completion?(NSError(
                domain: "HollowKnightVision.Atlas", code: 3,
                userInfo: [NSLocalizedDescriptionKey: "Atlas management is already in progress."]
            ))
            return
        }
        atlasManagementBusy = true
        botDemonstrations.stop(reason: "atlas-changed")
        botPlayer.stop(reason: "Atlas changed")
        if !switchingProfileAtlas { botDemo.stop(reason: "Atlas changed") }
        atlasManagementIssue = nil
        if replacingActiveWorld {
            // Close the capture epoch before touching persistent storage. A
            // Vision result may already be between its initial generation
            // check and atlas submission; invalidating its frame-pump ticket
            // here prevents that old pose becoming registration 1 in the new
            // atlas. Resetting after the disk operation is too late.
            advanceGeneration(resetCamera: true, preservingMenuNavigation: switchingProfileAtlas)
        }
        let priorProfile = atlasAutoSave.profile
        atlasManagementQueue.async { [weak self] in
            guard let self else { return }
            do {
                let loaded = try operation()
                if let loaded {
                    self.visionQueue.sync {
                        self.trackingIslandCoordinator.reset(nextID:
                            (loaded.snapshot.trackingIslands.map(\.id).max() ?? -1) + 1)
                        self.trackingIslandCoordinator.applyWorldSnapshot(loaded.snapshot)
                        self.walkedGroundMemory = WalkedGroundMemory(snapshot: loaded.snapshot)
                        self.knightContactTracker = .init()
                        self.trackingStateLock.lock()
                        self.trackingIslandProjection = LiveIslandWorldProjection(snapshot: loaded.snapshot)
                        self.trackingState.islandCapture = nil
                        self.trackingStateLock.unlock()
                    }
                    self.sampleQueue.sync {
                        _ = self.restoreRoomTopology(worldSnapshot: loaded.snapshot)
                    }
                    self.atlasPipeline.rebuildAtlasForRoomComposition()
                }
                let states = try self.atlasStateStore.list()
                let autoSave = self.atlasPipeline.activeAutoSaveSummary()
                DispatchQueue.main.async {
                    if let loaded {
                        self.atlasImage = nil
                        self.atlasBounds = nil
                        self.atlasRevision = nil
                        self.atlasTiles = []
                        self.visualPaths.reset()
                        self.visualBaseRooms.removeAll()
                        self.visualPathRoom = ""
                        self.visualPathOffset = .zero
                        self.liveImage = nil
                        self.liveBounds = .zero
                        self.mapFocus = .zero
                        self.presentationCorrectionOffset = .zero
                        self.liveWorldReview = nil
                        self.botAtlasCoverage = BotAtlasCoverage()
                        self.liveWorldSnapshot = loaded.snapshot
                        self.publishedIslandProjection = LiveIslandWorldProjection(snapshot: loaded.snapshot)
                        self.ownedGroundLines = loaded.snapshot.ownedGroundLines
                        self.presentedIslandFrame = nil
                        self.ownedGroundOverlayLines = []
                        self.groundOverlayRevision = -1
                        self.localFeatureTracking = .empty
                        self.groundReference = nil
                        self.groundEdgeImage = nil
                        self.groundDetectImage = nil
                        self.groundStageImage = nil
                        self.cleanedImage = nil
                        self.groundHypothesisImage = nil
                        self.groundHypothesisTracking = .empty
                        self.publishAtlasTiles(loaded.atlasTiles)
                        self.refreshOwnedWorldOverlays()
                    }
                    if loaded != nil || priorProfile != autoSave.profile {
                        self.resetLiveBlockades()
                    }
                    self.atlasSavedStates = states
                    self.atlasAutoSave = autoSave
                    self.worldStorageIssue = self.atlasPipeline.worldPersistenceIssue
                    self.atlasManagementBusy = false
                    completion?(nil)
                }
                self.refreshAtlasAutoSaveSize(for: autoSave)
            } catch {
                DispatchQueue.main.async {
                    self.atlasManagementIssue = error.localizedDescription
                    self.atlasManagementBusy = false
                    completion?(error)
                }
            }
        }
    }

    func configureGameMode(_ mode: HollowKnightRuntimeMode) {
        guard gameMode != mode else { return }
        botDemonstrations.stop(reason: "game-mode-changed")
        botPlayer.stop(reason: "Game mode changed")
        botDemo.stop(reason: "Game mode changed")
        gameControlForwarder.stop()
        gameMode = mode
        receiverCapabilities = []
        updateAtlasPathEvidence { $0 = LiveAtlasPathFrameEvidence() }
        groundTruthCameraStateLock.lock()
        latestGroundTruthCameraSample = nil
        groundTruthCameraStateLock.unlock()
        if mode == .normal {
            inputControlStatus = visionText("nativeInputReady")
            applyLabelPauseState(.ready)
        } else {
            inputControlStatus = "Modded mode · receiver connecting"
            applyLabelPauseState(.unavailable)
        }
    }

    func startGame(gamePath: String? = nil) {
        expectedGameApplicationURL = gamePath.map {
            HollowKnightInstallationDiscovery.gameApplicationURL(path: $0)
                .resolvingSymlinksInPath().standardizedFileURL
        }
        gameProcessLifetime = GameProcessLifetime()
        if gameMode == .modded {
            gameControlForwarder.start(gamePath: gamePath)
        } else {
            gameControlForwarder.launchGameOnly(gamePath: gamePath)
        }
        armQuitGameExitWatch()
    }

    func stopGameControls() {
        botDemonstrations.stop(reason: "controls-stopped")
        botPlayer.stop(reason: "Controls stopped")
        botDemo.stop(reason: "Controls stopped")
        gameControlForwarder.stop()
    }

    func setNativeReviewActive(_ active: Bool) {
        if active {
            botDemonstrations.stop(reason: "workspace-changed")
            botPlayer.stop(reason: "Workspace changed")
            botDemo.stop(reason: "Workspace changed")
        }
        gameControlForwarder.setNativeReviewActive(active)
    }

    private var botGameplayReady: Bool {
        isCapturing && (isHackerWorkspaceActive()
            ? receiverCapabilities.contains(ReceiverControlProtocol.hackerActorsCapability)
            : activeObjectModelSummary != nil) && labelPauseState == .ready
            && !isPathRecording && !isPathPlaybackActive && !isPathCheckpointPending && !playerOpsBusy
            && !atlasManagementBusy && !hackerAtlasBlocksGameplay && !profileAtlasGate.withLock({ $0 })
            && liveGameStatus == "Gameplay"
    }

    var canCollectBotDemonstration: Bool { botGameplayReady && !botPlayer.isActive && !botDemo.isActive }

    func botDemoStartIssue(training: Bool = false, stencilsReady: Bool = true) -> String? {
        let controlsReady = labelPauseState == .ready
            && !isPathRecording && !isPathPlaybackActive && !isPathCheckpointPending && !playerOpsBusy
            && ((!atlasManagementBusy && (!hackerAtlasBlocksGameplay || botDemo.isHandingOff))
                || (switchingProfileAtlas && botDemo.isActive)) && !botDemonstrations.isRecording
        return BotControlPresentationPolicy.startIssue(mode: gameMode, capturing: isCapturing, training: training,
            stencilsReady: stencilsReady, controlsReady: controlsReady)
    }

    var canStartBotDemo: Bool { botDemoStartIssue() == nil }

    func startBotDemo(inGameplay: Bool = false, domain: BotDomain = .hacker) {
        if let issue = botDemoStartIssue() { botDemo.rejectStart(issue); return }
        botPlayer.stop(reason: "Starting bot control")
        botDemo.start(inGameplay: inGameplay, domain: domain)
    }

    func botDemoDiagnostic() -> String {
        var result: [String: Any] = [
            "active": botDemo.isActive, "phase": botDemo.phaseName, "status": botDemo.status,
            "ready": canStartBotDemo, "applicationActive": NSApp.isActive,
            "keyWindow": NSApp.keyWindow?.title ?? "none", "mainWindow": NSApp.mainWindow?.title ?? "none",
            "capture": isCapturing, "pause": String(describing: labelPauseState),
            "gameContext": liveGameContextIdentifier ?? "unknown", "selected": liveSelectedMenuOption ?? "none",
            "menuDiagnostic": liveMenuStencilDiagnostic ?? "none",
            "trialReturnPoint": botTrialReturnPoint != nil, "botActive": botPlayer.isActive, "botDecisions": botPlayer.decisionCount,
            "botStatus": botPlayer.status, "botAction": botPlayer.lastAction.values,
            "terrainSource": botTerrainSource, "humanTerrainSlots": botHumanTerrainCount,
            "authoredGroundLines": botHumanGround.lineCount,
            "botInputTerrainSource": botPlayer.lastDecisionTerrainSource,
            "botInputHumanTerrainSlots": botPlayer.lastDecisionHumanTerrainCount,
            "botPerception": botPlayer.perceptionDiagnostic,
            "humanGroundDiagnostic": botHumanGround.diagnostic,
            "hackerAtlasBusy": hackerAtlasManagementBusy,
            "hackerAtlasBlocksGameplay": hackerAtlasBlocksGameplay,
            "pathRecording": isPathRecording,
            "learningRecording": botDemonstrations.isRecording,
            "learningObservations": botDemonstrations.observationCount,
            "learningUsable": botDemonstrations.usableCount,
            "learningPath": botDemonstrations.lastSessionURL?.path ?? "none",
            "botDomain": botPlayer.domain.rawValue,
            "botEvaluationPath": botEvaluation.lastDirectory?.path ?? "none",
            "lastReplayPath": lastRecordedPathURL?.path ?? "none",
            "receiverCapabilities": receiverCapabilities.sorted(),
        ]
        if let issue = botDemo.issue { result["issue"] = issue }
        groundTruthCameraStateLock.lock()
        let pose = latestGroundTruthCameraSample
        groundTruthCameraStateLock.unlock()
        if let (sample, seen) = pose, ProcessInfo.processInfo.systemUptime - seen < 0.5, sample.heroAvailable {
            result["evaluationPose"] = ["scene": sample.sceneName, "x": sample.heroX, "y": sample.heroY, "grounded": sample.grounded] as [String: Any]
        }
        if let url = botDemo.lastArchiveURL { result["archivePath"] = url.path }
        if let observed = botDemo.lastObservation {
            result["observedContext"] = observed.context?.storageIdentifier ?? "unknown"
            result["observedSelection"] = observed.selectedIdentifier ?? "none"
            result["observedOccupiedSlots"] = observed.occupiedSlots.sorted()
            result["observedGameplay"] = observed.gameplay
        }
        return String(decoding: (try? JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])) ?? Data("{}".utf8), as: UTF8.self)
    }

    func setBotHumanGround(_ document: GroundTruthLineDocument) {
        botHumanGround.update(document: document)
        refreshHackerPaths()
    }

    private func publishBotObservation(_ observation: BotObservation, image: CGImage, hackerEpoch: UInt64? = nil,
                                       hackerCamera: GroundTruthCameraTransform? = nil,
                                       terrain: [BotTerrainSegment]? = nil, walls: [DetectedWall]? = nil,
                                       pairedRun: UUID? = nil, atlasIsland: LiveIslandCapture? = nil,
                                       solveWidth: Double? = nil, health: Int? = nil, drive: Int = 0,
                                       detections: LiveObjectDetectionBatch? = nil,
                                       pathFloors: [BotTerrainSegment]? = nil, observedFloorEdges: [BotTerrainSegment] = [],
                                       walkedGround: WalkedGroundMemory? = nil, contactWall: BotWallSegment? = nil,
                                       grounded: Bool? = nil,
                                       visualDoors: [BotDoorSegment] = []) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            var observation = observation
            guard !self.profileAtlasGate.withLock({ $0 }), pairedRun == self.botDemonstrations.pairedRecordingID else { return }
            let foreground = self.isHackerWorkspaceActive() == (hackerEpoch != nil)
            guard foreground || pairedRun != nil else { return }
            if let hackerEpoch {
                guard self.isHackerGenerationCurrent(hackerEpoch, requireActive: pairedRun == nil),
                      self.hackerBotGate.accepts(observation, currentGeneration: self.currentStatusGeneration) else { return }
                if let camera = hackerCamera {
                    let room = self.hackerAtlasRooms.first { $0.sceneName == camera.sceneName }
                    let document = self.botHumanGround.snapshot
                    let calibration = room.flatMap { AtlasPathCalibration.hacker(room: $0, document: document, camera: camera) }
                        ?? .init(scene: camera.sceneName, origin: .zero, scale: .init(x: camera.pixelsPerWorldUnitX, y: camera.pixelsPerWorldUnitY))
                    let center = calibration.local(camera.cameraWorld)
                    let local = CGPoint(x: center.x - camera.frameSize.width / 2, y: center.y - camera.frameSize.height / 2)
                    if let room {
                        observation.atlasView = room.botCoverage.view(camera:local,size:camera.frameSize,health:health)
                    }
                } else if !foreground, let pairedRun, let paired = self.pairedVisualAtlas, paired.recording == pairedRun,
                          paired.frame.generation == observation.generation,
                          (0...0.2).contains(observation.capturedAt - paired.frame.capturedAt),
                          paired.frame.availableAt <= observation.availableAt {
                    observation.atlasView = paired.frame.atlasView
                }
            } else {
                guard self.visualBotGate.accepts(observation, currentGeneration: self.currentStatusGeneration) else { return }
                observation = self.botVisualKnightMemory.resolve(observation,
                    previousAction: self.gameplayGym ? self.gymPreviousAction : (self.botPlayer.isActive ? self.botPlayer.lastAction : nil),
                    terrain: terrain, detections: detections)
                observation.driveDirection = drive
                if let atlasIsland, let solveWidth {
                    observation.blockadePose = BotBlockadePose.visual(atlasIsland, width: solveWidth)
                }
                let wallSegments = walls.map { BotWallEvidence.segments(from: $0, height: image.height) }
                let geometry = BotScreenTerrain(width:Double(image.width),height:Double(image.height),
                    floors:terrain ?? [],walls:wallSegments ?? [], doors:visualDoors.isEmpty ? nil : visualDoors)
                observation.detectedTerrain = geometry
                var pathGeometry = geometry
                if let pathFloors { pathGeometry.floors = pathFloors }
                let width = CGFloat(image.width)
                let island = atlasIsland
                let scale = width / max(1,solveWidth ?? width)
                let key = "room/" + String(observation.roomID)
                let priorPathRoom = self.visualPathRoom
                let continuousPath = self.visualPathHandoff.map { prior in
                    prior.generation == observation.generation && prior.room == observation.roomID
                        && (0...0.35).contains(observation.captureTimestamp - prior.timestamp)
                } ?? false
                func inheritDirection(into destination: String) {
                    guard continuousPath,
                          priorPathRoom == key || priorPathRoom.hasPrefix(key + "/") else { return }
                    self.visualPaths.inheritAutomaticDirection(from: priorPathRoom, into: destination)
                }
                func prepareFrameChart(_ destination: String) {
                    if !continuousPath { self.visualPaths.discardFrameChart(room: destination) }
                    inheritDirection(into: destination)
                }
                var unplacedPath = true
                if let island, let solveWidth {
                    let pose = self.publishedIslandProjection?.worldPose(for:island) ?? island.worldPose
                    let registered = island.placement == .reference || island.placement == .connected
                    unplacedPath = !registered
                    let position = registered ? pose : island.localPose
                    let camera = CGPoint(x:position.x*scale,y:position.y*scale)
                    let chartOrigin = CGPoint(x: pose.x - position.x, y: pose.y - position.y)
                    let walked = walkedGround?.pathPlatforms(room: island.roomID, projection: self.publishedIslandProjection,
                                                              scale: scale, anchor: chartOrigin)
                    let islandKey = String(island.id) + "/" + String(island.startedAt.bitPattern)
                    if registered {
                        let translation = CGPoint(x:(pose.x-island.localPose.x)*scale,
                                                  y:(pose.y-island.localPose.y)*scale)
                        if PathPoint.supports(translation) {
                            self.visualPaths.promote(from:key + "/" + islandKey, into:key,
                                translation:PathPoint(translation))
                        }
                    }
                    self.visualPathRoom = registered ? key : key + "/" + islandKey
                    if !registered { inheritDirection(into: self.visualPathRoom) }
                    let anchor = self.presentedIslandFrame?.anchor ?? self.liveWorldSnapshot?.observations.first(where: { $0.id == self.liveWorldSnapshot?.anchoredObservationID })?.optimizedPose ?? self.liveWorldSnapshot?.observations.first?.optimizedPose
                    if registered, let anchor {
                        self.visualPathOffset = .init(x:-anchor.x*scale,y:-anchor.y*scale)
                    } else {
                        self.visualPathOffset = .init(x:self.liveBounds.minX-camera.x,y:self.liveBounds.minY-camera.y)
                    }
                    let seams = self.visualPaths.handoffSeams(room: self.visualPathRoom, island: island,
                        registered: registered, projection: self.publishedIslandProjection, scale: scale,
                        generation: observation.generation, time: observation.captureTimestamp)
                    self.visualPaths.observe(room:self.visualPathRoom,camera:camera,width:width,row:&observation,
                        geometry:island.measured ? pathGeometry : nil,walked:walked,observedFloorEdges:observedFloorEdges,
                        contactWall:contactWall,handoffSeams:seams,space:registered ? "registered" : islandKey,trackPosition:island.measured)
                    if let map = self.liveWorldSnapshot {
                        self.botAtlasCoverage.update(map) { self.visualRoomTracker.compositionBounds(for:$0) }
                        observation.atlasView = self.botAtlasCoverage.view(pose:pose,width:solveWidth,
                            height:solveWidth*Double(image.height)/Double(image.width),room:observation.roomID,health:health)
                    }
                    // Until the first measured ground is registered, use only the current frame.
                    if observation.roadHint == nil, !island.measured, self.visualPaths.graph(room:self.visualPathRoom)?.floors.isEmpty != false {
                        self.visualPathRoom += "/frame"
                        prepareFrameChart(self.visualPathRoom)
                        self.visualPathOffset = self.liveBounds.origin
                        self.visualPaths.observe(room:self.visualPathRoom,camera:.zero,width:width,row:&observation,
                            geometry:pathGeometry,trackPosition:false,replaceFrame:true)
                    }
                } else {
                    self.visualPathRoom = key + "/frame"; self.visualPathOffset = self.liveBounds.origin
                    prepareFrameChart(self.visualPathRoom)
                    self.visualPaths.observe(room:self.visualPathRoom,camera:.zero,width:width,row:&observation,
                        geometry:pathGeometry,trackPosition:false,replaceFrame:true)
                }
                if unplacedPath {
                    self.visualPaths.limitUnplacedCredit(room:key, chart:self.visualPathRoom, row:&observation)
                }
                self.visualPathHandoff = (observation.generation, observation.roomID, observation.captureTimestamp)
                let wallFeatures = BotWallEvidence.features(segments: observation.features[4] > 0 ? wallSegments : nil,
                    knightX: observation.features[0], knightY: observation.features[1], width: Double(image.width))
                observation.features.replaceSubrange(BotObservationContract.legacyFeatureCount..., with: wallFeatures)
            }
            // Visual paths above remain available for drawing, but policy and
            // credit always use the exact matched Hacker planner sample.
            let sharedPath = self.sharedHackerPath(for: observation)
            observation.policyScene = sharedPath?.policyScene
            observation.roadHint = sharedPath?.roadHint
            observation.pathProgress = sharedPath?.pathProgress
            observation.pathAlignment = sharedPath?.pathAlignment
            observation.hackerPathVersion = 1
            if hackerEpoch == nil, let pairedRun { self.pairedVisualAtlas = (pairedRun, observation) }
            let domain = BotDomain.of(observation)
            let projection = self.publishedIslandProjection
            let commandedDirection = observation.driveDirection ?? 0
            if domain == .gameplay, observation.blockadePose == nil, let camera = observation.visualContext?.camera {
                observation.blockadePose = BotBlockadePose(space: "local/\(observation.generation)/\(observation.roomID)",
                    x: camera.x, y: camera.y, width: 1)
            }
            if self.gameplayGym || observation.captureTimestamp > self.blockadeResetAt {
                let previousRevision = self.blockadeVisits[domain]?.map.revision ?? 0
                observation.blockade = self.blockadeVisits[domain, default: BotBlockadeVisit()].observe(
                    observation, pose: observation.blockadePose, direction: commandedDirection,
                    offset: { space in atlasIsland.flatMap { projection?.blockadeOffset(from: space, to: $0) } })
                if !self.gameplayGym, previousRevision != self.blockadeVisits[domain]?.map.revision {
                    self.objectWillChange.send()
                }
            }
            if self.gameplayGym { self.gymObservation = observation; return }
            if !foreground, let pairedRun {
                self.botDemonstrations.recordPairedObservation(observation, image: image, run: pairedRun)
                return
            }
            let terrainSource = observation.terrainSource ?? "detected"
            let humanCount = observation.humanTerrainCount ?? 0
            if self.botTerrainSource != terrainSource { self.botTerrainSource = terrainSource }
            if self.botHumanTerrainCount != humanCount { self.botHumanTerrainCount = humanCount }
            self.botDemonstrations.observe(observation, image: image)
            if self.botPlayer.isActive { self.botEvaluation.observe(observation, image: image) }
            self.skillObservation = observation
            self.botPlayer.observe(observation)
        }
    }

    func stopBotDemo(reason: String = "Demo stopped") {
        guard botDemo.isActive else { return }
        botDemo.stop(reason: reason)
        botPlayer.stop(reason: reason)
    }

    func completeBotDemoHandoff(_ request: UUID, deadline: Double? = nil) {
        guard botDemo.isActive, botDemo.handoffRequest == request else { return }
        let deadline = deadline ?? ProcessInfo.processInfo.systemUptime + 15
        guard ProcessInfo.processInfo.systemUptime < deadline else {
            botDemo.stop(reason: "\(botDemo.domain.title) bot controls did not become ready", failed: true); return
        }
        guard canStartBotPlayback else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
                self?.completeBotDemoHandoff(request, deadline: deadline)
            }
            return
        }
        botDemo.completeHandoff(request) { [self] in
            botPlayer.start(domain: botDemo.domain)
            return botPlayer.isActive
        }
    }

    var canStartBotPlayback: Bool {
        (botGameplayReady || (botPlayer.allowsInGameMenu && canStartBotDemo)) && gameMode == .modded && !botDemonstrations.isRecording
            && !botPlayer.isLoading
            && (!botDemo.isActive || botDemo.isHandingOff || botDemo.isPlaying)
    }

    @MainActor
    func runThinkingMenu(_ script: ThinkingMenuScript, generation: UInt64,
                         enabled: () -> Bool) async -> ThinkingHandoff {
        func ready() -> Bool {
            enabled() && skillGeneration == generation && botPlayer.automaticPoliciesEnabled && canStartBotDemo &&
                !botPlayer.isActive && !botDemo.isActive && NSApp.isActive && NSApp.keyWindow != nil
        }
        if script.request == .wait { return .status("Waiting for new menu evidence.") }
        guard ready(), let lease = gameControlForwarder.beginBotControl() else {
            return .status("Skipped · menu controls unavailable.")
        }
        thinkingMenuLease = lease
        defer {
            gameControlForwarder.endBotControl(sessionID: lease)
            if thinkingMenuLease == lease { thinkingMenuLease = nil }
        }
        return await script.run(current: { ready() ? self.currentSkillSight()?.menuPage : nil }, press: { input in
            guard ready(), self.gameControlForwarder.pulseBotDemoButton(input.hollowKnightButton, sessionID: lease) else { return false }
            if input == .confirm, case .select(let element) = script.request {
                self.thinkingScreens.confirmed(element, on: script.page.stateID)
            }
            return true
        })
    }

    func toggleBotPlayback() {
        if let lease = thinkingMenuLease {
            botPlayer.automaticPoliciesEnabled = false
            gameControlForwarder.endBotControl(sessionID: lease)
            thinkingMenuLease = nil
            return
        }
        if botDemo.isActive { botPlayer.automaticPoliciesEnabled = false; stopBotDemo(reason: "Stopped with B") }
        else if botPlayer.isActive { botPlayer.automaticPoliciesEnabled = false; botPlayer.stop() }
        else if canStartBotPlayback { botPlayer.automaticPoliciesEnabled = true; botPlayer.start(domain: isHackerWorkspaceActive() ? .hacker : .gameplay) }
    }

    func startBotDemonstration() {
        guard canCollectBotDemonstration else { return }
        gameControlForwarder.releaseForGroundLabeling()
        botDemonstrations.start(gameMode: gameMode.rawValue,
                                detectorVersion: activeObjectModelSummary ?? "unknown", domain: isHackerWorkspaceActive() ? .hacker : .gameplay,
                                skillControls: receiverCapabilities.contains("skill-controls-v1"))
    }

    func startGameControls() {
        configureGameMode(.modded)
        startGame()
    }

    /// Room identity survives independently of adjacency. Saved rooms without
    /// a measured portal remain disconnected; old placements are never moved
    /// just because a session happens to contain two room IDs.
    @discardableResult
    private func restoreRoomTopology(
        worldSnapshot: LiveWorldSnapshot
    ) -> VisualRoomSnapshot {
        roomTopologyWritesEnabled = true
        do {
            var restored = try visualRoomTracker.restoreOrImportDisconnectedRooms(
                from: roomTopologyURL, world: worldSnapshot)
            let durableRoomIDs = Set(worldSnapshot.observations.map(\.roomID))
            if durableRoomIDs.count == 1,
               !durableRoomIDs.contains(restored.roomID),
               let observation = worldSnapshot.observations.first {
                restored = visualRoomTracker.restoreActiveRoomFromDurableEvidence(
                    observation.roomID, entryWorldPose: observation.optimizedPose
                )
            }
            return publishRoomTopology(restored)
        } catch {
            // Continue with conservative in-memory ownership, but do not allow
            // a later capture to overwrite a file whose preservation failed.
            roomTopologyWritesEnabled = false
            performanceLog.error("room topology import preserved on disk; writes disabled: \(error.localizedDescription, privacy: .public)")
            return publishRoomTopology(visualRoomTracker.restoreDisconnectedRooms(from: worldSnapshot))
        }
    }

    private func persistRoomTopology() {
        guard roomTopologyWritesEnabled else { return }
        do {
            try visualRoomTracker.saveTopology(to: roomTopologyURL)
        } catch {
            performanceLog.error(
                "room topology save failed: \(error.localizedDescription, privacy: .public)"
            )
        }
    }

    @discardableResult
    private func publishRoomTopology(
        _ room: VisualRoomSnapshot
    ) -> VisualRoomSnapshot {
        lastPublishedRoomStatus = room.statusText
        lastPublishedTransitionPortals = room.portals
        DispatchQueue.main.async { [weak self] in
            self?.activeRoomStatus = room.statusText
            self?.transitionPortals = room.portals
        }
        return room
    }

    private var replaySceneRegions: SceneRegions? {
        guard visualReplayMode, let input = visualReplayInput else { return nil }
        return SceneRegions(knight: input.knightRect, health: .zero, geo: .zero, mana: .zero,
            // Knight was already part of the archived exclusion array.
            hudStencilRects: input.excludingRects.filter { $0 != input.knightRect })
    }

    func prepareVisualReplay(startTimestamp: Double, createdAt: Date, sourcePathID: UUID) {
        precondition(visualReplayMode)
        visualReplayLastJump = -.infinity
        advanceGeneration(resetCamera: true)
        sampleQueue.sync {}
        visionQueue.sync {
            gameStartupCoordinator.restoreGameplaySession()
            trackingStateLock.withLock {
                trackingState = TrackingState(cameraPosition: trackingState.cameraPosition,
                    regions: trackingState.regions, poseTimestamp: trackingState.poseTimestamp,
                    basisTicket: trackingState.basisTicket, groundAnchored: trackingState.groundAnchored,
                    gameplayEnabled: true, generation: trackingState.generation)
            }
        }
        if !gameplayGym { _ = inputPathRecorder.start(at: startTimestamp, createdAt: createdAt, sourcePathID: sourcePathID) }
    }

    func prepareGameplayGym(compiledDirectory: URL, confirmationURL: URL? = nil,
                            knightURL: URL? = nil, depositURL: URL? = nil) throws -> (crawlid: Bool, knight: Bool, deposit: Bool) {
        precondition(gameplayGym)
        let artifacts = try modelVersionStore.activeModelArtifacts()
        try objectDetector.load(artifacts, compiledDirectory: compiledDirectory, confirmationURL: confirmationURL,
                                knightURL: knightURL, depositURL: depositURL)
        guard !objectDetector.artifacts.isEmpty else { throw BotRuntimeError.incompatible("Gameplay requires a trained visual detector.") }
        let language = UserDefaults.standard.string(forKey: "HollowKnightVision.initialization.language.v1") ?? "en"
        let template = HUDStencilTemplate.captured(examples: try LabelingExampleStore().loadExamples(), languageIdentifier: language)
        sampleQueue.sync { hudStencilTracker = HUDStencilTracker(template: template) }
        return (objectDetector.usesCrawlidLocalizer, objectDetector.usesKnightLocalizer, objectDetector.usesDepositLocalizer)
    }

    /// Each request arrives after six simulation ticks. Compute the normal pixel refinement
    /// before the camera samples the frame; an episode owns its map and its action history.
    func observeGameplayGym(_ image: CGImage, ticks: Int, previous: BotAction) throws -> BotObservation? {
        let t = 1 + Double(ticks) / 60
        let generation = DispatchQueue.main.sync { () -> UInt64 in
            gymPreviousAction = previous
            gymObservation = nil
            return currentStatusGeneration
        }
        let identifier = UInt64(ticks + 1)
        let raw = try objectDetector.detect(in: image, sourceFrameIdentifier: identifier)
        let refined = LiveObjectPixelRefiner.refine(raw, in: image, createsDiagnosticImage: false)
        objectInferenceStateLock.withLock {
            let batch = LiveObjectDetectionBatch(detections: refined.detections, sourceFrameIdentifier: identifier,
                sourceTimestamp: t, captureGeneration: generation, inferenceDuration: 0)
            previousMaskDetectionBatch = latestMaskDetectionBatch
            latestMaskDetectionBatch = batch
            latestObjectDetectionBatch = .init(detections: objectDetectionTracker.update(detections: refined.detections,
                captureGeneration: generation), sourceFrameIdentifier: identifier, sourceTimestamp: t,
                captureGeneration: generation, inferenceDuration: 0)
            botDetectionHistory = [batch]
        }
        let record = VisualReplayFrame(index: ticks, captureTimestamp: t, capturedAt: t, captureGeneration: generation,
            solveWidth: Double(image.width), width: image.width, height: image.height, renderedGameFrame: nil)
        consumeVisualReplayFrame(image, record: record, input: nil,
            direction: VisualRoomDirection(rawValue: Int(previous.previousFeatures[0])),
            jumpInput: previous.buttons.contains(.actionZ), worldTimestamp: t)
        return DispatchQueue.main.sync { gymObservation }
    }

    /// Called serially by the offline driver. The only frozen stage outputs are
    /// visual masks/tuning. All camera estimates and room ownership are rerun.
    func consumeVisualReplayFrame(_ image: CGImage, record: VisualReplayFrame,
                                  input: VisualReplayGroundInput?,
                                  direction: VisualRoomDirection?, jumpInput: Bool, worldTimestamp: Double) {
        precondition(visualReplayMode)
        visualReplayInput = input
        visualReplayDirection = direction
        if jumpInput { visualReplayLastJump = record.captureTimestamp }
        visualReplayWorldTimestamp = worldTimestamp
        if let input {
            groundTuning = input.tuning
            semanticGroundTuning = input.semanticTuning
        }
        sampleQueue.sync {
            stageConvertedFrame(ConvertedCaptureFrame(image: image, solveWidth: CGFloat(record.solveWidth)),
                timestamp: record.captureTimestamp, capturedAt: record.capturedAt)
        }
        // One bounded serial schedule; no wall-clock race decides which image
        // or completed global search the next observation sees.
        visionQueue.sync {}
        atlasPipeline.drainVisualReplay()
        visionQueue.sync {}
    }

    func finishVisualReplay(at timestamp: Double) -> (RecordedInputPath?, LiveWorldSnapshot) {
        precondition(visualReplayMode)
        visionQueue.sync {}
        atlasPipeline.drainVisualReplay()
        visionQueue.sync {}
        return (inputPathRecorder.stop(at: timestamp), atlasPipeline.currentWorldSnapshot())
    }

    var canStartPathRecording: Bool {
        labelPauseState == .ready && !isPathPlaybackActive && !isPathCheckpointPending
            && !botDemonstrations.isRecording && !botPlayer.isActive && !botDemo.isActive
    }

    func togglePathRecording() {
        guard !isPathPlaybackActive, !isPathCheckpointPending else { return }
        if isPathRecording {
            stopPathRecording()
        } else {
            startPathRecording()
        }
    }

    func refreshPlayerTestState() {
        sendPlayerOps(
            status: "Loading player state…",
            command: { ReceiverPlayerOpsCommand.query(sessionID: $0) }
        )
    }

    func resetPlayerTestState() {
        playerTestDraft = .defaults
        applyPlayerTestState()
    }

    func applyPlayerTestState() {
        let state = playerTestDraft.normalized
        playerTestDraft = state
        guard !playerOpsBusy else {
            playerOpsApplyQueued = true
            playerOpsStatus = "Applying latest player state…"
            return
        }
        sendPlayerOps(
            status: "Applying player state…",
            command: { ReceiverPlayerOpsCommand.apply(sessionID: $0, state: state) }
        )
    }

    func randomizePlayerTestState() {
        var generator = SystemRandomNumberGenerator()
        let state = PlayerTestState.randomized(
            invincible: playerTestDraft.invincible,
            using: &generator
        )
        playerTestDraft = state
        sendPlayerOps(
            status: "Randomizing player state…",
            command: { ReceiverPlayerOpsCommand.apply(sessionID: $0, state: state) }
        )
    }

    func restoreEnemies() {
        sendPlayerOps(
            status: "Restoring enemies…",
            command: { ReceiverPlayerOpsCommand.restoreEnemies(sessionID: $0) }
        )
    }

    var canResetRoom: Bool {
        receiverCapabilities.contains(ReceiverControlProtocol.roomResetCapability)
            && !playerOpsBusy && !atlasManagementBusy && !hackerAtlasManagementBusy
            && !botDemonstrations.isRecording && !botPlayer.isActive && !botDemo.isActive
            && !isPathRecording && !isPathPlaybackActive && !isPathCheckpointPending
    }

    var managedGameIsActive: Bool { gameControlForwarder.managedGameIsActive }

    func resetRoom() {
        guard canResetRoom else { return }
        sendPlayerOps(status: "Resetting room…", command: { .resetRoom(sessionID: $0) }) { [weak self] ack in
            guard let self else { return }
            guard let scene = ack.resetScene, !scene.isEmpty else {
                self.playerOpsStatus = "Room reset was not confirmed."
                return
            }
            self.resetLiveBlockades()
            var issues = [String]()
            self.playerOpsBusy = true
            self.playerOpsStatus = "Clearing Atlas…"
            self.performAtlasManagement(replacingActiveWorld: true, { try self.purgeActiveAtlas() }) { error in
                if let error { issues.append(error.localizedDescription) }
                self.performHackerAtlasManagement(replacingActive: true, completion: { error in
                    if let error { issues.append(error.localizedDescription) }
                    self.playerOpsBusy = false
                    self.playerOpsStatus = issues.isEmpty ? "" : "Room reset. " + issues.joined(separator: " ")
                    self.applyQueuedPlayerStateIfNeeded()
                }) { $0.emptyForCurrentProfile }
            }
        }
    }

    private func sendPlayerOps(
        status: String,
        command: @escaping (UUID) -> ReceiverPlayerOpsCommand,
        onSuccess: ((ReceiverPlayerOpsAcknowledgement) -> Void)? = nil
    ) {
        guard !playerOpsBusy else { return }
        playerOpsBusy = true
        playerOpsStatus = status
        let accepted = gameControlForwarder.performPlayerOps(command) { [weak self] acknowledgement in
            DispatchQueue.main.async {
                guard let self else { return }
                self.playerOpsBusy = false
                let applyQueued = self.playerOpsApplyQueued
                guard let acknowledgement else {
                    self.playerOpsStatus = "Updated game receiver is not ready."
                    self.applyQueuedPlayerStateIfNeeded()
                    return
                }
                guard acknowledgement.accepted else {
                    self.playerOpsStatus = acknowledgement.failure ?? "Player operation failed."
                    self.applyQueuedPlayerStateIfNeeded()
                    return
                }
                if let state = acknowledgement.state, !applyQueued {
                    self.playerTestDraft = state.normalized
                }
                if let onSuccess {
                    onSuccess(acknowledgement)
                    if !self.playerOpsBusy { self.applyQueuedPlayerStateIfNeeded() }
                    return
                }
                if acknowledgement.enemiesRestored != nil {
                    self.playerOpsStatus = "Enemies restored."
                } else {
                    self.playerOpsStatus = ""
                }
                self.applyQueuedPlayerStateIfNeeded()
            }
        }
        if !accepted {
            playerOpsBusy = false
            playerOpsStatus = "Updated game receiver is not ready."
        }
    }

    private func applyQueuedPlayerStateIfNeeded() {
        guard playerOpsApplyQueued else { return }
        playerOpsApplyQueued = false
        applyPlayerTestState()
    }

    func stopPathRecording() {
        guard !stoppingUnifiedRecording else { return }
        stoppingUnifiedRecording = true
        defer { stoppingUnifiedRecording = false }
        botDemonstrations.stop()
        guard let path = inputPathRecorder.stop() else { return }
        isPathRecording = false
        restoreRenderFrameMarkerPolicy()
        pathRecordingStartedAt = nil
        inputPathLog.notice(
            "stopped id=\(path.id.uuidString, privacy: .public) duration=\(path.duration, privacy: .public) events=\(path.events.count, privacy: .public) samples=\(path.trackingSamples.count, privacy: .public) unverified=\(path.unverifiedSampleCount, privacy: .public) corrections=\(path.globalCorrectionCount, privacy: .public)"
        )
        inputPathStoreQueue.async { [weak self] in
            guard let self else { return }
            do {
                let url = try self.inputPathStore.save(path)
                DispatchQueue.main.async {
                    self.lastRecordedPathURL = url
                    self.pathRecordingIssue = nil
                }
                self.inputPathLog.notice("saved path=\(url.path, privacy: .public)")
            } catch {
                DispatchQueue.main.async {
                    self.pathRecordingIssue = "Path save failed: \(error.localizedDescription)"
                }
                self.inputPathLog.error("save failed error=\(String(describing: error), privacy: .public)")
            }
        }
    }

    private var botTrialReturnPoint: RecordedGameCheckpoint?

    func markBotTrialReturnPoint() -> Bool {
        guard botTrialReturnPoint == nil, canStartBotPlayback, !botPlayer.isActive, !botDemo.isActive else { return false }
        isPathCheckpointPending = true
        let accepted = gameControlForwarder.capturePlayerCheckpoint { [weak self] checkpoint, _ in
            self?.botTrialReturnPoint = checkpoint
            self?.isPathCheckpointPending = false
        }
        if !accepted { isPathCheckpointPending = false }
        return accepted
    }

    func returnFromBotTrial() -> Bool {
        guard let checkpoint = botTrialReturnPoint, canStartBotPlayback, !botPlayer.isActive, !botDemo.isActive else { return false }
        return restoreBotTrialCheckpoint(checkpoint) { [weak self] in self?.botTrialReturnPoint = nil }
    }

    func restoreBotTrialStart(fileName: String) -> Bool {
        guard !isHackerWorkspaceActive(), canStartBotPlayback, !botDemo.isActive, !botPlayer.isActive,
              let command = GamePathPlaybackCommand(serialized: "replay-path:\(fileName):1"),
              let path = try? inputPathStore.load(named: command.fileName),
              let checkpoint = path.startCheckpoint else { return false }
        return restoreBotTrialCheckpoint(checkpoint)
    }

    private func restoreBotTrialCheckpoint(_ checkpoint: RecordedGameCheckpoint, onRestored: (() -> Void)? = nil) -> Bool {
        isPathCheckpointPending = true
        let accepted = gameControlForwarder.restorePlayerCheckpoint(checkpoint) { [weak self] restored, failure in
            guard let self else { return }
            self.isPathCheckpointPending = false
            self.pathRecordingIssue = restored ? nil : failure
            self.botVisualKnightMemory.reset()
            if restored { self.resetLiveBlockades(); onRestored?() }
        }
        if !accepted { isPathCheckpointPending = false }
        return accepted
    }

    @discardableResult
    func performPathPlaybackCommand(_ command: GamePathPlaybackCommand) -> Bool {
        guard !isPathRecording, !isPathPlaybackActive,
              !isPathCheckpointPending, canStartPathRecording else {
            inputPathLog.error(
                "playback command rejected recording=\(self.isPathRecording, privacy: .public) active=\(self.isPathPlaybackActive, privacy: .public) checkpoint=\(self.isPathCheckpointPending, privacy: .public) pause=\(String(describing: self.labelPauseState), privacy: .public)"
            )
            return false
        }
        do {
            let path = try inputPathStore.load(named: command.fileName)
            guard path.startCheckpoint != nil else {
                pathRecordingIssue = "Path has no saved start position; record a new path"
                return false
            }
            return restoreAndStartPathPlayback(path, iteration: command.iteration)
        } catch {
            pathRecordingIssue = "Path playback failed: \(error.localizedDescription)"
            inputPathLog.error("playback load failed error=\(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    private func startPathRecording() {
        guard canStartPathRecording else { return }
        isPathCheckpointPending = true
        pathRecordingIssue = nil
        // Manual runs need the same exact capture/telemetry identity as
        // automated playback. Receipt timestamps alone cannot score shake.
        gameControlForwarder.setRenderFrameMarkerEnabled(true)
        let accepted = gameControlForwarder.capturePlayerCheckpoint { [weak self] checkpoint, failure in
            guard let self else { return }
            self.isPathCheckpointPending = false
            guard let checkpoint else {
                self.pathRecordingIssue = failure ?? "Start-position capture failed"
                self.restoreRenderFrameMarkerPolicy()
                return
            }
            let startedAt = ProcessInfo.processInfo.systemUptime
            let createdAt = Date()
            let recordingID = UUID()
            guard self.inputPathRecorder.start(
                at: startedAt,
                createdAt: createdAt,
                id: recordingID,
                startCheckpoint: checkpoint,
                visualArchiveRootURL: self.inputPathStore.rootURL
            ) else {
                self.restoreRenderFrameMarkerPolicy()
                return
            }
            let domain: BotDomain = self.canUseHacker ? .hacker : .gameplay
            let visualDetector = self.activeObjectModelSummary ?? "visual"
            guard self.botDemonstrations.start(gameMode: self.gameMode.rawValue,
                detectorVersion: domain == .hacker ? "receiver-actors-v1" : visualDetector,
                id: recordingID, at: startedAt, requiresUsableObservation: false,
                domain: domain, pairedGameplayDetector: domain == .hacker ? visualDetector : nil,
                skillControls: self.receiverCapabilities.contains("skill-controls-v1")) else {
                self.inputPathRecorder.cancel()
                self.pathRecordingIssue = self.botDemonstrations.issue ?? "Learning recording could not start"
                self.restoreRenderFrameMarkerPolicy()
                return
            }
            if self.isHackerWorkspaceActive() { self.botVisualKnightMemory.reset() }
            self.botDemonstrations.attachEvidence(reference: self.botHumanGround.snapshot,
                domain: domain, replayRoot: self.inputPathStore.rootURL)
            self.isPathRecording = true
            self.pathRecordingStartedAt = createdAt
            self.pathRecordingIssue = nil
            self.inputPathLog.notice(
                "started scene=\(checkpoint.sceneName, privacy: .public) hero=\(checkpoint.heroX, privacy: .public),\(checkpoint.heroY, privacy: .public) uptime=\(startedAt, privacy: .public)"
            )
        }
        guard accepted else {
            isPathCheckpointPending = false
            pathRecordingIssue = "Updated game receiver is not ready"
            restoreRenderFrameMarkerPolicy()
            return
        }
    }

    private func restoreAndStartPathPlayback(
        _ path: RecordedInputPath,
        iteration: Int
    ) -> Bool {
        guard path.startCheckpoint != nil else { return false }
        isPathCheckpointPending = true
        isPathPlaybackActive = true
        pathPlaybackIteration = iteration
        pathRecordingStartedAt = nil
        pathRecordingIssue = nil
        pathPlaybackAbortIssue = nil
        playbackDivergenceMonitor.prepare()
        gameControlForwarder.setRenderFrameMarkerEnabled(true)
        setPlaybackPreparationCapturePaused(true)
        // Reset the visual epoch before moving the game. Restoring first and
        // then restoring again after deletion visibly looped the one-way route
        // back to Room 1 twice even though playback itself never loops.
        performAtlasManagement(
            replacingActiveWorld: true,
            { [self] in try self.purgeActiveAtlas() },
            completion: { [weak self] resetError in
                guard let self, self.isPathPlaybackActive,
                      self.pathPlaybackIteration == iteration else { return }
                guard resetError == nil else {
                    self.setPlaybackPreparationCapturePaused(false)
                    self.abortPathPlayback(
                        "Replay atlas reset failed: \(resetError!.localizedDescription)"
                    )
                    return
                }
                self.inputPathLog.notice(
                    "fresh atlas source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public)"
                )
                self.restoreOnceAfterAtlasReset(path, iteration: iteration)
            }
        )
        return true
    }

    private func restoreOnceAfterAtlasReset(
        _ path: RecordedInputPath,
        iteration: Int
    ) {
        guard let checkpoint = path.startCheckpoint else {
            abortPathPlayback("Saved path has no start position")
            return
        }
        let accepted = gameControlForwarder.restorePlayerCheckpoint(checkpoint) { [weak self] restored, failure in
            guard let self, self.isPathPlaybackActive,
                  self.pathPlaybackIteration == iteration else { return }
            guard restored else {
                self.setPlaybackPreparationCapturePaused(false)
                self.abortPathPlayback(failure ?? "Start-position restore failed")
                return
            }
            self.inputPathLog.notice(
                "single restore source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public) hero=\(checkpoint.heroX, privacy: .public),\(checkpoint.heroY, privacy: .public)"
            )
            self.resetLiveBlockades()
            // The checkpoint receiver has positively restored a gameplay
            // scene. Serialize that fact with vision processing before waiting
            // for the freshly reset ground tracker to become ready.
            self.setPlaybackPreparationCapturePaused(false)
            self.visionQueue.async { [weak self] in
                guard let self else { return }
                self.gameStartupCoordinator.restoreGameplaySession()
                let restoredAt = ProcessInfo.processInfo.systemUptime
                self.waitForFreshGroundBeforePathPlayback(
                    path,
                    iteration: iteration,
                    deadline: ProcessInfo.processInfo.systemUptime
                        + Self.pathPlaybackReadinessTimeoutSeconds,
                    consecutiveReadyChecks: 0,
                    restoredAt: restoredAt,
                    finalEpoch: false
                )
            }
        }
        if !accepted {
            setPlaybackPreparationCapturePaused(false)
            abortPathPlayback("Start-position restore was not accepted")
            return
        }
        inputPathLog.notice(
            "restore requested source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public) scene=\(checkpoint.sceneName, privacy: .public)"
        )
    }

    private func abortPathPlayback(_ issue: String) {
        inputPathLog.error("playback preparation aborted: \(issue, privacy: .public)")
        setPlaybackPreparationCapturePaused(false)
        isPathCheckpointPending = false
        isPathPlaybackActive = false
        pathPlaybackIteration = nil
        pathRecordingStartedAt = nil
        pathRecordingIssue = issue
        pathPlaybackAbortIssue = nil
        playbackDivergenceMonitor.stop()
        restoreRenderFrameMarkerPolicy()
    }

    private func setPlaybackPreparationCapturePaused(_ paused: Bool) {
        capturedFrameStateLock.lock()
        playbackPreparationCapturePaused = paused
        capturedFrameStateLock.unlock()
    }

    private func waitForFreshGroundBeforePathPlayback(
        _ path: RecordedInputPath,
        iteration: Int,
        deadline: TimeInterval,
        consecutiveReadyChecks: Int,
        restoredAt: TimeInterval,
        finalEpoch: Bool
    ) {
        // This method and its state run only on visionQueue. Main-thread
        // playback identity is rechecked before any visible transition.
        let tracking = pathPlaybackGroundReadiness
        let observationReady = tracking.poseVerified
            && tracking.hasConfirmedGround
            && tracking.groundSegmentCount > 0
        let atlasReady = freshAtlasBootstrapGate.isReady
            && atlasPipeline.hasCommittedWorldEvidence
        let checkpointReady = path.startCheckpoint.map {
            checkpointPoseMatches($0, observedAfter: restoredAt)
        } ?? false
        let readyChecks = observationReady && atlasReady && checkpointReady
            ? consecutiveReadyChecks + 1 : 0
        if readyChecks >= Self.pathPlaybackRequiredReadyChecks {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isPathPlaybackActive,
                      self.pathPlaybackIteration == iteration else { return }
                if !finalEpoch {
                    self.resetAtlasAfterPlaybackSettle(path, iteration: iteration)
                    return
                }
                self.isPathCheckpointPending = false
                let checkpointScene = path.startCheckpoint?.sceneName ?? "missing"
                let checkpointX = path.startCheckpoint?.heroX ?? .nan
                let checkpointY = path.startCheckpoint?.heroY ?? .nan
                self.inputPathLog.notice(
                    "start verified source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public) scene=\(checkpointScene, privacy: .public) hero=\(checkpointX, privacy: .public),\(checkpointY, privacy: .public) segments=\(tracking.groundSegmentCount, privacy: .public)"
                )
                if !self.startRestoredPathPlayback(path, iteration: iteration) {
                    self.abortPathPlayback("Path playback rejected by controls")
                }
            }
            return
        }
        guard ProcessInfo.processInfo.systemUptime < deadline else {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isPathPlaybackActive,
                      self.pathPlaybackIteration == iteration else { return }
                self.abortPathPlayback(
                    "Saved start pose or fresh atlas ground tracking did not initialize"
                )
            }
            return
        }
        visionQueue.asyncAfter(
            deadline: .now() + Self.pathPlaybackReadinessPollSeconds
        ) { [weak self] in
            self?.waitForFreshGroundBeforePathPlayback(
                path,
                iteration: iteration,
                deadline: deadline,
                consecutiveReadyChecks: readyChecks,
                restoredAt: restoredAt,
                finalEpoch: finalEpoch
            )
        }
    }

    /// Checkpoint telemetry reaches its saved values before the rendered camera
    /// finishes easing from the prior endpoint. Let that visible motion settle,
    /// then discard its temporary atlas/odometry epoch without restoring the
    /// game again. The final epoch therefore starts at zero on the stable image.
    private func resetAtlasAfterPlaybackSettle(
        _ path: RecordedInputPath,
        iteration: Int
    ) {
        setPlaybackPreparationCapturePaused(true)
        performAtlasManagement(
            replacingActiveWorld: true,
            { [self] in try self.purgeActiveAtlas() },
            completion: { [weak self] resetError in
                guard let self, self.isPathPlaybackActive,
                      self.pathPlaybackIteration == iteration else { return }
                guard resetError == nil else {
                    self.setPlaybackPreparationCapturePaused(false)
                    self.abortPathPlayback(
                        "Stable-start atlas reset failed: \(resetError!.localizedDescription)"
                    )
                    return
                }
                self.inputPathLog.notice(
                    "stable start atlas source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public) restoreCount=1"
                )
                self.visionQueue.async { [weak self] in
                    guard let self, self.isPathPlaybackActive,
                          self.pathPlaybackIteration == iteration else { return }
                    // Both resets were queued before this barrier. Do not admit
                    // a capture until room-local motion is also back at origin.
                    self.sampleQueue.sync {}
                    self.gameStartupCoordinator.restoreGameplaySession()
                    let finalEpochStartedAt = ProcessInfo.processInfo.systemUptime
                    self.setPlaybackPreparationCapturePaused(false)
                    self.waitForFreshGroundBeforePathPlayback(
                        path,
                        iteration: iteration,
                        deadline: finalEpochStartedAt
                            + Self.pathPlaybackReadinessTimeoutSeconds,
                        consecutiveReadyChecks: 0,
                        restoredAt: finalEpochStartedAt,
                        finalEpoch: true
                    )
                }
            }
        )
    }

    /// A restore acknowledgement means the mod applied the checkpoint, but a
    /// scene-entry coroutine can still move the Knight or camera afterward.
    /// Start input only after fresh rendered telemetry proves both remained at
    /// the exact recorded start for the same stability window as ground.
    private func checkpointPoseMatches(
        _ checkpoint: RecordedGameCheckpoint,
        observedAfter restoredAt: TimeInterval
    ) -> Bool {
        groundTruthCameraStateLock.lock()
        let latest = latestGroundTruthCameraSample
        groundTruthCameraStateLock.unlock()
        guard let (sample, observedAt) = latest,
              observedAt >= restoredAt,
              sample.heroAvailable, sample.cameraAvailable,
              sample.sceneName == checkpoint.sceneName,
              sample.grounded == checkpoint.grounded,
              sample.facingRight == checkpoint.facingRight else { return false }
        let tolerance = Self.pathPlaybackPoseTolerance
        return abs(sample.heroX - checkpoint.heroX) <= tolerance
            && abs(sample.heroY - checkpoint.heroY) <= tolerance
            && abs(sample.heroZ - checkpoint.heroZ) <= tolerance
            && abs(sample.cameraX - checkpoint.cameraX) <= tolerance
            && abs(sample.cameraY - checkpoint.cameraY) <= tolerance
            && abs(sample.cameraZ - checkpoint.cameraZ) <= tolerance
            && abs(sample.cameraTargetX - checkpoint.cameraTargetX) <= tolerance
            && abs(sample.cameraTargetY - checkpoint.cameraTargetY) <= tolerance
            && abs(sample.cameraTargetZ - checkpoint.cameraTargetZ) <= tolerance
    }

    private func startRestoredPathPlayback(
        _ path: RecordedInputPath,
        iteration: Int
    ) -> Bool {
        let startedAt = ProcessInfo.processInfo.systemUptime
        let createdAt = Date()
        guard inputPathRecorder.start(
            at: startedAt,
            createdAt: createdAt,
            sourcePathID: path.id,
            replayIteration: iteration,
            startCheckpoint: path.startCheckpoint,
            visualArchiveRootURL: inputPathStore.rootURL
        ) else { return false }
        isPathRecording = true
        pathRecordingStartedAt = createdAt
        pathRecordingIssue = nil
        playbackDivergenceMonitor.beginEvaluation(at: startedAt)
        let accepted = gameControlForwarder.playInputPath(path) { [weak self] completed in
            self?.finishPathPlayback(completed: completed)
        }
        guard accepted else {
            inputPathRecorder.cancel()
            isPathRecording = false
            isPathPlaybackActive = false
            pathPlaybackIteration = nil
            pathRecordingStartedAt = nil
            pathRecordingIssue = "Path playback rejected by controls"
            playbackDivergenceMonitor.stop()
            restoreRenderFrameMarkerPolicy()
            return false
        }
        inputPathLog.notice(
            "playback started source=\(path.id.uuidString, privacy: .public) iteration=\(iteration, privacy: .public) duration=\(path.duration, privacy: .public)"
        )
        return true
    }

    private func finishPathPlayback(completed: Bool) {
        guard isPathPlaybackActive else { return }
        playbackDivergenceMonitor.stop()
        restoreRenderFrameMarkerPolicy()
        guard completed else {
            isPathPlaybackActive = false
            pathPlaybackIteration = nil
            pathRecordingIssue = pathPlaybackAbortIssue ?? "Path playback interrupted"
            pathPlaybackAbortIssue = nil
            stopPathRecording()
            return
        }
        pathPlaybackAbortIssue = nil
        // Keep recording briefly after final input so camera settling and the
        // terminal tracking result become part of this one-way run.
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            guard let self, self.isPathPlaybackActive else { return }
            self.isPathPlaybackActive = false
            self.pathPlaybackIteration = nil
            self.stopPathRecording()
        }
    }

    private func handlePlaybackDivergence(
        _ decision: PlaybackDivergenceDecision
    ) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.isPathPlaybackActive,
                  self.pathPlaybackAbortIssue == nil else { return }
            let error = Int(decision.errorPixels.rounded())
            let threshold = Int(decision.thresholdPixels.rounded())
            let issue: String
            switch decision.cause {
            case .poseError:
                issue = "Replay stopped: camera error \(error) px exceeded \(threshold) px in \(decision.sceneName)"
            case .visionStall:
                let milliseconds = Int(
                    ((decision.silenceSeconds ?? 0) * 1_000).rounded()
                )
                issue = "Replay stopped: Vision published no camera for \(milliseconds) ms while Hacker camera moved \(error) px in \(decision.sceneName)"
            }
            if ReplayDiagnosticOptions.continueAfterDivergence {
                self.inputPathLog.error("Diagnostic replay continuing after divergence: error=\(error, privacy: .public) threshold=\(threshold, privacy: .public) scene=\(decision.sceneName, privacy: .public) cause=\(decision.cause.rawValue, privacy: .public)")
                // Keep accepting frame pairs so the diagnostic trace retains
                // rendered frame IDs after the one-shot threshold report.
                return
            }
            self.pathPlaybackAbortIssue = issue
            self.inputPathLog.error("\(issue, privacy: .public) samples=\(decision.consecutiveSamples, privacy: .public)")
            self.playbackDivergenceMonitor.stop()
            self.gameControlForwarder.cancelPathPlayback()
        }
    }

    private func restoreRenderFrameMarkerPolicy() {
        gameControlForwarder.setRenderFrameMarkerEnabled(
            isHackerWorkspaceActive() || isPathRecording || RenderedFrameMarker.enabled
        )
    }

    @discardableResult
    func performAutomationCommand(_ command: GameAutomationCommand) -> Bool {
        gameControlForwarder.pulseAutomationButton(
            command.button,
            duration: command.duration
        )
    }

    func menuStencilAutomationReply(
        capture: Bool,
        expectedContextIdentifier: String? = nil,
        expectedSelectedIdentifier: String? = nil,
        languageIdentifier: String = HollowKnightMenuLanguage.legacyDefault.rawValue
    ) -> MenuStencilAutomationReply {
        let diagnosticSuffix = liveMenuStencilDiagnostic.map { "; \($0)" } ?? ""
        guard let contextIdentifier = expectedContextIdentifier
                ?? liveGameContextIdentifier,
              let context = LabelingContext(storageIdentifier: contextIdentifier),
              let scene = MenuStencilCatalog.humanLabeled?.scenes.first(where: {
                  $0.context == context
              }) else {
            return MenuStencilAutomationReply(
                ok: false,
                contextIdentifier: liveGameContextIdentifier,
                selectedIdentifier: nil,
                selectedName: liveSelectedMenuOption,
                capturePath: nil,
                message: "Waiting for a recognized menu and selected row"
                    + diagnosticSuffix
            )
        }
        let option: MenuStencilCatalog.Option?
        if let expectedSelectedIdentifier {
            option = scene.options.first {
                $0.classIdentifier == expectedSelectedIdentifier
            }
        } else if let selectedName = liveSelectedMenuOption {
            option = scene.options.first {
                $0.name.caseInsensitiveCompare(selectedName) == .orderedSame
            }
        } else {
            option = nil
        }
        guard let option else {
            return MenuStencilAutomationReply(
                ok: false,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: nil,
                selectedName: liveSelectedMenuOption,
                capturePath: nil,
                message: "Selected row is not part of the menu catalog"
                    + diagnosticSuffix
            )
        }
        guard capture else {
            return MenuStencilAutomationReply(
                ok: true,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: option.classIdentifier,
                selectedName: option.name,
                capturePath: nil,
                message: liveMenuStencilDiagnostic.map { "recognized; \($0)" }
                    ?? "recognized"
            )
        }
        capturedFrameStateLock.lock()
        let image = latestCleanGameFrame
        capturedFrameStateLock.unlock()
        guard let image else {
            return MenuStencilAutomationReply(
                ok: false,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: option.classIdentifier,
                selectedName: option.name,
                capturePath: nil,
                message: "No clean captured frame available"
            )
        }
        do {
            let saved = try MenuStencilLiveCaptureStore().save(
                image: image,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: option.classIdentifier,
                selectedName: option.name,
                languageIdentifier: languageIdentifier
            )
            return MenuStencilAutomationReply(
                ok: true,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: option.classIdentifier,
                selectedName: option.name,
                capturePath: saved.imageURL.path,
                message: "captured"
            )
        } catch {
            return MenuStencilAutomationReply(
                ok: false,
                contextIdentifier: contextIdentifier,
                selectedIdentifier: option.classIdentifier,
                selectedName: option.name,
                capturePath: nil,
                message: "Capture failed: \(error)"
            )
        }
    }

    func setSettingsDialogActive(_ active: Bool) {
        if active {
            botDemonstrations.stop(reason: "settings-opened")
            botPlayer.stop(reason: "Settings opened")
            botDemo.stop(reason: "Settings opened")
        }
        gameControlForwarder.setSettingsDialogActive(active)
    }

    func setModelReviewActive(_ active: Bool, reservesAllKeys: Bool = false) {
        if active {
            botPlayer.stop(reason: "Model review opened")
            botDemo.stop(reason: "Model review opened")
        }
        gameControlForwarder.setReviewNavigationActive(active, reservesAllKeys: reservesAllKeys)
    }

    func setObjectDetectionIconAtlas(_ snapshot: LabelingObjectAtlasSnapshot?) {
        let icons = snapshot.map { snapshot in
            Dictionary(uniqueKeysWithValues: snapshot.classIdentifiers.compactMap { identifier in
                snapshot.icon(for: identifier).map { (identifier, $0) }
            })
        } ?? [:]
        objectInferenceStateLock.lock()
        objectDetectionIcons = icons
        objectInferenceStateLock.unlock()
    }

    func refreshMenuStencilCatalog() {
        let examplesRootURL = LabelingExampleStore.defaultRootURL()
        let language = UserDefaults.standard.string(forKey: "HollowKnightVision.initialization.language.v1") ?? "en"
        menuStencilReloadQueue.async { [weak self] in
            let examples = (try? LabelingExampleStore().loadExamples()) ?? []
            let hud = HUDStencilTemplate.captured(examples: examples, languageIdentifier: language)
            // An empty/reset store has no scenes. Still replace the old catalog
            // and HUD tracker so previously captured pixels cannot remain active.
            let catalog = (try? MenuStencilCatalog(
                examplesRootURL: examplesRootURL,
                calibration: .loadDefault()
            )) ?? MenuStencilCatalog(scenes: [], referenceWidth: 640,
                                     referenceHeight: 360, pixelBand: 0..<360)
            self?.sampleQueue.async { [weak self] in
                self?.menuStencilWorker.replaceCatalog(catalog)
                self?.hudStencilTracker = HUDStencilTracker(template: hud)
                if hud == nil { self?.gameStatusTracker.reset() }
            }
            self?.hackerAtlasQueue.async { [weak self] in
                self?.hackerAtlasPipeline.replaceHUDTemplate(hud)
            }
        }
    }

    func forwardGamePointer(_ pointerEvent: GamePointerEvent) -> Bool {
        guard labelImage == nil else { return false }
        let accepted = gameControlForwarder.forwardPointer(pointerEvent)
        if QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: gameStatusTracker.current?.context,
            selectedOption: gameStatusTracker.current?.selectedMenuOption,
            event: pointerEvent.kind,
            accepted: accepted
        ) {
            confirmQuitGameExitWatch()
        }
        return accepted
    }

    private func handleQuitGameButton(
        _ button: RecordedGameButton,
        isPressed: Bool
    ) {
        guard QuitGameTerminationPolicy.shouldAwaitGameExit(
            context: gameStatusTracker.current?.context,
            selectedOption: gameStatusTracker.current?.selectedMenuOption,
            button: button,
            isPressed: isPressed,
            accepted: true
        ) else { return }
        confirmQuitGameExitWatch()
    }

    /// Track the selected installation across Steam's startup process handoff.
    private func armQuitGameExitWatch() {
        guard gameLostCountdown == nil, !quitGameExitWatchActive else { return }
        quitGameExitWatchActive = true
        quitGameExitWatchGeneration &+= 1
        pollForConfirmedGameExit(generation: quitGameExitWatchGeneration)
    }

    private func gameApplicationMatchesSelection(
        _ application: NSRunningApplication
    ) -> Bool {
        guard !application.isTerminated,
              let gameURL = application.bundleURL?.resolvingSymlinksInPath().standardizedFileURL
        else { return false }
        if let expectedGameApplicationURL, gameURL != expectedGameApplicationURL {
            return false
        }
        return HollowKnightInstallationPolicy.issue(gameURL: gameURL, mode: gameMode) == nil
    }

    private func confirmQuitGameExitWatch() {
        armQuitGameExitWatch()
        guard quitGameExitApplication != nil else {
            quitGameExitLog.error("confirmation observed without a game process")
            return
        }
        quitGameExitConfirmationObserved = true
        // Unity can remain registered with Launch Services while unwinding.
        // Keep the process-specific observer alive substantially longer than
        // the old ten-second polling window.
        quitGameExitDeadline = ProcessInfo.processInfo.systemUptime + 60
        quitGameExitLog.notice("confirmation observed")
    }

    private func updateQuitGameExitWatch(for status: LiveGameStatus?) {
        armQuitGameExitWatch()
    }

    private func handleTerminatedApplication(_ notification: Notification) {
        guard let watched = quitGameExitApplication,
              let terminated = notification.userInfo?[NSWorkspace.applicationUserInfoKey]
                as? NSRunningApplication,
              terminated.processIdentifier == watched.processIdentifier
        else { return }
        quitGameExitLog.notice(
            "process terminated pid=\(terminated.processIdentifier, privacy: .public)"
        )
        // Launch Services can still list this PID while delivering its exit.
        observeGameProcess(excluding: terminated.processIdentifier)
    }

    private func pollForConfirmedGameExit(generation: UInt64) {
        guard generation == quitGameExitWatchGeneration, quitGameExitWatchActive else { return }
        observeGameProcess()
        guard generation == quitGameExitWatchGeneration, quitGameExitWatchActive else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
            self?.pollForConfirmedGameExit(generation: generation)
        }
    }

    private func observeGameProcess(excluding terminatedPID: pid_t? = nil) {
        guard gameLostCountdown == nil, quitGameExitWatchActive else { return }
        let now = ProcessInfo.processInfo.systemUptime
        if let deadline = quitGameExitDeadline, now >= deadline {
            quitGameExitLog.error("confirmation timed out while game remained running")
            quitGameExitConfirmationObserved = false
            quitGameExitDeadline = nil
        }
        let application = NSWorkspace.shared.runningApplications.first {
            $0.processIdentifier != terminatedPID && gameApplicationMatchesSelection($0)
        }
        let previousPID = quitGameExitApplication?.processIdentifier
        let previousObservation = gameProcessLifetime.observation
        let observation = gameProcessLifetime.observe(
            processID: application?.processIdentifier,
            captureReady: isCapturing && capturedGameProcessID == application?.processIdentifier,
            quitConfirmed: quitGameExitConfirmationObserved,
            at: now
        )
        quitGameExitApplication = application
        if previousPID != application?.processIdentifier {
            if let previousPID { restartCaptureAfterGameExit(processID: previousPID) }
            if let application {
                quitGameExitLog.notice("watching selected game pid=\(application.processIdentifier, privacy: .public)")
            }
        }
        if observation == .exited {
            beginGameLostCountdown()
        } else if observation == .waitingForRelaunch, previousObservation != observation {
            quitGameExitLog.notice("startup process exited; waiting for Steam replacement")
        }
    }

    private func restartCaptureAfterGameExit(processID: pid_t) {
        guard capturedGameProcessID == processID, let oldStream = stream else { return }
        // Retire capture of the bootstrap window even if ScreenCaptureKit has
        // not delivered its stop callback yet. Keep wantsCapture set throughout.
        let oldStreamID = ObjectIdentifier(oldStream)
        Task { [weak self] in
            guard let self,
                  await self.invalidateCaptureAttempt(oldStreamID) else { return }
            do { try await oldStream.stopCapture() } catch { }
            await MainActor.run {
                guard self.stream === oldStream else { return }
                self.stream = nil
                self.capturedGameProcessID = nil
                self.isCapturing = false
                self.operationInFlight = false
                if self.wantsCapture { self.scheduleCaptureRestart() }
            }
        }
    }

    private func beginGameLostCountdown() {
        guard gameLostCountdown == nil else { return }
        quitGameExitWatchGeneration &+= 1
        quitGameExitWatchActive = false
        wantsCapture = false
        gameControlForwarder.stop()
        stopCapture()
        publishGameLostCountdown(GameLossCountdown.initialSeconds)
    }

    private func publishGameLostCountdown(_ remainingSeconds: Int) {
        gameLostCountdown = remainingSeconds
        let delay = remainingSeconds == 0 ? 0.5 : 1
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self,
                  self.gameLostCountdown == remainingSeconds else { return }
            if remainingSeconds == 0 {
                NSApp.terminate(nil)
            } else {
                self.publishGameLostCountdown(remainingSeconds - 1)
            }
        }
    }

    var canToggleLabelMode: Bool {
        guard labelPauseState == .ready || labelPauseState == .paused else {
            return false
        }
        if labelPauseState == .paused { return true }
        capturedFrameStateLock.lock()
        defer { capturedFrameStateLock.unlock() }
        return latestCleanGameFrame != nil
    }

    var canUseHacker: Bool {
        gameMode == .modded
            && receiverCapabilities.contains(ReceiverControlProtocol.groundTruthCapability)
    }

    var canUseOps: Bool {
        gameMode == .modded
            && receiverCapabilities.contains(ReceiverControlProtocol.playerOpsCapability)
    }

    var isLabelModeActive: Bool {
        labelPauseState == .pausing || labelPauseState == .paused
    }

    private func recordGroundTruthCameraSample(
        _ sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval
    ) {
        groundTruthCameraStateLock.lock()
        let prior = latestGroundTruthCameraSample?.0
        latestGroundTruthCameraSample = (sample, observedAt)
        groundTruthCameraStateLock.unlock()
        // Menus can stop producing camera matches while receiver state is
        // still arriving. Apply that state independently of the camera join.
        if let atlasGameplayActive = sample.atlasGameplayActive,
           prior?.sessionID != sample.sessionID || prior?.atlasGameplayActive != atlasGameplayActive {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.canUseHacker else { return }
                self.updateAtlasPathEvidence { $0.observeReceiver(gameplay: atlasGameplayActive, at: observedAt) }
            }
        }
        if let prior, prior.sessionID != sample.sessionID || prior.sceneName != sample.sceneName
            || (prior.heroAvailable && !sample.heroAvailable)
            || ((prior.actors?.heroHealth ?? 0) > 0 && sample.actors?.heroHealth == 0) {
            DispatchQueue.main.async { [weak self] in self?.resetLiveBlockades(at: observedAt) }
        }
        let replayFrameSize = CGSize(
            width: CGFloat(HollowKnightCaptureConfiguration.outputWidth),
            height: CGFloat(HollowKnightCaptureConfiguration.outputWidth)
                / HollowKnightCaptureConfiguration.gameplayAspectRatio
        )
        if let decision = playbackDivergenceMonitor.appendGroundTruth(
            sample,
            observedAt: observedAt,
            frameSize: replayFrameSize
        ) {
            handlePlaybackDivergence(decision)
        }
        guard canUseHacker || inputPathRecorder.isRecording || botEvaluation.isRecording,
              let match = hackerFrameSynchronizer.append(
                sample: sample,
                observedAt: observedAt
              ) else { return }
        enqueueHackerMatch(match)
    }

    func setHackerWorkspaceActive(_ active: Bool) {
        if !active { botHumanGround.clearCameras() }
        if !active { botHackerObservations.reset() }
        if !active { botPlayer.stop(reason: "Left Hacker workspace") }
        hackerWorkspaceWanted = active
        guard !active || canUseHacker else { return }
        if active && !hackerAtlasRestored {
            hackerAtlasRestored = true
            if FileManager.default.fileExists(atPath: self.hackerAtlasActiveURL.path) || atlasAutoSave.profile != nil {
                performHackerAtlasManagement(replacingActive: true, activateAfter: true) { [self] _ in
                    let disk = self.hackerAtlasActiveURL
                    let existing = FileManager.default.fileExists(atPath: disk.path)
                        ? try HackerAtlasArchiveStore.read(from: disk) : .empty
                    // The application may have closed between the two autosave writes.
                    if let root = atlasPipeline.activeAutoSaveRootURL, let profile = try SaveProfileAtlas.read(from: root) {
                        return try existing.switching(to: profile, library: hackerAtlasStateStore) ?? existing
                    }
                    return existing
                }
                return
            }
        }
        hackerStateLock.lock()
        guard hackerWorkspaceActive != active else {
            hackerStateLock.unlock()
            return
        }
        hackerWorkspaceActive = false
        hackerGeneration &+= 1
        let generation = hackerGeneration
        hackerStateLock.unlock()
        hackerFrameSynchronizer.reset()
        hackerPresentationPump.invalidate(epoch: generation)
        hackerAtlasPump.invalidate(epoch: generation)
        // Drain old work before reopening capture admission. Room anchors and
        // atlas compositors deliberately survive a workspace switch.
        hackerPresentationQueue.sync {}
        hackerAtlasQueue.sync {}
        if active {
            hackerStateLock.lock()
            if hackerGeneration == generation { hackerWorkspaceActive = true }
            hackerStateLock.unlock()
            hackerPresentedFrameCount = 0
            hackerPresentationWindowStartedAt = nil
            hackerPresentationWindowCount = 0
        }
        gameControlForwarder.setRenderFrameMarkerEnabled(
            active || isPathRecording || isPathPlaybackActive || isPathCheckpointPending
                || RenderedFrameMarker.enabled
        )
        hackerAtlasLog.notice("workspace active=\(active, privacy: .public)")
    }

    func deleteHackerAtlas() {
        let reactivate = isHackerWorkspaceActive()
        resetHackerAtlasWorkers(reactivate: reactivate)
        hackerAtlasTiles = []
        hackerAtlasRooms = []
        hackerPaths.reset()
        hackerRoomConnections = []
        hackerRoomPositionOverrides = [:]
        hackerRoomLayout = .empty
        hackerAtlasBounds = nil
        hackerLiveImage = nil
        hackerLiveBounds = .zero
        hackerMapFocus = .zero
        hackerCameraTransform = nil
        hackerAtlasLog.notice("atlas deleted")
    }

    private func resetHackerAtlasWorkers(reactivate: Bool) {
        hackerStateLock.lock()
        // Keep capture admission closed while both serial workers discard the
        // previous session. This prevents the first frame of a reopened scene
        // from racing the reset and inheriting the old atlas anchor.
        hackerWorkspaceActive = false
        hackerGeneration &+= 1
        let generation = hackerGeneration
        hackerStateLock.unlock()
        hackerFrameSynchronizer.reset()
        hackerPresentationPump.invalidate(epoch: generation)
        hackerAtlasPump.invalidate(epoch: generation)
        hackerPresentationQueue.sync {
            hackerFramePreparer.reset()
        }
        hackerAtlasQueue.sync {
            hackerAtlasPipeline.reset()
        }
        if reactivate {
            hackerStateLock.lock()
            if hackerGeneration == generation { hackerWorkspaceActive = true }
            hackerStateLock.unlock()
        }
        if reactivate {
            hackerPresentedFrameCount = 0
            hackerAtlasFrameCount = 0
            hackerPresentationWindowStartedAt = nil
            hackerPresentationWindowCount = 0
        }
    }

    private func isHackerWorkspaceActive() -> Bool {
        hackerStateLock.lock(); defer { hackerStateLock.unlock() }
        return hackerWorkspaceActive
    }

    func moveHackerRoom(_ roomID: UUID, by translation: CGVector) {
        guard translation.dx.isFinite, translation.dy.isFinite,
              abs(translation.dx) > 0.000_1 || abs(translation.dy) > 0.000_1,
              let room = hackerAtlasRooms.first(where: { $0.id == roomID })
        else { return }
        let position = hackerRoomPosition(room)
        hackerRoomPositionOverrides[roomID] = CGPoint(
            x: position.x + translation.dx, y: position.y + translation.dy)
        if hackerCameraTransform?.sceneName == room.sceneName {
            hackerLiveBounds = hackerLiveBounds.offsetBy(
                dx: translation.dx,
                dy: translation.dy
            )
            hackerMapFocus.x += translation.dx
            hackerMapFocus.y += translation.dy
        }
        updateHackerAtlasBounds()
        scheduleHackerAutoSave()
    }

    func hackerRoomPosition(_ room: HackerAtlasRoom) -> CGPoint {
        hackerRoomPositionOverrides[room.id] ?? hackerRoomLayout.positions[room.id] ?? room.position
    }

    var hackerRoomConnectorSegments: [HackerRoomConnectorSegment] {
        let rooms = Dictionary(uniqueKeysWithValues: hackerAtlasRooms.map { ($0.id, $0) })
        return hackerRoomConnections.compactMap { connection in
            guard let from = rooms[connection.fromRoomID],
                  let to = rooms[connection.toRoomID] else { return nil }
            return HackerRoomConnectorSegment(
                id: connection.id,
                start: hackerRoomConnectionPoint(
                    room: from,
                    position: hackerRoomPosition(from),
                    side: connection.fromSide,
                    coordinate: connection.fromCoordinate
                ),
                end: hackerRoomConnectionPoint(
                    room: to,
                    position: hackerRoomPosition(to),
                    side: connection.toSide,
                    coordinate: connection.toCoordinate
                ),
                isFreeform: hackerRoomLayout.freeformConnectionIDs.contains(connection.id)
            )
        }
    }

    private func hackerRoomConnectionPoint(
        room: HackerAtlasRoom,
        position: CGPoint,
        side: HackerRoomSide,
        coordinate: CGFloat
    ) -> CGPoint {
        let bounds = room.bounds.offsetBy(dx: position.x, dy: position.y)
        if side.isHorizontal {
            return CGPoint(
                x: side == .left ? bounds.minX : bounds.maxX,
                y: position.y + min(max(coordinate, room.bounds.minY), room.bounds.maxY)
            )
        }
        return CGPoint(
            x: position.x + min(max(coordinate, room.bounds.minX), room.bounds.maxX),
            y: side == .bottom ? bounds.minY : bounds.maxY
        )
    }

    private func updateHackerAtlasBounds() {
        let bounds = hackerAtlasRooms.reduce(CGRect.null) { partial, room in
            let position = hackerRoomPosition(room)
            return partial.union(room.bounds.offsetBy(dx: position.x, dy: position.y))
        }
        hackerAtlasBounds = bounds.isNull || bounds.isEmpty ? nil : bounds
    }

    private func recordHackerCapture(
        _ image: CGImage,
        unityFrame: Int,
        capturedAt: TimeInterval,
        integratesAtlas: Bool,
        captureGeneration: UInt64,
        pathState: HackerPathFrameState
    ) {
        guard canUseHacker || inputPathRecorder.isRecording || botEvaluation.isRecording else { return }
        let capture = HackerCapturedFrame(
            unityFrame: unityFrame,
            image: image,
            capturedAt: capturedAt,
            integratesAtlas: integratesAtlas,
            captureGeneration: captureGeneration, horizontalInput: gameControlForwarder.currentHorizontalRoomDirection()?.rawValue ?? 0,
            pathState: pathState
        )
        if let match = hackerFrameSynchronizer.append(capture: capture) {
            enqueueHackerMatch(match)
        }
    }

    private func enqueueHackerMatch(_ match: HackerMatchedFrame) {
        sharedPathMatchLock.lock()
        sharedPathMatches.append(match)
        if sharedPathMatches.count > 60 { sharedPathMatches.removeFirst(sharedPathMatches.count - 60) }
        sharedPathMatchLock.unlock()
        hackerStateLock.lock()
        let generation = hackerGeneration
        let active = hackerWorkspaceActive
        hackerStateLock.unlock()
        if let transform = GroundTruthCameraTransform(sample: match.sample, observedAt: match.sampleObservedAt,
            frameSize: CGSize(width: match.capture.image.width, height: match.capture.image.height)) {
            botHumanGround.record(transform: transform, timestamp: match.capture.capturedAt,
                generation: match.capture.captureGeneration)
            let pairedRun = botDemonstrations.pairedRecordingID
            if active || pairedRun != nil, botHackerObservations.beginSample(at: match.capture.capturedAt, epoch: generation) {
                hackerBotQueue.async { [self] in
                    defer { botHackerObservations.endSample() }
                    guard isHackerGenerationCurrent(generation, requireActive: pairedRun == nil),
                          botDemonstrations.pairedRecordingID == pairedRun,
                          let frame = botHackerObservations.makeFrame(match, now: ProcessInfo.processInfo.systemUptime)
                    else { return }
                    publishBotObservation(frame.observation, image: frame.image, hackerEpoch: generation, hackerCamera: transform, pairedRun: pairedRun, health: match.sample.actors?.heroHealth, grounded: match.sample.grounded)
                }
            }
        }
        guard active else { return }
        guard let ticket = hackerPresentationPump.submit(match, epoch: generation) else { return }
        hackerPresentationQueue.async { [weak self] in
            self?.processHackerPresentation(ticket)
        }
    }

    private func processHackerPresentation(
        _ ticket: LatestFramePump<HackerMatchedFrame>.Ticket
    ) {
        let prepared = isHackerGenerationCurrent(ticket.epoch)
            ? hackerFramePreparer.process(ticket.item) : nil
        let completion = hackerPresentationPump.complete(ticket)
        if completion.shouldAcceptResult,
           isHackerGenerationCurrent(ticket.epoch),
           let prepared {
            // Atlas composition always receives the clean capture. Detection
            // boxes and icons belong only to Hacker's current live preview.
            enqueueHackerAtlas(prepared, generation: ticket.epoch)
            var liveImage = prepared.liveImage
            if objectDetectionEnabled {
                let generation = ticket.item.capture.captureGeneration
                let tracked = currentObjectDetections(captureGeneration: generation,
                    timestamp: prepared.capturedAt)
                let detections = currentObjectDetectionOverlay(captureGeneration: generation,
                    timestamp: prepared.capturedAt, tracked: tracked)
                if !detections.isEmpty {
                    liveImage = FrameRegionRenderer.annotatedFrame(from: liveImage,
                        regions: .startup(in: prepared.liveBounds),
                        objectDetections: detections, objectIcons: currentObjectDetectionIcons(),
                        context: poseImageContext) ?? liveImage
                }
            }
            let presentedImage = liveImage
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isHackerGenerationCurrent(ticket.epoch),
                      ticket.item.capture.captureGeneration == self.currentStatusGeneration else { return }
                let offset = self.hackerAtlasRooms.first {
                    $0.sceneName == prepared.sceneName
                }.map { room -> CGPoint in
                    let position = self.hackerRoomPosition(room)
                    return CGPoint(
                        x: position.x - room.position.x,
                        y: position.y - room.position.y
                    )
                } ?? .zero
                LivePresentationTiming.shared.register(
                    presentedImage, capturedAt: prepared.capturedAt
                )
                self.atlasPathFrameEvidence.presentHackerFrame(at: prepared.capturedAt,
                    gameplay: ticket.item.capture.pathState.admits(receiverGameplay: prepared.sample.atlasGameplayActive))
                self.hackerLiveImage = presentedImage
                self.hackerLiveBounds = prepared.liveBounds.offsetBy(
                    dx: offset.x,
                    dy: offset.y
                )
                self.hackerMapFocus = CGPoint(
                    x: prepared.focusPoint.x + offset.x,
                    y: prepared.focusPoint.y + offset.y
                )
                self.hackerCameraTransform = prepared.cameraTransform
                self.recordHackerPresentationRate(prepared)
            }
        }
        if let next = completion.next {
            hackerPresentationQueue.async { [weak self] in
                self?.processHackerPresentation(next)
            }
        }
    }

    private func enqueueHackerAtlas(_ prepared: HackerPreparedFrame, generation: UInt64) {
        guard let ticket = hackerAtlasPump.submit(prepared, epoch: generation) else {
            return
        }
        hackerAtlasQueue.async { [weak self] in
            self?.processHackerAtlas(ticket)
        }
    }

    private func processHackerAtlas(
        _ ticket: LatestFramePump<HackerPreparedFrame>.Ticket
    ) {
        let output = isHackerGenerationCurrent(ticket.epoch) && !profileAtlasGate.withLock({ $0 })
            ? hackerAtlasPipeline.process(ticket.item) : nil
        let completion = hackerAtlasPump.complete(ticket)
        if completion.shouldAcceptResult,
           isHackerGenerationCurrent(ticket.epoch),
           let output {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isHackerGenerationCurrent(ticket.epoch) else { return }
                let oldActivePosition = self.hackerAtlasRooms.first {
                    $0.sceneName == self.hackerCameraTransform?.sceneName
                }.map(self.hackerRoomPosition)
                self.hackerAtlasTiles = output.atlasTiles
                self.hackerAtlasRooms = output.rooms
                self.refreshHackerPaths()
                self.hackerRoomConnections = output.connections
                self.hackerRoomLayout = HackerRoomLayout.make(
                    rooms: output.rooms,
                    connections: output.connections,
                    floorAlignedConnectionIDs: output.floorAlignedConnectionIDs
                )
                let validRoomIDs = Set(output.rooms.map(\.id))
                self.hackerRoomPositionOverrides = self.hackerRoomPositionOverrides.filter {
                    validRoomIDs.contains($0.key)
                }
                if let oldActivePosition,
                   let activeRoom = output.rooms.first(where: {
                       $0.sceneName == self.hackerCameraTransform?.sceneName
                   }) {
                    let newActivePosition = self.hackerRoomPosition(activeRoom)
                    let delta = CGPoint(
                        x: newActivePosition.x - oldActivePosition.x,
                        y: newActivePosition.y - oldActivePosition.y
                    )
                    self.hackerLiveBounds = self.hackerLiveBounds.offsetBy(
                        dx: delta.x, dy: delta.y
                    )
                    self.hackerMapFocus.x += delta.x
                    self.hackerMapFocus.y += delta.y
                }
                self.updateHackerAtlasBounds()
                self.scheduleHackerAutoSave()
                self.hackerAtlasFrameCount &+= 1
                if self.hackerAtlasFrameCount == 1
                    || self.hackerAtlasFrameCount.isMultiple(of: 60) {
                    self.hackerAtlasLog.notice(
                        "atlas count=\(self.hackerAtlasFrameCount, privacy: .public) frame=\(output.unityFrame, privacy: .public) scene=\(output.sceneName, privacy: .public) rooms=\(output.roomCount, privacy: .public) tiles=\(output.atlasTiles.count, privacy: .public)"
                    )
                }
            }
        }
        if let next = completion.next {
            hackerAtlasQueue.async { [weak self] in
                self?.processHackerAtlas(next)
            }
        }
    }

    private func recordHackerPresentationRate(_ prepared: HackerPreparedFrame) {
        let now = ProcessInfo.processInfo.systemUptime
        hackerPresentedFrameCount &+= 1
        hackerPresentationWindowCount &+= 1
        guard let startedAt = hackerPresentationWindowStartedAt else {
            hackerPresentationWindowStartedAt = now
            hackerAtlasLog.notice(
                "presentation count=1 frame=\(prepared.unityFrame, privacy: .public) scene=\(prepared.sceneName, privacy: .public)"
            )
            return
        }
        let elapsed = now - startedAt
        guard elapsed >= 5 else { return }
        let fps = Double(hackerPresentationWindowCount - 1) / elapsed
        let fpsText = String(format: "%.1f", fps)
        hackerAtlasLog.notice(
            "presentation fps=\(fpsText, privacy: .public) total=\(self.hackerPresentedFrameCount, privacy: .public) atlas=\(self.hackerAtlasFrameCount, privacy: .public) frame=\(prepared.unityFrame, privacy: .public)"
        )
        hackerPresentationWindowStartedAt = now
        hackerPresentationWindowCount = 1
    }

    private func isHackerGenerationCurrent(_ generation: UInt64, requireActive: Bool = true) -> Bool {
        hackerStateLock.lock(); defer { hackerStateLock.unlock() }
        return hackerGeneration == generation && (!requireActive || hackerWorkspaceActive)
    }

    func toggleLabelMode() {
        if gameMode == .normal {
            switch labelPauseState {
            case .ready: applyLabelPauseState(.paused)
            case .paused: applyLabelPauseState(.ready)
            case .unavailable, .pausing, .resuming: break
            }
            return
        }
        switch labelPauseState {
        case .ready:
            gameControlForwarder.setLabelPauseActive(true)
        case .paused:
            gameControlForwarder.setLabelPauseActive(false)
        case .unavailable, .pausing, .resuming:
            break
        }
    }

    func groundLabelCapture() -> GroundLabelCapture? {
        capturedFrameStateLock.lock()
        defer { capturedFrameStateLock.unlock() }
        guard let captured = latestGroundLabelCapture,
              ProcessInfo.processInfo.systemUptime - captured.timestamp < 2 else { return nil }
        return captured
    }

    func releaseControlsForGroundLabeling() {
        botPlayer.stop(reason: "Ground or room editing")
        botDemo.stop(reason: "Ground or room editing")
        botDemonstrations.stop(reason: "ground-or-room-editing")
        gameControlForwarder.releaseForGroundLabeling()
    }

    func thinkingScreenshot() -> CGImage? {
        guard isCapturing else { return nil }
        return capturedFrameStateLock.withLock { latestCleanGameFrame }
    }

    func recentCapturedFrames() -> [RecentCapturedFrame] {
        capturedFrameStateLock.lock()
        defer { capturedFrameStateLock.unlock() }
        return recentCapturedFrameBuffer.frames
    }

    private func applyLabelPauseState(_ state: GamePauseState) {
        if state != .ready {
            botDemonstrations.stop(reason: "gameplay-paused")
            botPlayer.stop(reason: "Gameplay paused")
            botDemo.stop(reason: "Gameplay paused")
        }
        let completedLabelSession = state == .ready
            && (labelPauseState == .paused || labelPauseState == .resuming)
        labelPauseState = state
        gameControlForwarder.setNativeLabelingActive(state != .ready)
        switch state {
        case .pausing, .paused, .resuming:
            setObjectInferenceSuspended(true)
        case .ready, .unavailable:
            setObjectInferenceSuspended(false)
        }
        switch state {
        case .paused:
            capturedFrameStateLock.lock()
            let cleanFrame = latestCleanGameFrame
            capturedFrameStateLock.unlock()
            if labelImage == nil { labelImage = cleanFrame }
        case .ready, .unavailable:
            labelImage = nil
        case .pausing, .resuming:
            break
        }
        capturedFrameStateLock.lock()
        captureProcessingPaused = state == .paused && labelImage != nil
        capturedFrameStateLock.unlock()
        if completedLabelSession && randomizePlayerStateAfterLabel {
            randomizePlayerTestState()
        }
    }

    func positionWindows() {
        guard let viewer = NSApp.windows.first(where: {
            ($0 is VisionPanel || $0 is NormalVisionPanel) && $0.isVisible
        }), let screen = viewer.screen ?? NSScreen.main,
           let primary = NSScreen.screens.first else { return }
        tileGameWindow(
            frame: DashboardWindowLayout.accessibilityFrame(for: screen.visibleFrame, primaryScreen: primary.frame),
            viewer: viewer
        )
    }

    private func tileGameWindow(frame: CGRect, viewer: NSWindow) {
        guard let game = GameControlForwarder.hollowKnightApplication() else { return }
        if AXIsProcessTrusted() {
            let application = AXUIElementCreateApplication(game.processIdentifier)
            var windowsValue: CFTypeRef?
            if AXUIElementCopyAttributeValue(
                application,
                kAXWindowsAttribute as CFString,
                &windowsValue
            ) == .success,
            let windows = windowsValue as? [AXUIElement],
                let window = preferredGameWindow(in: windows) {
                var position = frame.origin
                var size = frame.size
                if let positionValue = AXValueCreate(.cgPoint, &position) {
                    _ = AXUIElementSetAttributeValue(
                        window,
                        kAXPositionAttribute as CFString,
                        positionValue
                    )
                }
                if let sizeValue = AXValueCreate(.cgSize, &size) {
                    let sizeResult = AXUIElementSetAttributeValue(
                        window,
                        kAXSizeAttribute as CFString,
                        sizeValue
                    )
                    _ = sizeResult
                }
            }
        }
        viewer.makeKeyAndOrderFront(nil)
        if gameMode == .normal {
            gameControlForwarder.prepareNativeInput()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
    }

    /// Unity exposes a tiny helper window before its gameplay window. Choose
    /// the titled gameplay surface when present, then fall back to max area.
    private func preferredGameWindow(in windows: [AXUIElement]) -> AXUIElement? {
        let candidates = windows.map { window in
            GameWindowCandidate(
                title: accessibilityTitle(of: window),
                size: accessibilitySize(of: window)
            )
        }
        guard let index = GameWindowSelection.bestIndex(in: candidates) else { return nil }
        return windows[index]
    }

    private func accessibilityTitle(of element: AXUIElement) -> String {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXTitleAttribute as CFString,
            &value
        ) == .success else { return "" }
        return value as? String ?? ""
    }

    private func accessibilitySize(of element: AXUIElement) -> CGSize {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXSizeAttribute as CFString,
            &value
        ) == .success,
        let value,
        CFGetTypeID(value) == AXValueGetTypeID() else { return .zero }
        var size = CGSize.zero
        guard AXValueGetValue(value as! AXValue, .cgSize, &size) else { return .zero }
        return size
    }

    private func bestHollowKnightWindow(in windows: [SCWindow]) -> SCWindow? {
        windows
            .filter { window in
                guard window.owningApplication?.processID != ProcessInfo.processInfo.processIdentifier else {
                    return false
                }
                return SourceWindowIdentity.isCaptureWindow(
                    applicationName: window.owningApplication?.applicationName,
                    bundleIdentifier: window.owningApplication?.bundleIdentifier,
                    windowLayer: window.windowLayer,
                    windowSize: window.frame.size
                )
            }
            .filter { captureCompatibilityIssue(for: $0) == nil }
            .max { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }
    }

    private func runningGameCompatibilityIssue(in windows: [SCWindow]) -> String? {
        windows.lazy
            .filter { window in
                SourceWindowIdentity.isCaptureWindow(
                    applicationName: window.owningApplication?.applicationName,
                    bundleIdentifier: window.owningApplication?.bundleIdentifier,
                    windowLayer: window.windowLayer,
                    windowSize: window.frame.size
                )
            }
            .compactMap(captureCompatibilityIssue(for:))
            .first
    }

    private func captureCompatibilityIssue(for window: SCWindow) -> String? {
        guard let processID = window.owningApplication?.processID,
              let application = NSRunningApplication(processIdentifier: processID),
              let gameURL = application.bundleURL else {
            return "Vision could not verify whether the running Hollow Knight installation matches \(gameMode.title). Close it and launch the intended copy with --game-path."
        }
        if let expectedGameApplicationURL,
           gameURL.resolvingSymlinksInPath().standardizedFileURL != expectedGameApplicationURL {
            return "Waiting for the selected Hollow Knight installation to launch."
        }
        return HollowKnightInstallationPolicy.issue(gameURL: gameURL, mode: gameMode)?
            .runningApplicationMessage
    }

    private func prepareCaptureAttempt(_ streamID: ObjectIdentifier) async {
        await withCheckedContinuation { continuation in
            renderQueue.async { [weak self] in
                guard let self else { continuation.resume(); return }
                self.activeCaptureStreamID = streamID
                self.advanceGeneration(resetCamera: false)
                continuation.resume()
            }
        }
    }

    private func installAcceptedSampleStream(_ streamID: ObjectIdentifier) async {
        await withCheckedContinuation { continuation in
            sampleQueue.async { [weak self] in
                self?.capturedFrameStateLock.withLock { self?.latestCleanGameFrame = nil }
                self?.acceptedSampleStreamID = streamID
                continuation.resume()
            }
        }
    }

    private func invalidateCaptureAttempt(_ streamID: ObjectIdentifier) async -> Bool {
        let ownsPipeline = await withCheckedContinuation { continuation in
            renderQueue.async { [weak self] in
                guard let self, self.activeCaptureStreamID == streamID else {
                    continuation.resume(returning: false)
                    return
                }
                self.activeCaptureStreamID = nil
                self.advanceGeneration(resetCamera: false)
                continuation.resume(returning: true)
            }
        }
        guard ownsPipeline else { return false }
        await withCheckedContinuation { continuation in
            sampleQueue.async { [weak self] in
                guard let self else { continuation.resume(); return }
                if self.acceptedSampleStreamID == streamID {
                    self.acceptedSampleStreamID = nil
                }
                continuation.resume()
            }
        }
        return true
    }

    /// Drop stale render work before resetting sample-queue tracking.
    private func advanceGeneration(resetCamera: Bool, preservingMenuNavigation: Bool = false) {
        visionStateLock.lock()
        defer { visionStateLock.unlock() }
        renderGeneration &+= 1
        let generation = renderGeneration
        framePump.invalidate(epoch: generation)
        visionFramePump.invalidate(epoch: generation)
        atlasPipeline.beginCaptureGeneration()
        invalidateObjectInferenceResults()
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.skillReading = nil
            self.thinkingScreens.lostState()
            self.latestSkillFrame = nil
            self.currentStatusGeneration = generation
            self.updateAtlasPathEvidence { $0.resetVisualFrames() }
            self.resetLiveBlockades()
            self.botDemonstrations.stop(reason: "capture-generation-changed")
            self.botPlayer.stop(reason: "Capture generation changed")
            if !preservingMenuNavigation && !self.botDemo.isHandingOff { self.botDemo.stop(reason: "Capture generation changed") }
            self.gameStatusTracker.reset(preservingGameplay: true)
            self.liveGameStatus = self.gameStatusTracker.current?.displayText
            self.liveGameContextIdentifier = self.gameStatusTracker.current?
                .context.labelingContext?.storageIdentifier
            self.liveSelectedMenuOption = self.gameStatusTracker.current?.selectedMenuOption
            self.hasGameplayStarted = self.gameStatusTracker.current?.context.isGameplay == true
        }
        // Invalidate correction delivery before the asynchronous vision reset
        // is queued. A worker may finish while this method is running.
        featureRefinement.invalidate(before: generation)
        featureCorrectionPolicy.beginGeneration(generation)
        let retainedBasis = resetCamera ? CGPoint.zero : worldBasis.snapshot.worldFromLocal
        let basisGeneration = worldBasis.beginCapture(worldFromLocal: retainedBasis)
        precondition(basisGeneration == generation)
        if !resetCamera, atlasPipeline.hasCommittedWorldEvidence {
            worldBasis.loseLocalTracking()
            DispatchQueue.main.async { [weak self] in self?.localizationStatus = "Recovering" }
        }
        trackingStateLock.lock()
        trackingState = TrackingState(
            cameraPosition: resetCamera ? .zero : trackingState.cameraPosition,
            regions: nil,
            poseTimestamp: nil,
            basisTicket: nil,
            groundAnchored: nil,
            gameplayEnabled: false,
            generation: generation
        )
        trackingStateLock.unlock()
        visionQueue.async { [weak self] in
            guard let self else { return }
            let currentWorld = self.atlasPipeline.currentWorldSnapshot()
            self.trackingIslandCoordinator.reset(nextID:
                (currentWorld.trackingIslands.map(\.id).max() ?? -1) + 1)
            self.trackingIslandCoordinator.applyWorldSnapshot(currentWorld)
            self.walkedGroundMemory = WalkedGroundMemory(snapshot: currentWorld)
            self.knightContactTracker = .init()
            self.trackingStateLock.lock()
            self.trackingIslandProjection = LiveIslandWorldProjection(snapshot: currentWorld)
            self.trackingStateLock.unlock()
            self.handledRoomRevision = 0
            if resetCamera { self.accumulator.reset() }
            // Creating a new atlas changes the coordinate epoch, not the game
            // screen. Preserve an established gameplay gate; trained title or
            // profile evidence still closes it in observe(_:).
            self.gameStartupCoordinator.reset(preservingGameplay: true)
            self.registrationContinuity.reset()
            self.freshAtlasBootstrapGate.reset()
            self.featureRefinement.submitReset { [weak self] in
                guard let self else { return }
                if resetCamera {
                    self.persistentFeatureTracker.reset()
                } else {
                    self.persistentFeatureTracker.resetTracking(keepingLandmarks: true)
                }
            }
            if resetCamera {
                self.groundReferenceTracker.reset()
            }
            self.groundHypothesisTracker.reset(
                keepingGlobalFeatures: !resetCamera
            )
            if resetCamera { self.semanticGroundAtlas.reset() }
            self.transitionMotionBridge.reset()
            self.calibratedSceneMotion.reset()
            self.visualFallMotion.reset()
            self.groundPlacementRecovery.reset()
            self.pathPlaybackGroundReadiness = .empty
        }
        sampleQueue.async { [weak self] in
            guard let self else { return }
            self.sampleGeneration = generation
            self.sampleFrameCount = 0
            self.directPreviewPublishedGeneration = nil
            self.transitionFrameGate.reset()
            // ScreenCaptureKit may replace its stream while the same game and
            // menu remain visible. Preserve the established menu language
            // across that transport reset while discarding scene ownership.
            self.menuStencilWorker.reset()
            self.gameplayMenuEvidenceGate.reset()
            if resetCamera {
                let room = self.visualRoomTracker.reset()
                self.lastPublishedRoomStatus = room.statusText
                self.lastPublishedTransitionPortals = room.portals
                DispatchQueue.main.async { [weak self] in
                    self?.activeRoomStatus = room.statusText
                    self?.transitionPortals = room.portals
                }
            }
        }
    }


    private func message(for error: Error) -> String {
        if case CaptureFailure.windowNotFound = error {
            return "Hollow Knight window not found. Launch it, disable fullscreen, then press Capture."
        }
        if case CaptureFailure.incompatibleGame(let message) = error {
            return message
        }
        return "Capture failed: \(error.localizedDescription). Screen Recording permission may require an app restart."
    }

    private func applyWorldUpdate(_ update: LiveWorldPipelineUpdate) {
        walkedGroundMemory.acceptLatest(update.snapshot)
        if update.snapshot.trackingIslands.contains(where: { $0.placement != .legacyUnknown }) {
            // World closure never rebases the raw ground/masked/coarse solvers.
            // They continue in immutable local coordinates; publication projects
            // each capture through its own corrected island translation.
            if trackingIslandCoordinator.applyWorldSnapshot(update.snapshot) {
                trackingStateLock.lock()
                trackingIslandProjection = LiveIslandWorldProjection(snapshot: update.snapshot)
                trackingStateLock.unlock()
            }
            return
        }
        // A pose-graph closure has already moved the committed atlas. Animate
        // that atlas change even if this local epoch ended while matching.
        if update.acceptedClosure != nil {
            let correctedGround = groundReferenceTracker.applyWorldCorrection(update.correction)
            DispatchQueue.main.async { [weak self] in
                self?.groundReference = correctedGround
            }
            publishPresentationCorrection(
                update.correction,
                captureGeneration: update.captureGeneration
            )
        }
        guard update.acceptedClosure != nil || update.confirmedRecoveryPose != nil,
              let ticket = update.basisTicket else { return }
        guard let resolved = worldBasis.rebasedObservation(for: ticket) else { return }
        let matchedWorldPose: CGPoint?
        if let recovered = update.confirmedRecoveryPose {
            matchedWorldPose = recovered
        } else {
            matchedWorldPose = CGPoint(
                x: resolved.worldPose.x + update.correction.dx,
                y: resolved.worldPose.y + update.correction.dy
            )
        }
        guard let matchedWorldPose,
              worldBasis.confirmPlacement(for: resolved.ticket, matchedWorldPose: matchedWorldPose) else { return }

        let request = FeatureRefinementRequest(
            generation: update.captureGeneration,
            timestamp: update.captureTimestamp
        )
        _ = featureCorrectionPolicy.apply(update.correction, for: request, onAcceptance: {})
        let basisSnapshot = worldBasis.snapshot
        trackingStateLock.lock()
        let current = trackingState
        if current.generation == update.captureGeneration {
            trackingState = TrackingState(
                cameraPosition: basisSnapshot.latestWorldPose ?? matchedWorldPose,
                regions: current.regions,
                poseTimestamp: nil,
                basisTicket: nil,
                groundAnchored: current.groundAnchored,
                gameplayEnabled: current.gameplayEnabled,
                generation: current.generation
            )
        }
        trackingStateLock.unlock()
        if update.confirmedRecoveryPose != nil {
            publishPresentationCorrection(
                update.correction,
                captureGeneration: update.captureGeneration
            )
        }
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.localizationStatus = "Tracking"
        }
    }

    private func publishPresentationCorrection(
        _ correction: CGVector,
        captureGeneration: UInt64
    ) {
        let presentationCorrection = CGPoint(
            x: correction.dx * latestPresentationScale,
            y: correction.dy * latestPresentationScale
        )
        DispatchQueue.main.async { [weak self] in
            guard let self,
                  self.worldBasis.snapshot.captureGeneration == captureGeneration else { return }
            self.presentationCorrectionOffset.x += presentationCorrection.x
            self.presentationCorrectionOffset.y += presentationCorrection.y
        }
    }

    /// Main-thread publication uses the same immutable graph revision as atlas
    /// tiles and owned ground, including a render queued before a loop closure.
    private func reprojectPresentedIslandFrame() {
        refreshOwnedWorldOverlays()
        guard let frame = presentedIslandFrame else { return }
        let worldPose: CGPoint
        if let island = frame.island, let projection = publishedIslandProjection {
            guard let projected = projection.worldPose(for: island) else { return }
            worldPose = projected
        } else { worldPose = frame.island?.worldPose ?? frame.rawPose }
        let dx = (worldPose.x - frame.rawPose.x) * frame.scale
        let dy = (worldPose.y - frame.rawPose.y) * frame.scale
        let bounds = frame.rawBounds.offsetBy(dx: dx, dy: dy)
        let focus = CGPoint(x: frame.rawFocus.x + dx, y: frame.rawFocus.y + dy)
        if liveBounds != bounds { liveBounds = bounds }
        if mapFocus != focus { mapFocus = focus }
    }

    /// Reopened atlases need the same overlays even before a new live capture.
    private func refreshOwnedWorldOverlays() {
        guard let snapshot = liveWorldSnapshot,
              let first = snapshot.observations.first else {
            ownedGroundOverlayLines = []
            islandPlacementOverlay = LayerSceneFeatureReviewOverlay(markers: [], lines: [])
            groundOverlayRevision = -1
            return
        }
        let anchorObservation = snapshot.observations.first { $0.id == snapshot.anchoredObservationID } ?? first
        let scale = presentedIslandFrame?.scale ?? CGFloat(first.sourceWidth) / first.solveWidth
        let anchor = presentedIslandFrame?.anchor ?? anchorObservation.optimizedPose
        if snapshot.mapRevision != groundOverlayRevision || scale != groundOverlayScale
            || anchor != groundOverlayAnchor {
            let frameHeight = CGFloat(first.sourceHeight) * first.solveWidth * scale / CGFloat(first.sourceWidth)
            ownedGroundOverlayLines = LiveGroundOverlayGeometry.make(ownedGroundLines, scale: scale, anchor: anchor,
                clearanceHeight: GroundClearance.height(frameHeight: frameHeight, knight: nil))
            // Atlas graph coordinates are scaled world pixels. Anchor changes affect only display placement.
            let walkID = LiveGroundLineEvidence.walkedSegmentID
            let grouped = Dictionary(grouping: ownedGroundLines.filter {
                $0.segmentID == walkID || $0.placement == .connected || $0.placement == .reference
            }, by: \.roomID)
            for id in visualBaseRooms.subtracting(grouped.keys) {
                visualPaths.setBase(room:"room/" + String(id),floors:[],fallbackHeight:Int((frameHeight*0.12).rounded()))
            }
            visualBaseRooms = Set(grouped.keys)
            for (id,lines) in grouped {
                let key = "room/" + String(id)
                let supported = LiveGroundOverlayGeometry.make(lines, scale: scale, anchor: .zero,
                    clearanceHeight: GroundClearance.height(frameHeight: frameHeight, knight: nil))
                let floors = supported.filter { $0.segmentID != walkID }.compactMap(PathPlatform.init(stroke:))
                let walked = supported.filter { $0.segmentID == walkID }.compactMap(PathPlatform.init(stroke:))
                visualPaths.setBase(room:key,floors:floors,walked:walked,fallbackHeight:Int((frameHeight*0.12).rounded()))
            }
            if visualPathRoom.isEmpty, let last = snapshot.observations.last {
                visualPathRoom = "room/" + String(last.roomID)
                visualPathOffset = .init(x:-anchor.x*scale,y:-anchor.y*scale)
            }
            islandPlacementOverlay = LiveIslandOverlayGeometry.make(snapshot: snapshot, scale: scale, anchor: anchor)
            groundOverlayRevision = snapshot.mapRevision
            groundOverlayScale = scale
            groundOverlayAnchor = anchor
        }
    }

    private func publishAtlasTiles(_ tiles: [LiveAtlasOutput.AtlasLayer]) {
        guard !tiles.isEmpty else { return }
        if let incomingRevision = tiles.first?.revision,
           let atlasRevision,
           incomingRevision < atlasRevision { return }
        let combined = tiles.reduce(CGRect.null) { $0.union($1.bounds) }
        atlasTiles = tiles
        atlasImage = tiles.count == 1 ? tiles[0].image : nil
        let registered = tiles.first?.contentBounds
        atlasBounds = registered.flatMap {
            $0.width > 0 && $0.height > 0 ? $0 : nil
        } ?? (combined.isNull ? nil : combined)
        atlasRevision = tiles.first?.revision
    }

    /// Starts a new local odometry epoch exactly once for a run of rejected
    /// registrations. The committed world and its feature references remain.
    private func enterRecoveryIfNeeded() {
        guard worldBasis.trackingState == .tracking else { return }
        worldBasis.loseLocalTracking()
        groundHypothesisTracker.reset(keepingGlobalFeatures: true)
        featureRefinement.submitReset { [weak self] in
            self?.persistentFeatureTracker.resetTracking(keepingLandmarks: true)
        }
        DispatchQueue.main.async { [weak self] in self?.localizationStatus = "Recovering" }
    }
}

extension LiveCaptureModel: SCStreamOutput, SCStreamDelegate {
    func stream(
        _ stream: SCStream,
        didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
        of outputType: SCStreamOutputType
    ) {
        guard outputType == .screen,
              sampleBuffer.isValid,
              let pixelBuffer = sampleBuffer.imageBuffer,
              acceptedSampleStreamID == ObjectIdentifier(stream) else { return }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]]
        let info = attachments?.first
        if let status = info?[.status] as? Int,
           SCFrameStatus(rawValue: status) != .complete {
            captureIncompleteFrames &+= 1
            return
        }
        captureCompleteFrames &+= 1
        let timestamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer).seconds
        let capturedAt = ProcessInfo.processInfo.systemUptime
        telemetry.record(.captureDelivery, seconds: capturedAt - timestamp)
        autoreleasepool { stageFrame(pixelBuffer, info: info, timestamp: timestamp, capturedAt: capturedAt) }
    }

    private func stageFrame(_ pixelBuffer: CVPixelBuffer, info: [SCStreamFrameInfo: Any]?,
                            timestamp: Double, capturedAt: TimeInterval) {
        guard !capturedFrameStateLock.withLock({ captureProcessingPaused || playbackPreparationCapturePaused }) else { return }
        guard let converted = CaptureFrameConverter.convert(pixelBuffer, info: info, context: poseImageContext) else { return }
        stageConvertedFrame(converted, timestamp: timestamp, capturedAt: capturedAt)
    }

    private func stageConvertedFrame(_ converted: ConvertedCaptureFrame,
                                     timestamp: Double, capturedAt: TimeInterval) {
        capturedFrameStateLock.lock()
        let isFrozen = captureProcessingPaused
            || playbackPreparationCapturePaused
        capturedFrameStateLock.unlock()
        // A frozen editor already owns its screenshot. Do not convert, solve,
        // or publish background frames that invalidate its entire view graph.
        guard !isFrozen else { return }
        let frame = converted.image
        recordDirectPreviewFrame(at: timestamp)
        capturedFrameStateLock.lock()
        latestCleanGameFrame = frame
        capturedFrameStateLock.unlock()
        if labelPauseState == .paused {
            DispatchQueue.main.async { [weak self] in
                guard let self, self.labelPauseState == .paused,
                      self.labelImage == nil else { return }
                self.labelImage = frame
                self.capturedFrameStateLock.lock()
                self.captureProcessingPaused = true
                self.capturedFrameStateLock.unlock()
            }
        }
        telemetry.record(.captureToStage, seconds: ProcessInfo.processInfo.systemUptime - capturedAt)
        let framePreparationStartedAt = ProcessInfo.processInfo.systemUptime
        let signalProfile = FrameRegionRenderer.signalProfile(in: frame)
        let transitionDecision = transitionFrameGate.observe(signalProfile)
        sampleFrameCount += 1
        let hackerActive = isHackerWorkspaceActive()
        let renderedGameFrame = canUseHacker || RenderedFrameMarker.enabled
            || inputPathRecorder.isRecording || botEvaluation.isRecording || playbackDivergenceMonitor.isAcceptingSamples
            ? RenderedFrameMarker.decode(frame) : nil
        inputPathRecorder.recordVisualFrame(frame, captureTimestamp: timestamp,
            capturedAt: capturedAt, captureGeneration: sampleGeneration,
            solveWidth: Double(converted.solveWidth), renderedGameFrame: renderedGameFrame)
        nextObjectSourceFrameIdentifier &+= 1
        let objectSourceFrameIdentifier = nextObjectSourceFrameIdentifier
        let captureGeneration = sampleGeneration
        if directPreviewPublishedGeneration != captureGeneration {
            directPreviewPublishedGeneration = captureGeneration
            let previewBounds = CGRect(
                x: 0,
                y: 0,
                width: CGFloat(frame.width),
                height: CGFloat(frame.height)
            )
            DispatchQueue.main.async { [weak self] in
                guard let self,
                      self.labelImage == nil,
                      captureGeneration == self.currentStatusGeneration,
                      self.liveImage == nil else { return }
                self.liveImage = frame
                self.liveBounds = previewBounds
                self.mapFocus = CGPoint(x: previewBounds.midX, y: previewBounds.midY)
            }
        }
        let solved = currentTrackingState(for: captureGeneration)
        if let poseTimestamp = solved.poseTimestamp {
            telemetry.record(.poseAge, seconds: timestamp - poseTimestamp)
        }
        let objectDetections = currentObjectDetections(
            captureGeneration: captureGeneration,
            timestamp: timestamp
        )
        let hudStarted = ProcessInfo.processInfo.systemUptime
        let hudStencil = hudStencilTracker.observe(frame,
            // HUD evidence participates in opening the gameplay gate. Keeping
            // it behind that gate made startup depend only on coarse brightness
            // and delayed object-model output.
            gameplay: true,
            timestamp: timestamp, generation: captureGeneration)
        telemetry.record(.hudStencil, seconds: ProcessInfo.processInfo.systemUptime - hudStarted)
        // Fresh HUD evidence suppresses menu lookalikes while the object
        // model independently confirms the Knight for gameplay entry.
        let menuObservation = hudStencil?.establishesGameplay == true ? nil : menuStencilWorker.observe(
            frame, timestamp: timestamp, asynchronously: hackerActive
        )
        let rawMenuStencil = menuObservation?.observation.result
        let selectorCalibrationDiagnostic = menuObservation?.observation.diagnostic
        let menuStencil = gameplayMenuEvidenceGate.admits(
            menuContext: rawMenuStencil?.isMatch == true
                ? rawMenuStencil?.context : nil,
            gameplayLatched: solved.gameplayEnabled,
            hudHealthMatchCount: hudStencil?.health.count ?? 0,
            timestamp: timestamp
        ) ? rawMenuStencil : nil
        let recognizedGameStatus = LiveGameStatusResolver.resolve(
            objectDetections,
            menuStencil: menuStencil,
            hudStencil: hudStencil,
            creditsLikely: CreditsVisualSignature.isLikelyCredits(frame)
        )
        let menuInput = BotDemoMenuObservation.make(menu: menuStencil,
            freshGameplay: hudStencil?.establishesGameplay == true && recognizedGameStatus?.context.isGameplay == true,
            frameTimestamp: timestamp, capturedAt: capturedAt,
            availableAt: ProcessInfo.processInfo.systemUptime, generation: captureGeneration)
        if !visualReplayMode, !profileLaunchPending {
            if let launch = profileLaunchDetector.observe(menuInput) {
                profileLaunchPending = true
                DispatchQueue.main.async { [weak self] in self?.switchToProfileAtlas(launch) }
            }
            let hold = profileLaunchPending || profileLaunchFailed || profileLaunchDetector.awaitingGameplay
            profileAtlasGate.withLock { $0 = hold }
        }
        let readingOpening = profileLaunchDetector.awaitingGameplay && menuInput.context == nil && !menuInput.gameplay
        let holdAtlas = profileAtlasGate.withLock { $0 }
        if canUseHacker || inputPathRecorder.isRecording || botEvaluation.isRecording, let renderedGameFrame {
            recordHackerCapture(frame, unityFrame: renderedGameFrame, capturedAt: timestamp,
                integratesAtlas: sampleFrameCount.isMultiple(of: HollowKnightCaptureConfiguration.atlasSampleStride),
                captureGeneration: captureGeneration, pathState: .init(
                    hasSceneSignal: transitionDecision.admitsTracking && !holdAtlas, recognized: recognizedGameStatus?.context))
        }
        let displayingMenu = recognizedGameStatus.map { !$0.context.isGameplay } ?? false
        let frameExtent = CGRect(x: 0, y: 0, width: frame.width, height: frame.height)
        let transitionRegions = replaySceneRegions ?? SceneRegions.atlasGameplay(
            fromObjectDetections: objectDetections,
            in: frameExtent,
            knightMinimumConfidence:
                LiveObjectDetectionPostprocessor.groundKnightConfidenceThreshold,
            hudStencil: hudStencil
        )
        let priorRoom = visualRoomTracker.snapshot
        let groundTrackingReliable = CaptureGroundReliability.isReliable(
            groundAnchored: solved.groundAnchored,
            poseTimestamp: solved.poseTimestamp,
            frameTimestamp: timestamp,
            frameAdmitsTracking: transitionDecision.admitsTracking
        )
        let reliableCameraVelocity = FloorlessMotionHandoff.seed(
            velocity: solved.reliableGroundCameraVelocity,
            measuredAt: solved.reliableGroundVelocityTimestamp,
            presentedAt: timestamp
        )
        let roomCameraPosition = groundTrackingReliable
            ? solved.cameraPosition
            : FloorlessMotionHandoff.position(
                solved.cameraPosition,
                measuredAt: solved.poseTimestamp,
                seed: reliableCameraVelocity,
                presentedAt: timestamp,
                solveWidth: converted.solveWidth
            )
        let archivedInput = visualReplayMode && visualReplayInput?.captureTimestamp == timestamp ? visualReplayInput : nil
        let horizontalInput: VisualRoomDirection?
        if let drive = archivedInput?.horizontalDrive { horizontalInput = VisualRoomDirection(rawValue: drive) }
        else { horizontalInput = visualReplayMode ? visualReplayDirection : gameControlForwarder.currentHorizontalRoomDirection() }
        let room = solved.gameplayEnabled && !holdAtlas
            ? visualRoomTracker.observe(
                profile: signalProfile,
                baseDecision: transitionDecision,
                horizontalInput: horizontalInput,
                knightRect: transitionRegions.knight,
                frameSize: CGSize(width: frame.width, height: frame.height),
                currentWorldPose: roomCameraPosition,
                completedPose: solved.poseTimestamp.map {
                    VisualCameraPoseSample(timestamp: $0, cameraPosition: solved.cameraPosition)
                },
                reliableCameraVelocity: reliableCameraVelocity,
                solveWidth: converted.solveWidth,
                timestamp: timestamp,
                groundTrackingReliable: groundTrackingReliable
            )
            : visualRoomTracker.snapshot
        let roomChanged = room.revision != priorRoom.revision
        let roomOwnershipChanged = room.roomID != priorRoom.roomID
        let portalTopologyChanged = room.portals != priorRoom.portals
        if roomChanged {
            persistRoomTopology()
        }
        // A traversal changes only active ownership. Rebuilding the complete
        // atlas here used to race ahead of Room 2 persistence and replace the
        // composer with a Room-1-only snapshot on the return trip. Replay old
        // sources only when a portal/crop is first created or actually edited.
        if portalTopologyChanged {
            atlasPipeline.rebuildAtlasForRoomComposition()
        }
        let controllingCoarsePose = room.coarseMotionIsControlling
            ? room.coarseWorldPose ?? room.transitionWorldPose : nil
        let presentationCameraPosition = controllingCoarsePose
            ?? (roomOwnershipChanged
                ? room.activationWorldPose ?? room.entryWorldPose
                : PresentationPosePrediction.position(solved.cameraPosition,
                    velocity: solved.cameraVelocity, measuredAt: solved.poseTimestamp,
                    presentedAt: timestamp))
        let presentationIsland = roomOwnershipChanged ? nil : solved.islandCapture.map {
            $0.advancing(by: CGVector(dx: presentationCameraPosition.x - solved.cameraPosition.x,
                                     dy: presentationCameraPosition.y - solved.cameraPosition.y))
        }
        trackingStateLock.lock()
        let captureProjection = trackingIslandProjection
        trackingStateLock.unlock()
        let projectedPresentationPosition = presentationIsland.flatMap {
            captureProjection?.worldPose(for: $0) ?? $0.worldPose
        } ?? presentationCameraPosition
        inputPathRecorder.recordCoarseMotion(
            captureTimestamp: timestamp,
            renderedGameFrame: renderedGameFrame,
            presentedCameraPosition: projectedPresentationPosition,
            coarseCameraPosition: room.coarseWorldPose ?? room.transitionWorldPose,
            estimate: room.coarseMotionEstimate,
            placeMatch: room.coarsePlaceMatch,
            motionGrid: signalProfile.motionGrid,
            isControlling: room.coarseMotionIsControlling,
            isTransitioning: room.isTransitioning,
            roomID: room.roomID,
            roomRevision: room.revision,
            coarsePoseRevision: room.coarsePoseRevision,
            signalMeanPeak: signalProfile.meanPeak,
            signalVisibleFraction: signalProfile.visibleFraction,
            input: RecordedCoarseMotionInput(
                gameplayEnabled: solved.gameplayEnabled,
                groundTrackingReliable: groundTrackingReliable,
                cameraX: Double(roomCameraPosition.x), cameraY: Double(roomCameraPosition.y),
                velocityX: reliableCameraVelocity.map { Double($0.velocity.dx) },
                velocityY: reliableCameraVelocity.map { Double($0.velocity.dy) },
                velocityTimestamp: reliableCameraVelocity?.timestamp,
                horizontalInput: horizontalInput?.rawValue,
                completedPose: solved.poseTimestamp.map {
                    VisualCameraPoseSample(timestamp: $0, cameraPosition: solved.cameraPosition)
                }
            ),
            observedAt: capturedAt
        )
        if let renderedGameFrame,
           let decision = playbackDivergenceMonitor.appendVision(
            frameKey: renderedGameFrame,
            cameraPosition: projectedPresentationPosition,
            observedAt: capturedAt,
            isTransitioning: room.isTransitioning
           ) {
            handlePlaybackDivergence(decision)
        }
        if TransitionPosePublication.replacesTrackingState(
            roomOwnershipChanged: roomOwnershipChanged,
            coarseMotionIsControlling: room.coarseMotionIsControlling
        ) {
            trackingStateLock.lock()
            if trackingState.generation == captureGeneration {
                let priorReliableVelocity = trackingState.reliableGroundCameraVelocity
                let priorReliableTimestamp = trackingState.reliableGroundVelocityTimestamp
                trackingState = TrackingState(
                    cameraPosition: presentationCameraPosition,
                    regions: trackingState.regions,
                    poseTimestamp: nil,
                    basisTicket: nil,
                    groundAnchored: false,
                    gameplayEnabled: trackingState.gameplayEnabled,
                    generation: captureGeneration,
                    reliableGroundCameraVelocity: priorReliableVelocity,
                    reliableGroundVelocityTimestamp: priorReliableTimestamp,
                    islandCapture: presentationIsland
                )
            }
            trackingStateLock.unlock()
        }
        if coarseMotionDiagnosticsEnabled,
           let grid = room.coarseMotionGrid {
            let motion = coarseDiagnosticPrevious.flatMap { previous in
                timestamp > previous.timestamp && timestamp - previous.timestamp <= 0.5
                    ? LowResolutionRoomMotionTracker.translation(from: previous.grid, to: grid) : nil
            }
            coarseDiagnosticPrevious = (grid, timestamp)
            let diagnosticImage = LowResolutionMotionDiagnosticRenderer.image(
                motion: motion,
                gridWidth: grid.width,
                frameSize: CGSize(width: frame.width, height: frame.height),
                isControlling: room.coarseMotionIsControlling
            )
            let diagnosticStatus = LowResolutionMotionDiagnosticPresentation.text(
                motion: motion,
                isControlling: room.coarseMotionIsControlling,
                solveWidth: converted.solveWidth,
                gridWidth: grid.width
            )
            DispatchQueue.main.async { [weak self] in
                guard self?.coarseMotionDiagnosticsEnabled == true else { return }
                self?.coarseMotionDiagnosticImage = diagnosticImage
                self?.coarseMotionDiagnosticStatus = diagnosticStatus
            }
        } else {
            coarseDiagnosticPrevious = nil
        }
        // A pending doorway changes ownership policy, not pixel visibility.
        // Continue visual measurement until the signal gate detects blackout;
        // processVision independently withholds committed atlas writes.
        let hasSignal = transitionDecision.admitsTracking
        if room.statusText != lastPublishedRoomStatus {
            lastPublishedRoomStatus = room.statusText
            DispatchQueue.main.async { [weak self] in
                self?.activeRoomStatus = room.statusText
            }
        }
        if room.portals != lastPublishedTransitionPortals {
            lastPublishedTransitionPortals = room.portals
            DispatchQueue.main.async { [weak self] in
                self?.transitionPortals = room.portals
            }
        }
        let shouldRunVision = sampleFrameCount.isMultiple(
            of: HollowKnightCaptureConfiguration.motionSampleStride
        )
        if LiveGameStatusEvidencePolicy.shouldRunObjectInference(
            for: recognizedGameStatus
        ),
           sampleFrameCount.isMultiple(of: HollowKnightCaptureConfiguration.objectInferenceStride) {
            enqueueObjectInference(ObjectInferenceFrame(
                room: room.statusText, capturedAt: capturedAt, rawImage: frame,
                sourceFrameIdentifier: objectSourceFrameIdentifier,
                timestamp: timestamp,
                captureGeneration: captureGeneration,
                createsPixelDiagnostic: objectDetectionPixelsEnabled
            ))
        }
        if shouldRunVision {
            enqueueVision(VisionFrame(
                presentationFrame: frame,
                solveWidth: converted.solveWidth,
                hasGameplaySignal: hasSignal && !holdAtlas,
                resumedAfterTransition: transitionDecision.resumedAfterTransition || roomChanged,
                signalProfile: signalProfile,
                room: room,
                timestamp: timestamp,
                worldTimestamp: visualReplayWorldTimestamp ?? Date().timeIntervalSinceReferenceDate,
                capturedAt: capturedAt,
                generation: captureGeneration,
                hudStencil: hudStencil,
                menuStencil: menuStencil,
                horizontalInput: horizontalInput,
                jumpHeld: visualReplayMode
                    ? (archivedInput?.recentJump ?? (timestamp - visualReplayLastJump <= GameControlForwarder.jumpContactCooldown))
                    : gameControlForwarder.hasRecentJumpInput()
            ))
        }
        // Offline replay exercises capture, motion, world persistence and
        // atlas integration; it has no display or object-inference consumer.
        if visualReplayMode { return }
        let extent = frameExtent
        let regions = solved.gameplayEnabled
            ? SceneRegions.atlasGameplay(fromObjectDetections: objectDetections,
                                         in: extent, hudStencil: hudStencil)
            : SceneRegions.startup(in: extent)
        let presentationRegions = displayingMenu ? SceneRegions.startup(in: extent) : regions
        // The status label can retain Gameplay through missing cues. Paths
        // require Gameplay recognition on the current frame.
        let pathFrame = LiveAtlasPresentationPolicy.admitsPathFrame(
            hasSceneSignal: hasSignal && !holdAtlas && !room.isTransitioning,
            recognizedContext: recognizedGameStatus?.context
        )
        DispatchQueue.main.async { [weak self] in
            guard let self, self.labelImage == nil,
                  captureGeneration == self.currentStatusGeneration else { return }
            self.latestSkillFrame = menuInput
            if self.readingOpeningCutscene != readingOpening { self.readingOpeningCutscene = readingOpening }
            if [.mainTitle, .selectProfile, .clearSave].contains(menuInput.context) {
                self.pendingNotebookProfile = nil
                if self.notebookProfile != nil { self.notebookProfile = nil }
            }
            let status = self.gameStatusTracker.observe(recognizedGameStatus, at: timestamp)
            self.updateQuitGameExitWatch(for: status)
            let context = status?.context.labelingContext?.storageIdentifier
            let gameplay = status?.context.isGameplay == true
            self.updateAtlasPathEvidence { $0.observe(gameplay: pathFrame, at: timestamp) }
            if !gameplay {
                self.botDemonstrations.stop(reason: "gameplay-ended")
                if !self.botPlayer.allowsInGameMenu { self.botPlayer.stop(reason: "Gameplay ended") }
            }
            if self.liveGameStatus != status?.displayText { self.liveGameStatus = status?.displayText }
            if self.botDemo.isActive {
                if !self.switchingProfileAtlas { self.botDemo.observe(menuInput) }
            }
            if self.liveGameContextIdentifier != context { self.liveGameContextIdentifier = context }
            if self.liveSelectedMenuOption != status?.selectedMenuOption {
                self.liveSelectedMenuOption = status?.selectedMenuOption
            }
            if let rawMenuStencil {
                self.liveMenuStencilDiagnostic = String(
                    format: "raw=%@/%@ phase=%@ confidence=%.3f evidence=%.3f language=%@ comparisons=%d admitted=%@",
                    rawMenuStencil.context.storageIdentifier,
                    rawMenuStencil.selectedOption ?? "none",
                    rawMenuStencil.phase.rawValue,
                    rawMenuStencil.confidence,
                    rawMenuStencil.sceneEvidenceRatio,
                    rawMenuStencil.languageIdentifier ?? "shared",
                    rawMenuStencil.comparisonCount,
                    menuStencil == nil ? "false" : "true"
                ) + "; calibration="
                    + (selectorCalibrationDiagnostic ?? "idle")
            } else {
                self.liveMenuStencilDiagnostic = "raw=none admitted=false"
            }
            if self.hasGameplayStarted != gameplay { self.hasGameplayStarted = gameplay }
            self.publishThinkingScreen(status: status, recognition: recognizedGameStatus, page: menuInput.menuPage,
                                       stencilImage: menuStencil?.isMatch == true ? menuObservation?.image : nil,
                                       frame: frame, generation: captureGeneration)
        }
        if labelPauseState == .ready {
            capturedFrameStateLock.lock()
            recentCapturedFrameBuffer.append(RecentCapturedFrame(
                captureGeneration: captureGeneration,
                sourceFrameIdentifier: objectSourceFrameIdentifier,
                image: frame,
                detections: objectDetections
            ))
            capturedFrameStateLock.unlock()
        }
        telemetry.record(
            .framePreparation,
            seconds: ProcessInfo.processInfo.systemUptime - framePreparationStartedAt
        )
        enqueue(RenderFrame(
            frame: frame,
            solveWidth: converted.solveWidth,
            regions: presentationRegions,
            timestamp: timestamp,
            capturedAt: capturedAt,
            cameraPosition: presentationCameraPosition,
            observationPoseTimestamp: solved.poseTimestamp == timestamp ? timestamp : nil,
            hasGameplaySignal: hasSignal && solved.gameplayEnabled && !displayingMenu,
            allowAtlasWrite: !holdAtlas && hasSignal && solved.gameplayEnabled && !displayingMenu && sampleFrameCount.isMultiple(
                of: HollowKnightCaptureConfiguration.atlasSampleStride
            ),
            objectDetections: objectDetectionEnabled
                ? currentObjectDetectionOverlay(
                    captureGeneration: captureGeneration,
                    timestamp: timestamp,
                    tracked: objectDetections
                )
                : [],
            objectIcons: objectDetectionEnabled ? currentObjectDetectionIcons() : [:],
            enqueuedAt: ProcessInfo.processInfo.systemUptime,
            generation: captureGeneration,
            hudStencil: displayingMenu ? nil : hudStencil,
            showsHUDStencil: stencilDetectionEnabled,
            menuStencil: menuStencil,
            showsMenuStencil: stencilDetectionEnabled,
            islandCapture: presentationIsland,
            islandWorldRevision: captureProjection?.revision
        ))
    }

    private func updateAtlasPathEvidence(_ update: (inout LiveAtlasPathFrameEvidence) -> Void) {
        let timing = LivePresentationTiming.shared
        let gameplayTimestamp = liveImage.flatMap { timing.timestamp(for: $0) }
        let wasVisibleInGameplay = atlasPathFrameEvidence.admits(gameplayTimestamp)
        let wasVisibleInHacker = hasHackerAtlasPathEvidence
        update(&atlasPathFrameEvidence)
        if wasVisibleInGameplay != atlasPathFrameEvidence.admits(gameplayTimestamp)
            || wasVisibleInHacker != hasHackerAtlasPathEvidence {
            objectWillChange.send()
        }
    }

    private func enqueueVision(_ frame: VisionFrame) {
        guard let ticket = visionFramePump.submit(frame, epoch: frame.generation) else { return }
        visionQueue.async { [weak self] in self?.processVision(ticket) }
    }

    private func processVision(_ ticket: LatestFramePump<VisionFrame>.Ticket) {
        let snapshot = ticket.item
        inputPathRecorder.recordVisualStage(.visionStarted,
            captureTimestamp: snapshot.timestamp, captureGeneration: snapshot.generation)
        botEvaluation.stage(.visionStarted, timestamp: snapshot.timestamp, generation: snapshot.generation)
        defer {
            inputPathRecorder.recordVisualStage(.visionCompleted,
                captureTimestamp: snapshot.timestamp, captureGeneration: snapshot.generation)
            botEvaluation.stage(.visionCompleted, timestamp: snapshot.timestamp, generation: snapshot.generation)
        }
        let startedAt = ProcessInfo.processInfo.systemUptime
        telemetry.record(.motionQueueWait, seconds: startedAt - snapshot.enqueuedAt)
        defer {
            telemetry.record(.motion, seconds: ProcessInfo.processInfo.systemUptime - startedAt)
        }
        visionStateLock.lock()
        defer { visionStateLock.unlock() }
        guard snapshot.generation == worldBasis.snapshot.captureGeneration else {
            let completion = visionFramePump.complete(ticket)
            if let next = completion.next {
                visionQueue.async { [weak self] in self?.processVision(next) }
            }
            return
        }

        let startupPhaseBeforeObservation = gameStartupCoordinator.phase
        let startupDetections = currentObjectDetections(
            captureGeneration: snapshot.generation,
            timestamp: snapshot.timestamp
        )
        let startupEvidence = gameStartupDetector.detect(
            in: snapshot.presentationFrame,
            menuStencil: snapshot.menuStencil,
            hudStencil: snapshot.hudStencil,
            gameplayIsLatched: startupPhaseBeforeObservation == .gameplay,
            objectDetections: startupDetections
        )
        let startupDecision = gameStartupCoordinator.observe(startupEvidence)
        if lastStartupDiagnosticAt.map({ snapshot.timestamp - $0 >= 1 }) ?? true {
            lastStartupDiagnosticAt = snapshot.timestamp
            let detections = startupDetections.sorted { $0.confidence > $1.confidence }
                .prefix(8)
                .map { "\($0.classIdentifier):\(Int(($0.confidence * 100).rounded()))" }
                .joined(separator: ",")
            let screen = String(describing: startupEvidence.screen)
            let phase = String(describing: startupDecision.phase)
            let selected = startupEvidence.selectedOption ?? "none"
            let health = snapshot.hudStencil.map { String($0.health.count) } ?? "unavailable"
            atlasAdmissionLog.info(
                "startup phase=\(phase, privacy: .public) screen=\(screen, privacy: .public) selected=\(selected, privacy: .public) gameplayLikely=\(startupEvidence.gameplayLikely, privacy: .public) healthMatches=\(health, privacy: .public) detections=\(detections, privacy: .public)"
            )
        }
        if let action = startupDecision.action {
            let forwarded: Bool
            if autoNavigateToGameplay {
                switch action {
                case .moveSelectionUp:
                    forwarded = gameControlForwarder.pulseAutomationButton(
                        .up,
                        duration: 0.085
                    )
                case .selectStartGame, .selectProfileOne:
                    forwarded = gameControlForwarder.tapMenuSelect()
                }
            } else {
                forwarded = false
            }
            if !forwarded {
                gameStartupCoordinator.retry(action)
            }
        }
        let effectiveStartupPhase = gameStartupCoordinator.phase
        DispatchQueue.main.async { [weak self] in
            guard let self, self.labelImage == nil,
                  self.localizationStatus != effectiveStartupPhase.status else { return }
            self.localizationStatus = effectiveStartupPhase.status
        }

        if effectiveStartupPhase != .gameplay {
            let extent = CGRect(
                x: 0,
                y: 0,
                width: snapshot.presentationFrame.width,
                height: snapshot.presentationFrame.height
            )
            trackingStateLock.lock()
            trackingState = TrackingState(
                cameraPosition: worldBasis.snapshot.latestWorldPose ?? accumulator.position,
                regions: .startup(in: extent),
                poseTimestamp: nil,
                basisTicket: nil,
                groundAnchored: nil,
                gameplayEnabled: false,
                generation: snapshot.generation
            )
            trackingStateLock.unlock()
            let completion = visionFramePump.complete(ticket)
            if let next = completion.next {
                visionQueue.async { [weak self] in self?.processVision(next) }
            }
            return
        }

        if startupPhaseBeforeObservation != .gameplay {
            // The confirming frame remains display-only. Registration begins
            // from a clean reference on the next sampled gameplay frame.
            accumulator.reset()
            registrationContinuity.reset()
            trackingStateLock.lock()
            trackingState = TrackingState(
                cameraPosition: worldBasis.snapshot.latestWorldPose ?? .zero,
                regions: nil,
                poseTimestamp: nil,
                basisTicket: nil,
                groundAnchored: nil,
                gameplayEnabled: true,
                generation: snapshot.generation
            )
            trackingStateLock.unlock()
            let completion = visionFramePump.complete(ticket)
            if let next = completion.next {
                visionQueue.async { [weak self] in self?.processVision(next) }
            }
            return
        }

        latestPresentationScale = CGFloat(snapshot.presentationFrame.width)
            / max(1, snapshot.solveWidth)
        if snapshot.room.revision != handledRoomRevision {
            handledRoomRevision = snapshot.room.revision
            let activationPose = snapshot.room.activationWorldPose
                ?? snapshot.room.entryWorldPose
            accumulator.reset()
            accumulator.applyGlobalCorrection(to: activationPose)
            registrationContinuity.reset()
            groundReferenceTracker.reset()
            groundHypothesisTracker.reset()
            semanticGroundAtlas.reset()
            groundHypothesisTracker.reanchorLocalCamera(to: CGPoint(
                x: activationPose.x * latestPresentationScale,
                y: activationPose.y * latestPresentationScale
            ))
            transitionMotionBridge.reset()
            calibratedSceneMotion.reset()
            visualFallMotion.reset()
            groundPlacementRecovery.reset()
            pathPlaybackGroundReadiness = .empty
            worldBasis.beginLocalEpoch()
            featureRefinement.submitReset { [weak self] in
                self?.persistentFeatureTracker.reset()
            }
            DispatchQueue.main.async { [weak self] in
                self?.localizationStatus = "Tracking \(snapshot.room.roomName)"
            }
        }

        // A suspected doorway is not evidence that every visible texture has
        // vanished. Continue measured tracking while signal remains; only the
        // actual blackout bypasses the ground/masked solvers. Pending topology
        // still blocks committed atlas geometry until ownership is resolved.
        if snapshot.room.isTransitioning && !snapshot.hasGameplaySignal {
            let transitionPose = snapshot.room.transitionWorldPose
                ?? worldBasis.snapshot.latestWorldPose
                ?? accumulator.position
            let measured = snapshot.room.coarseMotionMeasured
                && snapshot.room.coarseMeasurementTimestamp.map { abs($0 - snapshot.timestamp) <= 0.000001 } == true
            let island = trackingIslandCoordinator.observe(timestamp: snapshot.timestamp,
                roomID: snapshot.room.roomID, worldPose: transitionPose,
                measured: measured, referenceTimestamp: snapshot.room.coarseReferenceTimestamp)
            recordFloorlessTracking(snapshot, pose: transitionPose, island: island,
                source: measured ? "transitionCoarse" : "transitionHeld")
            accumulator.applyGlobalCorrection(to: transitionPose)
            transitionMotionBridge.suspendForTransition()
            calibratedSceneMotion.reset()
            visualFallMotion.reset()
            groundPlacementRecovery.missingObservation()
            if TransitionPosePublication.replacesTrackingState(
                roomOwnershipChanged: false,
                coarseMotionIsControlling:
                    snapshot.room.coarseMotionIsControlling
            ) {
                trackingStateLock.lock()
                let priorReliableVelocity =
                    trackingState.reliableGroundCameraVelocity
                let priorReliableTimestamp =
                    trackingState.reliableGroundVelocityTimestamp
                trackingState = TrackingState(
                    cameraPosition: transitionPose,
                    regions: nil,
                    poseTimestamp: nil,
                    basisTicket: nil,
                    groundAnchored: false,
                    gameplayEnabled: true,
                    generation: snapshot.generation,
                    reliableGroundCameraVelocity: priorReliableVelocity,
                    reliableGroundVelocityTimestamp: priorReliableTimestamp,
                    islandCapture: island
                )
                trackingStateLock.unlock()
            }
            DispatchQueue.main.async { [weak self] in
                self?.localizationStatus = "Tracking doorway"
            }
            let completion = visionFramePump.complete(ticket)
            if let next = completion.next {
                visionQueue.async { [weak self] in self?.processVision(next) }
            }
            return
        }

        var regions: SceneRegions?
        var cameraUpdate: CameraUpdate?
        var basisTicket: WorldBasisController.ObservationTicket?
        var worldCameraPosition = worldBasis.snapshot.latestWorldPose ?? accumulator.position
        var islandCapture: LiveIslandCapture?
        var capturedGroundGeometry: LiveWorldGroundGeometry?
        var verifiedBootstrapWalk: VisualRoomDirection?
        var observedFloorCandidates: LiveObservedFloorCandidates?
        var observedPlayerBounds: CGRect?
        var groundForFrame = groundReferenceTracker.currentEstimate()
        var groundAnchoredPose = false
        var groundMotionMeasured = false
        var hadGlobalCorrection = false
        var transitionBridgePose = false
        let request = FeatureRefinementRequest(
            generation: snapshot.generation,
            timestamp: snapshot.timestamp
        )
        if snapshot.hasGameplaySignal {
            let objectDetections = currentMaskDetections(
                captureGeneration: snapshot.generation,
                timestamp: snapshot.timestamp
            )
            // Camera pose is supplied below by the line-first ground solver.
            // Capture leases, world tickets and atlas transactions retain
            // their existing lifecycle; Vision no longer estimates motion.
            basisTicket = worldBasis.observe(
                localPose: accumulator.position,
                captureTimestamp: snapshot.timestamp
            )
            if let basisTicket,
               let resolved = worldBasis.rebasedObservation(for: basisTicket) {
                worldCameraPosition = resolved.worldPose
            }
            let extent = CGRect(
                x: 0, y: 0,
                width: snapshot.presentationFrame.width,
                height: snapshot.presentationFrame.height
            )
            regions = replaySceneRegions ?? SceneRegions.atlasGameplay(
                fromObjectDetections: objectDetections,
                in: extent,
                knightMinimumConfidence:
                    LiveObjectDetectionPostprocessor.groundKnightConfidenceThreshold,
                hudStencil: snapshot.hudStencil
            )
            if visualReplayMode && !gameplayGym {
                observedPlayerBounds = visualReplayInput?.captureMatchedPlayerBounds(at: snapshot.timestamp)
            } else {
                objectInferenceStateLock.lock()
                observedPlayerBounds = LiveObjectDetectionFreshness.captureMatchedKnightBounds(
                    from: latestMaskDetectionBatch, captureGeneration: snapshot.generation,
                    timestamp: snapshot.timestamp, width: snapshot.presentationFrame.width,
                    height: snapshot.presentationFrame.height)
                objectInferenceStateLock.unlock()
            }
            let shouldShowGroundEdge = groundEdgeEnabled
            let shouldShowGroundDetect = groundDetectEnabled
            let shouldShowGroundStage = groundStageEnabled
            let shouldShowCleaned = cleanedEnabled
            let shouldShowGroundHypotheses = groundHypothesesEnabled
            let tuning = groundTuning
            let groundDetectionStarted = ProcessInfo.processInfo.systemUptime
            // Clean ground is now part of camera orientation, not only a debug
            // overlay. Analyze every sampled Gameplay frame so known line IDs
            // can correct vertical placement before this observation reaches
            // the atlas.
            let sceneForegroundRects = regions?.omittedRects ?? []
            let groundForegroundRects = SceneRegions.groundOmittedRects(from: sceneForegroundRects, in: extent)
            // Replay also uses these source regions for atlas masking.
            var recordedGroundInput = VisualReplayGroundInput(
                captureTimestamp: snapshot.timestamp, captureGeneration: snapshot.generation,
                excludingRects: sceneForegroundRects, knightRect: regions?.knight,
                tuning: tuning, semanticTuning: semanticGroundTuning,
                observedPlayerBounds: observedPlayerBounds)
            recordedGroundInput.horizontalDrive = snapshot.horizontalInput?.rawValue ?? 0
            recordedGroundInput.recentJump = snapshot.jumpHeld
            inputPathRecorder.recordGroundInput(recordedGroundInput)
            botEvaluation.groundInput(recordedGroundInput)
            let trackerForegroundRects = groundForegroundRects.map { rect in
                CGRect(
                    x: rect.minX,
                    y: CGFloat(snapshot.presentationFrame.height) - rect.maxY,
                    width: rect.width,
                    height: rect.height
                )
            }
            let groundAnalysis = GroundLineDetector.analyze(
                snapshot.presentationFrame,
                excluding: groundForegroundRects
            )
            let comparisonAnalysis = groundAnalysis.map(GroundLineDetector.compare)
            let semanticTuning = semanticGroundTuning
            let semanticTheoryAnalysis = comparisonAnalysis.flatMap {
                GroundLineDetector.semanticTheory(from: $0, tuning: semanticTuning,
                    includeRunEvidence: shouldShowGroundStage)
            }
            let semanticDetectedLines = semanticTheoryAnalysis.map {
                GroundLineDetector.cleanFloorLines(from: $0)
            } ?? []
            let rawSemanticFloorLines = GroundLineDetector.gameplayFloors(
                semanticDetectedLines,
                imageHeight: snapshot.presentationFrame.height,
                foregroundRects: groundForegroundRects,
                knightRect: regions?.knight
            )
            var semanticFloorLines = GroundStrokeGeometry.coalescedFloors(rawSemanticFloorLines,
                frameWidth: snapshot.presentationFrame.width)
            var cameraFloorLines = comparisonAnalysis.map {
                GroundLineDetector.cameraFloors(from: $0, tuning: tuning, visibleFloors: semanticFloorLines,
                    imageHeight: snapshot.presentationFrame.height, foregroundRects: groundForegroundRects,
                    knightRect: regions?.knight)
            } ?? []
            let clearance = GroundClearance.height(frameHeight: CGFloat(snapshot.presentationFrame.height),
                knight: observedPlayerBounds)
            let prior = currentTrackingState(for: snapshot.generation)
            if !snapshot.resumedAfterTransition, let island = prior.islandCapture, island.measured,
               island.roomID == snapshot.room.roomID, let t = prior.poseTimestamp, (0...0.15).contains(snapshot.timestamp - t) {
                let walked = walkedGroundMemory.visible(in: island, projection: trackingIslandProjection,
                    pixelsPerSolve: CGFloat(snapshot.presentationFrame.width) / snapshot.solveWidth,
                    width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height)
                // The prior pose is approximate during movement. Trim only
                // interior columns and retain the matching textured floor.
                let margin = max(2, Int(ceil((snapshot.timestamp - t) * 480 * CGFloat(snapshot.presentationFrame.width) / 640)))
                let interiors = walked.compactMap { floor -> CleanFloorLine? in
                    let left = floor.xRange.lowerBound + margin, right = floor.xRange.upperBound - margin
                    return left <= right ? CleanFloorLine(row: floor.row, xRange: left...right) : nil
                }
                let coverage = WalkedFloorCoverage(floors: interiors)
                cameraFloorLines = coverage.filtered(cameraFloorLines, clearance: clearance, keepMatchingRows: 4)
            }
            var semanticLineReviews = [GroundLinePresenceReview]()
            var hypothesisTracking: GroundHypothesisTrackingResult?
            let hypothesisImage: CGImage?
            telemetry.record(.groundDetection,
                seconds: ProcessInfo.processInfo.systemUptime - groundDetectionStarted)
            let groundTrackingStarted = ProcessInfo.processInfo.systemUptime
            var evaluatedPlacement = false
            let priorPoseTimestamp = currentTrackingState(for: snapshot.generation).poseTimestamp
            hypothesisTracking = comparisonAnalysis.map { _ in
                groundHypothesisTracker.update(
                    frame: snapshot.presentationFrame,
                    floorLines: cameraFloorLines,
                    visibleFloors: semanticFloorLines,
                    lineSeparation: tuning.lineSeparation,
                    occlusionMergeGap: tuning.occlusionMergeGap,
                    timestamp: snapshot.timestamp,
                    sourceLuma: groundAnalysis?.sourceLuma,
                    knightRect: regions?.knight,
                    protectedOcclusions: trackerForegroundRects,
                    placementValidator: { proposal in
                        evaluatedPlacement = true
                        let scaled = GroundPlacementRecovery.Proposal(
                            position: CGPoint(x: proposal.position.x / self.latestPresentationScale,
                                              y: proposal.position.y / self.latestPresentationScale),
                            textureSupport: proposal.textureSupport, textureError: proposal.textureError,
                            inlierCount: proposal.inlierCount, globalMatchCount: proposal.globalMatchCount,
                            hasGlobalCorrection: proposal.hasGlobalCorrection,
                            distinctFrame: proposal.distinctFrame,
                            // The atlas may still be purging the previous
                            // generation. Only this tracker's history belongs
                            // to the current seed and coordinate epoch.
                            hasEstablishedGround: proposal.hasEstablishedGround)
                        return self.groundPlacementRecovery.accepts(scaled,
                            current: self.accumulator.position, solveWidth: snapshot.solveWidth,
                            timestamp: snapshot.timestamp,
                            captureElapsed: priorPoseTimestamp.map { snapshot.timestamp - $0 })
                    }
                )
            }
            if !evaluatedPlacement { groundPlacementRecovery.missingObservation() }
            if var tracking = hypothesisTracking {
                semanticLineReviews = semanticFloorLines.enumerated().map { index, line in
                    GroundLinePresenceReview(
                        id: index + 1,
                        line: line,
                        state: .confirmed,
                        visibleSeconds: 0,
                        detectedFraction: 1
                    )
                }
                tracking.detectedFloorLines = semanticFloorLines
                tracking.atlasLines = semanticGroundAtlas.update(
                    trackingLines: tracking.atlasLines,
                    semanticLines: semanticFloorLines,
                    camera: tracking.cameraPosition,
                    frameWidth: snapshot.presentationFrame.width,
                    atlasHeight: CGFloat(snapshot.presentationFrame.height),
                    poseVerified: tracking.poseVerified
                )
                hypothesisTracking = tracking
            }
            var botDetectedLines = semanticLineReviews.filter { $0.state == .confirmed }.flatMap { review in
                review.line.evidenceRanges.map { span in
                    BotTerrainSegment(minimumX: Double(span.lowerBound), maximumX: Double(span.upperBound + 1),
                        y: Double(snapshot.presentationFrame.height - review.line.row),
                        confidence: Double(min(1, max(0, review.detectedFraction))))
                }
            }
            let groundTrackingSeconds = ProcessInfo.processInfo.systemUptime - groundTrackingStarted
            hadGlobalCorrection = hypothesisTracking?.globalCorrection != nil
            if let search = groundHypothesisTracker.latestPatchSearchExecution {
                telemetry.record(.groundPatchSearch, seconds: search.elapsed)
                if groundTraceEnabled {
                    groundTraceLog.info(
                        "patch-search backend=\(search.backend.rawValue, privacy: .public) candidates=\(search.candidateCount, privacy: .public) comparisons=\(search.patchComparisonCount, privacy: .public) milliseconds=\(search.elapsed * 1_000, privacy: .public)"
                    )
                }
            }
            telemetry.record(.groundTracking, seconds: groundTrackingSeconds)
            if let milliseconds = groundHypothesisTracker.completedGlobalSearchMilliseconds {
                telemetry.record(.globalSearch, seconds: milliseconds / 1_000)
            }
            if let hypothesisTracking { pathPlaybackGroundReadiness = hypothesisTracking }
            if let audit = rawGroundFrameAudit, let pixels = groundAnalysis?.sourceLuma,
               let tracking = hypothesisTracking {
                audit.record(pixels: pixels, width: snapshot.presentationFrame.width,
                    height: snapshot.presentationFrame.height, timestamp: snapshot.timestamp,
                    lines: semanticFloorLines, exclusions: trackerForegroundRects, tracking: tracking,
                    featureBank: groundHypothesisTracker.auditGlobalFeatureBank())
            }
            if let tracking = hypothesisTracking {
                groundFrameAudit?.record(frame: snapshot.presentationFrame,
                    timestamp: snapshot.timestamp, lines: semanticFloorLines,
                    tracking: tracking, tuning: semanticTuning, exclusions: trackerForegroundRects)
            }
            if groundTraceEnabled, let ground = hypothesisTracking {
                let pose = ground.cameraPosition.map { "\($0.x),\($0.y)" } ?? "none"
                let delta = ground.cameraTranslation.map { "\($0.dx),\($0.dy)" } ?? "none"
                let correction = ground.globalCorrection.map { "\($0.dx),\($0.dy)" } ?? "none"
                let rows = semanticFloorLines.map { "\($0.row):\($0.xRange.lowerBound)-\($0.xRange.upperBound)" }.joined(separator: ",")
                let reviews = ground.lineReviews.map {
                    "\($0.id):\($0.line.row):\($0.state.rawValue):\(Int($0.detectedFraction * 100))"
                }.joined(separator: ",")
                if lastGroundGeometryTraceAt.map({ snapshot.timestamp - $0 >= 0.25 }) ?? true {
                    lastGroundGeometryTraceAt = snapshot.timestamp
                    for line in ground.atlasLines {
                        let coordinates = "\(line.atlasStart.x),\(line.atlasEnd.x),\(line.atlasStart.y)"
                        groundTraceLog.info("geometry t=\(snapshot.timestamp, privacy: .public) id=\(line.segmentID, privacy: .public) atlas=\(coordinates, privacy: .public)")
                    }
                    for review in ground.lineReviews {
                        let coordinates = "\(review.line.xRange.lowerBound),\(review.line.xRange.upperBound),\(review.line.row)"
                        groundTraceLog.info("presence t=\(snapshot.timestamp, privacy: .public) id=\(review.id, privacy: .public) screen=\(coordinates, privacy: .public) state=\(review.state.rawValue, privacy: .public) fraction=\(review.detectedFraction, privacy: .public)")
                    }
                }
                groundTraceLog.info("track t=\(snapshot.timestamp, privacy: .public) pose=\(pose, privacy: .public) delta=\(delta, privacy: .public) correction=\(correction, privacy: .public) verified=\(ground.poseVerified, privacy: .public) confirmed=\(ground.hasConfirmedGround, privacy: .public) inliers=\(ground.inlierCount, privacy: .public) globalMatches=\(ground.globalMatchCount, privacy: .public) segments=\(ground.groundSegmentCount, privacy: .public) features=\(ground.globalFeatureCount, privacy: .public) rows=\(rows, privacy: .public) reviews=\(reviews, privacy: .public)")
            }
            groundAnchoredPose = false
            if lastAtlasAdmissionDiagnosticAt.map({ snapshot.timestamp - $0 >= 1 }) ?? true {
                let raw = semanticDetectedLines.map { "\($0.row):\($0.xRange)" }.joined(separator: ",")
                let filtered = semanticFloorLines.map { String($0.row) }.joined(separator: ",")
                let presence = hypothesisTracking?.lineReviews.map {
                    "\($0.id):\($0.state.rawValue):\(Int($0.detectedFraction * 100))%"
                }.joined(separator: ",") ?? ""
                atlasAdmissionLog.info("groundRaw=\(raw, privacy: .public) groundAllowed=\(filtered, privacy: .public) presence=\(presence, privacy: .public)")
            }
            // Ground Features are drawn from tracking geometry by the Metal
            // viewport. Avoid constructing a full-frame transparent texture:
            // besides the extra upload, some GPU paths displayed its clear
            // pixels as opaque black over the live capture.
            hypothesisImage = nil
            var bridgeResultForRecording: TransitionMotionBridgeResult?
            var fallPredictionForRecording: VisualFallMotionPrior.Prediction?
            var usedCoarseFloorlessPose = false
            var floorlessCoarseForRecording: CGPoint?
            var floorlessMaskedForRecording: CGPoint?
            var floorlessDirectionForRecording: VisualRoomDirection?
            var floorlessSelectionForRecording: String?
            let groundCandidate: CGPoint? = {
                guard let ground = hypothesisTracking,
                      GroundMotionEvidence.isMeasured(ground),
                      let pose = ground.cameraPosition else { return nil }
                return CGPoint(
                    x: pose.x / latestPresentationScale,
                    y: pose.y / latestPresentationScale
                )
            }()
            let sceneMotionStarted = ProcessInfo.processInfo.systemUptime
            let sceneMotion: CalibratedSceneMotionTracker.Measurement? = {
                guard let pixels = groundAnalysis?.sourceLuma,
                      let grid = CalibratedSceneMotionTracker.grid(pixels: pixels,
                        width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height,
                        excluding: regions?.omittedRects ?? []) else { return nil }
                var calibration: CalibratedSceneMotionTracker.GroundCalibration?
                if let position = groundCandidate, let ground = hypothesisTracking,
                   let reference = ground.localReferenceTimestamp,
                   let displacement = ground.localProjectedImageDisplacement {
                    calibration = .init(position: position, referenceTimestamp: reference,
                        displacement: CGVector(dx: displacement.dx / latestPresentationScale,
                                               dy: displacement.dy / latestPresentationScale))
                }
                return calibratedSceneMotion.observe(grid: grid, timestamp: snapshot.timestamp,
                    solveWidth: snapshot.solveWidth, ground: calibration)
            }()
            let sceneMotionSeconds = ProcessInfo.processInfo.systemUptime - sceneMotionStarted
            telemetry.record(.calibratedSceneMotion, seconds: sceneMotionSeconds)
            if let position = groundCandidate {
                groundAnchoredPose = true
                groundMotionMeasured = hypothesisTracking.map {
                    GroundMotionEvidence.isMeasured($0)
                } ?? false
                let step = CGVector(dx: position.x - accumulator.position.x,
                                    dy: position.y - accumulator.position.y)
                accumulator.applyGlobalCorrection(to: position)
                worldCameraPosition = position
                cameraUpdate = CameraUpdate(
                    state: .accepted, rawStep: step, acceptedStep: step, position: position
                )
                basisTicket = worldBasis.observe(
                    localPose: position, captureTimestamp: snapshot.timestamp
                )
                if groundAnchoredPose, let ticket = basisTicket,
                   worldBasis.trackingState == .recovering {
                    _ = worldBasis.confirmPlacement(for: ticket, matchedWorldPose: position)
                    basisTicket = worldBasis.observe(
                        localPose: position, captureTimestamp: snapshot.timestamp
                    )
                }
                transitionMotionBridge.anchor(
                    frame: snapshot.presentationFrame,
                    excluding: regions?.omittedRects ?? [],
                    cameraPosition: position,
                    movingForegroundRect: regions?.knight,
                    timestamp: snapshot.timestamp
                )
            } else {
                var bridged: TransitionMotionBridgeResult?
                var coarseFallbackPosition: CGPoint?
                if snapshot.resumedAfterTransition {
                    // No visual measurement spans a blackout. Seed the first
                    // visible frame at the last verified pose, but do not
                    // fabricate a room-sized step or write it into the atlas.
                    transitionMotionBridge.resumeAfterTransition(
                        frame: snapshot.presentationFrame,
                        excluding: regions?.omittedRects ?? [],
                        movingForegroundRect: regions?.knight,
                        timestamp: snapshot.timestamp
                    )
                    bridged = nil
                } else if snapshot.room.coarsePlaceMatch != nil,
                          let coarsePosition = snapshot.room.coarseWorldPose {
                    // Absolute room-local place evidence outranks both relative
                    // trajectories.
                    bridged = nil
                    coarseFallbackPosition = coarsePosition
                    floorlessCoarseForRecording = coarsePosition
                    floorlessSelectionForRecording = "coarsePlaceMatch"
                } else if let sceneMotion, sceneMotion.verticalMeasured {
                    // Ground-calibrated vertical scenery carries floorless falls.
                    // Horizontal-only calibration cannot constrain a fall and must
                    // not override the established masked/coarse horizontal path.
                    // Unknown X retains its estimate and cannot claim 2D lineage.
                    let other = snapshot.room.coarseWorldPose ?? accumulator.position
                    let position = CGPoint(
                        x: sceneMotion.horizontalMeasured ? sceneMotion.position.x : other.x,
                        y: sceneMotion.verticalMeasured ? sceneMotion.position.y : other.y)
                    bridged = TransitionMotionBridgeResult(cameraPosition: position,
                        cameraStep: CGVector(dx: position.x - accumulator.position.x,
                                             dy: position.y - accumulator.position.y),
                        confidence: Float(1 / (1 + max(sceneMotion.residualX, sceneMotion.residualY)
                            / (snapshot.solveWidth / 640))), source: .calibratedScene,
                        referenceTimestamp: sceneMotion.referenceTimestamp,
                        horizontalMeasured: sceneMotion.horizontalMeasured,
                        verticalMeasured: sceneMotion.verticalMeasured)
                    floorlessSelectionForRecording = "calibratedScene"
                    floorlessMaskedForRecording = position
                    if let coarse = snapshot.room.coarseWorldPose {
                        _ = visualRoomTracker.applyFloorlessCorrection(
                            CGVector(dx: position.x - coarse.x, dy: position.y - coarse.y),
                            roomID: snapshot.room.roomID, coarsePoseRevision: snapshot.room.coarsePoseRevision)
                    }
                    transitionMotionBridge.anchor(frame: snapshot.presentationFrame,
                        excluding: regions?.omittedRects ?? [], cameraPosition: position,
                        movingForegroundRect: regions?.knight, timestamp: snapshot.timestamp)
                } else {
                    let bridgeMeasurement = transitionMotionBridge.track(
                        frame: snapshot.presentationFrame,
                        excluding: regions?.omittedRects ?? [],
                        solveWidth: snapshot.solveWidth,
                        // Historical ground is not evidence that Y is stationary
                        // during a current-frame ground miss or rejected solve.
                        lockVertical: false,
                        movingForegroundRect: regions?.knight,
                        timestamp: snapshot.timestamp
                    )
                    let expectedDirection =
                        (visualReplayMode ? visualReplayDirection
                            : gameControlForwarder.currentHorizontalRoomDirection())
                            ?? snapshot.room.coarseMotionEstimate?.direction
                    floorlessCoarseForRecording = snapshot.room.coarseWorldPose
                    floorlessMaskedForRecording = bridgeMeasurement?.cameraPosition
                    floorlessDirectionForRecording = expectedDirection
                    let selection = FloorlessPoseCandidateSelector.select(
                        current: accumulator.position,
                        coarse: snapshot.room.coarseWorldPose,
                        masked: bridgeMeasurement?.cameraPosition,
                        expectedDirection: expectedDirection,
                        coarseIsPlaceMatch: false
                    )
                    switch selection?.source {
                    case .coarseCaptureRate:
                        floorlessSelectionForRecording = "coarseCaptureRate"
                        coarseFallbackPosition = selection?.position
                        bridged = nil
                    case .maskedRegistration:
                        floorlessSelectionForRecording = "maskedRegistration"
                        bridged = bridgeMeasurement
                    case nil:
                        floorlessSelectionForRecording = "held"
                        bridged = nil
                        if bridgeMeasurement != nil {
                            // track() advances its private coordinate before
                            // selection. Re-anchor a rejected contradiction so
                            // it cannot leak into the next comparison.
                            transitionMotionBridge.anchor(
                                frame: snapshot.presentationFrame,
                                excluding: regions?.omittedRects ?? [],
                                cameraPosition: accumulator.position,
                                movingForegroundRect: regions?.knight,
                                timestamp: snapshot.timestamp
                            )
                        }
                    }
                }
                if let position = coarseFallbackPosition {
                    let step = CGVector(
                        dx: position.x - accumulator.position.x,
                        dy: position.y - accumulator.position.y
                    )
                    accumulator.applyGlobalCorrection(to: position)
                    worldCameraPosition = position
                    cameraUpdate = CameraUpdate(
                        state: .accepted,
                        rawStep: step,
                        acceptedStep: step,
                        position: position
                    )
                    transitionBridgePose = true
                    usedCoarseFloorlessPose = true
                    basisTicket = worldBasis.observe(
                        localPose: position,
                        captureTimestamp: snapshot.timestamp
                    )
                    if groundPlacementRecovery.allowsAtlasWrite, let ticket = basisTicket,
                       worldBasis.trackingState == .recovering {
                        _ = worldBasis.confirmPlacement(
                            for: ticket,
                            matchedWorldPose: position
                        )
                        basisTicket = worldBasis.observe(
                            localPose: position,
                            captureTimestamp: snapshot.timestamp
                        )
                    }
                    transitionMotionBridge.anchor(
                        frame: snapshot.presentationFrame,
                        excluding: regions?.omittedRects ?? [],
                        cameraPosition: position,
                        movingForegroundRect: regions?.knight,
                        timestamp: snapshot.timestamp
                    )
                }
                if let bridged {
                    bridgeResultForRecording = bridged
                    let position = bridged.cameraPosition
                    accumulator.applyGlobalCorrection(to: position)
                    worldCameraPosition = position
                    cameraUpdate = CameraUpdate(
                        state: .accepted,
                        rawStep: bridged.cameraStep,
                        acceptedStep: bridged.cameraStep,
                        position: position
                    )
                    transitionBridgePose = true
                    basisTicket = worldBasis.observe(
                        localPose: position,
                        captureTimestamp: snapshot.timestamp
                    )
                    if groundPlacementRecovery.allowsAtlasWrite, let ticket = basisTicket,
                       worldBasis.trackingState == .recovering {
                        _ = worldBasis.confirmPlacement(
                            for: ticket,
                            matchedWorldPose: position
                        )
                        basisTicket = worldBasis.observe(
                            localPose: position,
                            captureTimestamp: snapshot.timestamp
                        )
                    }
                }
                if bridged == nil, coarseFallbackPosition == nil,
                   let prediction = visualFallMotion.predict(knight: regions?.knight,
                    timestamp: snapshot.timestamp,
                    frameSize: CGSize(width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height),
                    solveWidth: snapshot.solveWidth) {
                    // Only a short display prediction. No bridge flag, ground
                    // reseeding, confidence promotion or trusted atlas write.
                    let predicted = CGPoint(x: accumulator.position.x, y: prediction.cameraY)
                    let step = CGVector(dx: 0, dy: predicted.y - accumulator.position.y)
                    accumulator.applyGlobalCorrection(to: predicted)
                    worldCameraPosition = predicted
                    cameraUpdate = CameraUpdate(state: .accepted, rawStep: step, acceptedStep: step, position: predicted)
                    basisTicket = worldBasis.observe(localPose: predicted, captureTimestamp: snapshot.timestamp)
                    fallPredictionForRecording = prediction
                }
                // Keep private odometry in the same coordinate system as a
                // measured fallback. Never seed a merely held/unobserved pose.
                let measuredFallback = bridged != nil || usedCoarseFloorlessPose
                if measuredFallback && hypothesisTracking != nil && !groundMotionMeasured
                    // A trusted coordinate seed has no pixel measurement yet.
                    // Keep its new reference aligned with this frame's motion,
                    // just as for an explicitly unverified ground solve.
                    // Once ground texture carries provisional relative motion,
                    // do not overwrite that independent trajectory with the
                    // same coarse pose on every frame. Its absolute origin
                    // stays untrusted until a persistent/global match wins.
                    && hypothesisTracking?.provisionalContinuityActive != true
                    && !groundPlacementRecovery.retainsCandidate(at: snapshot.timestamp) {
                    let accepted = bridged?.cameraPosition ?? accumulator.position
                    groundHypothesisTracker.reanchorLocalCamera(
                        to: CGPoint(
                            x: accepted.x * latestPresentationScale,
                            y: accepted.y * latestPresentationScale
                        ),
                        frame: snapshot.presentationFrame,
                        floorLines: cameraFloorLines,
                        timestamp: snapshot.timestamp,
                        sourceLuma: groundAnalysis?.sourceLuma,
                        protectedOcclusions: trackerForegroundRects,
                        allowsProvisionalPromotion: GroundFallbackPromotionPolicy.allowsRecovery(
                            coarsePoseAccepted: usedCoarseFloorlessPose,
                            maskedConfidence: bridged?.confidence
                        )
                    )
                }
            }
            let recordedPoseSource: String
            if groundAnchoredPose {
                recordedPoseSource = "ground"
            } else if usedCoarseFloorlessPose {
                recordedPoseSource = "coarseMotion"
            } else if transitionBridgePose {
                recordedPoseSource = bridgeResultForRecording?.source == .calibratedScene
                    ? "calibratedScene" : "motionBridge"
            } else if fallPredictionForRecording != nil {
                recordedPoseSource = "fallPrediction"
            } else if cameraUpdate?.state == .accepted {
                recordedPoseSource = "unconfirmedGround"
            } else {
                recordedPoseSource = "held"
            }
            if groundMotionMeasured || (bridgeResultForRecording?.source == .calibratedScene
                                        && bridgeResultForRecording?.verticalMeasured == true) {
                visualFallMotion.observeMeasured(cameraY: worldCameraPosition.y, knight: regions?.knight,
                    timestamp: snapshot.timestamp,
                    frameSize: CGSize(width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height),
                    solveWidth: snapshot.solveWidth)
            }
            // Broad image registration has no ground-plane scale proof. Retain
            // its pose visibly as provisional; only observed ground or calibrated
            // scenery can continue rigid 2D ownership through floor loss.
            let islandMeasured = groundMotionMeasured
                || (!usedCoarseFloorlessPose && transitionBridgePose
                    && bridgeResultForRecording?.horizontalMeasured == true
                    && bridgeResultForRecording?.verticalMeasured == true)
            let islandReference = groundMotionMeasured ? hypothesisTracking?.localReferenceTimestamp
                : (usedCoarseFloorlessPose ? snapshot.room.coarseReferenceTimestamp
                    : bridgeResultForRecording?.referenceTimestamp)
            let islandBreak = hypothesisTracking?.globalCorrection.flatMap {
                hypot($0.dx, $0.dy) > 3 ? "ground-global-adjustment" : nil
            } ?? (usedCoarseFloorlessPose && snapshot.room.coarsePlaceMatch != nil
                ? "coarse-place-reacquisition" : nil)
            islandCapture = trackingIslandCoordinator.observe(timestamp: snapshot.timestamp,
                roomID: snapshot.room.roomID, worldPose: worldCameraPosition,
                measured: islandMeasured, referenceTimestamp: islandReference,
                discontinuity: islandBreak)
            let pairedRun = botDemonstrations.pairedRecordingID
            let botFrameDue = (pairedRun != nil || !isHackerWorkspaceActive())
                && snapshot.timestamp - lastBotObservationTimestamp >= BotObservationContract.sampleInterval - 0.005
            var botWalls: [DetectedWall]?
            var contactWall: BotWallSegment?
            // Only detector evidence accumulates in a path chart. Walked spans
            // retain their island owners and are reprojected as a whole.
            let observedFloorEdges = botDetectedLines
            var pathFloors = observedFloorEdges
            if let groundAnalysis {
                var terrain = TerrainWallDetector.detect(groundAnalysis, floors: semanticFloorLines)
                if snapshot.resumedAfterTransition || islandBreak != nil { knightContactTracker = .init() }
                let scale = CGFloat(groundAnalysis.width) / snapshot.solveWidth
                let pixelCamera = CGPoint(x: worldCameraPosition.x * scale, y: worldCameraPosition.y * scale)
                let actor = knightFootLocator.locate(groundAnalysis, near: observedPlayerBounds ?? regions?.knight,
                    direction: snapshot.horizontalInput?.rawValue ?? 0, time: snapshot.timestamp,
                    room: snapshot.room.roomID, generation: snapshot.generation)
                let support = knightContactTracker.observe(foot: actor?.foot, camera: islandMeasured ? pixelCamera : nil,
                    direction: snapshot.horizontalInput?.rawValue ?? 0, time: snapshot.timestamp,
                    room: snapshot.room.roomID, generation: snapshot.generation, width: groundAnalysis.width,
                    height: groundAnalysis.height, walls: terrain.walls, partial: actor?.partial == true,
                    jumpHeld: snapshot.jumpHeld, measuredIsland: islandCapture?.id)
                terrain.apply(support, floors: semanticFloorLines, analysis: groundAnalysis)
                if let wall = support.wall {
                    contactWall = BotWallEvidence.segments(from: [wall], height: groundAnalysis.height).first
                }
                if let walk = terrain.walkedFloor, CGFloat(walk.xRange.count) >= clearance,
                   let direction = snapshot.horizontalInput, let scenery = snapshot.room.coarseMotionEstimate,
                   scenery.direction == direction, scenery.confidence >= 0.8,
                   scenery.screenShift * direction.rawValue < 0 {
                    verifiedBootstrapWalk = direction
                }
                botWalls = terrain.walls
                var visibleWalk = [CleanFloorLine]()
                if let capture = islandCapture {
                    walkedGroundMemory.record(terrain.walkedFloor, capture: capture, pixelsPerSolve: scale, height: groundAnalysis.height)
                    visibleWalk = walkedGroundMemory.visible(in: capture, projection: trackingIslandProjection, pixelsPerSolve: scale,
                        width: groundAnalysis.width, height: groundAnalysis.height)
                }
                if !visibleWalk.isEmpty {
                    let coverage = WalkedFloorCoverage(floors: visibleWalk)
                    semanticFloorLines = coverage.filtered(semanticFloorLines, clearance: clearance, keepMatchingRows: 2)
                    hypothesisTracking?.detectedFloorLines = semanticFloorLines
                    let cleaned = GroundStrokeGeometry.coalescedFloors(coverage.filtered(semanticFloorLines, clearance: clearance) + visibleWalk,
                        frameWidth: groundAnalysis.width)
                    botDetectedLines = cleaned.map { line in
                        BotTerrainSegment(minimumX: Double(line.xRange.lowerBound),
                            maximumX: Double(line.xRange.upperBound+1),
                            y: Double(groundAnalysis.height-line.row), confidence: 1)
                    }
                    terrain.supportedFloor = nil // The walked layer owns these columns.
                }
                if shouldShowGroundHypotheses {
                    let review = TerrainReviewFrame(detection: terrain, time: snapshot.timestamp, generation: snapshot.generation,
                        roomID: snapshot.room.roomID, camera: worldCameraPosition, islandID: islandCapture?.id, walked: visibleWalk)
                    DispatchQueue.main.async { [weak self] in
                        guard self?.currentStatusGeneration == review.generation else { return }
                        self?.terrainReviewFrame = review
                    }
                }
            }
            let auditDue = snapshot.timestamp - lastBotAuditTimestamp >= BotObservationContract.sampleInterval - 0.005
            if botFrameDue || auditDue {
                func stitch(_ floors: [BotTerrainSegment]) -> [BotTerrainSegment] {
                    BotScreenTerrain.stitchedFloors(floors, walls: botWalls ?? [],
                        frameHeight: snapshot.presentationFrame.height, knightHeight: max(4, Int(clearance.rounded())),
                        knightWidth: max(2, Int((observedPlayerBounds?.width ?? clearance / 2).rounded())),
                        surfaces: rawSemanticFloorLines)
                }
                let sameFloors = pathFloors == botDetectedLines
                botDetectedLines = stitch(botDetectedLines)
                if botFrameDue { pathFloors = sameFloors ? botDetectedLines : stitch(pathFloors) }
            }
            if auditDue {
                lastBotAuditTimestamp = snapshot.timestamp
                let reference = botHumanGround.auditProjection(timestamp: snapshot.timestamp, generation: snapshot.generation,
                    frameSize: CGSize(width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height))
                let audit = BotTerrainAuditFrame(captureTimestamp: snapshot.timestamp, capturedAt: snapshot.capturedAt,
                    captureGeneration: snapshot.generation, width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height,
                    sceneName: reference.scene, referenceAvailable: reference.available, detected: botDetectedLines, authored: reference.lines)
                botDemonstrations.recordTerrainAudit(audit)
                botEvaluation.audit(audit, image: snapshot.presentationFrame, solveWidth: Double(snapshot.solveWidth))
            }
            if botFrameDue {
                lastBotObservationTimestamp = snapshot.timestamp
                if botObservationRoomID != snapshot.room.roomID {
                    botObservationRoomID = snapshot.room.roomID
                    botObservationRoomStartedAt = snapshot.timestamp
                }
                objectInferenceStateLock.lock()
                let botBatch = botDetectionHistory.last {
                    $0.captureGeneration == snapshot.generation && $0.sourceTimestamp <= snapshot.timestamp
                        && $0.sourceTimestamp >= botObservationRoomStartedAt
                }
                objectInferenceStateLock.unlock()
                let lines = botDetectedLines
                let frameSize = CGSize(width: snapshot.presentationFrame.width, height: snapshot.presentationFrame.height)
                let observation = BotObservationBuilder.make(captureTimestamp: snapshot.timestamp,
                    capturedAt: snapshot.capturedAt, availableAt: gameplayGym ? snapshot.capturedAt : ProcessInfo.processInfo.systemUptime,
                    generation: snapshot.generation, roomID: snapshot.room.roomID, roomRevision: snapshot.room.revision,
                    frameSize: frameSize,
                    detections: botBatch, terrain: lines,
                    terrainValid: comparisonAnalysis != nil,
                    transitioning: snapshot.room.isTransitioning,
                    visualCameraPosition: hypothesisTracking.flatMap { $0.poseVerified ? $0.cameraPosition : nil })
                groundTruthCameraStateLock.lock()
                let blueSample = latestGroundTruthCameraSample
                groundTruthCameraStateLock.unlock()
                var observationWithOutcomes = observation
                if let (sample, observedAt) = blueSample,
                   (0...0.2).contains(snapshot.capturedAt - observedAt) {
                    observationWithOutcomes.blueHealthOutcomes = sample.blueHealthOutcomes
                }
                let health = snapshot.hudStencil.flatMap { stencil -> Int? in
                    guard !stencil.holdsHealthMask, snapshot.timestamp - stencil.sourceTimestamp <= 0.18,
                          !stencil.health.isEmpty else { return nil }
                    return stencil.health.filter { $0.kind == "full" || $0.kind == "lifeblood" }.count
                }
                let visualDoors: [BotDoorSegment]
                if let islandCapture, islandCapture.roomID == snapshot.room.roomID, islandCapture.measured,
                   islandCapture.placement == .reference || islandCapture.placement == .connected {
                    visualDoors = BotVisualPortalProjection.doors(snapshot.room.portals, roomID: snapshot.room.roomID,
                        camera: islandCapture.worldPose, solveWidth: snapshot.solveWidth, frameSize: frameSize)
                } else { visualDoors = [] }
                publishBotObservation(observationWithOutcomes, image: snapshot.presentationFrame, terrain: lines, walls: botWalls,
                    pairedRun: pairedRun, atlasIsland: islandCapture, solveWidth: snapshot.solveWidth, health: health,
                    drive: snapshot.horizontalInput?.rawValue ?? 0, detections: botBatch, pathFloors: pathFloors,
                    observedFloorEdges: observedFloorEdges, walkedGround: walkedGroundMemory, contactWall: contactWall,
                    visualDoors: visualDoors)
            }
            observedFloorCandidates = LiveObservedFloorCandidates.captured(detected: semanticFloorLines,
                frameSize: CGSize(width: snapshot.presentationFrame.width,
                                  height: snapshot.presentationFrame.height),
                solveWidth: snapshot.solveWidth, measured: islandMeasured)
            capturedGroundGeometry = LiveWorldGroundGeometry.captured(
                reviews: hypothesisTracking?.lineReviews ?? [], detected: semanticFloorLines,
                frameSize: CGSize(width: snapshot.presentationFrame.width,
                                  height: snapshot.presentationFrame.height),
                solveWidth: snapshot.solveWidth, measured: islandMeasured,
                walkedIslands: islandMeasured ? walkedGroundMemory.pending(in: snapshot.room.roomID) : [])
            inputPathRecorder.recordTracking(
                hypothesisTracking ?? .empty,
                captureTimestamp: snapshot.timestamp,
                groundTrackingMilliseconds: groundTrackingSeconds * 1_000,
                publishedCameraPosition: islandCapture?.worldPose ?? worldCameraPosition,
                poseSource: recordedPoseSource,
                motionBridgeConfidence: bridgeResultForRecording.map {
                    Double($0.confidence)
                } ?? (usedCoarseFloorlessPose
                    ? snapshot.room.coarseMotionEstimate?.confidence : nil),
                motionBridgeSource: bridgeResultForRecording?.source.rawValue
                    ?? (usedCoarseFloorlessPose ? "coarse64x36" : nil),
                signalMeanPeak: snapshot.signalProfile.meanPeak,
                signalVisibleFraction: snapshot.signalProfile.visibleFraction,
                roomID: snapshot.room.roomID,
                roomRevision: snapshot.room.revision,
                // Hacker enables the marker dynamically even when the app was
                // not launched with --render-frame-marker. Decode every frame;
                // ordinary captures simply return nil.
                renderedGameFrame: groundAnalysis.flatMap {
                    RenderedFrameMarker.decode($0)
                },
                placementRecoveryState: groundPlacementRecovery.state,
                floorlessCoarsePosition: floorlessCoarseForRecording,
                floorlessMaskedPosition: floorlessMaskedForRecording,
                floorlessExpectedDirection: floorlessDirectionForRecording,
                floorlessSelectionSource: floorlessSelectionForRecording,
                island: islandCapture,
                sceneMotion: sceneMotion,
                sceneMotionMilliseconds: sceneMotionSeconds * 1_000,
                fallPrediction: fallPredictionForRecording,
                observedAt: visualReplayMode ? snapshot.capturedAt : ProcessInfo.processInfo.systemUptime
            )
            if shouldShowGroundEdge || shouldShowGroundDetect || shouldShowGroundStage
                || shouldShowCleaned
                || shouldShowGroundHypotheses {
                let edgeImage = shouldShowGroundEdge
                    ? groundAnalysis.flatMap(GroundLineDetector.edgeImage)
                    : nil
                let detectImage = shouldShowGroundDetect
                    ? comparisonAnalysis.flatMap(GroundLineDetector.groundDetectImage)
                    : nil
                let groundStageImage = shouldShowGroundStage
                    ? semanticTheoryAnalysis.flatMap(GroundLineDetector.groundStageImage)
                    : nil
                let cleanedImage = shouldShowCleaned
                    ? GroundLineDetector.cleanedImage(
                        from: semanticFloorLines,
                        width: snapshot.presentationFrame.width,
                        height: snapshot.presentationFrame.height
                    )
                    : nil
                DispatchQueue.main.async { [weak self] in
                    guard self?.currentStatusGeneration == snapshot.generation else { return }
                    if self?.groundEdgeEnabled == true {
                        self?.groundEdgeImage = edgeImage
                    }
                    if self?.groundDetectEnabled == true {
                        self?.groundDetectImage = detectImage
                    }
                    if self?.groundStageEnabled == true {
                        self?.groundStageImage = groundStageImage
                    }
                    if self?.cleanedEnabled == true {
                        self?.cleanedImage = cleanedImage

                    }
                    self?.groundHypothesisTracking = hypothesisTracking ?? .empty
                    if hypothesisTracking != nil {
                        self?.groundHypothesisImage = self?.groundHypothesesEnabled == true
                            ? hypothesisImage : nil
                    }
                }
            }
            // A physics-only display prediction must not seed the retained
            // ground-reference cache, even though its display step is accepted.
            if cameraUpdate?.state == .accepted, fallPredictionForRecording == nil {
                let ground = groundReferenceTracker.observe(
                    frame: snapshot.presentationFrame,
                    cameraPosition: worldCameraPosition,
                    solveWidth: snapshot.solveWidth,
                    knightRect: regions?.knight,
                    excluding: groundForegroundRects,
                    edgeAnalysis: groundAnalysis,
                    comparisonAnalysis: comparisonAnalysis,
                    tuning: tuning
                )
                groundForFrame = ground
                DispatchQueue.main.async { [weak self] in
                    self?.groundReference = ground
                }
            }
        } else {
            islandCapture = trackingIslandCoordinator.observe(timestamp: snapshot.timestamp,
                roomID: snapshot.room.roomID, worldPose: worldCameraPosition,
                measured: false, referenceTimestamp: nil)
            if let islandCapture {
                recordFloorlessTracking(snapshot, pose: worldCameraPosition,
                    island: islandCapture, source: "held")
            }
            groundHypothesisTracker.reset(keepingGlobalFeatures: true)
            transitionMotionBridge.suspendForTransition()
            calibratedSceneMotion.reset()
            visualFallMotion.reset()
            groundPlacementRecovery.missingObservation()
            DispatchQueue.main.async { [weak self] in
                self?.groundHypothesisImage = nil
                self?.groundHypothesisTracking = .empty
                self?.terrainReviewFrame = nil
            }
            if registrationContinuity.shouldEnterRecovery(
                after: nil, hasGameplaySignal: false
            ) {
                enterRecoveryIfNeeded()
            }
        }

        let groundAlignmentExclusions = GroundFeatureEligibility.alignmentExclusions(
            frameSize: CGSize(
                width: snapshot.presentationFrame.width,
                height: snapshot.presentationFrame.height
            ),
            cameraPosition: worldCameraPosition,
            solveWidth: snapshot.solveWidth,
            ground: groundForFrame,
            existing: regions?.omittedRects ?? []
        )
        let roomCompositionOmittedRects = VisualRoomCompositionMask.omittedRects(
            bounds: snapshot.room.compositionBounds,
            cameraPosition: worldCameraPosition,
            solveWidth: snapshot.solveWidth,
            frameSize: CGSize(
                width: snapshot.presentationFrame.width,
                height: snapshot.presentationFrame.height
            )
        )
        let transitionEdgeOmittedRects = transitionBridgePose
            ? snapshot.signalProfile.edgeBlackBands?.rectangularOmissions(
                frameSize: CGSize(
                    width: snapshot.presentationFrame.width,
                    height: snapshot.presentationFrame.height
                )
            ) ?? []
            : []

        let completion = visionFramePump.complete(ticket)
        if completion.shouldAcceptResult {
            if cameraUpdate?.state == .accepted,
               groundPlacementRecovery.allowsAtlasWrite,
               worldBasis.trackingState == .recovering,
               !atlasPipeline.hasCommittedWorldEvidence,
               let basisTicket,
               let tentativeWorldPose = worldBasis.rebasedWorldPose(for: basisTicket) {
                _ = worldBasis.confirmPlacement(
                    for: basisTicket,
                    matchedWorldPose: tentativeWorldPose
                )
                DispatchQueue.main.async { [weak self] in self?.localizationStatus = "Tracking" }
            }
            let worldLocated = (groundAnchoredPose || transitionBridgePose)
                && LiveRegistrationAdmission.canWriteAtlas(
                cameraUpdate,
                worldState: worldBasis.trackingState
            )
            let bootstrapReady = freshAtlasBootstrapGate.allowsFirstAtlasWrite(
                position: worldCameraPosition,
                registrationAccepted: cameraUpdate?.state == .accepted,
                groundVerified: groundAnchoredPose,
                hasCommittedEvidence: atlasPipeline.hasCommittedWorldEvidence,
                verifiedWalk: verifiedBootstrapWalk
            )
            let allowAtlasWrite = worldLocated && bootstrapReady && groundPlacementRecovery.allowsAtlasWrite
                && !snapshot.room.isTransitioning
            inputPathRecorder.recordAtlasAdmission(captureTimestamp: snapshot.timestamp,
                allowed: snapshot.hasGameplaySignal && allowAtlasWrite && regions != nil && basisTicket != nil)
            if lastAtlasAdmissionDiagnosticAt.map({ snapshot.timestamp - $0 >= 1 }) ?? true {
                lastAtlasAdmissionDiagnosticAt = snapshot.timestamp
                let registration = String(describing: cameraUpdate?.state)
                let placement = String(describing: worldBasis.trackingState)
                atlasAdmissionLog.info(
                    "gameplaySignal=\(snapshot.hasGameplaySignal, privacy: .public) registration=\(registration, privacy: .public) placement=\(placement, privacy: .public) atlasWrite=\(allowAtlasWrite, privacy: .public) bootstrap=\(self.freshAtlasBootstrapGate.stableSampleCount, privacy: .public) regions=\(regions != nil, privacy: .public) basisTicket=\(basisTicket != nil, privacy: .public) localX=\(Double(self.accumulator.position.x), privacy: .public) worldX=\(Double(worldCameraPosition.x), privacy: .public) generation=\(snapshot.generation, privacy: .public)"
                )
            }
            trackingStateLock.lock()
            let priorTrackingState = trackingState
            let cameraVelocity = PresentationPosePrediction.velocity(
                from: priorTrackingState.cameraPosition,
                at: priorTrackingState.poseTimestamp,
                to: worldCameraPosition,
                at: snapshot.timestamp,
                continuous: priorTrackingState.generation == snapshot.generation
                    && priorTrackingState.groundAnchored == true && groundMotionMeasured
                    && !hadGlobalCorrection
            )
            let hasMeasuredGroundVelocity = groundMotionMeasured
                && priorTrackingState.groundAnchored == true
                && hypot(cameraVelocity.dx, cameraVelocity.dy) >= 5
            trackingState = TrackingState(
                cameraPosition: worldCameraPosition,
                regions: regions,
                poseTimestamp: worldLocated ? snapshot.timestamp : nil,
                basisTicket: basisTicket,
                groundAnchored: groundMotionMeasured,
                gameplayEnabled: true,
                generation: snapshot.generation,
                cameraVelocity: cameraVelocity,
                reliableGroundCameraVelocity: hasMeasuredGroundVelocity
                    ? cameraVelocity
                    : priorTrackingState.reliableGroundCameraVelocity,
                reliableGroundVelocityTimestamp: hasMeasuredGroundVelocity
                    ? snapshot.timestamp
                    : priorTrackingState.reliableGroundVelocityTimestamp,
                islandCapture: islandCapture
            )
            trackingStateLock.unlock()
            if worldLocated {
                // Registration is now published. Landmark work runs independently;
                // its global correction may arrive after newer registrations, but
                // it can only move this capture generation once in timestamp order.
                featureRefinement.didPublishRegistration(request)
            }
            // Atlas evidence is isolated from the display pump. A slow or
            // delayed Vision result may enrich the atlas but cannot replace
            // the most recent captured window on screen.
            if snapshot.hasGameplaySignal, allowAtlasWrite, let regions, let basisTicket {
                atlasPipeline.submitAtlasObservation(
                    frame: snapshot.presentationFrame,
                    regions: regions,
                    cameraPosition: worldCameraPosition,
                    solveWidth: snapshot.solveWidth,
                    timestamp: snapshot.timestamp,
                    observationPoseTimestamp: snapshot.timestamp,
                    hasGameplaySignal: true,
                    allowAtlasWrite: true,
                    captureGeneration: snapshot.generation,
                    worldTimestamp: snapshot.worldTimestamp,
                    submittedAt: visualReplayMode ? snapshot.timestamp : ProcessInfo.processInfo.systemUptime,
                    basisTicket: basisTicket,
                    groundAnchoredPose: groundAnchoredPose,
                    transitionBridgePose: transitionBridgePose,
                    alignmentExclusions: groundAlignmentExclusions,
                    compositionOmittedRects: roomCompositionOmittedRects
                        + transitionEdgeOmittedRects,
                    roomID: snapshot.room.roomID,
                    islandCapture: islandCapture,
                    groundGeometry: capturedGroundGeometry,
                    observedFloorCandidates: observedFloorCandidates,
                    observedPlayerBounds: observedPlayerBounds,
                    doorwayTransition: snapshot.room.doorwayTransition
                )
            }
            // GroundHypothesisTracker owns feature tracking and loop matching.
            // Do not launch the obsolete full-image landmark worker per frame.
        }
        if let next = completion.next {
            visionQueue.async { [weak self] in self?.processVision(next) }
        }
    }

    /// Keep error/coverage diagnostics through blackouts and pending doors.
    /// Absence of a ground solve is explicit; it must not erase the difficult
    /// portion of a replay's camera-error trace.
    private func recordFloorlessTracking(_ frame: VisionFrame, pose: CGPoint,
                                        island: LiveIslandCapture, source: String) {
        inputPathRecorder.recordTracking(.empty, captureTimestamp: frame.timestamp,
            publishedCameraPosition: island.worldPose, poseSource: source,
            motionBridgeConfidence: frame.room.coarseMotionEstimate?.confidence,
            motionBridgeSource: frame.room.coarseMotionIsControlling ? "coarse64x36" : nil,
            signalMeanPeak: frame.signalProfile.meanPeak,
            signalVisibleFraction: frame.signalProfile.visibleFraction,
            roomID: frame.room.roomID, roomRevision: frame.room.revision,
            renderedGameFrame: RenderedFrameMarker.decode(frame.presentationFrame),
            island: island,
            observedAt: visualReplayMode ? frame.capturedAt : ProcessInfo.processInfo.systemUptime)
        inputPathRecorder.recordAtlasAdmission(captureTimestamp: frame.timestamp, allowed: false)
    }

    private func currentTrackingState(for generation: UInt64) -> TrackingState {
        trackingStateLock.lock()
        defer { trackingStateLock.unlock() }
        guard trackingState.generation == generation else {
            return TrackingState(
                cameraPosition: .zero,
                regions: nil,
                poseTimestamp: nil,
                basisTicket: nil,
                groundAnchored: nil,
                gameplayEnabled: false,
                generation: generation
            )
        }
        return trackingState
    }

    private func enqueueObjectInference(_ frame: ObjectInferenceFrame) {
        objectInferenceStateLock.lock()
        let epoch = objectModelsLoaded && !objectInferenceSuspended
            ? objectInferenceEpoch
            : nil
        objectInferenceStateLock.unlock()
        guard let epoch,
              let ticket = objectInferencePump.submit(frame, epoch: epoch) else { return }
        objectInferenceQueue.async { [weak self] in
            self?.processObjectInference(ticket)
        }
    }

    private func processObjectInference(
        _ ticket: LatestFramePump<ObjectInferenceFrame>.Ticket
    ) {
        let frame = ticket.item
        let startedAt = ProcessInfo.processInfo.systemUptime
        let result: Result<[LiveObjectDetection], Error>
        do {
            result = .success(try objectDetector.detect(
                in: frame.rawImage,
                sourceFrameIdentifier: frame.sourceFrameIdentifier
            ))
        } catch {
            result = .failure(error)
        }
        let modelDuration = ProcessInfo.processInfo.systemUptime - startedAt
        telemetry.record(.objectModel, seconds: modelDuration)
        let completion = objectInferencePump.complete(ticket)

        if completion.shouldAcceptResult {
            let refinement: LiveObjectPixelRefinement?
            let refinementStartedAt = ProcessInfo.processInfo.systemUptime
            if case .success(let detections) = result {
                refinement = LiveObjectPixelRefiner.refine(
                    detections,
                    in: frame.rawImage,
                    createsDiagnosticImage: frame.createsPixelDiagnostic
                )
            } else {
                refinement = nil
            }
            telemetry.record(
                .objectPixelRefinement,
                seconds: ProcessInfo.processInfo.systemUptime - refinementStartedAt
            )
            var publishedDetections = [LiveObjectDetection]()
            let trackingStartedAt = ProcessInfo.processInfo.systemUptime
            objectInferenceStateLock.lock()
            let canCommit = ticket.epoch == objectInferenceEpoch && !objectInferenceSuspended
            if canCommit,
               case .success(let rawDetections) = result,
               let refinement {
                publishedDetections = objectDetectionTracker.update(
                    detections: refinement.detections,
                    captureGeneration: frame.captureGeneration
                )
                let signs = publishedDetections.filter {
                    $0.classIdentifier == "world.sign" && $0.sourceFrameIdentifier == frame.sourceFrameIdentifier
                }
                if !signs.isEmpty {
                    latestSignObservation = SignObservation(image: frame.rawImage, detections: signs, room: frame.room,
                        capturedAt: frame.capturedAt, generation: frame.captureGeneration, sourceID: frame.sourceFrameIdentifier)
                } else if let prior = latestSignObservation, frame.capturedAt - prior.capturedAt > 1.5 {
                    latestSignObservation = nil
                }
                latestObjectDetectionBatch = LiveObjectDetectionBatch(
                    detections: publishedDetections,
                    sourceFrameIdentifier: frame.sourceFrameIdentifier,
                    sourceTimestamp: frame.timestamp,
                    captureGeneration: frame.captureGeneration,
                    inferenceDuration: modelDuration
                )
                previousMaskDetectionBatch = latestMaskDetectionBatch
                latestMaskDetectionBatch = LiveObjectDetectionBatch(
                    detections: refinement.detections,
                    sourceFrameIdentifier: frame.sourceFrameIdentifier,
                    sourceTimestamp: frame.timestamp,
                    captureGeneration: frame.captureGeneration,
                    inferenceDuration: modelDuration
                )
                if let batch = latestMaskDetectionBatch {
                    botDetectionHistory.append(batch)
                    if botDetectionHistory.count > 16 { botDetectionHistory.removeFirst(botDetectionHistory.count - 16) }
                }
                latestRawObjectDetectionBatch = LiveObjectDetectionBatch(
                    detections: rawDetections,
                    sourceFrameIdentifier: frame.sourceFrameIdentifier,
                    sourceTimestamp: frame.timestamp,
                    captureGeneration: frame.captureGeneration,
                    inferenceDuration: modelDuration
                )
            }
            objectInferenceStateLock.unlock()
            telemetry.record(
                .objectTracking,
                seconds: ProcessInfo.processInfo.systemUptime - trackingStartedAt
            )
            let totalDuration = ProcessInfo.processInfo.systemUptime - startedAt

            if canCommit {
                capturedFrameStateLock.lock()
                recentCapturedFrameBuffer.replaceDetections(
                    publishedDetections,
                    captureGeneration: frame.captureGeneration,
                    sourceFrameIdentifier: frame.sourceFrameIdentifier
                )
                capturedFrameStateLock.unlock()
                DispatchQueue.main.async { [weak self] in
                    guard let self, self.labelImage == nil else { return }
                    switch result {
                    case .success:
                        if self.liveObjectDetectionCount != publishedDetections.count {
                            self.liveObjectDetectionCount = publishedDetections.count
                        }
                        let identifiers = Array(Set(
                            publishedDetections.map(\.classIdentifier)
                        )).sorted()
                        if self.liveObjectDetectionClassIdentifiers != identifiers {
                            self.liveObjectDetectionClassIdentifiers = identifiers
                        }
                        self.objectInferenceMilliseconds = totalDuration * 1_000
                        if self.objectInferenceIssue != nil { self.objectInferenceIssue = nil }
                        if refinement?.diagnosticImage != nil || self.objectDetectionPixelImage != nil {
                            self.objectDetectionPixelImage = refinement?.diagnosticImage
                        }
                    case .failure(let error):
                        self.objectInferenceIssue = "Inference failed: \(error.localizedDescription)"
                    }
                }
            }
        }
        if let next = completion.next {
            objectInferenceQueue.async { [weak self] in
                self?.processObjectInference(next)
            }
        }
    }

    private func currentMaskDetections(captureGeneration: UInt64, timestamp: Double) -> [LiveObjectDetection] {
        objectInferenceStateLock.lock()
        defer { objectInferenceStateLock.unlock() }
        guard !objectInferenceSuspended else { return [] }
        let displayed = LiveObjectDetectionFreshness.detections(from: latestObjectDetectionBatch,
            captureGeneration: captureGeneration, timestamp: timestamp)
        return MovingObjectMask.detections(displayed: displayed,
            current: latestMaskDetectionBatch, previous: previousMaskDetectionBatch,
            generation: captureGeneration, timestamp: timestamp)
    }

    private func currentObjectDetections(
        captureGeneration: UInt64,
        timestamp: Double
    ) -> [LiveObjectDetection] {
        objectInferenceStateLock.lock()
        defer { objectInferenceStateLock.unlock() }
        guard !objectInferenceSuspended else { return [] }
        return LiveObjectDetectionFreshness.detections(
            from: latestObjectDetectionBatch,
            captureGeneration: captureGeneration,
            timestamp: timestamp
        )
    }

    private func currentObjectDetectionOverlay(
        captureGeneration: UInt64,
        timestamp: Double,
        tracked: [LiveObjectDetection]
    ) -> [LiveObjectDetection] {
        guard objectDetectionRawEnabled else { return tracked }
        objectInferenceStateLock.lock()
        defer { objectInferenceStateLock.unlock() }
        return LiveObjectDetectionFreshness.detections(
            from: latestRawObjectDetectionBatch,
            captureGeneration: captureGeneration,
            timestamp: timestamp
        )
    }

    private func currentObjectDetectionIcons() -> [String: CGImage] {
        objectInferenceStateLock.lock()
        defer { objectInferenceStateLock.unlock() }
        return objectDetectionIcons
    }

    private func invalidateObjectInferenceResults() {
        objectInferenceStateLock.lock()
        objectInferenceEpoch &+= 1
        let epoch = objectInferenceEpoch
        latestSignObservation = nil
        latestObjectDetectionBatch = nil
        latestRawObjectDetectionBatch = nil
        latestMaskDetectionBatch = nil
        previousMaskDetectionBatch = nil
        objectDetectionTracker.reset()
        objectInferenceStateLock.unlock()
        objectInferencePump.invalidate(epoch: epoch)
        DispatchQueue.main.async { [weak self] in
            self?.liveObjectDetectionCount = 0
            self?.liveObjectDetectionClassIdentifiers = []
            self?.objectInferenceMilliseconds = nil
            self?.objectDetectionPixelImage = nil
        }
    }

    private func setObjectInferenceSuspended(_ suspended: Bool) {
        objectInferenceStateLock.lock()
        guard objectInferenceSuspended != suspended else {
            objectInferenceStateLock.unlock()
            return
        }
        objectInferenceSuspended = suspended
        objectInferenceEpoch &+= 1
        let epoch = objectInferenceEpoch
        latestSignObservation = nil
        latestObjectDetectionBatch = nil
        latestRawObjectDetectionBatch = nil
        latestMaskDetectionBatch = nil
        previousMaskDetectionBatch = nil
        objectDetectionTracker.reset()
        objectInferenceStateLock.unlock()
        objectInferencePump.invalidate(epoch: epoch)
        liveObjectDetectionCount = 0
        liveObjectDetectionClassIdentifiers = []
        objectInferenceMilliseconds = nil
        objectDetectionPixelImage = nil
    }

    private func objectDisplayName(_ classIdentifier: String) -> String {
        if classIdentifier == LabelingModelIdentity.sharedObjectModel {
            return LabelingModelIdentity.sharedObjectModelName
        }
        return LabelingContext.allCases.flatMap(\.labels).first {
            $0.id == classIdentifier
        }?.name ?? classIdentifier
    }

    private func enqueue(_ frame: RenderFrame) {
        // Gameplay tracking and atlas integration have their own workers.
        // Its hidden preview must not contend with Hacker's live image for
        // GPU readback or trigger a second set of SwiftUI frame publications.
        guard !isHackerWorkspaceActive() else { return }
        guard let ticket = framePump.submit(frame, epoch: frame.generation) else { return }
        renderQueue.async { [weak self] in self?.render(ticket) }
    }

    private func render(_ ticket: LatestFramePump<RenderFrame>.Ticket) {
        let snapshot = ticket.item
        let startedAt = ProcessInfo.processInfo.systemUptime
        telemetry.record(.renderQueueWait, seconds: startedAt - snapshot.enqueuedAt)
        defer {
            telemetry.record(.render, seconds: ProcessInfo.processInfo.systemUptime - startedAt)
            let completion = framePump.complete(ticket)
            if let next = completion.next {
                renderQueue.async { [weak self] in self?.render(next) }
            }
        }
        guard ticket.epoch == renderGeneration, !isHackerWorkspaceActive() else { return }
        guard let output = atlasPipeline.process(
            frame: snapshot.frame,
            regions: snapshot.regions,
            objectDetections: snapshot.objectDetections,
            objectIcons: snapshot.objectIcons,
            cameraPosition: snapshot.cameraPosition,
            solveWidth: snapshot.solveWidth,
            timestamp: snapshot.timestamp,
            observationPoseTimestamp: snapshot.observationPoseTimestamp,
            hasGameplaySignal: snapshot.hasGameplaySignal,
            allowAtlasWrite: snapshot.allowAtlasWrite
        ) else { return }

        let stencilImage = snapshot.showsHUDStencil ? snapshot.hudStencil.flatMap {
            HUDStencilRenderer.overlay($0, width: snapshot.frame.width, height: snapshot.frame.height)
        } : nil
        let menuStencilImage = snapshot.showsMenuStencil
            ? snapshot.menuStencil.flatMap {
                MenuStencilRenderer.overlay(
                    $0,
                    width: snapshot.frame.width,
                    height: snapshot.frame.height
                )
            }
            : nil
        let renderedAt = ProcessInfo.processInfo.systemUptime
        DispatchQueue.main.async { [weak self] in
            guard let self, self.labelImage == nil,
                  snapshot.generation == self.currentStatusGeneration,
                  !self.isHackerWorkspaceActive() else { return }
            self.telemetry.record(
                .mainQueueWait,
                seconds: ProcessInfo.processInfo.systemUptime - renderedAt
            )
            self.telemetry.record(
                .captureToPublish,
                seconds: ProcessInfo.processInfo.systemUptime - snapshot.capturedAt
            )
            if let revision = snapshot.islandWorldRevision,
               revision > (self.liveWorldSnapshot?.mapRevision ?? -1) { return }
            if let island = snapshot.islandCapture, let projection = self.publishedIslandProjection,
               projection.worldPose(for: island) == nil { return }
            // A render may finish between atlas recomposition and publication
            // of its owning graph. Never pair those pixels with an older world.
            if output.worldRevision == self.liveWorldSnapshot?.mapRevision {
                self.publishAtlasTiles(output.atlasTiles)
            }
            self.hudStencilImage = stencilImage
            self.menuStencilImage = menuStencilImage
            LivePresentationTiming.shared.register(output.liveImage, capturedAt: snapshot.timestamp)
            self.liveImage = output.liveImage
            self.presentedIslandFrame = PresentedIslandFrame(rawBounds: output.liveBounds,
                rawFocus: output.focusPoint, rawPose: snapshot.cameraPosition,
                island: snapshot.islandCapture,
                scale: CGFloat(snapshot.frame.width) / snapshot.solveWidth,
                anchor: output.anchorPosition)
            self.reprojectPresentedIslandFrame()
        }
    }

    private func recordDirectPreviewFrame(at timestamp: Double) {
        directPreviewFrames += 1
        guard let start = directPreviewRateWindowStart else {
            directPreviewRateWindowStart = timestamp
            directPreviewFrames = 0
            return
        }
        let elapsed = timestamp - start
        guard elapsed >= 1 else { return }
        let framesPerSecond = Double(directPreviewFrames) / elapsed
        let timing = FramePipelineTelemetry.Stage.allCases.compactMap { stage -> String? in
            guard let summary = telemetry.summary(for: stage) else { return nil }
            let p50 = String(format: "%.1f", summary.p50Milliseconds)
            let p95 = String(format: "%.1f", summary.p95Milliseconds)
            return "\(String(describing: stage)) p50=\(p50)ms p95=\(p95)ms n=\(summary.count)"
        }.joined(separator: " ")
        let pipelines = "vision={\(visionFramePump.statistics.logDescription)} render={\(framePump.statistics.logDescription)} objects={\(objectInferencePump.statistics.logDescription)}"
        performanceLog.notice("frameFlow captureFPS=\(framesPerSecond, privacy: .public) captureComplete=\(self.captureCompleteFrames, privacy: .public) captureIncomplete=\(self.captureIncompleteFrames, privacy: .public) \(pipelines, privacy: .public)")
        performanceLog.notice("captureFPS=\(framesPerSecond, privacy: .public) \(timing, privacy: .public)")
        directPreviewRateWindowStart = timestamp
        directPreviewFrames = 0
    }

    func stream(_ stoppedStream: SCStream, didStopWithError error: Error) {
        let stoppedStreamID = ObjectIdentifier(stoppedStream)
        Task { [weak self] in
            guard let self,
                  await self.invalidateCaptureAttempt(stoppedStreamID) else { return }
            await MainActor.run {
                guard self.stream === stoppedStream else { return }
                self.stream = nil
                self.capturedGameProcessID = nil
                self.isCapturing = false
                self.operationInFlight = false
                if self.wantsCapture {
                    self.status = "Capture interrupted. Reconnecting…"
                    self.scheduleCaptureRestart()
                } else {
                    self.status = "Capture stopped: \(error.localizedDescription)"
                }
            }
        }
    }
}

private enum CaptureFailure: Error {
    case windowNotFound
    case incompatibleGame(String)
}
