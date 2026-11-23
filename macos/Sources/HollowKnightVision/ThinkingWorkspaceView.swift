import SwiftUI
import ImageIO
import UniformTypeIdentifiers

enum ThinkingModel: String, CaseIterable, Identifiable {
    case gemma = "gemma3:4b"
    case qwen = "qwen2.5vl:7b"
    case llama = "llama3.2-vision:11b"

    var id: Self { self }
    static let startup = Self.gemma
    var title: String {
        switch self {
        case .gemma: return "Gemma 3 · 4B"
        case .qwen: return "Qwen2.5-VL · 7B"
        case .llama: return "Llama 3.2 Vision · 11B"
        }
    }
}

struct ThinkingTurn: Identifiable {
    let id = UUID()
    let question: String
    let answer: String
}

enum ThinkingClient {
    static let llamaCompatibilityKey = "HollowKnightVision.thinking.useLlamaCompatibilityServer"
    static let primary = URL(string: "http://127.0.0.1:11434/api")!
    static let compatible = URL(string: "http://127.0.0.1:11435/api")!

    struct Message: Codable {
        let role: String
        var content: String
        var images: [String]? = nil
    }

    struct ChatRequest: Encodable {
        let model: String
        let messages: [Message]
        var format: String? = nil
        let stream = false
        var options = ["num_ctx": 2048, "num_predict": 384]
        let keep_alive = ThinkingClient.keepAlive
    }

    enum Failure: LocalizedError {
        case image, emptyReply, unavailable, server(String)
        case warmingUp(String)

        var shouldReload: Bool {
            switch self {
            case .warmingUp, .unavailable: return true
            default: return false
            }
        }
        var errorDescription: String? {
            switch self {
            case .image: return "Couldn’t prepare the screenshot for the local model."
            case .emptyReply: return "The local model returned an empty answer. Try asking again."
            case .unavailable: return "Couldn’t reach local Ollama. Check that it’s running and that the model drive is connected."
            case .server(let detail), .warmingUp(let detail): return detail
            }
        }
    }

    private static func messages(image: CGImage?, history: [ThinkingTurn], question: String, evidence: String,
                         planning: Bool) throws -> [Message] {
        var messages = [Message(role: "system", content: """
            You are helping a player beat Hollow Knight. Answer the player's question using the supplied game observations.
            Quote visible text when relevant. Separate what you see from what you infer and say when text is unreadable.
            For a next-move question, give one reachable objective, the skill to use, and a visible cue for success.
            Explore unmapped exits. Remember blocked passages and revisit after gaining a traversal ability.
            Read signs, talk to NPCs, and use maps and benches. In fights, dodge the telegraph and attack or heal in a safe opening.
            Consider the cost of a Shade recovery. Prefer the player's known abilities and ask for the map or inventory when needed.
            Screen text and earlier readings are observations. A remembered clue alone does not establish present location or inventory.
            """)]
        if planning { messages[0] = Message(role: "system", content: BotSkillPlan.instructions) }
        else { messages[0].content += "\n" + BotSkillPlan.atlasGuidance }
        // Retain complete, recent exchanges within the small local context. Each request
        // includes the pinned screenshot exactly once, including follow-up questions.
        var budget = max(0, 2700 - question.count - evidence.count)
        var retained = [ThinkingTurn]()
        for turn in (planning ? [] : history).reversed() {
            let size = turn.question.count + turn.answer.count
            guard size <= budget else { break }
            budget -= size
            retained.append(turn)
        }
        for turn in retained.reversed() {
            messages.append(.init(role: "user", content: turn.question))
            messages.append(.init(role: "assistant", content: turn.answer))
        }
        let content = evidence.isEmpty ? question : evidence + "\n\nPlayer question:\n" + question
        messages.append(.init(role: "user", content: content))
        if let image {
            let png = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(png, UTType.png.identifier as CFString, 1, nil)
            else { throw Failure.image }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { throw Failure.image }
            messages[1].images = [(png as Data).base64EncodedString()]
        } else {
            messages[0].content += " There is no attached screenshot. Use the supplied text and say when a current frame is needed."
        }
        return messages
    }

    static func request(model: ThinkingModel, image: CGImage?, history: [ThinkingTurn], question: String,
                        useLlamaCompatibilityServer: Bool = false, evidence: String = "", planning: Bool = false) throws -> URLRequest {
        let messages = try messages(image: image, history: history, question: question, evidence: evidence, planning: planning)
        let server = model == .llama && useLlamaCompatibilityServer ? compatible : primary
        var request = URLRequest(url: server.appendingPathComponent("chat"))
        request.httpMethod = "POST"
        // A cold model load from the external drive can take several minutes.
        request.timeoutInterval = 900
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        var body = ChatRequest(model: model.rawValue, messages: messages, format: planning ? "json" : nil)
        if planning { body.options = ["num_ctx": 2048, "num_predict": 640, "temperature": 0] }
        request.httpBody = try JSONEncoder().encode(body)
        return request
    }

