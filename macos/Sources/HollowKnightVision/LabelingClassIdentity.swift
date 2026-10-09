import Foundation

/// One semantic object owns one class identifier, even when it appears in
/// several labeling contexts. Legacy identifiers remain readable so existing
/// examples, reviews, and promoted models do not lose their labels.
enum LabelingClassIdentity {
    static let selectDecoration = "shared.select-decoration"
    static let back = "shared.back"
    static let options = "shared.options"
    static let achievements = "shared.achievements"
    static let extras = "shared.extras"
    static let audio = "shared.audio"
    static let video = "shared.video"
    static let controller = "shared.controller"
    static let keyboard = "shared.keyboard"
    static let mods = "shared.mods"
    static let dash = "shared.dash"
    static let focusCast = "shared.focus-cast"
    static let quickMap = "shared.quick-map"
    static let superDash = "shared.super-dash"
    static let dreamNail = "shared.dream-nail"
    static let quickCast = "shared.quick-cast"
    static let jump = "shared.jump"
    static let attack = "shared.attack"
    static let resetDefaults = "shared.reset-defaults"
    static let brightness = "shared.brightness"
    static let advancedSettings = "shared.advanced-settings"
    static let yes = "shared.yes"
    static let no = "shared.no"
    static let quitToMenu = "quit-to-menu.quit-to-menu"
    static let quitGame = "quit-game.quit-game"

    static let legacyIdentifiers: [String: String] = [
        "main-title.select-decoration",
        "select-profile.select-decoration",
    ].reduce(into: [String: String]()) { result, identifier in
        result[identifier] = selectDecoration
    }.merging([
        "select-profile.back": back,
        "main-title.options": options,
        "pause.options": options,
        "options.options": options,
        "main-title.achievements": achievements,
        "main-title.extras": extras,
        "options.audio": audio,
        "options.video": video,
        "options.controller": controller,
        "options.keyboard": keyboard,
        "options.mods": mods,
        "quit-to-menu.yes": yes,
        "quit-to-menu.no": no,
        "pause.quit-to-menu": quitToMenu,
        "main-title.quit-game": quitGame,
    ], uniquingKeysWith: { _, newest in newest })

    static let legacyClassIdentifiers = Set(legacyIdentifiers.keys)

    static func canonicalIdentifier(_ identifier: String) -> String {
        legacyIdentifiers[identifier] ?? identifier
    }

    static func matches(_ first: String, _ second: String) -> Bool {
        canonicalIdentifier(first) == canonicalIdentifier(second)
    }
}
