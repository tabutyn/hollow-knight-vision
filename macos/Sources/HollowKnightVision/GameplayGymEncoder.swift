import CoreGraphics
import Foundation

enum GameplayGymEncoder {
    // Only pixels, elapsed simulation time and already applied buttons enter visual perception.
    private struct Frame: Decodable {
        let ticks, width, height: Int
        let previous: [Int]
        let sample: ReceiverGroundTruthSample
    }

    static func readImage(_ pixels: URL, width: Int, height: Int) throws -> CGImage {
        guard width == 640, height == 360 else { throw BotRuntimeError.incompatible("Unexpected Gym image dimensions.") }
        let bytes = try Data(contentsOf: pixels)
        let stride = width * 3
        guard bytes.count == stride * height else { throw BotRuntimeError.incompatible("Incomplete RGB frame.") }
        var data = Data(capacity: bytes.count)
        for y in (0..<height).reversed() { data.append(bytes[(y * stride)..<(y * stride + stride)]) }
        guard let provider = CGDataProvider(data: data as CFData),
              let image = CGImage(width: width, height: height, bitsPerComponent: 8,
                bitsPerPixel: 24, bytesPerRow: stride, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: [], provider: provider, decode: nil, shouldInterpolate: false,
                intent: .defaultIntent) else { throw BotRuntimeError.incompatible("Could not read RGB frame.") }
        return image
    }

    static func run(_ arguments: [String]) throws {
        guard (3...6).contains(arguments.count), let version = Int(arguments[0]),
              BotObservationContract.policyNames(for: version) != nil,
              let masked = Bool(arguments[2]) else {
            throw BotRuntimeError.incompatible("Expected observation schema, frame path and masked Knight support.")
        }
        let pixels = URL(fileURLWithPath: arguments[1])
        let root = pixels.deletingLastPathComponent().appendingPathComponent("visual-world", isDirectory: true)
        let model = LiveCaptureModel(visualReplayMode: true, gameplayGym: true, worldRootOverride: root)
        let lock = NSLock()
        var result: Result<Void, Error>?
        DispatchQueue.global(qos: .userInitiated).async {
            let outcome = Result {
                model.prepareVisualReplay(startTimestamp: 1, createdAt: Date(), sourcePathID: UUID())
                func override(_ index: Int) -> URL? {
                    guard index < arguments.count, arguments[index] != "-" else { return nil }
                    return URL(fileURLWithPath: arguments[index])
                }
                let active = try model.prepareGameplayGym(compiledDirectory: pixels.deletingLastPathComponent().appendingPathComponent("encoder-tmp"),
                    confirmationURL: override(3), knightURL: override(4), depositURL: override(5))
                guard let labels = ProcessInfo.processInfo.environment["HKV_GYM_LABELS"] else {
                    throw BotRuntimeError.incompatible("Missing Gym authored terrain labels.")
                }
                let path = try GymHackerPath.fromEnvironment(labels: URL(fileURLWithPath: labels))
                var history = BotObservationHistory()
                let tracePath = ProcessInfo.processInfo.environment["HKV_ATLAS_PATH_TRACE"] == "1"
                while let line = readLine() {
                    try autoreleasepool {
                        let frame = try JSONDecoder().decode(Frame.self, from: Data(line.utf8))
                        guard (0...10_000_000).contains(frame.ticks), frame.width == 640, frame.height == 360 else {
                            throw BotRuntimeError.incompatible("Unexpected visual Gym frame dimensions or clock.")
                        }
                        let image = try readImage(pixels, width: frame.width, height: frame.height)
                        let action = try BotAction(values: frame.previous)
                        var response: [String: Any] = ["issues": ["waiting-for-visual-frame"]]
                        let hackerPath = path.observe(frame.sample, ticks: frame.ticks, direction: Int(action.previousFeatures[0]))
                        response["pathReachable"] = path.objectiveReachable
                        if var row = try model.observeGameplayGym(image, ticks: frame.ticks, previous: action) {
                            if version >= BotObservationContract.sharedPathVersion {
                                row.hackerPathVersion = 1
                                row.policyScene = hackerPath?.policyScene
                                row.roadHint = hackerPath?.roadHint
                            }
                            row.pathProgress = hackerPath?.pathProgress
                            row.pathAlignment = hackerPath?.pathAlignment
                            response = ["pathReachable": path.objectiveReachable, "issues": row.issues, "knight": row.knightSource ?? "missing",
                                "floors": row.detectedTerrain?.floors.count ?? 0, "walls": row.detectedTerrain?.walls.count ?? 0,
                                "atlas": row.atlasView != nil, "healthKnown": row.atlasView?.health != nil]
                            if let confidence = row.bestKnightConfidence { response["knightConfidence"] = confidence }
                            if row.knightPrediction?.usedWeakDetection == true { response["weakKnightCorrection"] = true }
                            if let health = row.atlasView?.health { response["visibleMasks"] = health }
                            if tracePath {
                                response["pathDebug"] = DispatchQueue.main.sync {
                                    model.visualPaths.diagnostic(room:model.visualPathRoom)
                                }
                            }
                            if let progress = row.pathProgress { response["pathProgress"] = progress }
                            if let alignment = row.pathAlignment { response["pathAlignment"] = alignment }
                            if let input = history.input(row, previousAction: action, now: row.capturedAt,
                                maskedKnightDeadline: masked ? BotKnightOcclusionWindow.deadline(for: row) : nil,
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
                        response["crawlidLocalizerActive"] = active.crawlid
                        response["knightLocalizerActive"] = active.knight
                        response["depositLocalizerActive"] = active.deposit
                        FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: response) + Data([10]))
                    }
                }
            }
            lock.withLock { result = outcome }
        }
        while lock.withLock({ result == nil }) { RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.01)) }
        try lock.withLock { try result!.get() }
    }
}
