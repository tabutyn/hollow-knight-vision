import Foundation

/// Menu automation uses fresh stencil identities. Gameplay handoff requires a
/// new positive visual recognition, never the status tracker's gameplay latch.
struct BotDemoMenuObservation {
    let context: LabelingContext?
    let selectedIdentifier: String?
    let gameplay: Bool
    let occupiedSlots: Set<Int>
    let frameTimestamp: Double
    let capturedAt: Double
    let availableAt: Double
    let generation: UInt64
    var directionToYes: GameAutomationCommand.Button? = nil
    var menuTimestamp: Double? = nil
    var checkedProfileSlots = Set<Int>()

    static func make(menu: MenuStencilResult?, freshGameplay: Bool, frameTimestamp: Double,
                     capturedAt: Double, availableAt: Double, generation: UInt64) -> Self {
        let menu = menu.flatMap { $0.isMatch && (0...0.3).contains(frameTimestamp - $0.sourceTimestamp) ? $0 : nil }
        let selected = menu.flatMap { menu -> String? in
            guard let name = menu.selectedOption else { return nil }
            return menu.selectorSearchRegions.first(where: { $0.optionName == name })?.optionIdentifier
                ?? menu.context.labels.first(where: { $0.name == name })?.id
                ?? (menu.context == .selectProfile && name.hasPrefix("Clear Save ")
                    ? Int(name.split(separator: " ").last ?? "").flatMap { (1...4).contains($0) ? "select-profile.clear-save-\($0)" : nil }
                    : nil)
        }
        let occupied = Set(menu?.anchors.compactMap { anchor -> Int? in
            guard anchor.classIdentifier == "select-profile.clear-save", anchor.name.hasPrefix("Clear Save "),
                  let index = Int(anchor.name.split(separator: " ").last ?? ""), (1...4).contains(index) else { return nil }
            return index
        } ?? [])
        let yesDirection = menu.flatMap { value -> GameAutomationCommand.Button? in
            guard let yes = value.anchors.first(where: { $0.classIdentifier == LabelingClassIdentity.yes }),
                  let no = value.anchors.first(where: { $0.classIdentifier == LabelingClassIdentity.no }) else { return nil }
            let dx = yes.rect.midX - no.rect.midX, dy = yes.rect.midY - no.rect.midY
            guard dx.isFinite, dy.isFinite, abs(dx) + abs(dy) > 1 else { return nil }
            return abs(dy) >= abs(dx) ? (dy > 0 ? .up : .down) : (dx < 0 ? .left : .right)
        }
        return Self(context: menu?.context, selectedIdentifier: selected, gameplay: freshGameplay,
                    occupiedSlots: occupied, frameTimestamp: frameTimestamp,
                    capturedAt: capturedAt, availableAt: availableAt, generation: generation,
                    directionToYes: yesDirection, menuTimestamp: menu?.sourceTimestamp, checkedProfileSlots: menu?.checkedProfileSlots ?? [])
    }
}

struct BotDemoNavigation {
    enum Phase: Equatable {
        case navigating, archiving, selectingClear, confirmingClear, waitingForEmptyProfile
        case startingNewSave, waitingForProfileExit, waitingForGameplay, waitingForBot, playing, stopped
    }
    enum Effect: Equatable {
        case press(GameAutomationCommand.Button)
        case archiveSlot
        case startBot
        case fail(String)
    }
    static let slot = 4
    private(set) var phase = Phase.stopped
    private(set) var archiveVerified = false
    private var startedAt = 0.0
    private var phaseStartedAt = 0.0
    private var lastFrame = -Double.infinity
    private var generation: UInt64?
    private var stableIdentity = ""
    private var stableSince = 0.0
    private var stableCount = 0
    private var nextActionAt = 0.0
    private var expectedContext: LabelingContext?
    private var expectedContextDeadline = 0.0
    private var commandCount = 0
    private struct PendingInput {
        let button: GameAutomationCommand.Button
        let context: LabelingContext?
        let selected: String?
        let gameplay: Bool
        var sentAt: Double
        var retries = 0
    }
    private var pendingInput: PendingInput?
    private var currentObservation: BotDemoMenuObservation?
    private var newSaveAttempts = 0
    private var lastNewSaveSelectAt = 0.0
    private var nextIntroButton = GameAutomationCommand.Button.x
    private var gameplayHUDSince: Double?

    var status: String {
        switch phase {
        case .navigating: "Finding Select Profile"
        case .archiving: "Archiving slot 4"
        case .selectingClear, .confirmingClear: "Resetting archived slot 4"
        case .waitingForEmptyProfile: "Waiting for empty slot 4"
        case .startingNewSave, .waitingForProfileExit: "Starting a new save in slot 4"
        case .waitingForGameplay: "Skipping opening cutscene"
        case .waitingForBot: "Preparing Hacker and bot"
        case .playing: "Bot playing"
        case .stopped: "Bot stopped"
        }
    }

