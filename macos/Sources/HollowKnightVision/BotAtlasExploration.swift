import CoreGraphics
import Foundation

struct BotAtlasDirectionPolicy: Codable {
    let schemaVersion: Int
    let checkpointSHA256: String
    let layers: [BotRuntimeLayer]

    func direction(_ input: [Float]) throws -> Int {
        guard input.count == 8 else { throw BotRuntimeError.incompatible("Invalid Atlas direction inputs.") }
        let mirrored = input[0] > input[1] || (input[0] == input[1] && input[7] < 0)
        let features = mirrored ? [input[1], input[0], input[3], input[2], input[5], input[4], input[6], -input[7]] : input
        let logits = try BotNativePolicy.forward(features, layers: layers)
        let direction = logits.indices.max { logits[$0] < logits[$1] } ?? 0
        return mirrored ? [0, 2, 1][direction] : direction
    }
}

/// Sample the inner part of the committed visual Atlas on a 64-solve-pixel grid.
/// A graph correction can move existing evidence. Only new, locally continuous
/// observations can earn points, and the same cell earns at most once per map.
struct BotAtlasCoverage {
    struct Cell: Hashable { let room, x, y: Int }
    private struct Placement: Equatable {
        let origin: CGPoint
        let room: Int
        let bounds: VisualRoomCompositionBounds
    }
    private var prior = [Int: Placement]()
    private var occupied = Set<Cell>()
    private var seen = Set<Cell>()
    private var islandPosition = [Int: CGPoint]()
    private var revision: Int?
    private(set) var earned = 0
    var count: Int { occupied.count }

    static let cellSize = 64.0
    mutating func update(_ snapshot: LiveWorldSnapshot, bounds: (Int) -> VisualRoomCompositionBounds) {
        guard revision != snapshot.mapRevision else { return }
        let baseline = revision == nil
        revision = snapshot.mapRevision
        var placements = [Int: Placement]()
        let roomBounds = Dictionary(uniqueKeysWithValues: Set(snapshot.observations.map(\.roomID)).map { ($0, bounds($0)) })
        for row in snapshot.observations {
            placements[row.id] = Placement(origin: row.optimizedPose, room: row.roomID, bounds: roomBounds[row.roomID]!)
        }
        let corrected = prior.contains { placements[$0.key] != $0.value }
        if corrected { occupied.removeAll(keepingCapacity: true); islandPosition.removeAll(keepingCapacity: true) }
        let owners = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.flatMap { island in
            island.members.map { ($0.observationID, (island.id, $0.measured)) }
        })
        // Corrected existing contributions must establish the baseline before any new credit.
        let rows = snapshot.observations
        if corrected {
            for row in rows where prior[row.id] != nil {
                add(row, bounds: roomBounds[row.roomID]!, owner: owners[row.id], credit: false)
            }
        }
        for row in rows where prior[row.id] == nil {
            add(row, bounds: roomBounds[row.roomID]!, owner: owners[row.id], credit: !baseline)
        }
        prior = placements
    }

    private mutating func add(_ row: LiveWorldObservation, bounds: VisualRoomCompositionBounds,
                              owner: (Int, Bool)?, credit: Bool) {
        let pose = row.optimizedPose
        guard let owner, pose.x.isFinite, pose.y.isFinite, abs(pose.x) < 1e8, abs(pose.y) < 1e8,
              row.solveWidth > 0, row.solveWidth <= 8192, row.sourceWidth > 0 else { return }
        let last = islandPosition[owner.0]
        if owner.1 { islandPosition[owner.0] = pose }
        let moved = last.map { hypot($0.x - pose.x, $0.y - pose.y) } ?? .infinity
        let canEarn = credit && owner.1 && moved > 1 && moved <= row.solveWidth * 0.5
        let scale = row.solveWidth / Double(row.sourceWidth)
        let h = Double(row.sourceHeight) * scale
        let left = max(pose.x, bounds.minimumWorldX ?? pose.x)
        let right = min(pose.x + row.solveWidth, bounds.maximumWorldX ?? (pose.x + row.solveWidth))
        guard right > left, h > 0, h <= 8192 else { return }
        let size = Self.cellSize
        for x in Int(floor(left / size))...Int(floor(right / size)) {
            let wx = (Double(x) + 0.5) * size
            guard wx >= left, wx < right else { continue }
            for y in Int(floor((pose.y + h * 0.18) / size))...Int(floor((pose.y + h * 0.78) / size)) {
                let wy = (Double(y) + 0.5) * size
                guard wy >= pose.y + h * 0.18, wy < pose.y + h * 0.78 else { continue }
                let local = CGPoint(x: (wx - pose.x) / scale, y: (wy - pose.y) / scale)
                guard !row.excludedRects.contains(where: { $0.contains(local) }) else { continue }
                let cell = Cell(room: row.roomID, x: x, y: y)
                occupied.insert(cell)
                if seen.insert(cell).inserted && canEarn { earned += 1 }
            }
        }
    }

    func view(pose: CGPoint, width: Double, height: Double, room: Int, health: Int?) -> BotAtlasView? {
        guard width > 0, height > 0, pose.x.isFinite, pose.y.isFinite,
              abs(pose.x) < 1e8, abs(pose.y) < 1e8 else { return nil }
        return Self.frontiers(pose: pose, width: width, height: height, covered: count, earned: earned,
                              revision: revision ?? 0, health: health) { x, y in
            occupied.contains(Cell(room: room, x: x, y: y))
        }
    }

    static func frontiers(pose: CGPoint, width: Double, height: Double, covered: Int, earned: Int, revision: Int,
                          health: Int?, contains: (Int, Int) -> Bool) -> BotAtlasView {
        let size = Self.cellSize
        let middle = Int(floor((pose.y + height / 2) / size))
        func proximity(_ direction: Int) -> Double {
            let edge = direction < 0 ? pose.x : pose.x + width
            let column = Int(floor(edge / size))
            for step in 1...64 {
                let x = column + direction * step
                let known = (-1...1).filter { contains(x, middle + $0) }.count
                if known < 2 { return 1 / (1 + Double(step - 1) / 5) }
            }
            return 0
        }
        // Local bins span 1.25 screens horizontally and 0.75 vertically on each side.
        // Every bin uses four cell queries in bottom-left Atlas coordinates.
        var missing = [Double]()
        for y in -1...1 {
            for x in -2...2 {
                var unknown = 0
                for oy in [-0.125, 0.125] {
                    for ox in [-0.125, 0.125] {
                        let wx = pose.x + width * (0.5 + Double(x) / 2 + ox)
                        let wy = pose.y + height * (0.5 + Double(y) / 2 + oy)
                        if !contains(Int(floor(wx / size)), Int(floor(wy / size))) { unknown += 1 }
                    }
                }
                missing.append(Double(unknown) / 4)
            }
        }
        return BotAtlasView(left: proximity(-1), right: proximity(1), camera: pose, width: width,
                            coveredCells: covered, earnedCells: earned, revision: revision, health: health, missing: missing)
    }
}