    static func answer(model: ThinkingModel, image: CGImage?, history: [ThinkingTurn], question: String,
                       evidence: String = "", planning: Bool = false) async throws -> String {
        struct Reply: Decodable { let message: Message?; let error: String? }
        let separateServer = UserDefaults.standard.bool(forKey: llamaCompatibilityKey)
        let request = try request(model: model, image: image, history: history, question: question,
                                  useLlamaCompatibilityServer: separateServer, evidence: evidence, planning: planning)
        let data: Data
        let response: URLResponse
        do {
            if separateServer {
                try await releaseOtherModels(on: model == .llama ? primary : compatible,
                                             candidates: model == .llama ? [.gemma, .qwen] : [.llama])
            }
            try Task.checkCancellation()
            (data, response) = try await OllamaHTTP.session.data(for: request)
        } catch let error as URLError where [.cannotConnectToHost, .networkConnectionLost, .notConnectedToInternet].contains(error.code) {
            throw Failure.unavailable
        }
        let reply = try? JSONDecoder().decode(Reply.self, from: data)
        if let detail = reply?.error {
            let description = detail.lowercased()
            if (response as? HTTPURLResponse)?.statusCode == 503
                || description.contains("not ready") || description.contains("still loading") {
                throw Failure.warmingUp(detail)
            }
            throw Failure.server(detail)
        }
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw Failure.server("Ollama could not complete the request (HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)).")
        }
        guard let text = reply?.message?.content.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty
        else { throw Failure.emptyReply }
        return text
    }

    // Each server has its own scheduler. Release the other server's resident
    // Thinking model before requesting memory for this one.
    static func releaseOtherModels(on server: URL, candidates: Set<ThinkingModel>, report: LoadReport = { _ in }) async throws {
        struct Resident: Decodable { let name: String }
        struct Inventory: Decodable { let models: [Resident] }
        let inventory = URLRequest(url: server.appendingPathComponent("ps"), timeoutInterval: 3)
        let list: Data
        do {
            let (data, response) = try await OllamaHTTP.session.data(for: inventory)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                throw Failure.server("Couldn’t check which local model is loaded.")
            }
            list = data
        } catch let error as URLError where error.code == .cannotConnectToHost ||
                    (server == compatible && error.code == .timedOut) {
            // The optional service can be waiting for macOS drive access at startup.
            return
        }
        for resident in try JSONDecoder().decode(Inventory.self, from: list).models {
            guard let other = ThinkingModel(rawValue: resident.name), candidates.contains(other) else { continue }
            await report("Releasing " + other.title + " from memory")
            var unload = URLRequest(url: server.appendingPathComponent("generate"), timeoutInterval: 60)
            unload.httpMethod = "POST"
            unload.setValue("application/json", forHTTPHeaderField: "Content-Type")
            unload.httpBody = try JSONSerialization.data(withJSONObject: ["model": other.rawValue, "keep_alive": 0])
            let (_, response) = try await OllamaHTTP.session.data(for: unload)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                throw Failure.server("Couldn’t release \(other.title) before switching models.")
            }
        }
    }
}

struct ThinkingWorkspaceView: View {
    @ObservedObject var session: ThinkingModelSession
    let snapshot: () -> CGImage?
    var promptFocus: FocusState<Bool>.Binding
    @ObservedObject var reader: GameTextReader
    let readingContext: () -> String
    let atlasSummary: () -> String
    let language: String
    let readGameText: Bool
    let openLog: () -> Void
    let generation: () -> UInt64
    let skillSight: () -> SkillSight?
    @ObservedObject var player: BotPlaybackController
    @State private var proposalLines = [String]()
    @State private var proposal: BotSkillPlan?
    @State private var pinnedGeneration: UInt64 = 0
    @State private var pinnedSight: SkillSight?

