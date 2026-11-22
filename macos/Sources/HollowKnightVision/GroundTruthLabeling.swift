import CoreGraphics
import Foundation
import SwiftUI

/// Development-only camera transform reported by the game mod. It is used by
/// the ground-label context and evaluation only; visual tracking never reads it.
struct GroundTruthCameraTransform: Equatable, Sendable {
    let sceneName: String
    let cameraWorld: CGPoint
    let pixelsPerWorldUnitX: CGFloat
    let pixelsPerWorldUnitY: CGFloat
    let frameSize: CGSize
    let observedAt: TimeInterval

    init?(
        sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval,
        frameSize: CGSize = CGSize(
            width: CGFloat(HollowKnightCaptureConfiguration.outputWidth),
            height: CGFloat(HollowKnightCaptureConfiguration.outputWidth)
                / HollowKnightCaptureConfiguration.gameplayAspectRatio
        )
    ) {
        guard sample.cameraAvailable,
              frameSize.width > 1, frameSize.height > 1,
              let projectionWidth = sample.projectionPixelWidth,
              let projectionHeight = sample.projectionPixelHeight,
              projectionWidth > 0, projectionHeight > 0 else { return nil }
        let rawY = sample.pixelsPerWorldUnitY
            ?? (sample.orthographicSize > 0
                ? Double(projectionHeight) / (2 * sample.orthographicSize) : 0)
        let rawX = sample.pixelsPerWorldUnitX ?? rawY
        let scaleX = CGFloat(rawX) * frameSize.width / CGFloat(projectionWidth)
        let scaleY = CGFloat(rawY) * frameSize.height / CGFloat(projectionHeight)
        guard scaleX.isFinite, scaleY.isFinite, scaleX > 0, scaleY > 0 else { return nil }
        sceneName = sample.sceneName
        cameraWorld = CGPoint(x: sample.cameraX, y: sample.cameraY)
        pixelsPerWorldUnitX = scaleX
        pixelsPerWorldUnitY = scaleY
        self.frameSize = frameSize
        self.observedAt = observedAt
    }

    init(
        sceneName: String,
        cameraWorld: CGPoint,
        pixelsPerWorldUnitX: CGFloat,
        pixelsPerWorldUnitY: CGFloat,
        frameSize: CGSize,
        observedAt: TimeInterval = 0
    ) {
        self.sceneName = sceneName
        self.cameraWorld = cameraWorld
        self.pixelsPerWorldUnitX = pixelsPerWorldUnitX
        self.pixelsPerWorldUnitY = pixelsPerWorldUnitY
        self.frameSize = frameSize
        self.observedAt = observedAt
    }

    func screenToWorld(_ topLeftPixel: CGPoint) -> CGPoint {
        CGPoint(
            x: cameraWorld.x + (topLeftPixel.x - frameSize.width * 0.5) / pixelsPerWorldUnitX,
            y: cameraWorld.y + (frameSize.height * 0.5 - topLeftPixel.y) / pixelsPerWorldUnitY
        )
    }

    func atlasToWorld(_ atlasPoint: CGPoint, liveBounds: CGRect) -> CGPoint {
        CGPoint(
            x: cameraWorld.x + (atlasPoint.x - liveBounds.midX) / pixelsPerWorldUnitX,
            y: cameraWorld.y + (atlasPoint.y - liveBounds.midY) / pixelsPerWorldUnitY
        )
    }

    func worldToAtlas(_ worldPoint: CGPoint, liveBounds: CGRect) -> CGPoint {
        CGPoint(
            x: liveBounds.midX + (worldPoint.x - cameraWorld.x) * pixelsPerWorldUnitX,
            y: liveBounds.midY + (worldPoint.y - cameraWorld.y) * pixelsPerWorldUnitY
        )
    }
}

enum GroundTruthLineKind: String, Codable, CaseIterable {
    case positive
    case negative
    case wall
    case spike
    case door
    case doorVertical

    var axis: TerrainLineAxis { (self == .wall || self == .doorVertical) ? .vertical : .horizontal }
}

enum TerrainLineAxis {
    case horizontal, vertical

    var perpendicular: Self { self == .horizontal ? .vertical : .horizontal }

    /// In editing coordinates x runs along the line and y runs across it.
    /// Swapping components is its own inverse.
    func components(_ point: CGPoint) -> CGPoint {
        self == .horizontal ? point : CGPoint(x: point.y, y: point.x)
    }
}

