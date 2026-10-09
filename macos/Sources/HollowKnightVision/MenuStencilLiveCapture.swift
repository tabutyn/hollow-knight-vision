import CoreGraphics
import Foundation

enum HollowKnightMenuLanguage: String, Codable, CaseIterable {
    case english = "en"
    case spanish = "es"
    case french = "fr"
    case italian = "it"
    case japanese = "ja"
    case korean = "ko"
    case portugueseBrazil = "pt-BR"
    case russian = "ru"
    case simplifiedChinese = "zh-Hans"
    case traditionalChinese = "zh-Hant"
    case german = "de"

    static let legacyDefault = HollowKnightMenuLanguage.english
}

struct MenuStencilLiveCapture: Codable, Equatable, Identifiable {
    static let schemaVersion = 1

    let schemaVersion: Int
    let id: UUID
    let capturedAt: Date
    let contextIdentifier: String
    let selectedIdentifier: String
    let selectedName: String
    /// Added without changing the schema version so the existing English
    /// capture library remains readable. Missing values are legacy English.
    let languageIdentifier: String?
    let imageFilename: String
    let imageWidth: Int
    let imageHeight: Int

    var resolvedLanguageIdentifier: String {
        languageIdentifier ?? HollowKnightMenuLanguage.legacyDefault.rawValue
    }
}

struct MenuStencilLiveCaptureStore {
    static let manifestFilename = "capture.json"
    static let imageFilename = "image.png"

    let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = Self.defaultRootURL(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL.standardizedFileURL
        self.fileManager = fileManager
    }

    static func defaultRootURL(fileManager: FileManager = .default) -> URL {
        LabelingExampleStore.defaultRootURL(fileManager: fileManager)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("menu-stencil-live-v1", isDirectory: true)
            .appendingPathComponent("captures", isDirectory: true)
    }

    func save(
        image: CGImage,
        contextIdentifier: String,
        selectedIdentifier: String,
        selectedName: String,
        languageIdentifier: String = HollowKnightMenuLanguage.legacyDefault.rawValue,
        now: Date = Date()
    ) throws -> (capture: MenuStencilLiveCapture, imageURL: URL) {
        guard HollowKnightMenuLanguage(rawValue: languageIdentifier) != nil else {
            throw MenuStencilLiveCaptureError.unsupportedLanguage(languageIdentifier)
        }
        let identifier = UUID()
        let capture = MenuStencilLiveCapture(
            schemaVersion: MenuStencilLiveCapture.schemaVersion,
            id: identifier,
            capturedAt: now,
            contextIdentifier: contextIdentifier,
            selectedIdentifier: selectedIdentifier,
            selectedName: selectedName,
            languageIdentifier: languageIdentifier,
            imageFilename: Self.imageFilename,
            imageWidth: image.width,
            imageHeight: image.height
        )
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let finalURL = rootURL.appendingPathComponent(
            identifier.uuidString.lowercased(), isDirectory: true
        )
        let stagingURL = rootURL.appendingPathComponent(
            ".staging-\(identifier.uuidString.lowercased())", isDirectory: true
        )
        do {
            try fileManager.createDirectory(at: stagingURL, withIntermediateDirectories: false)
            let imageURL = stagingURL.appendingPathComponent(Self.imageFilename)
            guard ImageFileIO.writePNG(image, to: imageURL) else {
                throw LabelingExampleStoreError.imageEncodingFailed
            }
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(capture).write(
                to: stagingURL.appendingPathComponent(Self.manifestFilename),
                options: .atomic
            )
            try fileManager.moveItem(at: stagingURL, to: finalURL)
        } catch {
            try? fileManager.removeItem(at: stagingURL)
            throw error
        }
        return (capture, finalURL.appendingPathComponent(Self.imageFilename))
    }

    func load() throws -> [(capture: MenuStencilLiveCapture, imageURL: URL)] {
        guard fileManager.fileExists(atPath: rootURL.path) else { return [] }
        let directories = try fileManager.contentsOfDirectory(
            at: rootURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        return try directories.compactMap { directory in
            guard try directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true
            else { return nil }
            let data = try Data(contentsOf: directory.appendingPathComponent(Self.manifestFilename))
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            let capture = try decoder.decode(MenuStencilLiveCapture.self, from: data)
            guard capture.schemaVersion == MenuStencilLiveCapture.schemaVersion else { return nil }
            return (capture, directory.appendingPathComponent(capture.imageFilename))
        }.sorted { $0.capture.capturedAt > $1.capture.capturedAt }
    }
}

enum MenuStencilLiveCaptureError: LocalizedError {
    case unsupportedLanguage(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedLanguage(let identifier):
            return "Unsupported Hollow Knight language: \(identifier)"
        }
    }
}

struct MenuStencilAutomationReply: Codable {
    let ok: Bool
    let contextIdentifier: String?
    let selectedIdentifier: String?
    let selectedName: String?
    let capturePath: String?
    let message: String

    var serialized: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        guard let data = try? encoder.encode(self) else {
            return #"{"message":"reply encoding failed","ok":false}"#
        }
        return String(decoding: data, as: UTF8.self)
    }
}
