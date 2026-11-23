import CoreGraphics
import Foundation

extension PathPlatform {
    init?(stroke: GroundHypothesisAtlasLine) {
        guard PathPoint.supports(stroke.atlasStart), PathPoint.supports(stroke.atlasEnd) else { return nil }
        let a = PathPoint(stroke.atlasStart), b = PathPoint(stroke.atlasEnd)
        guard a.y == b.y, a.x < b.x else { return nil }
        self.init(left: a.x, right: b.x, y: a.y)
    }
}

private extension PathWall {
    /// A verified crossing clears the Knight's body height at this x. A wall
    /// can still bound another platform above or below that crossing.
    func outsideClearance(_ floors: [PathPlatform], height: Int, width: Int) -> [PathWall] {
        let margin = max(1, width / 2)
        let crossings = floors.filter {
            $0.left + margin <= x && x <= $0.right - margin && $0.y < high && $0.y + height > low
        }.sorted { $0.y < $1.y }
        guard !crossings.isEmpty else { return [self] }
        var parts = [PathWall]()
        var bottom = low
        for floor in crossings {
            if bottom < floor.y {
                parts.append(.init(x: x, low: bottom, high: floor.y, solidSide: solidSide, confirmedByContact: confirmedByContact))
            }
            bottom = max(bottom, floor.y + height)
        }
        if bottom < high {
            parts.append(.init(x: x, low: bottom, high: high, solidSide: solidSide, confirmedByContact: confirmedByContact))
        }
        return parts
    }
}

struct AtlasPathCalibration: Codable, Equatable {
    let scene: String
    let origin, scale: CGPoint
    var valid: Bool {
        PathPoint.supports(origin) && (0.001...10_000).contains(scale.x) && (0.001...10_000).contains(scale.y)
    }
    func local(_ world: CGPoint) -> CGPoint {
        CGPoint(x: origin.x + world.x * scale.x, y: origin.y + world.y * scale.y)
    }
}

/// A map owns the graph, grounded visits and objective for one capture mode. Geometry
/// is room-local. Display placement is applied only at the viewport boundary.
final class AtlasPathMap {
    private final class Room {
        var base = [PathPlatform](), seen = [PathPlatform]()
        var savedWalk = [PathPlatform](), projectedWalk: [PathPlatform]?
        var walked: [PathPlatform] { projectedWalk ?? savedWalk }
        var authoredWalls = [PathWall](), seenWalls = [PathWall]()
        var wallHistory = VisualPathWallHistory()
        var exposedFloors = [PathPlatform]()
        var handoffSeams = [PathPlatform]()
        var spikes = [PathPlatform]()
        var doors = [(PathPoint, PathPoint)]()
        var calibration: AtlasPathCalibration?
        var authored: [GroundTruthWorldLine] = []
        var links = [PathConnection]()
        var graph: AtlasPlatformGraph?
        var displayStrokes = [PathStroke]()
        var pathBounds = CGRect.null
        var navigation = AtlasPathNavigation()
        var exploration = AtlasPathNavigation()
        var usesDefaultObjective = true
        var lastPose: (feet: PathPoint, grounded: Bool?, width: CGFloat, time: Double)?
        var revision = 0
        var height = 0, width = 0
        var sampledSize = false
        var dirty = true
        var evidenceSpace: String?
        var archiveCoverage: BotHackerAtlasCoverage?
        var visible = Set<BotHackerAtlasCoverage.Cell>()
        var lastCameraExtent: [Int] = []
        var lastFrameTime: Double?
        var lastMeasured = false

    }
    private var rooms = [String: Room]()
    private struct ChartSample {
        let room: String
        let island: LiveIslandCapture
        let originIsRegistered: Bool
        let generation: UInt64
        let scale, time: Double
    }
    private var lastChart: ChartSample?
    private var handoff: (source: ChartSample, floors: [PathPlatform], time: Double)?
    private var promoted = Set<String>()
    private struct UnplacedProgress {
        var charts: [String: Double] = [:]
        var best = 0.0
    }
    private var unplacedProgress: [String: UnplacedProgress] = [:]
    private let store: AtlasPathConnectionStore
    init(store: AtlasPathConnectionStore = .init()) { self.store = store }
    var onChange: (() -> Void)?
    func reset() {
        rooms.removeAll()
        lastChart = nil
        handoff = nil
        promoted.removeAll()
        unplacedProgress.removeAll()
        store.reload()
    }
    func calibration(room: String) -> AtlasPathCalibration? { rooms[room]?.calibration }
    private func state(_ key: String) -> Room {
        if let r = rooms[key] { return r }
        let r = Room()
        r.links = store.connections(room: key)
        rooms[key] = r
        return r
    }

