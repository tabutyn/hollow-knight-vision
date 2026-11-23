import Foundation

enum BotSkill: String, CaseIterable, Codable {
    case explore, traverse, interact, fight, recover, progress

    var initialPoints: Int {
        switch self {
        case .explore: 40
        case .traverse: 55
        case .interact: 80
        case .fight: 85
        case .recover: 95
        case .progress: 65
        }
    }

    var controls: [SkillControl] {
        switch self {
        case .explore: [.native, .left, .right, .inventory, .inspect]
        case .traverse: [.native, .left, .right, .jumpLeft, .jumpRight, .doubleJump, .wallJumpLeft, .wallJumpRight, .pogo, .dashLeft, .dashRight, .crystalDash]
        case .interact: [.inspect, .advance, .inventory, .pause, .menuUp, .menuDown, .menuLeft, .menuRight, .confirm, .cancel]
        case .fight: [.native, .pogo, .cast, .castUp, .castDown, .dashLeft, .dashRight, .dreamNail]
        case .recover: [.focus, .native]
        case .progress: [.native, .inventory, .inspect, .dreamNail]
        }
    }
}

enum SkillControl: String, CaseIterable, Codable {
    case native, left, right, jumpLeft, jumpRight, doubleJump, wallJumpLeft, wallJumpRight, pogo
    case dashLeft, dashRight, crystalDash, dreamNail, focus, cast, castUp, castDown
    case inspect, advance, inventory, pause, menuUp, menuDown, menuLeft, menuRight, confirm, cancel

    var isMenuInput: Bool { [.menuUp, .menuDown, .menuLeft, .menuRight, .confirm, .cancel].contains(self) }
    var duration: Double {
        switch self {
        case .native: 2
        case .focus: 1.55
        case .crystalDash: 1.2
        case .dreamNail: 0.95
        case .right, .left: 0.35
        case .jumpLeft, .jumpRight, .wallJumpLeft, .wallJumpRight: 0.23
        case .doubleJump: 0.52
        default: 0.12
        }
    }

    func buttons(elapsed: Double) -> InputBridgeButtons {
        switch self {
        case .native: []
        case .left, .menuLeft: .left
        case .right, .menuRight: .right
        case .jumpLeft: [.left, .actionZ]
        case .jumpRight: [.right, .actionZ]
        case .wallJumpLeft: [.right, .actionZ]
        case .wallJumpRight: [.left, .actionZ]
        case .advance, .confirm: .actionZ
        case .doubleJump: elapsed < 0.17 || elapsed >= 0.32 ? .actionZ : []
        case .pogo: [.down, .actionX]
        case .dashLeft: [.left, .dash]
        case .dashRight: [.right, .dash]
        case .crystalDash: .superDash
        case .dreamNail: .dreamNail
        case .focus, .cancel: .actionA
        case .cast: .quickCast
        case .castUp: [.up, .quickCast]
        case .castDown: [.down, .quickCast]
        case .inspect, .menuUp: .up
        case .menuDown: .down
        case .inventory: .inventory
        case .pause: .pauseMenu
        }
    }
}

struct BotSkillChoice: Codable, Equatable {
    let policy: BotSkill
    let control: SkillControl
    let points: Int
    let cue: String

    var measuresAtlasCoverage: Bool { policy == .explore && control == .native }

    var completionEvidence: String {
        if control == .inspect { return "Vision checks for a new passage after pressing Up." }
        if measuresAtlasCoverage { return "Vision checks whether the movement policy mapped new ground." }
        return "Vision records whether the bot completed the maneuver."
    }
}

struct BotPurchaseWish: Codable, Equatable {
    let item: String
    let desire: Int
    let maxGeo: Int
    let reserveGeo: Int

    func affordable(price: Int, wallet: Int) -> Bool {
        price >= 0 && price <= maxGeo && wallet >= price && wallet - price >= reserveGeo
    }
}

struct BotSkillPlan: Codable, Equatable {
    let objective: String
    let success: String
    let choices: [BotSkillChoice]
    let shopping: [BotPurchaseWish]
    var shop: ShopSnapshot? = nil

    struct ShopSnapshot: Codable, Equatable {
        struct Offer: Codable, Equatable { let item: String; let price: Int }
        let wallet: Int
        let offers: [Offer]
    }
    func recommendedPurchase(visibleText: [String]) -> (BotPurchaseWish, Int)? {
        guard let shop else { return nil }
        let labels = visibleText.map(RememberedGameText.key)
        func hasAmount(_ line: String, _ amount: Int) -> Bool {
            line.split(whereSeparator: { !$0.isNumber }).contains(Substring(String(amount)))
        }
        guard labels.contains(where: { hasAmount($0, shop.wallet) }) else { return nil }
        for wish in shoppingPriorities {
            let item = RememberedGameText.key(wish.item)
            guard let offer = shop.offers.first(where: { RememberedGameText.key($0.item) == item }),
                  wish.affordable(price: offer.price, wallet: shop.wallet),
                  let index = labels.firstIndex(where: { $0 == item || $0.hasPrefix(item + " ") }),
                  labels[index...].prefix(2).contains(where: { hasAmount($0, offer.price) })
            else { continue }
            return (wish, offer.price)
        }
        return nil
    }