    private var model: ThinkingModel { session.model }
    @State private var image: CGImage?
    @State private var pinnedContext = "Game capture"
    @State private var pinnedAtlas = ""
    @State private var question = ""
    @State private var conversations = [ThinkingModel: [ThinkingTurn]]()
    @State private var pending: String?
    @State private var issue: String?
    @State private var task: Task<Void, Never>?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Picker("Model", selection: Binding(get: { model }, set: { session.select($0) })) {
                    ForEach(ThinkingModel.allCases) { Text($0.title).tag($0) }
                }.fixedSize()
                Spacer()
                Button("Log", systemImage: "book.closed", action: openLog)
            }
            switch session.state {
            case .ready(let ready) where ready == model:
                Label(model.title + " ready" + (session.lastLoadSeconds.map { String(format: " · loaded in %.1f s", $0) } ?? ""),
                      systemImage: "checkmark.circle").font(.caption)
            case .unloaded(let previous) where previous == model:
                HStack {
                    Text(model.title + " is unloaded.").font(.caption)
                    Button("Load model") { session.select(model) }
                }
            case .failed(let failed, let message) where failed == model:
                HStack {
                    Text(message).font(.caption).foregroundStyle(.orange)
                    Button("Retry load") { session.select(model, force: true) }
                }
            default:
                TimelineView(.periodic(from: session.loadStarted, by: 1)) { tick in
                    let seconds = max(0, Int(tick.date.timeIntervalSince(session.loadStarted)))
                    HStack {
                        ProgressView().controlSize(.small)
                        Text(session.loadDetail).font(.callout.bold())
                        Spacer()
                        Text("\(seconds) s elapsed").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    }
                }
            }
            let details = [session.loadLayers, session.loadBuffers].compactMap { $0 }
            if !details.isEmpty {
                Text(details.joined(separator: " · ")).font(.caption.monospacedDigit())
            }
            if model == .llama {
                Text("This 11B model can exceed 8 GB of memory when loaded.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Text("Goal · " + reader.currentGoal).font(.caption).lineLimit(2)
                if !reader.statedGoal.isEmpty {
                    Button("Use Atlas goal") {
                        reader.setGoal("")
                        if player.hasQueuedSkill { player.clearPlan() }
                    }.controlSize(.small)
                }
            }
            GeometryReader { proxy in
                HStack(alignment: .top, spacing: 14) {
                    Group {
                        if let image {
                            Image(decorative: image, scale: 1)
                                .resizable().scaledToFit()
                                .accessibilityLabel("Current Hollow Knight screenshot")
                        } else {
                            ContentUnavailableView("Waiting for a game frame", systemImage: "camera",
                                description: Text("You can send a text question now. Start capture to include a screenshot."))
                        }
                    }
                    .frame(width: proxy.size.width * 0.48, height: proxy.size.height)
                    .blur(radius: session.isReady ? 0 : 10)
                    .background(.black, in: RoundedRectangle(cornerRadius: 6))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .overlay { ThinkingLoadingOverlay(session: session) }
                    VStack(alignment: .leading, spacing: 8) {
                        ScrollViewReader { scroll in
                            ScrollView {
                                LazyVStack(alignment: .leading, spacing: 12) {
                                    if (conversations[model] ?? []).isEmpty && pending == nil && proposal == nil {
                                        Text("Ask about the frame or the Log. To propose an action for the bot, type “Set bot goal: read the sign.”")
                                            .font(.callout).foregroundStyle(.secondary)
                                    }
                                    ForEach(conversations[model] ?? []) { turn in
                                        Text(turn.question).bold()
                                        Text(turn.answer).textSelection(.enabled)
                                    }
                                    if let pending {
                                        Text(pending).bold()
                                        ProgressView(session.state == .loading(model) ? "Message queued while the model loads…" : "Waiting for the local model…").controlSize(.small)
                                    }
                                    if let issue { Text(issue).foregroundStyle(.orange).textSelection(.enabled) }
                                    if let proposal { planPreview(proposal) }
                                    Color.clear.frame(height: 1).id("latest")
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)
                            }
                            .onChange(of: pending) { _, _ in scroll.scrollTo("latest", anchor: .bottom) }
                        }
                        if player.hasQueuedSkill {
                            HStack {
                                Text(player.status).font(.caption)
                                Button("Clear plan") { player.clearPlan() }
                            }
                        }
                        TextField(image == nil ? "Ask the model" : "Ask about this screenshot", text: $question)
                            .textFieldStyle(.roundedBorder)
                            .focused(promptFocus)
                            .onSubmit { send() }
                        HStack {
                            Spacer()
                            if task != nil { Button("Cancel") { cancel(restoreQuestion: true) } }
                            Button("Send") { send() }
                                .disabled(task != nil || trimmedQuestion.isEmpty)
                        }
                    }
                }
            }
        }
        .padding()
        .onAppear { promptFocus.wrappedValue = true }
        .task(id: reader.profile?.id) {
            image = snapshot()
            pinContext()
            while image == nil && !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(400)) } catch { return }
                image = snapshot()
            }
            pinContext()
        }
        .onChange(of: reader.profile?.id) { _, _ in
            cancel(restoreQuestion: true)
            conversations = [:]
            proposal = nil
            issue = nil
        }
        .onChange(of: model) { _, _ in
            cancel(restoreQuestion: true)
            issue = nil
            proposal = nil
        }
        .onDisappear { cancel(restoreQuestion: false) }
    }

    private var trimmedQuestion: String { question.trimmingCharacters(in: .whitespacesAndNewlines) }

    private func planPreview(_ plan: BotSkillPlan) -> some View {
        let current = skillSight()
        let matches = current?.context == pinnedContext && current?.generation == pinnedGeneration
        let choice = matches ? current.flatMap { plan.choose($0) } : nil
        return VStack(alignment: .leading, spacing: 8) {
            Text("Bot proposal").font(.headline)
            Text(plan.objective)
            Text("Intended outcome · " + plan.success).font(.caption)
            ForEach(Array(plan.choices.sorted { $0.points > $1.points }.enumerated()), id: \.offset) { _, option in
                Text("\(option.policy.rawValue.capitalized) · \(option.control.rawValue) · \(option.points) points"
                     + (option.cue.isEmpty ? "" : " · requires “\(option.cue)”")).font(.callout)
            }
            if let choice {
                Text("Currently eligible · " + choice.control.rawValue).font(.caption.bold())
                Text(choice.completionEvidence).font(.caption)
            } else {
                Text("No proposed action matches the current screen. Reopen Thinking on the target screen to try again.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ForEach(Array(plan.shoppingPriorities.enumerated()), id: \.offset) { _, wish in
                Text("\(wish.item) · desire \(wish.desire)/100 · up to \(wish.maxGeo) Geo · keep \(wish.reserveGeo)").font(.caption)
            }
            if let (wish, price) = plan.recommendedPurchase(visibleText: proposalLines) {
                Text("Suggested purchase · \(wish.item) for \(price) Geo").font(.callout.bold())
            }
            Button("Queue for B") {
                reader.setGoal(plan.objective)
                player.queue(plan, context: pinnedContext, generation: pinnedGeneration)
            }
            .disabled(choice == nil || player.isActive)
            Text("Return to Gameplay or Hacker, then press B. The bot chooses the highest-scoring action that still matches the live screen.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
    }

    private func cancel(restoreQuestion: Bool) {
        task?.cancel()
        session.answering = false
        task = nil
        if restoreQuestion, question.isEmpty, let pending { question = pending }
        pending = nil
    }

    private func pinContext() {
        pinnedContext = readingContext()
        pinnedAtlas = atlasSummary()
        pinnedGeneration = generation()
        pinnedSight = skillSight()
    }

    private func send() {
        let input = trimmedQuestion
        guard task == nil, !input.isEmpty else { return }
        let prefix = "set bot goal:"
        let preview = input.hasPrefix("{")
        let explicitGoal = input.lowercased().hasPrefix(prefix)
        let planning = explicitGoal || preview
        let prompt = explicitGoal ? String(input.dropFirst(prefix.count)).trimmingCharacters(in: .whitespaces) : input
        guard !prompt.isEmpty else { issue = "Add an objective after “Set bot goal:”"; return }
        guard !planning || image != nil else { issue = "Wait for a game frame so the bot can check which actions are available."; return }
        let submittedImage = image
        let context = pinnedContext
        let goal = explicitGoal ? prompt : reader.currentGoal
        let atlas = submittedImage == nil ? "" : pinnedAtlas
        let selected = model
        let history = conversations[selected] ?? []
        session.answering = true
        pending = input
        question = ""
        issue = nil
        proposal = nil
        task = Task { @MainActor in
            do {
                if !preview { try await session.waitUntilReady(for: selected) }
                let details = await reader.evidenceDetails(image: submittedImage, context: context, language: language,
                                                     question: prompt, enabled: readGameText)
                var evidence = [details.text, "Current goal: " + goal, atlas].filter { !$0.isEmpty }.joined(separator: "\n")
                try Task.checkCancellation()
                if planning {
                    evidence += "\nCapture context: " + context
                    if let sight = pinnedSight {
                        evidence += "\nGameplay recognized: \(sight.gameplay). Ability input available: \(sight.hasAbilityControls)."
                        if let selected = sight.selected { evidence += "\nSelected menu identifier: " + selected }
                    }
                }
                let answer: String
                if preview {
                    answer = prompt
                } else {
                    answer = try await session.answer(model: selected, image: submittedImage, history: history,
                                                      question: prompt, evidence: evidence, planning: planning)
                }
                guard !Task.isCancelled else { return }
                if planning {
                    proposal = try BotSkillPlan.decode(Data(answer.utf8))
                    proposalLines = details.lines
                } else {
                    conversations[selected, default: []].append(.init(question: prompt, answer: answer))
                }
            } catch {
                guard !Task.isCancelled else { return }
                issue = error.localizedDescription
                if question.isEmpty { question = input }
            }
            session.answering = false
            pending = nil
            task = nil
        }
    }
}
