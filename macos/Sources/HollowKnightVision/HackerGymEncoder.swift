import Foundation

/// The same authored path supplies Gym guidance in both observation domains.
final class GymHackerPath {
    private let source: BotHackerObservationSource
    private let document: GroundTruthLineDocument
    private let paths: AtlasPathMap
    private var previousRoom: String?
    private let objective: CGPoint?
    private(set) var objectiveReachable = false

    init(labels: URL, connections: URL? = nil, objective: CGPoint? = nil) throws {
        self.objective = objective
        let ground = BotHumanGroundTerrainCache()
        ground.update(document: try GroundTruthLineStore(url: labels).load())
        source = BotHackerObservationSource(ground: ground)
        document = ground.snapshot
        paths = AtlasPathMap(store: AtlasPathConnectionStore(file: connections))
    }

    static func fromEnvironment(labels: URL) throws -> GymHackerPath {
        let env = ProcessInfo.processInfo.environment
        let connections = env["HKV_ATLAS_PATH_CONNECTIONS"].map { URL(fileURLWithPath: $0) }
        var objective: CGPoint?
        if env["HKV_GYM_EXIT_X"] != nil || env["HKV_GYM_EXIT_Y"] != nil {
            guard let x = env["HKV_GYM_EXIT_X"], let y = env["HKV_GYM_EXIT_Y"],
                  let px = Double(x), let py = Double(y), px.isFinite, py.isFinite else {
                throw BotRuntimeError.incompatible("Invalid fixed Gym exit objective.")
            }
            objective = CGPoint(x: px, y: py)
        }
        return try GymHackerPath(labels: labels, connections: connections, objective: objective)
    }

    func interrupt() {
        if let previousRoom { paths.interrupt(room: previousRoom) }
        previousRoom = nil
        objectiveReachable = false
    }

    func observe(_ sample: ReceiverGroundTruthSample, ticks: Int, direction: Int, frameSize: CGSize? = nil) -> BotObservation? {
        let t = Double(ticks) / 60
        let size = frameSize ?? CGSize(width: sample.projectionPixelWidth ?? 0, height: sample.projectionPixelHeight ?? 0)
        guard var row = source.makeObservation(sample, size: size, timestamp: t, observedAt: t, generation: 1,
                                                drive: direction, availableAt: t),
              let camera = GroundTruthCameraTransform(sample: sample, observedAt: t, frameSize: size) else {
            if let previousRoom { paths.interrupt(room: previousRoom) }
            previousRoom = nil
            return nil
        }
        let point = HackerAtlasCameraPlacement.cameraPosition(camera)
        let key = "scene/" + camera.sceneName
        if let previousRoom, previousRoom != key { paths.interrupt(room: previousRoom) }
        previousRoom = key
        paths.setAuthored(room: key, calibration: .init(scene: camera.sceneName, origin: .zero,
            scale: .init(x: camera.pixelsPerWorldUnitX, y: camera.pixelsPerWorldUnitY)), document: document)
        var objectivePlaced = objective == nil
        if let objective, camera.sceneName == "Tutorial_01" {
            objectivePlaced = paths.setFixedObjective(room: key, world: objective)
        }
        paths.observe(room: key, camera: CGPoint(x: point.x-size.width/2, y: point.y-size.height/2),
                      width: size.width, row: &row, grounded: sample.grounded)
        if objective != nil && camera.sceneName != "Tutorial_01" {
            row.roadHint = nil
            row.pathProgress = nil
            row.pathAlignment = nil
        }
        objectiveReachable = objective == nil || (objectivePlaced && !paths.hasUnroutedObjective(room: key))
        row.hackerPathVersion = 1
        return row
    }
}

/// Headless play has the same mod camera and actors as Hacker capture. Each encoder
/// serves one episode and builds its own map of camera footprints in room coordinates.
enum HackerGymEncoder {
    private struct Frame: Decodable {
        let sample: ReceiverGroundTruthSample
        let previous: [Int]
        let health, ticks: Int
    }

