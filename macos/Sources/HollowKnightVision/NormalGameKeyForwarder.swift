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

enum NativeInputAccessPolicy {
    static func isGranted(postEvent: Bool, accessibility: Bool) -> Bool {
        postEvent || accessibility
    }
}

struct NativeGameWindowGeometry: Equatable {
    /// Accessibility and Quartz use a top-left global screen coordinate space.
    let frame: CGRect
    /// Window-relative ScreenCaptureKit source rectangle. This excludes native
    /// title-bar chrome when the captured game surface does.
    let captureSourceRect: CGRect
}

enum NativeGamePointerProjection {
    static func screenPoint(
        normalizedPoint: CGPoint,
        geometry: NativeGameWindowGeometry
    ) -> CGPoint? {
        guard normalizedPoint.x.isFinite, normalizedPoint.y.isFinite,
              (0...1).contains(normalizedPoint.x),
              (0...1).contains(normalizedPoint.y),
              geometry.frame.width.isFinite, geometry.frame.height.isFinite,
              geometry.frame.width > 0, geometry.frame.height > 0
        else { return nil }
        let windowBounds = CGRect(origin: .zero, size: geometry.frame.size)
        let source = geometry.captureSourceRect.standardized.intersection(windowBounds)
        guard !source.isNull, source.width > 0, source.height > 0 else { return nil }
        // Vision's normalized Y is bottom-up. CGEvent mouse positions are
        // top-down, so invert Y after applying the same source crop used by
        // ScreenCaptureKit. Keep edge coordinates inside the target window.
        let localX = min(source.maxX - 0.5, source.minX + normalizedPoint.x * source.width)
        let localY = min(
            source.maxY - 0.5,
            source.minY + (1 - normalizedPoint.y) * source.height
        )
        return CGPoint(
            x: geometry.frame.minX + localX,
            y: geometry.frame.minY + localY
        )
    }
}

enum NativeGameEventFactory {
    static func keyboard(
        keyCode: UInt16,
        isDown: Bool,
        flags: CGEventFlags = []
    ) -> CGEvent? {
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let event = CGEvent(
                keyboardEventSource: source,
                virtualKey: CGKeyCode(keyCode),
                keyDown: isDown
              ) else { return nil }
        source.localEventsSuppressionInterval = 0
        event.flags = flags
        event.setIntegerValueField(.keyboardEventAutorepeat, value: 0)
        return event
    }

    static func pointer(
        kind: GamePointerEventKind,
        screenPoint: CGPoint,
        clickCount: Int
    ) -> CGEvent? {
        let type: CGEventType
        switch kind {
        case .moved: type = .mouseMoved
        case .leftDown: type = .leftMouseDown
        case .leftDragged: type = .leftMouseDragged
        case .leftUp: type = .leftMouseUp
        case .exited: return nil
        }
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let event = CGEvent(
                mouseEventSource: source,
                mouseType: type,
                mouseCursorPosition: screenPoint,
                mouseButton: .left
              ) else { return nil }
        source.localEventsSuppressionInterval = 0
        event.setIntegerValueField(.mouseEventClickState, value: Int64(max(1, clickCount)))
        return event
    }
}