    func setConnections(_ links: [PathConnection], room: String) {
        let r = state(room)
        guard r.links != links else { return }
        store.reload()
        r.links = links
        r.dirty = true
        onChange?()
    }

    /// Keep only a route's orientation when visual registration cannot prove a
    /// coordinate transform. The new graph must choose its own reachable goal.
    func inheritAutomaticDirection(from sourceKey: String, into destinationKey: String) {
        guard sourceKey != destinationKey, let source = rooms[sourceKey],
              source.navigation.automatic else { return }
        let destination = state(destinationKey)
        guard destination.navigation.automatic else { return }
        destination.navigation.inheritAutomaticDirection(from: source.navigation)
    }

    func interrupt(room: String) {
        rooms[room]?.lastPose = nil
        rooms[room]?.navigation.interrupt()
        rooms[room]?.exploration.interrupt()
    }

    /// A reacquired camera can put the same fight into a new local chart.
    /// Previous geometry only corroborates short gaps between measured tops
    /// in the new view. It never establishes a landing on its own.
    func handoffSeams(room: String, island: LiveIslandCapture, registered: Bool, projection: LiveIslandWorldProjection?,
                      scale: Double, generation: UInt64, time: Double) -> [PathPlatform] {
        guard island.measured else { return [] }
        let current = ChartSample(room: room, island: island, originIsRegistered: registered,
                                  generation: generation, scale: scale, time: time)
        defer { lastChart = current }
        guard let prior = lastChart, prior.generation == generation, prior.island.roomID == island.roomID,
              prior.scale == scale, (0...0.35).contains(time - prior.time) else { handoff = nil; return [] }
        if prior.room != room {
            handoff = nil
            if let seen = rooms[prior.room]?.seen, let graph = graph(room: prior.room) {
                // Only export the portion of a walk edge with evidence in
                // this chart. A new handoff cannot ratify an earlier guess.
                let witnessed = graph.floors.flatMap { floor in
                    seen.compactMap { edge -> PathPlatform? in
                        let left = max(edge.left, floor.left), right = min(edge.right, floor.right)
                        guard left < right, abs(edge.y - floor.y) <= 2 else { return nil }
                        return .init(left: left, right: right, y: floor.y)
                    }
                }
                handoff = (prior, witnessed, time)
            }
        }
        guard let handoff, (0...0.7).contains(time - handoff.time) else { self.handoff = nil; return [] }
        func origin(_ chart: ChartSample) -> CGPoint? {
            let capture = chart.island
            var pose = capture.worldPose
            if let projection {
                guard let adjusted = projection.worldPose(for: capture) else { return nil }
                pose = adjusted
            }
            return chart.originIsRegistered ? .zero : CGPoint(x: pose.x - capture.localPose.x, y: pose.y - capture.localPose.y)
        }
        guard let from = origin(handoff.source), let to = origin(current) else { return [] }
        let delta = CGPoint(x: (from.x - to.x) * scale, y: (from.y - to.y) * scale)
        guard PathPoint.supports(delta) else { return [] }
        let shift = PathPoint(delta)
        return handoff.floors.map { .init(left: $0.left + shift.x, right: $0.right + shift.x, y: $0.y + shift.y) }
    }

    /// Frame-local coordinates have no durable identity across a capture gap.
    /// Measured islands and registered room maps are kept separately.
    func discardFrameChart(room: String) {
        guard room.hasSuffix("/frame") else { return }
        rooms[room] = nil
    }

    /// Unregistered islands might contain the same floor. Until their relationship
    /// is measured the longest observed chart is a conservative lower bound on travel.
    func limitUnplacedCredit(room: String, chart: String, row: inout BotObservation) {
        guard let r = rooms[chart], r.navigation.automatic || r.usesDefaultObjective, let progress = row.pathProgress,
              progress.isFinite, progress > 0 else { return }
        var evidence = unplacedProgress[room,default:UnplacedProgress()]
        let total = evidence.charts[chart,default:0] + progress
        evidence.charts[chart] = total
        row.pathProgress = max(0,total-evidence.best)
        evidence.best = max(total,evidence.best)
        unplacedProgress[room] = evidence
    }

