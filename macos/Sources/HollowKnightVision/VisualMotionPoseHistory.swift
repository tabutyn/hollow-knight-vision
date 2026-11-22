import CoreGraphics
import Foundation

/// A completed visual solve, expressed at its image capture time rather than
/// at the later time it reaches the capture worker.
struct VisualCameraPoseSample: Codable, Equatable, Sendable {
    let timestamp: TimeInterval
    let cameraPosition: CGPoint
}

/// Small capture-time ledger for anchoring coarse image displacement. Predicted
/// presentation poses are estimates until a completed solve replaces that exact
/// timestamp. Neither estimates nor history membership establishes confidence.
struct VisualMotionPoseHistory {
    private var poses = [TimeInterval: CGPoint]()
    private var order = [TimeInterval]()
    static let capacity = 64

    mutating func reset() { poses.removeAll(keepingCapacity: true); order.removeAll(keepingCapacity: true) }

    mutating func record(timestamp: TimeInterval, estimatedPose: CGPoint,
                         completed: VisualCameraPoseSample?) {
        guard timestamp.isFinite, estimatedPose.x.isFinite, estimatedPose.y.isFinite else { return }
        if poses[timestamp] == nil { order.append(timestamp) }
        poses[timestamp] = estimatedPose
        if let completed, completed.timestamp.isFinite, completed.timestamp <= timestamp,
           completed.cameraPosition.x.isFinite, completed.cameraPosition.y.isFinite {
            if poses[completed.timestamp] != nil {
                poses[completed.timestamp] = completed.cameraPosition
            }
        }
        if order.count > Self.capacity {
            let count = order.count - Self.capacity
            for time in order.prefix(count) { poses.removeValue(forKey: time) }
            order.removeFirst(count)
        }
    }

    func pose(at timestamp: TimeInterval?) -> CGPoint? {
        timestamp.flatMap { poses[$0] }
    }
}
