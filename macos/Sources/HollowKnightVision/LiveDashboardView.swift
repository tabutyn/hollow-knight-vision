import AppKit
import Combine
import Foundation
import SwiftUI
import UniformTypeIdentifiers

enum LabelingTrainingNotificationPolicy {
    static func untrainedImageCount(
        exampleModificationDates: [Date],
        latestDatasetCreatedAt: Date?
    ) -> Int {
        guard let latestDatasetCreatedAt else { return exampleModificationDates.count }
        return exampleModificationDates.count { $0 > latestDatasetCreatedAt }
    }

    static func hasUntrainedChanges(
        exampleModificationDates: [Date],
        latestDatasetCreatedAt: Date?
    ) -> Bool {
        untrainedImageCount(
            exampleModificationDates: exampleModificationDates,
            latestDatasetCreatedAt: latestDatasetCreatedAt
        ) > 0
    }
}

enum LiveAtlasPresentationPolicy {
    static func hackerFitBounds(mapped: CGRect?, live: CGRect, paths: CGRect) -> CGRect? {
        let base = mapped ?? live
        let bounds = (base.isEmpty ? CGRect.null : base).union(paths)
        return bounds.isNull || bounds.isEmpty ? nil : bounds
    }

    static func includesAtlas(hasGameplayStarted: Bool) -> Bool {
        hasGameplayStarted
    }

    static func admitsPathFrame(
        hasSceneSignal: Bool,
        recognizedContext: LiveGameStatus.Context?
    ) -> Bool {
        hasSceneSignal && recognizedContext == .gameplay
    }

    static func showsPaths(enabled: Bool, context: LabelingContext?, presentedGameplayFrame: Bool) -> Bool {
        enabled && context == .game && presentedGameplayFrame
    }
}

struct LiveAtlasPathFrameEvidence {
    // Visual recognition and presentation publish asynchronously. Hacker carries
    // its gameplay evidence alongside the matched image instead.
    private static let capacity = 24
    private var gameplayTimestamps = [TimeInterval]()
    private var latestTimestamp: TimeInterval?
    private var latestReceiverTimestamp = -Double.infinity
    private var receiverGameplay = true
    private var latestReceiverNonGameplay = -Double.infinity
    private var receiverGameplayResumedAt = -Double.infinity
    private var presentedHackerTimestamp: TimeInterval?

    mutating func presentHackerFrame(at timestamp: TimeInterval, gameplay: Bool) {
        presentedHackerTimestamp = gameplay ? timestamp : nil
    }

    var admitsHackerPreview: Bool { receiverAdmits(presentedHackerTimestamp) }

    mutating func observe(gameplay: Bool, at timestamp: TimeInterval) {
        guard timestamp.isFinite, timestamp >= (latestTimestamp ?? -.infinity) else { return }
        latestTimestamp = timestamp
        guard gameplay else {
            gameplayTimestamps.removeAll(keepingCapacity: true)
            return
        }
        guard gameplayTimestamps.last != timestamp else { return }
        if gameplayTimestamps.count == Self.capacity { gameplayTimestamps.removeFirst() }
        gameplayTimestamps.append(timestamp)
    }

    mutating func observeReceiver(gameplay: Bool, at timestamp: TimeInterval) {
        guard timestamp.isFinite else { return }
        if !gameplay { latestReceiverNonGameplay = max(latestReceiverNonGameplay, timestamp) }
        guard timestamp >= latestReceiverTimestamp else { return }
        if gameplay && !receiverGameplay { receiverGameplayResumedAt = timestamp }
        latestReceiverTimestamp = timestamp
        receiverGameplay = gameplay
    }

    mutating func resetVisualFrames() {
        gameplayTimestamps.removeAll(keepingCapacity: true)
        latestTimestamp = nil
        presentedHackerTimestamp = nil
    }

    func admits(_ timestamp: TimeInterval?) -> Bool {
        guard let timestamp else { return false }
        return receiverAdmits(timestamp) && gameplayTimestamps.contains(timestamp)
    }

    private func receiverAdmits(_ timestamp: TimeInterval?) -> Bool {
        guard let timestamp else { return false }
        return receiverGameplay && timestamp > latestReceiverNonGameplay && timestamp >= receiverGameplayResumedAt
    }
}

enum PathRecordingPresentationPolicy {
    static func showsControl(
        workspace: DashboardWorkspace,
        recognizedContext: String?
    ) -> Bool {
        workspace == .hacker
            || (workspace == .gameplay && recognizedContext == "Gameplay")
    }
}

enum DashboardWorkspace: String, CaseIterable, Identifiable {
    case gameplay = "Gameplay"
    case label = "Label"
    case stencils = "Stencil"
    case model = "Model"
    case bot = "Bot"
    case hacker = "Hacker"
    case ops = "Ops"

    var id: Self { self }
    var shortcutCharacter: Character { rawValue.lowercased().first! }
    var supportsGameplayExitShortcut: Bool { self == .label || self == .model || self == .bot || self == .stencils }
}

enum DashboardControlVisibility {
    static func showsFraming(workspace: DashboardWorkspace, phaseOneComplete: Bool,
                             recognizedContext: String?, hackerGameplayFrame: Bool) -> Bool {
        if workspace == .hacker { return hackerGameplayFrame }
        return workspace == .gameplay && phaseOneComplete && recognizedContext == "Gameplay"
    }
    static func showsOps(workspace: DashboardWorkspace, mode: HollowKnightRuntimeMode,
                         recognizedContext: String?) -> Bool {
        workspace == .gameplay && mode == .modded && recognizedContext == "Gameplay"
    }
    static var workspaces: [DashboardWorkspace] {
        DashboardWorkspace.allCases.filter { $0 != .ops }
    }
    // B is reserved for bot start/stop in Gameplay and Hacker.
    static var shortcutWorkspaces: [DashboardWorkspace] {
        DashboardWorkspace.allCases.filter { $0 != .bot }
    }
}

enum WorkspaceExitShortcutPolicy {
    static func shouldHandle(
        workspace: DashboardWorkspace,
        eventType: NSEvent.EventType,
        keyCode: UInt16,
        modifierFlags: NSEvent.ModifierFlags,
        isRepeat: Bool, isEditingText: Bool = false
    ) -> Bool {
        workspace.supportsGameplayExitShortcut
            && eventType == .keyDown
            && (keyCode == 7 || (workspace == .label && keyCode == 37 && !isEditingText)) // X / L
            && !isRepeat
            && modifierFlags.intersection([.command, .control, .option]).isEmpty
    }
}

enum RoomResetShortcut {
    static func matches(_ event: NSEvent) -> Bool {
        event.type == .keyDown && !event.isARepeat && event.charactersIgnoringModifiers?.lowercased() == "r"
            && event.modifierFlags.intersection([.shift, .control, .option, .command]).isEmpty
    }
}

struct RecentFramesPresentation: Identifiable {
    let id = UUID()
    let examples: [SavedLabelingExample]
    let capturedFrames: [RecentCapturedFrame]
}

/// An app-local command monitor remains active when a Label sheet owns the key
/// window. SwiftUI keyboard shortcuts attached below that sheet do not.
private struct WorkspaceExitKeyMonitor: NSViewRepresentable {
    let workspace: DashboardWorkspace
    let onExit: () -> Void
    let onAtlasTab: (() -> Void)?
    let onResetRoom: (() -> Void)?
    let gameHasFocus: () -> Bool

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class ShortcutView: NSView {
        var reservesTab = false { didSet { panel?.atlasTabEnabled = reservesTab } }
        private weak var panel: NormalVisionPanel?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            panel?.atlasTabEnabled = false
            panel = window as? NormalVisionPanel
            panel?.atlasTabEnabled = reservesTab
        }
    }

    func makeNSView(context: Context) -> ShortcutView {
        let view = ShortcutView(frame: .zero)
        context.coordinator.view = view
        context.coordinator.install()
        return view
    }

    func updateNSView(_ nsView: ShortcutView, context: Context) {
        context.coordinator.workspace = workspace
        context.coordinator.onExit = onExit
        context.coordinator.onAtlasTab = onAtlasTab
        context.coordinator.gameHasFocus = gameHasFocus
        context.coordinator.setRoomResetAction(onResetRoom)
        nsView.reservesTab = onAtlasTab != nil
    }

    final class Coordinator {
        var workspace = DashboardWorkspace.gameplay
        weak var view: NSView?
        var onAtlasTab: (() -> Void)?
        var onExit: () -> Void = {}
        private var monitor: Any?
        private var gameMonitor: Any?
        private var onResetRoom: (() -> Void)?
        var gameHasFocus: () -> Bool = { false }

        func setRoomResetAction(_ action: (() -> Void)?) {
            onResetRoom = action
            if action == nil, let gameMonitor {
                NSEvent.removeMonitor(gameMonitor)
                self.gameMonitor = nil
            } else if action != nil, gameMonitor == nil {
                gameMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
                    guard let self, RoomResetShortcut.matches(event), gameHasFocus(),
                          let window = view?.window, window.attachedSheet == nil, NSApp.modalWindow == nil else { return }
                    onResetRoom?()
                }
            }
        }

        func install() {
            guard monitor == nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard let self else { return event }
                if let onResetRoom, RoomResetShortcut.matches(event), let window = view?.window,
                   event.window === window, window.isKeyWindow, window.attachedSheet == nil, NSApp.modalWindow == nil,
                   !(window.firstResponder is NSTextView), !(window.firstResponder is NSTextField) {
                    onResetRoom()
                    return nil
                }
                if let onAtlasTab, let window = view?.window, event.window === window,
                   AtlasTabShortcut.handles(event, in: window) {
                    if !event.isARepeat { onAtlasTab() }
                    return nil
                }
                guard WorkspaceExitShortcutPolicy.shouldHandle(
                    workspace: workspace,
                    eventType: event.type,
                    keyCode: event.keyCode,
                    modifierFlags: event.modifierFlags,
                    isRepeat: event.isARepeat,
                    isEditingText: NSApp.keyWindow?.firstResponder is NSTextView
                ) else { return event }
                onExit()
                return nil
            }
        }

        deinit {
            if let monitor { NSEvent.removeMonitor(monitor) }
            if let gameMonitor { NSEvent.removeMonitor(gameMonitor) }
        }
    }
}

enum LiveGameStatusPresentationPolicy {
    static func text(recognizedContext: String?, roomStatus: String? = nil) -> String {
        let context = recognizedContext ?? "Finding State"
        guard context == "Gameplay", let roomStatus, !roomStatus.isEmpty else {
            return context
        }
        return "\(context) · \(roomStatus)"
    }
}

enum DebugViewAvailabilityPolicy {
    static func showsGroundTrackingTools(
        workspace: DashboardWorkspace,
        recognizedContext: String?
    ) -> Bool {
        workspace == .gameplay && recognizedContext == "Gameplay"
    }
}

enum DashboardSettingsSection: String, CaseIterable, Identifiable {
    case atlas = "Atlas"
    case debugView = "Debug View"
    case system = "System"

    var id: Self { self }
    var symbol: String {
        switch self {
        case .atlas: "map"
        case .debugView: "ladybug"
        case .system: "gearshape"
        }
    }
}

private struct DebugView: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    @ObservedObject var model: LiveCaptureModel
    let showsGroundTrackingTools: Bool
    @Binding var showsThinkingContext: Bool
    @Binding var showsGameState: Bool
    @Binding var showsGroundTrackingPatches: Bool
    @Binding var showsRoad: Bool
    @Binding var showsEstimatedPlacement: Bool
    let stencilReady: Bool
    let modelReady: Bool
    let openGroundViewer: () -> Void
    let openTextReader: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Toggle("Thinking context", isOn: $showsThinkingContext)
            Toggle(visionText("Game State"), isOn: $showsGameState)
            Toggle(visionText("Stencil Detection"), isOn: $model.stencilDetectionEnabled)
                .disabled(!stencilReady)
            if !stencilReady { Text(visionText("stencilLocked")).font(.caption).foregroundStyle(.secondary) }
            Toggle(visionText("Object Detection"), isOn: $model.objectDetectionEnabled)
                .disabled(!modelReady)
            if !modelReady { Text(visionText("modelLocked")).font(.caption).foregroundStyle(.secondary) }
            Group {
                Toggle(visionText("Raw Detection"), isOn: $model.objectDetectionRawEnabled)
                Toggle(visionText("Detection Pixels"), isOn: $model.objectDetectionPixelsEnabled)
            }.disabled(!modelReady || !model.objectDetectionEnabled)
            Divider()
            Toggle("Atlas paths", isOn: $showsRoad)
            Toggle(visionText("Estimated Room Outlines"), isOn: $showsEstimatedPlacement)
            Group {
                Toggle("Vertical Edges", isOn: $model.groundEdgeEnabled)
                Toggle("Ground Filter Response", isOn: $model.groundDetectEnabled)
                Toggle("Ground Candidate Runs", isOn: $model.groundStageEnabled)
                Toggle("Clean Floor Lines", isOn: $model.cleanedEnabled)
                Toggle(visionText("Ground Lines"), isOn: $model.groundHypothesesEnabled)
                Toggle(visionText("Ground Tracking Patches"), isOn: $showsGroundTrackingPatches)
                    .disabled(!model.groundHypothesesEnabled)
                Toggle(visionText("Transitions"), isOn: $model.transitionDiagnosticsEnabled)
                Toggle(visionText("Coarse Motion Overlay"), isOn: $model.coarseMotionDiagnosticsEnabled)
            }.disabled(!showsGroundTrackingTools)
            Button("Ground Viewer", action: openGroundViewer)
            Button("Text Reader", action: openTextReader)
        }.toggleStyle(.checkbox)
    }
}

struct MenuObjectParticlePlacement: Equatable {
    let position: CGPoint
    let opacity: Double
    let scale: CGFloat
    let rotationDegrees: Double
    let depth: CGFloat
}

enum MenuObjectParticleMotion {
    static let iconSize: CGFloat = 70
    static let defaultParticleCount = 32
    static let spawnInterval: TimeInterval = 0.28

    static func depth(for index: Int) -> CGFloat {
        CGFloat((index * 7 + 3) % 12) / 11
    }

    static func travelDuration(for index: Int) -> TimeInterval {
        15 - Double(depth(for: index)) * 7
    }

