import CoreGraphics
import Foundation
import SwiftUI

/// Development-only camera transform reported by the game mod. It is used by
/// the ground-label context and evaluation only; visual tracking never reads it.
struct GroundTruthCameraTransform: Equatable, Sendable {
    let sceneName: String
    let cameraWorld: CGPoint
    let pixelsPerWorldUnitX: CGFloat
    let pixelsPerWorldUnitY: CGFloat
    let frameSize: CGSize
    let observedAt: TimeInterval

    init?(
        sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval,
        frameSize: CGSize = CGSize(
            width: CGFloat(HollowKnightCaptureConfiguration.outputWidth),
            height: CGFloat(HollowKnightCaptureConfiguration.outputWidth)
                / HollowKnightCaptureConfiguration.gameplayAspectRatio
        )
    ) {
        guard sample.cameraAvailable,
              frameSize.width > 1, frameSize.height > 1,
              let projectionWidth = sample.projectionPixelWidth,
              let projectionHeight = sample.projectionPixelHeight,
              projectionWidth > 0, projectionHeight > 0 else { return nil }
        let rawY = sample.pixelsPerWorldUnitY
            ?? (sample.orthographicSize > 0
                ? Double(projectionHeight) / (2 * sample.orthographicSize) : 0)
        let rawX = sample.pixelsPerWorldUnitX ?? rawY
        let scaleX = CGFloat(rawX) * frameSize.width / CGFloat(projectionWidth)
        let scaleY = CGFloat(rawY) * frameSize.height / CGFloat(projectionHeight)
        guard scaleX.isFinite, scaleY.isFinite, scaleX > 0, scaleY > 0 else { return nil }
        sceneName = sample.sceneName
        cameraWorld = CGPoint(x: sample.cameraX, y: sample.cameraY)
        pixelsPerWorldUnitX = scaleX
        pixelsPerWorldUnitY = scaleY
        self.frameSize = frameSize
        self.observedAt = observedAt
    }

    init(
        sceneName: String,
        cameraWorld: CGPoint,
        pixelsPerWorldUnitX: CGFloat,
        pixelsPerWorldUnitY: CGFloat,
        frameSize: CGSize,
        observedAt: TimeInterval = 0
    ) {
        self.sceneName = sceneName
        self.cameraWorld = cameraWorld
        self.pixelsPerWorldUnitX = pixelsPerWorldUnitX
        self.pixelsPerWorldUnitY = pixelsPerWorldUnitY
        self.frameSize = frameSize
        self.observedAt = observedAt
    }

    func screenToWorld(_ topLeftPixel: CGPoint) -> CGPoint {
        CGPoint(
            x: cameraWorld.x + (topLeftPixel.x - frameSize.width * 0.5) / pixelsPerWorldUnitX,
            y: cameraWorld.y + (frameSize.height * 0.5 - topLeftPixel.y) / pixelsPerWorldUnitY
        )
    }

    func atlasToWorld(_ atlasPoint: CGPoint, liveBounds: CGRect) -> CGPoint {
        CGPoint(
            x: cameraWorld.x + (atlasPoint.x - liveBounds.midX) / pixelsPerWorldUnitX,
            y: cameraWorld.y + (atlasPoint.y - liveBounds.midY) / pixelsPerWorldUnitY
        )
    }

    func worldToAtlas(_ worldPoint: CGPoint, liveBounds: CGRect) -> CGPoint {
        CGPoint(
            x: liveBounds.midX + (worldPoint.x - cameraWorld.x) * pixelsPerWorldUnitX,
            y: liveBounds.midY + (worldPoint.y - cameraWorld.y) * pixelsPerWorldUnitY
        )
    }
}

enum GroundTruthLineKind: String, Codable, CaseIterable {
    case positive
    case negative
}

struct GroundTruthWorldLine: Codable, Equatable, Identifiable {
    var id = UUID()
    var sceneName: String
    var minimumWorldX: Double
    var maximumWorldX: Double
    var worldY: Double
    var kind: GroundTruthLineKind
    var seededFromDetector: Bool

