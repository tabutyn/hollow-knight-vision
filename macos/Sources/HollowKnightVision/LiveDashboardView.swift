import AppKit
import Foundation
import SwiftUI

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
    static func includesAtlas(hasGameplayStarted: Bool) -> Bool {
        hasGameplayStarted
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
    case hacker = "Hacker"
    case ops = "Ops"

    var id: Self { self }
    var shortcutCharacter: Character { rawValue.lowercased().first! }
    var supportsGameplayExitShortcut: Bool { self == .label || self == .model || self == .stencils }
}

enum DashboardControlVisibility {
    static func showsFraming(workspace: DashboardWorkspace, phaseOneComplete: Bool,
                             recognizedContext: String?) -> Bool {
        recognizedContext == "Gameplay"
            && (workspace == .hacker || (workspace == .gameplay && phaseOneComplete))
    }
    static func showsOps(workspace: DashboardWorkspace, mode: HollowKnightRuntimeMode,
                         recognizedContext: String?) -> Bool {
        workspace == .gameplay && mode == .modded && recognizedContext == "Gameplay"
    }
    static let workspaces = DashboardWorkspace.allCases.filter { $0 != .ops }
}

enum HackerInteractionMode: String, CaseIterable, Identifiable {
    case ground = "Ground"
    case rooms = "Rooms"
    case fit = "Fit"
    case freeFly = "Free Fly"
    case current = "Current"

    var id: Self { self }

    var framingMode: LayerSceneFramingMode {
        switch self {
        case .fit:
            return .fit
        case .current:
            return .current
        case .ground, .rooms, .freeFly:
            return .freeFly
        }
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

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        context.coordinator.install()
        return NSView(frame: .zero)
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.workspace = workspace
        context.coordinator.onExit = onExit
    }

    final class Coordinator {
        var workspace = DashboardWorkspace.gameplay
        var onExit: () -> Void = {}
        private var monitor: Any?