    static func baseScale(for index: Int) -> CGFloat {
        0.55 + depth(for: index) * 0.85
    }

    static func renderOrder(count: Int) -> [Int] {
        (0..<max(0, count)).sorted {
            let leftDepth = depth(for: $0)
            let rightDepth = depth(for: $1)
            return leftDepth == rightDepth ? $0 < $1 : leftDepth < rightDepth
        }
    }

    static func placement(
        elapsed: TimeInterval,
        index: Int,
        count: Int,
        in size: CGSize
    ) -> MenuObjectParticlePlacement? {
        let delay = Double(index) * spawnInterval
        guard elapsed >= delay, count > 0, size.width > 0, size.height > 0 else {
            return nil
        }
        let depth = depth(for: index)
        let duration = travelDuration(for: index)
        let progress = (elapsed - delay).truncatingRemainder(dividingBy: duration) / duration
        let phase = Double(index % 7) * 0.73
        let scale = baseScale(for: index)
            + CGFloat(sin(progress * .pi + phase) * 0.04)
        let sideRank = index / 2
        let positionsPerSide = max(1, (count + 1) / 2)
        let sideFraction = (CGFloat(sideRank) + 0.5) / CGFloat(positionsPerSide)
        let halfWidth = iconSize * scale / 2
        let outerCenter = halfWidth * 0.45
        let innerCenter = max(outerCenter, size.width * 0.055 - halfWidth)
        let laneCenter = outerCenter + (innerCenter - outerCenter) * sideFraction
        let swayLimit = min(8 + depth * 18, size.width * 0.018)
        let sway = CGFloat(sin(progress * .pi * 2 + phase)) * swayLimit
        let distanceFromEdge = max(outerCenter, min(innerCenter, laneCenter + sway))
        let baseX = index.isMultiple(of: 2)
            ? distanceFromEdge
            : size.width - distanceFromEdge
        let travel = size.height + iconSize
        let y = size.height + iconSize / 2 - CGFloat(progress) * travel
        let fadeIn = min(1, progress / 0.08)
        let fadeOut = min(1, (1 - progress) / 0.28)
        let maximumOpacity = 0.10 + Double(depth) * 0.18
        let opacity = maximumOpacity * max(0, min(fadeIn, fadeOut))
        let rotation = sin(progress * .pi * 2 + phase) * (1.5 + Double(depth) * 4.5)
        return MenuObjectParticlePlacement(
            position: CGPoint(x: baseX, y: y),
            opacity: opacity,
            scale: scale,
            rotationDegrees: rotation,
            depth: depth
        )
    }
}

