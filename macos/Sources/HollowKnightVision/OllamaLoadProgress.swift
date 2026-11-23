import Foundation

/// Ollama's public load API acknowledges completion. The local service logs
/// also identify weight mapping, GPU placement, and vision warm-up on this Mac.
struct OllamaLoadLog {
    private let blob: String
    private var matchingRunner = false
    private var partial = ""
    private var metalMiB = 0.0
    private var cpuMiB = 0.0

    init(blob: String) { self.blob = blob }

    mutating func consume(_ chunk: String) -> [String] {
        let lines = (partial + chunk).components(separatedBy: "\n")
        partial = String((lines.last ?? "").suffix(4096))
        var updates = [String]()
        let before = (metalMiB, cpuMiB)
        for line in lines.dropLast() {
            if line.contains("loading model via llama-server") || line.contains("srv    load_model: loading model '") {
                matchingRunner = line.contains(blob)
                if matchingRunner { metalMiB = 0; cpuMiB = 0; updates.append("Opening model weights") }
            }
            guard matchingRunner else { continue }
            if line.contains("load_tensors: loading model tensors") { updates.append("Mapping model weights into memory") }
            if let range = line.range(of: #"offloaded [0-9]+/[0-9]+ layers to GPU"#, options: .regularExpression) {
                updates.append("Offloaded " + line[range].dropFirst("offloaded ".count))
            }
            if line.contains("load_tensors:"),
               let match = line.range(of: #"Mapped model buffer size\s*=\s*[0-9.]+ MiB"#, options: .regularExpression),
               let amount = line[match].split(whereSeparator: \.isWhitespace).dropLast().last.flatMap({ Double($0) }) {
                if line.contains("MTL") { metalMiB += amount }
                if line.contains("CPU_Mapped") { cpuMiB += amount }
            }
            if line.contains("warming up the model with an empty run") { updates.append("Warming up the language model") }
            if line.contains("warmup with image size") { updates.append("Warming up the vision encoder") }
            if line.contains("srv    load_model: initializing, n_slots") { updates.append("Preparing the inference context") }
        }
        if before != (metalMiB, cpuMiB) && metalMiB + cpuMiB > 0 {
            updates.append(String(format: "Mapped buffers · %.2f GiB Metal · %.2f GiB CPU", metalMiB / 1024, cpuMiB / 1024))
        }
        return updates
    }

    static func monitor(server: URL, model: ThinkingModel, report: @escaping ThinkingClient.LoadReport) async {
        let filename = server == ThinkingClient.compatible ? "HollowKnightVision-ollama-llama-compat.log" : "HollowKnightVision-ollama.log"
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/" + filename)
        guard let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        guard (try? handle.seekToEnd()) != nil else { return }
        struct Info: Decodable { let modelfile: String }
        guard let data = try? await OllamaHTTP.post(server.appendingPathComponent("show"), body: ["model": model.rawValue], timeout: 4),
              let info = try? JSONDecoder().decode(Info.self, from: data),
              let from = info.modelfile.components(separatedBy: .newlines).first(where: { $0.hasPrefix("FROM ") }),
              let digest = from.range(of: #"sha256-[0-9a-fA-F]{64}"#, options: .regularExpression) else { return }
        var log = Self(blob: String(from[digest]))
        while !Task.isCancelled {
            if let bytes = try? handle.read(upToCount: 65_536), !bytes.isEmpty {
                for update in log.consume(String(decoding: bytes, as: UTF8.self)) {
                    guard !Task.isCancelled else { return }
                    await report(update)
                }
            }
            do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
        }
    }
}
