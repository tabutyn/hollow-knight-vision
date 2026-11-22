import Accelerate
import CryptoKit
import Foundation

enum BotRuntimeError: LocalizedError {
    case incompatible(String)
    var errorDescription: String? {
        switch self { case .incompatible(let message): message }
    }
}

struct BotAction: Equatable, Codable {
    let values: [Int]
    static let neutral = try! BotAction(values: [0, 0, 0, 0])
    init(values: [Int]) throws {
        guard values.count == 4, zip(values, BotObservationContract.actionSizes).allSatisfy({ $0.0 >= 0 && $0.0 < $0.1 })
        else { throw BotRuntimeError.incompatible("Invalid bot action.") }
        self.values = values
    }
    var buttons: InputBridgeButtons {
        var result = InputBridgeButtons()
        if values[0] == 1 { result.insert(.left) }
        if values[0] == 2 { result.insert(.right) }
        if values[1] == 1 { result.insert(.up) }
        if values[1] == 2 { result.insert(.down) }
        if values[2] == 1 { result.insert(.actionZ) }
        if values[3] == 1 { result.insert(.actionX) }
        return result
    }
    var previousFeatures: [Float] {
        [values[0] == 0 ? 0 : values[0] == 1 ? -1 : 1,
         values[1] == 0 ? 0 : values[1] == 1 ? 1 : -1, Float(values[2]), Float(values[3])]
    }
    var displayName: String {
        var parts = [String]()
        if values[0] != 0 { parts.append(values[0] == 1 ? "Left" : "Right") }
        if values[1] != 0 { parts.append(values[1] == 1 ? "Up" : "Down") }
        if values[2] != 0 { parts.append("Jump") }
        if values[3] != 0 { parts.append("Attack") }
        return parts.isEmpty ? "Neutral" : parts.joined(separator: " + ")
    }
}

struct BotRuntimeLayer: Codable {
    let inputSize: Int
    let outputSize: Int
    let activation: String
    let weights: [Float] // row-major, PyTorch Linear [output, input]
    let biases: [Float]
}

struct BotRuntimePolicyDocument: Codable {
    let schemaVersion: Int
    let observationSchemaVersion: Int
    let modelName: String
    let featureNames: [String]
    let historyLength: Int
    let actionSizes: [Int]
    let sourceCheckpointSHA256: String
    let layers: [BotRuntimeLayer]
    var supportsMaskedKnight: Bool? = nil
    var recoveryManeuver: BotRecoveryManeuver? = nil
    var pitPolicy: BotPitPolicy? = nil
    var atlasPolicy: BotAtlasDirectionPolicy? = nil
    var trainedOnCombat: Bool? = nil
}

struct BotNativePolicy {
    let document: BotRuntimePolicyDocument
    init(document: BotRuntimePolicyDocument) throws {
        guard document.schemaVersion == 1,
              let names = BotObservationContract.policyNames(for: document.observationSchemaVersion),
              document.featureNames == names,
              document.historyLength == BotObservationContract.historyLength,
              document.actionSizes == BotObservationContract.actionSizes,
              (1...4).contains(document.layers.count)
        else { throw BotRuntimeError.incompatible("The bot model uses a different observation contract.") }
        try Self.validate(document.layers, inputs: names.count * BotObservationContract.historyLength,
                          outputs: BotObservationContract.actionSizes.reduce(0, +),
                          maximumWidth: document.observationSchemaVersion == BotObservationContract.landingGeometryPolicyVersion
                            ? 512 + BotLandingGeometry.names.count : 512)
        if let pit = document.pitPolicy {
            guard pit.schemaVersion == 1, document.recoveryManeuver == nil else {
                throw BotRuntimeError.incompatible("Invalid learned pit policy contract.")
            }
            try Self.validate(pit.layers, inputs: 8, outputs: 5)
        }
        if let atlas = document.atlasPolicy {
            guard atlas.schemaVersion == 1 else { throw BotRuntimeError.incompatible("Unknown Atlas policy version.") }
            try Self.validate(atlas.layers, inputs: 8, outputs: 3)
        }
        self.document = document
    }

