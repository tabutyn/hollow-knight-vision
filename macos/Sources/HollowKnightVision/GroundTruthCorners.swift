import CoreGraphics
import Foundation

enum TerrainLineEnd: String, Codable, CaseIterable {
    case start, end

    var opposite: Self { self == .start ? .end : .start }
}

struct GroundTruthDoorLatch: Codable, Equatable {
    let groundID: UUID
    let end: TerrainLineEnd
    let fraction: Double
}

struct TerrainHandle: Hashable {
    let lineID: UUID
    let end: TerrainLineEnd
}

extension GroundTruthWorldLine {
    func point(at endpoint: TerrainLineEnd) -> CGPoint { endpoint == .start ? start : end }

    func corner(at endpoint: TerrainLineEnd) -> UUID? {
        endpoint == .start ? startCornerID : endCornerID
    }

    mutating func setCorner(_ id: UUID?, at endpoint: TerrainLineEnd) {
        if endpoint == .start { startCornerID = id } else { endCornerID = id }
    }
}

extension GroundTruthLineDocument {
    /// A door can meet a platform at its lip or anywhere on its top. Ground
    /// owns the anchor, so dragging a door never moves the platform.
    func nearestGroundLatch(for door: GroundTruthWorldLine, ends: [TerrainLineEnd], tolerance: CGPoint) -> GroundTruthDoorLatch? {
        var best: GroundTruthDoorLatch?
        var distance = Double.infinity
        for floor in lines where floor.kind == .positive && floor.sceneName == door.sceneName && floor.maximumWorldX > floor.minimumWorldX {
            for end in ends {
                let p = door.point(at: end)
                var x = min(floor.maximumWorldX, max(floor.minimumWorldX, p.x))
                let tip = abs(p.x - floor.minimumWorldX) <= abs(p.x - floor.maximumWorldX) ? floor.minimumWorldX : floor.maximumWorldX
                let dy = (p.y - floor.worldY) / tolerance.y
                if hypot((p.x - tip) / tolerance.x, dy) <= 1 { x = tip }
                let d = hypot((p.x - x) / tolerance.x, dy)
                if d <= 1 && d < distance {
                    best = .init(groundID: floor.id, end: end, fraction: (x - floor.minimumWorldX) / (floor.maximumWorldX - floor.minimumWorldX))
                    distance = d
                }
            }
        }
        return best
    }

    mutating func updateLatchedDoors() {
        guard var items = doors else { return }
        let ground = Dictionary(uniqueKeysWithValues: lines.filter { $0.kind == .positive }.map { ($0.id, $0) })
        for i in items.indices {
            guard let latch = items[i].groundLatch else { continue }
            guard let floor = ground[latch.groundID], floor.sceneName == items[i].sceneName else {
                items[i].groundLatch = nil
                continue
            }
            let x = floor.minimumWorldX + (floor.maximumWorldX - floor.minimumWorldX) * latch.fraction
            let p = items[i].point(at: latch.end)
            let axis = items[i].kind.axis
            let shift = axis.components(CGPoint(x: x - p.x, y: floor.worldY - p.y))
            let a = axis.components(items[i].start), b = axis.components(items[i].end)
            items[i].setExtent(from: a.x + shift.x, to: b.x + shift.x, across: a.y + shift.y)
        }
        doors = items
    }

    func line(at handle: TerrainHandle) -> GroundTruthWorldLine? {
        lines.first { $0.id == handle.lineID } ?? walls?.first { $0.id == handle.lineID }
            ?? spikes?.first { $0.id == handle.lineID } ?? doors?.first { $0.id == handle.lineID }
    }

    mutating func replaceLine(_ line: GroundTruthWorldLine) {
        if line.kind == .door || line.kind == .doorVertical {
            if let i = doors?.firstIndex(where: { $0.id == line.id }) { doors?[i] = line }
        } else if line.kind == .spike {
            if let i = spikes?.firstIndex(where: { $0.id == line.id }) { spikes?[i] = line }
        } else if line.kind == .wall {
            if let i = walls?.firstIndex(where: { $0.id == line.id }) { walls?[i] = line }
        } else if let i = lines.firstIndex(where: { $0.id == line.id }) {
            lines[i] = line
        }
    }

    func nearestCornerTarget(to point: CGPoint, for axis: TerrainLineAxis, scene: String,
                             tolerance: CGPoint, excluding: TerrainHandle? = nil) -> TerrainHandle? {
        var best: TerrainHandle?
        var distance: CGFloat = .infinity
        for line in self[axis.perpendicular] where line.sceneName == scene && line.kind != .negative {
            for end in TerrainLineEnd.allCases {
                let handle = TerrainHandle(lineID: line.id, end: end)
                guard handle != excluding else { continue }
                let node = line.point(at: end)
                let d = hypot((node.x - point.x) / tolerance.x, (node.y - point.y) / tolerance.y)
                if d <= 1, d < distance {
                    distance = d
                    best = handle
                }
            }
        }
        return best
    }