struct GroundTruthRoomPlacement: Equatable {
    let id: UUID
    let sceneName: String
    let position: CGPoint
    let bounds: CGRect

    var atlasBounds: CGRect { bounds.offsetBy(dx: position.x, dy: position.y) }
}

/// Maps a scene's game coordinates into its own room pixels. Moving the room
/// changes its placement, never this calibration or the saved ground lines.
struct GroundTruthRoomProjection: Codable, Equatable {
    let roomID: UUID
    let pixelsPerWorldUnitX: CGFloat
    let pixelsPerWorldUnitY: CGFloat
    let localOrigin: CGPoint

    init(room: GroundTruthRoomPlacement, transform: GroundTruthCameraTransform, liveBounds: CGRect) {
        roomID = room.id
        pixelsPerWorldUnitX = transform.pixelsPerWorldUnitX
        pixelsPerWorldUnitY = transform.pixelsPerWorldUnitY
        localOrigin = CGPoint(
            x: liveBounds.midX - room.position.x - transform.cameraWorld.x * pixelsPerWorldUnitX,
            y: liveBounds.midY - room.position.y - transform.cameraWorld.y * pixelsPerWorldUnitY)
    }

    var isValid: Bool {
        pixelsPerWorldUnitX.isFinite && pixelsPerWorldUnitY.isFinite
            && pixelsPerWorldUnitX > 0 && pixelsPerWorldUnitY > 0
            && localOrigin.x.isFinite && localOrigin.y.isFinite
    }

    func worldToAtlas(_ world: CGPoint, room: GroundTruthRoomPlacement) -> CGPoint {
        CGPoint(x: room.position.x + localOrigin.x + world.x * pixelsPerWorldUnitX,
                y: room.position.y + localOrigin.y + world.y * pixelsPerWorldUnitY)
    }

    func atlasToWorld(_ point: CGPoint, room: GroundTruthRoomPlacement) -> CGPoint {
        CGPoint(x: (point.x - room.position.x - localOrigin.x) / pixelsPerWorldUnitX,
                y: (point.y - room.position.y - localOrigin.y) / pixelsPerWorldUnitY)
    }
}

struct GroundTruthWorldLine: Codable, Equatable, Identifiable {
    var id = UUID()
    var sceneName: String
    var minimumWorldX: Double
    var maximumWorldX: Double
    var worldY: Double
    var kind: GroundTruthLineKind
    // Legacy provenance only. New lines are created exclusively by editor gestures.
    var seededFromDetector: Bool

    // Older horizontal labels have a single Y coordinate. Walls store their upper endpoint too.
    var endWorldY: Double? = nil
    var startCornerID: UUID? = nil
    var endCornerID: UUID? = nil
    var groundLatch: GroundTruthDoorLatch? = nil

    var start: CGPoint { CGPoint(x: minimumWorldX, y: worldY) }
    var end: CGPoint { CGPoint(x: maximumWorldX, y: endWorldY ?? worldY) }

    mutating func setExtent(from a: CGFloat, to b: CGFloat, across: CGFloat) {
        let start = kind.axis.components(CGPoint(x: min(a, b), y: across))
        let end = kind.axis.components(CGPoint(x: max(a, b), y: across))
        minimumWorldX = start.x
        maximumWorldX = end.x
        worldY = start.y
        endWorldY = kind.axis == .vertical ? end.y : nil
    }

    func overlayKind(selected: UUID?) -> LayerSceneFeatureReviewLineKind {
        if id == selected { return kind == .wall ? .selectedWallTruth : .selectedGroundTruth }
        if kind == .spike { return .spikeGroundTruth }
        if kind == .door || kind == .doorVertical { return .doorGroundTruth }
        return kind == .negative ? .negativeGroundTruth : .positiveGroundTruth
    }

}

struct GroundTruthLineDocument: Codable, Equatable {
    var schemaVersion = 2
    var seededScenes = Set<String>()
    var lines = [GroundTruthWorldLine]()
    var roomProjections: [String: GroundTruthRoomProjection]? = nil
    // Keep the v2 floor dataset readable by existing replay and training tools.
    var walls: [GroundTruthWorldLine]? = nil
    var spikes: [GroundTruthWorldLine]? = nil
    var doors: [GroundTruthWorldLine]? = nil