    /// Move one island's local evidence into a registered room exactly once.
    /// The caller supplies a translation only after the world solver establishes
    /// this exact island's identity and placement.
    func promote(from sourceKey: String, into destinationKey: String,
                 translation: PathPoint) {
        guard sourceKey != destinationKey, !promoted.contains(sourceKey),
              let source = rooms[sourceKey] else { return }
        let destination = state(destinationKey)
        let height = destination.height > 0 ? destination.height : source.height
        guard height > 0 else { return }
        if destination.height == 0 {
            destination.height = source.height
            destination.width = source.width
            destination.sampledSize = source.sampledSize
        }
        func moved(_ span: PathPlatform) -> PathPlatform {
            .init(left: span.left + translation.x, right: span.right + translation.x,
                  y: span.y + translation.y)
        }
        func moved(_ wall: PathWall) -> PathWall {
            .init(x: wall.x + translation.x, low: wall.low + translation.y,
                  high: wall.high + translation.y, solidSide: wall.solidSide, confirmedByContact: wall.confirmedByContact)
        }
        destination.seen = AtlasPlatformGraph.merge(destination.seen + source.seen.map(moved), height: height, bridgeSeams: false)
        destination.exposedFloors = AtlasPlatformGraph.merge(destination.exposedFloors + source.exposedFloors.map(moved),
                                                            height: height, bridgeSeams: false)
        destination.seenWalls += source.seenWalls.map(moved)
        destination.evidenceSpace = "registered"
        var incoming = source.navigation
        incoming.translate(by: translation, fromHeight: source.height, toHeight: height)
        let prior = destination.navigation
        if (!source.usesDefaultObjective && destination.usesDefaultObjective)
            || (!source.usesDefaultObjective && !incoming.automatic && prior.automatic) {
            incoming.mergeExploration(from: prior)
            destination.navigation = incoming
            destination.usesDefaultObjective = false
        } else {
            destination.navigation.mergeExploration(from: incoming)
        }
        destination.dirty = true
        promoted.insert(sourceKey)
        onChange?()
    }
    func graph(room: String) -> AtlasPlatformGraph? {
        guard let r = rooms[room], r.height > 0 else { return nil }
        if r.dirty {
            var floors = r.base + r.seen
            let walked = r.walked
            var seenWalls = r.seenWalls
            if r.calibration == nil {
                let id = LiveGroundLineEvidence.walkedSegmentID
                func stroke(_ f: PathPlatform) -> GroundHypothesisAtlasLine {
                    .init(segmentID: id, atlasStart: .init(x: f.left, y: f.y), atlasEnd: .init(x: f.right, y: f.y))
                }
                // Compare whole observations before subtracting walked columns.
                // Cutting both candidates first can turn a short combat edge
                // into a ledge at the endpoint of the walked stretch.
                let measured = GroundStrokeGeometry.coalesced(floors.map(stroke), clearanceHeight: CGFloat(r.height),
                                                              endpointTolerance: 3)
                floors = WalkedFloorCoverage(walked.map(stroke)).filtered(measured, clearance: CGFloat(r.height))
                    .compactMap(PathPlatform.init(stroke:))
                // A foreground silhouette can stay still for several images.
                // Over a previously clear top, require a collision or the upper
                // landing of a step before cutting the existing walk edge.
                let landings = floors + walked
                // The enemy mask can hide the middle of a previously measured
                // floor. Require exposed edges on both sides and an observation
                // spanning the seam. This only tests a wall's clearance.
                let exposed = seenWalls.isEmpty ? [] : AtlasPlatformGraph.stitchDetected(
                    r.exposedFloors, walls: [], height: r.height, width: r.width,
                    supportedSeams: r.seen, maximumSupportedGap: r.height, requireWitness: true)
                seenWalls = seenWalls.flatMap { wall -> [PathWall] in
                    if wall.confirmedByContact { return [wall] }
                    let hasLanding = landings.contains { landing in
                        let lip = wall.solidSide > 0 ? landing.left : landing.right
                        return landing.right - landing.left >= r.width && abs(lip - wall.x) <= max(3, r.width / 2)
                            && abs(landing.y - wall.high) <= max(3, r.height / 6)
                    }
                    return hasLanding ? [wall] : wall.outsideClearance(exposed, height: r.height, width: r.width)
                }
                if !walked.isEmpty {
                    seenWalls = seenWalls.flatMap { $0.outsideClearance(walked, height: r.height, width: r.width) }
                }
            }
            let graph = AtlasPlatformGraph(
                floors: floors, walls: r.authoredWalls + seenWalls,
                height: r.height, width: r.width, connections: r.links, worldScale: r.calibration?.scale,
                previous: r.graph, spikes: r.spikes, doors: r.doors, detectedGround: r.calibration == nil,
                walked: walked, handoffSeams: r.handoffSeams)
            r.graph = graph
            if r.usesDefaultObjective, !room.hasSuffix("/frame") {
                let target = graph.upperRightObjective
                if target != r.navigation.objective { r.navigation.setObjective(target) }
            }
            r.displayStrokes = Self.displayStrokes(graph.strokes)
            r.pathBounds = r.displayStrokes.reduce(CGRect.null) { bounds, line in
                let extent = CGRect(x: line.a.x, y: line.a.y, width: line.b.x - line.a.x, height: line.b.y - line.a.y)
                return bounds.union(extent.insetBy(dx: -1, dy: -1))
            }
            r.dirty = false
            r.revision += 1
        }
        return r.graph
    }