    mutating func begin(at now: Double) {
        self = Self()
        startedAt = now; phaseStartedAt = now; phase = .navigating
    }
    mutating func beginGameplay(at now: Double) {
        begin(at: now)
        transition(to: .waitingForBot, at: now)
    }
    mutating func stop() { phase = .stopped }
    mutating func botStarted(at now: Double) { transition(to: .playing, at: now) }

    mutating func archived(hasActiveSave: Bool, at now: Double) {
        guard phase == .archiving else { return }
        archiveVerified = true
        transition(to: hasActiveSave ? .selectingClear : .startingNewSave, at: now)
    }

    mutating func checkTimeout(at now: Double) -> Effect? {
        guard phase != .stopped, phase != .playing else { return nil }
        let limit: Double = phase == .waitingForGameplay ? 300 : phase == .waitingForBot ? 20 : 90
        if now - phaseStartedAt > limit || (phase != .waitingForGameplay && now - startedAt > 420) {
            return fail("Bot timed out: \(status). Check the game's current screen and its stencils.")
        }
        return nil
    }

    mutating func observe(_ observation: BotDemoMenuObservation, hasActiveSave: Bool,
                          now: Double) -> Effect? {
        if let timeout = checkTimeout(at: now) { return timeout }
        // A held menu match is one menu observation. During the opening, every
        // fresh capture can show the HUD or advance the skip cadence.
        let startingGame = phase == .waitingForProfileExit || phase == .waitingForGameplay
        let frameTime = startingGame ? observation.frameTimestamp : (observation.menuTimestamp ?? observation.frameTimestamp)
        guard phase != .stopped, phase != .playing, phase != .archiving, phase != .waitingForBot,
              observation.capturedAt >= startedAt,
              (0...0.5).contains(now - observation.capturedAt),
              (0...0.3).contains(now - observation.availableAt),
              frameTime.isFinite, generation != observation.generation || frameTime > lastFrame else { return nil }
        if generation != observation.generation { stableIdentity = ""; stableCount = 0; gameplayHUDSince = nil }
        generation = observation.generation; lastFrame = frameTime
        if startingGame {
            guard observation.capturedAt >= phaseStartedAt else { return nil }
            if phase == .waitingForProfileExit, observation.gameplay || observation.context != .selectProfile {
                guard observation.gameplay || now >= nextActionAt else { return nil }
                transition(to: .waitingForGameplay, at: now)
            }
            if phase == .waitingForGameplay {
                return observeOpening(observation, at: now)
            }
        }
        let identity = "\(String(describing: observation.context))/\(observation.selectedIdentifier ?? "?")/\(observation.gameplay)/\(observation.occupiedSlots.sorted())/\(observation.directionToYes?.rawValue ?? "?")"
        if identity != stableIdentity {
            stableIdentity = identity; stableSince = now; stableCount = 0
        }
        stableCount += 1
        guard stableCount >= 2, now - stableSince >= 0.25, now >= nextActionAt else { return nil }
        currentObservation = observation
        if let expectedContext {
            if observation.context == expectedContext {
                self.expectedContext = nil; pendingInput = nil
            } else if var pending = pendingInput, pending.retries < 3, now < expectedContextDeadline,
                      observation.context == pending.context, observation.selectedIdentifier == pending.selected,
                      observation.gameplay == pending.gameplay, now - pending.sentAt >= 1.5 {
                // A recognized menu can still be fading in and ignore the
                // first key. Retry only when the exact originating selection
                // remains visible, never blindly through an unknown screen.
                pending.retries += 1; pending.sentAt = now; pendingInput = pending
                nextActionAt = now + 0.65; stableIdentity = ""; stableCount = 0
                commandCount += 1
                return .press(pending.button)
            } else if now > expectedContextDeadline {
                return fail("Bot did not recognize \(expectedContext.rawValue) after its menu command.")
            } else { return nil }
        }
        let context = observation.context
        let selected = observation.selectedIdentifier
        switch phase {
        case .navigating:
            if observation.gameplay { return press(.pause, expecting: .pause, at: now) }
            switch context {
            case .mainTitle:
                return selected.map { $0 == "main-title.start-game"
                    ? press(.z, expecting: .selectProfile, at: now) : press(.up, at: now) }
            case .selectProfile:
                guard selected == "select-profile.slot-4" else { return navigateToSlot(selected, at: now) }
                transition(to: .archiving, at: now)
                return .archiveSlot
            case .pause:
                return selected.map { $0 == LabelingClassIdentity.quitToMenu
                    ? press(.z, expecting: .quitToMenu, at: now) : press(.down, at: now) }
            case .quitToMenu:
                if selected == LabelingClassIdentity.yes { return press(.z, expecting: .mainTitle, at: now) }
                return observation.directionToYes.map { press($0, at: now) }
            case .clearSave, .quitGame:
                // Unrelated destructive/exit dialogs are always cancelled.
                return press(.a, at: now)
            case .inventory:
                return press(.inventory, at: now)
            case .options, .gameOptions, .audio, .video, .screenScale, .brightness,
                 .videoAdvancedSettings, .controller, .remapController, .controllerAdvancedSettings,
                 .keyboard, .mods, .achievements, .extras, .credits, .hiddenDreams,
                 .grimmTroupe, .lifeblood, .godmaster:
                return press(.a, at: now)
            default: return nil
            }
        case .selectingClear:
            guard archiveVerified, context == .selectProfile else { return nil }
            if selected == "select-profile.clear-save-4" {
                transition(to: .confirmingClear, at: now)
                return press(.z, expecting: .clearSave, at: now)
            }
            if selected == "select-profile.slot-4" {
                guard observation.occupiedSlots.contains(Self.slot), hasActiveSave else {
                    return fail("Slot 4's save file and Clear Save stencil disagree. Bot stopped before clearing.")
                }
                return press(.right, at: now)
            }
            return navigateToSlot(selected, at: now)
        case .confirmingClear:
            guard archiveVerified, context == .clearSave, let selected else { return nil }
            if selected == LabelingClassIdentity.yes {
                transition(to: .waitingForEmptyProfile, at: now)
                return press(.z, expecting: .selectProfile, at: now)
            }
            return observation.directionToYes.map { press($0, at: now) }
        case .waitingForEmptyProfile:
            guard context == .selectProfile, !hasActiveSave,
                  !observation.occupiedSlots.contains(Self.slot) else { return nil }
            transition(to: .startingNewSave, at: now)
            return nil
        case .startingNewSave:
            guard archiveVerified, context == .selectProfile else { return nil }
            guard !hasActiveSave, !observation.occupiedSlots.contains(Self.slot) else {
                return fail("Slot 4 is occupied; Demo will not load it as a new game.")
            }
            guard selected == "select-profile.slot-4" else { return navigateToSlot(selected, at: now) }
            transition(to: .waitingForProfileExit, at: now)
            newSaveAttempts = 1; lastNewSaveSelectAt = now
            return press(.z, at: now)
        case .waitingForProfileExit:
            // The game can ignore Select while the profile is fading in.
            guard context == .selectProfile, selected == "select-profile.slot-4",
                  !hasActiveSave, !observation.occupiedSlots.contains(Self.slot),
                  newSaveAttempts < 4, now - lastNewSaveSelectAt >= 2 else { return nil }
            newSaveAttempts += 1; lastNewSaveSelectAt = now
            return press(.z, at: now)
        default: return nil
        }
    }

