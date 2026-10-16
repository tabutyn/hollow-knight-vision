import Foundation

enum HollowKnightRuntimeMode: String, Codable, CaseIterable, Identifiable {
    case normal
    case modded

    var id: Self { self }
    var title: String {
        switch self {
        case .normal: "Normal Hollow Knight"
        case .modded: "Modded Hollow Knight"
        }
    }
}

enum HollowKnightRuntimeModeLaunchPolicy {
    static func requiresChoice(argumentMode: HollowKnightRuntimeMode?) -> Bool {
        argumentMode == nil
    }

    static func initialMode(argumentMode: HollowKnightRuntimeMode?) -> HollowKnightRuntimeMode {
        argumentMode ?? .normal
    }
}

enum HollowKnightInstallationIssue: Equatable {
    case moddedGameInNormalMode
    case incompleteModdedGame

    var runningApplicationMessage: String {
        switch self {
        case .moddedGameInNormalMode:
            "Normal mode will not capture the running modded Hollow Knight. Close it and launch an unmodified copy, or restart Vision in Modded mode."
        case .incompleteModdedGame:
            "Modded mode will not capture this Hollow Knight installation because its Modding API loader or Vision receiver is missing. Close it and launch the configured modded copy, or restart Vision in Normal mode."
        }
    }

    var selectedPathMessage: String {
        switch self {
        case .moddedGameInNormalMode:
            "Normal mode requires an unmodified Hollow Knight app at --game-path"
        case .incompleteModdedGame:
            "Modded mode requires the matching loader and Vision receiver at --game-path"
        }
    }
}

enum HollowKnightInstallationPolicy {
    static func issue(
        gameURL: URL,
        mode: HollowKnightRuntimeMode,
        fileManager: FileManager = .default
    ) -> HollowKnightInstallationIssue? {
        let managed = gameURL
            .appendingPathComponent("Contents/Resources/Data/Managed", isDirectory: true)
        let loader = managed.appendingPathComponent("MMHOOK_Assembly-CSharp.dll")
        let receiver = managed.appendingPathComponent(
            "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
        )
        let hasLoader = fileManager.fileExists(atPath: loader.path)
        let hasReceiver = fileManager.fileExists(atPath: receiver.path)
        switch mode {
        case .normal:
            return hasLoader || hasReceiver ? .moddedGameInNormalMode : nil
        case .modded:
            return hasLoader && hasReceiver ? nil : .incompleteModdedGame
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