    func pathBounds(room: String) -> CGRect? {
        guard graph(room: room) != nil, let bounds = rooms[room]?.pathBounds, !bounds.isNull else { return nil }
        return bounds
    }

    /// A shaft can serve multiple landings. Draw its shared length and arrow
    /// once, while preserving each directed branch in the navigation graph.
    static func displayStrokes(_ strokes: [PathStroke]) -> [PathStroke] {
        struct Lane: Hashable {
            let horizontal: Bool
            let coordinate: Int
        }
        var result = strokes.filter { $0.kind != .wall && $0.kind != .drop }
        let drops = Dictionary(grouping: strokes.filter { $0.kind == .drop }) {
            Lane(horizontal: $0.horizontal, coordinate: $0.horizontal ? $0.a.y : $0.a.x)
        }
        let lanes = drops.keys.sorted { a, b in
            a.horizontal == b.horizontal ? a.coordinate < b.coordinate : a.horizontal
        }
        for key in lanes {
            guard let lane = drops[key] else { continue }
            var merged: PathStroke?
            for line in lane.sorted(by: { $0.a < $1.a }) {
                if let prior = merged, line.a <= prior.b {
                    merged = .init(a: prior.a, b: max(prior.b, line.b), kind: .drop, forward: prior.forward)
                } else {
                    if let merged { result.append(merged) }
                    merged = line
                }
            }
            if let merged { result.append(merged) }
        }
        return result
    }
    func setBase(room: String, floors: [PathPlatform], walked: [PathPlatform] = [], fallbackHeight: Int) {
        let r = state(room)
        if r.height == 0 {
            r.height = max(4, fallbackHeight)
            r.width = max(3, r.height / 2)
        }
        let compact = AtlasPlatformGraph.merge(floors, height: r.height, bridgeSeams: r.calibration != nil)
        if compact != r.base || walked != r.savedWalk {
            r.base = compact
            r.savedWalk = walked
            r.dirty = true
        }
    }

    func setAuthored(room: String, calibration: AtlasPathCalibration, document: GroundTruthLineDocument) {
        let r = state(room)
        guard calibration.valid else { return }
        let labels = document.authoredLines.filter {
            !$0.seededFromDetector && $0.sceneName == calibration.scene && $0.kind != .negative
        }
        guard r.calibration != calibration || labels != r.authored else { return }
        let old = r.calibration
        r.calibration = calibration
        r.authored = labels
        if old != calibration {
            r.links = store.connections(
                room: room, scene: calibration.scene, origin: calibration.origin, scale: calibration.scale)
        }
        var floors = [PathPlatform]()
        var walls = [PathWall]()
        var spikes = [PathPlatform]()
        var doors = [(PathPoint, PathPoint)]()
        for line in labels {
            let start = calibration.local(line.start)
            let end = calibration.local(line.end)
            guard PathPoint.supports(start), PathPoint.supports(end) else { continue }
            let a = PathPoint(start)
            let b = PathPoint(end)
            switch line.kind {
            case .positive: floors.append(.init(left: min(a.x, b.x), right: max(a.x, b.x), y: a.y))
            case .wall: walls.append(.init(x: a.x, low: min(a.y, b.y), high: max(a.y, b.y), solidSide: 0))
            case .negative: break
            case .spike: spikes.append(.init(left: min(a.x, b.x), right: max(a.x, b.x), y: a.y))
            case .door, .doorVertical: doors.append((a, b))
            }
        }
        r.authoredWalls = walls
        r.spikes = spikes
        r.doors = doors
        r.dirty = true
        if !r.sampledSize {
            r.width = max(3, Int((0.5 * calibration.scale.x).rounded()))
            r.height = max(4, Int((1.28 * calibration.scale.y).rounded()))
        }
        setBase(room: room, floors: floors, fallbackHeight: Int((1.28 * calibration.scale.y).rounded()))
    }

    func setRegisteredCoverage(room: String, coverage: BotHackerAtlasCoverage) {
        state(room).archiveCoverage = coverage
    }