    private mutating func observeOpening(_ observation: BotDemoMenuObservation, at now: Double) -> Effect? {
        if observation.gameplay {
            guard let since = gameplayHUDSince else {
                gameplayHUDSince = observation.frameTimestamp
                return nil
            }
            guard observation.frameTimestamp - since >= 0.25 else { return nil }
            transition(to: .waitingForBot, at: now)
            return .startBot
        }
        gameplayHUDSince = nil
        guard now >= nextActionAt else { return nil }
        let button = nextIntroButton
        nextIntroButton = button == .x ? .z : .x
        nextActionAt = now + 0.65
        return .press(button)
    }

    private mutating func navigateToSlot(_ selected: String?, at now: Double) -> Effect? {
        guard let selected else { return nil }
        if selected.hasPrefix("select-profile.clear-save-") { return press(.left, at: now) }
        if selected == LabelingClassIdentity.back { return press(.up, at: now) }
        guard selected.hasPrefix("select-profile.slot-"),
              let slot = Int(selected.suffix(1)), (1...4).contains(slot) else { return nil }
        return slot == Self.slot ? nil : press(.down, at: now)
    }
    private mutating func press(_ button: GameAutomationCommand.Button,
                                expecting context: LabelingContext? = nil, at now: Double) -> Effect {
        commandCount += 1
        guard commandCount <= 70 else { return fail("Bot stopped after too many menu navigation attempts.") }
        nextActionAt = now + 0.65
        stableIdentity = ""; stableCount = 0
        expectedContext = context; expectedContextDeadline = now + 15
        if context != nil, let observed = currentObservation,
           phase == .navigating || (phase == .confirmingClear && context == .clearSave) {
            pendingInput = PendingInput(button: button, context: observed.context,
                selected: observed.selectedIdentifier, gameplay: observed.gameplay, sentAt: now)
        } else { pendingInput = nil }
        return .press(button)
    }
    private mutating func transition(to phase: Phase, at now: Double) {
        self.phase = phase; phaseStartedAt = now; stableIdentity = ""; stableCount = 0
        expectedContext = nil; pendingInput = nil
    }
    private mutating func fail(_ reason: String) -> Effect { phase = .stopped; return .fail(reason) }
}