    /// Each wall shares X between its endpoints, and each floor shares Y. Walk
    /// only the edges that constrain the coordinate being dragged.
    mutating func move(_ handle: TerrainHandle, along coordinate: TerrainLineAxis, to value: CGFloat) {
        guard let owner = line(at: handle), coordinate.components(owner.point(at: handle.end)).x != value else { return }
        let room = authoredLines.filter { $0.sceneName == owner.sceneName }
        let byID = Dictionary(uniqueKeysWithValues: room.map { ($0.id, $0) })
        var members = [UUID: [TerrainHandle]]()
        for line in room {
            for end in TerrainLineEnd.allCases {
                if let corner = line.corner(at: end) {
                    members[corner, default: []].append(.init(lineID: line.id, end: end))
                }
            }
        }
        var moving = Set<TerrainHandle>()
        var pending = [handle]
        while let node = pending.popLast() {
            guard moving.insert(node).inserted, let line = byID[node.lineID] else { continue }
            if line.kind.axis != coordinate {
                pending.append(.init(lineID: line.id, end: node.end.opposite))
            }
            if let id = line.corner(at: node.end), let peers = members.removeValue(forKey: id) {
                pending.append(contentsOf: peers)
            }
        }

        // Keep left/right and bottom/top handle identities stable if a drag
        // reaches the opposite endpoint of any connected line.
        var position = value
        for node in moving {
            guard let line = byID[node.lineID], line.kind.axis == coordinate,
                  !moving.contains(.init(lineID: line.id, end: node.end.opposite)) else { continue }
            let bound = coordinate.components(line.point(at: node.end.opposite)).x
            position = node.end == .start ? min(bound, position) : max(bound, position)
        }
        for id in Set(moving.map(\.lineID)) {
            guard var line = byID[id] else { continue }
            let axis = line.kind.axis
            let a = axis.components(line.start), b = axis.components(line.end)
            if axis == coordinate {
                line.setExtent(from: moving.contains(.init(lineID: id, end: .start)) ? position : a.x,
                               to: moving.contains(.init(lineID: id, end: .end)) ? position : b.x, across: a.y)
            } else {
                line.setExtent(from: a.x, to: b.x, across: position)
            }
            guard line != byID[id] else { continue }
            line.seededFromDetector = false
            replaceLine(line)
        }
    }

    /// A new line owns its tip. When grabbing an existing floor endpoint in
    /// Modify, the wall supplies the fixed corner instead.
    mutating func join(_ incoming: TerrainHandle, to target: TerrainHandle, keepingTarget: Bool = false) {
        guard let source = line(at: incoming), let other = line(at: target),
              source.sceneName == other.sceneName, source.kind != .negative, other.kind != .negative,
              source.kind.axis != other.kind.axis, source.corner(at: incoming.end) == nil else { return }
        let fixed = keepingTarget ? other.point(at: target.end) : source.point(at: incoming.end)
        let moving = keepingTarget ? incoming : target
        var joined = self
        joined.move(moving, along: .horizontal, to: fixed.x)
        joined.move(moving, along: .vertical, to: fixed.y)
        guard var updated = joined.line(at: incoming), var adjusted = joined.line(at: target),
              updated.point(at: incoming.end) == fixed, adjusted.point(at: target.end) == fixed else { return }
        let corner = adjusted.corner(at: target.end) ?? UUID()
        updated.setCorner(corner, at: incoming.end)
        adjusted.setCorner(corner, at: target.end)
        joined.replaceLine(updated)
        joined.replaceLine(adjusted)
        self = joined
    }

    mutating func pruneCorners(afterRemoving removed: GroundTruthWorldLine) {
        for id in Set([removed.startCornerID, removed.endCornerID].compactMap { $0 }) {
            let members = authoredLines.filter { $0.startCornerID == id || $0.endCornerID == id }
            guard !members.contains(where: { $0.kind == .positive }) || !members.contains(where: { $0.kind == .wall }) else { continue }
            for var line in members {
                if line.startCornerID == id { line.startCornerID = nil }
                if line.endCornerID == id { line.endCornerID = nil }
                replaceLine(line)
            }
        }
    }

    func validateCorners() throws {
        var locations = [UUID: (scene: String, point: CGPoint)]()
        var ground = Set<UUID>(), wall = Set<UUID>()
        for line in authoredLines {
            for end in TerrainLineEnd.allCases {
                guard let id = line.corner(at: end) else { continue }
                let point = line.point(at: end)
                if let location = locations[id], location.scene != line.sceneName || location.point != point {
                    throw GroundTruthLineStoreError.invalid("Disconnected terrain corner")
                }
                locations[id] = (line.sceneName, point)
                if line.kind == .wall { wall.insert(id) }
                else if line.kind == .positive { ground.insert(id) }
                else { throw GroundTruthLineStoreError.invalid("Negative label attached to a terrain corner") }
            }
        }
        guard ground == wall else { throw GroundTruthLineStoreError.invalid("Incomplete terrain corner") }
    }
}