    /// Hit-test the visible path itself. A room's image tiles can cover only a
    /// small part of its authored ground. The first room wins equal-distance hits.
    func selectObjective(at point: CGPoint, tolerance: CGFloat,
                         in placements: [(key: String, offset: CGPoint)]) -> String? {
        guard PathPoint.supports(point), tolerance.isFinite, (0...10000).contains(tolerance) else { return nil }
        let radius = max(2, Int(ceil(tolerance)))
        var hit: (key: String, point: PathPoint, distance: Int)?
        for (key, offset) in placements {
            let local = CGPoint(x: point.x - offset.x, y: point.y - offset.y)
            guard PathPoint.supports(local), let g = graph(room: key) else { continue }
            let click = PathPoint(local)
            guard let stroke = g.nearestStroke(click, within: radius) else { continue }
            let projected = stroke.closest(click)
            let distance = projected.distance(click)
            if let hit, hit.distance <= distance { continue }
            hit = (key, projected, distance)
        }
        guard let hit, let r = rooms[hit.key], let g = graph(room: hit.key) else { return nil }
        for (key, _) in placements where key != hit.key && rooms[key]?.navigation.automatic == false {
            rooms[key]?.navigation.setObjective(nil)
            rooms[key]?.usesDefaultObjective = false
        }
        r.usesDefaultObjective = false
        if !r.navigation.automatic, let selected = r.navigation.objective,
           selected.distance(hit.point) <= min(radius, max(2, g.knightWidth / 2)) {
            r.navigation.setObjective(nil)
        } else {
            r.navigation.setObjective(hit.point)
            if let pose = r.lastPose {
                // Recompute immediately, including while the game is paused.
                // A click is never a measured movement or training reward.
                _ = r.navigation.observe(g, feet: pose.feet, viewWidth: pose.width,
                                         timestamp: pose.time, measured: false, grounded: pose.grounded)
            }
        }
        onChange?()
        return hit.key
    }

    func selectObjective(room: String, point: CGPoint, tolerance: CGFloat) -> Bool {
        selectObjective(at: point, tolerance: tolerance, in: [(room, .zero)]) != nil
    }

    /// World is a measured hero position near the exit gate. Keep the goal fixed
    /// even when the current platform has no route to it.
    @discardableResult
    func setFixedObjective(room: String, world: CGPoint) -> Bool {
        let r = state(room)
        guard let calibration = r.calibration, PathPoint.supports(calibration.local(world)),
              let graph = graph(room: room) else { return false }
        let gate = PathPoint(calibration.local(world))
        let point = graph.nearestStroke(gate, within: 4 * graph.knightHeight, walkOnly: true)?.closest(gate)
        let goal = point ?? gate
        r.usesDefaultObjective = false
        if r.navigation.automatic || r.navigation.objective != goal { r.navigation.setObjective(goal) }
        return point != nil
    }

    func hasUnroutedObjective(room: String) -> Bool {
        guard let navigation = rooms[room]?.navigation else { return false }
        return !navigation.automatic && navigation.selectedRoute.isEmpty
    }