    static func run(_ arguments: [String]) throws {
        if arguments.count == 3, arguments[0] == "verify" {
            struct Fixture: Decodable { let inputs, logits: [[Float]]; let actions: [[Int]] }
            let policy = try BotNativePolicy.load(from: URL(fileURLWithPath: arguments[1]))
            let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: URL(fileURLWithPath: arguments[2])))
            guard !fixture.inputs.isEmpty, fixture.actions.count == fixture.inputs.count,
                  fixture.logits.count == fixture.inputs.count else { throw BotRuntimeError.incompatible("Empty parity fixture.") }
            for i in fixture.inputs.indices {
                let logits = try policy.logits(for: fixture.inputs[i])
                guard logits.count == fixture.logits[i].count, zip(logits, fixture.logits[i]).allSatisfy({ abs($0 - $1) < 0.001 }),
                      try BotNativePolicy.action(from: logits).values == fixture.actions[i] else {
                    throw BotRuntimeError.incompatible("Hacker Gym native export disagrees with the training policy.")
                }
            }
            return
        }
        guard (2...3).contains(arguments.count), let version = Int(arguments[0]),
              BotObservationContract.policyNames(for: version) != nil else {
            throw BotRuntimeError.incompatible("Expected the checkpoint observation version and ground labels path.")
        }
        let path = try GymHackerPath.fromEnvironment(labels: URL(fileURLWithPath: arguments[1]))
        var history = BotObservationHistory(), blockades = BotBlockadeVisit()
        var covered = Set<BotHackerAtlasCoverage.Cell>()
        var origin: CGPoint?
        while let line = readLine() {
            let frame = try JSONDecoder().decode(Frame.self, from: Data(line.utf8))
            let sample = frame.sample, t = Double(frame.ticks) / 60
            let action = try BotAction(values: frame.previous)
            let direction = Int(action.previousFeatures[0])
            let size = CGSize(width: sample.projectionPixelWidth ?? 0, height: sample.projectionPixelHeight ?? 0)
            var response: [String: Any] = ["issues": ["missing-camera"]]
            if var row = path.observe(sample, ticks: frame.ticks, direction: direction),
               let camera = GroundTruthCameraTransform(sample: sample, observedAt: t, frameSize: size) {
                let point = HackerAtlasCameraPlacement.cameraPosition(camera)
                if origin == nil { origin = point }
                let pose = CGPoint(x: point.x - (origin?.x ?? point.x), y: point.y - (origin?.y ?? point.y))
                let cell = BotAtlasCoverage.cellSize
                // As in the tile compositor, a grid cell is known when its center is inside the frame.
                for y in Int(ceil(pose.y / cell - 0.5))..<Int(ceil((pose.y + size.height) / cell - 0.5)) {
                    for x in Int(ceil(pose.x / cell - 0.5))..<Int(ceil((pose.x + size.width) / cell - 0.5)) {
                        covered.insert(.init(x: x, y: y))
                    }
                }
                row.atlasView = BotAtlasCoverage.frontiers(pose: pose, width: size.width, height: size.height,
                    covered: covered.count, earned: 0, revision: frame.ticks, health: frame.health) {
                        covered.contains(.init(x: $0, y: $1))
                    }
                row.blockade = blockades.observe(row, pose: row.blockadePose, direction: direction)
                response = ["issues": row.issues, "pathReachable": path.objectiveReachable]
                if let progress = row.pathProgress { response["pathProgress"] = progress }
                if let alignment = row.pathAlignment { response["pathAlignment"] = alignment }
                if let input = history.input(row, previousAction: action, now: t,
                    includeScreenTerrain: version >= BotObservationContract.screenPolicyVersion,
                    includeBlockade: version >= BotObservationContract.blockadePolicyVersion,
                    includeGeoCaches: version >= BotObservationContract.geoPolicyVersion,
                    includeAtlas: version >= BotObservationContract.generalPolicyVersion,
                    includeAtlasMap: version >= BotObservationContract.atlasMapVersion,
                    includeRoad: version >= BotObservationContract.roadVersion,
                    includePickup: version >= BotObservationContract.pickupVersion,
                    includeCocoons: version >= BotObservationContract.cocoonVersion,
                    includeDoors: version >= BotObservationContract.doorVersion,
                    includeSharedPath: version >= BotObservationContract.sharedPathVersion,
                    includeCompactGeometry: version >= BotObservationContract.compactGeometryPolicyVersion, compactGeometryVersion: version) {
                    response["input"] = input.withUnsafeBytes { Data($0).base64EncodedString() }
                }
            } else { history.reset() }
            let output = try JSONSerialization.data(withJSONObject: response)
            FileHandle.standardOutput.write(output + Data([10]))
        }
    }
}
