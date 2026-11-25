import Foundation

enum ThinkingInventory {
    struct Resident {
        let model: ThinkingModel
        let server: URL
        let contextLength: Int?
    }

    // Query the local runtimes before submitting work. Merely opening the Notebook
    // must not pull a model from disk or evict a player's current Thinking model.
    static func resident(preferred: ThinkingModel) async throws -> Resident? {
        struct Item: Decodable { let name: String; let context_length: Int? }
        struct Inventory: Decodable { let models: [Item] }
        let separate = UserDefaults.standard.bool(forKey: ThinkingClient.llamaCompatibilityKey)
        let primary = ThinkingClient.primary, compatible = ThinkingClient.compatible
        let servers = !separate ? [primary] : preferred == .llama ? [compatible, primary] : [primary, compatible]
        var reachable = false
        var fallback: Resident?
        for server in servers {
            try Task.checkCancellation()
            let query = URLRequest(url: server.appendingPathComponent("ps"), timeoutInterval: 2)
            guard let (data, response) = try? await OllamaHTTP.session.data(for: query),
                  (response as? HTTPURLResponse)?.statusCode == 200,
                  let list = try? JSONDecoder().decode(Inventory.self, from: data) else { continue }
            reachable = true
            for item in list.models {
                guard let model = ThinkingModel(rawValue: item.name) else { continue }
                let found = Resident(model: model, server: server, contextLength: item.context_length)
                if model == preferred { return found }
                if fallback == nil { fallback = found }
            }
        }
        guard reachable else { throw ThinkingClient.Failure.unavailable }
        return fallback
    }

}
