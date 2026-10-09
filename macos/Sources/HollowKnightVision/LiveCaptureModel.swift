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
    @Published private(set) var inputControlStatus = "Controls unavailable"
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
    @Published private(set) var hackerRoomOffsets = [UUID: CGPoint]()
    @Published private(set) var hackerAtlasBounds: CGRect?
    @Published private(set) var hackerLiveImage: CGImage?
    @Published private(set) var hackerLiveBounds = CGRect.zero
    @Published private(set) var hackerMapFocus = CGPoint.zero
    @Published private(set) var hackerCameraTransform: GroundTruthCameraTransform?
    private var hackerRoomLayout = HackerRoomLayout.empty
    @Published private(set) var labelImage: CGImage?
    @Published private(set) var liveBounds = CGRect.zero
    @Published private(set) var mapFocus = CGPoint.zero
    @Published private(set) var presentationCorrectionOffset = CGPoint.zero
    @Published var groundEdgeEnabled = false
    @Published private(set) var groundEdgeImage: CGImage?
    @Published var groundDetectEnabled = false
    @Published private(set) var groundDetectImage: CGImage?
    @Published private(set) var groundStageImage: CGImage?
    @Published var cleanedEnabled = false
    @Published private(set) var cleanedImage: CGImage?
    @Published var groundHypothesesEnabled = ProcessInfo.processInfo.arguments.contains("--show-ground-features")
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
    @Published var stencilDetectionEnabled = false
    @Published private(set) var menuStencilImage: CGImage?
    // Owned by sampleQueue. Each result travels with its source capture.
    // No-menu gameplay is the common case. Probe every menu set at 10 Hz;
    // after a scene locks, its single probe returns to capture cadence.
    private let menuStencilTracker = MenuStencilTracker(
        loadsDeployedCatalog: false,
        minimumSearchInterval: 0.1,
        selectorCalibrationURL: MenuStencilCalibration.writableURL(),
        selectorCalibrationMirrorURL: MenuStencilCalibration.projectMirrorURL()
    )
    @Published private(set) var hudStencilImage: CGImage?
    // Owned by sampleQueue. Each result travels with its source capture.
    private let hudStencilTracker = HUDStencilTracker()
    @Published var objectDetectionEnabled = false
    @Published var objectDetectionRawEnabled = false
    @Published var objectDetectionExtension = 0.12
    @Published var objectDetectionPixelsEnabled = false
    @Published private(set) var objectDetectionPixelImage: CGImage?
    private var groundTuning = GroundTheoryTuning.default
    private var semanticGroundTuning = GroundTheoryTuning.semanticDefault
    @Published private(set) var liveWorldReview: LiveWorldTrackerReview?
    @Published private(set) var liveWorldSnapshot: LiveWorldSnapshot?
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
        qos: .userInteractive
    )
    private let menuStencilReloadQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.menu-stencil-reload",
        qos: .utility
    )
    private let renderQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.render",
        qos: .userInitiated
    )
    private let hackerPresentationQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.hacker-presentation",
        qos: .userInteractive
    )
    private let hackerAtlasQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.hacker-atlas",
        qos: .userInitiated
    )
    private let atlasManagementQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.atlas-management",
        qos: .userInitiated
    )
    private let atlasMetadataQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.atlas-metadata",
        qos: .utility
    )
    private let inputPathStoreQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.input-path-store",
        qos: .utility
    )
    private let visionQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.vision",
        qos: .userInitiated
    )
    private let objectInferenceQueue = DispatchQueue(
        label: "com.ballroller.hollow-knight-vision.object-inference",
        qos: .userInitiated
    )
    private let poseImageContext = CIContext(options: [.cacheIntermediates: false])
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
    private let groundHypothesisTracker = GroundHypothesisTracker(
        minimumObservationSeconds: 0.2, backgroundGlobalSearch: true,
        confirmedLossGraceSeconds: 3, productionCameraSolver: true)
    /// Semantic ground uses the tracker pose but never participates in its
    /// camera solve. This lets labeled edge tuning change the green map without
    /// changing the feature anchors that make return tracking stable.
    private let semanticGroundAtlas = GroundSemanticAtlas()
    private let transitionMotionBridge = TransitionMotionBridge()
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
    private let atlasStateStore = AtlasStateStore()
    private let inputPathRecorder = InputPathRecorder()
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
            }
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
    private var latestObjectDetectionBatch: LiveObjectDetectionBatch?
    private var latestRawObjectDetectionBatch: LiveObjectDetectionBatch?
    private var latestMaskDetectionBatch: LiveObjectDetectionBatch?
    private var previousMaskDetectionBatch: LiveObjectDetectionBatch?
    private var objectDetectionTracker = LiveObjectDetectionTracker()
    private var objectDetectionIcons = [String: CGImage]()
    private var gameStatusTracker = LiveGameStatusTracker()
    // Owned by sampleQueue with menuStencilTracker.
    private var gameplayMenuEvidenceGate = GameplayMenuEvidenceGate()
    private var quitGameExitWatchGeneration: UInt64 = 0
    private var quitGameExitApplication: NSRunningApplication?
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
    }

    private struct ObjectInferenceFrame {
        /// Untouched presentation capture. Ground-only intensity correction
        /// is created later on the vision queue and must never enter models.
        let rawImage: CGImage
        let sourceFrameIdentifier: UInt64
        let timestamp: Double
        let captureGeneration: UInt64
        let extensionFraction: CGFloat
        let createsPixelDiagnostic: Bool
    }

    init(autoNavigateToGameplay: Bool = false) {
        self.autoNavigateToGameplay = autoNavigateToGameplay
        if UserDefaults.standard.object(
            forKey: Self.randomizePlayerStateAfterLabelDefaultsKey
        ) != nil {
            randomizePlayerStateAfterLabel = UserDefaults.standard.bool(
                forKey: Self.randomizePlayerStateAfterLabelDefaultsKey
            )
        }
        let worldRootURL = LiveWorldSessionStore.defaultRootURL()
        roomTopologyURL = worldRootURL.appendingPathComponent("visual-rooms.json")
        atlasPipeline = LiveAtlasPipeline(
            context: CIContext(options: [.cacheIntermediates: false]),
            worldRootURL: worldRootURL,
            restoresPersistedWorldOnInitialization: false
        )
        super.init()
        quitGameExitObserver = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            self?.handleTerminatedApplication(notification)
        }
        let restoredRoom = restoreRoomTopology(
            worldSnapshot: atlasPipeline.currentWorldSnapshot()
        )
        atlasPipeline.setRoomCompositionBoundsProvider { [weak visualRoomTracker] roomID in
            visualRoomTracker?.compositionBounds(for: roomID) ?? .unbounded
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
                    self.liveWorldReview = update.review
                    self.liveWorldSnapshot = update.snapshot
                    self.publishAtlasTiles(update.atlasTiles)
                }
                if let closure = update.acceptedClosure {
                    self.inputPathRecorder.recordLoopClosure(
                        update: update,
                        closure: closure
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
        refreshMenuStencilCatalog()
        reloadActiveObjectModels()
        refreshAtlasSavedStates()
    }

    deinit {
        if let quitGameExitObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(quitGameExitObserver)
        }
    }

    func reloadActiveObjectModels() {
        objectInferenceStateLock.lock()
        objectModelConfigurationID &+= 1
        let configurationID = objectModelConfigurationID
        objectInferenceEpoch &+= 1
        let epoch = objectInferenceEpoch
        objectModelsLoaded = false
        latestObjectDetectionBatch = nil
        latestRawObjectDetectionBatch = nil
        latestMaskDetectionBatch = nil
        previousMaskDetectionBatch = nil
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
        status = "Finding the Hollow Knight window…"
        Task { [weak self] in
            guard let self else { return }
            var startingStreamID: ObjectIdentifier?
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
                    throw CaptureFailure.windowNotFound
                }
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
                await MainActor.run { self.stream = stream }
                try await stream.startCapture()
                await MainActor.run {
                    guard self.stream === stream else { return }
                    self.isCapturing = true
                    self.operationInFlight = false
                    self.status = "Live atlas · waiting for camera lock…"
                    // Steam activates the game while launching it. Bring the
                    // ordinary Vision window forward once when capture is
                    // ready so menu review is visible. No floating level or
                    // repeated raise timer is installed.
                    self.positionWindows()
                }
            } catch {
                let attemptedStreamID = startingStreamID
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
                    self.operationInFlight = false
                    self.isCapturing = false
                    self.status = self.message(for: error)
                    if case CaptureFailure.windowNotFound = error {
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

    private func refreshAtlasAutoSaveSize(for summary: AtlasAutoSaveSummary) {
        guard let rootURL = atlasPipeline.activeAutoSaveRootURL else { return }
        atlasMetadataQueue.async { [weak self] in
            guard let self,
                  let bytes = try? AtlasDiskUsage.allocatedByteCount(at: rootURL)
            else { return }
            DispatchQueue.main.async {
                guard self.atlasAutoSave.registrationCount == summary.registrationCount,
                      self.atlasAutoSave.createdAt == summary.createdAt
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
        atlasManagementIssue = nil
        if replacingActiveWorld {
            // Close the capture epoch before touching persistent storage. A
            // Vision result may already be between its initial generation
            // check and atlas submission; invalidating its frame-pump ticket
            // here prevents that old pose becoming registration 1 in the new
            // atlas. Resetting after the disk operation is too late.
            advanceGeneration(resetCamera: true)
        }
        atlasManagementQueue.async { [weak self] in
            guard let self else { return }
            do {
                let loaded = try operation()
                if let loaded {
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
                        self.liveImage = nil
                        self.liveBounds = .zero
                        self.mapFocus = .zero
                        self.presentationCorrectionOffset = .zero
                        self.liveWorldReview = nil
                        self.liveWorldSnapshot = loaded.snapshot
                        self.localFeatureTracking = .empty
                        self.groundReference = nil
                        self.groundEdgeImage = nil
                        self.groundDetectImage = nil
                        self.groundStageImage = nil
                        self.cleanedImage = nil
                        self.groundHypothesisImage = nil
                        self.groundHypothesisTracking = .empty
                        self.publishAtlasTiles(loaded.atlasTiles)
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

    func startGameControls() {
        gameControlForwarder.start()
    }

    /// The sparse world and the room graph share one autosave directory. A
    /// legacy two-room atlas has no graph file, so infer its one left/right
    /// doorway from the first observation in the left room exactly once.
    @discardableResult
    private func restoreRoomTopology(
        worldSnapshot: LiveWorldSnapshot
    ) -> VisualRoomSnapshot {
        let fileManager = FileManager.default
        if fileManager.fileExists(atPath: roomTopologyURL.path),
           var restored = try? visualRoomTracker.restoreTopology(from: roomTopologyURL),
           !restored.portals.isEmpty
                || Set(worldSnapshot.observations.map(\.roomID)).count < 2 {
            let durableRoomIDs = Set(worldSnapshot.observations.map(\.roomID))
            if durableRoomIDs.count == 1,
               !durableRoomIDs.contains(restored.roomID),
               let durableRoomID = durableRoomIDs.first {
                restored = visualRoomTracker.restoreActiveRoomFromDurableEvidence(
                    durableRoomID
                )
            }
            // Restore also rectifies older mismatched door-height diagnostics.
            // Save that normalized geometry once so subsequent launches and
            // atlas composition use the same portal representation.
            persistRoomTopology()
            return publishRoomTopology(restored)
        }

        var room = visualRoomTracker.reset()
        let roomIDs = Set(worldSnapshot.observations.map(\.roomID))
        if roomIDs.count == 2,
           roomIDs == Set([0, 1]),
           let evidence = atlasPipeline.boundaryEvidence(
            leftRoomID: 1, rightRoomID: 0
           ) {
            let layout = VisualRoomPortalLayout.make(
                direction: .left,
                departureWorldPose: evidence.rightDepartureWorldPose,
                solveWidth: evidence.solveWidth,
                solveHeight: evidence.solveHeight,
                sourceBand: evidence.rightRoomLeftBand,
                targetBand: evidence.leftRoomRightBand,
                measuredTravel: nil
            )
            let translation = CGVector(
                dx: layout.targetEntryWorldPose.x
                    - evidence.leftOriginalEntryWorldPose.x,
                dy: layout.targetEntryWorldPose.y
                    - evidence.leftOriginalEntryWorldPose.y
            )
            let didTranslate = atlasPipeline.translateRoom(
                roomID: 1,
                by: translation,
                updatingComposer: false
            )
            let leftEntry = didTranslate
                ? layout.targetEntryWorldPose : evidence.leftOriginalEntryWorldPose
            let leftDoor = didTranslate
                ? layout.targetDoorWorldX
                : evidence.leftOriginalEntryWorldPose.x
                    + evidence.solveWidth
                    * (1 - (evidence.leftRoomRightBand?.widthFraction ?? 0))
            let leftDoorRange = didTranslate
                ? layout.targetDoorYRange
                : Self.doorYRange(
                    band: evidence.leftRoomRightBand,
                    cameraY: evidence.leftOriginalEntryWorldPose.y,
                    solveHeight: evidence.solveHeight
                )
            let activeRoomID = Dictionary(
                grouping: worldSnapshot.observations, by: \.roomID
            ).max(by: { $0.value.count < $1.value.count })?.key
            room = visualRoomTracker.bootstrapTwoRoomTopology(
                leftRoomID: 1,
                rightRoomID: 0,
                leftDoorWorldX: min(leftDoor, layout.sourceDoorWorldX),
                rightDoorWorldX: layout.sourceDoorWorldX,
                leftDoorYRange: leftDoorRange,
                rightDoorYRange: layout.sourceDoorYRange,
                leftEntryWorldPose: leftEntry,
                rightEntryWorldPose: evidence.rightDepartureWorldPose,
                activeRoomID: activeRoomID
            )
        }
        persistRoomTopology()
        return publishRoomTopology(room)
    }

    private static func doorYRange(
        band: FrameEdgeBlackBand?,
        cameraY: CGFloat,
        solveHeight: CGFloat
    ) -> ClosedRange<CGFloat> {
        let normalized = band?.contentYRangeFraction ?? 0.38...0.68
        let lower = cameraY + normalized.lowerBound * solveHeight
        let upper = cameraY + normalized.upperBound * solveHeight
        return lower...upper
    }

    private func persistRoomTopology() {
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

    var canStartPathRecording: Bool {
        labelPauseState == .ready && !isPathPlaybackActive && !isPathCheckpointPending
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

    private func sendPlayerOps(
        status: String,
        command: @escaping (UUID) -> ReceiverPlayerOpsCommand
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
        guard let path = inputPathRecorder.stop() else { return }
        isPathRecording = false
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
                self.inputPathLog.error("save failed error=\(error.localizedDescription, privacy: .public)")
            }
        }
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
        let accepted = gameControlForwarder.capturePlayerCheckpoint { [weak self] checkpoint, failure in
            guard let self else { return }
            self.isPathCheckpointPending = false
            guard let checkpoint else {
                self.pathRecordingIssue = failure ?? "Start-position capture failed"
                return
            }
            let startedAt = ProcessInfo.processInfo.systemUptime
            let createdAt = Date()
            guard self.inputPathRecorder.start(
                at: startedAt,
                createdAt: createdAt,
                startCheckpoint: checkpoint
            ) else { return }
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
            startCheckpoint: path.startCheckpoint
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
            self.pathPlaybackAbortIssue = issue
            self.inputPathLog.error("\(issue, privacy: .public) samples=\(decision.consecutiveSamples, privacy: .public)")
            self.playbackDivergenceMonitor.stop()
            self.gameControlForwarder.cancelPathPlayback()
        }
    }

    private func restoreRenderFrameMarkerPolicy() {
        gameControlForwarder.setRenderFrameMarkerEnabled(
            isHackerWorkspaceActive() || RenderedFrameMarker.enabled
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

    func setModelReviewActive(_ active: Bool) {
        gameControlForwarder.setReviewNavigationActive(active)
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
        menuStencilReloadQueue.async { [weak self] in
            guard let catalog = try? MenuStencilCatalog(
                examplesRootURL: examplesRootURL,
                calibration: .loadDefault()
            ) else { return }
            self?.sampleQueue.async { [weak self] in
                self?.menuStencilTracker.replaceCatalog(catalog)
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

    /// Arm as soon as the trained screen state recognizes Quit Game / Yes.
    /// This precedes the click or key event, so a fast Unity shutdown cannot
    /// race the local input callback. Vision still closes only after the exact
    /// Hollow Knight process observed here terminates.
    private func armQuitGameExitWatch() {
        guard quitGameExitApplication == nil,
              let application = GameControlForwarder.hollowKnightApplication()
        else { return }
        quitGameExitApplication = application
        quitGameExitConfirmationObserved = false
        quitGameExitDeadline = nil
        quitGameExitWatchGeneration &+= 1
        quitGameExitLog.notice(
            "armed pid=\(application.processIdentifier, privacy: .public)"
        )
        pollForConfirmedGameExit(
            generation: quitGameExitWatchGeneration
        )
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

    private func cancelQuitGameExitWatch() {
        guard quitGameExitApplication != nil else { return }
        quitGameExitWatchGeneration &+= 1
        quitGameExitApplication = nil
        quitGameExitConfirmationObserved = false
        quitGameExitDeadline = nil
        quitGameExitLog.notice("cancelled before confirmation")
    }

    private func updateQuitGameExitWatch(for status: LiveGameStatus?) {
        if QuitGameTerminationPolicy.recognizesExitIntent(
            context: status?.context,
            selectedOption: status?.selectedMenuOption
        ) {
            armQuitGameExitWatch()
        } else if status != nil, !quitGameExitConfirmationObserved {
            cancelQuitGameExitWatch()
        }
    }

    private func handleTerminatedApplication(_ notification: Notification) {
        guard let watched = quitGameExitApplication,
              let terminated = notification.userInfo?[NSWorkspace.applicationUserInfoKey]
                as? NSRunningApplication,
              terminated.processIdentifier == watched.processIdentifier
        else { return }
        quitGameExitLog.notice(
            "confirmed termination pid=\(terminated.processIdentifier, privacy: .public)"
        )
        NSApp.terminate(nil)
    }

    private func pollForConfirmedGameExit(generation: UInt64) {
        guard generation == quitGameExitWatchGeneration else { return }
        guard let application = quitGameExitApplication else { return }
        if application.isTerminated
            || !NSWorkspace.shared.runningApplications.contains(where: {
                $0.processIdentifier == application.processIdentifier
            }) {
            quitGameExitLog.notice(
                "confirmed termination by poll pid=\(application.processIdentifier, privacy: .public)"
            )
            NSApp.terminate(nil)
            return
        }
        if let deadline = quitGameExitDeadline,
           ProcessInfo.processInfo.systemUptime >= deadline {
            quitGameExitLog.error("confirmation timed out while game remained running")
            cancelQuitGameExitWatch()
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
            self?.pollForConfirmedGameExit(generation: generation)
        }
    }

    var canToggleLabelMode: Bool {
        labelPauseState == .ready || labelPauseState == .paused
    }

    var isLabelModeActive: Bool {
        labelPauseState == .pausing || labelPauseState == .paused
    }

    private func recordGroundTruthCameraSample(
        _ sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval
    ) {
        groundTruthCameraStateLock.lock()
        latestGroundTruthCameraSample = (sample, observedAt)
        groundTruthCameraStateLock.unlock()
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
        guard isHackerWorkspaceActive(),
              let match = hackerFrameSynchronizer.append(
                sample: sample,
                observedAt: observedAt
              ) else { return }
        enqueueHackerMatch(match)
    }

    func setHackerWorkspaceActive(_ active: Bool) {
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
            active || isPathPlaybackActive || RenderedFrameMarker.enabled
        )
        hackerAtlasLog.notice("workspace active=\(active, privacy: .public)")
    }

    func deleteHackerAtlas() {
        let reactivate = isHackerWorkspaceActive()
        resetHackerAtlasWorkers(reactivate: reactivate)
        hackerAtlasTiles = []
        hackerAtlasRooms = []
        hackerRoomConnections = []
        hackerRoomOffsets = [:]
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
        var offset = hackerRoomOffsets[roomID] ?? .zero
        offset.x += translation.dx
        offset.y += translation.dy
        hackerRoomOffsets[roomID] = offset
        if hackerCameraTransform?.sceneName == room.sceneName {
            hackerLiveBounds = hackerLiveBounds.offsetBy(
                dx: translation.dx,
                dy: translation.dy
            )
            hackerMapFocus.x += translation.dx
            hackerMapFocus.y += translation.dy
        }
        updateHackerAtlasBounds()
    }

    func hackerRoomPosition(_ room: HackerAtlasRoom) -> CGPoint {
        let offset = hackerRoomOffsets[room.id] ?? .zero
        let layoutPosition = hackerRoomLayout.positions[room.id] ?? room.position
        return CGPoint(
            x: layoutPosition.x + offset.x,
            y: layoutPosition.y + offset.y
        )
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
        integratesAtlas: Bool
    ) {
        guard isHackerWorkspaceActive() else { return }
        let capture = HackerCapturedFrame(
            unityFrame: unityFrame,
            image: image,
            capturedAt: capturedAt,
            integratesAtlas: integratesAtlas
        )
        if let match = hackerFrameSynchronizer.append(capture: capture) {
            enqueueHackerMatch(match)
        }
    }

    private func enqueueHackerMatch(_ match: HackerMatchedFrame) {
        hackerStateLock.lock()
        let generation = hackerGeneration
        let active = hackerWorkspaceActive
        hackerStateLock.unlock()
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
            enqueueHackerAtlas(prepared, generation: ticket.epoch)
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isHackerGenerationCurrent(ticket.epoch) else { return }
                let offset = self.hackerAtlasRooms.first {
                    $0.sceneName == prepared.sceneName
                }.map { room -> CGPoint in
                    let position = self.hackerRoomPosition(room)
                    return CGPoint(
                        x: position.x - room.position.x,
                        y: position.y - room.position.y
                    )
                } ?? .zero
                self.hackerLiveImage = prepared.liveImage
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
        let output = isHackerGenerationCurrent(ticket.epoch)
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
                self.hackerRoomConnections = output.connections
                self.hackerRoomLayout = HackerRoomLayout.make(
                    rooms: output.rooms,
                    connections: output.connections
                )
                let validRoomIDs = Set(output.rooms.map(\.id))
                self.hackerRoomOffsets = self.hackerRoomOffsets.filter {
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

    private func isHackerGenerationCurrent(_ generation: UInt64) -> Bool {
        hackerStateLock.lock(); defer { hackerStateLock.unlock() }
        return hackerWorkspaceActive && hackerGeneration == generation
    }

    func toggleLabelMode() {
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
        gameControlForwarder.releaseForGroundLabeling()
    }

    func recentCapturedFrames() -> [RecentCapturedFrame] {
        capturedFrameStateLock.lock()
        defer { capturedFrameStateLock.unlock() }
        return recentCapturedFrameBuffer.frames
    }

    private func applyLabelPauseState(_ state: GamePauseState) {
        let completedLabelSession = state == .ready
            && (labelPauseState == .paused || labelPauseState == .resuming)
        labelPauseState = state
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
        guard let screen = NSScreen.main else { return }
        let visible = screen.visibleFrame
        guard let viewer = NSApp.windows.first(where: {
            $0 is VisionPanel && $0.isVisible
        }) else { return }
        viewer.setFrame(
            visible,
            display: true,
            animate: false
        )
        tileGameWindow(
            frame: CGRect(
                x: visible.minX,
                y: screen.frame.maxY - visible.maxY,
                width: visible.width,
                height: visible.height
            ),
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
        NSApp.activate(ignoringOtherApps: true)
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
            .max { $0.frame.width * $0.frame.height < $1.frame.width * $1.frame.height }
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
    private func advanceGeneration(resetCamera: Bool) {
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
            self.currentStatusGeneration = generation
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
            self.menuStencilTracker.reset(preservingLanguage: true)
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
        return "Capture failed: \(error.localizedDescription). Screen Recording permission may require an app restart."
    }

    private func applyWorldUpdate(_ update: LiveWorldPipelineUpdate) {
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
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]],
           let status = attachments.first?[.status] as? Int,
           SCFrameStatus(rawValue: status) != .complete {
            captureIncompleteFrames &+= 1
            return
        }
        captureCompleteFrames &+= 1
        let timestamp = CMSampleBufferGetPresentationTimeStamp(sampleBuffer).seconds
        let capturedAt = ProcessInfo.processInfo.systemUptime
        telemetry.record(.captureDelivery, seconds: capturedAt - timestamp)
        autoreleasepool { stageFrame(pixelBuffer, timestamp: timestamp, capturedAt: capturedAt) }
    }

    private func stageFrame(_ pixelBuffer: CVPixelBuffer, timestamp: Double, capturedAt: TimeInterval) {
        capturedFrameStateLock.lock()
        let isFrozen = captureProcessingPaused
            || playbackPreparationCapturePaused
        capturedFrameStateLock.unlock()
        // A frozen editor already owns its screenshot. Do not convert, solve,
        // or publish background frames that invalidate its entire view graph.
        guard !isFrozen else { return }
        guard let converted = CaptureFrameConverter.convert(pixelBuffer, context: poseImageContext) else { return }
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
        let renderedGameFrame = hackerActive
            || playbackDivergenceMonitor.isAcceptingSamples
            ? RenderedFrameMarker.decode(frame) : nil
        if hackerActive, let renderedGameFrame {
            recordHackerCapture(
                frame,
                unityFrame: renderedGameFrame,
                capturedAt: capturedAt,
                integratesAtlas: sampleFrameCount.isMultiple(
                    of: HollowKnightCaptureConfiguration.atlasSampleStride
                )
            )
        }
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
        let menuStencilStarted = ProcessInfo.processInfo.systemUptime
        let rawMenuStencil = menuStencilTracker.observe(frame, timestamp: timestamp)
        let selectorCalibrationDiagnostic =
            menuStencilTracker.selectorCalibrationDiagnostic
        telemetry.record(
            .menuStencil,
            seconds: ProcessInfo.processInfo.systemUptime - menuStencilStarted
        )
        let hudStarted = ProcessInfo.processInfo.systemUptime
        let hudStencil = hudStencilTracker.observe(frame,
            // HUD evidence participates in opening the gameplay gate. Keeping
            // it behind that gate made startup depend only on coarse brightness
            // and delayed object-model output.
            gameplay: true,
            timestamp: timestamp, generation: captureGeneration)
        telemetry.record(.hudStencil, seconds: ProcessInfo.processInfo.systemUptime - hudStarted)
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
        let displayingMenu = recognizedGameStatus.map { !$0.context.isGameplay } ?? false
        let frameExtent = CGRect(x: 0, y: 0, width: frame.width, height: frame.height)
        let transitionRegions = SceneRegions.atlasGameplay(
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
        let room = solved.gameplayEnabled
            ? visualRoomTracker.observe(
                profile: signalProfile,
                baseDecision: transitionDecision,
                horizontalInput: gameControlForwarder.currentHorizontalRoomDirection(),
                knightRect: transitionRegions.knight,
                frameSize: CGSize(width: frame.width, height: frame.height),
                currentWorldPose: roomCameraPosition,
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
        let presentationCameraPosition = room.coarseWorldPose
            ?? room.transitionWorldPose
            ?? (roomOwnershipChanged
                ? room.activationWorldPose ?? room.entryWorldPose
                : PresentationPosePrediction.position(solved.cameraPosition,
                    velocity: solved.cameraVelocity, measuredAt: solved.poseTimestamp,
                    presentedAt: timestamp))
        inputPathRecorder.recordCoarseMotion(
            captureTimestamp: timestamp,
            renderedGameFrame: renderedGameFrame,
            presentedCameraPosition: presentationCameraPosition,
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
            observedAt: capturedAt
        )
        if let renderedGameFrame,
           let decision = playbackDivergenceMonitor.appendVision(
            frameKey: renderedGameFrame,
            cameraPosition: presentationCameraPosition,
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
                    reliableGroundVelocityTimestamp: priorReliableTimestamp
                )
            }
            trackingStateLock.unlock()
        }
        if coarseMotionDiagnosticsEnabled,
           let grid = room.coarseMotionGrid {
            let diagnosticImage = LowResolutionMotionDiagnosticRenderer.image(
                grid: grid,
                estimate: room.coarseMotionEstimate,
                isControlling: room.coarseMotionIsControlling
            )
            let diagnosticStatus = LowResolutionMotionDiagnosticPresentation.text(
                estimate: room.coarseMotionEstimate,
                isControlling: room.coarseMotionIsControlling,
                solveWidth: converted.solveWidth,
                gridWidth: grid.width
            )
            DispatchQueue.main.async { [weak self] in
                guard self?.coarseMotionDiagnosticsEnabled == true else { return }
                self?.coarseMotionDiagnosticImage = diagnosticImage
                self?.coarseMotionDiagnosticStatus = diagnosticStatus
            }
        }
        let hasSignal = transitionDecision.admitsTracking && !room.isTransitioning
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
                rawImage: frame,
                sourceFrameIdentifier: objectSourceFrameIdentifier,
                timestamp: timestamp,
                captureGeneration: captureGeneration,
                extensionFraction: CGFloat(max(0, min(0.5, objectDetectionExtension))),
                createsPixelDiagnostic: objectDetectionPixelsEnabled
            ))
        }
        if shouldRunVision {
            enqueueVision(VisionFrame(
                presentationFrame: frame,
                solveWidth: converted.solveWidth,
                hasGameplaySignal: hasSignal,
                resumedAfterTransition: transitionDecision.resumedAfterTransition || roomChanged,
                signalProfile: signalProfile,
                room: room,
                timestamp: timestamp,
                worldTimestamp: Date().timeIntervalSinceReferenceDate,
                capturedAt: capturedAt,
                generation: captureGeneration,
                hudStencil: hudStencil,
                menuStencil: menuStencil
            ))
        }
        let extent = frameExtent
        let regions = solved.gameplayEnabled
            ? SceneRegions.atlasGameplay(fromObjectDetections: objectDetections,
                                         in: extent, hudStencil: hudStencil)
            : SceneRegions.startup(in: extent)
        let presentationRegions = displayingMenu ? SceneRegions.startup(in: extent) : regions
        DispatchQueue.main.async { [weak self] in
            guard let self, self.labelImage == nil,
                  captureGeneration == self.currentStatusGeneration else { return }
            let status = self.gameStatusTracker.observe(recognizedGameStatus, at: timestamp)
            self.updateQuitGameExitWatch(for: status)
            let context = status?.context.labelingContext?.storageIdentifier
            let gameplay = status?.context.isGameplay == true
            if self.liveGameStatus != status?.displayText { self.liveGameStatus = status?.displayText }
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
            allowAtlasWrite: hasSignal && solved.gameplayEnabled && !displayingMenu && sampleFrameCount.isMultiple(
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
            showsMenuStencil: stencilDetectionEnabled
        ))
    }

    private func enqueueVision(_ frame: VisionFrame) {
        guard let ticket = visionFramePump.submit(frame, epoch: frame.generation) else { return }
        visionQueue.async { [weak self] in self?.processVision(ticket) }
    }

    private func processVision(_ ticket: LatestFramePump<VisionFrame>.Ticket) {
        let snapshot = ticket.item
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
            gameplayIsLatched: startupPhaseBeforeObservation == .gameplay
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
            atlasAdmissionLog.info(
                "startup phase=\(phase, privacy: .public) screen=\(screen, privacy: .public) selected=\(selected, privacy: .public) gameplayLikely=\(startupEvidence.gameplayLikely, privacy: .public) detections=\(detections, privacy: .public)"
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

        if snapshot.room.isTransitioning {
            let transitionPose = snapshot.room.transitionWorldPose
                ?? worldBasis.snapshot.latestWorldPose
                ?? accumulator.position
            accumulator.applyGlobalCorrection(to: transitionPose)
            transitionMotionBridge.suspendForTransition()
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
                    reliableGroundVelocityTimestamp: priorReliableTimestamp
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
            regions = SceneRegions.atlasGameplay(
                fromObjectDetections: objectDetections,
                in: extent,
                knightMinimumConfidence:
                    LiveObjectDetectionPostprocessor.groundKnightConfidenceThreshold,
                hudStencil: snapshot.hudStencil
            )
            let shouldShowGroundEdge = groundEdgeEnabled
            let shouldShowGroundDetect = groundDetectEnabled
            let shouldShowCleaned = cleanedEnabled
            let shouldShowGroundHypotheses = groundHypothesesEnabled
            let tuning = groundTuning
            let groundDetectionStarted = ProcessInfo.processInfo.systemUptime
            // Clean ground is now part of camera orientation, not only a debug
            // overlay. Analyze every sampled Gameplay frame so known line IDs
            // can correct vertical placement before this observation reaches
            // the atlas.
            let groundForegroundRects = regions?.omittedRects ?? []
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
            let theoryAnalysis = comparisonAnalysis.flatMap {
                GroundLineDetector.theory(from: $0, tuning: tuning)
            }
            let detectedFloorLines = theoryAnalysis.map(GroundLineDetector.cleanFloorLines) ?? []
            let cleanFloorLines = GroundLineDetector.rejectingKnownForegroundLines(
                detectedFloorLines,
                imageHeight: snapshot.presentationFrame.height,
                foregroundRects: groundForegroundRects,
                knightRect: regions?.knight
            )
            let semanticTuning = semanticGroundTuning
            let semanticTheoryAnalysis = comparisonAnalysis.flatMap {
                GroundLineDetector.semanticTheory(from: $0, tuning: semanticTuning)
            }
            let semanticDetectedLines = semanticTheoryAnalysis.map {
                GroundLineDetector.cleanFloorLines(from: $0)
            } ?? []
            let semanticFloorLines = GroundLineDetector.rejectingKnownForegroundLines(
                semanticDetectedLines,
                imageHeight: snapshot.presentationFrame.height,
                foregroundRects: groundForegroundRects,
                knightRect: regions?.knight
            )
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
                    floorLines: cleanFloorLines,
                    lineSeparation: tuning.lineSeparation,
                    occlusionMergeGap: tuning.occlusionMergeGap,
                    timestamp: snapshot.timestamp,
                    sourceLuma: groundAnalysis?.sourceLuma,
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
            let groundTrackingSeconds = ProcessInfo.processInfo.systemUptime - groundTrackingStarted
            hadGlobalCorrection = hypothesisTracking?.globalCorrection != nil
            telemetry.record(.groundTracking, seconds: groundTrackingSeconds)
            if let milliseconds = groundHypothesisTracker.completedGlobalSearchMilliseconds {
                telemetry.record(.globalSearch, seconds: milliseconds / 1_000)
            }
            if let hypothesisTracking { pathPlaybackGroundReadiness = hypothesisTracking }
            if let audit = rawGroundFrameAudit, let pixels = groundAnalysis?.sourceLuma,
               let tracking = hypothesisTracking {
                audit.record(pixels: pixels, width: snapshot.presentationFrame.width,
                    height: snapshot.presentationFrame.height, timestamp: snapshot.timestamp,
                    lines: cleanFloorLines, exclusions: trackerForegroundRects, tracking: tracking,
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
                let rows = cleanFloorLines.map { "\($0.row):\($0.xRange.lowerBound)-\($0.xRange.upperBound)" }.joined(separator: ",")
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
                let raw = detectedFloorLines.map { "\($0.row):\($0.xRange)" }.joined(separator: ",")
                let filtered = cleanFloorLines.map { String($0.row) }.joined(separator: ",")
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
            var usedCoarseFloorlessPose = false
            var floorlessCoarseForRecording: CGPoint?
            var floorlessMaskedForRecording: CGPoint?
            var floorlessDirectionForRecording: VisualRoomDirection?
            var floorlessSelectionForRecording: String?
            let groundCandidate: CGPoint? = {
                guard let ground = hypothesisTracking,
                      ground.poseVerified, ground.hasConfirmedGround,
                      let pose = ground.cameraPosition else { return nil }
                return CGPoint(
                    x: pose.x / latestPresentationScale,
                    y: pose.y / latestPresentationScale
                )
            }()
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
                    movingForegroundRect: regions?.knight
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
                        movingForegroundRect: regions?.knight
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
                } else {
                    let bridgeMeasurement = transitionMotionBridge.track(
                        frame: snapshot.presentationFrame,
                        excluding: regions?.omittedRects ?? [],
                        solveWidth: snapshot.solveWidth,
                        // Historical ground is not evidence that Y is stationary
                        // during a current-frame ground miss or rejected solve.
                        lockVertical: false,
                        movingForegroundRect: regions?.knight
                    )
                    let expectedDirection =
                        gameControlForwarder.currentHorizontalRoomDirection()
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
                                movingForegroundRect: regions?.knight
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
                        movingForegroundRect: regions?.knight
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
                // Keep private odometry in the same coordinate system as a
                // measured fallback. Never seed a merely held/unobserved pose.
                let measuredFallback = bridged != nil || usedCoarseFloorlessPose
                if measuredFallback && hypothesisTracking?.poseVerified == false
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
                        floorLines: cleanFloorLines,
                        timestamp: snapshot.timestamp,
                        sourceLuma: groundAnalysis?.sourceLuma,
                        protectedOcclusions: trackerForegroundRects,
                        allowsProvisionalPromotion: bridged.map {
                            $0.confidence >= 0.8
                        } ?? false
                    )
                }
            }
            let recordedPoseSource: String
            if groundAnchoredPose {
                recordedPoseSource = "ground"
            } else if usedCoarseFloorlessPose {
                recordedPoseSource = "coarseMotion"
            } else if transitionBridgePose {
                recordedPoseSource = "motionBridge"
            } else if cameraUpdate?.state == .accepted {
                recordedPoseSource = "unconfirmedGround"
            } else {
                recordedPoseSource = "held"
            }
            inputPathRecorder.recordTracking(
                hypothesisTracking ?? .empty,
                captureTimestamp: snapshot.timestamp,
                groundTrackingMilliseconds: groundTrackingSeconds * 1_000,
                publishedCameraPosition: worldCameraPosition,
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
                floorlessSelectionSource: floorlessSelectionForRecording
            )
            if shouldShowGroundEdge || shouldShowGroundDetect
                || shouldShowCleaned
                || shouldShowGroundHypotheses {
                let edgeImage = shouldShowGroundEdge
                    ? groundAnalysis.flatMap(GroundLineDetector.edgeImage)
                    : nil
                let detectImage = shouldShowGroundDetect
                    ? comparisonAnalysis.flatMap(GroundLineDetector.groundDetectImage)
                    : nil
                let groundStageImage = shouldShowGroundDetect
                    ? semanticTheoryAnalysis.flatMap(GroundLineDetector.groundStageImage)
                    : nil
                // Cleaned and Ground Features share this exact line image.
                // Building one source prevents persistent-cell cleanup from
                // shortening the visual ground line in hypothesis mode.
                let lineReviews = hypothesisTracking?.poseVerified == true
                    ? semanticLineReviews : []
                let cleanedImage = shouldShowCleaned
                    ? GroundLineDetector.presenceImage(
                        from: lineReviews,
                        width: snapshot.presentationFrame.width,
                        height: snapshot.presentationFrame.height
                    )
                    : nil
                DispatchQueue.main.async { [weak self] in
                    if self?.groundEdgeEnabled == true {
                        self?.groundEdgeImage = edgeImage
                    }
                    if self?.groundDetectEnabled == true {
                        self?.groundDetectImage = detectImage
                        self?.groundStageImage = groundStageImage
                    }
                    if self?.cleanedEnabled == true {
                        self?.cleanedImage = cleanedImage
                    } else if self?.groundHypothesesEnabled == true {
                        self?.cleanedImage = nil
                    }
                    if let hypothesisTracking {
                        self?.groundHypothesisTracking = hypothesisTracking
                        self?.groundHypothesisImage = self?.groundHypothesesEnabled == true
                            ? hypothesisImage : nil
                    }
                }
            }
            if cameraUpdate?.state == .accepted {
                let ground = groundReferenceTracker.observe(
                    frame: snapshot.presentationFrame,
                    cameraPosition: worldCameraPosition,
                    solveWidth: snapshot.solveWidth,
                    knightRect: regions?.knight,
                    excluding: regions?.omittedRects ?? [],
                    edgeAnalysis: groundAnalysis,
                    comparisonAnalysis: comparisonAnalysis,
                    theoryAnalysis: theoryAnalysis,
                    tuning: tuning
                )
                groundForFrame = ground
                DispatchQueue.main.async { [weak self] in
                    self?.groundReference = ground
                }
            }
        } else {
            groundHypothesisTracker.reset(keepingGlobalFeatures: true)
            transitionMotionBridge.suspendForTransition()
            groundPlacementRecovery.missingObservation()
            DispatchQueue.main.async { [weak self] in
                self?.groundHypothesisImage = nil
                self?.groundHypothesisTracking = .empty
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
                hasCommittedEvidence: atlasPipeline.hasCommittedWorldEvidence
            )
            let allowAtlasWrite = worldLocated && bootstrapReady && groundPlacementRecovery.allowsAtlasWrite
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
                    : priorTrackingState.reliableGroundVelocityTimestamp
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
                    basisTicket: basisTicket,
                    groundAnchoredPose: groundAnchoredPose,
                    transitionBridgePose: transitionBridgePose,
                    alignmentExclusions: groundAlignmentExclusions,
                    compositionOmittedRects: roomCompositionOmittedRects
                        + transitionEdgeOmittedRects,
                    roomID: snapshot.room.roomID
                )
            }
            // GroundHypothesisTracker owns feature tracking and loop matching.
            // Do not launch the obsolete full-image landmark worker per frame.
        }
        if let next = completion.next {
            visionQueue.async { [weak self] in self?.processVision(next) }
        }
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
                    extensionFraction: frame.extensionFraction,
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
        guard ticket.epoch == renderGeneration else { return }
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
                  snapshot.generation == self.currentStatusGeneration else { return }
            self.telemetry.record(
                .mainQueueWait,
                seconds: ProcessInfo.processInfo.systemUptime - renderedAt
            )
            self.telemetry.record(
                .captureToPublish,
                seconds: ProcessInfo.processInfo.systemUptime - snapshot.capturedAt
            )
            self.publishAtlasTiles(output.atlasTiles)
            self.hudStencilImage = stencilImage
            self.menuStencilImage = menuStencilImage
            self.liveImage = output.liveImage
            LivePresentationTiming.shared.register(output.liveImage, capturedAt: snapshot.timestamp)
            if self.liveBounds != output.liveBounds { self.liveBounds = output.liveBounds }
            if self.mapFocus != output.focusPoint { self.mapFocus = output.focusPoint }
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
}