    /// Partial frames are unioned only in a consistent camera space. A new island
    /// replaces speculative evidence. Registered base ground follows the world solver.
    private func include(
        _ geometry: BotScreenTerrain, observedFloorEdges: [BotTerrainSegment], contactWall: BotWallSegment?, camera: CGPoint,
        space: String, time: Double, room r: Room
    ) {
        if r.evidenceSpace != space {
            r.seen = []
            r.seenWalls = []
            r.wallHistory = .init()
            r.exposedFloors = []
            r.projectedWalk = nil
            r.evidenceSpace = space
            r.dirty = true
            r.navigation.interrupt()
            r.exploration.interrupt()
        }
        func span(_ f: BotTerrainSegment) -> PathPlatform? {
            guard [f.minimumX, f.maximumX, f.y].allSatisfy(\.isFinite), f.minimumX < f.maximumX else { return nil }
            return .init(
                left: Int(floor(camera.x + f.minimumX)), right: Int(ceil(camera.x + f.maximumX)),
                y: Int((camera.y + f.y).rounded()))
        }
        // Keep compact extents. Existing longer strokes absorb registration jitter.
        func extend(_ current: inout [PathPlatform], _ samples: [PathPlatform]) -> Bool {
            let dy = max(2, min(6, r.height / 5))
            let additions = samples.filter { s in
                !current.contains {
                    abs($0.y - s.y) <= dy && $0.left <= s.left + 1 && $0.right >= s.right - 1
                }
            }
            guard !additions.isEmpty else { return false }
            let next = AtlasPlatformGraph.merge(current + additions, height: r.height, bridgeSeams: false)
            guard next != current else { return false }
            current = next
            return true
        }
        if extend(&r.seen, geometry.floors.compactMap(span)) { r.dirty = true }
        var walls = geometry.walls.compactMap { w -> PathWall? in
            guard [w.x, w.bottomY, w.topY].allSatisfy(\.isFinite), w.topY > w.bottomY else { return nil }
            return PathWall(
                x: Int((camera.x + w.x).rounded()), low: Int(floor(camera.y + w.bottomY)),
                high: Int(ceil(camera.y + w.topY)), solidSide: w.solidSide,
                confirmedByContact: contactWall.map { $0.solidSide == w.solidSide && abs($0.x - w.x) <= 3
                    && $0.bottomY < w.topY && w.bottomY < $0.topY } ?? false)
        }
        if r.calibration == nil {
            walls = r.wallHistory.observe(walls, floorEdges: observedFloorEdges.compactMap(span), time: time)
            if !observedFloorEdges.isEmpty {
                if extend(&r.exposedFloors, r.wallHistory.exposedFloors(height: r.height, width: r.width)) { r.dirty = true }
                let retained = r.seenWalls.flatMap { wall in
                    let clear = r.wallHistory.clearCrossings(of: wall, height: r.height, width: r.width)
                    return wall.outsideClearance(clear, height: r.height, width: r.width)
                }
                if retained != r.seenWalls { r.seenWalls = retained; r.dirty = true }
            }
        }
        for wall in walls {
            if let i = r.seenWalls.firstIndex(where: {
                abs($0.x - wall.x) <= 3 && $0.solidSide == wall.solidSide && $0.low <= wall.high + 2
                    && wall.low <= $0.high + 2
            }) {
                if wall.confirmedByContact && !r.seenWalls[i].confirmedByContact {
                    r.seenWalls[i].confirmedByContact = true
                    r.dirty = true
                }
                if wall.low < r.seenWalls[i].low - 1 || wall.high > r.seenWalls[i].high + 1 {
                    r.seenWalls[i].low = min(wall.low, r.seenWalls[i].low)
                    r.seenWalls[i].high = max(wall.high, r.seenWalls[i].high)
                    r.dirty = true
                }
            } else {
                r.seenWalls.append(wall)
                r.dirty = true
            }
        }
    }

