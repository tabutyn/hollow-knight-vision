import AppKit
import CoreGraphics

/// A nonactivating key panel lets Unity keep application focus while Vision
/// owns keyboard focus. Ordinary windows make stock Unity discard our events.
final class NormalVisionPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

struct NativeGameKeyState {
    private(set) var targetPID: pid_t?
    private(set) var heldKeyCodes = Set<UInt16>()

    mutating func press(_ keyCode: UInt16, targetPID: pid_t, isRepeat: Bool) -> Bool {
        guard !isRepeat, self.targetPID == nil || self.targetPID == targetPID,
              heldKeyCodes.insert(keyCode).inserted else { return false }
        self.targetPID = targetPID
        return true
    }

    mutating func release(_ keyCode: UInt16) -> Bool {
        guard heldKeyCodes.remove(keyCode) != nil else { return false }
        if heldKeyCodes.isEmpty { targetPID = nil }
        return true
    }

    mutating func releaseAll() -> Set<UInt16> {
        let released = heldKeyCodes
        heldKeyCodes.removeAll()
        targetPID = nil
        return released
    }
}

enum NormalGameKeyPolicy {
    static let arrows: Set<UInt16> = [123, 124, 125, 126]
    // Common stock bindings plus menu keys. L and S stay with Vision for
    // labeling and stencils, matching the existing workspace shortcuts.
    static let supported = arrows.union([0, 2, 6, 7, 8, 34, 36, 48, 49, 53])

    static func keyCodes(labeling: Bool, reviewing: Bool, settings: Bool) -> Set<UInt16> {
        guard !labeling, !reviewing else { return [] }
        return settings ? arrows : supported
    }
}

/// Main-thread-only native input transport for the unmodified macOS game.
/// Posts copies of local events to one validated game PID, never globally.
final class NormalGameKeyForwarder {
    private let gameURL: URL?
    private let activityHandler: () -> Void
    private let statusHandler: (String) -> Void
    private var monitor: Any?
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var keys = NativeGameKeyState()
    private var pressEvents: [UInt16: CGEvent] = [:]
    private var target: NSRunningApplication?
    private var focusTransferPending = false
    private var requestedPermission = false
    private var started = false
    private var labeling = false
    private var reviewing = false
    private var settings = false

    init(gameURL: URL?, activityHandler: @escaping () -> Void,
         statusHandler: @escaping (String) -> Void) {
        self.gameURL = gameURL?.resolvingSymlinksInPath().standardizedFileURL
        self.activityHandler = activityHandler
        self.statusHandler = statusHandler
    }

    private var panel: NormalVisionPanel? {
        NSApp.windows.compactMap { $0 as? NormalVisionPanel }.first
    }

