import Foundation

enum HollowKnightLaunchRoute: Equatable {
    case steam
    case application(URL)
}

enum HollowKnightLaunchPolicy {
    static let steamAppID = "367520"
    // A steam://run URL can choose Remote Play on another computer. The
    // client's -applaunch command requests this computer's installation.
    static let steamLaunchArguments = ["-applaunch", steamAppID]

    /// Steam may terminate a directly opened game and replace it with its own
    /// process. Only route the manifest's installed bundle through Steam: a
    /// separate Normal/Modded copy must still launch the selected application.
    static func route(for gameURL: URL) -> HollowKnightLaunchRoute {
        let gameURL = gameURL.resolvingSymlinksInPath().standardizedFileURL
        let installation = gameURL.deletingLastPathComponent()
        let common = installation.deletingLastPathComponent()
        let steamapps = common.deletingLastPathComponent()
        guard gameURL.lastPathComponent.lowercased() == "hollow_knight.app",
              common.lastPathComponent.lowercased() == "common",
              steamapps.lastPathComponent.lowercased() == "steamapps",
              let manifest = try? String(
                contentsOf: steamapps.appendingPathComponent("appmanifest_\(steamAppID).acf"),
                encoding: .utf8
              ),
              value("appid", in: manifest) == steamAppID,
              value("installdir", in: manifest) == installation.lastPathComponent
        else { return .application(gameURL) }
        return .steam
    }

    private static func value(_ key: String, in manifest: String) -> String? {
        let pattern = "\"" + NSRegularExpression.escapedPattern(for: key)
            + "\"\\s*\"([^\"]+)\""
        guard let expression = try? NSRegularExpression(pattern: pattern),
              let match = expression.firstMatch(
                in: manifest, range: NSRange(manifest.startIndex..., in: manifest)
              ),
              let range = Range(match.range(at: 1), in: manifest) else { return nil }
        return String(manifest[range])
    }
}
