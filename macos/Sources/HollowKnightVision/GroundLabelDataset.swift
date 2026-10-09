import CoreGraphics
import CryptoKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

struct GroundLabelCapture {
    let image: CGImage
    let foregroundRects: [CGRect] // Bottom-left coordinates, as used by the live detector.
    let knightRect: CGRect?
    let timestamp: TimeInterval
}

enum GroundLabelSplit: String, Codable, CaseIterable { case train, check }

struct GroundLabelEdge: Codable, Equatable, Identifiable {
    var id = UUID()
    var x0: Int
    var x1: Int
    var y: Int
}

struct GroundLabelRect: Codable, Equatable, Identifiable {
    var id = UUID()
    var x: Double
    var y: Double
    var width: Double
    var height: Double
    var rect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
    init(_ rect: CGRect) {
        x = rect.minX; y = rect.minY; width = rect.width; height = rect.height
    }
    func bottomLeft(imageHeight: Int) -> CGRect {
        CGRect(x: x, y: Double(imageHeight) - y - height, width: width, height: height)
    }
    static func fromBottomLeft(_ rect: CGRect, imageHeight: Int) -> Self {
        Self(CGRect(x: rect.minX, y: CGFloat(imageHeight) - rect.maxY,
                    width: rect.width, height: rect.height))
    }
}

struct GroundLabelDocument: Codable, Equatable, Identifiable {
    var schemaVersion = 1
    var id = UUID()
    var imageFile: String
    var imageSHA256: String
    var width: Int
    var height: Int
    var createdAt: Date
    var source: String
    var group: String
    var split: GroundLabelSplit = .train
    var fullyReviewed = false
    var edges = [GroundLabelEdge]()
    var ignoredRegions = [GroundLabelRect]()
    // Human ignore regions are scoring-only. These independent captured masks
    // are the only rectangles that may be passed to the detector.
    var runtimeForeground = [GroundLabelRect]()
    var runtimeKnight: GroundLabelRect?
    var runtimeMasksKnown: Bool
    var runtimeKnightKnown: Bool

    func validate() throws {
        guard schemaVersion == 1, (2...4096).contains(width), (2...4096).contains(height),
              imageFile == "\(id.uuidString).png", imageSHA256.count == 64,
              imageSHA256.allSatisfy({ $0.isHexDigit }),
              !group.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              group.count <= 120, edges.count <= 10_000, ignoredRegions.count <= 500,
              runtimeForeground.count <= 500 else { throw GroundLabelError.invalid("Invalid frame metadata") }
        guard Set(edges.map(\.id)).count == edges.count,
              edges.allSatisfy({ $0.x0 >= 0 && $0.x1 >= $0.x0 && $0.x1 < width && $0.y >= 0 && $0.y < height })
        else { throw GroundLabelError.invalid("Ground edges must lie inside the image") }
        for region in ignoredRegions + runtimeForeground + (runtimeKnight.map { [$0] } ?? []) {
            guard [region.x, region.y, region.width, region.height].allSatisfy(\.isFinite),
                  region.width > 0, region.height > 0,
                  abs(region.x) <= 8192, abs(region.y) <= 8192,
                  region.width <= 8192, region.height <= 8192
            else { throw GroundLabelError.invalid("Invalid ignore region or capture mask") }
        }
    }
}

enum GroundLabelError: LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case let .invalid(message) = self { return message }; return nil }
}