    static let defaultObjective = "Find more unmapped territory for the Atlas"
    static let atlasGuidance = """
        The permanent objective is to complete the Atlas, including the progression needed to beat Hollow Knight.
        Follow the player's stated goal first. In its absence, seek reachable unexplored exits or unrecorded
        ground. Use clues, traversal upgrades and bosses to open new territory. Treat newly mapped ground
        or a newly recorded room as evidence of progress. Do not claim exploration without observations.
        """
    static let initial = BotSkillPlan(objective: defaultObjective, success: "New ground or a room recorded in the Atlas",
        choices: [.init(policy: .interact, control: .inspect, points: BotSkill.interact.initialPoints, cue: "Inspect"),
                  .init(policy: .interact, control: .inspect, points: 75, cue: "Listen"),
                  .init(policy: .explore, control: .native, points: BotSkill.explore.initialPoints, cue: "")], shopping: [])

    static func decode(_ data: Data) throws -> Self {
        let plan = try JSONDecoder().decode(Self.self, from: data)
        return try plan.validated()
    }

    func validated(allowEmptyChoices: Bool = false) throws -> Self {
        func valid(_ text: String, max: Int) -> Bool { !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && text.count <= max }
        guard valid(objective, max: 240), valid(success, max: 240),
              ((allowEmptyChoices ? 0 : 1)...6).contains(choices.count), shopping.count <= 5,
              choices.allSatisfy({ $0.policy.controls.contains($0.control) && (1...100).contains($0.points) &&
                  $0.cue.count <= 160 && (![.inspect, .advance, .confirm].contains($0.control) || valid($0.cue, max: 160)) }),
              shop.map({ (0...99999).contains($0.wallet) && $0.offers.count <= 20 &&
                  $0.offers.allSatisfy { valid($0.item, max: 100) && (0...99999).contains($0.price) } }) ?? true,
              shopping.allSatisfy({ valid($0.item, max: 100) && (1...100).contains($0.desire) &&
                  (0...99999).contains($0.maxGeo) && (0...99999).contains($0.reserveGeo) }) else {
            throw BotRuntimeError.incompatible("Invalid policy plan.")
        }
        return self
    }

    func choose(_ sight: SkillSight) -> BotSkillChoice? {
        choices.filter { sight.allows($0) }.max { $0.points < $1.points }
    }

    var shoppingPriorities: [BotPurchaseWish] { shopping.sorted { $0.desire > $1.desire } }
}

struct SkillReading {
    let context: String
    let capturedAt: Double
    let visible: [String]
    var passage: String? = nil
}

struct SkillSight {
    let context: String
    let capturedAt: Double
    let generation: UInt64
    let gameplay: Bool
    let menu: LabelingContext?
    let selected: String?
    let lines: [String]
    let hasAbilityControls: Bool
    var reading: SkillReading? = nil

    var inGameMenu: Bool { menu == .inventory || menu == .pause }
    func allows(_ choice: BotSkillChoice) -> Bool {
        choice.policy.controls.contains(choice.control) && controlInput(for: choice).allows(choice.control)
    }

    func controlInput(for choice: BotSkillChoice) -> BotControlInput {
        let cue = RememberedGameText.key(choice.cue)
        return BotControlInput(gameplay: gameplay, inGameMenu: inGameMenu, requiresCue: !cue.isEmpty,
            cueVisible: !cue.isEmpty && lines.contains { RememberedGameText.key($0) == cue },
            selectionMatches: !cue.isEmpty && selected.map(RememberedGameText.key) == cue,
            extendedControls: hasAbilityControls)
    }
}

/// Six booleans per proposal. OCR strings and menu identifiers stay in the reader.
struct BotControlInput {
    let gameplay, inGameMenu, requiresCue, cueVisible, selectionMatches, extendedControls: Bool

    func allows(_ command: SkillControl) -> Bool {
        if command == .confirm { return inGameMenu && selectionMatches }
        guard !requiresCue || cueVisible else { return false }
        if command.isMenuInput { return inGameMenu }
        if command == .inventory || command == .pause { return gameplay || inGameMenu }
        guard gameplay else { return false }
        if command == .inspect || command == .advance { return cueVisible }
        return extendedControls || command.buttons(elapsed: 0).intersection(.skillAbilities).isEmpty
    }
}
