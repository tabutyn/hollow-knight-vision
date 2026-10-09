import CoreGraphics
import Foundation

struct LabelingObjectAtlasSnapshot: @unchecked Sendable {
    static let imageSize = 2048
    static let slotSize = 128
    static let columns = imageSize / slotSize
    static let capacity = columns * columns

    let image: CGImage
    let classIdentifiers: [String]
    let iconRects: [CGRect]
    private let icons: [String: CGImage]

    init(image: CGImage, classIdentifiers: [String], iconRects: [CGRect]) {
        self.image = image
        self.classIdentifiers = classIdentifiers
        self.iconRects = iconRects
        var icons = [String: CGImage]()
        for (index, identifier) in classIdentifiers.enumerated() {
            guard index < iconRects.count, !iconRects[index].isEmpty else { continue }
            let rect = iconRects[index].offsetBy(
                dx: CGFloat((index % Self.columns) * Self.slotSize),
                dy: CGFloat((index / Self.columns) * Self.slotSize)
            )
            icons[identifier] = image.cropping(to: rect)
        }
        self.icons = icons
    }

    func icon(for classIdentifier: String) -> CGImage? {
        icons[LabelingClassIdentity.canonicalIdentifier(classIdentifier)]
    }
}

struct LabelingObjectAtlasBuildInput: @unchecked Sendable {
    let examples: [SavedLabelingExample]
    let classIdentifiers: [String]
    let preferredExampleIdentifiers: [String: UUID]
}

private struct LabelingObjectAtlasMetadata: Codable, Equatable {
    let schemaVersion: Int
    let fingerprint: String
    let classIdentifiers: [String]
    let iconRects: [CGRect]
}

final class LabelingObjectAtlasStore {
    static let imageFilename = "object-atlas.png"
    static let metadataFilename = "object-atlas.json"

    private let rootURL: URL
    private let fileManager: FileManager

    init(
        rootURL: URL = LabelingExampleStore.defaultRootURL().deletingLastPathComponent(),
        fileManager: FileManager = .default
    ) {
        self.rootURL = rootURL.standardizedFileURL
        self.fileManager = fileManager
    }

    func loadOrBuild(
        input: LabelingObjectAtlasBuildInput,
        force: Bool = false
    ) throws -> LabelingObjectAtlasSnapshot {
        let identifiers = Array(Set(input.classIdentifiers.map(
            LabelingClassIdentity.canonicalIdentifier
        ))).sorted()
        guard identifiers.count <= LabelingObjectAtlasSnapshot.capacity else {
            throw CocoaError(.fileWriteOutOfSpace)
        }
        let fingerprint = fingerprint(input: input, identifiers: identifiers)
        if !force, let cached = load(fingerprint: fingerprint, identifiers: identifiers) {
            return cached
        }
        let snapshot = try build(input: input, identifiers: identifiers)
        try save(snapshot, fingerprint: fingerprint)
        return snapshot
    }

    private func load(
        fingerprint: String,
        identifiers: [String]
    ) -> LabelingObjectAtlasSnapshot? {
        let metadataURL = rootURL.appendingPathComponent(Self.metadataFilename)
        let imageURL = rootURL.appendingPathComponent(Self.imageFilename)
        guard let data = try? Data(contentsOf: metadataURL),
              let metadata = try? JSONDecoder().decode(LabelingObjectAtlasMetadata.self, from: data),
              metadata.schemaVersion == 2,
              metadata.fingerprint == fingerprint,
              metadata.classIdentifiers == identifiers,
              metadata.iconRects.count == identifiers.count,
              let image = ImageFileIO.load(imageURL) else { return nil }
        return LabelingObjectAtlasSnapshot(
            image: image,
            classIdentifiers: identifiers,
            iconRects: metadata.iconRects
        )
    }

    private func build(
        input: LabelingObjectAtlasBuildInput,
        identifiers: [String]
    ) throws -> LabelingObjectAtlasSnapshot {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: nil,
            width: LabelingObjectAtlasSnapshot.imageSize,
            height: LabelingObjectAtlasSnapshot.imageSize,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw CocoaError(.fileWriteUnknown)
        }
        context.clear(CGRect(
            x: 0,
            y: 0,
            width: LabelingObjectAtlasSnapshot.imageSize,
            height: LabelingObjectAtlasSnapshot.imageSize
        ))