    var standardized: Self {
        var result = self
        result.minimumWorldX = min(minimumWorldX, maximumWorldX)
        result.maximumWorldX = max(minimumWorldX, maximumWorldX)
        return result
    }
}

struct GroundTruthLineDocument: Codable, Equatable {
    var schemaVersion = 2
    var seededScenes = Set<String>()
    var lines = [GroundTruthWorldLine]()

    func validate() throws {
        guard schemaVersion == 2, lines.count <= 20_000,
              seededScenes.allSatisfy({ !$0.isEmpty && $0.count <= 256 }),
              Set(lines.map(\.id)).count == lines.count else {
            throw GroundTruthLineStoreError.invalid("Invalid ground-label document")
        }
        for line in lines {
            guard !line.sceneName.isEmpty, line.sceneName.count <= 256,
                  [line.minimumWorldX, line.maximumWorldX, line.worldY]
                    .allSatisfy({ $0.isFinite && abs($0) <= 1_000_000 }),
                  line.maximumWorldX >= line.minimumWorldX,
                  line.maximumWorldX - line.minimumWorldX <= 100_000 else {
                throw GroundTruthLineStoreError.invalid("Invalid world-space ground line")
            }
        }
    }
}

enum GroundTruthLineStoreError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        guard case let .invalid(message) = self else { return nil }
        return message
    }
}

struct GroundTruthLineStore {
    let url: URL

    static var defaultURL: URL {
        if let path = ProcessInfo.processInfo.environment["HKV_GROUND_LABEL_ROOT"] {
            let root = URL(fileURLWithPath: path, isDirectory: true)
            return root.appendingPathComponent("world-ground-lines-v2.json")
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HollowKnightVision/world-ground-lines-v2.json")
    }

    init(url: URL = Self.defaultURL) { self.url = url }

    func load() throws -> GroundTruthLineDocument {
        guard FileManager.default.fileExists(atPath: url.path) else {
            return GroundTruthLineDocument()
        }
        let document = try JSONDecoder().decode(
            GroundTruthLineDocument.self,
            from: Data(contentsOf: url)
        )
        try document.validate()
        return document
    }

    func save(_ document: GroundTruthLineDocument) throws {
        try document.validate()
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: url, options: .atomic)
    }
}

enum GroundTruthLineProjection {
    static func overlayLines(
        document: GroundTruthLineDocument,
        transform: GroundTruthCameraTransform,
        liveBounds: CGRect,
        selectedID: UUID?,
        draft: GroundTruthWorldLine?
    ) -> [LayerSceneFeatureReviewOverlay.Line] {
        let lines = document.lines + (draft.map { [$0] } ?? [])
        return lines.compactMap { line in
            guard line.sceneName == transform.sceneName else { return nil }
            let start = transform.worldToAtlas(
                CGPoint(x: line.minimumWorldX, y: line.worldY),
                liveBounds: liveBounds
            )
            let end = transform.worldToAtlas(
                CGPoint(x: line.maximumWorldX, y: line.worldY),
                liveBounds: liveBounds
            )
            guard [start.x, start.y, end.x, end.y].allSatisfy(\.isFinite) else { return nil }
            let kind: LayerSceneFeatureReviewLineKind
            if line.id == selectedID { kind = .selectedGroundTruth }
            else { kind = line.kind == .positive ? .positiveGroundTruth : .negativeGroundTruth }
            return .init(start: start, end: end, kind: kind)
        }
    }

