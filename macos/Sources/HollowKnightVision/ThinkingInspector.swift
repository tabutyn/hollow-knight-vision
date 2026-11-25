import SwiftUI
import ImageIO
import UniformTypeIdentifiers

/// Retain the lossless backing file as long as Core Graphics may still read its pixels.
final class ThinkingImageFile {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("hnv-event-" + UUID().uuidString + ".png")
    deinit { try? FileManager.default.removeItem(at: url) }
}

struct ThinkingFrame {
    let id = UUID()
    let capturedAt = Date()
    var image: CGImage
    let profile: UUID?
    let generation: UInt64
    let context: String
    let mode: String
    let atlas: String
    let sight: SkillSight?
    let language: String
    let readsText: Bool
    var capturedMenu: ThinkingMenuPage?
    var capturedGameplay = false
    var stateVisit: UInt64?
    var previousStateID: String?
    var previousConfirmedElement: String?
    var controlDomain: BotDomain = .gameplay
    var reasoning = ""
    var signEvidence = ""
    var allowsMenuMore = true
    var event = ""
    var imageFile: ThinkingImageFile?

    func diskBacked() throws -> Self {
        let file = ThinkingImageFile()
        guard let destination = CGImageDestinationCreateWithURL(file.url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw ThinkingClient.Failure.image
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination),
              let source = CGImageSourceCreateWithURL(file.url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            throw ThinkingClient.Failure.image
        }
        var copy = self
        copy.image = image
        copy.imageFile = file
        return copy
    }

    func decisionEvidence(visible: [String]) -> String {
        var lines = ["Current room or menu: " + context, "Game mode: " + mode]
        if let previousStateID { lines.append("Previous detected state: " + previousStateID) }
        if let previousConfirmedElement { lines.append("Vision sent confirm on: " + previousConfirmedElement) }
        if !event.isEmpty { lines.append("Reason for capture: " + event) }
        if !signEvidence.isEmpty { lines.append(signEvidence) }
        if !atlas.isEmpty { lines.append(atlas) }
        if let sight {
            lines.append("Gameplay recognized: \(sight.gameplay). Ability input available: \(sight.hasAbilityControls).")
            if let selected = sight.selected { lines.append("Selected menu identifier: " + selected) }
        }
        if readsText {
            lines.append("Visible OCR on the captured frame:\n" + visible.joined(separator: "\n").prefix(650))
        }
        return lines.joined(separator: "\n")
    }
}

struct ThinkingSignals {
    let profile: UUID?
    let generation: UInt64
    let room, mode: String
    var sign = false, npc = false  // Visible INSPECT / LISTEN cues.
    var signTracks = Set<UInt64>(), npcTracks = Set<UInt64>()
    var scene: BotPolicyScene?
    var activeRoute: BotRouteTarget?
    var blocked = false
    var selectedMenu: String?
    var outcomeID: Int?
    var outcome: String?
    var signImage: CGImage?
    var signBounds = [CGRect]()
    var signSourceID: UInt64?
    var menuImage: CGImage?
    var menuPage: ThinkingMenuPage?
    var recognizedGameplay = false
    var stateID: String?
    var stateVisit: UInt64?
    var previousStateID: String?
    var previousConfirmedElement: String?
    var awaitingRecognition = false

    func decisionBrief(lastResult: ThinkingInspector.Output?) -> String {
        var lines = ["Objective · " + BotSkillPlan.defaultObjective, "Current location · " + room]
        if let page = menuPage {
            lines.append(page.elements.contains(where: \.canSelect)
                ? "Choose a visible element to continue from " + page.stateID + "."
                : "Waiting for a visible selectable element in " + page.stateID + ".")
        } else if sign || !signTracks.isEmpty { lines.append("A sign is visible. Look for the INSPECT prompt.") }
        else if npc || !npcTracks.isEmpty { lines.append("An NPC is visible. Look for the LISTEN prompt.") }
        else if let route = activeRoute { lines.append("Following waypoint (\(route.waypoint.x), \(route.waypoint.y)) in " + route.chart + ".") }
        else if let scene { lines.append("Reachable routes · \(scene.routes.count)") }
        if let outcome { lines.append("Observed outcome · " + outcome) }
        if let lastResult, lastResult.validated, lastResult.input.frame.profile == profile,
           lastResult.input.frame.generation == generation {
            let choice = lastResult.input.context.contract.rows(selectedBy: lastResult.plan, script: lastResult.script).first(where: \.selected)
            if let choice { lines.append("Previous choice · " + choice.policy + " · " + choice.target) }
            if let handoff = lastResult.handoff { lines.append(handoff) }
        }
        return lines.joined(separator: "\n")
    }