    private static func validate(_ layers: [BotRuntimeLayer], inputs: Int, outputs: Int, maximumWidth: Int = 512) throws {
        guard (1...4).contains(layers.count) else { throw BotRuntimeError.incompatible("Invalid bot network depth.") }
        var expected = inputs
        for (index, layer) in layers.enumerated() {
            let final = index == layers.count - 1
            guard layer.inputSize == expected, (1...maximumWidth).contains(layer.outputSize),
                  layer.weights.count == layer.inputSize * layer.outputSize,
                  layer.biases.count == layer.outputSize,
                  layer.weights.allSatisfy(\.isFinite), layer.biases.allSatisfy(\.isFinite),
                  layer.activation == (final ? "linear" : "tanh")
            else { throw BotRuntimeError.incompatible("The bot model has invalid network parameters.") }
            expected = layer.outputSize
        }
        guard expected == outputs else {
            throw BotRuntimeError.incompatible("The bot model has incompatible controls.")
        }
    }

    static var defaultModelDirectory: URL {
        (try? BotModelStore().activeDirectory()) ?? BotModelStore().models.appendingPathComponent("v1")
    }

    static func load(from directory: URL) throws -> Self {
        struct RuntimeManifest: Decodable {
            let schemaVersion: Int
            let policyFile: String
            let policySHA256: String
            let sourceCheckpointSHA256: String
        }
        let manifest = try JSONDecoder().decode(RuntimeManifest.self,
            from: Data(contentsOf: directory.appendingPathComponent("runtime-manifest.json")))
        guard manifest.schemaVersion == 1, manifest.policyFile == "runtime-policy.json" else {
            throw BotRuntimeError.incompatible("The bot runtime manifest is incompatible.")
        }
        let url = directory.appendingPathComponent(manifest.policyFile)
        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
        guard size <= 16 * 1_024 * 1_024 else { throw BotRuntimeError.incompatible("The bot model is too large.") }
        let data = try Data(contentsOf: url)
        let checkpoint = try Data(contentsOf: directory.appendingPathComponent("model.zip"))
        guard sha256(data) == manifest.policySHA256, sha256(checkpoint) == manifest.sourceCheckpointSHA256 else {
            throw BotRuntimeError.incompatible("Bot model verification failed. Export the selected checkpoint again.")
        }
        let document = try JSONDecoder().decode(BotRuntimePolicyDocument.self, from: data)
        guard document.sourceCheckpointSHA256 == manifest.sourceCheckpointSHA256 else {
            throw BotRuntimeError.incompatible("The native policy belongs to a different checkpoint.")
        }
        if let pit = document.pitPolicy {
            let checkpoint = try Data(contentsOf: directory.appendingPathComponent("pit-model.zip"))
            guard sha256(checkpoint) == pit.checkpointSHA256 else {
                throw BotRuntimeError.incompatible("Pit recovery checkpoint verification failed.")
            }
        }
        if let atlas = document.atlasPolicy {
            let weights = try Data(contentsOf: directory.appendingPathComponent("atlas-model.zip"))
            guard sha256(weights) == atlas.checkpointSHA256 else {
                throw BotRuntimeError.incompatible("Atlas direction checkpoint verification failed.")
            }
        }
        return try Self(document: document)
    }

    static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    func logits(for input: [Float]) throws -> [Float] {
        let history = BotObservationContract.historyLength
        let width = document.featureNames.count
        guard (input.count == width * history || (width < BotObservationContract.featureCount && input.count == BotObservationContract.featureCount * history)),
              input.allSatisfy({ $0.isFinite && abs($0) <= 4 }) else {
            throw BotRuntimeError.incompatible("Invalid bot observation.")
        }
        // A v1 policy sees its original prefix at each of the four time steps.
        let vector = input.count == width * history ? input : (0..<history).flatMap { step in
            input[(step * BotObservationContract.featureCount)...].prefix(width)
        }
        return try Self.forward(vector, layers: document.layers)
    }