struct BotAtlasView: Codable, Equatable {
    let left, right: Double
    let camera: CGPoint
    let width: Double
    let coveredCells, earnedCells, revision: Int
    let health: Int?
    var missing: [Double]? = nil
}

struct BotAtlasScore: Codable {
    static let heldAttackCost = 0.05
    var newCells = 0
    var damage = 0
    var blueHealthGained = 0
    var dwellDecisions = 0
    var heldAttackDecisions = 0
    var pathProgress: Double? = nil
    var pathAlignment: Double? = nil
    var reward: Double {
        4 * ((pathProgress ?? 0) + 0.5 * (pathAlignment ?? 0)) + Double(blueHealthGained)
            - Double(damage * 32) - Double(dwellDecisions) * 0.12 - Double(heldAttackDecisions) * Self.heldAttackCost
    }

    init() {}
    private enum CodingKeys: String, CodingKey { case newCells, damage, blueHealthGained, dwellDecisions, heldAttackDecisions, pathProgress, pathAlignment }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        newCells = try values.decode(Int.self, forKey: .newCells)
        damage = try values.decode(Int.self, forKey: .damage)
        blueHealthGained = try values.decodeIfPresent(Int.self, forKey: .blueHealthGained) ?? 0
        dwellDecisions = try values.decode(Int.self, forKey: .dwellDecisions)
        heldAttackDecisions = try values.decodeIfPresent(Int.self, forKey: .heldAttackDecisions) ?? 0
        pathAlignment = try values.decodeIfPresent(Double.self, forKey: .pathAlignment)
        pathProgress = try values.decodeIfPresent(Double.self, forKey: .pathProgress)
    }
}

struct BotAtlasRun {
    private var priorGeneration: UInt64?
    private var priorEarned: Int?
    private var anchor: CGPoint?
    private var entered = 0.0
    private var room: Int?
    private var lastTime = -Double.infinity
    private var lastBlueHealthOutcome: BotBlueHealthOutcomes?
    private var lastHealth: Int?
    private var candidateHealth: Int?
    private var healthSince = 0.0
    private(set) var score = BotAtlasScore()

    mutating func recordAction(_ action: BotAction, after previous: BotAction) {
        if previous.buttons.contains(.actionX) && action.buttons.contains(.actionX) {
            score.heldAttackDecisions += 1
        }
    }

