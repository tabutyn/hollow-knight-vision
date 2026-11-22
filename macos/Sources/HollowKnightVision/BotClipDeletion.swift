import Foundation

enum BotClipDeletion {
    static func delete(snippet: BotSnippet, root: URL,
                       replayRoot: URL = InputPathStore.defaultRootURL()) throws {
        let manager = FileManager.default
        let expected = root.appendingPathComponent("demonstrations", isDirectory: true).resolvingSymlinksInPath().standardizedFileURL
        let directory = snippet.directory.resolvingSymlinksInPath().standardizedFileURL
        let id = snippet.id.uuidString.lowercased()
        guard directory.deletingLastPathComponent() == expected,
              directory.lastPathComponent == "demo-\(id)", snippet.manifest.status != "recording" else {
            throw BotRuntimeError.incompatible("Only a stopped clip inside the demonstration folder can be deleted.")
        }
        let current = try BotSnippet.load(directory)
        guard current.id == snippet.id, current.manifest.status != "recording" else {
            throw BotRuntimeError.incompatible("This clip changed or is still recording.")
        }
        let replayID = current.manifest.sourceReplayID
        guard replayID == current.id || replayID == current.manifest.pairedClipID else {
            throw BotRuntimeError.incompatible("Invalid shared replay identity.")
        }
        if let partner = current.manifest.pairedClipID {
            let sibling = expected.appendingPathComponent("demo-\(partner.uuidString.lowercased())")
            if manager.fileExists(atPath: sibling.path) {
                try manager.removeItem(at: directory)
                return
            }
        }
        let replayKey = replayID.uuidString.lowercased()
        var targets = [directory]
        let resolvedReplayRoot = replayRoot.resolvingSymlinksInPath().standardizedFileURL
        let candidates = (try? manager.contentsOfDirectory(at: replayRoot, includingPropertiesForKeys: nil)) ?? []
        for url in candidates where url.lastPathComponent == "frames-\(replayKey)"
            || (url.lastPathComponent.hasPrefix("path-") && url.lastPathComponent.hasSuffix("-\(replayKey).json")) {
            guard url.resolvingSymlinksInPath().standardizedFileURL.deletingLastPathComponent() == resolvedReplayRoot else {
                throw BotRuntimeError.incompatible("A linked replay points outside the recording folder.")
            }
            targets.append(url)
        }
        for target in targets.reversed() { try manager.removeItem(at: target) }
    }
}