/// Main-thread-only native input transport for the unmodified macOS game.
/// Builds clean keyboard and pointer events for one validated PID, never
/// posting them globally.
final class NormalGameKeyForwarder {
    private let gameURL: URL?
    private let activityHandler: () -> Void
    private let statusHandler: (String) -> Void
    private var monitor: Any?
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var keys = NativeGameKeyState()
    private var pressFlags: [UInt16: CGEventFlags] = [:]
    private var target: NSRunningApplication?
    private var focusTransferPending = false
    private var focusTransferGeneration: UInt64 = 0
    private var cachedWindowGeometry: NativeGameWindowGeometry?
    private var cachedWindowGeometryAt: TimeInterval = 0
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
                self.cancelFocusTransfer()
                self.panel?.level = .normal
            })
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.didTerminateApplicationNotification,
            object: nil, queue: .main) { [weak self] _ in
                guard let self, self.target?.isTerminated == true else { return }
                self.releaseAll()
                self.cancelFocusTransfer()
                self.target = nil
                self.cachedWindowGeometry = nil
                self.panel?.level = .normal
            })
        if hasEventPostingAccess() {
            statusHandler(visionText("nativeInputReady"))
        } else {
            statusHandler(visionText("nativeInputPermission"))
            DispatchQueue.main.async { [weak self] in
                guard let self, let panel = self.panel else { return }
                self.showPermissionHelp(in: panel)
            }
        }
    }

    func stop() {
        started = false
        cancelFocusTransfer()
        releaseAll()
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers.removeAll()
        workspaceObservers.forEach(NSWorkspace.shared.notificationCenter.removeObserver)
        workspaceObservers.removeAll()
        cachedWindowGeometry = nil
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
        focusTransferGeneration &+= 1
        let generation = focusTransferGeneration
        _ = game.activate(options: [])
        finishFocusTransfer(
            to: game,
            panel: panel,
            generation: generation,
            attemptsRemaining: 20
        )
    }

    private func finishFocusTransfer(
        to game: NSRunningApplication,
        panel: NormalVisionPanel,
        generation: UInt64,
        attemptsRemaining: Int
    ) {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self, weak panel] in
            guard let self, self.started,
                  self.focusTransferGeneration == generation,
                  let panel, panel.isVisible, panel.attachedSheet == nil
            else { return }
            if game.isActive {
                self.focusTransferPending = false
                panel.level = .floating
                panel.makeKeyAndOrderFront(nil)
            } else if attemptsRemaining > 1 {
                self.finishFocusTransfer(
                    to: game,
                    panel: panel,
                    generation: generation,
                    attemptsRemaining: attemptsRemaining - 1
                )
            } else {
                self.focusTransferPending = false
            }
        }
    }

    private func cancelFocusTransfer() {
        focusTransferGeneration &+= 1
        focusTransferPending = false
    }

    private func validatedGame() -> NSRunningApplication? {
        if let target, !target.isTerminated { return target }
        let runningGames = NSWorkspace.shared.runningApplications.filter { application in
            guard application.processIdentifier != ProcessInfo.processInfo.processIdentifier else {
                return false
            }
            let name = application.localizedName?.lowercased() ?? ""
            let bundle = application.bundleIdentifier?.lowercased() ?? ""
            return name == "hollow knight"
                || (bundle.contains("hollowknight") && !bundle.contains("vision"))
        }
        let game = runningGames.first { application in
            guard let gameURL else { return true }
            return application.bundleURL?.resolvingSymlinksInPath().standardizedFileURL
                == gameURL
        }
        guard let game, let url = game.bundleURL,
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
        guard ensureEventPostingAccess(in: panel) else { return nil }
        let flags = event.cgEvent?.flags ?? []
        guard let forwardedEvent = NativeGameEventFactory.keyboard(
            keyCode: event.keyCode,
            isDown: true,
            flags: flags
        ) else { return event }
        target = game
        guard keys.press(event.keyCode, targetPID: game.processIdentifier,
                         isRepeat: event.isARepeat) else { return nil }
        pressFlags[event.keyCode] = flags
        forwardedEvent.postToPid(game.processIdentifier)
        statusHandler(visionText("nativeInputReady"))
        activityHandler()
        return nil
    }

    func forwardPointer(_ pointerEvent: GamePointerEvent) -> Bool {
        guard started, let panel, panel.isVisible, panel.attachedSheet == nil,
              let game = validatedGame(), game.isActive else {
            prepareForInput()
            return false
        }
        if pointerEvent.kind == .exited { return true }
        guard ensureEventPostingAccess(in: panel),
              let geometry = gameWindowGeometry(
                for: game.processIdentifier,
                refresh: pointerEvent.kind == .leftDown
              ),
              let screenPoint = NativeGamePointerProjection.screenPoint(
                normalizedPoint: pointerEvent.normalizedPoint,
                geometry: geometry
              ),
              let event = NativeGameEventFactory.pointer(
                kind: pointerEvent.kind,
                screenPoint: screenPoint,
                clickCount: pointerEvent.clickCount
              ) else { return false }
        target = game
        event.postToPid(game.processIdentifier)
        if pointerEvent.kind == .leftDown { activityHandler() }
        return true
    }

    private func ensureEventPostingAccess(in panel: NormalVisionPanel) -> Bool {
        guard hasEventPostingAccess() else {
            releaseAll()
            statusHandler(visionText("nativeInputPermission"))
            showPermissionHelp(in: panel)
            return false
        }
        return true
    }

    private func hasEventPostingAccess() -> Bool {
        NativeInputAccessPolicy.isGranted(
            postEvent: CGPreflightPostEventAccess(),
            accessibility: AXIsProcessTrusted()
        )
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
            self?.requestedPermission = false
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
        guard keys.release(keyCode), let flags = pressFlags.removeValue(forKey: keyCode),
              let pid, let target, !target.isTerminated,
              target.processIdentifier == pid else { return }
        guard let event = NativeGameEventFactory.keyboard(
            keyCode: keyCode,
            isDown: false,
            flags: flags
        ) else { return }
        event.postToPid(pid)
    }

    func releaseAll() {
        for key in keys.heldKeyCodes { release(key) }
        _ = keys.releaseAll()
        pressFlags.removeAll()
    }

    private func gameWindowGeometry(
        for processID: pid_t,
        refresh: Bool
    ) -> NativeGameWindowGeometry? {
        let now = ProcessInfo.processInfo.systemUptime
        if !refresh, let cachedWindowGeometry,
           now - cachedWindowGeometryAt < 0.25 {
            return cachedWindowGeometry
        }
        let application = AXUIElementCreateApplication(processID)
        var windowsValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            application,
            kAXWindowsAttribute as CFString,
            &windowsValue
        ) == .success,
        let windows = windowsValue as? [AXUIElement] else { return cachedWindowGeometry }
        let candidates = windows.map { window in
            GameWindowCandidate(
                title: accessibilityTitle(of: window),
                size: accessibilitySize(of: window) ?? .zero
            )
        }
        guard let index = GameWindowSelection.bestIndex(in: candidates),
              let position = accessibilityPosition(of: windows[index]),
              let size = accessibilitySize(of: windows[index]),
              size.width > 0, size.height > 0 else { return cachedWindowGeometry }
        let geometry = NativeGameWindowGeometry(
            frame: CGRect(origin: position, size: size),
            captureSourceRect: HollowKnightCaptureConfiguration.gameplaySourceRect(
                windowSize: size
            ) ?? CGRect(origin: .zero, size: size)
        )
        cachedWindowGeometry = geometry
        cachedWindowGeometryAt = now
        return geometry
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

    private func accessibilityPosition(of element: AXUIElement) -> CGPoint? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXPositionAttribute as CFString,
            &value
        ) == .success,
        let value,
        CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero
        guard AXValueGetValue(value as! AXValue, .cgPoint, &point) else { return nil }
        return point
    }

    private func accessibilitySize(of element: AXUIElement) -> CGSize? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element,
            kAXSizeAttribute as CFString,
            &value
        ) == .success,
        let value,
        CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var size = CGSize.zero
        guard AXValueGetValue(value as! AXValue, .cgSize, &size) else { return nil }
        return size
    }
}