    static func seedLines(
        tracking: GroundHypothesisTrackingResult,
        transform: GroundTruthCameraTransform,
        liveBounds: CGRect
    ) -> [GroundTruthWorldLine] {
        var result = [GroundTruthWorldLine]()
        for line in tracking.atlasLines {
            let first = transform.atlasToWorld(line.atlasStart, liveBounds: liveBounds)
            let last = transform.atlasToWorld(line.atlasEnd, liveBounds: liveBounds)
            appendSeed(
                GroundTruthWorldLine(
                    sceneName: transform.sceneName,
                    minimumWorldX: Double(min(first.x, last.x)),
                    maximumWorldX: Double(max(first.x, last.x)),
                    worldY: Double((first.y + last.y) * 0.5),
                    kind: .positive,
                    seededFromDetector: true
                ),
                pixelsPerWorldUnit: transform.pixelsPerWorldUnitY,
                to: &result
            )
        }
        for review in GroundLineDetector.featureHypothesisPresenceReviews(tracking.lineReviews) {
            let y = CGFloat(review.line.row) + 0.5
            let first = transform.screenToWorld(CGPoint(
                x: CGFloat(review.line.xRange.lowerBound), y: y
            ))
            let last = transform.screenToWorld(CGPoint(
                x: CGFloat(review.line.xRange.upperBound + 1), y: y
            ))
            appendSeed(
                GroundTruthWorldLine(
                    sceneName: transform.sceneName,
                    minimumWorldX: Double(min(first.x, last.x)),
                    maximumWorldX: Double(max(first.x, last.x)),
                    worldY: Double((first.y + last.y) * 0.5),
                    kind: .positive,
                    seededFromDetector: true
                ),
                pixelsPerWorldUnit: transform.pixelsPerWorldUnitY,
                to: &result
            )
        }
        return result
    }

    private static func appendSeed(
        _ proposed: GroundTruthWorldLine,
        pixelsPerWorldUnit: CGFloat,
        to result: inout [GroundTruthWorldLine]
    ) {
        let rowTolerance = 3 / Double(max(1, pixelsPerWorldUnit))
        let duplicate = result.contains { existing in
            guard abs(existing.worldY - proposed.worldY) <= rowTolerance else { return false }
            let overlap = min(existing.maximumWorldX, proposed.maximumWorldX)
                - max(existing.minimumWorldX, proposed.minimumWorldX)
            let shorter = min(
                existing.maximumWorldX - existing.minimumWorldX,
                proposed.maximumWorldX - proposed.minimumWorldX
            )
            return overlap >= max(0, shorter * 0.6)
        }
        if !duplicate { result.append(proposed.standardized) }
    }
}

enum LayerSceneGroundLabelPointerPhase { case began, changed, ended }

@MainActor
final class GroundTruthLabelController: ObservableObject {
    @Published var mode = LabelingEditorMode.add
    @Published private(set) var document: GroundTruthLineDocument
    @Published private(set) var selectedID: UUID?
    @Published private(set) var draft: GroundTruthWorldLine?
    @Published private(set) var error: String?

    private struct Snapshot {
        let document: GroundTruthLineDocument
        let selectedID: UUID?
    }
    private enum ModificationPart { case start, end, whole }
    private let store: GroundTruthLineStore
    private var history = [Snapshot]()
    private var original: GroundTruthWorldLine?
    private var modificationPart: ModificationPart?
    private var gestureStartWorld: CGPoint?
    private var mutatedOnBegin = false

    init(store: GroundTruthLineStore = GroundTruthLineStore()) {
        self.store = store
        do { document = try store.load() }
        catch {
            document = GroundTruthLineDocument()
            self.error = error.localizedDescription
        }
    }

    var canUndo: Bool { !history.isEmpty }
    var visiblePositiveCount: Int {
        document.lines.count { $0.kind == .positive }
    }
    var visibleNegativeCount: Int {
        document.lines.count { $0.kind == .negative }
    }

    func enter(
        tracking: GroundHypothesisTrackingResult,
        transform: GroundTruthCameraTransform?,
        liveBounds: CGRect
    ) {
        guard let transform else {
            error = "Camera transform unavailable"
            return
        }
        guard !document.seededScenes.contains(transform.sceneName) else {
            error = nil
            return
        }
        let seeds = GroundTruthLineProjection.seedLines(
            tracking: tracking,
            transform: transform,
            liveBounds: liveBounds
        )
        guard !seeds.isEmpty else {
            error = "Waiting for solved ground lines"
            return
        }
        recordSnapshot()
        document.lines.append(contentsOf: seeds)
        document.seededScenes.insert(transform.sceneName)
        persist()
    }