    var authoredLines: [GroundTruthWorldLine] {
        return lines + (walls ?? []) + (spikes ?? []) + (doors ?? [])
    }

    subscript(axis: TerrainLineAxis) -> [GroundTruthWorldLine] {
        get { axis == .vertical ? walls ?? [] : lines }
        set {
            if axis == .vertical { walls = newValue.isEmpty ? nil : newValue }
            else { lines = newValue }
        }
    }

    func validate() throws {
        let all = authoredLines
        guard schemaVersion == 2, all.count <= 20_000,
              lines.allSatisfy({ $0.kind == .positive || $0.kind == .negative }),
              (walls ?? []).allSatisfy({ $0.kind == .wall }), (spikes ?? []).allSatisfy({ $0.kind == .spike }),
              (doors ?? []).allSatisfy({ $0.kind == .door || $0.kind == .doorVertical }),
              seededScenes.allSatisfy({ !$0.isEmpty && $0.count <= 256 }),
              Set(all.map(\.id)).count == all.count else {
            throw GroundTruthLineStoreError.invalid("Invalid ground-label document")
        }
        guard (roomProjections ?? [:]).allSatisfy({ !$0.key.isEmpty && $0.value.isValid }) else {
            throw GroundTruthLineStoreError.invalid("Invalid room projection")
        }
        for line in all {
            if let latch = line.groundLatch {
                guard (line.kind == .door || line.kind == .doorVertical), (0...1).contains(latch.fraction),
                      lines.contains(where: { $0.id == latch.groundID && $0.kind == .positive && $0.sceneName == line.sceneName }) else {
                    throw GroundTruthLineStoreError.invalid("Invalid door attachment")
                }
            }
            guard !line.sceneName.isEmpty, line.sceneName.count <= 256,
                  [line.minimumWorldX, line.maximumWorldX, line.worldY, line.end.y]
                    .allSatisfy({ $0.isFinite && abs($0) <= 1_000_000 }),
                  line.maximumWorldX >= line.minimumWorldX,
                  line.maximumWorldX - line.minimumWorldX <= 100_000,
                  line.kind.axis == .vertical
                    ? (line.endWorldY != nil && line.start.x == line.end.x && line.end.y >= line.start.y
                        && line.end.y - line.start.y <= 100_000)
                    : line.endWorldY == nil else {
                throw GroundTruthLineStoreError.invalid("Invalid world-space ground line")
            }
        }
        try validateCorners()
    }
}

enum GroundTruthLineStoreError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        guard case let .invalid(message) = self else { return nil }
        return message
    }
}

struct GroundTruthLineStore {
    let url: URL

    static var defaultURL: URL {
        if let path = ProcessInfo.processInfo.environment["HKV_GROUND_LABEL_ROOT"] {
            let root = URL(fileURLWithPath: path, isDirectory: true)
            return root.appendingPathComponent("world-ground-lines-v2.json")
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HollowKnightVision/world-ground-lines-v2.json")
    }

    init(url: URL = Self.defaultURL) { self.url = url }

    func load() throws -> GroundTruthLineDocument {
        guard FileManager.default.fileExists(atPath: url.path) else {
            return GroundTruthLineDocument()
        }
        var document = try JSONDecoder().decode(
            GroundTruthLineDocument.self,
            from: Data(contentsOf: url)
        )
        try document.validate()
        // Recognize legacy doors already touching a floor (within one display
        // pixel) without rewriting the user's file just by opening it.
        for (i, door) in (document.doors ?? []).enumerated() where door.groundLatch == nil {
            let projection = document.roomProjections?[door.sceneName]
            let tolerance = CGPoint(x: 1 / (projection?.pixelsPerWorldUnitX ?? 100),
                                    y: 1 / (projection?.pixelsPerWorldUnitY ?? 100))
            let latch = document.nearestGroundLatch(for: door, ends: TerrainLineEnd.allCases, tolerance: tolerance)
            document.doors?[i].groundLatch = latch
        }
        document.updateLatchedDoors()
        return document
    }

    func save(_ document: GroundTruthLineDocument) throws {
        try document.validate()
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: url, options: .atomic)
    }
}

enum GroundTruthLineProjection {
    static func overlay(
        document: GroundTruthLineDocument,
        transform: GroundTruthCameraTransform,
        liveBounds: CGRect,
        selectedID: UUID?,
        draft: GroundTruthWorldLine?
    ) -> LayerSceneFeatureReviewOverlay {
        overlay(document: document, selectedID: selectedID, draft: draft) { scene, point in
            guard scene == transform.sceneName else { return nil }
            return transform.worldToAtlas(point, liveBounds: liveBounds)
        }
    }

