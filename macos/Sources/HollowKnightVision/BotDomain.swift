import Foundation

enum BotDomain: String, Codable, CaseIterable, Identifiable {
    case gameplay
    case hacker
    var id: Self { self }
    var title: String { self == .gameplay ? "Gameplay" : "Hacker" }
    static func of(_ observation: BotObservation) -> Self {
        observation.terrainSource == "human-authored" ? .hacker : .gameplay
    }
}
