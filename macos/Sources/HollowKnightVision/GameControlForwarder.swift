import AppKit
import Foundation
import Network
import OSLog

enum GameKeyRelayAction: Equatable { case pass; case forward }

struct GameAutomationCommand: Equatable {
    static let darwinNotificationName =
        "com.ballroller.hollow-knight-vision.automation-command"
    static let pasteboardName =
        "com.ballroller.hollow-knight-vision.automation-pasteboard"

    enum Button: String, Equatable {
        case left, right, down, up, a, z, x, inventory, pause
    }

    let button: Button
    let duration: TimeInterval

    init(button: Button, duration: TimeInterval) {
        self.button = button
        self.duration = duration
    }

    init?(serialized: String) {
        let components = serialized.split(separator: ":", omittingEmptySubsequences: false)
        guard (1...2).contains(components.count),
              let button = Button(rawValue: String(components[0]))
        else { return nil }
        let requestedDuration = components.count == 2
            ? TimeInterval(components[1]) : 1.5
        guard let requestedDuration, requestedDuration.isFinite,
              requestedDuration >= 0.05, requestedDuration <= 10
        else { return nil }
        self.button = button
        duration = requestedDuration
    }
}

struct GamePathPlaybackCommand: Equatable {
    static let serializedPrefix = "replay-path:"

    let fileName: String
    let iteration: Int

    init(fileName: String, iteration: Int) {
        self.fileName = fileName
        self.iteration = iteration
    }

    init?(serialized: String) {
        guard serialized.hasPrefix(Self.serializedPrefix) else { return nil }
        let payload = serialized.dropFirst(Self.serializedPrefix.count)
        let components = payload.split(separator: ":", omittingEmptySubsequences: false)
        guard components.count == 2,
              let iteration = Int(components[1]), (1...10_000).contains(iteration)
        else { return nil }
        let fileName = String(components[0])
        guard fileName == (fileName as NSString).lastPathComponent,
              fileName.hasPrefix("path-"), fileName.hasSuffix(".json") else { return nil }
        self.fileName = fileName
        self.iteration = iteration
    }
}

struct GameKeyRelay {
    static func action(
        type: NSEvent.EventType,
        keyCode: UInt16,
        modifierFlags: NSEvent.ModifierFlags = [],
        supportedKeyCodes: Set<UInt16> = GameControlForwarder.supportedKeyCodes,
        inputSuppressed: Bool = false
    ) -> GameKeyRelayAction {
        guard !inputSuppressed, supportedKeyCodes.contains(keyCode),
              type == .keyDown || type == .keyUp else { return .pass }
        // A release must always reach the bridge: modifiers can change between
        // press and release, and dropping it is how controls become stuck.
        if type == .keyUp { return .forward }
        return modifierFlags.intersection([.command, .control, .option]).isEmpty ? .forward : .pass
    }
}

enum GamePauseState: Equatable {
    case unavailable
    case ready
    case pausing
    case paused
    case resuming
}

enum GamePointerEventKind: Equatable {
    case moved
    case leftDown
    case leftDragged
    case leftUp
    case exited
}

struct GamePointerEvent: Equatable {
    let kind: GamePointerEventKind
    let normalizedPoint: CGPoint
    let clickCount: Int
}

enum GamePointerProjection {
    /// Converts a point in the atlas view into the current captured frame. The
    /// view is bottom-up, matching AppKit; the returned y therefore remains
    /// bottom-up until it is projected into macOS's top-down screen space.
    static func normalizedPoint(
        viewPoint: CGPoint,
        displayedLiveFrame: CGRect
    ) -> CGPoint? {
        let frame = displayedLiveFrame.standardized
        guard LayerSceneProjection.isFinite(frame), frame.width > 0, frame.height > 0,
              viewPoint.x.isFinite, viewPoint.y.isFinite,
              frame.contains(viewPoint) else { return nil }
        return CGPoint(
            x: (viewPoint.x - frame.minX) / frame.width,
            y: (viewPoint.y - frame.minY) / frame.height
        )
    }

}

/// Uses global key state only after macOS has positively reported a key held.
/// Some ad-hoc builds receive ordinary local key events while global polling
/// remains unavailable; treating that unavailable state as "released" turns
/// every press into a 16 ms tap.
struct PhysicalKeyReconciler {
    private(set) var globallyObservedKeyCodes = Set<UInt16>()

    mutating func releasedKeyCodes(
        from heldKeyCodes: Set<UInt16>,
        isPhysicallyHeld: (UInt16) -> Bool
    ) -> Set<UInt16> {
        globallyObservedKeyCodes.formIntersection(heldKeyCodes)
        var released = Set<UInt16>()
        for keyCode in heldKeyCodes {
            if isPhysicallyHeld(keyCode) {
                globallyObservedKeyCodes.insert(keyCode)
            } else if globallyObservedKeyCodes.remove(keyCode) != nil {
                released.insert(keyCode)
            }
        }
        return released
    }

    mutating func noteLocalRelease(_ keyCode: UInt16) {
        globallyObservedKeyCodes.remove(keyCode)
    }

    mutating func reset() {
        globallyObservedKeyCodes.removeAll(keepingCapacity: true)
    }
}