    static func overlay(document: GroundTruthLineDocument, rooms: [GroundTruthRoomPlacement],
                        selectedID: UUID?, draft: GroundTruthWorldLine?) -> LayerSceneFeatureReviewOverlay {
        let byScene = Dictionary(rooms.map { ($0.sceneName, $0) }, uniquingKeysWith: { _, last in last })
        return overlay(document: document, selectedID: selectedID, draft: draft) { scene, point in
            guard let room = byScene[scene], let projection = document.roomProjections?[scene],
                  projection.roomID == room.id else { return nil }
            return projection.worldToAtlas(point, room: room)
        }
    }

    private static func overlay(document: GroundTruthLineDocument, selectedID: UUID?, draft: GroundTruthWorldLine?,
                                project: (String, CGPoint) -> CGPoint?) -> LayerSceneFeatureReviewOverlay {
        var result = LayerSceneFeatureReviewOverlay(markers: [], lines: [])
        var drawnCorners = Set<UUID>()
        for line in document.authoredLines + (draft.map { [$0] } ?? []) {
            guard let start = project(line.sceneName, line.start), let end = project(line.sceneName, line.end),
                  [start.x, start.y, end.x, end.y].allSatisfy(\.isFinite) else { continue }
            result.lines.append(.init(start: start, end: end, kind: line.overlayKind(selected: selectedID)))
            for endpoint in TerrainLineEnd.allCases {
                let kind: LayerSceneFeatureReviewMarkerKind
                if let corner = line.corner(at: endpoint) {
                    guard drawnCorners.insert(corner).inserted else { continue }
                    kind = .joinedTerrainCorner
                } else if line.id == selectedID {
                    kind = .selectedGroundTruthEndpoint
                } else {
                    kind = line.kind == .negative ? .negativeGroundTruthEndpoint
                        : line.kind == .spike ? .spikeGroundTruthEndpoint
                        : (line.kind == .door || line.kind == .doorVertical) ? .doorGroundTruthEndpoint : .positiveGroundTruthEndpoint
                }
                result.markers.append(.init(worldPosition: endpoint == .start ? start : end, kind: kind))
            }
        }
        return result
    }

    static func overlayLines(document: GroundTruthLineDocument, transform: GroundTruthCameraTransform,
                             liveBounds: CGRect, selectedID: UUID?, draft: GroundTruthWorldLine?)
        -> [LayerSceneFeatureReviewOverlay.Line] {
        overlay(document: document, transform: transform, liveBounds: liveBounds, selectedID: selectedID, draft: draft).lines
    }

}

enum LayerSceneGroundLabelPointerPhase { case began, changed, ended }

@MainActor
final class GroundTruthLabelController: ObservableObject {
    @Published var mode = LabelingEditorMode.add {
        didSet { if mode != oldValue { cancelGesture() } }
    }
    @Published private(set) var editingAxis: TerrainLineAxis = .horizontal
    @Published private(set) var editingSpikes = false
    @Published private(set) var editingDoors = false
    @Published private(set) var document: GroundTruthLineDocument
    @Published private(set) var selectedID: UUID?
    @Published private var preview: GroundTruthLineDocument?
    @Published private(set) var error: String?

    private struct Snapshot {
        let document: GroundTruthLineDocument
        let selectedID: UUID?
    }
    private enum ModificationPart { case endpoint(TerrainLineEnd), whole }
    private let store: GroundTruthLineStore
    private var history = [Snapshot]()
    private var original: GroundTruthWorldLine?
    private var modificationPart: ModificationPart?
    private var gestureStartWorld: CGPoint?
    private var newLine: GroundTruthWorldLine?
    private var startTarget: TerrainHandle?
    private var gestureTolerance = CGPoint.zero
    private var gestureRoomID: UUID?

    init(store: GroundTruthLineStore = GroundTruthLineStore()) {
        self.store = store
        do { document = try store.load() }
        catch {
            document = GroundTruthLineDocument()
            self.error = error.localizedDescription
        }
    }

