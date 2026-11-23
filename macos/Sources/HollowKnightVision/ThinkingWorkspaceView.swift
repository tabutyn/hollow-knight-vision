import SwiftUI
import ImageIO
import UniformTypeIdentifiers

enum ThinkingModel: String, CaseIterable, Identifiable {
    case gemma = "gemma3:4b"
    case qwen = "qwen2.5vl:7b"
    case llama = "llama3.2-vision:11b"

    var id: Self { self }
    static let startup = Self.gemma
    static let policyContextLength = 4096
    var title: String {
        switch self {
        case .gemma: return "Gemma 3 · 4B"
        case .qwen: return "Qwen2.5-VL · 7B"
        case .llama: return "Llama 3.2 Vision · 11B"
        }
    }
}

struct ThinkingWorkspaceView: View {
    @ObservedObject var session: ThinkingModelSession
    @ObservedObject var inspector: ThinkingInspector
    @ObservedObject var player: BotPlaybackController
    let openLog: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Picker("Model", selection: Binding(get: { session.model }, set: { session.select($0) })) {
                    ForEach(ThinkingModel.allCases) { Text($0.title).tag($0) }
                }.fixedSize()
                Spacer()
                Button("Log", systemImage: "book.closed", action: openLog)
                Toggle("Automatic policies", isOn: $player.automaticPoliciesEnabled).toggleStyle(.switch)
            }
            modelStatus
            if let status = inspector.status {
                HStack(spacing: 7) {
                    ProgressView().controlSize(.small)
                    Text(status)
                }.font(.caption)
            }
            if let issue = inspector.issue {
                Text(issue).font(.caption).foregroundStyle(.orange).textSelection(.enabled)
            }
            HSplitView {
                VSplitView {
                    screenshotPanel
                        .frame(minHeight: 132, idealHeight: 310)
                    inputPanel
                        .frame(minHeight: 132)
                }
                .frame(minWidth: 300, idealWidth: 690)
                outputPanel
                    .frame(minWidth: 280, idealWidth: 480)
            }
        }
        .padding(12)
    }

    @ViewBuilder private var modelStatus: some View {
        switch session.state {
        case .ready:
            Label("Model ready", systemImage: "checkmark.circle").font(.caption)
        case .loading:
            TimelineView(.periodic(from: session.loadStarted, by: 1)) { tick in
                HStack {
                    ProgressView().controlSize(.small)
                    Text(session.loadDetail)
                    Spacer()
                    Text("\(max(0, Int(tick.date.timeIntervalSince(session.loadStarted)))) s elapsed")
                }.font(.caption).monospacedDigit()
            }
            let placement = [session.loadLayers, session.loadBuffers].compactMap { $0 }.joined(separator: " · ")
            if !placement.isEmpty {
                Text(placement).font(.caption).monospacedDigit()
            }
        case .failed(_, let reason):
            HStack {
                Text(reason).foregroundStyle(.orange)
                Button("Retry model") { session.select(session.model, force: true) }
            }.font(.caption)
        case .idle, .unloaded:
            Text("Model will load for the next decision.").font(.caption).foregroundStyle(.secondary)
        }
    }

    private var screenshotPanel: some View {
        VStack(alignment: .leading, spacing: 6) {
            panelHeading("Latest screenshot", detail: inspector.frame.map { captureTime($0.capturedAt) } ?? "Every minute")
                .accessibilityIdentifier("thinking-frame-time")
            if let frame = inspector.frame {
                Image(decorative: frame.image, scale: 1)
                    .resizable()
                    .scaledToFit()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .accessibilityLabel("Last automatically captured Hollow Knight frame")
            } else {
                Text("Waiting for game capture")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }.padding(8)
    }

    private var inputPanel: some View {
        VStack(alignment: .leading, spacing: 6) {
            panelHeading("Model input", detail: inspector.input.map { captureTime($0.frame.capturedAt) })
            ScrollView {
                if let input = inspector.input {
                    VStack(alignment: .leading, spacing: 12) {
                        if inspector.output?.input.frame.id != input.frame.id {
                            candidateList(input, plan: nil, showsScores: false)
                        }
                        DisclosureGroup("System message") {
                            contextBlock(input.context.system)
                        }
                        DisclosureGroup("User message") {
                            contextBlock(input.context.user)
                        }
                        DisclosureGroup("Response schema") {
                            Text(input.context.schemaText)
                                .font(.system(.caption, design: .monospaced))
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier("thinking-context")
                    .id(input.frame.id)
                } else {
                    Text("Waiting for context from the first frame.").foregroundStyle(.secondary)
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }.padding(8)
    }

    private var outputPanel: some View {
        VStack(alignment: .leading, spacing: 6) {
            panelHeading("Latest decision", detail: inspector.output.map { "Frame " + captureTime($0.input.frame.capturedAt) })
            if let output = inspector.output {
                Text(output.input.model.title + " · received " + captureTime(output.receivedAt))
                    .font(.caption).foregroundStyle(.secondary)
                if let issue = output.issue {
                    Text(issue).font(.caption).foregroundStyle(.orange)
                } else if let handoff = output.handoff {
                    Text(handoff).font(.caption)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        candidateList(output.input, plan: output.plan, showsScores: true)
                        Text("JSON reply").font(.subheadline.bold())
                        contextBlock(output.text)
                            .accessibilityIdentifier("thinking-output-json")
                    }.id(output.input.frame.id)
                }
            } else {
                Text("Waiting for the first policy reply.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }.padding(8)
    }

    private func panelHeading(_ title: String, detail: String?) -> some View {
        HStack {
            Text(title).font(.headline)
            Spacer()
            if let detail { Text(detail).font(.caption).monospacedDigit().foregroundStyle(.secondary) }
        }
    }

    private func candidateList(_ input: ThinkingInspector.Input, plan: BotSkillPlan?, showsScores: Bool) -> some View {
        let contract = input.context.contract
        let rows = contract.rows(scoredBy: plan)
        return VStack(alignment: .leading, spacing: 7) {
            Text("Vision candidates · decision \(contract.decisionID)").font(.subheadline.bold())
                .help("The highest-scored candidate with a live target can run. Equal scores use offer order.")
            if rows.isEmpty {
                Text("No executable targets found in this capture.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(rows) { row in
                DisclosureGroup {
                    contextBlock(row.parameters)
                } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(verbatim: row.id).monospaced()
                        VStack(alignment: .leading, spacing: 2) {
                            Text(row.policy).bold()
                            Text(row.target).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if showsScores {
                            Text(row.score.map { "\($0) / 100" } ?? (plan == nil ? "Invalid reply" : "Unscored"))
                                .font(.caption).monospacedDigit()
                        }
                    }
                }
            }
        }.accessibilityIdentifier("thinking-candidates")
    }

    private func contextBlock(_ value: String) -> some View {
        Text(value).font(.system(.callout, design: .monospaced)).textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func captureTime(_ date: Date) -> String {
        date.formatted(.dateTime.hour().minute().second())
    }
}

struct ThinkingContext {
    let system: String
    let observations: String
    let contract: ThinkingPolicyContract

    init(evidence: String, hasImage: Bool, sight: SkillSight? = nil, visible: [String] = []) {
        var instructions = BotSkillPlan.atlasGuidance +
            "\nUse recorded and visible game text as observations. Follow the final policy selection specification."
        if !hasImage {
            instructions += "\nUse the supplied observations. There is no attached screenshot."
        }
        system = instructions
        observations = evidence
        contract = ThinkingPolicyContract(sight: sight, visible: visible)
    }

    var user: String {
        observations + "\n\nPolicy selection specification\n" + contract.selectionSpec
    }

    var schemaText: String {
        guard let data = try? JSONSerialization.data(withJSONObject: contract.schema, options: [.prettyPrinted, .sortedKeys])
        else { return "" }
        return String(decoding: data, as: UTF8.self)
    }
}

enum ThinkingClient {
    static let llamaCompatibilityKey = "HollowKnightVision.thinking.useLlamaCompatibilityServer"
    static let primary = URL(string: "http://127.0.0.1:11434/api")!
    static let compatible = URL(string: "http://127.0.0.1:11435/api")!

    struct Message: Decodable {
        let role: String
        var content: String
        var images: [String]? = nil

        var payload: [String: Any] {
            var result: [String: Any] = ["role": role, "content": content]
            result["images"] = images
            return result
        }
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
            case .emptyReply: return "The local model returned an empty proposal."
            case .unavailable: return "Couldn’t reach local Ollama. Check that it’s running and that the model drive is connected."
            case .server(let detail), .warmingUp(let detail): return detail
            }
        }
    }

    static func request(model: ThinkingModel, image: CGImage?, context: ThinkingContext,
                        useLlamaCompatibilityServer: Bool = false) throws -> URLRequest {
        var input = Message(role: "user", content: context.user)
        if let image {
            let png = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(png, UTType.png.identifier as CFString, 1, nil)
            else { throw Failure.image }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { throw Failure.image }
            input.images = [(png as Data).base64EncodedString()]
        }
        let server = model == .llama && useLlamaCompatibilityServer ? compatible : primary
        var request = URLRequest(url: server.appendingPathComponent("chat"))
        request.httpMethod = "POST"
        request.timeoutInterval = 900
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "model": model.rawValue, "messages": [Message(role: "system", content: context.system).payload, input.payload],
            "format": context.contract.schema, "stream": false, "keep_alive": keepAlive,
            "options": ["num_ctx": ThinkingModel.policyContextLength, "num_predict": 640, "temperature": 0]
        ])
        return request
    }

    static func answer(model: ThinkingModel, image: CGImage?, context: ThinkingContext) async throws -> String {
        struct Reply: Decodable { let message: Message?; let error: String? }
        let separateServer = UserDefaults.standard.bool(forKey: llamaCompatibilityKey)
        let request = try request(model: model, image: image, context: context, useLlamaCompatibilityServer: separateServer)
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
