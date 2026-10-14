import Foundation

enum HollowKnightRuntimeMode: String, Codable, CaseIterable, Identifiable {
    case normal
    case modded

    static let defaultsKey = "HollowKnightVision.runtimeMode.v1"

    var id: Self { self }
    var title: String {
        switch self {
        case .normal: "Normal Hollow Knight"
        case .modded: "Modded Hollow Knight"
        }
    }
}

enum HollowKnightRuntimeModeArgumentError: Error, Equatable, LocalizedError {
    case missingValue
    case invalidValue(String)
    case repeated

    var errorDescription: String? {
        switch self {
        case .missingValue:
            "--game-mode requires normal or modded"
        case .invalidValue(let value):
            "Invalid --game-mode value '\(value)'; expected normal or modded"
        case .repeated:
            "--game-mode may be specified only once"
        }
    }
}

enum HollowKnightRuntimeModeParser {
    static func parse(_ arguments: [String]) throws -> HollowKnightRuntimeMode? {
        var values = [String]()
        var index = 1
        while index < arguments.count {
            let argument = arguments[index]
            if argument == "--game-mode" {
                guard index + 1 < arguments.count else {
                    throw HollowKnightRuntimeModeArgumentError.missingValue
                }
                values.append(arguments[index + 1])
                index += 2
                continue
            }
            if argument.hasPrefix("--game-mode=") {
                values.append(String(argument.dropFirst("--game-mode=".count)))
            }
            index += 1
        }
        guard values.count <= 1 else {
            throw HollowKnightRuntimeModeArgumentError.repeated
        }
        guard let value = values.first else { return nil }
        guard let mode = HollowKnightRuntimeMode(rawValue: value.lowercased()) else {
            throw HollowKnightRuntimeModeArgumentError.invalidValue(value)
        }
        return mode
    }
}

enum HollowKnightGamePathArgumentError: Error, Equatable, LocalizedError {
    case missingValue
    case repeated

    var errorDescription: String? {
        switch self {
        case .missingValue: "--game-path requires a Hollow Knight .app path"
        case .repeated: "--game-path may be specified only once"
        }
    }
}

enum HollowKnightGamePathParser {
    static func parse(_ arguments: [String]) throws -> String? {
        var values = [String]()
        var index = 1
        while index < arguments.count {
            let argument = arguments[index]
            if argument == "--game-path" {
                guard index + 1 < arguments.count else {
                    throw HollowKnightGamePathArgumentError.missingValue
                }
                values.append(arguments[index + 1])
                index += 2
                continue
            }
            if argument.hasPrefix("--game-path=") {
                values.append(String(argument.dropFirst("--game-path=".count)))
            }
            index += 1
        }
        guard values.count <= 1 else { throw HollowKnightGamePathArgumentError.repeated }
        return values.first
    }
}

struct HollowKnightRuntimeModePreferences {
    let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    func load() -> HollowKnightRuntimeMode? {
        defaults.string(forKey: HollowKnightRuntimeMode.defaultsKey)
            .flatMap(HollowKnightRuntimeMode.init(rawValue:))
    }

    func save(_ mode: HollowKnightRuntimeMode) {
        defaults.set(mode.rawValue, forKey: HollowKnightRuntimeMode.defaultsKey)
    }

    func reset() {
        defaults.removeObject(forKey: HollowKnightRuntimeMode.defaultsKey)
    }
}