    func start() {
        guard !started else { return }
        started = true
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self else { return event }
            return self.handle(event)
        }
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSWindow.didResignKeyNotification,
            object: nil, queue: .main) { [weak self] note in
                guard let self, let window = note.object as? NormalVisionPanel else { return }
                self.releaseAll()
                window.level = .normal
            })
        observers.append(center.addObserver(forName: NSWindow.didBecomeKeyNotification,
            object: nil, queue: .main) { [weak self] note in
                guard note.object is NormalVisionPanel else { return }
                self?.prepareForInput()
            })
        observers.append(center.addObserver(forName: NSApplication.didBecomeActiveNotification,
            object: nil, queue: .main) { [weak self] _ in
                self?.prepareForInput()
            })
        let workspace = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] note in
                guard let self,
                      let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                      app.processIdentifier != ProcessInfo.processInfo.processIdentifier,
                      app.processIdentifier != self.target?.processIdentifier else { return }
                self.releaseAll()
                self.panel?.level = .normal
            })
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.didTerminateApplicationNotification,
            object: nil, queue: .main) { [weak self] _ in
                guard let self, self.target?.isTerminated == true else { return }
                self.releaseAll()
                self.target = nil
                self.panel?.level = .normal
            })
        statusHandler(visionText("nativeInputReady"))
    }

    func stop() {
        started = false
        releaseAll()
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers.removeAll()
        workspaceObservers.forEach(NSWorkspace.shared.notificationCenter.removeObserver)
        workspaceObservers.removeAll()
        panel?.level = .normal
    }

    func setLabeling(_ active: Bool) { releaseAll(); labeling = active }
    func setReviewing(_ active: Bool) { releaseAll(); reviewing = active }
    func setSettings(_ active: Bool) { releaseAll(); settings = active }

    /// Called when capture is ready or the user returns to Vision. Never raises
    /// Vision in response to arbitrary activation of the game or another app.
    func prepareForInput() {
        guard started, !focusTransferPending, let panel, panel.isVisible,
              panel.attachedSheet == nil, let game = validatedGame() else { return }
        target = game
        if game.isActive {
            panel.level = .floating
            panel.makeKeyAndOrderFront(nil)
            return
        }
        guard NSApp.isActive || panel.isKeyWindow else { return }
        releaseAll()
        focusTransferPending = true
        _ = game.activate(options: [])
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self, weak panel] in
            guard let self else { return }
            self.focusTransferPending = false
            guard self.started, game.isActive, let panel, panel.isVisible,
                  panel.attachedSheet == nil else { return }
            panel.level = .floating
            panel.makeKeyAndOrderFront(nil)
        }
    }

    private func validatedGame() -> NSRunningApplication? {
        if let target, !target.isTerminated { return target }
        guard let game = GameControlForwarder.hollowKnightApplication(),
              let url = game.bundleURL,
              gameURL == nil || url.resolvingSymlinksInPath().standardizedFileURL == gameURL,
              HollowKnightInstallationPolicy.issue(gameURL: url, mode: .normal) == nil
        else { return nil }
        return game
    }

    private func handle(_ event: NSEvent) -> NSEvent? {
        // Releases bypass UI filters: a field, sheet or workspace may have
        // gained focus between key-down and key-up.
        if event.type == .keyUp, keys.heldKeyCodes.contains(event.keyCode) {
            release(event.keyCode)
            return nil
        }
        guard started, let panel, panel.isKeyWindow, panel.attachedSheet == nil,
              event.window === panel else { return event }
        let textInput = panel.firstResponder is NSTextView || panel.firstResponder is NSTextField
        guard !textInput || (settings && NormalGameKeyPolicy.arrows.contains(event.keyCode)) else {
            releaseAll()
            return event
        }
        guard GameKeyRelay.action(type: event.type, keyCode: event.keyCode,
            modifierFlags: event.modifierFlags,
            supportedKeyCodes: NormalGameKeyPolicy.keyCodes(
                labeling: labeling, reviewing: reviewing, settings: settings)
        ) == .forward, event.type == .keyDown else { return event }
        guard let game = validatedGame(), game.isActive else {
            prepareForInput()
            return event
        }
        guard CGPreflightPostEventAccess() else {
            releaseAll()
            statusHandler(visionText("nativeInputPermission"))
            showPermissionHelp(in: panel)
            return nil
        }
        guard let copy = event.cgEvent?.copy() else { return event }
        target = game
        guard keys.press(event.keyCode, targetPID: game.processIdentifier,
                         isRepeat: event.isARepeat) else { return nil }
        pressEvents[event.keyCode] = copy
        copy.postToPid(game.processIdentifier)
        statusHandler(visionText("nativeInputReady"))
        activityHandler()
        return nil
    }

    private func showPermissionHelp(in panel: NormalVisionPanel) {
        guard !requestedPermission else { return }
        requestedPermission = true
        let alert = NSAlert()
        alert.messageText = visionText("nativeInputPermissionTitle")
        alert.informativeText = visionText("nativeInputPermission")
        alert.addButton(withTitle: visionText("Settings"))
        alert.addButton(withTitle: visionText("cancel"))
        alert.beginSheetModal(for: panel) { [weak self] response in
            if response == .alertFirstButtonReturn {
                _ = CGRequestPostEventAccess()
                if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
                    NSWorkspace.shared.open(url)
                }
            } else {
                DispatchQueue.main.async { self?.prepareForInput() }
            }
        }
        // A stale macOS grant can suppress the system prompt. Keep this
        // explanation visible even when the game owns a fullscreen Space.
        NSApp.activate(ignoringOtherApps: true)
    }

    private func release(_ keyCode: UInt16) {
        let pid = keys.targetPID
        guard keys.release(keyCode), let event = pressEvents.removeValue(forKey: keyCode),
              let pid, let target, !target.isTerminated,
              target.processIdentifier == pid else { return }
        event.type = .keyUp
        event.flags = []
        event.setIntegerValueField(.keyboardEventAutorepeat, value: 0)
        event.postToPid(pid)
    }

    func releaseAll() {
        for key in keys.heldKeyCodes { release(key) }
        _ = keys.releaseAll()
        pressEvents.removeAll()
    }
}