    mutating func observe(_ row: BotObservation, view: BotAtlasView?, creditProgress: Bool = true) {
        if creditProgress, row.hackerPathVersion == 1, let signed = row.pathAlignment, (-3...3).contains(signed) {
            score.pathAlignment = (score.pathAlignment ?? 0) + signed
        }
        if creditProgress, let progress = row.pathProgress, progress.isFinite, (0...100).contains(progress) {
            score.pathProgress = (score.pathProgress ?? 0) + progress
        }
        if let current = row.blueHealthOutcomes, current.gained >= 0 {
            if let prior = lastBlueHealthOutcome, prior.epoch == current.epoch {
                if current.gained >= prior.gained {
                    score.blueHealthGained += current.gained - prior.gained
                    lastBlueHealthOutcome = current
                }
            } else { lastBlueHealthOutcome = current }
        } else { lastBlueHealthOutcome = nil }
        guard let view else { anchor = nil; candidateHealth = nil; priorEarned = nil; return }
        let time = row.captureTimestamp
        let visible = row.features[4] > 0
        let position = CGPoint(x: view.camera.x + view.width * (visible ? row.features[0] : 0.5),
                               y: view.camera.y + view.width * (visible ? row.features[1] : 0.28))
        let continuous = priorGeneration == row.generation && room == row.roomID && (0...0.6).contains(time - lastTime)
        if !continuous {
            anchor = nil; priorEarned = nil; lastHealth = nil; candidateHealth = nil
        }
        if let priorEarned { score.newCells += max(0, view.earnedCells - priorEarned) }
        priorGeneration = row.generation
        priorEarned = view.earnedCells
        room = row.roomID; lastTime = time
        if anchor.map({ hypot($0.x - position.x, $0.y - position.y) >= 1.25 * BotAtlasCoverage.cellSize }) ?? true {
            anchor = position; entered = time
        }
        let dwell = max(0, time - entered)
        if dwell >= 2 { score.dwellDecisions += 1 }
        if let health = view.health {
            if candidateHealth != health { candidateHealth = health; healthSince = time }
            if time - healthSince >= 0.22 {
                if let lastHealth { score.damage += max(0, lastHealth - health) }
                lastHealth = health
            }
        } else { candidateHealth = nil }
    }
}

/// Sample committed Hacker atlas pixels, including restored tiles. Room layout and
/// the current camera share the same room-local coordinate system.
struct BotHackerAtlasCoverage {
    struct Cell: Hashable { let x, y: Int }
    private var cells = Set<Cell>()
    private var tileImages = [Int: CGImage]()
    private var priorCamera: CGPoint?
    private(set) var earned = 0
    private var revision = 0
    private let cellPixels = Int(BotAtlasCoverage.cellSize)

    mutating func update(_ snapshot: LiveTiledAtlasSnapshot, camera: CGPoint? = nil, width: Double = 0) {
        let moved = priorCamera.flatMap { old in camera.map { hypot($0.x - old.x, $0.y - old.y) } }
        let credit = moved.map { $0 > 1 && $0 <= width * 0.5 } ?? false
        priorCamera = camera
        for tile in snapshot.tiles where tileImages[tile.id] !== tile.image {
            // Compositor tiles use top-down RGBA bytes and bottom-left world bounds.
            let image = tile.image
            guard image.bitsPerPixel == 32, image.alphaInfo == .premultipliedLast,
                  let data = image.dataProvider?.data, let bytes = CFDataGetBytePtr(data),
                  CFDataGetLength(data) >= image.bytesPerRow * image.height else { continue }
            tileImages[tile.id] = image
            for localY in stride(from: cellPixels / 2, to: image.height, by: cellPixels) {
                for localX in stride(from: cellPixels / 2, to: image.width, by: cellPixels) {
                    let alpha = bytes[(image.height - 1 - localY) * image.bytesPerRow + localX * 4 + 3]
                    guard alpha >= 128 else { continue }
                    let cell = Cell(x: Int(floor((tile.worldBounds.minX + Double(localX)) / Double(cellPixels))),
                                    y: Int(floor((tile.worldBounds.minY + Double(localY)) / Double(cellPixels))))
                    if cells.insert(cell).inserted && credit { earned += 1 }
                }
            }
        }
        revision += 1
    }

    func registered(_ cell: Cell) -> Bool { cells.contains(cell) }

    func view(camera: CGPoint, size: CGSize, health: Int?) -> BotAtlasView? {
        guard !cells.isEmpty, size.width > 0, size.height > 0, camera.x.isFinite, camera.y.isFinite,
              abs(camera.x) < 1e8, abs(camera.y) < 1e8 else { return nil }
        let view = BotAtlasCoverage.frontiers(pose: camera, width: size.width, height: size.height,
            covered: cells.count, earned: earned, revision: revision, health: health) { cells.contains(.init(x: $0, y: $1)) }
        // Express displacement/dwell in the same 640-wide units as the visual training clips.
        let scale = 640 / size.width
        return .init(left: view.left, right: view.right, camera: CGPoint(x: camera.x * scale, y: camera.y * scale),
                     width: 640, coveredCells: view.coveredCells, earnedCells: earned, revision: revision, health: health, missing: view.missing)
    }
}