private struct MenuObjectBackdrop: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    let context: LabelingContext
    let objectAtlas: LabelingObjectAtlasSnapshot?
    @State private var startedAt = Date()

    private var identifiers: [String] {
        Array(Set(context.labels.map {
            LabelingClassIdentity.canonicalIdentifier($0.id)
        })).sorted()
    }

    private var particleIdentifiers: [String] {
        Array(identifiers.prefix(12)).filter { objectAtlas?.icon(for: $0) != nil }
    }

    private var particleCount: Int {
        particleIdentifiers.isEmpty
            ? 0
            : max(MenuObjectParticleMotion.defaultParticleCount, particleIdentifiers.count)
    }

    var body: some View {
        Group {
            if context == .credits {
                Color.black.opacity(0.25)
            } else if particleIdentifiers.isEmpty {
                Color.clear
            } else {
                TimelineView(.animation(minimumInterval: 1.0 / 24.0)) { timeline in
                    GeometryReader { proxy in
                        let elapsed = max(0, timeline.date.timeIntervalSince(startedAt))
                        ForEach(
                            MenuObjectParticleMotion.renderOrder(count: particleCount),
                            id: \.self
                        ) { index in
                            let identifier = particleIdentifiers[
                                index % particleIdentifiers.count
                            ]
                            if let image = objectAtlas?.icon(for: identifier),
                               let placement = MenuObjectParticleMotion.placement(
                                   elapsed: elapsed,
                                   index: index,
                                   count: particleCount,
                                   in: proxy.size
                               ) {
                                Image(decorative: image, scale: 1)
                                    .resizable()
                                    .scaledToFit()
                                    .frame(
                                        width: MenuObjectParticleMotion.iconSize,
                                        height: MenuObjectParticleMotion.iconSize
                                    )
                                    .scaleEffect(placement.scale)
                                    .rotationEffect(.degrees(placement.rotationDegrees))
                                    .position(placement.position)
                                    .opacity(placement.opacity)
                                    .shadow(
                                        color: LabelingVisualIdentity.color(for: identifier),
                                        radius: 8
                                    )
                            }
                        }
                        Rectangle()
                            .fill(
                                LinearGradient(
                                    colors: [
                                        .clear,
                                        .white.opacity(0.18),
                                        .white.opacity(0.18),
                                        .clear,
                                    ],
                                    startPoint: .leading,
                                    endPoint: .trailing
                                )
                            )
                            .frame(width: proxy.size.width, height: 2)
                            .shadow(color: .white.opacity(0.18), radius: 5)
                            .position(x: proxy.size.width / 2, y: proxy.size.height - 1)
                    }
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

}

struct DashboardView: View {
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        initialization.text(key, values)
    }

    @ObservedObject var model: LiveCaptureModel
    @State private var workspace = DashboardWorkspace.gameplay
    @State private var gameTextReader = GameTextReader()
    @StateObject private var thinkingInspector = ThinkingInspector()
    @ObservedObject var thinkingSession: ThinkingModelSession
    @AppStorage("HollowKnightVision.readGameText") private var readGameText = true
    @AppStorage("HollowKnightVision.debug.thinkingContext") private var showsThinkingContext = true
    @State private var contextExpanded = false
    @State private var showingTextReader = false

    private var textReadingContext: String {
        model.readingOpeningCutscene ? RememberedGameText.openingContext : model.skillContext
    }

    private func dispatchThinkingPolicy(_ output: ThinkingInspector.Output) async -> ThinkingHandoff? {
        let frame = output.input.frame
        guard output.validated, frame.profile == model.notebookProfile?.id,
              output.input.model == thinkingSession.model,
              frame.mode == model.gameMode.rawValue else {
            return .status("Skipped · save, model or game mode changed.")
        }
        guard model.gameMode == .modded else { return .status("Inspection only · Normal mode.") }
        let domain = frame.controlDomain
        func controlsAvailable() -> Bool {
            (domain == .gameplay && workspace == .gameplay || domain == .hacker && workspace == .hacker)
                && !showingSettings && !showingTextReader && initialization.isFirstPhaseComplete
                && !labelingTrainer.isRunning && !botTrainer.isRunning
                && frame.profile == model.notebookProfile?.id && output.input.model == thinkingSession.model
                && (frame.stateVisit == nil || frame.stateVisit == model.thinkingStateVisit)
        }
        guard controlsAvailable() else { return .status("Skipped · gameplay controls unavailable.") }
        if let script = output.script {
            return await model.runThinkingMenu(script, generation: frame.generation, enabled: controlsAvailable)
        }
        guard let plan = output.plan else { return nil }
        return .status(model.botPlayer.dispatch(plan, context: frame.context, generation: frame.generation,
                                        domain: domain))
    }

    private func thinkingFrame(image source: CGImage? = nil) -> ThinkingFrame? {
        guard let image = source ?? model.thinkingScreenshot() else { return nil }
        let atlas = model.liveWorldSnapshot.map {
            "Atlas observations: \($0.observations.count). Recorded transitions: \(model.transitionPortals.count)."
        } ?? ""
        return ThinkingFrame(image: image, profile: model.notebookProfile?.id, generation: model.skillGeneration,
                             context: textReadingContext, mode: model.gameMode.rawValue,
                             atlas: atlas, sight: model.currentSkillSight(), language: initialization.language.rawValue,
                             readsText: readGameText,
                             controlDomain: workspace == .hacker ? .hacker : .gameplay)
    }
    @State private var viewportFraming: LayerSceneFramingMode = .current
    @State private var framingRequest = 0
    @State private var viewportTransformStore = LayerSceneViewportTransformStore()
    @State private var hackerFramingRequest = 0
    @State private var hackerViewportTransformStore = LayerSceneViewportTransformStore()
    @State private var hackerFramingMode = LayerSceneFramingMode.fit
    @State private var hackerEditingTarget = HackerEditingTarget.defaultTarget
    @StateObject private var pathEditor = AtlasPathConnectionStore()
    @State private var pathTool = AtlasPathConnectionStore.Tool.jump
    @State private var lastTerrainTarget = HackerEditingTarget.ground
    @State private var objectiveRoom: String?
    @AppStorage("HollowKnightVision.debug.atlasRoad") private var showsRoad = true
    @State private var hackerRoomDrag: (id: UUID, point: CGPoint)?
    @State private var showsGameState = true
    @State private var showingSettings = false
    @State private var trainingTransferBusy = false
    @State private var trainingTransferStatus = ""
    @State private var settingsSection = DashboardSettingsSection.atlas
    @AppStorage("HollowKnightVision.debug.estimatedRoomOutlines") private var showsEstimatedPlacement = false
    @AppStorage("HollowKnightVision.debug.groundTrackingPatches")
    private var showsGroundTrackingPatches = false
    @State private var confirmsStencilReset = false
    @State private var confirmsFactoryReset = false
    @State private var initializationTargetContext: LabelingContext?
    @State private var labelingContext: LabelingContext
    @State private var labelingDraft = LabelingDraftState()
    @State private var labelingSelectedClassID: String
    @State private var labelingSavedExampleIDsByClass = [String: Set<UUID>]()
    @State private var labelingSavedExampleIDsByContext = [String: Set<UUID>]()
    @State private var labelingSaveError: String?
    @State private var recentFramesPresentation: RecentFramesPresentation?
    @State private var editingLabelingExample: SavedLabelingExample?
    @State private var pendingModelErrorExample: SavedLabelingExample?
    @State private var pendingModelErrorClassIdentifier: String?
    @State private var pendingLabelingEditorMode = LabelingEditorMode.add
    @State private var labelingEditorMode = LabelingEditorMode.add
    @State private var reopenedLabelImage: CGImage?
    @State private var labelingCaptureGroupIdentifier = UUID()
    @State private var labelingEditorIdentity = UUID()
    @State private var labelingObjectAtlas: LabelingObjectAtlasSnapshot?
    @State private var latestModelCandidate: LabelingModelCandidate?
    @State private var campaignCounts = [String: Int]()
    @State private var reviewingModelCandidate: LabelingModelCandidate?
    @State private var modelWorkspaceSelection = LabelingModelWorkspaceSelection()
    @State private var modelCandidates = [LabelingModelCandidate]()
    @State private var activeModelVersionName = "Default"
    @State private var trainedGameplayModelReady = false
    @State private var appliedDefaultObjectDetection = false
    @State private var hasGameplayTrainingLabels = false
    @State private var hasUntrainedLabelChanges = false
    @State private var untrainedLabeledImageCount = 0
    @State private var trainingAlertMessage: String?
    @State private var lockedWorkspaceMessage: String?
    @StateObject private var labelingTrainer = LabelingTrainingController()
    @StateObject private var botTrainer = BotTrainingWorkspaceController()
    @StateObject private var initialization = StencilInitializationCoordinator()
    @StateObject private var groundFeatureWindowController = GroundFeatureWindowController()
    @StateObject private var groundTruthLabelController = GroundTruthLabelController()
    private let labelingExampleStore = LabelingExampleStore()
    private let labelingDatasetExporter = LabelingDatasetExporter()
    private let labelingModelReviewStore = LabelingModelReviewStore()
    private let labelingModelVersionStore = LabelingModelVersionStore()
    private let labelingSelectionPreferences: LabelingSelectionPreferences
    private let labelingObjectReferenceStore = LabelingObjectReferenceStore()

    init(model: LiveCaptureModel, thinkingSession: ThinkingModelSession, defaults: UserDefaults = .standard) {
        self.model = model
        self.thinkingSession = thinkingSession
        let preferences = LabelingSelectionPreferences(defaults: defaults)
        labelingSelectionPreferences = preferences
        let selection = preferences.load()
        _labelingContext = State(initialValue: selection.context)
        _labelingSelectedClassID = State(initialValue: selection.classIdentifier)
    }

    private var dashboardContent: some View {
        VStack(spacing: 8) {
            canvas
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            controls
        }
        .padding(10)
        .background(Color(red: 0.012, green: 0.026, blue: 0.035))
        .preferredColorScheme(.dark)
        .frame(minWidth: 720, minHeight: 560)
        .background {
            WorkspaceExitKeyMonitor(
                workspace: showingSettings || showingTextReader ? .gameplay : workspace,
                onExit: {
                    recentFramesPresentation = nil
                    selectWorkspace(.gameplay)
                },
                onAtlasTab: !showingSettings && !showingTextReader && showsFramingControls ? cycleAtlasFraming : nil,
                onResetRoom: !showingTextReader && roomResetShortcutEnabled ? model.resetRoom : nil,
                gameHasFocus: { model.managedGameIsActive }
            )
            .frame(width: 0, height: 0)
        }
        .onChange(of: workspace) { _, value in
            model.setNativeReviewActive(!BotControlPresentationPolicy.shows(in: value))
            if groundFeatureWindowController.isOpen {
                groundFeatureWindowController.changeScene(lines: groundViewerSceneLines, center: groundViewerCenter)
            }
        }
        .onChange(of: showingSettings || showingTextReader) { _, active in model.setSettingsDialogActive(active) }
        .onReceive(model.botDemo.handoffRequests) { request in
            selectWorkspace(model.botDemo.domain == .hacker ? .hacker : .gameplay)
            model.completeBotDemoHandoff(request)
        }
        .onReceive(groundTruthLabelController.$document) { document in model.setBotHumanGround(document) }
        .onReceive(model.objectWillChange.throttle(for: .milliseconds(300), scheduler: RunLoop.main, latest: true)) { _ in
            if groundFeatureWindowController.isOpen {
                groundFeatureWindowController.refreshScene(lines: groundViewerSceneLines, center: groundViewerCenter)
            }
        }
        .onChange(of: model.objectDetectionEnabled, initial: true) { _, enabled in
            if enabled { initialization.recordObjectDetectionEnabled() }
        }
        .sheet(item: $recentFramesPresentation) { presentation in
            RecentFramesView(
                examples: presentation.examples,
                capturedFrames: presentation.capturedFrames,
                onOpen: openLabelingExample,
                onOpenCaptured: openCapturedFrame,
                onResume: resumeFromRecentFrames
            )
        }
        .onChange(of: initialization.isFirstPhaseComplete) { _, ready in
            // Match the default Windows debug view once captured stencils are usable.
            model.stencilDetectionEnabled = ready
            if !ready {
                model.objectDetectionEnabled = false
                if workspace == .model || workspace == .bot { selectWorkspace(.gameplay) }
            }
            enableDefaultObjectDetectionIfReady()
        }
        .onChange(of: initialization.examples) { _, _ in
            trainedGameplayModelReady = hasTrainedGameplayModel()
            if !trainedGameplayModelReady { model.objectDetectionEnabled = false }
            enableDefaultObjectDetectionIfReady()
        }
        .overlay {
            if showingSettings {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea().onTapGesture {
                        if !trainingTransferBusy { showingSettings = false }
                    }
                    settingsContent.environment(\.visionLanguage, initialization.language)
                }
                .accessibilityAddTraits(.isModal)
            }
        }
    }

    private var visibleContent: some View {
        Group {
            if let remainingSeconds = model.gameLostCountdown {
                ZStack {
                    Color(red: 0.012, green: 0.026, blue: 0.035)
                    Text(GameLossCountdown.message(remainingSeconds: remainingSeconds))
                        .font(.system(size: 28, weight: .semibold))
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                        .padding(32)
                }
                .preferredColorScheme(.dark)
                .frame(minWidth: 720, minHeight: 560)
                .accessibilityElement(children: .combine)
            } else {
                dashboardContent
            }
        }
    }

    var body: some View {
        visibleContent
        .task(id: thinkingSession.model) {
            guard thinkingSession.isEnabled else { return }
            let selected = thinkingSession.model
            while !Task.isCancelled {
                await thinkingSession.checkResidency(of: selected)
                if thinkingSession.state == .unloaded(selected) {
                    thinkingSession.select(selected)
                }
                do { try await Task.sleep(for: .seconds(20)) } catch { return }
            }
        }
        .task {
            guard thinkingSession.isEnabled else { return }
            model.onThinkingStateChange = { image in
                guard let state = model.thinkingSignals(profile: model.notebookProfile?.id, readsText: readGameText) else { return }
                thinkingInspector.observe(state, reader: gameTextReader, session: thinkingSession,
                    dispatch: dispatchThinkingPolicy, snapshot: { thinkingFrame(image: image) })
            }
            defer { model.onThinkingStateChange = nil }
            await thinkingInspector.monitor(reader: gameTextReader, session: thinkingSession,
                signals: { model.thinkingSignals(profile: model.notebookProfile?.id, readsText: readGameText) },
                dispatch: dispatchThinkingPolicy, snapshot: { thinkingFrame() })
        }
        .onChange(of: showsThinkingContext) { _, visible in if !visible { contextExpanded = false } }
        .onChange(of: thinkingSession.model) { _, _ in
            thinkingInspector.reset(session: thinkingSession, keepFrame: true)
        }
        .onReceive(model.$notebookProfile.removeDuplicates()) { profile in
            if profile != gameTextReader.profile {
                thinkingInspector.reset(session: thinkingSession, keepFrame: false)
                gameTextReader.selectProfile(profile)
            }
        }
        .task(id: "\(model.notebookProfile?.id.uuidString ?? "")-\(readGameText)-\(model.isCapturing)-\(initialization.language.rawValue)") {
            gameTextReader.selectProfile(model.notebookProfile)
            await gameTextReader.monitor(enabled: readGameText && model.isCapturing, language: initialization.language.rawValue) {
                model.thinkingScreenshot().map { ($0, textReadingContext) }
            }
        }
        .onAppear { model.botPlayer.didInspect = { sight, start in gameTextReader.beginInspection(sight, at: start) } }
        .onReceive(gameTextReader.$skillReading) { model.skillReading = $0 }
        .sheet(isPresented: $showingTextReader) {
            GameTextDebugView(reader: gameTextReader, enabled: $readGameText)
        }
        .alert(initialization.text("resetTitle"), isPresented: $confirmsStencilReset) {
            Button(initialization.text("cancel"), role: .cancel) {}
            Button(initialization.text("reset"), role: .destructive, action: resetStencils)
        } message: {
            Text(initialization.text("resetMessage"))
        }
        .alert(initialization.text("factoryTitle"), isPresented: $confirmsFactoryReset) {
            Button(initialization.text("cancel"), role: .cancel) {}
            Button(initialization.text("factoryAction"), role: .destructive, action: factoryReset)
        } message: {
            Text(initialization.text("factoryMessage"))
        }
        .alert(
            visionText("Training Failed"),
            isPresented: Binding(
                get: { trainingAlertMessage != nil },
                set: { if !$0 { trainingAlertMessage = nil } }
            )
        ) {
            Button(visionText("OK"), role: .cancel) {}
        } message: {
            Text(trainingAlertMessage ?? "Unknown training error")
        }
        .alert(
            visionText("Modded Hollow Knight Required"),
            isPresented: Binding(
                get: { lockedWorkspaceMessage != nil },
                set: { if !$0 { lockedWorkspaceMessage = nil } }
            )
        ) {
            Button(visionText("OK"), role: .cancel) {}
        } message: {
            Text(lockedWorkspaceMessage ?? "")
        }
        .onAppear {
            if CommandLine.arguments.contains("--show-ground-label-context")
                || CommandLine.arguments.contains("--show-ground-label-editor") {
                DispatchQueue.main.async { openGroundLabelContext() }
            }
            refreshTrainingEligibility()
            refreshLatestModelCandidate()
            autoPromoteLatestModelCandidate()
            rebuildLabelingObjectAtlas()
            initialization.refresh(mode: model.gameMode)
            model.stencilDetectionEnabled = initialization.isFirstPhaseComplete
            enableDefaultObjectDetectionIfReady()
        }
        .onDisappear {
            model.setModelReviewActive(false)
            model.setHackerWorkspaceActive(false)
            groundFeatureWindowController.close()
        }
        .onChange(of: labelingTrainer.completedRunURL) { _, completedRunURL in
            guard completedRunURL != nil else { return }
            refreshLatestModelCandidate()
            autoPromoteLatestModelCandidate()
        }
        .onChange(of: labelingTrainer.hasError) { _, hasError in
            if hasError { trainingAlertMessage = labelingTrainer.message }
        }
        .onChange(of: model.labelImage != nil) { _, isLabeling in
            if isLabeling {
                labelingDraft.reset()
                labelingSaveError = nil
                editingLabelingExample = nil
                reopenedLabelImage = nil
                labelingCaptureGroupIdentifier = UUID()
                labelingEditorIdentity = UUID()
                refreshTrainingEligibility()
                refreshLatestModelCandidate()
                if pendingModelErrorExample != nil {
                    openPendingModelErrorExampleIfNeeded()
                } else {
                    restoreStencilDraftIfAvailable()
                }
            } else {
                recentFramesPresentation = nil
                editingLabelingExample = nil
                reopenedLabelImage = nil
            }
        }
        .onChange(of: model.gameMode) { _, mode in
            initialization.refresh(mode: mode)
        }
        .onChange(of: labelingContext) { _, context in
            labelingSelectionPreferences.saveContext(context)
            if workspace == .label, model.labelImage != nil,
               editingLabelingExample?.manifest.contextIdentifier != context.storageIdentifier {
                editingLabelingExample = nil
                reopenedLabelImage = nil
                labelingDraft.reset()
                labelingCaptureGroupIdentifier = UUID()
                restoreStencilDraftIfAvailable()
            }
        }
        .onChange(of: initialization.language) { _, _ in
            initializationTargetContext = nil
            if workspace == .label || workspace == .model {
                let wasShowingSettings = showingSettings
                showingSettings = false
                selectWorkspace(.gameplay)
                showingSettings = wasShowingSettings
            }
            model.stencilDetectionEnabled = initialization.isFirstPhaseComplete
            model.refreshMenuStencilCatalog()
        }
        .onChange(of: labelingSelectedClassID) { _, _ in
            labelingSelectionPreferences.saveClassIdentifier(labelingSelectedClassID)
        }
        .onChange(of: showsRoad) { _, visible in
            if !visible, hackerEditingTarget.editsPaths { selectHackerEditingTarget(lastTerrainTarget) }
        }
        .onChange(of: atlasPathsVisible) { _, visible in
            if !visible { pathEditor.cancelSelection() }
            refreshGroundFeatureWindow()
        }
        .onChange(of: model.groundHypothesisTracking) { _, _ in
            if showsGroundTrackingPatches { refreshGroundFeatureWindow() }
        }
        .onChange(of: model.hackerCameraTransform) { _, _ in
            guard workspace == .hacker else { return }
            registerCurrentGroundRoom()
        }
        .onChange(of: model.hackerAtlasRooms.map(\.id)) { _, _ in
            guard workspace == .hacker else { return }
            registerCurrentGroundRoom()
        }
        .environment(\.visionLanguage, initialization.language)
    }

    private var canvas: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10).fill(.black)
            if let context = menuObjectBackdropContext {
                MenuObjectBackdrop(context: context, objectAtlas: labelingObjectAtlas)
                    .id(context.storageIdentifier)
                    .transition(.opacity)
                    .animation(.easeInOut(duration: 0.35), value: context)
            }
            if workspace == .ops {
                PlayerOpsView(model: model)
            } else if workspace == .stencils {
                StencilWorkspaceView(
                    coordinator: initialization,
                    onOpen: { example in
                        initializationTargetContext = LabelingContext(storageIdentifier: example.manifest.contextIdentifier)
                        openModelErrorExample(example.id)
                    },
                    onSelect: { initializationTargetContext = $0 },
                    onReset: { confirmsStencilReset = true },
                    onDelete: { example in
                        do {
                            try labelingExampleStore.delete(example)
                            if editingLabelingExample?.id == example.id {
                                editingLabelingExample = nil
                                reopenedLabelImage = nil
                                labelingDraft.reset()
                            }
                            initialization.refresh(mode: model.gameMode)
                            model.refreshMenuStencilCatalog()
                        } catch { labelingSaveError = error.localizedDescription }
                    }
                )
            } else if workspace == .bot {
                BotModelWorkspaceView(controller: botTrainer, trainingAllowed: !labelingTrainer.isRunning,
                                      reloadModel: { model.botPlayer.reloadModel() })
                    .equatable()
            } else if workspace == .model {
                LabelingModelWorkspaceView(
                    candidate: reviewingModelCandidate,
                    campaignProgress: campaignProgress,
                    campaignHistory: LabelingCampaignPolicy.history(modelCandidates),
                    context: $labelingContext,
                    selectedClassIdentifier: $labelingSelectedClassID,
                    activeVersionName: activeModelVersionName,
                    canTrain: canTrainSelectedObject,
                    hasUntrainedLabelChanges: hasUntrainedLabelChanges,
                    untrainedLabeledImageCount: untrainedLabeledImageCount,
                    isTraining: labelingTrainer.isRunning,
                    trainingProgress: labelingTrainer.progressText,
                    trainingHelp: trainingHelp,
                    onTrain: prepareSelectedObjectForTraining,
                    onStopTraining: labelingTrainer.cancel,
                    onOpenExample: { identifier, editorMode in
                        openModelErrorExample(identifier, editorMode: editorMode)
                    },
                    selection: $modelWorkspaceSelection,
                    onDatasetChanged: refreshTrainingEligibility
                )
                .id(reviewingModelCandidate?.id)
            } else if let labelImage = model.labelImage {
                LabelingEditorView(
                    image: reopenedLabelImage ?? labelImage,
                    objectAtlas: labelingObjectAtlas,
                    onShowRecentFrames: showRecentLabelingFrames,
                    onDraftCommit: autoSaveLabelingDraft,
                    context: $labelingContext,
                    draft: $labelingDraft,
                    selectedClassID: $labelingSelectedClassID,
                    initialEditorMode: labelingEditorMode,
                    gameMode: model.gameMode
                )
                .id(labelingEditorIdentity)
            } else if workspace == .hacker {
                hackerCanvas
            } else if model.liveImage != nil || !model.atlasTiles.isEmpty {
                VStack(spacing: 0) {
                    LayerSceneViewport(
                        tiles: presentationTiles,
                        mode: .world,
                        cameraPosition: .zero,
                        hiddenLayerIDs: [],
                        selectedLayerID: nil,
                        fitRequest: 0,
                        focusPoint: model.mapFocus,
                        autoFollow: false,
                        framingMode: viewportFraming,
                        framingRequest: framingRequest,
                        transformStore: viewportTransformStore,
                        fitBounds: viewportFitBounds,
                        followBounds: model.liveBounds,
                        onSelect: { _, _, _ in },
                        onInteraction: { viewportFraming = .freeFly },
                        onGamePointerEvent: model.forwardGamePointer,
                        onGroundFeatureSelect: { feature in
                            groundFeatureWindowController.show(
                                selected: feature,
                                available: featureReviewOverlay.markers
                                    .filter(\.isVisible)
                                    .compactMap(\.groundFeatureDetails),
                                sceneLines: groundViewerSceneLines
                            )
                        },
                        onPathSelect: { point, tolerance in
                            guard atlasPathsVisible else { return false }
                            let offset = model.visualPathOffset
                            return model.visualPaths.selectObjective(room: model.visualPathRoom,
                                point: CGPoint(x: point.x - offset.x, y: point.y - offset.y), tolerance: tolerance)
                        },
                        featureReviewOverlay: featureReviewOverlay,
                        showsCheckerboardBackground: menuObjectBackdropContext == nil,
                        worldBasisOffset: model.presentationCorrectionOffset
                    )
                }
            } else {
                VStack(spacing: 9) {
                    Image(systemName: "viewfinder")
                        .font(.system(size: 34, weight: .light))
                        .foregroundStyle(.cyan.opacity(0.8))
                    Text(visionText("Waiting for Hollow Knight…"))
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .blur(radius: showsModelLoadingOverGame && !thinkingSession.isReady ? 10 : 0)
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay { if showsModelLoadingOverGame { ThinkingLoadingOverlay(session: thinkingSession) } }
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.white.opacity(0.14)))
        .overlay(alignment: .topTrailing) {
            if workspace == .gameplay
                && model.labelImage == nil && reviewingModelCandidate == nil
                && (model.objectInferenceIssue != nil || model.worldStorageIssue != nil) {
                VStack(alignment: .trailing, spacing: 5) {
                    if let issue = model.objectInferenceIssue {
                        Text(issue)
                            .foregroundStyle(.red)
                            .lineLimit(2)
                    }
                    if let issue = model.worldStorageIssue {
                        Text(issue)
                            .foregroundStyle(.red)
                            .accessibilityLabel("World storage error: \(issue)")
                    }
                }
                .font(.caption2.monospaced())
                .padding(.horizontal, 7)
                .padding(.vertical, 4)
                .background(.black.opacity(0.62), in: RoundedRectangle(cornerRadius: 7))
                .padding(8)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if (workspace == .gameplay || workspace == .hacker), model.labelImage == nil, reviewingModelCandidate == nil {
                VStack(alignment: .trailing, spacing: 6) {
                    if workspace == .gameplay && model.groundReviewAvailable && model.groundHypothesesEnabled && showsGroundTrackingPatches {
                        featureHypothesisLegend
                    }
                    if workspace == .gameplay, showsEstimatedPlacement, model.hasEstimatedIslandPlacement,
                       model.hasGameplayStarted || model.liveImage == nil {
                        Text(visionText("estimatedIslandPlacement"))
                            .font(.caption2)
                            .foregroundStyle(.orange)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 5)
                            .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 7))
                    }
                }.padding(8).allowsHitTesting(false)
            }
        }
        .overlay(alignment: .bottomLeading) {
            if showsGameState && workspace == .gameplay
                && model.labelImage == nil && reviewingModelCandidate == nil {
                Text(LiveGameStatusPresentationPolicy.text(
                    recognizedContext: model.liveGameStatus,
                    roomStatus: model.activeRoomStatus
                ))
                    .font(.system(size: 30, weight: .semibold, design: .monospaced))
                    .foregroundStyle(.orange)
                    .lineLimit(1)
                    .minimumScaleFactor(0.72)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .background(.black.opacity(0.62), in: RoundedRectangle(cornerRadius: 7))
                    .padding(10)
                    .allowsHitTesting(false)
            }
        }
        .overlay(alignment: .topLeading) {
            if workspace == .gameplay,
               model.groundReviewAvailable && model.coarseMotionDiagnosticsEnabled,
               reviewingModelCandidate == nil,
               model.labelImage == nil {
                VStack(alignment: .leading, spacing: 6) {
                    if model.coarseMotionDiagnosticsEnabled {
                        Text(model.coarseMotionDiagnosticStatus)
                            .font(.caption2.monospaced())
                            .padding(.horizontal, 7)
                            .padding(.vertical, 5)
                            .background(
                                .black.opacity(0.72),
                                in: RoundedRectangle(cornerRadius: 7)
                            )
                            .accessibilityLabel(visionText("Coarse motion diagnostic status"))
                    }
                }
                    .padding(8)
                    .allowsHitTesting(false)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if contextVisible {
                GeometryReader { area in
                    ThinkingContextPanel(reader: gameTextReader, session: thinkingSession, inspector: thinkingInspector,
                        player: model.botPlayer, expanded: $contextExpanded, readsText: readGameText)
                        .frame(width: min(max(0, area.size.width - 16), contextExpanded ? 1120 : 280),
                               height: min(max(0, area.size.height - 16), contextExpanded ? 490 : 66))
                        .padding(8)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
                }
            }
        }
    }

    private var contextVisible: Bool {
        showsThinkingContext && (workspace == .gameplay || workspace == .hacker)
            && model.labelImage == nil && reviewingModelCandidate == nil
    }

    private var showsModelLoadingOverGame: Bool {
        thinkingSession.isEnabled && (workspace == .gameplay || workspace == .hacker) && model.labelImage == nil
    }

    private var hackerCanvas: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Picker("Editing target", selection: Binding(get: { hackerEditingTarget }, set: selectHackerEditingTarget)) {
                    ForEach(HackerEditingTarget.allCases) { target in
                        Text(visionText(target.rawValue)).tag(target)
                    }
                }
                .pickerStyle(.menu).labelsHidden().frame(width: 110)
                .accessibilityLabel("Hacker editing target")
                .padding(.leading, 10)
                if hackerEditingTarget == .path {
                    ForEach(AtlasPathConnectionStore.Tool.allCases) { tool in
                        Button(tool.rawValue) {
                            pathEditor.cancelSelection()
                            pathTool = tool
                        }
                        .tint(pathTool == tool ? .accentColor : .secondary)
                    }
                    if let issue = pathEditor.issue { Text(issue).font(.caption).foregroundStyle(.red) }
                    Spacer()
                } else if hackerEditingTarget == .objective {
                    Text(objectiveInstructions).font(.caption).foregroundStyle(.secondary)
                    Spacer()
                } else if hackerEditingTarget != .rooms {
                    GroundTruthLabelToolbar(controller: groundTruthLabelController,
                        hasCameraTransform: model.hackerCameraTransform != nil,
                        currentSceneName: model.hackerCameraTransform?.sceneName,
                        targetName: hackerEditingTarget.rawValue)
                } else {
                    Text("Drag a room to align it with its neighbours.")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                }
            }
            .frame(minHeight: 38).background(.regularMaterial)
            if model.hackerLiveImage != nil || !model.hackerAtlasRooms.isEmpty {
                LayerSceneViewport(
                    tiles: hackerPresentationTiles,
                    mode: .world,
                    cameraPosition: .zero,
                    hiddenLayerIDs: [],
                    selectedLayerID: nil,
                    fitRequest: 0,
                    focusPoint: model.hackerMapFocus,
                    autoFollow: false,
                    framingMode: hackerFramingMode,
                    framingRequest: hackerFramingRequest,
                    transformStore: hackerViewportTransformStore,
                    fitBounds: hackerViewportFitBounds,
                    followBounds: model.hackerLiveBounds,
                    onSelect: { _, _, _ in },
                    onInteraction: handleHackerViewportInteraction,
                    onGamePointerEvent: model.forwardGamePointer,
                    onPathSelect: { point, tolerance in
                        guard hackerEditingTarget == .objective, atlasPathsVisible else { return false }
                        return selectHackerPathObjective(point, tolerance: tolerance)
                    },
                    groundLabelEditingEnabled: hackerEditingTarget.editsTerrain || (hackerEditingTarget == .path && atlasPathsVisible),
                    onGroundLabelPointer: { phase, point, tolerance in
                        if case .began = phase {
                            registerCurrentGroundRoom()
                            model.releaseControlsForGroundLabeling()
                        }
                        if hackerEditingTarget == .path {
                            if atlasPathsVisible, case .ended = phase { editHackerPath(point, tolerance: tolerance) }
                            return
                        }
                        groundTruthLabelController.handle(
                            phase: phase,
                            atlasPoint: point,
                            atlasTolerance: tolerance,
                            rooms: groundLabelRooms
                        )
                    },
                    roomEditingEnabled: hackerEditingTarget == .rooms,
                    roomRegions: hackerRoomRegions,
                    onRoomPointer: handleHackerRoomPointer,
                    featureReviewOverlay: hackerFeatureReviewOverlay,
                    showsCheckerboardBackground: true,
                    worldBasisOffset: .zero
                )
            } else {
                Image(systemName: "hammer.fill")
                    .font(.system(size: 34, weight: .light))
                    .foregroundStyle(.green.opacity(0.8))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .accessibilityLabel(visionText("Waiting for frame-matched game camera"))
            }
        }
    }

    private var objectiveInstructions: String {
        if let key = objectiveRoom, model.hackerPaths.hasUnroutedObjective(room: key) {
            return "No route from the Knight to this point. Click another path or click the target to clear it."
        }
        return "Click a path to highlight the route. Click the target again to resume automatic exploration."
    }

    private func selectHackerPathObjective(_ point: CGPoint, tolerance: CGFloat) -> Bool {
        let placements = model.hackerAtlasRooms.reversed().map { ($0.id.uuidString, model.hackerRoomPosition($0)) }
        guard let key = model.hackerPaths.selectObjective(at: point, tolerance: tolerance, in: placements) else { return false }
        objectiveRoom = key
        return true
    }

    private func editHackerPath(_ point: CGPoint, tolerance: CGFloat) {
        for room in model.hackerAtlasRooms.reversed() {
            let origin = model.hackerRoomPosition(room)
            let local = CGPoint(x: point.x-origin.x, y: point.y-origin.y)
            guard room.bounds.insetBy(dx: -tolerance, dy: -tolerance).contains(local),
                  let graph = model.hackerPaths.graph(room: room.id.uuidString) else { continue }
            if let links = pathEditor.click(room: room.id.uuidString, point: local, tolerance: tolerance,
                                             graph: graph, tool: pathTool,
                                             calibration: model.hackerPaths.calibration(room: room.id.uuidString)) {
                model.hackerPaths.setConnections(links, room: room.id.uuidString)
            }
            return
        }
    }

    private func selectHackerEditingTarget(_ target: HackerEditingTarget) {
        hackerRoomDrag = nil
        if target == .door { groundTruthLabelController.selectDoor() }
        else if target == .spike { groundTruthLabelController.selectSpike() }
        else { groundTruthLabelController.selectAxis(target == .wall ? .vertical : .horizontal) }
        pathEditor.cancelSelection()
        if target.editsPaths {
            if !hackerEditingTarget.editsPaths { lastTerrainTarget = hackerEditingTarget }
            showsRoad = true
        }
        pathTool = .jump
        hackerEditingTarget = target
    }

    private var hackerFeatureReviewOverlay: LayerSceneFeatureReviewOverlay {
        let computed = model.hackerAtlasRooms.flatMap { room in
            model.hackerPaths.lines(room: room.id.uuidString, offset: model.hackerRoomPosition(room),
                                    visible: atlasPathsVisible)
        }
        let blockades = atlasPathsVisible ? model.blockadeOverlay(.hacker) : []
        var selection = [LayerSceneFeatureReviewOverlay.Line]()
        if atlasPathsVisible, hackerEditingTarget == .path, let selected = pathEditor.selection,
           let room = model.hackerAtlasRooms.first(where: { $0.id.uuidString == selected.room }) {
            let o = model.hackerRoomPosition(room), floor = selected.floor
            let h = model.hackerPaths.graph(room: selected.room)?.knightHeight ?? 0
            selection.append(.init(start: CGPoint(x: o.x + CGFloat(floor.left), y: o.y + CGFloat(floor.y+h)),
                                   end: CGPoint(x: o.x + CGFloat(floor.right), y: o.y + CGFloat(floor.y+h)),
                                   kind: .atlasPathSelection))
            selection.append(.init(start: CGPoint(x: o.x + CGFloat(floor.left), y: o.y + CGFloat(floor.y)),
                                   end: CGPoint(x: o.x + CGFloat(floor.right), y: o.y + CGFloat(floor.y)),
                                   kind: .atlasPathSelection))
        }
        if hackerEditingTarget.editsPaths {
            let terrain = hackerGroundLineOverlay
            return HackerTerrainOverlay.paths(terrain: terrain, computed: computed + blockades, selection: selection)
        }
        let ground = hackerGroundLineOverlay
        return LayerSceneFeatureReviewOverlay(markers: ground.markers,
            lines: ground.lines + computed + blockades + (hackerEditingTarget == .rooms ? hackerRoomOverlay.lines : []))
    }

    private var showsFramingControls: Bool {
        DashboardControlVisibility.showsFraming(workspace: workspace,
            phaseOneComplete: initialization.isFirstPhaseComplete, recognizedContext: model.liveGameStatus,
            hackerGameplayFrame: model.isCapturing && model.hasHackerAtlasPathEvidence)
    }

    private func cycleAtlasFraming() {
        if workspace == .hacker {
            selectHackerFramingMode(hackerFramingMode.nextTabPreset)
        } else {
            viewportFraming = viewportFraming.nextTabPreset
            framingRequest &+= 1
        }
    }

    private func handleHackerViewportInteraction() {
        guard hackerFramingMode != .freeFly else { return }
        selectHackerFramingMode(.freeFly)
    }

    private var hackerGroundLineOverlay: LayerSceneFeatureReviewOverlay {
        groundTruthLabelController.overlay(rooms: groundLabelRooms)
    }

    private var groundLabelRooms: [GroundTruthRoomPlacement] {
        model.hackerAtlasRooms.map { room in
            let position = model.hackerRoomPosition(room)
            let bounds = HackerTerrainRoomGeometry.editingBounds(roomBounds: room.bounds, position: position,
                isCurrent: room.sceneName == model.hackerCameraTransform?.sceneName, liveBounds: model.hackerLiveBounds,
                pathBounds: hackerEditingTarget == .door ? model.hackerPaths.pathBounds(room: room.id.uuidString) : nil)
            return .init(id: room.id, sceneName: room.sceneName, position: position, bounds: bounds)
        }
    }

    private func registerCurrentGroundRoom() {
        guard let transform = model.hackerCameraTransform,
              let room = groundLabelRooms.first(where: { $0.sceneName == transform.sceneName }),
              model.hackerLiveBounds.width > 0, model.hackerLiveBounds.height > 0 else { return }
        groundTruthLabelController.registerRoom(room, transform: transform, liveBounds: model.hackerLiveBounds)
    }

    private var hackerRoomRegions: [LayerSceneRoomRegion] {
        model.hackerAtlasRooms.map { room in
            let position = model.hackerRoomPosition(room)
            return LayerSceneRoomRegion(
                id: room.id,
                bounds: room.bounds.offsetBy(dx: position.x, dy: position.y)
            )
        }
    }

    private var hackerRoomOverlay: LayerSceneFeatureReviewOverlay {
        let boundaries = hackerRoomRegions.flatMap { room -> [LayerSceneFeatureReviewOverlay.Line] in
            let bounds = room.bounds
            return [
                .init(start: CGPoint(x: bounds.minX, y: bounds.minY),
                      end: CGPoint(x: bounds.maxX, y: bounds.minY), kind: .roomBoundary),
                .init(start: CGPoint(x: bounds.maxX, y: bounds.minY),
                      end: CGPoint(x: bounds.maxX, y: bounds.maxY), kind: .roomBoundary),
                .init(start: CGPoint(x: bounds.maxX, y: bounds.maxY),
                      end: CGPoint(x: bounds.minX, y: bounds.maxY), kind: .roomBoundary),
                .init(start: CGPoint(x: bounds.minX, y: bounds.maxY),
                      end: CGPoint(x: bounds.minX, y: bounds.minY), kind: .roomBoundary),
            ]
        }
        let connectors = model.hackerRoomConnectorSegments.map { connector in
            LayerSceneFeatureReviewOverlay.Line(
                start: connector.start,
                end: connector.end,
                kind: .roomBoundary
            )
        }
        return LayerSceneFeatureReviewOverlay(
            markers: [],
            lines: connectors + boundaries
        )
    }

    private func handleHackerRoomPointer(
        _ phase: LayerSceneRoomPointerPhase,
        _ roomID: UUID,
        _ point: CGPoint
    ) {
        switch phase {
        case .began:
            model.releaseControlsForGroundLabeling()
            hackerRoomDrag = (roomID, point)
        case .changed:
            guard let drag = hackerRoomDrag, drag.id == roomID else { return }
            model.moveHackerRoom(
                roomID,
                by: CGVector(dx: point.x - drag.point.x, dy: point.y - drag.point.y)
            )
            hackerRoomDrag = (roomID, point)
        case .ended:
            hackerRoomDrag = nil
        }
    }

    private static func presentationTiles(_ tiles: [LiveAtlasOutput.AtlasLayer], atlasID: UUID) -> [LayerSceneDrawTile] {
        tiles.map { tile in
            LayerSceneDrawTile(roomID: atlasID, layerID: tileUUID(tile.id),
                image: tile.image, bounds: tile.bounds, factor: 1, order: 0,
                roomPosition: .zero, roomScale: 1)
        }
    }

    private var hackerPresentationTiles: [LayerSceneDrawTile] {
        var tiles = model.hackerAtlasRooms.flatMap { room -> [LayerSceneDrawTile] in
            let position = model.hackerRoomPosition(room)
            return room.atlasTiles.map { tile in
                LayerSceneDrawTile(
                    roomID: room.id,
                    layerID: Self.tileUUID(tile.id),
                    image: tile.image,
                    bounds: tile.bounds,
                    factor: 1,
                    order: 0,
                    roomPosition: position,
                    roomScale: 1
                )
            }
        }
        if let live = model.hackerLiveImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.hackerLiveID,
                layerID: Self.hackerLiveID,
                image: live,
                bounds: model.hackerLiveBounds,
                factor: 1,
                order: 1,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true
            ))
        }
        if model.objectDetectionEnabled, model.objectDetectionPixelsEnabled,
           let pixels = model.objectDetectionPixelImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.hackerLiveID, layerID: Self.objectDetectionPixelsID,
                image: pixels, bounds: model.hackerLiveBounds, factor: 1, order: 2,
                roomPosition: .zero, roomScale: 1, isLiveOverlay: true, opacity: 0.5
            ))
        }
        return tiles
    }

    private var hackerViewportFitBounds: CGRect? {
        // Once there is a map, a preview arriving between atlas inserts (or a
        // menu camera) must not change the extent of the mapped world.
        var paths = CGRect.null
        if showsRoad && hackerEditingTarget.editsPaths {
            for room in model.hackerAtlasRooms {
                guard let path = model.hackerPaths.pathBounds(room: room.id.uuidString) else { continue }
                let offset = model.hackerRoomPosition(room)
                paths = paths.union(path.offsetBy(dx: offset.x, dy: offset.y))
            }
        }
        return LiveAtlasPresentationPolicy.hackerFitBounds(mapped: model.hackerAtlasBounds,
            live: model.hackerLiveBounds, paths: paths)
    }

    private var featureReviewOverlay: LayerSceneFeatureReviewOverlay {
        let showGround = model.groundReviewAvailable && model.groundHypothesesEnabled
        let ownsGround = showGround && model.usesIslandGroundProjection
        let debug = LiveFeatureReviewOverlay.make(
            groundHypotheses: showGround
                ? model.groundHypothesisTracking : nil,
            transitionPortals: model.groundReviewAvailable && model.transitionDiagnosticsEnabled
                ? model.transitionPortals : [],
            showsTrackingPatches: showsGroundTrackingPatches,
            selectedGroundFeature: groundFeatureWindowController.selectedFeature,
            persistentGroundLines: ownsGround ? model.ownedGroundOverlayLines : nil,
            walkedGround: showGround ? model.visibleWalkedGround + (ownsGround ? [] :
                model.visualPaths.walkedGround(room: model.visualPathRoom, offset: model.visualPathOffset)) : [],
            liveBounds: model.liveImage == nil ? .zero : model.liveBounds
        )
        let placement = LiveIslandOverlayGeometry.visibleOverlay(model.islandPlacementOverlay,
            showsEstimatedPlacement: showsEstimatedPlacement,
            hasGameplayStarted: model.hasGameplayStarted || model.liveImage == nil)
        let roads = model.visualPaths.lines(room: model.visualPathRoom, offset: model.visualPathOffset, visible: atlasPathsVisible)
        let blockades = atlasPathsVisible ? model.blockadeOverlay(.gameplay) : []
        let terrain = model.visualTerrainOverlay()
        return LayerSceneFeatureReviewOverlay(markers: debug.markers + terrain.markers,
            lines: roads + placement.lines + terrain.lines + debug.lines + blockades)
    }

    private var groundViewerSceneLines: [LayerSceneFeatureReviewOverlay.Line] {
        if workspace == .hacker {
            return model.hackerAtlasRooms.flatMap { room in
                let key = room.id.uuidString, offset = model.hackerRoomPosition(room)
                return model.hackerPaths.groundLines(room: key, offset: offset)
                    + model.hackerPaths.spikeLines(room: key, offset: offset)
                    + model.hackerPaths.lines(room: key, offset: offset, visible: atlasPathsVisible)
            }
        }
        return model.visualPaths.groundLines(room: model.visualPathRoom, offset: model.visualPathOffset)
            + model.visualPaths.lines(room: model.visualPathRoom, offset: model.visualPathOffset, visible: atlasPathsVisible)
    }

    private var groundViewerCenter: CGPoint { workspace == .hacker ? model.hackerMapFocus : model.mapFocus }

    private func refreshGroundFeatureWindow() {
        guard groundFeatureWindowController.isOpen else { return }
        groundFeatureWindowController.refresh(
            available: featureReviewOverlay.markers.filter { $0.coordinateSpace == .atlas }.compactMap(\.groundFeatureDetails),
            sceneLines: groundViewerSceneLines
        )
    }

    private var recognizedLabelingContext: LabelingContext? {
        guard let identifier = model.liveGameContextIdentifier else { return nil }
        return LabelingContext(storageIdentifier: identifier)
    }

    private var atlasPathsVisible: Bool {
        guard showsRoad, model.isCapturing,
              model.labelPauseState != .pausing, model.labelPauseState != .paused,
              model.labelPauseState != .resuming else { return false }
        switch workspace {
        case .gameplay:
            return LiveAtlasPresentationPolicy.showsPaths(enabled: showsRoad, context: recognizedLabelingContext,
                presentedGameplayFrame: model.hasAtlasPathEvidence(for: model.liveImage))
        case .hacker: return model.hasHackerAtlasPathEvidence
        default: return false
        }
    }

    private var menuObjectBackdropContext: LabelingContext? {
        guard workspace == .gameplay,
              model.labelImage == nil,
              reviewingModelCandidate == nil,
              let context = recognizedLabelingContext,
              context != .game else { return nil }
        return context
    }

    private var featureHypothesisLegend: some View {
        VStack(alignment: .leading, spacing: 2) {
            legendRow("Green · camera-consistent ground", Color(red: 0.27, green: 1, blue: 0.47))
            legendRow("Purple · matched reference patch", Color(red: 0.75, green: 0.31, blue: 1))
            legendRow("Orange · occluded / waiting", .orange)
            legendRow("Red · disagrees with camera estimate", .red)
        }
        .font(.caption2.monospaced())
        .padding(.horizontal, 7)
        .padding(.vertical, 5)
        .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 7))
        .accessibilityLabel(visionText("Ground tracking patch colors"))
    }

    private func legendRow(_ label: String, _ color: Color) -> some View {
        HStack(spacing: 5) {
            Rectangle().fill(color).frame(width: 10, height: 3)
            Text(label)
        }
    }

    private var presentationTiles: [LayerSceneDrawTile] {
        var tiles = [LayerSceneDrawTile]()
        let includesAtlas = LiveAtlasPresentationPolicy.includesAtlas(
            hasGameplayStarted: model.hasGameplayStarted || (model.liveImage == nil && !model.atlasTiles.isEmpty)
        )
        if includesAtlas, !model.atlasTiles.isEmpty {
            tiles.append(contentsOf: Self.presentationTiles(model.atlasTiles, atlasID: Self.atlasID))
        } else if includesAtlas,
                  let atlas = model.atlasImage,
                  let bounds = model.atlasBounds {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.atlasID,
                layerID: Self.atlasID,
                image: atlas,
                bounds: bounds,
                factor: 1,
                order: 0,
                roomPosition: .zero,
                roomScale: 1
            ))
        }
        if let live = model.liveImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.liveID,
                image: live,
                bounds: model.liveBounds,
                factor: 1,
                order: 1,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                coversSavedGround: true
            ))
        }
        if model.stencilDetectionEnabled, let stencil = model.hudStencilImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID, layerID: Self.hudStencilID,
                image: stencil, bounds: model.liveBounds, factor: 1, order: 3,
                roomPosition: .zero, roomScale: 1, isLiveOverlay: true
            ))
        }
        if model.stencilDetectionEnabled,
           let stencil = model.menuStencilImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.menuStencilID,
                image: stencil,
                bounds: model.liveBounds,
                factor: 1,
                order: 4,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true
            ))
        }
        if model.objectDetectionEnabled,
           model.objectDetectionPixelsEnabled,
           let pixels = model.objectDetectionPixelImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.objectDetectionPixelsID,
                image: pixels,
                bounds: model.liveBounds,
                factor: 1,
                order: 2,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.5
            ))
        }
        if model.groundReviewAvailable, model.coarseMotionDiagnosticsEnabled,
           let motion = model.coarseMotionDiagnosticImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.coarseMotionID,
                image: motion,
                bounds: model.liveBounds,
                factor: 1,
                order: 2,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.48
            ))
        }
        if model.groundReviewAvailable, model.groundEdgeEnabled,
           let edge = model.groundEdgeImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.groundEdgeID,
                image: edge,
                bounds: model.liveBounds,
                factor: 1,
                order: 3,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.5
            ))
        }
        if model.groundReviewAvailable, model.groundDetectEnabled,
           let detection = model.groundDetectImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.groundDetectID,
                image: detection,
                bounds: model.liveBounds,
                factor: 1,
                order: 4,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.5
            ))
        }
        if model.groundReviewAvailable, model.groundStageEnabled,
           let stages = model.groundStageImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.groundStageID,
                image: stages,
                bounds: model.liveBounds,
                factor: 1,
                order: 5,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.85
            ))
        }
        if model.groundReviewAvailable, model.cleanedEnabled,
           let cleaned = model.cleanedImage {
            tiles.append(LayerSceneDrawTile(
                roomID: Self.liveID,
                layerID: Self.cleanedID,
                image: cleaned,
                bounds: model.liveBounds,
                factor: 1,
                order: 8,
                roomPosition: .zero,
                roomScale: 1,
                isLiveOverlay: true,
                opacity: 0.95
            ))
        }
        return tiles
    }

    private var viewportFitBounds: CGRect? {
        let live = model.liveBounds
        guard live.width.isFinite, live.height.isFinite,
              live.width > 0, live.height > 0 else { return model.atlasBounds }
        guard let atlas = model.atlasBounds,
              atlas.width.isFinite, atlas.height.isFinite,
              atlas.width > 0, atlas.height > 0 else { return live }
        return atlas.union(live)
    }

    private static let atlasID = UUID(uuidString: "E2249EC3-82A2-48CA-BF0C-53D5AA9A0595")!
    private static let liveID = UUID(uuidString: "BF8D7D91-2268-44EE-8E5E-46B3907C3891")!
    private static let hackerAtlasID = UUID(uuidString: "AEE53053-7CD8-4F93-AEF8-FD73333F63F1")!
    private static let hackerLiveID = UUID(uuidString: "CE14743E-4117-4149-8ACB-5A93113DBED7")!
    private static let objectDetectionPixelsID = UUID(
        uuidString: "6126A0E8-2761-4BCD-928A-59F27BD67CA5"
    )!
    private static let hudStencilID = UUID(uuidString: "918732F3-BC4D-40A2-8171-5FB25CE6E361")!
    private static let menuStencilID = UUID(
        uuidString: "B746551E-0D65-47B9-A62C-6D4B405E563F"
    )!
    private static let coarseMotionID = UUID(uuidString: "94317406-A5BE-49D4-A039-68B1910A92E8")!
    private static let groundEdgeID = UUID(uuidString: "19F853D7-D30D-4277-9018-D9BFB04A5978")!
    private static let groundDetectID = UUID(uuidString: "0FD75630-C300-4F22-91F9-1F8534858D49")!
    private static let groundStageID = UUID(uuidString: "A8C7D0F9-90B7-43CB-99E7-2D880487A94F")!
    private static let cleanedID = UUID(uuidString: "E6856675-C49E-4C22-A9F1-35FEF78F0863")!

    static func tileUUID(_ id: Int) -> UUID {
        let bits = UInt64(bitPattern: Int64(id))
        let high = String(format: "%04llX", bits >> 48)
        let low = String(format: "%012llX", bits & 0x0000_FFFF_FFFF_FFFF)
        return UUID(uuidString: "7A11A500-\(high)-4000-8000-\(low)")!
    }

    private var campaignProgress: [LabelingCampaignProgress] {
        selectedTrainingClassIdentifiers.sorted().map {
            LabelingCampaignProgress(id: $0, count: campaignCounts[$0, default: 0])
        }
    }

    private var controls: some View {
        HStack(spacing: 8) {
            workspaceSelector
            if let guidance = initialization.guidance(
                labelingContext: workspace == .label ? labelingContext : nil,
                selectedClassIdentifier: labelingSelectedClassID,
                navigationContext: initializationTargetContext
            ) {
                Text(guidance)
                    .foregroundStyle(.blue)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(guidance)
                    .frame(minWidth: 80, maxWidth: .infinity, alignment: .leading)
                    .accessibilityIdentifier("stencil-guidance")
            } else {
                Spacer(minLength: 0)
            }
            if let labelingSaveError {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red).help(labelingSaveError)
            }
            if PathRecordingPresentationPolicy.showsControl(
                workspace: workspace, recognizedContext: model.liveGameStatus
            ) && model.gameMode == .modded {
                pathRecordingButton
            }
            if BotControlPresentationPolicy.shows(in: workspace) {
                BotControlsView(model: model, domain: workspace == .hacker ? .hacker : .gameplay,
                    canStart: !labelingTrainer.isRunning && !botTrainer.isRunning
                    && initialization.isFirstPhaseComplete && model.canStartBotDemo,
                    action: toggleBotControl)
            }
            if let issue = model.pathRecordingIssue {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red).help(issue)
            }
            if showsFramingControls {
                if workspace == .hacker {
                    hackerFramingButton(.freeFly, title: "Free Fly")
                    hackerFramingButton(.fit, title: "Fit")
                    hackerFramingButton(.current, title: "Current")
                } else {
                    framingButton(.freeFly, title: "Free Fly")
                    framingButton(.fit, title: "Fit")
                    framingButton(.current, title: "Current")
                }
            }
            Button { showingSettings.toggle() } label: {
                Image(systemName: "gearshape")
            }
            .accessibilityLabel(initialization.text("settings"))
            .help(initialization.text("settings"))

        }
        .font(.caption)
        .buttonStyle(.bordered)
        .lineLimit(1)
    }

    private func toggleBotControl() {
        if model.botDemo.isActive { model.botPlayer.automaticPoliciesEnabled = false; model.stopBotDemo(reason: "Stopped with B"); return }
        if model.botPlayer.isActive { model.botPlayer.automaticPoliciesEnabled = false; model.botPlayer.stop(); return }
        guard !labelingTrainer.isRunning && !botTrainer.isRunning else { model.botDemo.rejectStart("Wait for Model training to finish before starting the bot."); return }
        guard initialization.isFirstPhaseComplete else { model.botDemo.rejectStart("Complete stencil setup before starting the bot."); return }
        let inGameplay = model.liveGameStatus == "Gameplay"
        let domain: BotDomain = workspace == .hacker ? .hacker : .gameplay
        if !inGameplay { selectWorkspace(.gameplay) }
        // An accessibility press or a click on an inactive window may arrive
        // before AppKit has made the dashboard key. Acquire focus for this
        // explicit user action, then start after activation has been delivered.
        NSApp.activate(ignoringOtherApps: true)
        NSApp.windows.first { $0.title == "Hollow Knight Vision" && $0.isVisible }?.makeKeyAndOrderFront(nil)
        DispatchQueue.main.async {
            model.botPlayer.automaticPoliciesEnabled = true
            model.startBotDemo(inGameplay: inGameplay, domain: domain)
        }
    }

    private func hasTrainedGameplayModel() -> Bool {
        // Bundled models and models trained on a previous, cleared dataset do not
        // unlock detection for a fresh setup. Require an active artifact and a
        // completed run that evaluated one of the user's current gameplay labels.
        let exampleIDs = Set(initialization.examples.filter {
            $0.manifest.contextIdentifier == "game" && $0.manifest.annotations.contains { !$0.isHardNegative }
        }.map(\.id))
        return (try? labelingModelVersionStore.hasTrainedGameplayModel(
            exampleIDs: exampleIDs, candidates: modelCandidates)) == true
    }

    private var settingsContent: some View {
        VStack(spacing: 0) {
            HStack {
                Text(initialization.text("settings")).font(.title2.weight(.semibold))
                Spacer()
                Button(visionText("Done")) { showingSettings = false }
                    .disabled(trainingTransferBusy)
                    .keyboardShortcut(.cancelAction)
            }.padding(20)
            Divider()
            HStack(alignment: .top, spacing: 0) {
                VStack(spacing: 4) {
                        ForEach(DashboardSettingsSection.allCases) { section in
                        Button { settingsSection = section } label: {
                            Label(visionText(section.rawValue), systemImage: section.symbol)
                                .fontWeight(settingsSection == section ? .semibold : .regular)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(10)
                                .background(settingsSection == section ? Color.blue.opacity(0.24) : .clear,
                                            in: RoundedRectangle(cornerRadius: 7))
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityAddTraits(settingsSection == section ? .isSelected : [])
                    }
                    Spacer(minLength: 0)
                }
                .padding(10)
                .frame(width: 160)
                Divider()
                ScrollView {
                    VStack(alignment: .leading, spacing: 20) {
                        Text(visionText(settingsSection.rawValue)).font(.headline)
                        switch settingsSection {
                        case .atlas:
                            AtlasManagerView(model: model, workspace: workspace)
                                .id(workspace == .hacker)
                        case .debugView:
                            DebugView(model: model,
                                showsGroundTrackingTools: DebugViewAvailabilityPolicy.showsGroundTrackingTools(
                                    workspace: workspace, recognizedContext: model.liveGameStatus),
                                showsThinkingContext: $showsThinkingContext, showsGameState: $showsGameState, showsGroundTrackingPatches: $showsGroundTrackingPatches,
                                showsRoad: $showsRoad, showsEstimatedPlacement: $showsEstimatedPlacement,
                                stencilReady: initialization.isFirstPhaseComplete,
                                modelReady: initialization.isFirstPhaseComplete && trainedGameplayModelReady,
                                openGroundViewer: {
                                    groundFeatureWindowController.showScene(lines: groundViewerSceneLines, center: groundViewerCenter)
                                }, openTextReader: {
                                    showingSettings = false
                                    showingTextReader = true
                                })
                        case .system:
                            if model.canUseOps && model.liveGameStatus == "Gameplay" {
                                PlayerOpsView(model: model)
                                Divider()
                            }
                            Picker(initialization.text("language"), selection: $initialization.language) {
                                ForEach(HollowKnightMenuLanguage.allCases, id: \.self) { language in
                                    Text(language.nativeName).tag(language)
                                }
                            }
                            Divider()
                            Text("Training library").font(.headline)
                            HStack {
                                Button("Export Training Library…") { transferTrainingLibrary(importing: false) }
                                Button("Import Training Library…") { transferTrainingLibrary(importing: true) }
                            }
                            .disabled(trainingTransferBusy)
                            if !trainingTransferStatus.isEmpty {
                                Text(trainingTransferStatus).foregroundStyle(.secondary).textSelection(.enabled)
                            }
                            Divider()
                            Button(initialization.text("factory"), role: .destructive) {
                                showingSettings = false
                                confirmsFactoryReset = true
                            }
                            .disabled(trainingTransferBusy)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(20)
                }
            }
        }
        .frame(width: 660, height: 460)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(.white.opacity(0.15)))
        .padding(24)
    }

    private func transferTrainingLibrary(importing: Bool) {
        guard !trainingTransferBusy else { return }
        guard !labelingTrainer.isRunning, !botTrainer.isRunning, !model.botDemonstrations.isRecording else {
            trainingTransferStatus = "Finish training or recording before transferring the library."
            return
        }
        let fileType = UTType(filenameExtension: "hkvtraining") ?? .data
        let panel: NSSavePanel
        if importing {
            let open = NSOpenPanel()
            open.canChooseDirectories = false
            open.allowsMultipleSelection = false
            panel = open
        } else {
            panel = NSSavePanel()
            let day = Date().formatted(.iso8601.year().month().day().dateSeparator(.dash))
            let suffix = UUID().uuidString.prefix(8).lowercased()
            panel.nameFieldStringValue = "Vision Training \(day) \(suffix).hkvtraining"
        }
        panel.allowedContentTypes = [fileType]
        guard panel.runModal() == .OK, let bundle = panel.url else { return }
        let support = LabelingExampleStore.defaultRootURL().deletingLastPathComponent().deletingLastPathComponent()
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/HollowKnightVisionTrainer")
        trainingTransferBusy = true
        trainingTransferStatus = importing ? "Importing training library…" : "Exporting training library…"
        Task { @MainActor in
            defer { trainingTransferBusy = false }
            do {
                trainingTransferStatus = try await Task.detached(priority: .utility) {
                    try TrainingLibraryTransfer.run(importing: importing, bundle: bundle, support: support, helper: helper)
                }.value
                if importing {
                    initialization.refresh(mode: model.gameMode)
                    refreshTrainingEligibility()
                    refreshLatestModelCandidate()
                    botTrainer.refresh()
                    model.refreshMenuStencilCatalog()
                    model.reloadActiveObjectModels()
                }
            } catch {
                trainingTransferStatus = error.localizedDescription
            }
        }
    }

    private func openGroundLabelContext() {
        model.groundHypothesesEnabled = true
        selectWorkspace(.hacker)
        selectHackerEditingTarget(.ground)
        registerCurrentGroundRoom()
    }

    private var visibleWorkspaces: [DashboardWorkspace] {
        DashboardControlVisibility.workspaces.filter { destination in
            if destination == .model || destination == .bot { return initialization.isFirstPhaseComplete }
            if destination == .hacker { return model.canUseHacker && model.liveGameStatus == "Gameplay" }
            return true
        }
    }

    private var roomResetShortcutEnabled: Bool {
        let inResetContext = showingSettings ? settingsSection == .system
            : (BotControlPresentationPolicy.shows(in: workspace) || workspace == .ops)
        return inResetContext && model.liveGameStatus == "Gameplay" && model.canUseOps && model.canResetRoom
    }

    private func workspaceTitle(_ destination: DashboardWorkspace) -> String {
        destination == .stencils ? initialization.buttonTitle : visionText(destination.rawValue)
    }

    private var workspaceSelection: Binding<DashboardWorkspace> {
        Binding(get: { workspace }, set: selectWorkspace)
    }

    private var workspaceSelector: some View {
        StableSegmentedPicker<DashboardWorkspace>(
            label: "Workspace", choices: visibleWorkspaces,
            title: workspaceTitle, selection: workspaceSelection
        )
        .frame(width: 480)
        .overlay { workspaceShortcuts }
    }

    private var workspaceShortcuts: some View {
        ZStack {
            if contextVisible && !showingSettings {
                Button("Toggle Thinking context") { contextExpanded.toggle() }.keyboardShortcut("t", modifiers: [])
            }
            ForEach(DashboardControlVisibility.shortcutWorkspaces) { destination in
                Button("Open \(destination.rawValue)") { performWorkspaceShortcut(destination) }
                    .keyboardShortcut(KeyEquivalent(destination.shortcutCharacter), modifiers: [])
            }
        }
        .frame(width: 1, height: 1)
        .opacity(0)
        .accessibilityHidden(true)
        .allowsHitTesting(false)
    }

    private func performWorkspaceShortcut(_ destination: DashboardWorkspace) {
        if destination != .gameplay && destination != .stencils && workspace == destination {
            selectWorkspace(.gameplay)
        } else {
            selectWorkspace(destination)
        }
    }

    private var pathRecordingButton: some View {
        Button(action: model.togglePathRecording) {
            HStack(spacing: 4) {
                if model.isPathCheckpointPending
                    || (model.isPathPlaybackActive && model.pathRecordingStartedAt == nil) {
                    ProgressView()
                        .controlSize(.small)
                } else {
                    Image(systemName: model.isPathRecording ? "stop.circle.fill" : "record.circle")
                }
                Text(visionText("K")).fontWeight(.semibold)
            }
        }
        .keyboardShortcut("k", modifiers: [])
        .accessibilityLabel(model.isPathRecording ? "Stop replay and learning recording" : "Record replay and bot learning")
        .accessibilityIdentifier("unified-recording-control")
        .disabled(
            model.isPathPlaybackActive
                || model.isPathCheckpointPending
                || (!model.isPathRecording
                    && (!model.canStartPathRecording || labelingTrainer.isRunning || botTrainer.isRunning))
        )
        .tint(
            model.isPathPlaybackActive ? .purple
                : (model.isPathRecording ? .red : .accentColor)
        )
        .help(model.canUseHacker ? "K records one replay and both Hacker and Gameplay training clips."
              : "K records a replay and a Gameplay training clip.")
        .contextMenu {
            Button("Show last replay recording") {
                if let url = model.lastRecordedPathURL { NSWorkspace.shared.activateFileViewerSelecting([url]) }
            }.disabled(model.lastRecordedPathURL == nil)
            Button("Show learning recordings") { NSWorkspace.shared.open(BotDemonstrationRecorder.defaultRootURL) }
        }
    }

    private func selectWorkspace(_ destination: DashboardWorkspace) {
        guard !showingSettings, destination != workspace else { return }
        guard (destination != .model && destination != .bot) || initialization.isFirstPhaseComplete else { return }
        if destination == .hacker && (!model.canUseHacker || model.liveGameStatus != "Gameplay") {
            lockedWorkspaceMessage = model.gameMode == .normal
                ? "Start Vision in Modded mode to use Hacker. Gameplay recognition and the visual atlas remain available in Normal mode."
                : "Hacker will unlock after a compatible Vision receiver provides ground-truth telemetry."
            return
        }
        if destination == .ops {
            showingSettings = true
            return
        }
        guard !labelingTrainer.isRunning && !botTrainer.isRunning,
              !model.isPathRecording,
              !model.isPathCheckpointPending else { return }
        if destination == .label {
            guard model.canToggleLabelMode else { return }
            labelingEditorMode = .add
        }


        if workspace == .model || workspace == .bot || reviewingModelCandidate != nil {
            closeModelReview()
        }
        if model.isLabelModeActive {
            guard model.canToggleLabelMode else { return }
            model.toggleLabelMode()
        }

        switch destination {
        case .gameplay, .ops, .stencils:
            workspace = destination
            if let target = initializationTargetContext,
               !initialization.steps.contains(where: { $0.context == target }) {
                initializationTargetContext = nil
            }
            if destination == .stencils { initialization.refresh(mode: model.gameMode) }
        case .hacker:
            workspace = .hacker
            if hackerEditingTarget.editsPaths { showsRoad = true }
            registerCurrentGroundRoom()
            hackerFramingRequest &+= 1
        case .label:
            initialization.refresh(mode: model.gameMode)
            labelingContext = initialization.labelContextForCapture(
                recognized: recognizedLabelingContext, navigation: initializationTargetContext,
                fallback: labelingContext
            )
            labelingSelectedClassID = initialization.navigationStep(context: initializationTargetContext)
                .flatMap { $0.context == labelingContext ? $0.classIdentifier : nil }
                ?? labelingContext.labels.first?.id ?? ""
            workspace = .label
            model.toggleLabelMode()
        case .bot:
            workspace = .bot
            model.setModelReviewActive(true)
        case .model:
            labelingContext = .game
            if !LabelingSharedModelPolicy.classIdentifiers.contains(labelingSelectedClassID) {
                labelingSelectedClassID = LabelingContext.game.labels.first {
                    LabelingSharedModelPolicy.classIdentifiers.contains($0.id)
                }?.id ?? ""
            }
            workspace = .model
            reviewingModelCandidate = latestModelCandidate
            model.setModelReviewActive(true)
        }
        model.setHackerWorkspaceActive(workspace == .hacker)
    }

    private func closeModelReview() {
        model.setModelReviewActive(false)
        reviewingModelCandidate = nil
    }

    private func framingButton(_ mode: LayerSceneFramingMode, title: String) -> some View {
        Button(visionText(title)) {
            viewportFraming = mode
            framingRequest &+= 1
        }
        .tint((viewportFraming == mode || (mode == .current && viewportFraming.followsCurrent)) ? .cyan : .gray)
        .overlay(
            RoundedRectangle(cornerRadius: 6)
                .stroke((viewportFraming == mode || (mode == .current && viewportFraming.followsCurrent)) ? Color.cyan : .clear, lineWidth: 1.5)
                .allowsHitTesting(false)
        )
    }

    private func hackerFramingButton(_ mode: LayerSceneFramingMode, title: String) -> some View {
        Button(visionText(title)) {
            selectHackerFramingMode(mode)
        }
        .tint((hackerFramingMode == mode || (mode == .current && hackerFramingMode.followsCurrent)) ? .cyan : .gray)
        .overlay(
            RoundedRectangle(cornerRadius: 6)
                .stroke((hackerFramingMode == mode || (mode == .current && hackerFramingMode.followsCurrent)) ? Color.cyan : .clear, lineWidth: 1.5)
                .allowsHitTesting(false)
        )
    }

    private func selectHackerFramingMode(_ mode: LayerSceneFramingMode) {
        hackerFramingMode = mode
        hackerFramingRequest &+= 1
    }

    private func autoSaveLabelingDraft(_ committedDraft: LabelingDraftState) {
        guard LabelingAutosavePolicy.shouldPersist(
            rectangles: committedDraft.rectangles,
            isEditingSavedExample: editingLabelingExample != nil
        ) else { return }
        do {
            try persistCurrentLabelingExample(rectangles: committedDraft.rectangles)
            labelingSaveError = nil
            rebuildLabelingObjectAtlas()
            initialization.refresh(mode: model.gameMode)
            // Keep guidance in the footer while the user finishes the screen.
            // Never interrupt labeling with another setup sheet.

        } catch {
            labelingSaveError = "Auto-save failed: \(error.localizedDescription)"
        }
    }

    private func showRecentLabelingFrames() {
        do {
            let presentation = RecentFramesPresentation(
                examples: try labelingExampleStore.loadTrainingExamples(),
                capturedFrames: model.recentCapturedFrames()
            )
            labelingSaveError = nil
            recentFramesPresentation = presentation
        } catch {
            labelingSaveError = "Recent frames failed: \(error.localizedDescription)"
        }
    }

    private func resumeFromRecentFrames() {
        recentFramesPresentation = nil
        workspace = .gameplay
        guard model.isLabelModeActive, model.canToggleLabelMode else { return }
        model.toggleLabelMode()
    }

    private func openCapturedFrame(_ frame: RecentCapturedFrame) {
        let openState = RecentCapturedFrameLabelingPolicy.stateForOpening(
            frame,
            fallbackContext: labelingContext,
            fallbackClassIdentifier: labelingSelectedClassID
        )
        do {
            let savedExample: SavedLabelingExample?
            if openState.draft.rectangles.isEmpty {
                savedExample = nil
            } else {
                savedExample = try labelingExampleStore.saveDraft(
                    image: frame.image,
                    context: openState.context,
                    rectangles: openState.draft.rectangles,
                    captureGroupIdentifier: labelingCaptureGroupIdentifier,
                    languageIdentifier: initialization.language.rawValue
                )
            }
            labelingContext = openState.context
            labelingSelectedClassID = openState.selectedClassIdentifier
            labelingDraft = openState.draft
            labelingEditorMode = .add
            editingLabelingExample = savedExample
            reopenedLabelImage = frame.image
            labelingEditorIdentity = UUID()
            labelingSaveError = nil
            refreshTrainingEligibility()
        } catch {
            labelingSaveError = "Open captured frame failed: \(error.localizedDescription)"
        }
    }

    private func openLabelingExample(_ example: SavedLabelingExample) {
        labelingEditorMode = .add
        openLabelingExample(example, preferredClassIdentifier: nil)
    }

    private func openLabelingExample(
        _ example: SavedLabelingExample,
        preferredClassIdentifier: String?
    ) {
        do {
            guard let openState = LabelingFrameOpeningPolicy.stateForOpening(
                example.manifest,
                preferredClassIdentifier: preferredClassIdentifier
            ) else {
                throw LabelingExampleStoreError.unsupportedContext(
                    example.manifest.contextIdentifier
                )
            }
            let image = try labelingExampleStore.loadImage(for: example)
            labelingContext = openState.context
            labelingSelectedClassID = openState.selectedClassIdentifier
            labelingDraft = openState.draft
            editingLabelingExample = example
            reopenedLabelImage = image
            labelingEditorIdentity = UUID()
            labelingSaveError = nil
        } catch {
            labelingSaveError = "Open failed: \(error.localizedDescription)"
        }
    }

    private func openModelErrorExample(
        _ exampleIdentifier: UUID,
        editorMode: LabelingEditorMode = .add
    ) {
        do {
            guard let example = try labelingExampleStore.loadExamples().first(where: {
                $0.id == exampleIdentifier
            }) else {
                labelingSaveError = "The source error frame is no longer available."
                return
            }
            pendingModelErrorExample = example
            pendingModelErrorClassIdentifier = labelingSelectedClassID
            pendingLabelingEditorMode = editorMode
            closeModelReview()
            workspace = .label
            if model.labelImage != nil {
                if pendingModelErrorExample != nil {
                    openPendingModelErrorExampleIfNeeded()
                } else {
                    restoreStencilDraftIfAvailable()
                }
            } else if model.canToggleLabelMode {
                model.toggleLabelMode()
            } else {
                pendingModelErrorExample = nil
                pendingModelErrorClassIdentifier = nil
                labelingSaveError = "Label mode is not ready to open this error frame."
            }
        } catch {
            labelingSaveError = "Open error frame failed: \(error.localizedDescription)"
        }
    }

    private func openPendingModelErrorExampleIfNeeded() {
        guard let example = pendingModelErrorExample,
              let classIdentifier = pendingModelErrorClassIdentifier else { return }
        pendingModelErrorExample = nil
        pendingModelErrorClassIdentifier = nil
        labelingEditorMode = pendingLabelingEditorMode
        pendingLabelingEditorMode = .add
        openLabelingExample(
            example,
            preferredClassIdentifier: classIdentifier
        )
    }

    private var selectedModelIdentifier: String {
        LabelingSharedModelPolicy.modelIdentifier
    }

    private var selectedModelName: String {
        LabelingSharedModelPolicy.modelName
    }

    private var selectedTrainingClassIdentifiers: Set<String> {
        LabelingSharedModelPolicy.classIdentifiers
    }

    private var canTrainSelectedObject: Bool {
        let isLabeling = model.labelImage != nil && labelingContext == .game
        guard !labelingTrainer.isRunning && !botTrainer.isRunning,
              isLabeling || workspace == .model else { return false }
        let saved = selectedTrainingClassIdentifiers.reduce(into: Set<UUID>()) {
            $0.formUnion(labelingSavedExampleIDsByClass[$1] ?? [])
        }
        // Train the shared gameplay model regardless of which object is selected.
        return LabelingTrainingEligibility.canTrain(
            classIdentifiers: selectedTrainingClassIdentifiers,
            currentRectangles: isLabeling ? labelingDraft.rectangles : [],
            savedExampleIdentifiers: saved,
            editingExampleIdentifier: isLabeling ? editingLabelingExample?.id : nil
        )
    }

    private var trainingHelp: String {
        canTrainSelectedObject
            ? visionText("updateModel", ["model": visionText(selectedModelName)])
            : visionText("enableTraining")
    }

    private func refreshTrainingEligibility() {
        do {
            let allExamples = try labelingExampleStore.loadTrainingExamples(in: .game)
            var exampleIDsByClass = [String: Set<UUID>]()
            var exampleIDsByContext = [String: Set<UUID>]()
            for example in allExamples {
                let classIdentifiers = Set(example.manifest.annotations.compactMap {
                    $0.isHardNegative ? nil : $0.classIdentifier
                })
                if !classIdentifiers.isEmpty {
                    exampleIDsByContext[
                        example.manifest.contextIdentifier,
                        default: []
                    ].insert(example.id)
                }
                for classIdentifier in classIdentifiers {
                    exampleIDsByClass[classIdentifier, default: []].insert(example.id)
                }
            }
            labelingSavedExampleIDsByClass = exampleIDsByClass
            labelingSavedExampleIDsByContext = exampleIDsByContext
            refreshTrainingNotification()
        } catch {
            labelingSavedExampleIDsByClass = [:]
            labelingSavedExampleIDsByContext = [:]
            labelingSaveError = "Examples failed: \(error.localizedDescription)"
        }
    }

    private func rebuildLabelingObjectAtlas() {
        let examples = (try? labelingExampleStore.loadExamples()) ?? []
        // Display icons include captured stencils as well as trainable objects.
        let identifiers = Array(LabelingCatalogPolicy.classIdentifiers).sorted()
        let input = LabelingObjectAtlasBuildInput(
            examples: examples,
            classIdentifiers: identifiers,
            preferredExampleIdentifiers: labelingObjectReferenceStore
                .exampleIdentifiers(for: identifiers)
        )
        Task {
            let result = await Task.detached(priority: .utility) {
                Result { try LabelingObjectAtlasStore().loadOrBuild(input: input) }
            }.value
            if case .success(let snapshot) = result {
                labelingObjectAtlas = snapshot
                model.setObjectDetectionIconAtlas(snapshot)
            }
        }
    }

    private func refreshTrainingNotification() {
        let examples = (try? labelingExampleStore.loadTrainingExamples(in: .game)) ?? []
        campaignCounts = LabelingCampaignPolicy.counts(examples)
        hasGameplayTrainingLabels = examples.contains { example in
            example.manifest.annotations.contains {
                !$0.isHardNegative && selectedTrainingClassIdentifiers.contains($0.classIdentifier)
            }
        }
        let modificationDates = examples.compactMap { example in
            try? example.directoryURL
                .appendingPathComponent(LabelingExampleStore.manifestFilename)
                .resourceValues(forKeys: [.contentModificationDateKey])
                .contentModificationDate
        }
        let datasetCreatedAt: Date? = latestModelCandidate.flatMap { candidate in
            let manifestURL = candidate.datasetURL.appendingPathComponent(
                LabelingDatasetExporter.manifestFilename
            )
            guard let data = try? Data(contentsOf: manifestURL) else { return nil }
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            return try? decoder.decode(LabelingDatasetManifest.self, from: data).createdAt
        }
        hasUntrainedLabelChanges = LabelingTrainingNotificationPolicy.hasUntrainedChanges(
            exampleModificationDates: modificationDates,
            latestDatasetCreatedAt: datasetCreatedAt
        )
        untrainedLabeledImageCount = LabelingTrainingNotificationPolicy.untrainedImageCount(
            exampleModificationDates: modificationDates,
            latestDatasetCreatedAt: datasetCreatedAt
        )
    }

    private func prepareSelectedObjectForTraining() {
        do {
            if model.labelImage != nil,
               editingLabelingExample != nil || !labelingDraft.rectangles.isEmpty {
                try persistCurrentLabelingExample()
            }
            let examples = try labelingExampleStore.loadTrainingExamples(in: .game)
            let snapshot = try labelingDatasetExporter.exportGameplayModel(examples: examples)
            labelingSaveError = nil
            let configuration = LabelingTrainingConfiguration.forModel(
                identifier: selectedModelIdentifier
            )
            let candidates = try labelingModelReviewStore.loadCandidates(
                classIdentifier: selectedModelIdentifier
            )
            let baseCheckpointURL = try labelingModelVersionStore.trainingCheckpointURL(
                for: selectedModelIdentifier,
                candidates: candidates
            )
            try labelingTrainer.start(
                snapshot: snapshot,
                className: selectedModelName,
                maximumIterations: configuration.maximumIterations,
                gridSize: configuration.gridSize,
                baseCheckpointURL: baseCheckpointURL
            )
        } catch {
            labelingSaveError = "Train setup failed: \(error.localizedDescription)"
        }
    }

    private func persistCurrentLabelingExample(
        rectangles: [LabelingDraftRectangle]? = nil
    ) throws {
        guard let capturedImage = model.labelImage else { return }
        let image = reopenedLabelImage ?? capturedImage
        let rectangles = rectangles ?? labelingDraft.rectangles
        if let editingLabelingExample {
            self.editingLabelingExample = try labelingExampleStore.updateDraft(
                editingLabelingExample,
                context: labelingContext,
                rectangles: rectangles
            )
        } else {
            editingLabelingExample = try labelingExampleStore.saveDraft(
                image: image,
                context: labelingContext,
                rectangles: rectangles,
                captureGroupIdentifier: labelingCaptureGroupIdentifier,
                languageIdentifier: initialization.language.rawValue
            )
        }
        model.refreshMenuStencilCatalog()
        refreshTrainingEligibility()
    }

    private func restoreStencilDraftIfAvailable() {
        guard labelingContext != .game else { return }
        if let example = initialization.example(for: labelingContext) {
            openLabelingExample(example, preferredClassIdentifier: labelingSelectedClassID)
        }
        let remaining = StencilLabelSelectionPolicy.availableLabels(
            context: labelingContext, rectangles: labelingDraft.rectangles,
            mode: model.gameMode, hidesCompleted: true
        )
        labelingSelectedClassID = remaining.first?.id ?? ""
    }

    private func resetStencils() {
        do {
            _ = try VisionResetController.resetStencils()
            if workspace == .label || workspace == .model { selectWorkspace(.gameplay) }
            model.stencilDetectionEnabled = false
            model.objectDetectionEnabled = false
            appliedDefaultObjectDetection = false
            initializationTargetContext = nil
            editingLabelingExample = nil
            reopenedLabelImage = nil
            labelingDraft.reset()
            model.refreshMenuStencilCatalog()
            initialization.refresh(mode: model.gameMode)
            refreshTrainingEligibility()
        } catch {
            labelingSaveError = "Stencil reset failed: \(error.localizedDescription)"
        }
    }

    private func factoryReset() {
        do {
            _ = try VisionResetController.factoryReset()
            NSApp.terminate(nil)
        } catch {
            labelingSaveError = "Factory reset failed: \(error.localizedDescription)"
        }
    }

    private func refreshLatestModelCandidate() {
        do {
            let candidates = try labelingModelReviewStore.loadCandidates()
            try labelingModelVersionStore.synchronizeCandidates(candidates)
            try labelingModelVersionStore.reconcileDevelopmentCandidates(candidates)
            modelCandidates = candidates
            latestModelCandidate = candidates.last { $0.summary.classIdentifier == selectedModelIdentifier }
            refreshActiveModelVersion()
            refreshTrainingNotification()
        } catch {
            latestModelCandidate = nil
            modelCandidates = []
            labelingSaveError = "Model review failed: \(error.localizedDescription)"
        }
    }

    private func refreshActiveModelVersion() {
        trainedGameplayModelReady = hasTrainedGameplayModel()
        enableDefaultObjectDetectionIfReady()
        do {
            activeModelVersionName = try labelingModelVersionStore.activeSelection(
                for: selectedModelIdentifier
            ).displayName
        } catch {
            activeModelVersionName = "Default"
            labelingSaveError = "Model versions failed: \(error.localizedDescription)"
        }
    }

    private func enableDefaultObjectDetectionIfReady() {
        guard !appliedDefaultObjectDetection, initialization.isFirstPhaseComplete, trainedGameplayModelReady else { return }
        appliedDefaultObjectDetection = true
        model.objectDetectionEnabled = true
    }

    private func autoPromoteLatestModelCandidate() {
        guard let latestModelCandidate else { return }
        do {
            _ = try labelingModelVersionStore.promote(latestModelCandidate)
            refreshActiveModelVersion()
            labelingSaveError = nil
            model.reloadActiveObjectModels()
        } catch {
            labelingSaveError = "Automatic promotion failed: \(error.localizedDescription)"
        }
    }

}

/// Converts review evidence into the same atlas coordinates used by liveBounds.
/// It has no view or model dependencies so coordinate and kind routing stay testable.
enum LiveFeatureReviewOverlay {
    static let showsTrackingPatchesByDefault = true

    static func make(
        groundHypotheses: GroundHypothesisTrackingResult? = nil,
        transitionPortals: [VisualRoomPortal] = [],
        showsTrackingPatches: Bool = showsTrackingPatchesByDefault,
        selectedGroundFeature: LayerSceneGroundFeatureDetails? = nil,
        persistentGroundLines: [GroundHypothesisAtlasLine]? = nil,
        walkedGround: [GroundHypothesisAtlasLine] = [],
        liveBounds: CGRect
    ) -> LayerSceneFeatureReviewOverlay {
        var markers = [LayerSceneFeatureReviewOverlay.Marker]()
        var lines = [LayerSceneFeatureReviewOverlay.Line]()

        let clearance = GroundClearance.height(frameHeight: liveBounds.height, knight: nil)
        let archive = persistentGroundLines ?? GroundStrokeGeometry.coalesced(groundHypotheses?.atlasLines ?? [], clearanceHeight: clearance)
        let saved: [GroundHypothesisAtlasLine]
        if walkedGround.isEmpty { saved = archive }
        else {
            let id = LiveGroundLineEvidence.walkedSegmentID
            let walk = GroundStrokeGeometry.coalesced(archive.filter { $0.segmentID == id } + walkedGround)
            saved = walk + WalkedFloorCoverage(walk).filtered(archive.filter { $0.segmentID != id }, clearance: clearance)
        }
        lines = saved.map { .init(start: $0.atlasStart, end: $0.atlasEnd,
            kind: $0.segmentID == LiveGroundLineEvidence.walkedSegmentID ? .walkedGround : .persistentGround) }
        if let groundHypotheses {
            if valid(liveBounds) {
                let current = groundHypotheses.detectedFloorLines.enumerated().map { index, line in
                    let y = liveBounds.maxY - CGFloat(line.row) - 0.5
                    return GroundHypothesisAtlasLine(segmentID: index,
                        atlasStart: CGPoint(x: liveBounds.minX + CGFloat(line.xRange.lowerBound), y: y),
                        atlasEnd: CGPoint(x: liveBounds.minX + CGFloat(line.xRange.upperBound) + 1, y: y))
                }
                lines += GroundStrokeGeometry.coalesced(current).map {
                    .init(start: $0.atlasStart, end: $0.atlasEnd, kind: .currentGround, coordinateSpace: .live)
                }
                if showsTrackingPatches {
                    appendTrackingPatches(groundHypotheses, selectedGroundFeature: selectedGroundFeature,
                        persistentGroundLines: persistentGroundLines, liveBounds: liveBounds, markers: &markers)
                }
            }
        }
        appendTransitionPortals(
            transitionPortals,
            markers: &markers,
            lines: &lines
        )

        return LayerSceneFeatureReviewOverlay(markers: markers, lines: lines)
    }

    private static func appendTransitionPortals(
        _ portals: [VisualRoomPortal],
        markers: inout [LayerSceneFeatureReviewOverlay.Marker],
        lines: inout [LayerSceneFeatureReviewOverlay.Line]
    ) {
        for portal in portals {
            let doors = [
                (portal.leftDoorWorldX, portal.leftDoorYRange),
                (portal.rightDoorWorldX, portal.rightDoorYRange),
            ]
            guard doors.allSatisfy({ x, range in
                x.isFinite && range.lowerBound.isFinite
                    && range.upperBound.isFinite
                    && range.upperBound > range.lowerBound
            }) else { continue }
            for (x, range) in doors {
                markers.append(.init(
                    worldPosition: CGPoint(
                        x: x,
                        y: (range.lowerBound + range.upperBound) * 0.5
                    ),
                    kind: .transitionDoor,
                    worldSize: CGSize(
                        width: 5,
                        height: range.upperBound - range.lowerBound
                    ),
                    opacity: 0.95
                ))
            }
            lines.append(.init(
                start: CGPoint(
                    x: portal.leftDoorWorldX,
                    y: (portal.leftDoorYRange.lowerBound
                        + portal.leftDoorYRange.upperBound) * 0.5
                ),
                end: CGPoint(
                    x: portal.rightDoorWorldX,
                    y: (portal.rightDoorYRange.lowerBound
                        + portal.rightDoorYRange.upperBound) * 0.5
                ),
                kind: .roomTransition
            ))
        }
    }

    private static func appendTrackingPatches(
        _ hypotheses: GroundHypothesisTrackingResult,
        selectedGroundFeature: LayerSceneGroundFeatureDetails?,
        persistentGroundLines: [GroundHypothesisAtlasLine]?,
        liveBounds: CGRect,
        markers: inout [LayerSceneFeatureReviewOverlay.Marker]
    ) {
        struct FeatureKey: Hashable {
            let segmentID: Int
            let sequenceIndex: Int
        }
        let selectedKey = selectedGroundFeature.map {
            FeatureKey(segmentID: $0.segmentID, sequenceIndex: $0.sequenceIndex)
        }
        var detailsByKey = [FeatureKey: LayerSceneGroundFeatureDetails]()
        let visibleFeatures = Dictionary(hypotheses.features.map {
            (FeatureKey(segmentID: $0.segmentID, sequenceIndex: $0.sequenceIndex), $0)
        }, uniquingKeysWith: { _, last in last })
        let savedBySegment = Dictionary(grouping: hypotheses.atlasLines, by: \.segmentID)
        let patchWidth = CGFloat(GroundHypothesisTracker.featureWidth)
        let patchHeight = CGFloat(GroundHypothesisTracker.featureHeight)
        for feature in hypotheses.atlasFeatures {
            let key = FeatureKey(
                segmentID: feature.segmentID,
                sequenceIndex: feature.sequenceIndex
            )
            let position: CGPoint
            if persistentGroundLines != nil {
                // Raw ground-bank anchors are local solver data. Keep current
                // feature inspection, but never draw them as corrected world
                // geometry after switching to island-owned ground publication.
                guard let visible = visibleFeatures[key] else { continue }
                position = CGPoint(x: liveBounds.minX + visible.imageRect.midX,
                                   y: liveBounds.maxY - visible.imageRect.midY)
            } else { position = feature.atlasPosition }
            guard isFinite(position) else { continue }
            let details = LayerSceneGroundFeatureDetails(
                segmentID: feature.segmentID,
                sequenceIndex: feature.sequenceIndex,
                worldPosition: position,
                referencePixels: feature.referencePixels,
                referenceOpacity: feature.referenceOpacity
            )
            let isSelected = key == selectedKey
            detailsByKey[key] = details
            if persistentGroundLines == nil, !liveBounds.contains(position),
               savedBySegment[feature.segmentID]?.contains(where: {
                   abs($0.atlasStart.y - (position.y + patchHeight / 2)) <= 3
                       && $0.atlasStart.x <= position.x - patchWidth / 2
                       && $0.atlasEnd.x >= position.x + patchWidth / 2
               }) == true { markers.append(.init(
                worldPosition: position,
                kind: isSelected ? .selectedGroundFeature : .groundFeature,
                worldSize: CGSize(
                    width: GroundHypothesisTracker.featureWidth,
                    height: GroundHypothesisTracker.featureHeight
                ),
                groundFeatureDetails: details,
                opacity: isSelected ? 1 : featureOpacity(feature.referenceOpacity)
            )) }
        }
        // Draw live diagnostics as vector outlines. A transparent full-frame
        // raster texture could be interpreted as opaque black by the Metal
        // upload path and hide the current capture underneath it.
        var occupied = [CGRect]()
        for feature in hypotheses.features {
            let key = FeatureKey(
                segmentID: feature.segmentID,
                sequenceIndex: feature.sequenceIndex
            )
            // Candidates are intentionally not presented. The Ground Features
            // legend describes only accepted, persistent, occluded, and
            // rejected motion/depth evidence.
            let x = Int(feature.imageRect.minX), y = Int(feature.imageRect.minY)
            guard feature.classification != .candidate,
                  let floor = hypotheses.detectedFloorLines.filter({ $0.containsPatch(atX: x, row: y) })
                    .min(by: { abs($0.row - y) < abs($1.row - y) }) else { continue }
            let rect = CGRect(x: feature.imageRect.minX, y: CGFloat(floor.row), width: patchWidth, height: patchHeight)
            occupied.append(rect)
            let details = detailsByKey[key]
            let isSelected = key == selectedKey
            let livePosition = CGPoint(
                x: liveBounds.minX + rect.midX,
                y: liveBounds.maxY - rect.midY
            )
            guard isFinite(livePosition) else { continue }
            markers.append(.init(
                worldPosition: livePosition,
                kind: isSelected
                    ? .selectedGroundFeature
                    : markerKind(for: feature.classification),
                worldSize: CGSize(
                    width: GroundHypothesisTracker.featureWidth,
                    height: GroundHypothesisTracker.featureHeight
                ),
                groundFeatureDetails: details,
                coordinateSpace: .live,
                isVisible: true,
                opacity: 1
            ))
        }

        // Local texture matches are available before a newly visible platform
        // earns a permanent atlas ID. Keep the inspectable atlas box at a shared location.
        for rect in hypotheses.matchedPatchRects {
            guard !occupied.contains(where: { $0.intersects(rect) && !$0.intersection(rect).isEmpty }) else { continue }
            occupied.append(rect)
            markers.append(.init(worldPosition: CGPoint(x: liveBounds.minX + rect.midX,
                y: liveBounds.maxY - rect.midY), kind: .cameraConsistentGround,
                worldSize: rect.size, coordinateSpace: .live))
        }

    }

    private static func markerKind(
        for classification: GroundHypothesisClassification
    ) -> LayerSceneFeatureReviewMarkerKind {
        switch classification {
        case .candidate: .occludedGroundFeature
        case .groundPlane: .cameraConsistentGround
        case .globalMatch: .groundFeature
        case .occluded: .occludedGroundFeature
        case .depthError: .groundFeatureDepthError
        }
    }

    private static func valid(_ rect: CGRect) -> Bool {
        rect.minX.isFinite && rect.minY.isFinite && rect.width.isFinite && rect.height.isFinite
            && rect.width > 0 && rect.height > 0
    }

    private static func isFinite(_ point: CGPoint) -> Bool {
        point.x.isFinite && point.y.isFinite
    }

    private static func featureOpacity(_ values: [UInt8]) -> Float {
        guard values.count == GroundHypothesisTracker.featurePixelCount else { return 0.15 }
        let mean = Float(values.reduce(0) { $0 + Int($1) })
            / Float(values.count * 255)
        return max(0.15, mean)
    }

}
