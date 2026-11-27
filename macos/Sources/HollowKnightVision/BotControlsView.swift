import AppKit
import SwiftUI

enum BotControlPresentationPolicy {
    static func shows(in workspace: DashboardWorkspace) -> Bool {
        workspace == .gameplay || workspace == .hacker
    }

    static func startIssue(mode: HollowKnightRuntimeMode, capturing: Bool, training: Bool,
                           stencilsReady: Bool, controlsReady: Bool) -> String? {
        if mode != .modded { return "Choose Modded game mode to start the bot." }
        if !capturing { return "Start a modded game capture before starting the bot." }
        if training { return "Wait for training to finish before starting the bot." }
        if !stencilsReady { return "Complete stencil setup before starting the bot." }
        if !controlsReady { return "Return to live capture and finish any recording, replay, or Atlas operation before starting the bot." }
        return nil
    }

    static func help(active: Bool, startIssue: String?, lastIssue: String?) -> String {
        if active { return "Press B to stop the bot." }
        return startIssue ?? lastIssue ?? "Press B to start the bot. From a game menu it archives slot 4 and starts a fresh game. During Gameplay it runs the model for the current workspace."
    }

    static func canToggle(active: Bool, startIssue: String?) -> Bool { active || startIssue == nil }
}

enum BotShortcutPolicy {
    static func handles(keyCode: UInt16, modifiers: NSEvent.ModifierFlags,
                        isRepeat: Bool, editingText: Bool, hasSheet: Bool) -> Bool {
        keyCode == 11 && modifiers.intersection([.command, .control, .option, .shift]).isEmpty
            && !isRepeat && !editingText && !hasSheet
    }
}

private struct BotShortcutMonitor: NSViewRepresentable {
    let action: () -> Void
    func makeCoordinator() -> Coordinator { Coordinator() }
    func makeNSView(context: Context) -> NSView { context.coordinator.install(); return NSView() }
    func updateNSView(_ view: NSView, context: Context) { context.coordinator.action = action }
    final class Coordinator {
        var action: () -> Void = {}
        var monitor: Any?
        func install() {
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard let self, event.window?.isKeyWindow == true,
                      BotShortcutPolicy.handles(keyCode: event.keyCode, modifiers: event.modifierFlags,
                        isRepeat: event.isARepeat,
                        editingText: event.window?.firstResponder is NSTextView
                            || event.window?.firstResponder is NSTextField,
                        hasSheet: event.window?.attachedSheet != nil) else { return event }
                action()
                return nil
            }
        }
        deinit { if let monitor { NSEvent.removeMonitor(monitor) } }
    }
}

struct BotControlsView: View {
    @ObservedObject var model: LiveCaptureModel
    @ObservedObject private var collection: BotDemonstrationController
    @ObservedObject private var player: BotPlaybackController
    @ObservedObject private var bot: BotDemoController
    let domain: BotDomain
    let startIssue: String?
    let action: () -> Void
    @State private var showsBotDetails = false

    init(model: LiveCaptureModel, domain: BotDomain, startIssue: String?, action: @escaping () -> Void) {
        self.model = model; self.domain = domain; self.startIssue = startIssue; self.action = action
        collection = model.botDemonstrations; player = model.botPlayer; bot = model.botDemo
    }
    var body: some View {
        let active = player.isActive || bot.isActive
        let help = BotControlPresentationPolicy.help(active: active, startIssue: startIssue, lastIssue: bot.issue)
        HStack(spacing: 5) {
            Button(action: action) {
                HStack(spacing: 4) {
                    Text("🤖")
                    Text("B").fontWeight(.semibold)
                    if player.isActive || bot.isActive { Image(systemName: "stop.circle.fill") }
                }
            }
            .disabled(!BotControlPresentationPolicy.canToggle(active: active, startIssue: startIssue))
            .tint(player.isActive || bot.isActive ? .green : .accentColor)
            .accessibilityIdentifier("bot-control")
            .accessibilityLabel(player.isActive || bot.isActive ? "Stop Hollow Knight Bot" : "Start Hollow Knight Bot")
            .help(help)
            .accessibilityHint(Text(help))
            .contextMenu {
                Button("Bot details") { showsBotDetails = true }
                Button("Show save archives") { NSWorkspace.shared.open(bot.lastArchiveURL ?? BotDemoSaveArchive.archivesURL) }
            }
            .popover(isPresented: $showsBotDetails, arrowEdge: .top) {
                VStack(alignment: .leading, spacing: 10) {
                    Text(player.loadedModelName(for: domain) ?? "Hollow Knight Bot").font(.headline)
                    Text(player.status)
                    Text(domain == .hacker ? "Hacker uses frame-matched mod actors and your green floors."
                         : "Gameplay uses detected terrain and visual actors.").font(.caption)
                    if let issue = player.issue { Text(issue).foregroundStyle(.red) }
                    Button("Reload model") { player.reloadModel() }
                }.padding(16).frame(width: 350)
            }
            if player.isActive {
                Text(player.isWaiting ? "BOT · \(player.domain.title) · waiting" : "BOT · \(player.domain.title) · \(player.lastAction.displayName)")
                    .font(.caption).foregroundStyle(player.isWaiting ? .orange : .green)
                    .help("\(player.status) · \(player.decisionCount) decisions")
                    .accessibilityIdentifier("bot-playback-status")
            } else if bot.isActive || bot.issue != nil {
                Text(bot.status).font(.caption).foregroundStyle(bot.issue == nil ? Color.green : Color.orange)
                    .lineLimit(1).frame(maxWidth: 180).help(bot.status)
                    .accessibilityIdentifier("bot-navigation-status")
            }
            if collection.isRecording {
                let counts = collection.pairedGameplayUsableCount.map { "H \(collection.usableCount) · G \($0)" }
                    ?? String(collection.usableCount)
                Text("REC \(Int(collection.elapsed))s · \(counts)")
                    .font(.caption.monospacedDigit()).foregroundStyle(.red)
                    .help(collection.pairedGameplayUsableCount == nil ? "Usable observations recorded" : "Usable observations for Hacker (H) and Gameplay (G)")
            }
        }
        .background { BotShortcutMonitor(action: action).frame(width: 0, height: 0) }
    }
}
