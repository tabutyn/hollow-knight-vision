import Foundation

/// The six stable actor inputs describe the currently selected path waypoint.
struct BotRoadHint: Codable, Equatable {
    let dx, dy, clearance, distance: Double
    let frontier: Bool
    var features: [Float] { [1, Float(dx), Float(dy), Float(clearance), Float(distance), frontier ? 1 : 0] }
}