        let sortedExamples = input.examples.sorted {
            $0.manifest.createdAt < $1.manifest.createdAt
        }
        var iconRects = [CGRect](repeating: .zero, count: identifiers.count)
        for (index, identifier) in identifiers.enumerated() {
            let references = sortedExamples.flatMap { example in
                example.manifest.annotations.compactMap { annotation
                    -> (SavedLabelingExample, LabelingExampleAnnotation)? in
                    guard !annotation.isHardNegative,
                          LabelingClassIdentity.matches(annotation.classIdentifier, identifier)
                    else { return nil }
                    return (example, annotation)
                }
            }
            let preferred = input.preferredExampleIdentifiers[identifier]
            guard let reference = references.first(where: { $0.0.id == preferred })
                    ?? references.first,
                  let sourceImage = loadImage(for: reference.0),
                  let crop = crop(sourceImage, normalizedRect: reference.1.normalizedRect)
            else { continue }

            let column = index % LabelingObjectAtlasSnapshot.columns
            let topRow = index / LabelingObjectAtlasSnapshot.columns
            let slot = CGRect(
                x: column * LabelingObjectAtlasSnapshot.slotSize,
                y: LabelingObjectAtlasSnapshot.imageSize
                    - (topRow + 1) * LabelingObjectAtlasSnapshot.slotSize,
                width: LabelingObjectAtlasSnapshot.slotSize,
                height: LabelingObjectAtlasSnapshot.slotSize
            )
            let scale = min(
                slot.width / CGFloat(crop.width),
                slot.height / CGFloat(crop.height)
            )
            let destination = CGRect(
                x: slot.midX - CGFloat(crop.width) * scale / 2,
                y: slot.midY - CGFloat(crop.height) * scale / 2,
                width: CGFloat(crop.width) * scale,
                height: CGFloat(crop.height) * scale
            )
            context.interpolationQuality = .high
            context.draw(crop, in: destination)
            iconRects[index] = CGRect(
                x: destination.minX - slot.minX,
                y: slot.maxY - destination.maxY,
                width: destination.width,
                height: destination.height
            ).integral.intersection(CGRect(
                x: 0,
                y: 0,
                width: LabelingObjectAtlasSnapshot.slotSize,
                height: LabelingObjectAtlasSnapshot.slotSize
            ))
        }
        guard let image = context.makeImage() else { throw CocoaError(.fileWriteUnknown) }
        return LabelingObjectAtlasSnapshot(
            image: image,
            classIdentifiers: identifiers,
            iconRects: iconRects
        )
    }

    private func save(
        _ snapshot: LabelingObjectAtlasSnapshot,
        fingerprint: String
    ) throws {
        try fileManager.createDirectory(at: rootURL, withIntermediateDirectories: true)
        let imageURL = rootURL.appendingPathComponent(Self.imageFilename)
        guard ImageFileIO.writePNG(snapshot.image, to: imageURL) else {
            throw CocoaError(.fileWriteUnknown)
        }
        let metadata = LabelingObjectAtlasMetadata(
            schemaVersion: 2,
            fingerprint: fingerprint,
            classIdentifiers: snapshot.classIdentifiers,
            iconRects: snapshot.iconRects
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(metadata).write(
            to: rootURL.appendingPathComponent(Self.metadataFilename),
            options: .atomic
        )
    }

    private func fingerprint(
        input: LabelingObjectAtlasBuildInput,
        identifiers: [String]
    ) -> String {
        var components = ["v2", identifiers.joined(separator: ",")]
        for example in input.examples.sorted(by: { $0.id.uuidString < $1.id.uuidString }) {
            components.append(example.id.uuidString)
            for annotation in example.manifest.annotations.sorted(by: {
                $0.id.uuidString < $1.id.uuidString
            }) where !annotation.isHardNegative {
                components.append(
                    "\(annotation.id.uuidString):\(LabelingClassIdentity.canonicalIdentifier(annotation.classIdentifier))"
                )
            }
        }
        for key in input.preferredExampleIdentifiers.keys.sorted() {
            components.append("star:\(key):\(input.preferredExampleIdentifiers[key]!.uuidString)")
        }
        return components.joined(separator: "|")
    }

    private func loadImage(for example: SavedLabelingExample) -> CGImage? {
        let url = example.directoryURL.appendingPathComponent(example.manifest.imageFilename)
        return ImageFileIO.load(url)
    }

    private func crop(_ image: CGImage, normalizedRect: CGRect) -> CGImage? {
        let normalized = normalizedRect.standardized
        let rect = CGRect(
            x: normalized.minX * CGFloat(image.width),
            y: normalized.minY * CGFloat(image.height),
            width: normalized.width * CGFloat(image.width),
            height: normalized.height * CGFloat(image.height)
        ).integral.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard !rect.isEmpty else { return nil }
        return image.cropping(to: rect)
    }
}
