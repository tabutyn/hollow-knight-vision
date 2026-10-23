import AppKit
import SwiftUI

/// Modded mode uses an ordinary window; its in-game receiver accepts input
/// while Vision is the active application.
final class VisionPanel: NSWindow {}

enum DashboardWindowLayout {
    static func initialFrame(visibleFrame: CGRect) -> CGRect {
        guard visibleFrame.origin.x.isFinite, visibleFrame.origin.y.isFinite,
              visibleFrame.width.isFinite, visibleFrame.height.isFinite,
              visibleFrame.width > 0, visibleFrame.height > 0
        else { return CGRect(x: 0, y: 0, width: 1_200, height: 800) }
        return visibleFrame.standardized
    }
}

private struct GameModeChooserView: View {
    let icon: NSImage
    let normalAvailable: Bool
    let moddedAvailable: Bool
    let choose: (NSApplication.ModalResponse) -> Void

    var body: some View {
        VStack(spacing: 24) {
            Image(nsImage: icon)
                .resizable()
                .interpolation(.high)
                .scaledToFit()
                .frame(width: 156, height: 156)

            HStack(spacing: 10) {
                Button(visionText(HollowKnightRuntimeMode.normal.title)) {
                    choose(.alertFirstButtonReturn)
                }
                .disabled(!normalAvailable)
                .accessibilityLabel(visionText(HollowKnightRuntimeMode.normal.title))

                Button(visionText(HollowKnightRuntimeMode.modded.title)) {
                    choose(.alertSecondButtonReturn)
                }
                .disabled(!moddedAvailable)
                .accessibilityLabel(visionText(HollowKnightRuntimeMode.modded.title))

                Button(visionText("Quit")) {
                    choose(.abort)
                }
                .keyboardShortcut(.cancelAction)
                .accessibilityLabel(visionText("Quit"))
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .fixedSize(horizontal: true, vertical: false)
        }
        .padding(.horizontal, 30)
        .padding(.vertical, 26)
        .frame(minWidth: 500, minHeight: 270)
        .background(.regularMaterial)
    }
}

struct HollowKnightVisionLaunchOptions: Equatable {
    static let autoNavigateFlag = "--auto-navigate-gameplay"
    static let automationControlFlag = "--enable-automation-control"

    static func retiredArgument(in arguments: [String]) -> String? {
        let retired: Set<String> = [
            "--replay", "--registration", "--ownership", "--benchmark-capture-pixels",
            "--ground-texture-affine", "--ground-texture-subpixel", "--ground-snap-recovery",
            "--ground-snap-audit-directory", "--capture-owned-bgra", "--capture-core-image",
            "--capture-pixel-audit", "--ground-surface-architecture", "--capture-fixed-refresh",
            "--capture-queue-eight", "--presentation-three-drawables", "--interactive-vision-workers",
            "--capture-user-activity", "--global-search-stabilized-rows"
        ]
        return arguments.first { retired.contains(String($0.split(separator: "=", maxSplits: 1).first ?? "")) }
    }

    let autoNavigateToGameplay: Bool
    let automationControlEnabled: Bool
    let gameMode: HollowKnightRuntimeMode?
    let gameModeError: HollowKnightRuntimeModeArgumentError?
    let gamePath: String?
    let gamePathError: HollowKnightGamePathArgumentError?

