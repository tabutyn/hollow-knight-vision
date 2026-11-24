import SwiftUI

private final class LocalOnlyDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

enum OllamaHTTP {
    static let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.connectionProxyDictionary = [:]
        config.timeoutIntervalForResource = 900
        return URLSession(configuration: config, delegate: LocalOnlyDelegate(), delegateQueue: nil)
    }()

    static func post(_ url: URL, body: [String: Any], timeout: TimeInterval = 900) async throws -> Data {
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            let message = json?["error"] as? String
            throw ThinkingClient.Failure.server(message ?? "Ollama returned HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0).")
        }
        return data
    }
}

extension ThinkingClient {
    typealias LoadReport = @MainActor @Sendable (String) -> Void
    static let keepAlive = "30m"

    static func load(_ model: ThinkingModel, report: @escaping LoadReport = { _ in }) async throws {
        let separate = UserDefaults.standard.bool(forKey: llamaCompatibilityKey)
        let server = separate && model == .llama ? compatible : primary
        await report("Checking resident models")
        if separate {
            try await releaseOtherModels(on: model == .llama ? primary : compatible,
                candidates: model == .llama ? [.gemma, .qwen] : [.llama], report: report)
        }
        try await releaseOtherModels(on: server, candidates: Set(ThinkingModel.allCases).subtracting([model]), report: report)
        try Task.checkCancellation()
        await report("Waiting for Ollama to load the model")
        let diagnostics = Task { await OllamaLoadLog.monitor(server: server, model: model, report: report) }
        defer { diagnostics.cancel() }
        let data: Data
        do {
            data = try await OllamaHTTP.post(server.appendingPathComponent("generate"), body: [
                "model": model.rawValue, "stream": false, "keep_alive": keepAlive, "options": ["num_ctx": ThinkingModel.policyContextLength]])
        } catch let error as URLError where server == compatible && error.code == .cannotConnectToHost {
            throw Failure.server("Couldn’t reach the local Ollama service for Llama Vision. Check that it is running and has access to the external model drive, then retry.")
        }
        struct Receipt: Decodable { let done: Bool; let done_reason: String? }
        let receipt = try JSONDecoder().decode(Receipt.self, from: data)
        guard receipt.done, receipt.done_reason != "unload" else { throw Failure.server("Ollama did not confirm the model load.") }
    }
}

@MainActor
final class ThinkingModelSession: ObservableObject {
    enum State: Equatable {
        case idle, loading(ThinkingModel), ready(ThinkingModel), unloaded(ThinkingModel), failed(ThinkingModel, String)
    }
    @Published private(set) var state = State.idle
    @Published private(set) var model = ThinkingModel.startup
    @Published private(set) var loadDetail = ""
    @Published private(set) var loadLayers: String?
    @Published private(set) var loadBuffers: String?
    @Published private(set) var loadStarted = Date()
    @Published private(set) var lastLoadSeconds: TimeInterval?
    @Published var answering = false
    private var loadTask: Task<Void, Never>?
    let isEnabled: Bool
    var isReady: Bool { state == .ready(model) }

    init(isEnabled: Bool) { self.isEnabled = isEnabled }

    func checkResidency(of target: ThinkingModel) async {
        guard state == .ready(target), !answering else { return }
        do {
            let found = try await GameTextCleanup.resident(preferred: target)
            guard !Task.isCancelled, state == .ready(target), !answering else { return }
            if found?.model != target { state = .unloaded(target) }
        } catch { /* A failed poll gives no evidence that the loaded model stopped. */ }
    }

    func select(_ target: ThinkingModel, force: Bool = false,
                load: ((ThinkingModel) async throws -> Void)? = nil) {
        guard isEnabled else { return }
        if !force, state == .loading(target) || state == .ready(target) { return }
        loadTask?.cancel()
        model = target
        loadStarted = Date()
        loadDetail = "Connecting to local Ollama"
        loadLayers = nil
        loadBuffers = nil
        lastLoadSeconds = nil
        state = .loading(target)
        loadTask = Task {
            do {
                if let load { try await load(target) }
                else {
                    try await ThinkingClient.load(target) { [weak self] step in
                        guard !Task.isCancelled, self?.state == .loading(target) else { return }
                        if step.hasPrefix("Offloaded ") { self?.loadLayers = step }
                        else if step.hasPrefix("Mapped buffers") { self?.loadBuffers = step }
                        else { self?.loadDetail = step }
                    }
                }
                try Task.checkCancellation()
                lastLoadSeconds = Date().timeIntervalSince(loadStarted)
                state = .ready(target)
            } catch {
                guard !Task.isCancelled else { return }
                state = .failed(target, error.localizedDescription)
            }
        }
    }

    func waitUntilReady(for requested: ThinkingModel) async throws {
        try Task.checkCancellation()
        guard isEnabled, model == requested else { throw CancellationError() }
        if state == .ready(requested) { return }
        if state != .loading(requested) { select(requested) }
        while state == .loading(requested), let load = loadTask {
            await load.value
            try Task.checkCancellation()
            guard model == requested else { throw CancellationError() }
        }
        if case .failed(_, let reason) = state { throw ThinkingClient.Failure.server(reason) }
        guard isReady else { throw ThinkingClient.Failure.unavailable }
    }

    func answer(model requested: ThinkingModel, image: CGImage, context: ThinkingContext) async throws -> String {
        guard isEnabled else { throw CancellationError() }
        func submit() async throws -> String {
            try await ThinkingClient.answer(model: requested, image: image, context: context)
        }
        do { return try await submit() }
        catch let failure as ThinkingClient.Failure where failure.shouldReload {
            try Task.checkCancellation()
            guard model == requested else { throw CancellationError() }
            select(requested, force: true)
            try await waitUntilReady(for: requested)
            return try await submit()
        }
    }
}

struct ThinkingLoadingOverlay: View {
    @ObservedObject var session: ThinkingModelSession

    var body: some View {
        if !session.isReady {
            VStack(spacing: 8) {
                if case .failed(_, let reason) = session.state {
                    Text(reason).font(.caption).multilineTextAlignment(.center)
                    Button("Retry model") { session.select(session.model, force: true) }
                } else {
                    ProgressView().controlSize(.small)
                    Text("Loading…").font(.headline)
                    Text(session.loadDetail).font(.caption)
                    if let layers = session.loadLayers { Text(layers).font(.caption.monospacedDigit()) }
                }
            }
            .padding(16)
            .frame(maxWidth: 360)
            .background(.black.opacity(0.8), in: RoundedRectangle(cornerRadius: 10))
        }
    }
}