    func observe(
        room: String, camera: CGPoint, width: CGFloat, row: inout BotObservation,
        geometry: BotScreenTerrain? = nil, walked: [PathPlatform]? = nil, observedFloorEdges: [BotTerrainSegment] = [],
        contactWall: BotWallSegment? = nil, handoffSeams: [PathPlatform] = [], space: String = "",
        grounded: Bool? = nil, trackPosition: Bool = true, replaceFrame: Bool = false
    ) {
        guard (1...32768).contains(width), PathPoint.supports(camera), row.features.count >= 5 else { return }
        row.atlasPathVersion = 1
        let r = state(room)
        if handoffSeams != r.handoffSeams { r.handoffSeams = handoffSeams; r.dirty = true }
        let f = row.features
        let measured = f[4] > 0 && row.knightPrediction == nil
        r.lastMeasured = measured && trackPosition
        if !r.sampledSize, measured, f[2].isFinite, f[3].isFinite,
            (0.006...0.15).contains(f[2]), (0.014...0.22).contains(f[3])
        {
            r.width = max(3, Int((width * f[2]).rounded()))
            r.height = max(5, Int((width * f[3]).rounded()))
            r.sampledSize = true
            r.dirty = true
        } else if r.height == 0 {
            r.height = max(5, Int(width * 0.055))
            r.width = max(3, Int(width * 0.028))
        }
        if let geometry {
            if replaceFrame {
                // Without a registered camera a screen pixel cannot own a persistent room objective.
                if row.captureTimestamp != r.lastFrameTime && !r.navigation.automatic {
                    r.navigation.setObjective(nil)
                }
                r.lastFrameTime = row.captureTimestamp
                r.seen = []
                r.projectedWalk = nil
                r.seenWalls = []
                r.wallHistory = .init()
                r.exposedFloors = []
                r.dirty = true
            }
            include(geometry, observedFloorEdges: observedFloorEdges, contactWall: contactWall,
                    camera: camera, space: space, time: row.captureTimestamp, room: r)
        }
        if let walked, walked != r.projectedWalk {
            r.projectedWalk = walked
            r.dirty = true
        }
        guard measured || row.knightPrediction != nil, f.prefix(5).allSatisfy(\.isFinite),
            !row.issues.contains("room-transition"), let g = graph(room: room)
        else {
            r.lastPose = nil
            r.navigation.interrupt()
            r.exploration.interrupt()
            return
        }
        // The mod's actor position is the hero transform pivot. In recorded
        // grounded frames it lies one collider height above the authored floor.
        // Visual detections instead supply the geometric center of their box.
        let aboveFeet = r.calibration == nil ? f[3] / 2 : f[3]
        let position = CGPoint(x: camera.x + width * f[0], y: camera.y + width * (f[1] - aboveFeet))
        guard PathPoint.supports(position) else {
            r.lastPose = nil
            r.navigation.interrupt()
            r.exploration.interrupt()
            return
        }
        let feet = PathPoint(position)
        if r.usesDefaultObjective, room.hasSuffix("/frame"), r.navigation.objective != g.upperRightObjective {
            r.navigation.setObjective(g.upperRightObjective)
        }
        r.lastPose = (feet, grounded, width, row.captureTimestamp)
        // Authored geometry may extend past the explored Atlas. Automatic
        // goals and routes use only registered camera footprints, including
        // previously saved tiles. A black but observed corridor is registered.
        let cell = BotAtlasCoverage.cellSize
        let height = row.authoredTerrain?.height ?? width * 9 / 16
        let hasCoverage = r.calibration != nil
        if hasCoverage {
            let lastX: CGFloat = camera.x + width - 1
            let lastY: CGFloat = camera.y + height - 1
            let firstColumn = Int(floor(camera.x / cell))
            let lastColumn = Int(floor(lastX / cell))
            let firstRow = Int(floor(camera.y / cell))
            let lastRow = Int(floor(lastY / cell))
            let extent = [firstColumn, lastColumn, firstRow, lastRow]
            if extent != r.lastCameraExtent {
                for y in firstRow...lastRow {
                    for x in firstColumn...lastColumn { r.visible.insert(.init(x: x, y: y)) }
                }
                r.lastCameraExtent = extent
            }
        }
        func registered(_ p: PathPoint) -> Bool {
            guard hasCoverage else { return true }
            let index = BotHackerAtlasCoverage.Cell(
                x: Int(floor(CGFloat(p.x) / cell)),
                y: Int(floor(CGFloat(p.y - g.knightHeight) / cell)))
            return r.visible.contains(index) || r.archiveCoverage?.registered(index) == true
        }
        (row.roadHint, row.pathProgress) = r.navigation.observe(
            g, feet: feet, viewWidth: width,
            timestamp: row.captureTimestamp, measured: measured && trackPosition, grounded: grounded,
            revision: r.revision, registered: registered)
        if r.calibration != nil { row.pathAlignment = r.navigation.alignment }
        if var scene = row.policyScene {
            func target(_ nav: AtlasPathNavigation, _ hint: BotRoadHint?) -> BotRouteTarget? {
                guard let hint, let goal = nav.goal, let waypoint = nav.next,
                      hint.dx != 0 || hint.dy != 0 else { return nil }
                return BotRouteTarget(chart: room, goal: goal, waypoint: waypoint, hint: hint)
            }
            if r.navigation.automatic {
                scene.routes = [target(r.navigation, row.roadHint)].compactMap { $0 }
            } else {
                let (frontier, _) = r.exploration.observe(g, feet: feet, viewWidth: width,
                    timestamp: row.captureTimestamp, measured: measured && trackPosition, grounded: grounded,
                    revision: r.revision, registered: registered)
                scene.routes = [target(r.exploration, frontier), target(r.navigation, row.roadHint)].compactMap { $0 }
            }
            row.policyScene = scene
        }
    }

    func diagnostic(room: String) -> [String:Any] {
        guard let r = rooms[room] else { return ["room":room] }
        var result: [String:Any] = ["room":room,"geometryRevision":r.revision,
            "automatic":r.navigation.automatic,"measured":r.lastMeasured]
        for (key,p) in [("objective",r.navigation.objective),("current",r.navigation.current),("next",r.navigation.next)] {
            if let p { result[key] = [p.x,p.y] }
        }
        return result
    }

