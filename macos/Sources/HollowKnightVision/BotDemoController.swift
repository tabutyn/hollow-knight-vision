import AppKit
import Combine
import Foundation
import OSLog

final class BotDemoController: ObservableObject {
    @Published private(set) var isActive = false
    @Published private(set) var status = "Bot starts a fresh game in slot 4"
    @Published private(set) var issue: String?
    @Published private(set) var lastArchiveURL: URL?
    @Published private(set) var handoffRequest: UUID?
    let handoffRequests = PassthroughSubject<UUID, Never>()
    private(set) var domain = BotDomain.hacker
    private var navigation = BotDemoNavigation()
    private var sessionID: UUID?
    private var runID: UUID?
    private var watchdog: Timer?
    private var focusObserver: NSObjectProtocol?
    private let archive: BotDemoSaveArchive
    private let now: () -> Double
    private let hasFocus: () -> Bool
    private var canContinue: () -> Bool = { false }
    private var beginControl: () -> UUID? = { nil }
    private var pressControl: (GameAutomationCommand.Button, UUID) -> Bool = { _, _ in false }
    private var endControl: (UUID) -> Void = { _ in }
    private var botIsPlaying: () -> Bool = { false }
    private let log = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "bot-demo")
    private(set) var lastObservation: BotDemoMenuObservation?
    private var lastObservationLogAt = -Double.infinity
    private var activeProfileSavePresent = false

    init(archive: BotDemoSaveArchive = .init(), now: @escaping () -> Double = { ProcessInfo.processInfo.systemUptime },
         hasFocus: @escaping () -> Bool = { NSApp.isActive && NSApp.keyWindow != nil }) {
        self.archive = archive; self.now = now; self.hasFocus = hasFocus
    }
    var isHandingOff: Bool { navigation.phase == .waitingForBot }
    var isPlaying: Bool { navigation.phase == .playing }
    var phaseName: String { String(describing: navigation.phase) }

    func rejectStart(_ reason: String) {
        status = reason; issue = reason
        log.error("start rejected reason=\(reason, privacy: .public)")
    }

    func configure(canContinue: @escaping () -> Bool, begin: @escaping () -> UUID?,
                   press: @escaping (GameAutomationCommand.Button, UUID) -> Bool,
                   end: @escaping (UUID) -> Void, botIsPlaying: @escaping () -> Bool) {
        self.canContinue = canContinue; beginControl = begin; pressControl = press
        endControl = end; self.botIsPlaying = botIsPlaying
    }

    func start(inGameplay: Bool = false, domain: BotDomain = .hacker) {
        guard !isActive else { return }
        guard canContinue() else { rejectStart("Bot is not ready. Finish recording, replay, or editing and check capture."); return }
        guard hasFocus() else { rejectStart("Focus the Vision window to start the bot."); return }
        guard let id = beginControl() else { rejectStart("Bot needs a connected Modded receiver."); return }
        self.domain = domain
        sessionID = id; runID = UUID(); navigation.begin(at: now())
        isActive = true; issue = nil; handoffRequest = nil; status = navigation.status
        lastObservation = nil
        log.notice("started session=\(id.uuidString, privacy: .public)")
        watchdog = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in self?.checkState() }
        if let window = NSApp?.mainWindow {
            focusObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                object: window, queue: .main) { [weak self] _ in self?.stop(reason: "Window focus changed") }
        }
        if inGameplay {
            navigation.beginGameplay(at: now()); status = navigation.status
            handle(.startBot)
        }
    }

    func stop(reason: String = "Bot stopped", failed: Bool = false) {
        guard isActive || sessionID != nil else { return }
        let id = sessionID
        sessionID = nil; runID = nil; handoffRequest = nil; navigation.stop(); isActive = false
        watchdog?.invalidate(); watchdog = nil
        if let focusObserver { NotificationCenter.default.removeObserver(focusObserver) }
        focusObserver = nil
        if let id { endControl(id) }
        status = reason
        log.notice("stopped reason=\(reason, privacy: .public)")
        if failed { issue = reason }
    }

    func interrupted(sessionID: UUID, reason: String) {
        guard self.sessionID == sessionID else { return }
        stop(reason: reason)
    }

    func observe(_ observation: BotDemoMenuObservation) {
        guard isActive else { return }
        lastObservation = observation
        if now() - lastObservationLogAt >= 1 {
            lastObservationLogAt = now()
            log.notice("observed phase=\(self.phaseName, privacy: .public) context=\(observation.context?.storageIdentifier ?? "unknown", privacy: .public) selected=\(observation.selectedIdentifier ?? "none", privacy: .public) gameplay=\(observation.gameplay, privacy: .public)")
        }
        checkState()
        guard isActive else { return }
        do {
            var occupied = false
            if observation.context == .selectProfile, navigation.phase != .waitingForGameplay,
               navigation.phase != .waitingForBot, navigation.phase != .playing {
                occupied = try archive.hasActiveSave(matching: observation.occupiedSlots)
                activeProfileSavePresent = occupied
            }
            let effect = navigation.observe(observation, hasActiveSave: occupied, now: now())
            status = navigation.status
            if let effect { handle(effect) }
        } catch BotDemoSaveArchiveError.profileMismatch {
            // Profile text can appear before every Clear Save label has faded
            // in. Wait for complete, agreeing evidence instead of declaring a
            // file mismatch on the first partial frame.
            status = "Waiting for profile/save version agreement"
        } catch { stop(reason: "Bot save check failed: \(error.localizedDescription)", failed: true) }
    }

    func finishWithoutPolicy(_ request: UUID) {
        guard isActive, isHandingOff, handoffRequest == request else { return }
        stop(reason: "Gameplay reached. Train or select a \(domain.title) bot model to play; controls returned to you.", failed: true)
    }

    func completeHandoff(_ request: UUID, startBot: () -> Bool) {
        guard isActive, isHandingOff, handoffRequest == request, hasFocus(), canContinue() else { return }
        guard startBot() else { stop(reason: "Bot could not start after new-game Gameplay", failed: true); return }
        handoffRequest = nil; navigation.botStarted(at: now()); status = navigation.status
        log.notice("handoff completed: bot playing")
    }

    private func handle(_ effect: BotDemoNavigation.Effect) {
        switch effect {
        case .press(let button):
            log.notice("menu input=\(button.rawValue, privacy: .public) phase=\(self.phaseName, privacy: .public)")
            if button == .z && navigation.phase == .waitingForEmptyProfile {
                do {
                    guard let url = lastArchiveURL else { throw BotRuntimeError.incompatible("No verified slot 4 archive") }
                    try archive.verifySourceMatchesArchive(at: url)
                } catch { stop(reason: error.localizedDescription, failed: true); return }
            }
            guard let sessionID, pressControl(button, sessionID) else {
                stop(reason: "Bot menu controls interrupted", failed: true); return
            }
        case .archiveSlot:
            let archive = archive, run = runID, activeSavePresent = activeProfileSavePresent
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                let result = Result { try archive.create(activeSavePresent: activeSavePresent) }
                DispatchQueue.main.async { [weak self] in
                    guard let self, self.isActive, self.runID == run else { return }
                    switch result {
                    case .success(let saved):
                        self.lastArchiveURL = saved.url
                        self.log.notice("archive verified path=\(saved.url.path, privacy: .public) occupied=\(saved.hadActiveSave, privacy: .public)")
                        self.navigation.archived(hasActiveSave: saved.hadActiveSave, at: self.now())
                        self.status = self.navigation.status
                    case .failure(let error):
                        self.stop(reason: "Bot archive failed: \(error.localizedDescription)", failed: true)
                    }
                }
            }
        case .startBot:
            let oldSession = sessionID
            sessionID = nil
            if let oldSession { endControl(oldSession) }
            let request = UUID()
            handoffRequest = request
            // @Published emits in willSet. Deliver the actionable event after
            // the token and the capture's Gameplay state have been stored.
            DispatchQueue.main.async { [weak self] in
                guard let self, self.isActive, self.handoffRequest == request else { return }
                self.handoffRequests.send(request)
            }
        case .fail(let reason): stop(reason: reason, failed: true)
        }
    }

    private func checkState() {
        guard isActive else { return }
        guard hasFocus() else { stop(reason: "Window focus changed"); return }
        guard canContinue() else { stop(reason: "Bot readiness changed"); return }
        if isPlaying && !botIsPlaying() { stop(reason: "Bot stopped"); return }
        if let effect = navigation.checkTimeout(at: now()) { handle(effect) }
    }
}