    var canUndo: Bool { !history.isEmpty }
    func lineCount(in sceneName: String? = nil) -> Int {
        let lines = editingDoors ? (document.doors ?? []) : editingSpikes ? (document.spikes ?? []) : document[editingAxis]
        guard let sceneName else { return lines.count }
        return lines.count { $0.sceneName == sceneName }
    }

    func overlay(
        transform: GroundTruthCameraTransform?,
        liveBounds: CGRect
    ) -> LayerSceneFeatureReviewOverlay {
        guard let transform else { return .init(markers: [], lines: []) }
        return GroundTruthLineProjection.overlay(
            document: preview ?? document,
            transform: transform,
            liveBounds: liveBounds,
            selectedID: selectedID,
            draft: nil
        )
    }

    func registerRoom(_ room: GroundTruthRoomPlacement, transform: GroundTruthCameraTransform,
                      liveBounds: CGRect) {
        guard room.sceneName == transform.sceneName else { return }
        if let existing = document.roomProjections?[room.sceneName], existing.roomID == room.id,
           abs(existing.pixelsPerWorldUnitX - transform.pixelsPerWorldUnitX) < 0.001,
           abs(existing.pixelsPerWorldUnitY - transform.pixelsPerWorldUnitY) < 0.001 { return }
        let projection = GroundTruthRoomProjection(room: room, transform: transform, liveBounds: liveBounds)
        guard projection.isValid else { return }
        var projections = document.roomProjections ?? [:]
        projections[room.sceneName] = projection
        document.roomProjections = projections
        persist()
    }

    func overlay(rooms: [GroundTruthRoomPlacement]) -> LayerSceneFeatureReviewOverlay {
        GroundTruthLineProjection.overlay(document: preview ?? document, rooms: rooms,
            selectedID: selectedID, draft: nil)
    }

    func handle(phase: LayerSceneGroundLabelPointerPhase, atlasPoint: CGPoint,
                atlasTolerance: CGFloat, rooms: [GroundTruthRoomPlacement]) {
        let room: GroundTruthRoomPlacement?
        if phase == .began {
            room = hitRoom(at: atlasPoint, tolerance: (atlasTolerance > 0 ? atlasTolerance : 2), rooms: rooms)
                ?? rooms.filter { $0.atlasBounds.contains(atlasPoint) }
                .min { $0.bounds.width * $0.bounds.height < $1.bounds.width * $1.bounds.height }
            gestureRoomID = room?.id
        } else { room = rooms.first { $0.id == gestureRoomID } }
        guard let room, let projection = document.roomProjections?[room.sceneName],
              projection.roomID == room.id else {
            error = "Visit this room once to calibrate its ground editor"
            return
        }
        error = nil
        handleWorld(phase: phase, world: projection.atlasToWorld(atlasPoint, room: room),
            toleranceX: (atlasTolerance > 0 ? atlasTolerance : 2) / projection.pixelsPerWorldUnitX,
            toleranceY: (atlasTolerance > 0 ? atlasTolerance : 2) / projection.pixelsPerWorldUnitY,
            sceneName: room.sceneName, minimumLength: 4 / editingAxis.components(CGPoint(x: projection.pixelsPerWorldUnitX, y: projection.pixelsPerWorldUnitY)).x)
        if phase == .ended { gestureRoomID = nil }
    }

    private func hitRoom(at point: CGPoint, tolerance: CGFloat,
                         rooms: [GroundTruthRoomPlacement]) -> GroundTruthRoomPlacement? {
        let byScene = Dictionary(rooms.map { ($0.sceneName, $0) }, uniquingKeysWith: { _, last in last })
        let hits = hitCandidates.compactMap { line -> (GroundTruthRoomPlacement, CGFloat)? in
            guard let room = byScene[line.sceneName],
                  let projection = document.roomProjections?[line.sceneName],
                  projection.roomID == room.id else { return nil }
            let world = projection.atlasToWorld(point, room: room)
            let radius = CGPoint(x: tolerance / projection.pixelsPerWorldUnitX,
                                 y: tolerance / projection.pixelsPerWorldUnitY)
            guard let rank = hitRank(line: line, point: world, tolerance: radius) else { return nil }
            return (room, rank)
        }
        return hits.min { $0.1 < $1.1 }?.0
    }