    typealias Line = LayerSceneFeatureReviewOverlay.Line
    private func stroke(_ a: PathPoint, _ b: PathPoint, kind: LayerSceneFeatureReviewLineKind, offset: CGPoint)
        -> Line
    {
        .init(
            start: .init(x: CGFloat(a.x) + offset.x, y: CGFloat(a.y) + offset.y),
            end: .init(x: CGFloat(b.x) + offset.x, y: CGFloat(b.y) + offset.y), kind: kind)
    }
    func groundLines(room: String, offset: CGPoint = .zero) -> [Line] {
        guard let g = graph(room: room) else { return [] }
        return g.floors.map {
            stroke(.init($0.left, $0.y), .init($0.right, $0.y), kind: .positiveGroundTruth, offset: offset)
        }
    }
    func spikeLines(room: String, offset: CGPoint = .zero) -> [Line] {
        (rooms[room]?.spikes ?? []).map {
            stroke(.init($0.left, $0.y), .init($0.right, $0.y), kind: .spikeGroundTruth, offset: offset)
        }
    }

    func walkedGround(room: String, offset: CGPoint) -> [GroundHypothesisAtlasLine] {
        (rooms[room]?.walked ?? []).map { floor in
            let y = offset.y + CGFloat(floor.y)
            return .init(segmentID: LiveGroundLineEvidence.walkedSegmentID, atlasStart: .init(x: offset.x + CGFloat(floor.left), y: y),
                         atlasEnd: .init(x: offset.x + CGFloat(floor.right), y: y))
        }
    }
    func lines(room: String, offset: CGPoint = .zero, visible: Bool) -> [Line] {
        guard visible, let g = graph(room: room), let r = rooms[room] else { return [] }
        var lines = r.displayStrokes.flatMap { line -> [Line] in
            let kind: LayerSceneFeatureReviewLineKind
            switch line.kind {
            case .wall: return []
            case .doubleJump: kind = .atlasPathDoubleJump
            case .drop: kind = .atlasPathDrop
            default: kind = .atlasRoad
            }
            var result = [stroke(line.a, line.b, kind: kind, offset: offset)]
            // Room coordinates have their origin at the bottom left. Put the
            // arrowhead on the descending leg, away from either landing.
            if line.kind == .drop, line.a.x == line.b.x {
                let height = abs(line.b.y - line.a.y)
                if height >= 4 {
                    let size = min(max(4, g.knightWidth / 2), height / 2)
                    let tip = PathPoint(line.a.x, (line.a.y + line.b.y) / 2)
                    for side in [-1, 1] {
                        result.append(stroke(tip, .init(tip.x + side * size, tip.y + size), kind: kind, offset: offset))
                    }
                }
            }
            return result
        }
        if let target = r.navigation.objective {
            let size = max(4, g.knightWidth / 2)
            lines.append(
                stroke(
                    .init(target.x - size, target.y), .init(target.x + size, target.y), kind: .atlasPathObjective,
                    offset: offset))
            lines.append(
                stroke(
                    .init(target.x, target.y - size), .init(target.x, target.y + size), kind: .atlasPathObjective,
                    offset: offset))
        }
        let route = r.navigation.selectedRoute
        for (a, b) in zip(route, route.dropFirst()) {
            lines.append(stroke(a, b, kind: .atlasPathObjective, offset: offset))
        }
        if let p = r.navigation.current, let n = r.navigation.next, p != n {
            lines.append(stroke(p, n, kind: .atlasPathObjective, offset: offset))
            let length = min(max(3, g.knightWidth / 2), p.distance(n))
            let dx = (n.x - p.x).signum()
            let dy = (n.y - p.y).signum()
            for side in [-1, 1] {
                let end = PathPoint(
                    n.x - dx * length + side * dy * length / 2, n.y - dy * length + side * dx * length / 2)
                lines.append(stroke(n, end, kind: .atlasPathObjective, offset: offset))
            }
        }
        return lines
    }
}

extension AtlasPathCalibration {
    static func hacker(
        room: HackerAtlasRoom, document: GroundTruthLineDocument, camera: GroundTruthCameraTransform? = nil
    ) -> Self? {
        let scene = room.sceneName
        let known = document.roomProjections?[scene]
        if let camera, camera.sceneName == scene {
            return .init(
                scene: scene,
                origin: .init(
                    x: camera.frameSize.width / 2 - room.botCameraOrigin.x,
                    y: camera.frameSize.height / 2 - room.botCameraOrigin.y),
                scale: .init(x: camera.pixelsPerWorldUnitX, y: camera.pixelsPerWorldUnitY))
        }
        if let saved = room.pathCalibration, saved.scene == scene, saved.valid { return saved }
        guard let known, known.isValid else { return nil }
        let origin =
            known.roomID == room.id
            ? known.localOrigin : CGPoint(x: 320 - room.botCameraOrigin.x, y: 180 - room.botCameraOrigin.y)  // Legacy 640×360 capture archive.
        return .init(
            scene: scene, origin: origin, scale: .init(x: known.pixelsPerWorldUnitX, y: known.pixelsPerWorldUnitY))
    }
}
