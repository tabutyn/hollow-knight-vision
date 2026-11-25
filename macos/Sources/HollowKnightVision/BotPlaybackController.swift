import AppKit
import Combine
import Foundation
import OSLog

/// Main-thread policy execution. Native inference is bounded and small; no
/// Python process, network request, or asynchronous model result owns game keys.
final class BotPlaybackController: ObservableObject {
    @Published private(set) var isActive = false
    @Published private(set) var isWaiting = false
    @Published private(set) var isLoading = false
    @Published private(set) var modelName: String?
    @Published private(set) var status = "Loading bot model…"
    @Published private(set) var decisionCount = 0
    @Published private(set) var lastAction = BotAction.neutral
    @Published private(set) var issue: String?
    private(set) var lastDecisionTerrainSource = "none"
    private(set) var lastDecisionHumanTerrainCount = 0
    private(set) var humanTerrainDecisionCount = 0
    private(set) var nonNeutralDecisionCount = 0
    private(set) var hackerKnightDecisionCount = 0
    private(set) var predictedKnightDecisionCount = 0
    private var observationIssueCounts = [String: Int]()
    private var policy: BotNativePolicy?
    private var policies = [BotDomain: BotNativePolicy]()
    private var playbackDirectories = [BotDomain: URL]()
    private(set) var domain = BotDomain.hacker
    var onStarted: ((BotDomain, BotNativePolicy?) -> Bool)?
    var onActionApplied: ((BotAction, Double) -> Void)?
    var onStopped: (() -> Void)?
    private struct Maneuver {
        let choice: BotSkillChoice
        let plan: BotSkillPlan
        let start: Double
        let sight: SkillSight
        var sent = false
        var returnsToMotor = false
        var initialNewCells = 0
    }
    @Published var automaticPoliciesEnabled = true
    private var startingMenuManeuver = false
    private var skillDecisionCount = 0
    private var maneuver: Maneuver?
    var activeRoute: BotRouteTarget? {
        guard case .traverse(let route)? = maneuver?.choice.binding?.target else { return nil }
        return route
    }
    private var learnedSkill: BotLearnedSkill?
    private var starter: BotSkillInputLayout?
    private var starterHistory = BotObservationHistory()
    var onSkillEvent: ((BotSkillPlan, BotSkillChoice, String, String, Double) -> Void)?
    var didInspect: ((SkillSight, Double) -> Void)?
    var skillSight: () -> SkillSight? = { nil }
    var allowsInGameMenu: Bool {
        skillSight()?.inGameMenu == true && (maneuver != nil || startingMenuManeuver)
    }

    /// A response is consumed once, on arrival. It never arms a later key press.
    func dispatch(_ plan: BotSkillPlan, context: String, generation: UInt64, domain: BotDomain) -> String {
        guard automaticPoliciesEnabled else { return "Automatic policies paused." }
        guard !plan.choices.isEmpty else { return "Model abstained." }
        guard let sight = skillSight(), sight.generation == generation, sight.context == context,
              (0...0.45).contains(now() - sight.capturedAt), let choice = plan.choose(sight) else {
            return "Skipped · no eligible option on the fresh screen."
        }
        guard maneuver == nil, !isLoading, hasFocus(), !isActive || self.domain == domain else {
            return "Skipped · playback unavailable or maneuver in progress."
        }
        startingMenuManeuver = sight.inGameMenu
        defer { startingMenuManeuver = false }
        guard canContinue() else { return "Skipped · game controls unavailable." }
        var run = Maneuver(choice: choice, plan: plan, start: now(), sight: sight)
        if let id = controlSessionID, isActive {
            do { try configureSkill(choice, sight: sight) }
            catch { return "Skipped · " + error.localizedDescription }
            // Release the preceding motor action before the new policy owns this lease.
            neutralizeControl(id)
            lastAction = .neutral
            onActionApplied?(.neutral, now())
            lastDecisionValidUntil = 0
            skillDecisionCount = 0
            history.reset()
            isWaiting = true
            run.returnsToMotor = true
            run.initialNewCells = atlasRun.score.newCells
            maneuver = run
            onSkillEvent?(plan, choice, context, "selected", run.start)
            _ = driveSkill(id: id)
        } else {
            start(domain: domain, selection: run)
        }
        guard maneuver != nil else { return "Skipped · " + (issue ?? status) }
        let candidate = choice.candidateID.map { $0 + " · " } ?? ""
        return "Dispatched · " + candidate + choice.policy.rawValue + " / " + choice.control.rawValue
    }