    func handle(
        phase: LayerSceneGroundLabelPointerPhase,
        atlasPoint: CGPoint,
        atlasTolerance: CGFloat,
        transform: GroundTruthCameraTransform?,
        liveBounds: CGRect
    ) {
        guard let transform else {
            error = "Camera transform unavailable"
            return
        }
        let world = transform.atlasToWorld(atlasPoint, liveBounds: liveBounds)
        let radius = atlasTolerance > 0 ? atlasTolerance : 2
        let toleranceX = radius / transform.pixelsPerWorldUnitX
        let toleranceY = radius / transform.pixelsPerWorldUnitY
        handleWorld(phase: phase, world: world, toleranceX: toleranceX, toleranceY: toleranceY,
            sceneName: transform.sceneName, minimumLength: 4 / editingAxis.components(CGPoint(x: transform.pixelsPerWorldUnitX, y: transform.pixelsPerWorldUnitY)).x)
    }

    private func handleWorld(phase: LayerSceneGroundLabelPointerPhase, world: CGPoint,
                             toleranceX: CGFloat, toleranceY: CGFloat,
                             sceneName: String, minimumLength: CGFloat) {
        switch phase {
        case .began:
            begin(world: world, toleranceX: toleranceX, toleranceY: toleranceY,
                  sceneName: sceneName)
        case .changed:
            change(world: world)
        case .ended:
            change(world: world)
            finish(at: world, minimumLength: minimumLength)
        }
    }

    func undo() {
        if newLine != nil || original != nil {
            cancelGesture()
            return
        }
        guard let snapshot = history.popLast() else { return }
        let projections = document.roomProjections
        document = snapshot.document
        document.roomProjections = projections
        selectedID = snapshot.selectedID
        persist()
    }

    private func cancelGesture() {
        preview = nil
        newLine = nil
        original = nil
        modificationPart = nil
        gestureStartWorld = nil
        startTarget = nil
    }

    func selectAxis(_ axis: TerrainLineAxis) {
        cancelGesture()
        editingSpikes = false
        editingDoors = false
        editingAxis = axis
        selectedID = nil
        gestureRoomID = nil
    }

    func selectSpike() {
        selectAxis(.horizontal)
        editingSpikes = true
    }

    func selectDoor() {
        selectAxis(.horizontal)
        editingDoors = true
    }

    private func begin(world: CGPoint, toleranceX: CGFloat, toleranceY: CGFloat, sceneName: String) {
        cancelGesture()
        gestureStartWorld = world
        gestureTolerance = CGPoint(x: toleranceX, y: toleranceY)
        let hit = mode == .add ? nil : nearestLine(to: world, sceneName: sceneName)
        switch mode {
        case .add, .negative:
            if mode == .negative {
                guard editingAxis == .horizontal && !editingDoors && !editingSpikes else { return }
                if var hit {
                    recordSnapshot()
                    let previous = hit
                    hit.kind = .negative
                    hit.startCornerID = nil
                    hit.endCornerID = nil
                    var next = document
                    next.replaceLine(hit)
                    next.pruneCorners(afterRemoving: previous)
                    next.updateLatchedDoors()
                    document = next
                    selectedID = hit.id
                    persist()
                    return
                }
            } else if !editingSpikes && !editingDoors {
                startTarget = document.nearestCornerTarget(to: world, for: editingAxis,
                    scene: sceneName, tolerance: gestureTolerance)
            }
            let origin = startTarget.flatMap { document.line(at: $0)?.point(at: $0.end) } ?? world
            selectedID = nil
            newLine = GroundTruthWorldLine(sceneName: sceneName, minimumWorldX: origin.x, maximumWorldX: origin.x,
                worldY: origin.y, kind: mode == .negative ? .negative : editingSpikes ? .spike : editingDoors ? .door : editingAxis == .vertical ? .wall : .positive,
                seededFromDetector: false, endWorldY: editingAxis == .vertical ? origin.y : nil)
            change(world: origin)
        case .delete:
            guard let hit else { selectedID = nil; return }
            recordSnapshot()
            var next = document
            if editingDoors { next.doors?.removeAll { $0.id == hit.id } }
            else if editingSpikes { next.spikes?.removeAll { $0.id == hit.id } }
            else { next[editingAxis].removeAll { $0.id == hit.id } }
            next.pruneCorners(afterRemoving: hit)
            next.updateLatchedDoors()
            document = next
            selectedID = nil
            persist()
        case .modify:
            guard let hit else { selectedID = nil; return }
            selectedID = hit.id
            original = hit
            let axis = hit.kind.axis
            let along = axis.components(world).x
            let a = abs(along - axis.components(hit.start).x)
            let b = abs(along - axis.components(hit.end).x)
            modificationPart = min(a, b) <= axis.components(gestureTolerance).x
                ? .endpoint(a <= b ? .start : .end) : .whole
        }
    }

