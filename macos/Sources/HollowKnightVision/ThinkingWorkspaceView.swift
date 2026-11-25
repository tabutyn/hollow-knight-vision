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

struct ThinkingContext {
    let system: String
    let observations: String
    let image: CGImage?
    let contract: ThinkingPolicyContract

    init(evidence: String, image: CGImage?, sight: SkillSight? = nil, visible: [String] = [], allowsMore: Bool = true,
         capturedMenu: ThinkingMenuPage? = nil, recognizedGameplay: Bool = false) {
        var instructions = BotSkillPlan.atlasGuidance +
            "\nUse recorded and visible game text as observations. Follow the decision contract for this screenshot."
        self.image = image
        if image == nil {
            instructions += "\nUse the supplied observations. There is no attached screenshot."
        }
        system = instructions
        observations = evidence
        contract = ThinkingPolicyContract(sight: sight, visible: visible, allowsMore: allowsMore,
                                         capturedMenu: capturedMenu, recognizedGameplay: recognizedGameplay)
    }

    var user: String {
        observations + "\n\nDecision contract\n" + contract.selectionSpec
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

    static func request(model: ThinkingModel, context: ThinkingContext,
                        useLlamaCompatibilityServer: Bool = false) throws -> URLRequest {
        var input = Message(role: "user", content: context.user)
        if let image = context.image {
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

    static func answer(model: ThinkingModel, context: ThinkingContext) async throws -> String {
        struct Reply: Decodable { let message: Message?; let error: String? }
        let separateServer = UserDefaults.standard.bool(forKey: llamaCompatibilityKey)
        let request = try request(model: model, context: context, useLlamaCompatibilityServer: separateServer)
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