struct GroundLabelStore {
    let root: URL
    static var defaultRoot: URL {
        if let path = ProcessInfo.processInfo.environment["HKV_GROUND_LABEL_ROOT"] {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HollowKnightVision/ground-labels-v1", isDirectory: true)
    }
    init(root: URL = Self.defaultRoot) { self.root = root }
    static func digest(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
    func documents() throws -> [GroundLabelDocument] {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }.map {
                let doc = try JSONDecoder().decode(GroundLabelDocument.self, from: Data(contentsOf: $0))
                try doc.validate()
                guard $0.lastPathComponent == "\(doc.id.uuidString).json" else {
                    throw GroundLabelError.invalid("Frame filename does not match its identifier")
                }
                return doc
            }.sorted { $0.createdAt < $1.createdAt }
    }
    func image(for document: GroundLabelDocument) throws -> CGImage {
        try document.validate()
        let data = try Data(contentsOf: root.appendingPathComponent(document.imageFile))
        guard Self.digest(data) == document.imageSHA256,
              let image = ImageFileIO.decode(data),
              image.width == document.width, image.height == document.height else {
            throw GroundLabelError.invalid("Source image changed or cannot be decoded")
        }
        return image
    }
    func save(_ document: GroundLabelDocument) throws {
        try document.validate()
        let others = try documents().filter { $0.id != document.id }
        try Self.validateSplits(others + [document])
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: root.appendingPathComponent("\(document.id.uuidString).json"), options: .atomic)
    }
    static func validateSplits(_ documents: [GroundLabelDocument]) throws {
        var groups = [String: GroundLabelSplit](), images = [String: GroundLabelSplit]()
        for document in documents {
            let group = document.group.trimmingCharacters(in: .whitespacesAndNewlines)
            if let previous = groups[group], previous != document.split {
                throw GroundLabelError.invalid("Group ‘\(group)’ cannot appear in both Train and Check. Use a separate capture session for Check.")
            }
            if let previous = images[document.imageSHA256], previous != document.split {
                throw GroundLabelError.invalid("The same source image cannot appear in both Train and Check")
            }
            groups[group] = document.split; images[document.imageSHA256] = document.split
        }
    }
    func add(image: CGImage, source: String, group: String,
             foreground: [GroundLabelRect] = [], knight: GroundLabelRect? = nil,
             masksKnown: Bool = false, knightKnown: Bool = false) throws -> GroundLabelDocument {
        guard (2...4096).contains(image.width), (2...4096).contains(image.height) else {
            throw GroundLabelError.invalid("Images must be between 2 and 4096 pixels on each side")
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let id = UUID(), filename = "\(UUID().uuidString).png"
        let temporary = root.appendingPathComponent(filename)
        guard let destination = CGImageDestinationCreateWithURL(temporary as CFURL, UTType.png.identifier as CFString, 1, nil)
        else { throw GroundLabelError.invalid("Cannot create the source PNG") }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw GroundLabelError.invalid("Cannot save the source PNG") }
        defer { try? FileManager.default.removeItem(at: temporary) }
        let data = try Data(contentsOf: temporary)
        let finalFile = "\(id.uuidString).png"
        var document = GroundLabelDocument(imageFile: finalFile, imageSHA256: Self.digest(data),
            width: image.width, height: image.height, createdAt: Date(), source: source, group: group,
            runtimeForeground: foreground, runtimeKnight: knight,
            runtimeMasksKnown: masksKnown, runtimeKnightKnown: knightKnown)
        document.id = id
        document.split = try documents().first(where: {
            $0.group.trimmingCharacters(in: .whitespacesAndNewlines)
                == group.trimmingCharacters(in: .whitespacesAndNewlines)
        })?.split ?? .train
        try data.write(to: root.appendingPathComponent(finalFile), options: .atomic)
        do { try save(document) }
        catch { try? FileManager.default.removeItem(at: root.appendingPathComponent(finalFile)); throw error }
        return document
    }
}

/// Canvas has the image's exact aspect ratio. Zoom changes only its scale.
enum GroundLabelCoordinates {
    static func pixel(_ point: CGPoint, displayedSize: CGSize, width: Int, height: Int) -> CGPoint? {
        guard displayedSize.width > 0, displayedSize.height > 0,
              point.x >= 0, point.y >= 0, point.x < displayedSize.width, point.y < displayedSize.height
        else { return nil }
        return CGPoint(x: min(width - 1, Int(point.x * CGFloat(width) / displayedSize.width)),
                       y: min(height - 1, Int(point.y * CGFloat(height) / displayedSize.height)))
    }
}