    static func forward(_ input: [Float], layers: [BotRuntimeLayer]) throws -> [Float] {
        guard input.count == layers.first?.inputSize, input.allSatisfy(\.isFinite) else {
            throw BotRuntimeError.incompatible("Invalid bot network input.")
        }
        var vector = input
        for layer in layers {
            var result = layer.biases
            layer.weights.withUnsafeBufferPointer { weights in
                vector.withUnsafeBufferPointer { values in
                    result.withUnsafeMutableBufferPointer { output in
                        cblas_sgemv(CblasRowMajor, CblasNoTrans, Int32(layer.outputSize), Int32(layer.inputSize),
                            1, weights.baseAddress!, Int32(layer.inputSize), values.baseAddress!, 1,
                            1, output.baseAddress!, 1)
                    }
                }
            }
            if layer.activation == "tanh" { result = result.map { tanhf($0) } }
            guard result.allSatisfy(\.isFinite) else { throw BotRuntimeError.incompatible("Non-finite bot output.") }
            vector = result
        }
        return vector
    }

    func action(for input: [Float]) throws -> BotAction {
        try Self.action(from: logits(for: input))
    }

    static func action(from logits: [Float]) throws -> BotAction {
        guard logits.count == 10, logits.allSatisfy(\.isFinite) else {
            throw BotRuntimeError.incompatible("Invalid bot control scores.")
        }
        var offset = 0
        var values = [Int]()
        for size in BotObservationContract.actionSizes {
            var best = 0
            for index in 1..<size where logits[offset + index] > logits[offset + best] { best = index }
            values.append(best)
            offset += size
        }
        return try BotAction(values: values)
    }
}

/// Same padding and segmentation as the demonstration exporter. The previous
/// input is the controller's actual last action, never the action being predicted.
struct BotObservationHistory {
    private var history = [[Float]]()
    private var generation: UInt64?
    private var roomID: Int?
    private var timestamp: Double?

    mutating func reset() { history = []; generation = nil; roomID = nil; timestamp = nil }