    func attachEvidence(to frame: inout ThinkingFrame, trackedSign: Bool) {
        frame.capturedMenu = menuPage
        frame.capturedGameplay = recognizedGameplay
        frame.stateVisit = stateVisit
        frame.previousStateID = previousStateID
        frame.previousConfirmedElement = previousConfirmedElement
        if trackedSign, let signImage, let signSourceID {
            frame.image = signImage
            let bounds = signBounds.map { [Double($0.minX), Double($0.minY), Double($0.width), Double($0.height)] }
            frame.signEvidence = "Detector: world.sign, frame \(signSourceID), normalized [left, top, width, height]: \(bounds)"
        } else if let menuImage { frame.image = menuImage }
    }
}

/// Poll only cheap observations. Capture pixels when an observed condition starts.
struct ThinkingEventDetector {
    private(set) var newSignTrack = false
    private var previous: ThinkingSignals?
    private var menuElements = Set<String>()
    private var menuHighlight: String?
    private var signGoneSince: Double?, npcGoneSince: Double?
    private var signPresent = false, npcPresent = false
    private var seenSigns = Set<UInt64>(), seenNPCs = Set<UInt64>()
    private var lastEnemy: Int?, focusAvailable = false
    private var blockedSince: Double?
    private var reportedDeadEnd = false
    private var routesAvailable = false
    private var observedRoute: BotRouteTarget?

    func changedState(_ now: ThinkingSignals) -> Bool {
        previous == nil || previous?.profile != now.profile || previous?.generation != now.generation ||
            previous?.stateVisit != now.stateVisit || previous?.stateID != now.stateID ||
            previous?.room != now.room || previous?.mode != now.mode
    }

    mutating func events(_ now: ThinkingSignals, at time: Double) -> [String] {
        let newRoom = changedState(now)
        var reasons = [String]()
        if newRoom {
            if let id = now.menuPage?.stateID { reasons.append("Menu entered · " + id) }
            else { reasons.append(previous == nil ? "First observation" : "Room or game state changed") }
            menuElements = []; menuHighlight = nil
            signPresent = false; npcPresent = false; signGoneSince = nil; npcGoneSince = nil
            seenSigns = []; seenNPCs = []; lastEnemy = nil; focusAvailable = false
            blockedSince = nil; reportedDeadEnd = false; routesAvailable = false; observedRoute = nil
        }
        func appeared(_ visible: Bool, present: inout Bool, goneSince: inout Double?) -> Bool {
            if visible {
                let rising = !present
                present = true; goneSince = nil
                return rising
            }
            if goneSince == nil { goneSince = time }
            if time - (goneSince ?? time) >= 2.5 { present = false }
            return false
        }
        let newSignCue = appeared(now.sign, present: &signPresent, goneSince: &signGoneSince)
        let newNPCCue = appeared(now.npc, present: &npcPresent, goneSince: &npcGoneSince)
        newSignTrack = !now.signTracks.isSubset(of: seenSigns)
        if newSignCue || newSignTrack { reasons.append("Sign visible") }
        if newNPCCue || !now.npcTracks.isSubset(of: seenNPCs) { reasons.append("NPC visible") }
        seenSigns.formUnion(now.signTracks); seenNPCs.formUnion(now.npcTracks)
        if let scene = now.scene {
            if let enemy = scene.enemies.first?.id, enemy != lastEnemy { reasons.append("Enemy encountered") }
            if scene.canStartFocus && !focusAvailable { reasons.append("Focus available") }
            lastEnemy = scene.enemies.first?.id; focusAvailable = scene.canStartFocus
        }
        if let scene = now.scene, let position = scene.navigationPosition {
            // A selected waypoint takes precedence over the default Atlas frontier.
            if let selected = now.activeRoute, previous?.activeRoute?.matches(selected) != true {
                observedRoute = selected
            } else if previous?.activeRoute != nil && now.activeRoute == nil {
                observedRoute = nil
            }
            if let prior = observedRoute, prior.chart == position.chart, position.feet.distance(prior.goal) <= position.arrivalRadius {
                reasons.append(prior.hint.frontier ? "Explore destination reached" : "Target reached")
                observedRoute = nil
            }
            if let next = now.activeRoute ?? scene.routes.first, position.feet.distance(next.goal) > position.arrivalRadius {
                observedRoute = next
            }
            if !newRoom && !routesAvailable && !scene.routes.isEmpty { reasons.append("Route available") }
            routesAvailable = !scene.routes.isEmpty
            if scene.grounded && scene.routes.isEmpty {
                if blockedSince == nil { blockedSince = time }
                if !reportedDeadEnd && (now.blocked || time - (blockedSince ?? time) >= 1.5) {
                    reasons.append("Dead end"); reportedDeadEnd = true
                }
            } else { blockedSince = nil; reportedDeadEnd = false }
        } else { blockedSince = nil }
        if let page = now.menuPage {
            let elements = Set(page.elements.map(\.id))
            if !newRoom, let selected = page.selected, selected != menuHighlight { reasons.append("Menu selection changed") }
            else if !newRoom, !elements.isEmpty, elements != menuElements { reasons.append("Menu elements changed") }
            if let selected = page.selected { menuHighlight = selected }
            if !elements.isEmpty { menuElements = elements }
        } else if !newRoom, let selected = now.selectedMenu, selected != previous?.selectedMenu {
            reasons.append("Menu selection changed")
        }
        if !newRoom, let id = now.outcomeID, previous?.outcomeID != id, let outcome = now.outcome, !reasons.contains(outcome) { reasons.append(outcome) }
        previous = now
        return reasons
    }
}

