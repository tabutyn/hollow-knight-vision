import Foundation

enum GameTextLabel: String, Codable, CaseIterable {
    case tutorial, lore, dialogue, sign, location, shop, menu, prompt, hud, credits, cutscene, unknown

    var title: String {
        switch self {
        case .lore: return "Lore tablet"
        case .dialogue: return "NPC dialogue"
        case .hud: return "HUD"
        case .unknown: return "Unclear"
        default: return rawValue.capitalized
        }
    }
}

struct GameTextInterpretation: Codable {
    let text: String
    let label: GameTextLabel
    let model: String
    var detail: String? = nil
    var sameAs: String? = nil
    var displayLabel: String { detail ?? label.title }
}