    mutating func input(_ observation: BotObservation, previousAction: BotAction, now: Double,
                        maskedKnightDeadline: Double? = nil, includeScreenTerrain: Bool = false, includeBlockade: Bool = false,
                        includeGeoCaches: Bool = false, includeAtlas: Bool = false, includeAtlasMap: Bool = false, includeRoad: Bool = false,
                        includePickup: Bool = false, includeCocoons: Bool = false, includeDoors: Bool = false,
                        includeSharedPath: Bool = false, includeCompactGeometry: Bool = false, compactGeometryVersion: Int = 14) -> [Float]? {
        guard observation.isUsable || maskedKnightDeadline.map({ now < $0 }) == true,
              observation.features.count == BotObservationContract.featureCount,
              observation.features.allSatisfy({ $0.isFinite && abs($0) <= 4 }),
              now.isFinite, observation.availableAt.isFinite, observation.capturedAt.isFinite,
              (0...0.25).contains(now - observation.availableAt),
              now < observation.knightPredictionDeadline,
              (0...BotObservationContract.maximumCaptureAge).contains(now - observation.capturedAt)
        else { reset(); return nil }
        let continuous = generation == observation.generation && roomID == observation.roomID
            && timestamp.map { observation.availableAt > $0 && observation.availableAt - $0 <= 0.35 } == true
        if !continuous { history = [] }
        var values = observation.features.map(Float.init)
        values[8] = continuous ? 0 : 1
        values.replaceSubrange(9..<13, with: continuous ? previousAction.previousFeatures : [0, 0, 0, 0])
        if includeBlockade, let obstacle = observation.blockade {
            values.replaceSubrange(13..<16, with: [Float(obstacle.dx), Float(obstacle.dy), Float(obstacle.mark.direction)])
        }
        if includeCompactGeometry {
            values = Array(values[0..<16]) + Array(values[96..<144])
        } else if includeScreenTerrain {
            values += BotTerrainGrid.features(observation.screenTerrain)
        }
        func relativePosition(_ objects: [BotObjectBounds]?) -> [Float] {
            guard let object = observation.nearestObject(in: objects) else { return [0, 0, 0] }
            return [Float(object.x - observation.features[0]), Float(object.y - observation.features[1]), 1]
        }
        if includeGeoCaches { values += relativePosition(observation.geoCaches) }
        if includeAtlas {
            if let view = observation.atlasView, view.left.isFinite, view.right.isFinite {
                let health = view.health.map { Float(min(16, max(0, $0))) / 16 }
                values += [1, Float(min(1, max(0, view.left))), Float(min(1, max(0, view.right))),
                           health == nil ? 0 : 1, health ?? 0]
            } else { values += [0, 0, 0, 0, 0] }
        }
        if includeAtlasMap {
            if let grid = observation.atlasView?.missing, grid.count == 15,
               grid.allSatisfy({ $0.isFinite && (0...1).contains($0) }) {
                values += [1] + grid.map(Float.init)
            } else { values += Array(repeating: 0, count: 16) }
        }
        if includeRoad { values += observation.roadHint?.features ?? [0, 0, 0, 0, 0, 0] }
        if includePickup && !includeCompactGeometry { values += relativePosition(observation.joniBlessings) }
        if includeCocoons { values += relativePosition(observation.lifebloodCocoons) }
        if includeDoors && !includeCompactGeometry { values += BotTerrainGrid.doorFeatures(observation.screenTerrain) }
        if includeSharedPath {
            if observation.hackerPathVersion == 1, let hint = observation.roadHint, hint.dx != 0 || hint.dy != 0 {
                values += [1, Float(hint.dx), Float(hint.dy)]
            } else {
                values += [0, 0, 0]
            }
        }
        if includeCompactGeometry {
            guard values.count == BotObservationContract.compactScalarIndices.count else { reset(); return nil }
            let markings = BotCompactMarkings.features(observation.screenTerrain,
                    knightX: observation.features[0], knightY: observation.features[1], version: compactGeometryVersion)
            values += markings
            if compactGeometryVersion >= BotObservationContract.groundContactPolicyVersion {
                values.append(observation.grounded.map { $0 ? 1 : -1 } ?? 0)
            }
            if compactGeometryVersion == BotObservationContract.landingGeometryPolicyVersion {
                let prior = history.last.flatMap { $0.count == values.count + BotLandingGeometry.names.count
                    ? $0.suffix(BotLandingGeometry.names.count) : nil }
                values += BotLandingGeometry.features(markings: markings, height: values[3],
                                                       grounded: observation.grounded, previous: prior)
            }
        }
        history.append(values)
        if history.count > BotObservationContract.historyLength { history.removeFirst() }
        generation = observation.generation; roomID = observation.roomID; timestamp = observation.availableAt
        return [Float](repeating: 0, count: (BotObservationContract.historyLength - history.count)
            * values.count) + history.flatMap { $0 }
    }
}

/// Transport ownership survives a perception gap, but a held action does not.
/// Old session tokens can neither renew nor release a newer control session.
struct BotInputLease {
    private(set) var sessionID: UUID?
    private(set) var validUntil: Double = 0
    mutating func begin() -> UUID { let id = UUID(); sessionID = id; validUntil = 0; return id }
    mutating func renew(sessionID id: UUID, until deadline: Double, now: Double) -> Bool {
        guard sessionID == id, now.isFinite, deadline.isFinite, deadline > now, deadline - now <= 0.35 else { return false }
        validUntil = deadline
        return true
    }
    func hasFreshDecision(at now: Double) -> Bool { sessionID != nil && now.isFinite && now < validUntil }
    mutating func neutralize(sessionID id: UUID) { if sessionID == id { validUntil = 0 } }
    mutating func end() { sessionID = nil; validUntil = 0 }
}
