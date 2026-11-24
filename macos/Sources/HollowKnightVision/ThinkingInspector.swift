import SwiftUI

struct ThinkingFrame {
    let id = UUID()
    let capturedAt = Date()
    let image: CGImage
    let profile: UUID?
    let generation: UInt64
    let context: String
    let mode: String
    let atlas: String
    let sight: SkillSight?
    let language: String
    let readsText: Bool
    var controlDomain: BotDomain = .gameplay

    func decisionEvidence(visible: [String]) -> String {
        var lines = ["Current room or menu: " + context, "Game mode: " + mode]
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

@MainActor
final class ThinkingInspector: ObservableObject {
    struct Input {
        let frame: ThinkingFrame
        let model: ThinkingModel
        let context: ThinkingContext
    }

    struct Output {
        let input: Input
        let receivedAt = Date()
        let text: String
        let plan: BotSkillPlan?
        let issue: String?
        var handoff: String?

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
                plan = try input.context.contract.resolve(data)
                issue = nil
            } catch {
                plan = nil
                issue = "Proposal rejected. " + error.localizedDescription
            }
        }
    }

    @Published private(set) var frame: ThinkingFrame?
    @Published private(set) var input: Input?
    @Published private(set) var output: Output?
    @Published private(set) var status: String?
    @Published private(set) var issue: String?
    private var pending: ThinkingFrame?
    private var worker: Task<Void, Never>?
    private var revision = 0

    func monitor(reader: GameTextReader, session: ThinkingModelSession, interval: Duration = .seconds(60),
                 dispatch: @escaping (Output) -> String? = { _ in nil },
                 snapshot: () -> ThinkingFrame?) async {
        guard session.isEnabled else { return }
        defer { reset(session: session, keepFrame: true) }
        while !Task.isCancelled {
            if let sample = snapshot() {
                frame = sample
                submit(sample, reader: reader, session: session, dispatch: dispatch)
                do { try await Task.sleep(for: interval) } catch { return }
            } else {
                do { try await Task.sleep(for: .seconds(1)) } catch { return }
            }
        }
    }

    func reset(session: ThinkingModelSession, keepFrame: Bool) {
        revision += 1
        worker?.cancel()
        worker = nil
        pending = nil
        if !keepFrame { frame = nil }
        input = nil
        output = nil
        status = nil
        issue = nil
        session.answering = false
    }

    private func submit(_ sample: ThinkingFrame, reader: GameTextReader, session: ThinkingModelSession, dispatch: @escaping (Output) -> String?) {
        pending = sample
        guard worker == nil else { return }
        let ticket = revision
        let model = session.model
        session.answering = true
        worker = Task {
            while !Task.isCancelled, ticket == revision, let sample = pending {
                pending = nil
                status = "Preparing context…"
                issue = nil
                do {
                    guard reader.profile?.id == sample.profile else { continue }
                    let memory = try await reader.policyContext(goal: BotSkillPlan.defaultObjective)
                    let lines = sample.readsText
                        ? await reader.visibleLines(in: sample.image, language: sample.language, context: sample.context) : []
                    try Task.checkCancellation()
                    guard session.model == model, reader.profile?.id == sample.profile else { continue }
                    let evidence = memory + "\nLive decision evidence\n" + sample.decisionEvidence(visible: lines)
                    let sight = sample.mode == HollowKnightRuntimeMode.modded.rawValue ? sample.sight : nil
                    let context = ThinkingContext(evidence: evidence, hasImage: true, sight: sight, visible: lines)
                    let prepared = Input(frame: sample, model: model, context: context)
                    input = prepared
                    status = "Waiting for \(model.title)…"
                    try await session.waitUntilReady(for: model)
                    status = "Generating policy JSON…"
                    let reply = try await session.answer(model: model, image: sample.image, context: context)
                    try Task.checkCancellation()
                    guard session.model == model, reader.profile?.id == sample.profile else { continue }
                    var result = Output(input: prepared, reply: reply)
                    if result.plan != nil {
                        result.handoff = frame?.id == sample.id ? dispatch(result) : "Skipped · a newer frame is available."
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
