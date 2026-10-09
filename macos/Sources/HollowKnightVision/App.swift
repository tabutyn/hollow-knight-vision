import AppKit
import SwiftUI

/// The Vision window deliberately uses normal AppKit window ordering. Input is
/// received while the user selects this window; it never needs to float above
/// other applications or borrow focus from the game.
final class VisionPanel: NSWindow {}

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

    init(arguments: [String]) {
        autoNavigateToGameplay = arguments.contains(Self.autoNavigateFlag)
        automationControlEnabled = arguments.contains(Self.automationControlFlag)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    let model: LiveCaptureModel
    private let automationControlEnabled: Bool
    private var automationController: GameAutomationController?
    private var viewerPanel: NSWindow?

    override init() {
        let options = HollowKnightVisionLaunchOptions(arguments: CommandLine.arguments)
        model = LiveCaptureModel(autoNavigateToGameplay: options.autoNavigateToGameplay)
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

        if let iconURL = Bundle.main.url(
            forResource: "HollowKnightVision",
            withExtension: "icns"
        ), let icon = NSImage(contentsOf: iconURL) {
            NSApp.applicationIconImage = icon
        }
        NSApp.setActivationPolicy(.regular)
        let initialFrame = NSScreen.main?.visibleFrame
            ?? CGRect(x: 0, y: 0, width: 900, height: 720)
        let panel = VisionPanel(
            contentRect: initialFrame,
            styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        panel.title = "Hollow Knight Vision"
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.contentMinSize = CGSize(width: 720, height: 560)
        panel.contentView = NSHostingView(rootView: DashboardView(model: model))
        panel.acceptsMouseMovedEvents = true
        viewerPanel = panel
        panel.makeKeyAndOrderFront(nil)
        // A normal app still needs to become active once when it is launched.
        // After this, ordinary window selection and Cmd-Tab determine ordering;
        // Vision never raises itself again or activates Hollow Knight.
        NSApp.activate(ignoringOtherApps: true)
        model.startGameControls()
        if automationControlEnabled {
            automationController = GameAutomationController(model: model)
            automationController?.start()
        }
        model.startCapture()
    }

    func applicationWillTerminate(_ notification: Notification) {
        automationController?.stop()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
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