    private var latest: BotObservation?
    private var history = BotObservationHistory()
    private var maskedKnightDeadline: Double?
    private var atlasRun = BotAtlasRun()
    var atlasScore: BotAtlasScore? { atlasRun.score }
    private var controlSessionID: UUID?
    private var sessionStartedAt: Double = 0
    private var lastDecisionValidUntil: Double = 0
    private var loadGeneration = 0
    private var watchdog: Timer?
    private var focusObserver: NSObjectProtocol?
    private var canContinue: () -> Bool = { false }
    private var beginControl: () -> UUID? = { nil }
    private var applyControl: (BotAction, Double, UUID) -> Bool = { _, _, _ in false }
    private var neutralizeControl: (UUID) -> Void = { _ in }
    private var endControl: (UUID) -> Void = { _ in }
    private let now: () -> Double
    private let hasFocus: () -> Bool
    private let log = Logger(subsystem: "com.ballroller.hollow-knight-vision", category: "bot-control")

    init(policy: BotNativePolicy? = nil, loadModel: Bool = true, domain: BotDomain = .hacker,
         now: @escaping () -> Double = { ProcessInfo.processInfo.systemUptime },
         hasFocus: @escaping () -> Bool = { NSApp.isActive && NSApp.keyWindow != nil }) {
        self.now = now; self.hasFocus = hasFocus; self.policy = policy
        if let policy {
            policies[domain] = policy
            modelName = policy.document.modelName; status = "Ready · \(policy.document.modelName)"
        } else if loadModel { reloadModel() }
    }

    func configure(canContinue: @escaping () -> Bool, begin: @escaping () -> UUID?,
                   apply: @escaping (BotAction, Double, UUID) -> Bool,
                   neutralize: @escaping (UUID) -> Void, end: @escaping (UUID) -> Void) {
        self.canContinue = canContinue; beginControl = begin; applyControl = apply
        neutralizeControl = neutralize; endControl = end
    }