    private func change(world: CGPoint) {
        guard let gestureStartWorld else { return }
        var next = document
        if var line = newLine {
            if editingDoors {
                line.kind = abs(world.y - gestureStartWorld.y) > abs(world.x - gestureStartWorld.x) ? .doorVertical : .door
            }
            let axis = line.kind.axis
            let start = axis.components(line.start), along = axis.components(world).x
            let first: TerrainLineEnd = along >= start.x ? .start : .end
            line.setExtent(from: start.x, to: along, across: start.y)
            if editingDoors { next.doors = (next.doors ?? []) + [line] }
            else if line.kind == .spike { next.spikes = (next.spikes ?? []) + [line] }
            else { next[axis].append(line) }
            if let startTarget { next.join(.init(lineID: line.id, end: first), to: startTarget) }
            if mode == .add, !editingSpikes, !editingDoors, let endTarget = document.nearestCornerTarget(to: line.point(at: first.opposite),
                for: axis, scene: line.sceneName, tolerance: gestureTolerance, excluding: startTarget) {
                next.join(.init(lineID: line.id, end: first.opposite), to: endTarget)
            }
        } else if let line = original, let modificationPart {
            let axis = line.kind.axis
            let a = axis.components(line.start), b = axis.components(line.end)
            let delta = axis.components(CGPoint(x: world.x - gestureStartWorld.x, y: world.y - gestureStartWorld.y))
            switch modificationPart {
            case .whole:
                if line.startCornerID != nil || line.endCornerID != nil {
                    next.move(.init(lineID: line.id, end: .start), along: axis.perpendicular, to: a.y + delta.y)
                } else {
                    var moved = line
                    moved.setExtent(from: a.x + delta.x, to: b.x + delta.x, across: a.y + delta.y)
                    moved.seededFromDetector = false
                    next.replaceLine(moved)
                }
            case let .endpoint(end):
                let handle = TerrainHandle(lineID: line.id, end: end)
                next.move(handle, along: axis, to: (end == .start ? a.x : b.x) + delta.x)
                if line.corner(at: end) != nil || axis != editingAxis {
                    next.move(handle, along: axis.perpendicular, to: a.y + delta.y)
                }
                if line.kind != .negative, line.kind != .spike, line.kind != .door, line.kind != .doorVertical, line.corner(at: end) == nil,
                   let tip = next.line(at: handle)?.point(at: end),
                   let target = next.nearestCornerTarget(to: tip, for: axis,
                       scene: line.sceneName, tolerance: gestureTolerance) {
                    next.join(handle, to: target, keepingTarget: axis == .horizontal)
                }
            }
        } else { return }
        if editingDoors, let id = newLine?.id ?? original?.id, let i = next.doors?.firstIndex(where: { $0.id == id }),
           let door = next.doors?[i] {
            if case let .endpoint(end) = modificationPart, let existing = original?.groundLatch, existing.end != end {
                next.doors?[i].groundLatch = existing
            } else {
                let ends: [TerrainLineEnd]
                if case let .endpoint(end) = modificationPart { ends = [end] } else { ends = TerrainLineEnd.allCases }
                let latch = next.nearestGroundLatch(for: door, ends: ends, tolerance: gestureTolerance)
                next.doors?[i].groundLatch = latch
            }
        }
        next.updateLatchedDoors()
        preview = next
    }

    private func finish(at world: CGPoint, minimumLength: CGFloat) {
        defer { cancelGesture() }
        guard let next = preview, next != document else { return }
        if let newLine {
            let axis = next.line(at: .init(lineID: newLine.id, end: .start))?.kind.axis ?? newLine.kind.axis
            guard let gestureStartWorld,
                  abs(axis.components(world).x - axis.components(gestureStartWorld).x) >= minimumLength,
                  let added = next.line(at: .init(lineID: newLine.id, end: .start)),
                  axis.components(added.end).x - axis.components(added.start).x >= minimumLength else { return }
        }
        do {
            try store.save(next)
            recordSnapshot()
            document = next
            selectedID = newLine?.id ?? original?.id
            error = nil
        } catch { self.error = error.localizedDescription }
    }