        func install() {
            guard monitor == nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard let self, WorkspaceExitShortcutPolicy.shouldHandle(
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

private struct DebugView: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    @ObservedObject var model: LiveCaptureModel
    let showsGroundTrackingTools: Bool
    @Binding var showsGameState: Bool
    let stencilReady: Bool
    let modelReady: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Toggle(visionText("Game State"), isOn: $showsGameState)
            Toggle(visionText("Stencil Detection"), isOn: $model.stencilDetectionEnabled)
                .disabled(!stencilReady)
            if !stencilReady { Text(visionText("stencilLocked")).font(.caption).foregroundStyle(.secondary) }
            Toggle(visionText("Object Detection"), isOn: $model.objectDetectionEnabled)
                .disabled(!modelReady)
            if !modelReady { Text(visionText("modelLocked")).font(.caption).foregroundStyle(.secondary) }
            Group {
                Toggle(visionText("Raw Detection"), isOn: $model.objectDetectionRawEnabled)
                HStack {
                    Text(visionText("Detection Extend"))
                    Slider(value: $model.objectDetectionExtension, in: 0...0.5)
                    Text(model.objectDetectionExtension, format: .percent.precision(.fractionLength(0)))
                        .monospacedDigit().frame(width: 38, alignment: .trailing)
                }
                Toggle(visionText("Detection Pixels"), isOn: $model.objectDetectionPixelsEnabled)
            }.disabled(!modelReady || !model.objectDetectionEnabled)
            Divider()
            Group {
                Toggle(visionText("Ground Features"), isOn: $model.groundHypothesesEnabled)
                Toggle(visionText("Transitions"), isOn: $model.transitionDiagnosticsEnabled)
                Toggle(visionText("Coarse Motion Overlay"), isOn: $model.coarseMotionDiagnosticsEnabled)
            }.disabled(!showsGroundTrackingTools)
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
    @State private var viewportFraming: LayerSceneFramingMode = .current
    @State private var framingRequest = 0
    @State private var viewportTransformStore = LayerSceneViewportTransformStore()
    @State private var hackerFramingRequest = 0
    @State private var hackerViewportTransformStore = LayerSceneViewportTransformStore()
    @State private var hackerInteractionMode = HackerInteractionMode.fit
    @State private var hackerRoomDrag: (id: UUID, point: CGPoint)?
    @State private var showsGameState = true
    @State private var showingSettings = false
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
    @State private var reviewingModelCandidate: LabelingModelCandidate?
    @State private var modelWorkspaceSelection = LabelingModelWorkspaceSelection()
    @State private var modelCandidates = [LabelingModelCandidate]()
    @State private var activeModelVersionName = "Default"
    @State private var trainedGameplayModelReady = false
    @State private var hasGameplayTrainingLabels = false
    @State private var gameplayLabelBreaches = 0
    @State private var hasUntrainedLabelChanges = false
    @State private var untrainedLabeledImageCount = 0
    @State private var trainingAlertMessage: String?
    @State private var lockedWorkspaceMessage: String?
    @StateObject private var labelingTrainer = LabelingTrainingController()
    @StateObject private var initialization = StencilInitializationCoordinator()
    @StateObject private var groundFeatureWindowController = GroundFeatureWindowController()
    @StateObject private var groundTruthLabelController = GroundTruthLabelController()
    private let labelingExampleStore = LabelingExampleStore()
    private let labelingDatasetExporter = LabelingDatasetExporter()
    private let labelingModelReviewStore = LabelingModelReviewStore()
    private let labelingModelVersionStore = LabelingModelVersionStore()
    private let labelingSelectionPreferences: LabelingSelectionPreferences
    private let labelingObjectReferenceStore = LabelingObjectReferenceStore()

    init(model: LiveCaptureModel, defaults: UserDefaults = .standard) {
        self.model = model
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
                workspace: showingSettings ? .gameplay : workspace,
                onExit: {
                    recentFramesPresentation = nil
                    selectWorkspace(.gameplay)
                }
            )
            .frame(width: 0, height: 0)
        }
        .onChange(of: workspace) { _, value in
            model.setNativeReviewActive(value != .gameplay)
        }
        .onChange(of: showingSettings) { _, active in model.setSettingsDialogActive(active) }
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
                if workspace == .model { selectWorkspace(.gameplay) }
            }
        }
        .onChange(of: initialization.examples) { _, _ in
            trainedGameplayModelReady = hasTrainedGameplayModel()
            if !trainedGameplayModelReady { model.objectDetectionEnabled = false }
        }
        .overlay {
            if showingSettings {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea().onTapGesture { showingSettings = false }
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
            model.objectDetectionEnabled = false
            model.refreshMenuStencilCatalog()
        }
        .onChange(of: labelingSelectedClassID) { _, _ in
            labelingSelectionPreferences.saveClassIdentifier(labelingSelectedClassID)
        }
        .onChange(of: model.groundHypothesisTracking) { _, _ in
            if workspace == .hacker {
                groundTruthLabelController.enter(
                    tracking: model.groundHypothesisTracking,
                    transform: model.groundTruthCameraTransform,
                    liveBounds: model.liveBounds
                )
            }
            groundFeatureWindowController.refresh(
                available: featureReviewOverlay.markers.filter {
                    $0.coordinateSpace == .atlas
                }.compactMap(\.groundFeatureDetails)
            )
        }
        .onChange(of: model.liveBounds) { _, _ in
            guard workspace == .hacker else { return }
            groundTruthLabelController.enter(
                tracking: model.groundHypothesisTracking,
                transform: model.groundTruthCameraTransform,
                liveBounds: model.liveBounds
            )
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
            } else if workspace == .model {
                LabelingModelWorkspaceView(
                    candidate: reviewingModelCandidate,
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
                    gameMode: model.gameMode, includesDeferredLabels: initialization.isFirstPhaseComplete
                )
                .id(labelingEditorIdentity)
            } else if workspace == .hacker {
                hackerCanvas
            } else if model.liveImage != nil {
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
                                    .compactMap(\.groundFeatureDetails)
                            )
                        },
                        featureReviewOverlay: model.groundReviewAvailable
                            && (model.groundHypothesesEnabled
                                || model.transitionDiagnosticsEnabled)
                            ? featureReviewOverlay : nil,
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
        .clipShape(RoundedRectangle(cornerRadius: 10))
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
               model.groundReviewAvailable
                    && (model.groundHypothesesEnabled
                        || model.coarseMotionDiagnosticsEnabled),
               reviewingModelCandidate == nil,
               model.labelImage == nil {
                VStack(alignment: .leading, spacing: 6) {
                    if model.groundHypothesesEnabled {
                        featureHypothesisLegend
                    }
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
    }

    private var hackerCanvas: some View {
        VStack(spacing: 0) {
            if hackerInteractionMode == .ground {
                GroundTruthLabelToolbar(
                    controller: groundTruthLabelController,
                    hasCameraTransform: model.hackerCameraTransform != nil
                )
            }
            if model.hackerLiveImage != nil {
                LayerSceneViewport(
                    tiles: hackerPresentationTiles,
                    mode: .world,
                    cameraPosition: .zero,
                    hiddenLayerIDs: [],
                    selectedLayerID: nil,
                    fitRequest: 0,
                    focusPoint: model.hackerMapFocus,
                    autoFollow: false,
                    framingMode: hackerInteractionMode.framingMode,
                    framingRequest: hackerFramingRequest,
                    transformStore: hackerViewportTransformStore,
                    fitBounds: hackerViewportFitBounds,
                    followBounds: model.hackerLiveBounds,
                    onSelect: { _, _, _ in },
                    onInteraction: handleHackerViewportInteraction,
                    onGamePointerEvent: model.forwardGamePointer,
                    groundLabelEditingEnabled: hackerInteractionMode == .ground,
                    onGroundLabelPointer: { phase, point, tolerance in
                        groundTruthLabelController.handle(
                            phase: phase,
                            atlasPoint: point,
                            atlasTolerance: tolerance,
                            transform: model.hackerCameraTransform,
                            liveBounds: model.hackerLiveBounds
                        )
                    },
                    roomEditingEnabled: hackerInteractionMode == .rooms,
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

    private var hackerFeatureReviewOverlay: LayerSceneFeatureReviewOverlay {
        switch hackerInteractionMode {
        case .ground:
            return hackerGroundLineOverlay
        case .rooms:
            return hackerRoomOverlay
        case .fit, .freeFly, .current:
            return LayerSceneFeatureReviewOverlay(markers: [], lines: [])
        }
    }

    private func handleHackerViewportInteraction() {
        guard hackerInteractionMode == .fit || hackerInteractionMode == .current else { return }
        selectHackerInteractionMode(.freeFly)
    }

    private var hackerGroundLineOverlay: LayerSceneFeatureReviewOverlay {
        LayerSceneFeatureReviewOverlay(
            markers: [],
            lines: groundTruthLabelController.overlayLines(
                transform: model.hackerCameraTransform,
                liveBounds: model.hackerLiveBounds
            )
        )
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
        return tiles
    }

    private var hackerViewportFitBounds: CGRect? {
        let live = model.hackerLiveBounds
        guard live.width > 0, live.height > 0 else { return model.hackerAtlasBounds }
        guard let atlas = model.hackerAtlasBounds, atlas.width > 0, atlas.height > 0 else {
            return live
        }
        return atlas.union(live)
    }

    private var featureReviewOverlay: LayerSceneFeatureReviewOverlay {
        LiveFeatureReviewOverlay.make(
            groundHypotheses: model.groundHypothesesEnabled
                ? model.groundHypothesisTracking : nil,
            transitionPortals: model.transitionDiagnosticsEnabled
                ? model.transitionPortals : [],
            selectedGroundFeature: groundFeatureWindowController.selectedFeature,
            liveBounds: model.liveBounds
        )
    }

    private var recognizedLabelingContext: LabelingContext? {
        guard let identifier = model.liveGameContextIdentifier else { return nil }
        return LabelingContext(storageIdentifier: identifier)
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
            legendRow("Purple · persistent global match", Color(red: 0.75, green: 0.31, blue: 1))
            legendRow("Orange · occluded / waiting", .orange)
            legendRow("Red · different depth or motion", .red)
        }
        .font(.caption2.monospaced())
        .padding(.horizontal, 7)
        .padding(.vertical, 5)
        .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 7))
        .accessibilityLabel(visionText("Ground Features color legend"))
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
            hasGameplayStarted: model.hasGameplayStarted
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
                isLiveOverlay: true
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
        if model.groundReviewAvailable, model.groundDetectEnabled,
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

    private var controls: some View {
        HStack(spacing: 8) {
            workspaceSelector
            if let guidance = initialization.guidance(
                labelingContext: workspace == .label ? labelingContext : nil,
                selectedClassIdentifier: labelingSelectedClassID,
                navigationContext: initializationTargetContext
            ) ?? initialization.modelGuidance(
                workspace: workspace, hasGameplayLabels: hasGameplayTrainingLabels,
                breaches: gameplayLabelBreaches, modelReady: trainedGameplayModelReady,
                training: labelingTrainer.isRunning, objectDetectionEnabled: model.objectDetectionEnabled
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
            if let issue = model.pathRecordingIssue {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red).help(issue)
            }
            if DashboardControlVisibility.showsFraming(workspace: workspace,
                phaseOneComplete: initialization.isFirstPhaseComplete, recognizedContext: model.liveGameStatus) {
                if workspace == .hacker {
                    hackerInteractionButton(.freeFly)
                    hackerInteractionButton(.fit)
                    hackerInteractionButton(.current)
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
                    .keyboardShortcut(.cancelAction)
            }.padding(20)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    AtlasManagerView(model: model, workspace: workspace)
                    if workspace == .hacker {
                        Picker(visionText("Hacker"), selection: Binding(get: { hackerInteractionMode }, set: selectHackerInteractionMode)) {
                            ForEach(HackerInteractionMode.allCases) { mode in Text(visionText(mode.rawValue)).tag(mode) }
                        }
                    }
                    Divider()
                    if model.canUseOps && model.liveGameStatus == "Gameplay" {
                        Text(visionText("Ops")).font(.headline)
                        PlayerOpsView(model: model)
                        Divider()
                    }
                    Text(initialization.text("debugView")).font(.headline)
                    DebugView(model: model,
                        showsGroundTrackingTools: DebugViewAvailabilityPolicy.showsGroundTrackingTools(
                            workspace: workspace, recognizedContext: model.liveGameStatus),
                        showsGameState: $showsGameState, stencilReady: initialization.isFirstPhaseComplete,
                        modelReady: initialization.isFirstPhaseComplete && trainedGameplayModelReady)
                    Divider()
                    Picker(initialization.text("language"), selection: $initialization.language) {
                        ForEach(HollowKnightMenuLanguage.allCases, id: \.self) { language in
                            Text(language.nativeName).tag(language)
                        }
                    }
                    Divider()
                    Button(initialization.text("factory"), role: .destructive) {
                        showingSettings = false
                        confirmsFactoryReset = true
                    }
                }.padding(20)
            }
        }
        .frame(width: 620)
        .frame(maxHeight: 760)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(.white.opacity(0.15)))
        .padding(24)
    }

    private func openGroundLabelContext() {
        model.groundHypothesesEnabled = true
        selectWorkspace(.hacker)
        selectHackerInteractionMode(.ground)
        groundTruthLabelController.enter(
            tracking: model.groundHypothesisTracking,
            transform: model.groundTruthCameraTransform,
            liveBounds: model.liveBounds
        )
    }

    private var visibleWorkspaces: [DashboardWorkspace] {
        DashboardControlVisibility.workspaces.filter { destination in
            if destination == .model { return initialization.isFirstPhaseComplete }
            if destination == .hacker { return model.canUseHacker && model.liveGameStatus == "Gameplay" }
            return true
        }
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
        .frame(width: 410)
        .overlay(workspaceShortcuts)
    }

    private var workspaceShortcuts: some View {
        ZStack {
            ForEach(DashboardWorkspace.allCases) { destination in
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
        .accessibilityLabel(model.isPathRecording ? "Stop path recording" : "Record path")
        .disabled(
            model.isPathPlaybackActive
                || model.isPathCheckpointPending
                || (!model.isPathRecording
                    && (!model.canStartPathRecording || labelingTrainer.isRunning))
        )
        .tint(
            model.isPathPlaybackActive ? .purple
                : (model.isPathRecording ? .red : .accentColor)
        )
        .help(model.lastRecordedPathURL.map {
            "Last saved path: \($0.path)"
        } ?? "Record forwarded gameplay input and tracking state")
    }

    private func selectWorkspace(_ destination: DashboardWorkspace) {
        guard !showingSettings, destination != workspace else { return }
        guard destination != .model || initialization.isFirstPhaseComplete else { return }
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
        guard !labelingTrainer.isRunning,
              !model.isPathRecording,
              !model.isPathCheckpointPending else { return }
        if destination == .label {
            guard model.canToggleLabelMode else { return }
            labelingEditorMode = .add
        }


        if workspace == .model || reviewingModelCandidate != nil {
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
            groundTruthLabelController.enter(
                tracking: model.groundHypothesisTracking,
                transform: model.groundTruthCameraTransform,
                liveBounds: model.liveBounds
            )
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
        .tint(viewportFraming == mode ? .cyan : .gray)
        .overlay(
            RoundedRectangle(cornerRadius: 6)
                .stroke(viewportFraming == mode ? Color.cyan : .clear, lineWidth: 1.5)
                .allowsHitTesting(false)
        )
    }

    private func hackerInteractionButton(_ mode: HackerInteractionMode) -> some View {
        Button(visionText(mode.rawValue)) {
            selectHackerInteractionMode(mode)
        }
        .tint(hackerInteractionMode == mode ? .cyan : .gray)
        .overlay(
            RoundedRectangle(cornerRadius: 6)
                .stroke(hackerInteractionMode == mode ? Color.cyan : .clear, lineWidth: 1.5)
                .allowsHitTesting(false)
        )
    }

    private func selectHackerInteractionMode(_ mode: HackerInteractionMode) {
        hackerInteractionMode = mode
        hackerRoomDrag = nil
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
        guard !labelingTrainer.isRunning, gameplayLabelBreaches == 0,
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
        hasGameplayTrainingLabels = examples.contains { example in
            example.manifest.annotations.contains {
                !$0.isHardNegative && selectedTrainingClassIdentifiers.contains($0.classIdentifier)
            }
        }
        let exemptions = LabelingContractExemptionStore()
        gameplayLabelBreaches = examples.filter {
            LabelingContractEvaluator.evaluate($0, exemptions: exemptions.load(for: $0)).contains(where: \.isBreach)
        }.count
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
            let snapshot = try labelingDatasetExporter.exportSharedModel(
                classIdentifiers: selectedTrainingClassIdentifiers,
                examples: examples
            )
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
            mode: model.gameMode, hidesCompleted: true, includesDeferredLabels: initialization.isFirstPhaseComplete
        )
        labelingSelectedClassID = remaining.first?.id ?? ""
    }

    private func resetStencils() {
        do {
            _ = try VisionResetController.resetStencils()
            if workspace == .label || workspace == .model { selectWorkspace(.gameplay) }
            model.stencilDetectionEnabled = false
            model.objectDetectionEnabled = false
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
        do {
            activeModelVersionName = try labelingModelVersionStore.activeSelection(
                for: selectedModelIdentifier
            ).displayName
        } catch {
            activeModelVersionName = "Default"
            labelingSaveError = "Model versions failed: \(error.localizedDescription)"
        }
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
    static func make(
        groundHypotheses: GroundHypothesisTrackingResult? = nil,
        transitionPortals: [VisualRoomPortal] = [],
        selectedGroundFeature: LayerSceneGroundFeatureDetails? = nil,
        liveBounds: CGRect
    ) -> LayerSceneFeatureReviewOverlay {
        var markers = [LayerSceneFeatureReviewOverlay.Marker]()
        var lines = [LayerSceneFeatureReviewOverlay.Line]()

        appendGroundHypotheses(
            groundHypotheses,
            selectedGroundFeature: selectedGroundFeature,
            liveBounds: liveBounds,
            markers: &markers,
            lines: &lines
        )
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

    private static func appendGroundHypotheses(
        _ hypotheses: GroundHypothesisTrackingResult?,
        selectedGroundFeature: LayerSceneGroundFeatureDetails?,
        liveBounds: CGRect,
        markers: inout [LayerSceneFeatureReviewOverlay.Marker],
        lines: inout [LayerSceneFeatureReviewOverlay.Line]
    ) {
        guard let hypotheses, valid(liveBounds) else { return }
        struct FeatureKey: Hashable {
            let segmentID: Int
            let sequenceIndex: Int
        }
        let selectedKey = selectedGroundFeature.map {
            FeatureKey(segmentID: $0.segmentID, sequenceIndex: $0.sequenceIndex)
        }
        var detailsByKey = [FeatureKey: LayerSceneGroundFeatureDetails]()
        for feature in hypotheses.atlasFeatures {
            let key = FeatureKey(
                segmentID: feature.segmentID,
                sequenceIndex: feature.sequenceIndex
            )
            let position = feature.atlasPosition
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
            markers.append(.init(
                worldPosition: position,
                kind: isSelected ? .selectedGroundFeature : .groundFeature,
                worldSize: CGSize(
                    width: GroundHypothesisTracker.featureWidth,
                    height: GroundHypothesisTracker.featureHeight
                ),
                groundFeatureDetails: details,
                opacity: isSelected ? 1 : featureOpacity(feature.referenceOpacity)
            ))
        }
        // Draw live diagnostics as vector outlines. A transparent full-frame
        // raster texture could be interpreted as opaque black by the Metal
        // upload path and hide the current capture underneath it.
        for feature in hypotheses.features {
            let key = FeatureKey(
                segmentID: feature.segmentID,
                sequenceIndex: feature.sequenceIndex
            )
            // Candidates are intentionally not presented. The Ground Features
            // legend describes only accepted, persistent, occluded, and
            // rejected motion/depth evidence.
            guard feature.classification != .candidate else { continue }
            let details = detailsByKey[key]
            let isSelected = key == selectedKey
            let livePosition = CGPoint(
                x: liveBounds.minX + feature.imageRect.midX,
                y: liveBounds.maxY - feature.imageRect.midY
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
        for review in GroundLineDetector.featureHypothesisPresenceReviews(
            hypotheses.lineReviews
        ) {
            let y = liveBounds.maxY - CGFloat(review.line.row) - 0.5
            let start = CGPoint(
                x: liveBounds.minX + CGFloat(review.line.xRange.lowerBound),
                y: y
            )
            let end = CGPoint(
                x: liveBounds.minX + CGFloat(review.line.xRange.upperBound + 1),
                y: y
            )
            guard isFinite(start), isFinite(end) else { continue }
            lines.append(.init(
                start: start,
                end: end,
                kind: .currentGround,
                coordinateSpace: .live
            ))
        }
        for line in hypotheses.atlasLines {
            let start = line.atlasStart
            let end = line.atlasEnd
            guard isFinite(start), isFinite(end) else { continue }
            lines.append(.init(start: start, end: end, kind: .persistentGround))
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