    func overlayLines(
        transform: GroundTruthCameraTransform?,
        liveBounds: CGRect
    ) -> [LayerSceneFeatureReviewOverlay.Line] {
        guard let transform else { return [] }
        return GroundTruthLineProjection.overlayLines(
            document: document,
            transform: transform,
            liveBounds: liveBounds,
            selectedID: selectedID,
            draft: draft
        )
    }

    func handle(
        phase: LayerSceneGroundLabelPointerPhase,
        atlasPoint: CGPoint,
        atlasTolerance: CGFloat,
        transform: GroundTruthCameraTransform?,
        liveBounds: CGRect
    ) {
        guard let transform else {
            error = "Camera transform unavailable"
            return
        }
        let world = transform.atlasToWorld(atlasPoint, liveBounds: liveBounds)
        let toleranceX = max(2 / transform.pixelsPerWorldUnitX,
                             atlasTolerance / transform.pixelsPerWorldUnitX)
        let toleranceY = max(2 / transform.pixelsPerWorldUnitY,
                             atlasTolerance / transform.pixelsPerWorldUnitY)
        switch phase {
        case .began:
            begin(world: world, toleranceX: toleranceX, toleranceY: toleranceY,
                  sceneName: transform.sceneName)
        case .changed:
            change(world: world)
        case .ended:
            change(world: world)
            finish(minimumLength: 4 / transform.pixelsPerWorldUnitX)
        }
    }

    func undo() {
        guard let snapshot = history.popLast() else { return }
        document = snapshot.document
        selectedID = snapshot.selectedID
        draft = nil
        persist()
    }

    func clearSelection() { selectedID = nil }

    private func begin(
        world: CGPoint,
        toleranceX: CGFloat,
        toleranceY: CGFloat,
        sceneName: String
    ) {
        draft = nil
        original = nil
        modificationPart = nil
        gestureStartWorld = world
        mutatedOnBegin = false
        let hit = nearestLine(
            to: world,
            sceneName: sceneName,
            toleranceX: toleranceX,
            toleranceY: toleranceY
        )
        switch mode {
        case .add:
            selectedID = nil
            draft = GroundTruthWorldLine(
                sceneName: sceneName,
                minimumWorldX: Double(world.x),
                maximumWorldX: Double(world.x),
                worldY: Double(world.y),
                kind: .positive,
                seededFromDetector: false
            )
        case .negative:
            if var hit {
                recordSnapshot()
                hit.kind = .negative
                replace(hit)
                selectedID = hit.id
                mutatedOnBegin = true
                persist()
            } else {
                selectedID = nil
                draft = GroundTruthWorldLine(
                    sceneName: sceneName,
                    minimumWorldX: Double(world.x),
                    maximumWorldX: Double(world.x),
                    worldY: Double(world.y),
                    kind: .negative,
                    seededFromDetector: false
                )
            }
        case .delete:
            guard let hit else { selectedID = nil; return }
            recordSnapshot()
            document.lines.removeAll { $0.id == hit.id }
            selectedID = nil
            mutatedOnBegin = true
            persist()
        case .modify:
            guard let hit else { selectedID = nil; return }
            selectedID = hit.id
            original = hit
            recordSnapshot()
            let x = CGFloat(world.x)
            if abs(x - CGFloat(hit.minimumWorldX)) <= toleranceX {
                modificationPart = .start
            } else if abs(x - CGFloat(hit.maximumWorldX)) <= toleranceX {
                modificationPart = .end
            } else {
                modificationPart = .whole
            }
        }
    }