@MainActor
final class ThinkingInspector: ObservableObject {
    struct Input {
        let frame: ThinkingFrame
        let model: ThinkingModel
        let context: ThinkingContext
        var liveEvidence: String = ""
    }

    struct Output {
        let input: Input
        let receivedAt = Date()
        let text: String
        let plan: BotSkillPlan?
        let script: ThinkingMenuScript?
        var validated: Bool { plan != nil || script != nil }
        let issue: String?
        var handoff: String?
        var file: URL?

        init(input: Input, reply: String) {
            self.input = input
            let data = Data(reply.utf8)
            if let json = try? JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed),
               let formatted = try? JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys, .fragmentsAllowed]) {
                text = String(decoding: formatted, as: UTF8.self)
            } else {
                text = reply
            }
            do {
                if input.context.contract.menuPage != nil {
                    script = try input.context.contract.menuScript(data)
                    plan = nil
                } else {
                    plan = try input.context.contract.resolve(data)
                    script = nil
                }
                issue = nil
            } catch {
                script = nil
                plan = nil
                issue = "Reply rejected. " + error.localizedDescription
            }
        }
    }

    @Published private(set) var frame: ThinkingFrame?
    @Published private(set) var input: Input?
    @Published private(set) var output: Output?
    @Published private(set) var status: String?
    @Published private(set) var issue: String?
    private var pending = [ThinkingFrame]()
    private var worker: Task<Void, Never>?
    private var revision = 0
    @Published private(set) var reasoning = ""
    private var nextBriefUpdate = 0.0
    private var menuRunning = false
    private var menuAdvancedTo: String?
    private var browsed = Set<[String]>()
    private var browsingMenu: String?
    private var detector = ThinkingEventDetector()
    private let sessionID = UUID()
    private let directory: URL
    typealias Answer = @MainActor (ThinkingModelSession, ThinkingModel, ThinkingContext) async throws -> String
    private let answer: Answer

    init(directory: URL = GameTextNotebook.directory.deletingLastPathComponent().appendingPathComponent("Thinking"),
         answer: @escaping Answer = { try await $0.answer(model: $1, context: $2) }) {
        self.directory = directory
        self.answer = answer
    }

    func save(_ result: Output) throws -> URL {
        guard result.validated else { throw BotRuntimeError.incompatible("Invalid policy selection.") }
        let folder = directory.appendingPathComponent(result.input.frame.profile?.uuidString ?? "before-save")
            .appendingPathComponent(sessionID.uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent("latest-policy.json")
        try Data(result.text.utf8).write(to: url, options: .atomic)
        return url
    }

    func monitor(reader: GameTextReader, session: ThinkingModelSession, interval: Duration = .milliseconds(200),
                 signals: () -> ThinkingSignals?, dispatch: @escaping (Output) async -> ThinkingHandoff? = { _ in nil },
                 snapshot: () -> ThinkingFrame?) async {
        guard session.isEnabled else { return }
        defer { reset(session: session, keepFrame: true) }
        while !Task.isCancelled {
            if let state = signals() {
                observe(state, reader: reader, session: session, dispatch: dispatch, snapshot: snapshot)
            } else if frame != nil { reset(session: session, keepFrame: false) }
            do { try await Task.sleep(for: interval) } catch { return }
        }
    }

    /// Called immediately on a recognized state edge, and by the slower gameplay poll.
    /// A menu transition during a script or inference still owns its original screenshot.
    func observe(_ state: ThinkingSignals, reader: GameTextReader, session: ThinkingModelSession,
                 dispatch: @escaping (Output) async -> ThinkingHandoff?, snapshot: () -> ThinkingFrame?) {
        guard session.isEnabled, !state.awaitingRecognition else { return }
        let changedState = detector.changedState(state)
        let now = ProcessInfo.processInfo.systemUptime
        if changedState || now >= nextBriefUpdate {
            let brief = state.decisionBrief(lastResult: output)
            if reasoning != brief { reasoning = brief }
            nextBriefUpdate = now + 5
        }
        guard !menuRunning || changedState else { return }
        var next = detector
        var reasons = next.events(state, at: ProcessInfo.processInfo.systemUptime)
        if !changedState, let menuAdvancedTo, state.selectedMenu == menuAdvancedTo, state.menuImage != nil {
            reasons.append("Menu advanced")
        }
        if reasons.isEmpty { detector = next; return }
        guard var sample = snapshot() else { return }
        detector = next
        state.attachEvidence(to: &sample, trackedSign: next.newSignTrack)
        let page = sample.capturedMenu ?? sample.sight?.menuPage
        if changedState || browsingMenu != page?.stateID {
            browsed.removeAll()
            browsingMenu = page?.stateID
        }
        sample.allowsMenuMore = page.map { !browsed.contains($0.pageKey) } ?? true
        menuAdvancedTo = nil
        sample.reasoning = state.decisionBrief(lastResult: output)
        if reasoning != sample.reasoning { reasoning = sample.reasoning }
        sample.event = reasons.joined(separator: " · ")
        frame = sample
        submit(sample, reader: reader, session: session, dispatch: dispatch)
    }

    func reset(session: ThinkingModelSession, keepFrame: Bool) {
        detector = ThinkingEventDetector()
        reasoning = ""
        nextBriefUpdate = 0
        menuRunning = false
        menuAdvancedTo = nil
        browsed.removeAll()
        browsingMenu = nil
        revision += 1
        worker?.cancel()
        worker = nil
        pending.removeAll()
        if !keepFrame { frame = nil }
        input = nil
        output = nil
        status = nil
        issue = nil
        session.answering = false
    }

    private func submit(_ sample: ThinkingFrame, reader: GameTextReader, session: ThinkingModelSession, dispatch: @escaping (Output) async -> ThinkingHandoff?) {
        // Keep the newest waiting preview in memory. Spill older queued pictures
        // to local storage when gameplay produces events faster than inference.
        if let previous = pending.indices.last, pending[previous].imageFile == nil {
            do { pending[previous] = try pending[previous].diskBacked() }
            catch {
                issue = "Keeping the queued screenshot in memory. " + error.localizedDescription
            }
        }
        pending.append(sample)
        guard worker == nil else { return }
        let ticket = revision
        let model = session.model
        session.answering = true
        worker = Task {
            while !Task.isCancelled, ticket == revision, !pending.isEmpty {
                let sample = pending.removeFirst()
                status = "Preparing context…"
                issue = nil
                do {
                    guard reader.profile?.id == sample.profile else { continue }
                    let memory = try await reader.policyContext()
                    let lines = sample.readsText
                        ? await reader.visibleLines(in: sample.image, language: sample.language, context: sample.context) : []
                    try Task.checkCancellation()
                    guard session.model == model, reader.profile?.id == sample.profile else { continue }
                    let live = sample.decisionEvidence(visible: lines)
                    let evidence = memory + "\nLive decision evidence\n" + live + "\nDecision brief\n" + sample.reasoning
                    let sight = sample.sight
                    let context = ThinkingContext(evidence: evidence, image: sample.image, sight: sight, visible: lines,
                        allowsMore: sample.allowsMenuMore, capturedMenu: sample.capturedMenu, recognizedGameplay: sample.capturedGameplay)
                    let prepared = Input(frame: sample, model: model, context: context, liveEvidence: live)
                    input = prepared
                    guard context.contract.hasChoices else {
                        status = "Waiting for a recognized game or menu target."
                        continue
                    }
                    status = "Waiting for \(model.title)…"
                    try await session.waitUntilReady(for: model)
                    status = "Choosing an action…"
                    let reply = try await answer(session, model, context)
                    try Task.checkCancellation()
                    guard session.model == model, reader.profile?.id == sample.profile else { continue }
                    var result = Output(input: prepared, reply: reply)
                    if result.validated {
                        if frame?.id == sample.id {
                            result.file = try save(result)
                            menuRunning = result.script.map { $0.request != .wait } ?? false
                            let handoff = await dispatch(result)
                            result.handoff = handoff?.text
                            guard ticket == revision, !Task.isCancelled else { return }
                            menuRunning = false
                            if frame?.id == sample.id, let script = result.script, case .menuAdvanced(let highlighted) = handoff {
                                browsed.insert(script.page.pageKey)
                                menuAdvancedTo = highlighted
                            }
                        } else { result.handoff = "Skipped · a newer event is available." }
                    }
                    output = result
                } catch {
                    if !Task.isCancelled, ticket == revision { issue = error.localizedDescription }
                }
            }
            if ticket == revision {
                worker = nil
                status = nil
                session.answering = false
            }
        }
    }
}