    init(arguments: [String]) {
        autoNavigateToGameplay = arguments.contains(Self.autoNavigateFlag)
        automationControlEnabled = arguments.contains(Self.automationControlFlag)
        do {
            gameMode = try HollowKnightRuntimeModeParser.parse(arguments)
            gameModeError = nil
        } catch let error as HollowKnightRuntimeModeArgumentError {
            gameMode = nil
            gameModeError = error
        } catch {
            gameMode = nil
            gameModeError = .invalidValue("unknown")
        }
        do {
            gamePath = try HollowKnightGamePathParser.parse(arguments)
            gamePathError = nil
        } catch let error as HollowKnightGamePathArgumentError {
            gamePath = nil
            gamePathError = error
        } catch {
            gamePath = nil
            gamePathError = .missingValue
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    let model: LiveCaptureModel
    private let automationControlEnabled: Bool
    private var requestedGamePath: String?
    private let requestedGameMode: HollowKnightRuntimeMode?
    private let availableInstallations: HollowKnightInstallationCatalog
    private let requiresGameModeChoice: Bool
    private var automationController: GameAutomationController?
    private var viewerPanel: NSWindow?

    override init() {
        let options = HollowKnightVisionLaunchOptions(arguments: CommandLine.arguments)
        let additionalCandidates = [
            GameControlForwarder.hollowKnightApplication()?.bundleURL,
            NSWorkspace.shared.urlForApplication(
                withBundleIdentifier: "unity.Team Cherry.Hollow Knight"
            ),
        ].compactMap { $0 }
        let installations = HollowKnightInstallationDiscovery.discover(
            explicitPath: options.gamePath,
            additionalCandidates: additionalCandidates
        )
        availableInstallations = installations
        requestedGameMode = options.gameMode
        requestedGamePath = options.gameMode.flatMap {
            installations.installation(for: $0)?.path
        }
        requiresGameModeChoice = HollowKnightRuntimeModeLaunchPolicy.requiresChoice(
            argumentMode: options.gameMode
        )
        model = LiveCaptureModel(
            autoNavigateToGameplay: options.autoNavigateToGameplay,
            gameMode: HollowKnightRuntimeModeLaunchPolicy.initialMode(
                argumentMode: options.gameMode
            )
        )
        automationControlEnabled = options.automationControlEnabled
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // The in-game receiver deliberately accepts one Vision client. Direct
        // development launches can bypass Launch Services' single-instance
        // plist gate, so the newest build retires any older copies first.
        let currentPID = ProcessInfo.processInfo.processIdentifier
        NSRunningApplication.runningApplications(
            withBundleIdentifier: "com.ballroller.hollow-knight-vision"
        )
        .filter { $0.processIdentifier != currentPID }
        .forEach { $0.terminate() }

        if let iconFilename = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleIconFile"
        ) as? String,
           let resourceURL = Bundle.main.resourceURL,
           let icon = NSImage(contentsOf: resourceURL.appendingPathComponent(iconFilename)) {
            NSApp.applicationIconImage = icon
        }
        NSApp.setActivationPolicy(.regular)
        // Make per-launch mode selection the first visible app UI. Showing the
        // dashboard first can leave its modal alert on another Space or hidden
        // behind the large dashboard window.
        NSApp.activate(ignoringOtherApps: true)
        if requiresGameModeChoice {
            guard let gameMode = Self.chooseGameMode(
                availableInstallations: availableInstallations
            ) else {
                NSApp.terminate(nil)
                return
            }
            requestedGamePath = availableInstallations.installation(for: gameMode)?.path
            model.configureGameMode(gameMode)
        } else if let requestedGameMode,
                  requestedGamePath == nil {
            Self.showMissingInstallation(for: requestedGameMode)
            NSApp.terminate(nil)
            return
        }

        showDashboard()
        model.startGame(gamePath: requestedGamePath)
        if automationControlEnabled {
            automationController = GameAutomationController(model: model)
            automationController?.start()
        }
        model.startCapture()
    }

    private func showDashboard() {
        let visibleFrame = NSScreen.main?.visibleFrame
            ?? CGRect(x: 0, y: 0, width: 1_200, height: 800)
        let initialFrame = DashboardWindowLayout.initialFrame(
            visibleFrame: visibleFrame
        )
        let style: NSWindow.StyleMask = [.titled, .closable, .resizable, .fullSizeContentView]
        let panel: NSWindow
        if model.gameMode == .normal {
            let nativePanel = NormalVisionPanel(
                contentRect: initialFrame, styleMask: style.union(.nonactivatingPanel),
                backing: .buffered, defer: false
            )
            nativePanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel = nativePanel
        } else {
            panel = VisionPanel(contentRect: initialFrame, styleMask: style,
                                backing: .buffered, defer: false)
        }
        panel.title = "Hollow Knight Vision"
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.contentMinSize = CGSize(width: 720, height: 560)
        panel.contentView = NSHostingView(rootView: DashboardView(model: model))
        panel.acceptsMouseMovedEvents = true
        panel.delegate = self
        // contentRect construction can add title-bar geometry. Set the outer
        // frame explicitly so the first dashboard fills the usable display.
        panel.setFrame(initialFrame, display: false)
        viewerPanel = panel
        panel.makeKeyAndOrderFront(nil)
    }

    private static func chooseGameMode(
        availableInstallations: HollowKnightInstallationCatalog
    ) -> HollowKnightRuntimeMode? {
        let panel = NSPanel(
            contentRect: CGRect(x: 0, y: 0, width: 500, height: 270),
            styleMask: [.titled, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.level = .modalPanel
        panel.contentView = NSHostingView(rootView: GameModeChooserView(
            icon: NSApp.applicationIconImage,
            normalAvailable: availableInstallations.normal != nil,
            moddedAvailable: availableInstallations.modded != nil,
            choose: { NSApp.stopModal(withCode: $0) }
        ))
        panel.center()
        panel.makeKeyAndOrderFront(nil)
        let response = NSApp.runModal(for: panel)
        panel.orderOut(nil)
        switch response {
        case .alertFirstButtonReturn: return .normal
        case .alertSecondButtonReturn: return .modded
        default: return nil
        }
    }

    private static func showMissingInstallation(for mode: HollowKnightRuntimeMode) {
        let alert = NSAlert()
        alert.messageText = visionText("Installation not found")
        alert.informativeText = visionText(mode.title)
        alert.alertStyle = .warning
        alert.addButton(withTitle: visionText("Quit"))
        alert.runModal()
    }

    func applicationWillTerminate(_ notification: Notification) {
        automationController?.stop()
        model.stopGameControls()
    }

    // AppKit excludes panels from its last-window check. Closing the mode
    // chooser would otherwise schedule a quit despite the Normal dashboard.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow,
              window === viewerPanel else { return }
        // Only the dashboard owns app lifetime; alerts and auxiliary windows
        // can close without terminating the game-launch/capture sequence.
        DispatchQueue.main.async { NSApp.terminate(nil) }
    }
}

private func gameAutomationDarwinCallback(
    _ center: CFNotificationCenter?,
    _ observer: UnsafeMutableRawPointer?,
    _ name: CFNotificationName?,
    _ object: UnsafeRawPointer?,
    _ userInfo: CFDictionary?
) {
    guard let observer else { return }
    let controller = Unmanaged<GameAutomationController>
        .fromOpaque(observer).takeUnretainedValue()
    DispatchQueue.main.async { controller.receiveCommand() }
}

final class GameAutomationController {
    private weak var model: LiveCaptureModel?
    private var isStarted = false

    init(model: LiveCaptureModel) {
        self.model = model
    }

    deinit { stop() }

    func start() {
        guard !isStarted else { return }
        isStarted = true
        CFNotificationCenterAddObserver(
            CFNotificationCenterGetDarwinNotifyCenter(),
            Unmanaged.passUnretained(self).toOpaque(),
            gameAutomationDarwinCallback,
            GameAutomationCommand.darwinNotificationName as CFString,
            nil,
            .deliverImmediately
        )
    }

    func stop() {
        guard isStarted else { return }
        isStarted = false
        CFNotificationCenterRemoveObserver(
            CFNotificationCenterGetDarwinNotifyCenter(),
            Unmanaged.passUnretained(self).toOpaque(),
            CFNotificationName(
                GameAutomationCommand.darwinNotificationName as CFString
            ),
            nil
        )
    }

    func receiveCommand() {
        let pasteboard = NSPasteboard(name: .init(
            GameAutomationCommand.pasteboardName
        ))
        let serialized = pasteboard.string(forType: .string)
        let reply: String
        if serialized == "menu-calibration-status" {
            reply = model?.menuStencilAutomationReply(capture: false).serialized
                ?? MenuStencilAutomationReply(
                    ok: false, contextIdentifier: nil, selectedIdentifier: nil,
                    selectedName: nil, capturePath: nil, message: "Vision model unavailable"
                ).serialized
        } else if serialized == "menu-calibration-capture" {
            reply = model?.menuStencilAutomationReply(capture: true).serialized
                ?? MenuStencilAutomationReply(
                    ok: false, contextIdentifier: nil, selectedIdentifier: nil,
                    selectedName: nil, capturePath: nil, message: "Vision model unavailable"
                ).serialized
        } else if let serialized,
                  serialized.hasPrefix("menu-calibration-capture:"),
                  serialized.split(separator: ":").count == 3 {
            let components = serialized.split(separator: ":")
            reply = model?.menuStencilAutomationReply(
                capture: true,
                expectedContextIdentifier: String(components[1]),
                expectedSelectedIdentifier: String(components[2])
            ).serialized ?? MenuStencilAutomationReply(
                ok: false, contextIdentifier: nil, selectedIdentifier: nil,
                selectedName: nil, capturePath: nil, message: "Vision model unavailable"
            ).serialized
        } else if let serialized,
                  serialized.hasPrefix("menu-calibration-capture:"),
                  serialized.split(separator: ":").count == 4 {
            let components = serialized.split(separator: ":")
            reply = model?.menuStencilAutomationReply(
                capture: true,
                expectedContextIdentifier: String(components[2]),
                expectedSelectedIdentifier: String(components[3]),
                languageIdentifier: String(components[1])
            ).serialized ?? MenuStencilAutomationReply(
                ok: false, contextIdentifier: nil, selectedIdentifier: nil,
                selectedName: nil, capturePath: nil, message: "Vision model unavailable"
            ).serialized
        } else {
            let accepted: Bool
            if let serialized,
               let playback = GamePathPlaybackCommand(serialized: serialized) {
                accepted = model?.performPathPlaybackCommand(playback) == true
            } else {
                accepted = serialized.flatMap(GameAutomationCommand.init(serialized:))
                    .map { model?.performAutomationCommand($0) == true } ?? false
            }
            reply = accepted ? "accepted" : "rejected"
        }
        pasteboard.clearContents()
        pasteboard.setString(reply, forType: .string)
    }
}

@main
struct HollowKnightVisionApp: App {
    init() {
        let launchOptions = HollowKnightVisionLaunchOptions(arguments: CommandLine.arguments)
        if let error = launchOptions.gameModeError {
            fputs("\(error.localizedDescription)\n", stderr)
            exit(64)
        }
        if let error = launchOptions.gamePathError {
            fputs("\(error.localizedDescription)\n", stderr)
            exit(64)
        }
        if let retired = HollowKnightVisionLaunchOptions.retiredArgument(in: CommandLine.arguments) {
            fputs("Retired option: \(retired). See RETIRED_FEATURES.md for supported workflows.\n", stderr)
            exit(64)
        }
        if CommandLine.arguments.contains("--export-gameplay-model-dataset") {
            do {
                let examples = try LabelingExampleStore().loadTrainingExamples()
                let snapshot = try LabelingDatasetExporter().exportSharedModel(
                    classIdentifiers: LabelingSharedModelPolicy.classIdentifiers,
                    examples: examples
                )
                print(
                    "Gameplay dataset path=\(snapshot.directoryURL.path) "
                        + "images=\(snapshot.manifest.items.count) "
                        + "trainingAnnotations=\(snapshot.manifest.trainingAnnotationCount) "
                        + "validationAnnotations=\(snapshot.manifest.validationAnnotationCount)"
                )
                exit(0)
            } catch {
                fputs("Gameplay dataset export failed: \(error)\n", stderr)
                exit(1)
            }
        }
        if let runPath = Self.argument(
            prefix: "--promote-gameplay-model-run=",
            in: Array(CommandLine.arguments.dropFirst())
        ) {
            do {
                let reviewStore = LabelingModelReviewStore()
                let versionStore = LabelingModelVersionStore()
                let candidates = try reviewStore.loadCandidates(
                    classIdentifier: LabelingSharedModelPolicy.modelIdentifier
                )
                try versionStore.synchronizeCandidates(candidates)
                let requestedURL = URL(fileURLWithPath: runPath, isDirectory: true)
                    .standardizedFileURL
                guard let candidate = candidates.first(where: {
                    $0.runURL.standardizedFileURL == requestedURL
                }) else {
                    throw CocoaError(.fileNoSuchFile)
                }
                let restorePoint = try versionStore.promote(candidate)
                print("Promoted gameplay model \(restorePoint.version.displayName)")
                exit(0)
            } catch {
                fputs("Gameplay model promotion failed: \(error)\n", stderr)
                exit(1)
            }
        }
        if CommandLine.arguments.contains("--calibrate-menu-stencils") {
            do {
                let arguments = Array(CommandLine.arguments.dropFirst())
                let examples = Self.argument(
                    prefix: "--menu-stencil-examples=",
                    in: arguments
                ).map { URL(fileURLWithPath: $0, isDirectory: true) }
                    ?? LabelingExampleStore.defaultRootURL()
                let output = Self.argument(
                    prefix: "--menu-stencil-calibration-output=",
                    in: arguments
                ).map { URL(fileURLWithPath: $0) }
                    ?? examples.deletingLastPathComponent()
                        .appendingPathComponent("menu-stencil-positions.json")
                let report = try MenuStencilCalibrationGenerator.run(
                    examplesRootURL: examples,
                    outputURL: output
                )
                print(
                    "Menu stencil calibration scenes=\(report.sceneCount) "
                        + "measuredStates=\(report.measuredStateCount) "
                        + "derivedStates=\(report.derivedStateCount) "
                        + "accepted=\(report.acceptedMatchCount) "
                        + "rejected=\(report.rejectedMatchCount) "
                        + "candidates=\(report.fullScreenCandidateCount) "
                        + "output=\(report.outputURL.path)"
                )
                exit(0)
            } catch {
                fputs("Menu stencil calibration failed: \(error)\n", stderr)
                exit(1)
            }
        }
        if CommandLine.arguments.contains("--evaluate-ground-labels") {
            do {
                let output = try GroundLabelEvaluator.runCommand(
                    arguments: Array(CommandLine.arguments.dropFirst())
                )
                print("Ground-label evaluation written to \(output.path)")
                exit(0)
            } catch {
                fputs("Ground-label evaluation failed: \(error)\n", stderr)
                exit(1)
            }
        }
        if CommandLine.arguments.contains("--world-replay-session") {
            do {
                let arguments = Array(CommandLine.arguments.dropFirst())
                let report = try LiveWorldReplayEvaluator.run(arguments: arguments)
                let data = try JSONEncoder().encode(report)
                print(String(decoding: data, as: UTF8.self))
                exit(report.passed ? 0 : 2)
            } catch {
                fputs("World replay failed: \(error)\n", stderr)
                exit(1)
            }
        }
        if CommandLine.arguments.contains("--evaluate-low-resolution-trace") {
            do {
                let report = try LowResolutionTraceEvaluator.run(
                    arguments: Array(CommandLine.arguments.dropFirst())
                )
                let encoder = JSONEncoder()
                encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
                print(String(decoding: try encoder.encode(report), as: UTF8.self))
                exit(0)
            } catch {
                fputs("Low-resolution trace evaluation failed: \(error)\n", stderr)
                exit(1)
            }
        }

    }

    private static func argument(prefix: String, in arguments: [String]) -> String? {
        arguments.first(where: { $0.hasPrefix(prefix) }).map {
            String($0.dropFirst(prefix.count))
        }
    }
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Settings { EmptyView() }
    }
}
