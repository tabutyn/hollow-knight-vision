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
    case notHollowKnightInstallation
    case moddedGameInNormalMode
    case incompleteModdedGame

    var runningApplicationMessage: String {
        switch self {
        case .notHollowKnightInstallation:
            "The selected app is not a complete Hollow Knight installation."
        case .moddedGameInNormalMode:
            "Normal mode will not capture the running modded Hollow Knight. Close it and launch an unmodified copy, or restart Vision in Modded mode."
        case .incompleteModdedGame:
            "Modded mode will not capture this Hollow Knight installation because its Modding API loader or Vision receiver is missing. Close it and launch the configured modded copy, or restart Vision in Normal mode."
        }
    }

    var selectedPathMessage: String {
        switch self {
        case .notHollowKnightInstallation:
            "The selected --game-path is not a complete Hollow Knight installation"
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
        let gameAssembly = managed.appendingPathComponent("Assembly-CSharp.dll")
        guard fileManager.fileExists(atPath: gameAssembly.path) else {
            return .notHollowKnightInstallation
        }
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

struct HollowKnightInstallationCatalog: Equatable {
    let normal: URL?
    let modded: URL?

    func installation(for mode: HollowKnightRuntimeMode) -> URL? {
        switch mode {
        case .normal: normal
        case .modded: modded
        }
    }
}

enum HollowKnightInstallationDiscovery {
    static func discover(
        explicitPath: String?,
        additionalCandidates: [URL] = [],
        fileManager: FileManager = .default
    ) -> HollowKnightInstallationCatalog {
        let candidates: [URL]
        if let explicitPath {
            candidates = [gameApplicationURL(path: explicitPath)]
        } else {
            candidates = additionalCandidates
                + knownCandidates(fileManager: fileManager)
                + spotlightCandidates()
        }
        return catalog(candidateURLs: candidates, fileManager: fileManager)
    }

    static func catalog(
        candidateURLs: [URL],
        fileManager: FileManager = .default
    ) -> HollowKnightInstallationCatalog {
        var normal: URL?
        var modded: URL?
        var seen = Set<String>()
        for candidate in candidateURLs {
            let url = gameApplicationURL(path: candidate.path).standardizedFileURL
            guard seen.insert(url.path).inserted,
                  fileManager.fileExists(atPath: url.path) else { continue }
            if normal == nil,
               HollowKnightInstallationPolicy.issue(
                    gameURL: url,
                    mode: .normal,
                    fileManager: fileManager
               ) == nil {
                normal = url
            }
            if modded == nil,
               HollowKnightInstallationPolicy.issue(
                    gameURL: url,
                    mode: .modded,
                    fileManager: fileManager
               ) == nil {
                modded = url
            }
        }
        return HollowKnightInstallationCatalog(normal: normal, modded: modded)
    }

    static func gameApplicationURL(path: String) -> URL {
        var url = URL(fileURLWithPath: path).standardizedFileURL
        let original = url
        while url.pathExtension.lowercased() != "app", url.path != "/" {
            url.deleteLastPathComponent()
        }
        return url.pathExtension.lowercased() == "app" ? url : original
    }

    private static func knownCandidates(fileManager: FileManager) -> [URL] {
        let home = fileManager.homeDirectoryForCurrentUser
        let user = NSUserName()
        var urls = [
            home.appendingPathComponent("Applications/Hollow Knight.app"),
            URL(fileURLWithPath: "/Applications/Hollow Knight.app"),
            home.appendingPathComponent(
                "Library/Application Support/Steam/steamapps/common/Hollow Knight/hollow_knight.app"
            ),
        ]
        for volume in fileManager.mountedVolumeURLs(
            includingResourceValuesForKeys: nil,
            options: [.skipHiddenVolumes]
        ) ?? [] {
            urls.append(volume.appendingPathComponent("Applications/Hollow Knight.app"))
            urls.append(volume.appendingPathComponent(
                "SteamLibrary/steamapps/common/Hollow Knight/hollow_knight.app"
            ))
            urls.append(volume.appendingPathComponent(
                "Relocated/\(user)/Applications/Hollow Knight.app"
            ))
            urls.append(volume.appendingPathComponent(
                "Relocated/\(user)/steam/steamapps/common/Hollow Knight/hollow_knight.app"
            ))
        }
        return urls
    }

    private static func spotlightCandidates() -> [URL] {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/mdfind")
        process.arguments = [
            "(kMDItemFSName == 'hollow_knight.app'cd) || "
                + "(kMDItemFSName == 'Hollow Knight.app'cd)",
        ]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { return [] }
            let data = output.fileHandleForReading.readDataToEndOfFile()
            guard let paths = String(data: data, encoding: .utf8) else { return [] }
            return paths.split(separator: "\n").map {
                URL(fileURLWithPath: String($0))
            }
        } catch {
            return []
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