    private var hitCandidates: [GroundTruthWorldLine] {
        if editingDoors { return document.doors ?? [] }
        if editingSpikes { return document.spikes ?? [] }
        switch mode {
        case .add: return document[editingAxis.perpendicular].filter { $0.kind != .negative }
        case .modify where editingAxis == .vertical:
            // The wall tool can grab a ground endpoint while laying out corners.
            return document[.vertical] + document.lines.filter { $0.kind == .positive }
        default: return document[editingAxis]
        }
    }

    private func nearestLine(to point: CGPoint, sceneName: String) -> GroundTruthWorldLine? {
        hitCandidates.compactMap { line -> (GroundTruthWorldLine, CGFloat)? in
            guard line.sceneName == sceneName,
                  let rank = hitRank(line: line, point: point, tolerance: gestureTolerance) else { return nil }
            return (line, rank)
        }.min { $0.1 < $1.1 }?.0
    }

    private func hitRank(line: GroundTruthWorldLine, point: CGPoint, tolerance: CGPoint) -> CGFloat? {
        let axis = line.kind.axis
        let p = axis.components(point), a = axis.components(line.start)
        let b = axis.components(line.end), t = axis.components(tolerance)
        let across = abs(p.y - a.y) / t.y
        guard across <= 1, p.x >= a.x - t.x, p.x <= b.x + t.x else { return nil }
        let endpoint = min(abs(p.x - a.x), abs(p.x - b.x)) / t.x
        if axis != editingAxis && !editingDoors {
            let distance = hypot(endpoint, across)
            return distance <= 1 ? distance : nil
        }
        return mode == .modify && endpoint <= 1 ? hypot(endpoint, across) : 3 + across
    }

    private func recordSnapshot() {
        history.append(Snapshot(document: document, selectedID: selectedID))
        if history.count > 100 { history.removeFirst() }
    }

    private func persist() {
        do {
            try store.save(document)
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

struct GroundTruthLabelToolbar: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    @ObservedObject var controller: GroundTruthLabelController
    let hasCameraTransform: Bool
    var currentSceneName: String? = nil
    var targetName = "Ground"
    var modes: [LabelingEditorMode] = [.add, .modify, .delete]
    var onDone: (() -> Void)? = nil

    var body: some View {
        HStack(spacing: 8) {
            StableSegmentedPicker(
                label: "\(targetName) editing mode",
                choices: modes,
                title: { visionText($0.rawValue) },
                selection: $controller.mode
            )
            .frame(width: 290)
            Button(visionText("Undo"), action: controller.undo)
                .disabled(!controller.canUndo)
            Text(currentSceneName.map {
                "\(controller.lineCount(in: $0)) in this room · \(controller.lineCount()) in all rooms"
            } ?? "\(controller.lineCount()) in all rooms")
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .help(currentSceneName.map { "Current room: \($0). Includes saved lines outside the viewport." }
                    ?? "All saved lines of this type, including rooms outside the viewport.")
            if !hasCameraTransform {
                Image(systemName: "camera.metering.unknown")
                    .foregroundStyle(.orange)
                    .help("Waiting for the game camera transform")
            }
            if let error = controller.error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .lineLimit(1)
                    .help(error)
            }
            Spacer()
            if let onDone { Button(visionText("Done"), action: onDone) }
        }
        .font(.caption)
        .help(controller.editingDoors
              ? "Draw a horizontal or vertical door. An endpoint near green ground snaps to it and follows later ground edits."
              : controller.editingSpikes
              ? "Drag to add a spike line. In Modify, drag an endpoint to resize it or drag the middle to move it. Shift-drag to pan."
              : "Start or finish near an endpoint to join a corner. A filled square marks a joined corner. In Modify, drag the corner, move the wall sideways, or move the floor up and down. Shift-drag to pan.")
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(.regularMaterial)
        .overlay {
            ZStack {
                Button(visionText("Undo"), action: controller.undo)
                    .keyboardShortcut("z", modifiers: .command)
                ForEach(modes) { mode in
                    Button("\(mode.rawValue) \(targetName) mode") { controller.mode = mode }
                        .keyboardShortcut(KeyEquivalent(mode.shortcutCharacter), modifiers: [])
                }
            }
            .frame(width: 1, height: 1)
            .opacity(0)
            .accessibilityHidden(true)
        }
    }
}