/// Sends Vision's local keyboard state to the receiver inside Hollow Knight
/// and normalized pointer events to the in-game receiver. It never activates
/// the game, posts synthetic macOS input, or moves the system pointer.
final class GameControlForwarder {
    /// Capturing is immediate. Restoring may first load another Unity scene;
    /// the receiver deliberately allows that operation twelve seconds, so the
    /// client must not discard its acknowledgement after the old three-second
    /// request timeout.
    private static let checkpointCaptureTimeout: TimeInterval = 3
    private static let checkpointRestoreTimeout: TimeInterval = 15
    private let pointerLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision", category: "game-pointer"
    )
    static let gameplayKeyCodes: Set<UInt16> = [0, 6, 7, 123, 124, 125, 126]
    static let menuShortcutKeyCodes: Set<UInt16> = [34, 35] // I, P
    static let supportedKeyCodes = gameplayKeyCodes.union(menuShortcutKeyCodes)
    // Model owns the arrow keys for review navigation and X for returning to
    // Gameplay. Do not consume or forward those keys to Hollow Knight while
    // review is active; SwiftUI receives them as local workspace commands.
    static let reviewNavigationKeyCodes: Set<UInt16> = [7, 123, 124, 125, 126]

    private static let receiverHost = NWEndpoint.Host("127.0.0.1")
    private static let receiverPort = NWEndpoint.Port(rawValue: 36_752)!
    private static let samplingInterval: DispatchTimeInterval = .milliseconds(16)
    private static let pauseRenewalInterval: DispatchTimeInterval = .milliseconds(750)

    private let activityHandler: () -> Void
    private let connectionStateHandler: (String) -> Void
    private let pauseStateHandler: (GamePauseState) -> Void
    private let physicalInputHandler: (RecordedGameButton, Bool, TimeInterval) -> Void
    private let groundTruthHandler: (ReceiverGroundTruthSample, TimeInterval) -> Void
    private let stateQueue = DispatchQueue(label: "com.ballroller.hollow-knight-vision.input-bridge", qos: .userInteractive)
    private var eventMonitor: Any?
    private var notificationObservers = [NSObjectProtocol]()
    private var inputSampler: DispatchSourceTimer?
    private var pauseLeaseTimer: DispatchSourceTimer?
    private var connection: NWConnection?
    private var sender = InputBridgeSenderState()
    private var acknowledgementTracker = InputAcknowledgementTracker()
    private var physicalKeyReconciler = PhysicalKeyReconciler()
    private var receiveBuffer = Data()
    private var started = false
    private var transportReady = false
    private var awaitingFreshPress = true
    private var automatedTapInFlight = false
    private var automationPulseGeneration: UInt64 = 0
    private var automationPulseActive = false
    private var pathPlaybackGeneration: UInt64 = 0
    private var pathPlaybackActive = false
    private var pathPlaybackCompletion: ((Bool) -> Void)?
    private let backgroundPathPlayback = ProcessInfo.processInfo.arguments.contains("--enable-automation-control")
        && ProcessInfo.processInfo.arguments.contains("--allow-background-path-playback")
    private let playbackLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "input-path")
    private let groundTraceEnabled = ProcessInfo.processInfo.arguments.contains("--trace-ground-tracking")
    private let groundTraceLog = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "ground-trace")
    private var controlSessionID: UUID?
    private var pauseLeaseMilliseconds = 0
    private var pauseCapabilityAvailable = false
    private var menuShortcutsCapabilityAvailable = false
    private var pointerCapabilityAvailable = false
    private var playerCheckpointCapabilityAvailable = false
    private var playerPosePlaybackCapabilityAvailable = false
    private var groundTruthCapabilityAvailable = false
    private var playerOpsCapabilityAvailable = false
    private var renderFrameMarkerDesired = RenderedFrameMarker.enabled
    private var lastGroundTruthSequence: UInt64?
    private struct PendingCheckpointCommand {
        let completion: (ReceiverCheckpointAcknowledgement?) -> Void
    }
    private var pendingCheckpointCommands = [UUID: PendingCheckpointCommand]()
    private struct PendingPlayerOpsCommand {
        let completion: (ReceiverPlayerOpsAcknowledgement?) -> Void
    }
    private var pendingPlayerOpsCommands = [UUID: PendingPlayerOpsCommand]()
    private var nextPointerSequence: UInt64 = 0
    private var nextPlayerPoseSequence: UInt64 = 0
    private var reviewNavigationActive = false
    private var pauseDesired = false
    private var pauseConfirmed = false
    private var managedApplication: NSRunningApplication?
    private var launchInFlight = false
    private var lastPublishedConnectionState: String?
    private var lastPublishedPauseState: GamePauseState?

    init(
        activityHandler: @escaping () -> Void,
        connectionStateHandler: @escaping (String) -> Void = { _ in },
        pauseStateHandler: @escaping (GamePauseState) -> Void = { _ in },
        physicalInputHandler: @escaping (RecordedGameButton, Bool, TimeInterval) -> Void = { _, _, _ in },
        groundTruthHandler: @escaping (ReceiverGroundTruthSample, TimeInterval) -> Void = { _, _ in }
    ) {
        self.activityHandler = activityHandler
        self.connectionStateHandler = connectionStateHandler
        self.pauseStateHandler = pauseStateHandler
        self.physicalInputHandler = physicalInputHandler
        self.groundTruthHandler = groundTruthHandler
    }

    deinit { stop() }

    func start() {
        guard !started else { return }
        started = true
        installEventMonitor()
        installFocusObservers()
        startInputSampler()
        startPauseLeaseTimer()
        publishConnectionState("Controls unavailable")
        publishPauseState(.unavailable)
        stateQueue.async { [weak self] in self?.connect() }
        _ = ensureGameRunning()
    }

    func stop() {
        guard started else { return }
        started = false
        if let eventMonitor { NSEvent.removeMonitor(eventMonitor) }
        eventMonitor = nil
        notificationObservers.forEach(NotificationCenter.default.removeObserver)
        notificationObservers.removeAll()
        inputSampler?.cancel()
        inputSampler = nil
        pauseLeaseTimer?.cancel()
        pauseLeaseTimer = nil
        stateQueue.sync {
            if pauseDesired || pauseConfirmed { sendResumeLocked() }
            releaseAllLocked()
            connection?.cancel()
            connection = nil
            transportReady = false
            clearPauseStateLocked()
        }
    }

    func setLabelPauseActive(_ active: Bool) {
        stateQueue.async { [weak self] in
            guard let self else { return }
            guard self.started, self.transportReady, self.pauseCapabilityAvailable else {
                self.publishPauseState(.unavailable)
                return
            }
            guard self.pauseDesired != active || self.pauseConfirmed != active else { return }
            self.pauseDesired = active
            if active {
                self.releaseAllLocked()
                self.publishPauseState(.pausing)
                self.sendPauseLocked()
            } else {
                self.publishPauseState(.resuming)
                self.sendResumeLocked()
            }
        }
    }

    func releaseForGroundLabeling() {
        stateQueue.async { [weak self] in self?.releaseAllLocked() }
    }

    func setRenderFrameMarkerEnabled(_ enabled: Bool) {
        stateQueue.async { [weak self] in
            guard let self, self.renderFrameMarkerDesired != enabled else { return }
            self.renderFrameMarkerDesired = enabled
            guard self.transportReady, let sessionID = self.controlSessionID else { return }
            self.sendControlLocked(ReceiverCapabilityRequest(
                sessionID: sessionID,
                renderFrameMarker: enabled
            ))
        }
    }

    func setReviewNavigationActive(_ active: Bool) {
        stateQueue.async { [weak self] in
            guard let self, self.reviewNavigationActive != active else { return }
            self.releaseAllLocked()
            self.reviewNavigationActive = active
        }
    }

    @discardableResult
    func capturePlayerCheckpoint(
        completion: @escaping (RecordedGameCheckpoint?, String?) -> Void
    ) -> Bool {
        stateQueue.sync {
            guard started, transportReady, playerCheckpointCapabilityAvailable,
                  let controlSessionID, pendingCheckpointCommands.isEmpty,
                  !pauseDesired, !pauseConfirmed, !pathPlaybackActive,
                  sender.heldButtons.isEmpty else { return false }
            let command = ReceiverCheckpointCommand.capture(sessionID: controlSessionID)
            pendingCheckpointCommands[command.commandID] = PendingCheckpointCommand { acknowledgement in
                DispatchQueue.main.async {
                    guard let acknowledgement else {
                        completion(nil, "Start-position capture timed out")
                        return
                    }
                    guard acknowledgement.accepted,
                          let checkpoint = acknowledgement.checkpoint,
                          checkpoint.hasFiniteCoordinates else {
                        completion(nil, acknowledgement.failure ?? "Start-position capture failed")
                        return
                    }
                    completion(checkpoint, nil)
                }
            }
            sendControlLocked(command)
            expireCheckpointCommandLocked(
                command.commandID,
                after: Self.checkpointCaptureTimeout
            )
            return true
        }
    }

    @discardableResult
    func restorePlayerCheckpoint(
        _ checkpoint: RecordedGameCheckpoint,
        completion: @escaping (Bool, String?) -> Void
    ) -> Bool {
        guard checkpoint.hasFiniteCoordinates else { return false }
        return stateQueue.sync {
            guard started, transportReady, playerCheckpointCapabilityAvailable,
                  let controlSessionID, pendingCheckpointCommands.isEmpty,
                  !pauseDesired, !pauseConfirmed, !pathPlaybackActive else { return false }
            awaitingFreshPress = true
            physicalKeyReconciler.reset()
            sendLocked(sender.releaseAll())
            let command = ReceiverCheckpointCommand.restore(
                sessionID: controlSessionID,
                checkpoint: checkpoint
            )
            pendingCheckpointCommands[command.commandID] = PendingCheckpointCommand { acknowledgement in
                DispatchQueue.main.async {
                    guard let acknowledgement else {
                        completion(false, "Start-position restore timed out")
                        return
                    }
                    completion(
                        acknowledgement.accepted,
                        acknowledgement.accepted ? nil
                            : (acknowledgement.failure ?? "Start-position restore failed")
                    )
                }
            }
            sendControlLocked(command)
            expireCheckpointCommandLocked(
                command.commandID,
                after: Self.checkpointRestoreTimeout
            )
            return true
        }
    }

    @discardableResult
    func performPlayerOps(
        _ makeCommand: (UUID) -> ReceiverPlayerOpsCommand,
        completion: @escaping (ReceiverPlayerOpsAcknowledgement?) -> Void
    ) -> Bool {
        stateQueue.sync {
            guard started, transportReady, playerOpsCapabilityAvailable,
                  let controlSessionID, pendingPlayerOpsCommands.isEmpty,
                  !pathPlaybackActive else { return false }
            let command = makeCommand(controlSessionID)
            pendingPlayerOpsCommands[command.commandID] = PendingPlayerOpsCommand(
                completion: completion
            )
            sendControlLocked(command)
            expirePlayerOpsCommandLocked(command.commandID)
            return true
        }
    }

    @discardableResult
    func forwardPointer(
        _ pointerEvent: GamePointerEvent
    ) -> Bool {
        stateQueue.sync {
            guard started, transportReady, pointerCapabilityAvailable,
                  let controlSessionID else {
                if pointerEvent.kind == .leftDown {
                    pointerLog.warning(
                        "pointer rejected started=\(self.started, privacy: .public) transport=\(self.transportReady, privacy: .public) capability=\(self.pointerCapabilityAvailable, privacy: .public)"
                    )
                }
                return false
            }
            let command = ReceiverPointerCommand(
                sessionID: controlSessionID,
                sequence: nextPointerSequence,
                event: pointerEvent
            )
            nextPointerSequence &+= 1
            sendControlLocked(command)
            if pointerEvent.kind == .leftDown {
                pointerLog.notice("pointer click sent x=\(pointerEvent.normalizedPoint.x, privacy: .public) y=\(pointerEvent.normalizedPoint.y, privacy: .public)")
            }
            return true
        }
    }

    /// Sends the same submit button used by a physical Z press through the
    /// in-game receiver. No application activation or macOS event injection is
    /// involved, so menu automation cannot move or capture the pointer.
    @discardableResult
    func tapMenuSelect() -> Bool {
        stateQueue.sync {
            guard started, transportReady, !automatedTapInFlight,
                  !pathPlaybackActive,
                  pendingCheckpointCommands.isEmpty,
                  !pauseDesired, !pauseConfirmed, sender.heldButtons.isEmpty else { return false }
            automatedTapInFlight = true
            let press: InputBridgeSnapshot
            if awaitingFreshPress || !sender.isEnabled {
                awaitingFreshPress = false
                press = sender.beginFreshPress(.actionZ)
            } else if let changed = sender.setHeld(.actionZ, isHeld: true) {
                press = changed
            } else {
                automatedTapInFlight = false
                return false
            }
            sendLocked(press)
            stateQueue.asyncAfter(deadline: .now() + .milliseconds(85)) { [weak self] in
                guard let self else { return }
                if let release = self.sender.setHeld(.actionZ, isHeld: false) {
                    self.sendLocked(release)
                }
                self.automatedTapInFlight = false
            }
            return true
        }
    }

    /// Development-only input used by an explicitly enabled local automation
    /// channel. The bridge owns the hold and its heartbeats, so macOS synthetic
    /// key timing and application focus cannot truncate a camera-look test.
    @discardableResult
    func pulseAutomationButton(
        _ button: GameAutomationCommand.Button,
        duration: TimeInterval
    ) -> Bool {
        stateQueue.sync {
            guard started, transportReady,
                  !pathPlaybackActive,
                  pendingCheckpointCommands.isEmpty,
                  !pauseDesired, !pauseConfirmed,
                  let bridgeButton = Self.bridgeButton(for: button)
            else { return false }
            automationPulseGeneration &+= 1
            let generation = automationPulseGeneration
            automationPulseActive = true
            awaitingFreshPress = false
            physicalKeyReconciler.reset()
            sendLocked(sender.beginFreshPress(bridgeButton))
            if groundTraceEnabled {
                let now = ProcessInfo.processInfo.systemUptime
                groundTraceLog.info("input t=\(now, privacy: .public) event=press command=\(button.rawValue, privacy: .public) generation=\(generation, privacy: .public) duration=\(duration, privacy: .public)")
            }
            DispatchQueue.main.async { [weak self] in self?.activityHandler() }
            continueAutomationPulseLocked(
                generation: generation,
                deadline: ProcessInfo.processInfo.systemUptime + duration
            )
            return true
        }
    }

    /// Includes physical and recorded-path input because both flows update the
    /// same bridge-owned held-button state before a frame is captured.
    func currentHorizontalRoomDirection() -> VisualRoomDirection? {
        stateQueue.sync {
            let left = sender.heldButtons.contains(.left)
            let right = sender.heldButtons.contains(.right)
            guard left != right else { return nil }
            return left ? .left : .right
        }
    }

    @discardableResult
    func playInputPath(
        _ path: RecordedInputPath,
        completion: @escaping (Bool) -> Void
    ) -> Bool {
        guard Self.isValidPlaybackPath(path) else { return false }
        let poseTrace = path.groundTruthTrace.filter {
            $0.sample.heroAvailable && $0.sample.hasFiniteCoordinates
                && !$0.sample.sceneName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
        return stateQueue.sync {
            guard started, transportReady,
                  !pauseDesired, !pauseConfirmed,
                  !automationPulseActive, !pathPlaybackActive,
                  sender.heldButtons.isEmpty,
                  poseTrace.isEmpty || playerPosePlaybackCapabilityAvailable
            else { return false }

            pathPlaybackGeneration &+= 1
            let generation = pathPlaybackGeneration
            pathPlaybackActive = true
            pathPlaybackCompletion = completion
            physicalKeyReconciler.reset()
            let start = DispatchTime.now()
            for event in path.events {
                stateQueue.asyncAfter(deadline: start + event.offset) { [weak self] in
                    self?.applyPlaybackEventLocked(event, generation: generation)
                }
            }
            for pose in poseTrace {
                stateQueue.asyncAfter(deadline: start + pose.offset) { [weak self] in
                    self?.applyPlaybackPoseLocked(pose.sample, generation: generation)
                }
            }
            stateQueue.asyncAfter(deadline: start + path.duration) { [weak self] in
                self?.finishPathPlaybackLocked(generation: generation, completed: true)
            }
            return true
        }
    }

    /// Ends one controlled replay without restoring its checkpoint. Scheduled
    /// input and pose events become inert because playback ownership is cleared
    /// before every held button is released.
    func cancelPathPlayback() {
        stateQueue.async { [weak self] in
            guard let self, self.pathPlaybackActive else { return }
            self.finishPathPlaybackLocked(
                generation: self.pathPlaybackGeneration,
                completed: false
            )
        }
    }

    private func continueAutomationPulseLocked(
        generation: UInt64,
        deadline: TimeInterval
    ) {
        stateQueue.asyncAfter(deadline: .now() + .milliseconds(50)) { [weak self] in
            guard let self,
                  self.automationPulseActive,
                  self.automationPulseGeneration == generation
            else { return }
            if ProcessInfo.processInfo.systemUptime >= deadline {
                self.automationPulseActive = false
                self.awaitingFreshPress = true
                self.physicalKeyReconciler.reset()
                self.sendLocked(self.sender.releaseAll())
                if self.groundTraceEnabled {
                    let now = ProcessInfo.processInfo.systemUptime
                    self.groundTraceLog.info("input t=\(now, privacy: .public) event=release generation=\(generation, privacy: .public)")
                }
                return
            }
            self.sendLocked(self.sender.heartbeat())
            self.continueAutomationPulseLocked(
                generation: generation,
                deadline: deadline
            )
        }
    }

    private func installEventMonitor() {
        eventMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self, NSApp.isActive,
                  !Self.firstResponderConsumesTextInput() else { return event }
            let relayState = self.stateQueue.sync {
                (
                    self.activeSupportedKeyCodesLocked,
                    self.pauseDesired || self.pauseConfirmed || self.pathPlaybackActive
                        || !self.pendingCheckpointCommands.isEmpty
                )
            }
            guard GameKeyRelay.action(
                type: event.type,
                keyCode: event.keyCode,
                modifierFlags: event.modifierFlags,
                supportedKeyCodes: relayState.0,
                inputSuppressed: relayState.1
            ) == .forward else { return event }
            self.handleLocalKey(event)
            return nil
        }
    }

    private func installFocusObservers() {
        let center = NotificationCenter.default
        notificationObservers.append(center.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in self?.releaseForFocusLoss() })
        notificationObservers.append(center.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in self?.requireFreshPresses() })
    }

    private func startInputSampler() {
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + Self.samplingInterval, repeating: Self.samplingInterval)
        timer.setEventHandler { [weak self] in self?.sampleInputState() }
        timer.resume()
        inputSampler = timer
    }

    private func startPauseLeaseTimer() {
        let timer = DispatchSource.makeTimerSource(queue: stateQueue)
        timer.schedule(
            deadline: .now() + Self.pauseRenewalInterval,
            repeating: Self.pauseRenewalInterval
        )
        timer.setEventHandler { [weak self] in
            guard let self, self.pauseDesired, self.pauseCapabilityAvailable else { return }
            self.sendPauseLocked()
        }
        timer.resume()
        pauseLeaseTimer = timer
    }

    private func handleLocalKey(_ event: NSEvent) {
        guard let button = Self.button(for: event.keyCode) else { return }
        stateQueue.async { [weak self] in
            guard let self, self.transportReady,
                  !self.pauseDesired, !self.pauseConfirmed,
                  self.pendingCheckpointCommands.isEmpty else { return }
            if event.type == .keyDown {
                guard !event.isARepeat else { return }
                if self.awaitingFreshPress {
                    self.awaitingFreshPress = false
                    self.sendLocked(self.sender.beginFreshPress(button))
                    self.publishPhysicalInputLocked(button, isPressed: true)
                    DispatchQueue.main.async { self.activityHandler() }
                } else if let snapshot = self.sender.setHeld(button, isHeld: true) {
                    self.sendLocked(snapshot)
                    self.publishPhysicalInputLocked(button, isPressed: true)
                    DispatchQueue.main.async { self.activityHandler() }
                }
            } else if !self.awaitingFreshPress, let snapshot = self.sender.setHeld(button, isHeld: false) {
                self.physicalKeyReconciler.noteLocalRelease(event.keyCode)
                self.sendLocked(snapshot)
                self.publishPhysicalInputLocked(button, isPressed: false)
            }
        }
    }

    /// This timer runs on the main queue. A frozen Vision UI emits no heartbeat,
    /// so the receiver watchdog releases all controls after 250 ms.
    private func sampleInputState() {
        let applicationActive = NSApp.isActive
        guard applicationActive || backgroundPathPlayback else { return }
        stateQueue.async { [weak self] in
            guard let self, self.transportReady, self.sender.isEnabled,
                  !self.pauseDesired, !self.pauseConfirmed else { return }
            if self.pathPlaybackActive {
                self.sendLocked(self.sender.heartbeat())
                return
            }
            // Background permission belongs only to explicitly scheduled paths.
            // Physical keys still require focus and a fresh press; UI stalls
            // still stop this main-queue heartbeat and trigger the watchdog.
            guard applicationActive, !self.awaitingFreshPress else { return }
            let heldKeyCodes = Set(self.activeSupportedKeyCodesLocked.filter { keyCode in
                Self.button(for: keyCode).map(self.sender.heldButtons.contains) ?? false
            })
            let releasedKeyCodes = self.physicalKeyReconciler.releasedKeyCodes(
                from: heldKeyCodes,
                isPhysicallyHeld: { keyCode in
                    CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(keyCode))
                }
            )
            for keyCode in releasedKeyCodes {
                guard let button = Self.button(for: keyCode),
                      let release = self.sender.setHeld(button, isHeld: false) else { continue }
                self.sendLocked(release)
                self.publishPhysicalInputLocked(button, isPressed: false)
            }
            self.sendLocked(self.sender.heartbeat())
        }
    }

    private func releaseForFocusLoss() {
        stateQueue.async { [weak self] in
            guard let self, !self.automationPulseActive else { return }
            if self.pathPlaybackActive {
                if self.backgroundPathPlayback {
                    self.playbackLog.notice("playback continuing after focus loss (automation option)")
                    return
                }
                self.playbackLog.notice("playback interrupted reason=focus-loss")
            }
            self.releaseAllLocked()
        }
    }
    private func requireFreshPresses() { stateQueue.async { [weak self] in self?.awaitingFreshPress = true } }

    private func releaseAllLocked() {
        let heldButtons = sender.heldButtons
        let playbackCompletion = pathPlaybackActive ? pathPlaybackCompletion : nil
        pathPlaybackGeneration &+= 1
        pathPlaybackActive = false
        pathPlaybackCompletion = nil
        automationPulseGeneration &+= 1
        automationPulseActive = false
        awaitingFreshPress = true
        automatedTapInFlight = false
        physicalKeyReconciler.reset()
        if transportReady { sendLocked(sender.releaseAll()) } else { _ = sender.releaseAll() }
        publishPhysicalReleasesLocked(heldButtons)
        if let playbackCompletion {
            DispatchQueue.main.async { playbackCompletion(false) }
        }
    }

    private func connect() {
        guard started, connection == nil else { return }
        let newConnection = NWConnection(host: Self.receiverHost, port: Self.receiverPort, using: .tcp)
        connection = newConnection
        newConnection.stateUpdateHandler = { [weak self, weak newConnection] state in
            guard let self else { return }
            self.stateQueue.async {
                guard self.connection === newConnection else { return }
                switch state {
                case .ready:
                    self.transportReady = true
                    self.awaitingFreshPress = true
                    self.receiveBuffer.removeAll(keepingCapacity: true)
                    let session = self.sender.beginNewSession()
                    self.controlSessionID = session.sessionID
                    self.pauseLeaseMilliseconds = 0
                    self.pauseCapabilityAvailable = false
                    self.menuShortcutsCapabilityAvailable = false
                    self.pointerCapabilityAvailable = false
                    self.playerCheckpointCapabilityAvailable = false
                    self.playerPosePlaybackCapabilityAvailable = false
                    self.groundTruthCapabilityAvailable = false
                    self.playerOpsCapabilityAvailable = false
                    self.lastGroundTruthSequence = nil
                    self.failPendingCheckpointCommandsLocked()
                    self.failPendingPlayerOpsCommandsLocked()
                    self.nextPointerSequence = 0
                    self.nextPlayerPoseSequence = 0
                    self.pauseDesired = false
                    self.pauseConfirmed = false
                    self.acknowledgementTracker.beginSession(session.sessionID)
                    self.sendLocked(session)
                    self.sendControlLocked(ReceiverCapabilityRequest(
                        sessionID: session.sessionID,
                        renderFrameMarker: self.renderFrameMarkerDesired
                    ))
                    self.receiveNextLocked()
                    self.publishConnectionState("Receiver connected · waiting for game")
                    self.publishPauseState(.unavailable)
                case .waiting:
                    // A connection created before the mod starts can remain in
                    // waiting even after the localhost listener appears. Tear
                    // down that attempt and create a fresh one instead of
                    // requiring the user to restart Vision.
                    self.publishConnectionState("Controls unavailable")
                    self.stateQueue.asyncAfter(deadline: .now() + .milliseconds(500)) { [weak self, weak newConnection] in
                        guard let self, let newConnection,
                              self.connection === newConnection,
                              !self.transportReady else { return }
                        self.handleDisconnectedLocked()
                    }
                case .failed, .cancelled:
                    self.handleDisconnectedLocked()
                default: break
                }
            }
        }
        newConnection.start(queue: stateQueue)
    }

    private func receiveNextLocked() {
        guard let connection, transportReady else { return }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, complete, error in
            guard let self else { return }
            self.stateQueue.async {
                guard self.connection === connection else { return }
                if let data { self.receiveBuffer.append(data) }
                self.consumeAcknowledgementsLocked()
                if complete || error != nil { self.handleDisconnectedLocked() } else { self.receiveNextLocked() }
            }
        }
    }

    private func consumeAcknowledgementsLocked() {
        while let lineEnd = receiveBuffer.firstIndex(of: 0x0A) {
            let line = receiveBuffer.prefix(upTo: lineEnd)
            receiveBuffer.removeSubrange(...lineEnd)
            guard let type = try? InputBridgeWireCodec.messageType(line: Data(line)) else { continue }
            switch type {
            case "ack":
                guard let acknowledgement = try? JSONDecoder().decode(InputBridgeAcknowledgement.self, from: line) else { continue }
                consumeInputAcknowledgementLocked(acknowledgement)
            case "capabilitiesAck":
                guard let acknowledgement = try? InputBridgeWireCodec.decodeCapabilitiesAcknowledgement(line: Data(line)) else { continue }
                consumeCapabilitiesAcknowledgementLocked(acknowledgement)
            case "pauseAck", "resumeAck":
                guard let acknowledgement = try? InputBridgeWireCodec.decodePauseAcknowledgement(line: Data(line)) else { continue }
                consumePauseAcknowledgementLocked(acknowledgement)
            case "captureCheckpointAck", "restoreCheckpointAck":
                guard let acknowledgement = try? InputBridgeWireCodec.decodeCheckpointAcknowledgement(line: Data(line)) else { continue }
                consumeCheckpointAcknowledgementLocked(acknowledgement)
            case "playerOpsAck":
                guard let acknowledgement = try? InputBridgeWireCodec
                    .decodePlayerOpsAcknowledgement(line: Data(line)) else { continue }
                consumePlayerOpsAcknowledgementLocked(acknowledgement)
            case "groundTruth":
                guard let sample = try? InputBridgeWireCodec.decodeGroundTruth(line: Data(line))
                else { continue }
                consumeGroundTruthLocked(sample)
            default:
                continue
            }
        }
    }

    private func consumeInputAcknowledgementLocked(_ acknowledgement: InputBridgeAcknowledgement) {
        if case .matched = acknowledgementTracker.receive(acknowledgement, at: ProcessInfo.processInfo.systemUptime) {
                let suffix: String
                if let p95 = acknowledgementTracker.p95Latency {
                    suffix = " · \(Int((p95 * 1_000).rounded())) ms p95"
                } else {
                    suffix = ""
                }
                publishConnectionState("Controls connected\(suffix)")
        }
    }

    private func consumeCapabilitiesAcknowledgementLocked(
        _ acknowledgement: ReceiverCapabilitiesAcknowledgement
    ) {
        guard acknowledgement.version == ReceiverControlProtocol.version,
              acknowledgement.type == "capabilitiesAck",
              acknowledgement.sessionID == controlSessionID else { return }
        menuShortcutsCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.menuShortcutsCapability
        )
        pointerCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.pointerCapability
        )
        playerCheckpointCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.playerCheckpointCapability
        )
        playerPosePlaybackCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.playerPosePlaybackCapability
        )
        groundTruthCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.groundTruthCapability
        )
        playerOpsCapabilityAvailable = acknowledgement.capabilities.contains(
            ReceiverControlProtocol.playerOpsCapability
        )
        pointerLog.notice("receiver pointer capability=\(self.pointerCapabilityAvailable, privacy: .public)")
        guard
              acknowledgement.capabilities.contains(ReceiverControlProtocol.pauseCapability),
              acknowledgement.pauseLeaseMilliseconds > 0 else { return }
        pauseLeaseMilliseconds = acknowledgement.pauseLeaseMilliseconds
        pauseCapabilityAvailable = true
        publishPauseState(.ready)
    }

    private func consumePauseAcknowledgementLocked(_ acknowledgement: ReceiverPauseAcknowledgement) {
        guard acknowledgement.version == ReceiverControlProtocol.version,
              acknowledgement.sessionID == controlSessionID else { return }
        switch acknowledgement.type {
        case "pauseAck":
            pauseConfirmed = acknowledgement.paused
            if pauseDesired, acknowledgement.paused {
                publishPauseState(.paused)
            } else if !pauseDesired {
                publishPauseState(.resuming)
                sendResumeLocked()
            } else {
                pauseDesired = false
                publishPauseState(.ready)
            }
        case "resumeAck":
            pauseConfirmed = acknowledgement.paused
            if pauseDesired {
                publishPauseState(.pausing)
                sendPauseLocked()
            } else if acknowledgement.paused {
                publishPauseState(.resuming)
                sendResumeLocked()
            } else {
                publishPauseState(.ready)
            }
        default:
            break
        }
    }

    private func consumeCheckpointAcknowledgementLocked(
        _ acknowledgement: ReceiverCheckpointAcknowledgement
    ) {
        guard acknowledgement.version == ReceiverControlProtocol.version,
              acknowledgement.sessionID == controlSessionID,
              acknowledgement.type == "captureCheckpointAck"
                || acknowledgement.type == "restoreCheckpointAck",
              let pending = pendingCheckpointCommands.removeValue(
                forKey: acknowledgement.commandID
              ) else { return }
        pending.completion(acknowledgement)
    }

    private func consumeGroundTruthLocked(_ sample: ReceiverGroundTruthSample) {
        guard groundTruthCapabilityAvailable,
              sample.version == ReceiverControlProtocol.version,
              sample.type == "groundTruth",
              sample.sessionID == controlSessionID,
              sample.hasFiniteCoordinates,
              lastGroundTruthSequence.map({ sample.sequence > $0 }) ?? true
        else { return }
        lastGroundTruthSequence = sample.sequence
        groundTruthHandler(sample, ProcessInfo.processInfo.systemUptime)
    }

    private func consumePlayerOpsAcknowledgementLocked(
        _ acknowledgement: ReceiverPlayerOpsAcknowledgement
    ) {
        guard acknowledgement.version == ReceiverControlProtocol.version,
              acknowledgement.type == "playerOpsAck",
              acknowledgement.sessionID == controlSessionID,
              let pending = pendingPlayerOpsCommands.removeValue(
                forKey: acknowledgement.commandID
              ) else { return }
        pending.completion(acknowledgement)
    }

    private func expireCheckpointCommandLocked(
        _ commandID: UUID,
        after timeout: TimeInterval
    ) {
        stateQueue.asyncAfter(deadline: .now() + timeout) { [weak self] in
            guard let self,
                  let pending = self.pendingCheckpointCommands.removeValue(forKey: commandID)
            else { return }
            pending.completion(nil)
        }
    }

    private func failPendingCheckpointCommandsLocked() {
        let pending = pendingCheckpointCommands.values
        pendingCheckpointCommands.removeAll(keepingCapacity: false)
        pending.forEach { $0.completion(nil) }
    }

    private func expirePlayerOpsCommandLocked(_ commandID: UUID) {
        stateQueue.asyncAfter(deadline: .now() + .seconds(3)) { [weak self] in
            guard let self,
                  let pending = self.pendingPlayerOpsCommands.removeValue(forKey: commandID)
            else { return }
            pending.completion(nil)
        }
    }

    private func failPendingPlayerOpsCommandsLocked() {
        let pending = pendingPlayerOpsCommands.values
        pendingPlayerOpsCommands.removeAll(keepingCapacity: false)
        pending.forEach { $0.completion(nil) }
    }

    private func sendLocked(_ snapshot: InputBridgeSnapshot) {
        guard transportReady, let connection, let data = try? InputBridgeWireCodec.encode(snapshot) else { return }
        _ = acknowledgementTracker.recordSent(snapshot, at: ProcessInfo.processInfo.systemUptime)
        connection.send(content: data, completion: .contentProcessed { [weak self, weak connection] error in
            guard error != nil, let self else { return }
            self.stateQueue.async {
                guard self.connection === connection else { return }
                self.handleDisconnectedLocked()
            }
        })
    }

    private func sendPauseLocked() {
        guard let controlSessionID, pauseCapabilityAvailable,
              pauseLeaseMilliseconds > 0 else { return }
        sendControlLocked(ReceiverPauseCommand.pause(
            sessionID: controlSessionID,
            leaseMilliseconds: pauseLeaseMilliseconds
        ))
    }

    private func sendResumeLocked() {
        guard let controlSessionID, transportReady else { return }
        sendControlLocked(ReceiverPauseCommand.resume(sessionID: controlSessionID))
    }

    private func sendControlLocked<T: Encodable>(_ message: T) {
        guard transportReady, let connection else { return }
        let data: Data?
        if let request = message as? ReceiverCapabilityRequest {
            data = try? InputBridgeWireCodec.encode(request)
        } else if let command = message as? ReceiverPauseCommand {
            data = try? InputBridgeWireCodec.encode(command)
        } else if let command = message as? ReceiverPointerCommand {
            data = try? InputBridgeWireCodec.encode(command)
        } else if let command = message as? ReceiverCheckpointCommand {
            data = try? InputBridgeWireCodec.encode(command)
        } else if let command = message as? ReceiverPlayerPoseCommand {
            data = try? InputBridgeWireCodec.encode(command)
        } else if let command = message as? ReceiverPlayerOpsCommand {
            data = try? InputBridgeWireCodec.encode(command)
        } else {
            data = nil
        }
        guard let data else { return }
        connection.send(content: data, completion: .contentProcessed { [weak self, weak connection] error in
            guard error != nil, let self else { return }
            self.stateQueue.async {
                guard self.connection === connection else { return }
                self.handleDisconnectedLocked()
            }
        })
    }

    private func handleDisconnectedLocked() {
        guard connection != nil else { return }
        if pathPlaybackActive { playbackLog.notice("playback interrupted reason=receiver-disconnected") }
        let heldButtons = sender.heldButtons
        let playbackCompletion = pathPlaybackActive ? pathPlaybackCompletion : nil
        pathPlaybackGeneration &+= 1
        pathPlaybackActive = false
        pathPlaybackCompletion = nil
        failPendingCheckpointCommandsLocked()
        failPendingPlayerOpsCommandsLocked()
        transportReady = false
        connection?.cancel()
        connection = nil
        receiveBuffer.removeAll(keepingCapacity: false)
        acknowledgementTracker = InputAcknowledgementTracker()
        _ = sender.releaseAll()
        publishPhysicalReleasesLocked(heldButtons)
        if let playbackCompletion {
            DispatchQueue.main.async { playbackCompletion(false) }
        }
        physicalKeyReconciler.reset()
        awaitingFreshPress = true
        automatedTapInFlight = false
        clearPauseStateLocked()
        publishConnectionState("Controls unavailable")
        guard started else { return }
        stateQueue.asyncAfter(deadline: .now() + .milliseconds(500)) { [weak self] in self?.connect() }
    }

    private func publishConnectionState(_ state: String) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.lastPublishedConnectionState != state else { return }
            self.lastPublishedConnectionState = state
            self.connectionStateHandler(state)
        }
    }

    private func clearPauseStateLocked() {
        controlSessionID = nil
        pauseLeaseMilliseconds = 0
        pauseCapabilityAvailable = false
        menuShortcutsCapabilityAvailable = false
        pointerCapabilityAvailable = false
        playerCheckpointCapabilityAvailable = false
        playerPosePlaybackCapabilityAvailable = false
        groundTruthCapabilityAvailable = false
        playerOpsCapabilityAvailable = false
        lastGroundTruthSequence = nil
        failPendingCheckpointCommandsLocked()
        failPendingPlayerOpsCommandsLocked()
        nextPointerSequence = 0
        nextPlayerPoseSequence = 0
        pauseDesired = false
        pauseConfirmed = false
        publishPauseState(.unavailable)
    }

    private func publishPauseState(_ state: GamePauseState) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.lastPublishedPauseState != state else { return }
            self.lastPublishedPauseState = state
            self.pauseStateHandler(state)
        }
    }

    private static func button(for keyCode: UInt16) -> InputBridgeButtons? {
        switch keyCode {
        case 123: return .left; case 124: return .right; case 125: return .down; case 126: return .up
        case 0: return .actionA; case 6: return .actionZ; case 7: return .actionX
        case 34: return .inventory; case 35: return .pauseMenu
        default: return nil
        }
    }

    private static func bridgeButton(
        for button: GameAutomationCommand.Button
    ) -> InputBridgeButtons? {
        switch button {
        case .left: .left
        case .right: .right
        case .down: .down
        case .up: .up
        case .a: .actionA
        case .z: .actionZ
        case .x: .actionX
        case .inventory: .inventory
        case .pause: .pauseMenu
        }
    }

    private func publishPhysicalInputLocked(
        _ button: InputBridgeButtons,
        isPressed: Bool
    ) {
        guard let recorded = RecordedGameButton.from(button) else { return }
        physicalInputHandler(recorded, isPressed, ProcessInfo.processInfo.systemUptime)
    }

    private func publishPhysicalReleasesLocked(_ heldButtons: InputBridgeButtons) {
        for button in RecordedGameButton.allCases where heldButtons.contains(button.bridgeButton) {
            physicalInputHandler(button, false, ProcessInfo.processInfo.systemUptime)
        }
    }

    private func applyPlaybackEventLocked(
        _ event: RecordedInputPathEvent,
        generation: UInt64
    ) {
        guard pathPlaybackActive, pathPlaybackGeneration == generation else { return }
        let bridgeButton = event.button.bridgeButton
        let snapshot: InputBridgeSnapshot?
        switch event.transition {
        case .pressed:
            if awaitingFreshPress || !sender.isEnabled {
                awaitingFreshPress = false
                snapshot = sender.beginFreshPress(bridgeButton)
            } else {
                snapshot = sender.setHeld(bridgeButton, isHeld: true)
            }
        case .released:
            snapshot = sender.setHeld(bridgeButton, isHeld: false)
        }
        guard let snapshot else { return }
        sendLocked(snapshot)
        physicalInputHandler(
            event.button,
            event.transition == .pressed,
            ProcessInfo.processInfo.systemUptime
        )
    }

    private func applyPlaybackPoseLocked(
        _ sample: ReceiverGroundTruthSample,
        generation: UInt64
    ) {
        guard pathPlaybackActive, pathPlaybackGeneration == generation,
              playerPosePlaybackCapabilityAvailable,
              let controlSessionID else { return }
        let sequence = nextPlayerPoseSequence
        nextPlayerPoseSequence &+= 1
        sendControlLocked(ReceiverPlayerPoseCommand(
            sessionID: controlSessionID,
            sequence: sequence,
            sample: sample
        ))
    }

    private func finishPathPlaybackLocked(generation: UInt64, completed: Bool) {
        guard pathPlaybackActive, pathPlaybackGeneration == generation else { return }
        let completion = pathPlaybackCompletion
        let heldButtons = sender.heldButtons
        pathPlaybackActive = false
        pathPlaybackCompletion = nil
        awaitingFreshPress = true
        physicalKeyReconciler.reset()
        sendLocked(sender.releaseAll())
        publishPhysicalReleasesLocked(heldButtons)
        if let completion {
            DispatchQueue.main.async { completion(completed) }
        }
    }

    static func isValidPlaybackPath(_ path: RecordedInputPath) -> Bool {
        guard path.duration.isFinite, (0.05...900).contains(path.duration),
              path.events.count <= 10_000,
              path.groundTruthTrace.count <= 100_000 else { return false }
        var lastOffset: TimeInterval = 0
        var held = Set<RecordedGameButton>()
        for event in path.events {
            guard event.offset.isFinite,
                  event.offset >= lastOffset,
                  event.offset <= path.duration + 0.000_001 else { return false }
            lastOffset = event.offset
            switch event.transition {
            case .pressed:
                guard held.insert(event.button).inserted else { return false }
            case .released:
                guard held.remove(event.button) != nil else { return false }
            }
        }
        guard held.isEmpty else { return false }
        var lastPoseOffset: TimeInterval = 0
        for pose in path.groundTruthTrace {
            guard pose.offset.isFinite,
                  pose.offset >= lastPoseOffset,
                  pose.offset <= path.duration + 0.000_001,
                  pose.receivedTimestamp.isFinite,
                  pose.sample.hasFiniteCoordinates else { return false }
            lastPoseOffset = pose.offset
        }
        return true
    }

    private var activeSupportedKeyCodesLocked: Set<UInt16> {
        let supported = menuShortcutsCapabilityAvailable
            ? Self.supportedKeyCodes
            : Self.gameplayKeyCodes
        return reviewNavigationActive
            ? supported.subtracting(Self.reviewNavigationKeyCodes)
            : supported
    }

    private static func firstResponderConsumesTextInput() -> Bool {
        guard let responder = NSApp.keyWindow?.firstResponder else { return false }
        return responder is NSTextView || responder is NSTextField
    }

    @discardableResult
    private func ensureGameRunning() -> NSRunningApplication? {
        if let managedApplication, !managedApplication.isTerminated { return managedApplication }
        if let running = Self.hollowKnightApplication() { managedApplication = running; return running }
        guard !launchInFlight else { return nil }
        launchInFlight = true
        // Opening the app bundle directly can stop at Steam's library page
        // without creating the game process. The Steam app ID route performs
        // the launch and loads the installed mod receiver reliably.
        if let steamURL = URL(string: "steam://run/367520") {
            NSWorkspace.shared.open(steamURL)
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
                self?.launchInFlight = false
                self?.managedApplication = Self.hollowKnightApplication()
            }
        } else if let gameURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "unity.Team Cherry.Hollow Knight") {
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = false
            configuration.addsToRecentItems = false
            NSWorkspace.shared.openApplication(at: gameURL, configuration: configuration) { [weak self] application, _ in
                DispatchQueue.main.async { self?.launchInFlight = false; self?.managedApplication = application ?? Self.hollowKnightApplication() }
            }
        } else { launchInFlight = false }
        return nil
    }

    static func hollowKnightApplication() -> NSRunningApplication? {
        NSWorkspace.shared.runningApplications.first { application in
            guard application.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return false }
            let name = application.localizedName?.lowercased() ?? ""
            let bundle = application.bundleIdentifier?.lowercased() ?? ""
            return name == "hollow knight" || (bundle.contains("hollowknight") && !bundle.contains("vision"))
        }
    }
}
