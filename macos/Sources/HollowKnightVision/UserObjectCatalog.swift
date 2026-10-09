import Foundation

enum UserObjectCatalogError: LocalizedError {
    case invalidName

    var errorDescription: String? { "Object name needs a letter or number." }
}

enum UserObjectGroup: String, CaseIterable, Codable {
    case game
    case enemies
    case world

    var title: String {
        switch self {
        case .game: return "Gameplay Object"
        case .enemies: return "Enemy"
        case .world: return "World Object"
        }
    }
}

struct UserObjectDefinition: Codable, Hashable, Identifiable {
    let identifier: String
    let name: String
    let group: String
    var id: String { identifier }
}

struct UserObjectCatalogDocument: Codable, Equatable {
    static let currentSchemaVersion = 1
    let schemaVersion: Int
    let objects: [UserObjectDefinition]
}

enum UserObjectCatalogStore {
    private static let cacheLock = NSLock()
    private static var cachedDefaultDocument: UserObjectCatalogDocument?

    static func defaultURL(fileManager: FileManager = .default) -> URL {
        let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("HollowKnightVision", isDirectory: true)
            .appendingPathComponent("object-catalog-v1.json")
    }

    static func load(from url: URL = defaultURL()) -> UserObjectCatalogDocument {
        let isDefault = url.standardizedFileURL == defaultURL().standardizedFileURL
        if isDefault {
            cacheLock.lock()
            defer { cacheLock.unlock() }
            if let cachedDefaultDocument { return cachedDefaultDocument }
            let document = loadUncached(from: url)
            cachedDefaultDocument = document
            return document
        }
        return loadUncached(from: url)
    }

    private static func loadUncached(from url: URL) -> UserObjectCatalogDocument {
        guard let data = try? Data(contentsOf: url),
              let document = try? JSONDecoder().decode(UserObjectCatalogDocument.self, from: data),
              document.schemaVersion == UserObjectCatalogDocument.currentSchemaVersion else {
            return UserObjectCatalogDocument(schemaVersion: 1, objects: [])
        }
        let valid = document.objects.compactMap(validated)
        let unique = Dictionary(valid.map { ($0.identifier, $0) }, uniquingKeysWith: { first, _ in first })
        return UserObjectCatalogDocument(
            schemaVersion: UserObjectCatalogDocument.currentSchemaVersion,
            objects: unique.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        )
    }

    @discardableResult
    static func add(
        name: String,
        group: UserObjectGroup,
        to url: URL = defaultURL(),
        fileManager: FileManager = .default
    ) throws -> UserObjectDefinition {
        let cleanName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanName.isEmpty else { throw UserObjectCatalogError.invalidName }
        let identifierSlug = slug(cleanName)
        guard !identifierSlug.isEmpty else { throw UserObjectCatalogError.invalidName }
        let entry = UserObjectDefinition(
            identifier: "\(group.rawValue).\(identifierSlug)",
            name: cleanName,
            group: group.rawValue
        )
        var objects = load(from: url).objects
        if let existing = objects.first(where: { $0.identifier == entry.identifier }) {
            return existing
        }
        objects.append(entry)
        let document = UserObjectCatalogDocument(
            schemaVersion: UserObjectCatalogDocument.currentSchemaVersion,
            objects: objects.sorted { $0.identifier < $1.identifier }
        )
        try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try JSONEncoder.pretty.encode(document)
        try data.write(to: url, options: .atomic)
        if url.standardizedFileURL == defaultURL().standardizedFileURL {
            cacheLock.lock()
            cachedDefaultDocument = document
            cacheLock.unlock()
        }
        return entry
    }

    static func definitions(for context: LabelingContext) -> [LabelingClassDefinition] {
        let objects = load().objects.filter { object in
            switch context {
            case .game: return true
            case .enemies: return object.group == UserObjectGroup.enemies.rawValue
            case .world: return object.group == UserObjectGroup.world.rawValue
            default: return false
            }
        }
        return objects.map { LabelingClassDefinition(id: $0.identifier, name: $0.name) }
    }

    static var classIdentifiers: Set<String> {
        Set(load().objects.map(\.identifier))
    }

    private static func validated(_ entry: UserObjectDefinition) -> UserObjectDefinition? {
        guard let group = UserObjectGroup(rawValue: entry.group),
              !entry.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              entry.identifier == "\(group.rawValue).\(slug(entry.name))" else { return nil }
        return entry
    }

    private static func slug(_ value: String) -> String {
        let allowed = CharacterSet.alphanumerics
        let parts = value.lowercased().unicodeScalars.split(whereSeparator: { !allowed.contains($0) })
        return parts.map(String.init).joined(separator: "-")
    }
}

private extension JSONEncoder {
    static var pretty: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}