    private func change(world: CGPoint) {
        if var draft {
            guard let start = gestureStartWorld else { return }
            draft.minimumWorldX = Double(min(start.x, world.x))
            draft.maximumWorldX = Double(max(start.x, world.x))
            draft.worldY = Double(start.y)
            self.draft = draft
            return
        }
        guard mode == .modify, let original, let start = gestureStartWorld,
              let modificationPart else { return }
        var changed = original
        switch modificationPart {
        case .start:
            changed.minimumWorldX = Double(min(world.x, CGFloat(changed.maximumWorldX)))
        case .end:
            changed.maximumWorldX = Double(max(world.x, CGFloat(changed.minimumWorldX)))
        case .whole:
            let dx = world.x - start.x
            changed.minimumWorldX += Double(dx)
            changed.maximumWorldX += Double(dx)
            changed.worldY += Double(world.y - start.y)
        }
        replace(changed.standardized)
    }

    private func finish(minimumLength: CGFloat) {
        defer {
            draft = nil
            original = nil
            modificationPart = nil
            gestureStartWorld = nil
            mutatedOnBegin = false
        }
        if var draft {
            guard CGFloat(draft.maximumWorldX - draft.minimumWorldX) >= minimumLength else { return }
            recordSnapshot()
            draft = draft.standardized
            document.lines.append(draft)
            selectedID = draft.id
            persist()
        } else if mode == .modify, original != nil {
            persist()
        } else if mutatedOnBegin {
            persist()
        }
    }

    private func nearestLine(
        to point: CGPoint,
        sceneName: String,
        toleranceX: CGFloat,
        toleranceY: CGFloat
    ) -> GroundTruthWorldLine? {
        document.lines.filter { line in
            line.sceneName == sceneName
                && abs(CGFloat(line.worldY) - point.y) <= toleranceY
                && point.x >= CGFloat(line.minimumWorldX) - toleranceX
                && point.x <= CGFloat(line.maximumWorldX) + toleranceX
        }.min {
            abs(CGFloat($0.worldY) - point.y) < abs(CGFloat($1.worldY) - point.y)
        }
    }

    private func replace(_ line: GroundTruthWorldLine) {
        guard let index = document.lines.firstIndex(where: { $0.id == line.id }) else { return }
        document.lines[index] = line
    }

    private func recordSnapshot() {
        history.append(Snapshot(document: document, selectedID: selectedID))
        if history.count > 100 { history.removeFirst() }
    }

    private func persist() {
        do {
            try store.save(document)
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
    }
}

struct GroundTruthLabelToolbar: View {
    @ObservedObject var controller: GroundTruthLabelController
    let hasCameraTransform: Bool
    var modes = LabelingEditorMode.allCases
    var onDone: (() -> Void)? = nil

    var body: some View {
        HStack(spacing: 8) {
            StableSegmentedPicker(
                label: "Ground editing mode",
                choices: modes,
                title: { $0.rawValue },
                selection: $controller.mode
            )
            .frame(width: 290)
            Button("Undo", action: controller.undo)
                .disabled(!controller.canUndo)
            Text("+\(controller.visiblePositiveCount)  −\(controller.visibleNegativeCount)")
                .monospacedDigit()
                .foregroundStyle(.secondary)
            if !hasCameraTransform {
                Image(systemName: "camera.metering.unknown")
                    .foregroundStyle(.orange)
                    .help("Waiting for the game camera transform")
            }
            if let error = controller.error {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .help(error)
            }
            Spacer()
            if let onDone { Button("Done", action: onDone) }
        }
        .font(.caption)
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .background(.regularMaterial)
        .overlay {
            ZStack {
                Button("Undo ground edit", action: controller.undo)
                    .keyboardShortcut("z", modifiers: .command)
                ForEach(modes) { mode in
                    Button("\(mode.rawValue) ground mode") { controller.mode = mode }
                        .keyboardShortcut(KeyEquivalent(mode.shortcutCharacter), modifiers: [])
                }
            }
            .frame(width: 1, height: 1)
            .opacity(0)
            .accessibilityHidden(true)
        }
    }
}
