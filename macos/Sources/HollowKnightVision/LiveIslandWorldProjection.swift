import CoreGraphics
import Foundation

/// Immutable placement table for one published world revision. Capture-local
/// coordinates are projected at publication time, so an older render cannot
/// reintroduce a pre-correction camera position. Solver coordinates stay raw.
struct LiveIslandWorldProjection {
    private struct Entry {
        let roomID: Int
        let startedAt: Double
        let translation: CGPoint
    }
    let revision: Int
    private let entries: [Int: Entry]

    init(snapshot: LiveWorldSnapshot) {
        revision = snapshot.mapRevision
        entries = Dictionary(uniqueKeysWithValues: snapshot.trackingIslands.map {
            ($0.id, Entry(roomID: $0.roomID, startedAt: $0.startedAt,
                         translation: $0.worldFromLocal))
        })
    }

    func worldPose(for capture: LiveIslandCapture) -> CGPoint? {
        guard let entry = entries[capture.id] else { return capture.worldPose }
        guard entry.roomID == capture.roomID, entry.startedAt == capture.startedAt else { return nil }
        return CGPoint(x: entry.translation.x + capture.localPose.x,
                       y: entry.translation.y + capture.localPose.y)
    }

    func blockadeOrigin(_ space: String) -> CGPoint? {
        let parts = space.split(separator: "/")
        guard parts.count == 4, parts[0] == "visual", let id = Int(parts[2]),
              let entry = entries[id], entry.roomID == Int(parts[1]), entry.startedAt.bitPattern == UInt64(parts[3]) else { return nil }
        return entry.translation
    }
    func blockadeOffset(from space: String, to current: LiveIslandCapture) -> CGPoint? {
        guard space.hasPrefix("visual/\(current.roomID)/"), let source = blockadeOrigin(space),
              let pose = worldPose(for: current) else { return nil }
        return CGPoint(x: source.x - pose.x + current.localPose.x, y: source.y - pose.y + current.localPose.y)
    }
}

extension LiveIslandCapture {
    /// Capture-rate extrapolation changes only this image's local position.
    /// It does not add a graph measurement or promote provisional placement.
    func advancing(by delta: CGVector) -> Self {
        Self(id: id, roomID: roomID, startedAt: startedAt, worldFromLocal: worldFromLocal,
             localPose: CGPoint(x: localPose.x + delta.dx, y: localPose.y + delta.dy),
             placement: placement, uncertainty: uncertainty, breakReason: breakReason,
             measured: measured)
    }
}