    func reloadModel(directory: URL? = nil) {
        stop(reason: "Model changed")
        loadGeneration += 1
        let generation = loadGeneration
        isLoading = true; modelName = nil; policy = nil; policies = [:]; issue = nil
        status = "Loading bot model…"
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            var loaded = [BotDomain: BotNativePolicy](), locations = [BotDomain: URL](), failures = [String]()
            for domain in BotDomain.allCases where directory == nil || domain == .hacker {
                do {
                    let source = try directory ?? BotModelStore(domain: domain).playbackDirectory()
                    loaded[domain] = try BotNativePolicy.load(from: source)
                    if directory == nil { locations[domain] = source }
                }
                catch { failures.append("\(domain.title): \(error.localizedDescription)") }
            }
            DispatchQueue.main.async { [weak self] in
                guard let self, self.loadGeneration == generation else { return }
                self.isLoading = false
                self.playbackDirectories = locations
                self.policies = loaded; self.policy = loaded[.hacker] ?? loaded[.gameplay]
                self.modelName = self.policy?.document.modelName
                self.status = loaded.isEmpty ? "Ready · built-in playing policies" : "Ready · " + BotDomain.allCases.filter { loaded[$0] != nil }.map(\.title).joined(separator: " + ")
                self.issue = failures.isEmpty ? nil : failures.joined(separator: "\n")
            }
        }
    }

    var hasModel: Bool { policy != nil }
    func hasModel(for domain: BotDomain) -> Bool { policies[domain] != nil }
    func loadedModelName(for domain: BotDomain) -> String? { policies[domain]?.document.modelName }
    var perceptionDiagnostic: [String: Any] {
        var result: [String: Any] = ["humanTerrainDecisions": humanTerrainDecisionCount,
            "nonNeutralDecisions": nonNeutralDecisionCount, "hackerKnightDecisions": hackerKnightDecisionCount,
            "predictedKnightDecisions": predictedKnightDecisionCount,
            "actionSource": skillDecisionCount == 0 ? "general-policy" : "skill-plan",
            "policyDecisions": decisionCount, "skillDecisions": skillDecisionCount,
            "issueCounts": observationIssueCounts, "domain": domain.rawValue]
        if let score = atlasScore {
            result["atlasNewCells"] = score.newCells
            result["atlasDamage"] = score.damage
            result["atlasBlueHealthGained"] = score.blueHealthGained
            result["atlasDwellDecisions"] = score.dwellDecisions
            result["heldAttackDecisions"] = score.heldAttackDecisions
            result["atlasReward"] = score.reward
            if let path = score.pathProgress { result["atlasPathProgress"] = path }
        }
        if let latest {
            if let view = latest.atlasView {
                result["atlasView"] = ["left": view.left, "right": view.right, "x": view.camera.x, "y": view.camera.y,
                    "covered": view.coveredCells, "earned": view.earnedCells, "revision": view.revision, "health": view.health ?? -1] as [String: Any]
            }
            result["issues"] = latest.issues
            result["captureAge"] = now() - latest.capturedAt
            result["deliveryAge"] = now() - latest.availableAt
            result["processingAge"] = latest.availableAt - latest.capturedAt
            result["terrainSource"] = latest.terrainSource ?? "detected"
            result["knightImagePosition"] = Array(latest.features.prefix(2))
            result["knightSource"] = latest.knightSource ?? "visual"
            result["enemySource"] = latest.enemySource ?? "visual"
            result["enemyCount"] = (0..<BotObservationContract.enemyCapacity).filter { latest.features[96 + $0 * 6 + 5] > 0 }.count
            if let terrain = latest.screenTerrain {
                result["screenFloorCount"] = terrain.floors.count
                result["screenWallCount"] = terrain.walls.count
                result["policyUsesScreenTerrain"] = (policy?.document.observationSchemaVersion ?? 0) >= BotObservationContract.screenPolicyVersion
            }
            if latest.features.count == BotObservationContract.featureCount {
                result["wallsAnalyzed"] = latest.features[144] == 1
                result["leftWallPresent"] = latest.features[149] == 1
                result["rightWallPresent"] = latest.features[154] == 1
                result["policyUsesWallInputs"] = (policy?.document.observationSchemaVersion ?? 0) >= BotObservationContract.schemaVersion
            }
            if let frameID = latest.detectionFrameID { result["frameID"] = frameID }
            if let b = latest.blockade { result["blockade"] = ["direction": b.mark.direction, "dx": b.dx, "dy": b.dy] as [String: Any] }
            if let confidence = latest.bestKnightConfidence { result["knightConfidence"] = confidence }
            if let prediction = latest.knightPrediction {
                result["knightPredictionAge"] = prediction.age
                result["knightPredictionUncertainty"] = prediction.uncertainty
                result["knightPredictionCameraCompensated"] = prediction.cameraCompensated
                result["knightObservedIssues"] = prediction.observedIssues
            }
        }
        return result
    }
    var hasFreshObservation: Bool {
        guard let latest else { return false }
        let now = now()
        return (latest.isUsable || maskedKnightDeadline.map { now < $0 } == true) && (0...0.25).contains(now - latest.availableAt)
            && now < latest.knightPredictionDeadline
            && (0...BotObservationContract.maximumCaptureAge).contains(now - latest.capturedAt)
    }

    func observe(_ observation: BotObservation) {
        DispatchQueue.main.async { [weak self] in self?.consume(observation) }
    }

    func start(domain: BotDomain = .hacker) { start(domain: domain, selection: nil) }

    private func configureSkill(_ choice: BotSkillChoice?, sight: SkillSight?) throws {
        let schema = policy?.document.observationSchemaVersion ?? BotObservationContract.nearestGeometryPolicyVersion
        var head: BotLearnedSkill?
        if let choice, choice.control == .native, sight?.hasAbilityControls == true {
            head = try BotLearnedSkill.load(domain: domain, skill: choice.policy, schema: schema)
        }
        var layout: BotSkillInputLayout?
        if let choice, choice.control == .native, head == nil || policy == nil {
            layout = try BotSkillInputLayout(skill: choice.policy, schema: max(schema, 14))
        } else if choice == nil && policy == nil {
            layout = try BotSkillInputLayout(skill: .explore, schema: 14)
        }
        learnedSkill = head
        starter = layout
        starterHistory.reset()
    }

    private func start(domain: BotDomain, selection: Maneuver?) {
        guard !isActive, !isLoading, canContinue(), hasFocus() else { return }
        // Gym can publish while Vision is open. Resolve the version before taking
        // control so a running maneuver always finishes on the same policy.
        if let loaded = playbackDirectories[domain] {
            do {
                let selected = try BotModelStore(domain: domain).playbackDirectory()
                if selected != loaded {
                    policies[domain] = try BotNativePolicy.load(from: selected)
                    playbackDirectories[domain] = selected
                }
            } catch {
                issue = "Could not load the selected bot. \(error.localizedDescription)"
                return
            }
        }
        let policy = policies[domain]
        self.domain = domain; self.policy = policy
        modelName = policy?.document.modelName ?? "Built-in playing policies"
        do { try configureSkill(selection?.choice, sight: selection?.sight) }
        catch { issue = error.localizedDescription; return }
        guard let id = beginControl() else { issue = "Game controls are not ready."; return }
        controlSessionID = id; sessionStartedAt = now()
        if let selection {
            maneuver = Maneuver(choice: selection.choice, plan: selection.plan, start: sessionStartedAt, sight: selection.sight)
        }
        skillDecisionCount = 0
        maskedKnightDeadline = nil
        atlasRun = BotAtlasRun()
        if let latest, BotDomain.of(latest) == domain, let view = latest.atlasView,
           (0...0.25).contains(sessionStartedAt - latest.availableAt) {
            atlasRun.observe(latest, view: view, creditProgress: false)
        }
        history.reset(); lastAction = .neutral; lastDecisionValidUntil = 0; decisionCount = 0; issue = nil
        lastDecisionTerrainSource = "none"; lastDecisionHumanTerrainCount = 0
        humanTerrainDecisionCount = 0; nonNeutralDecisionCount = 0; hackerKnightDecisionCount = 0
        predictedKnightDecisionCount = 0; observationIssueCounts = [:]
        isActive = true; isWaiting = true; status = "Bot waiting for a fresh frame"
        guard onStarted?(domain, policy) != false else {
            issue = "Could not start the bot playback evidence recording."
            stop(reason: "Bot playback capture failed"); return
        }
        if let run = maneuver {
            onSkillEvent?(run.plan, run.choice, run.sight.context, "selected", sessionStartedAt)
            _ = driveSkill(id: id)
        }
        guard isActive else { return }
        watchdog = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in self?.checkInputFreshness() }
        if let window = NSApp?.mainWindow {
            focusObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                object: window, queue: .main) { [weak self] _ in self?.stop(reason: "Window focus changed") }
        }
        log.notice("started model=\(self.modelName ?? "unknown", privacy: .public)")
    }

    func stop(reason: String = "Stopped with B") {
        guard isActive || controlSessionID != nil else { return }
        let id = controlSessionID
        if let run = maneuver { onSkillEvent?(run.plan, run.choice, run.sight.context, reason, now()) }
        maneuver = nil
        starter = nil
        learnedSkill = nil
        controlSessionID = nil; isActive = false; isWaiting = false
        history.reset(); lastAction = .neutral
        watchdog?.invalidate(); watchdog = nil
        if let focusObserver { NotificationCenter.default.removeObserver(focusObserver) }
        focusObserver = nil
        if let id { endControl(id) }
        onActionApplied?(.neutral, now()); onStopped?()
        status = reason
        log.notice("stopped reason=\(reason, privacy: .public) decisions=\(self.decisionCount, privacy: .public)")
    }

    func interrupted(sessionID: UUID, reason: String) {
        guard controlSessionID == sessionID else { return }
        stop(reason: reason)
    }

    private func consume(_ received: BotObservation) {
        var observation = received
        if isActive, observation.terrainSource != nil, BotDomain.of(observation) != domain { return }
        if let latest, latest.generation == observation.generation,
           observation.captureTimestamp <= latest.captureTimestamp { return }
        latest = observation
        guard isActive, let id = controlSessionID,
              observation.availableAt >= sessionStartedAt, observation.capturedAt >= sessionStartedAt else { return }
        guard canContinue(), hasFocus() else { stop(reason: "Gameplay controls interrupted"); return }
        var boundWaypoint: BotRoadHint?
        if let maneuver {
            guard maneuver.choice.control == .native else { return }
            if case .traverse(let target) = maneuver.choice.binding?.target,
               observation.generation == maneuver.sight.generation, observation.roomID == maneuver.choice.binding?.room,
               observation.isUsable, (0...BotObservationContract.maximumCaptureAge).contains(now() - observation.capturedAt),
               let position = observation.policyScene?.navigationPosition, position.chart == target.chart {
                if position.feet.distance(target.goal) <= position.arrivalRadius {
                    finishManeuver(target.hint.frontier ? "Explore destination reached" : "Target reached", id: id); return
                }
                if position.feet.distance(target.waypoint) <= position.arrivalRadius {
                    finishManeuver("Waypoint reached", id: id); return
                }
            }
            guard let sight = skillSight(), sight.generation == maneuver.sight.generation,
                  sight.context == maneuver.sight.context, (0...0.45).contains(now() - sight.capturedAt),
                  sight.allows(maneuver.choice) else {
                stop(reason: "Policy context changed"); return
            }
            if let binding = maneuver.choice.binding {
                guard let scene = observation.policyScene, scene.allows(binding) else {
                    finishManeuver("Bound target expired", id: id); return
                }
                if case .traverse(let target) = binding.target {
                    boundWaypoint = scene.routes.first { $0.matches(target) }?.hint
                    observation.roadHint = boundWaypoint
                }
            }
            if now() - maneuver.start >= maneuver.choice.control.duration {
                finishManeuver("Policy window ended", id: id); return
            }
        }
        let now = now()
        maskedKnightDeadline = domain == .gameplay && policy?.document.supportsMaskedKnight == true
            ? BotKnightOcclusionWindow.deadline(for: observation) : nil
        for issue in observation.issues { observationIssueCounts[issue, default: 0] += 1 }
        if lastDecisionValidUntil > 0 && now >= lastDecisionValidUntil {
            waitForObservation(id: id, reason: "Bot waiting for a fresh observation")
        }
        let schema = policy?.document.observationSchemaVersion ?? BotObservationContract.nearestGeometryPolicyVersion
        guard let input = history.input(observation, previousAction: lastAction, now: now, schema: schema,
                                        maskedKnightDeadline: maskedKnightDeadline) else {
            waitForObservation(id: id, reason: Self.waitingStatus(for: observation, now: now)); return
        }
        do {
            let motor = try policy?.action(for: input)
            var action = motor ?? .neutral
            if let starter {
                if schema >= 14 {
                    action = try starter.action(raw: input, motor: motor, waypoint: boundWaypoint)
                } else if let raw = starterHistory.input(observation, previousAction: lastAction, now: now, schema: 14,
                                                         maskedKnightDeadline: maskedKnightDeadline) {
                    action = try starter.action(raw: raw, motor: motor, waypoint: boundWaypoint)
                }
            }
            if let learnedSkill, skillSight()?.hasAbilityControls == true {
                action = try learnedSkill.action(for: input, base: action)
            }
            if let binding = maneuver?.choice.binding, let scene = observation.policyScene {
                let actor: BotSceneActor?
                switch binding.target {
                case .fight(let id): actor = scene.enemies.first { $0.id == id }
                case .lifeblood(let id): actor = scene.cocoons.first { $0.id == id }
                default: actor = nil
                }
                if let actor {
                    var buttons: InputBridgeButtons = actor.dx < -0.01 ? .left : actor.dx > 0.01 ? .right : []
                    if abs(actor.dx) < 0.11 && abs(actor.dy) < 0.13 && !lastAction.buttons.contains(.actionX) {
                        buttons.insert(.actionX)
                    }
                    action = BotAction(buttons: buttons)
                }
            }
            atlasRun.observe(observation, view: observation.atlasView)
            let deadline = min(now + 0.2, observation.capturedAt + BotObservationContract.maximumCaptureAge,
                               observation.availableAt + 0.25, observation.knightPredictionDeadline,
                               maskedKnightDeadline ?? .infinity)
            guard applyControl(action, deadline, id) else { stop(reason: "Game controls interrupted"); return }
            if maneuver != nil { skillDecisionCount += 1 }
            atlasRun.recordAction(action, after: lastAction)
            onActionApplied?(action, now)
            lastDecisionValidUntil = deadline
            lastDecisionTerrainSource = observation.terrainSource ?? "detected"
            lastDecisionHumanTerrainCount = observation.humanTerrainCount ?? 0
            if lastDecisionHumanTerrainCount > 0 { humanTerrainDecisionCount += 1 }
            if action != .neutral { nonNeutralDecisionCount += 1 }
            if observation.knightSource == "hacker-frame" { hackerKnightDecisionCount += 1 }
            if observation.knightPrediction != nil { predictedKnightDecisionCount += 1 }
            lastAction = action; decisionCount += 1; isWaiting = false
            let tracking = maskedKnightDeadline != nil ? " · Knight occluded" :
                (observation.knightPrediction != nil ? " · estimating Knight" : "")
            status = "Bot · \(action.displayName)" + tracking
        } catch {
            issue = error.localizedDescription
            stop(reason: "Bot inference failed")
        }
    }

    static func waitingStatus(for observation: BotObservation?, now: Double) -> String {
        guard let observation else { return "Bot waiting for a fresh frame" }
        if observation.issues.contains("stale-capture") || now - observation.capturedAt > BotObservationContract.maximumCaptureAge {
            return "Bot waiting for a fresh capture"
        }
        if observation.issues.contains("stale-or-missing-detection") { return "Bot waiting for fresh object detection" }
        if observation.issues.contains("missing-hacker-actor-telemetry") || observation.issues.contains("invalid-hacker-actors") {
            return "Bot waiting for mod actor telemetry"
        }
        if observation.issues.contains("missing-hacker-knight") { return "Bot waiting for the mod Knight position" }
        if observation.issues.contains("missing-hacker-enemies") { return "Bot waiting for mod enemy positions" }
        if observation.issues.contains("missing-knight") { return "Bot waiting for Knight detection" }
        if now >= observation.knightPredictionDeadline { return "Bot waiting for Knight detection" }
        if observation.issues.contains("missing-terrain-analysis") { return "Bot waiting for ground lines" }
        if observation.issues.contains("room-transition") { return "Bot waiting for the room transition" }
        return "Bot waiting for a fresh observation"
    }

    private func waitForObservation(id: UUID, reason: String) {
        if !isWaiting { neutralizeControl(id) }
        if !isWaiting { onActionApplied?(.neutral, now()) }
        history.reset(); starterHistory.reset(); lastAction = .neutral; lastDecisionValidUntil = 0
        if !isWaiting { isWaiting = true }
        if status != reason { status = reason }
    }

    private func finishManeuver(_ reason: String, id: UUID) {
        guard let run = maneuver, run.returnsToMotor else { stop(reason: reason); return }
        onSkillEvent?(run.plan, run.choice, run.sight.context, reason, now())
        maneuver = nil
        neutralizeControl(id)
        lastAction = .neutral
        onActionApplied?(.neutral, now())
        lastDecisionValidUntil = 0
        isWaiting = true
        history.reset()
        do { try configureSkill(nil, sight: nil) }
        catch { stop(reason: error.localizedDescription); return }
        status = "Bot waiting for a fresh frame"
    }

    // Direct maneuvers renew the same exclusive, short-lived input lease as the learned actor.
    // Menu and dialogue frames need fresh screen evidence even when no Knight is visible.
    private func driveSkill(id: UUID) -> Bool {
        let time = now()
        let sight = skillSight()
        if maneuver == nil, let sight, (0...0.45).contains(time - sight.capturedAt), let defaultChoice = BotSkillPlan.initial.choose(sight),
           defaultChoice.control == .inspect {
            maneuver = Maneuver(choice: defaultChoice, plan: .initial, start: time, sight: sight)
            onSkillEvent?(.initial, defaultChoice, sight.context, "selected", time)
        }
        guard let run = maneuver else { return false }
        guard let sight, sight.generation == run.sight.generation,
              (0...0.45).contains(time - sight.capturedAt) else {
            stop(reason: "Policy stopped · game frame expired")
            return true
        }
        let control = run.choice.control
        let elapsed = time - run.start
        let prefix = run.choice.policy.rawValue + " · " + control.rawValue
        if sight.context != run.sight.context {
            stop(reason: prefix + " · screen changed")
            return true
        }
        if run.choice.binding?.target == .focus, let binding = run.choice.binding {
            guard let scene = sight.scene, scene.room == binding.room, let recovery = scene.recovery else {
                finishManeuver("Focus telemetry expired", id: id); return true
            }
            if run.sent, let initial = run.sight.scene?.recovery, recovery.health > initial.health {
                finishManeuver("Focus restored a mask", id: id); return true
            }
            if !scene.focusOpening || (!run.sent && !scene.canStartFocus) {
                finishManeuver("Focus opening closed", id: id); return true
            }
        }
        if run.choice.measuresAtlasCoverage && atlasRun.score.newCells > run.initialNewCells {
            finishManeuver("Atlas updated · \(atlasRun.score.newCells - run.initialNewCells) new cells", id: id)
            return true
        }
        if elapsed >= control.duration {
            if control == .inspect, elapsed < 12 {
                if !isWaiting { neutralizeControl(id); onActionApplied?(.neutral, time) }
                lastAction = .neutral
                isWaiting = true
                status = "Reading sign or dialogue…"
                if let reading = sight.reading, reading.capturedAt > run.start, let clue = reading.passage {
                    finishManeuver("Remembered · " + clue, id: id)
                }
                return true
            }
            let result = control == .inspect ? "no new text verified"
                : control == .focus ? "no healing verified" : run.choice.measuresAtlasCoverage ? "no new ground verified" : "maneuver finished"
            finishManeuver(prefix + " · " + result, id: id)
            return true
        }
        if !run.sent && !sight.allows(run.choice) {
            stop(reason: prefix + " · cue or controls changed")
            return true
        }
        if control == .native { return false }
        let action = BotAction(buttons: control.buttons(elapsed: elapsed))
        let deadline = min(time + 0.14, sight.capturedAt + 0.45, run.start + control.duration)
        guard deadline > time, applyControl(action, deadline, id) else {
            stop(reason: prefix + " · controls unavailable")
            return true
        }
        if !run.sent && control == .inspect { didInspect?(sight, time) }
        maneuver?.sent = true
        if lastAction != action || isWaiting { onActionApplied?(action, time) }
        isWaiting = false
        lastAction = action
        decisionCount += 1
        skillDecisionCount += 1
        status = prefix
        return true
    }

    private func checkInputFreshness() {
        guard isActive, let id = controlSessionID else { return }
        guard canContinue(), hasFocus() else { stop(reason: "Gameplay controls interrupted"); return }
        if driveSkill(id: id) { return }
        if !hasFreshObservation || (lastDecisionValidUntil > 0 && now() >= lastDecisionValidUntil) {
            waitForObservation(id: id, reason: Self.waitingStatus(for: latest, now: now()))
        }
    }
}
