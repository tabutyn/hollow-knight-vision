import CoreGraphics
import CoreImage
import Foundation
import OSLog

enum LiveTiledAtlasPixelPolicy {
    case newestOpaque
    case newestQuilted
    case preserveBrighter
    case temporalAgreement
    case temporalQuilting

    var usesTemporalEvidence: Bool {
        switch self {
        case .temporalAgreement, .temporalQuilting: true
        case .newestOpaque, .newestQuilted, .preserveBrighter: false
        }
    }
}

struct LiveTiledAtlasTemporalDiagnostics: Equatable {
    let retainedFrameCount: Int
    let retainedDecodedBytes: Int
    let lastFilterMilliseconds: Double
    let lastConsensusPixelCount: Int
    let lastImmediatePixelCount: Int

    static let empty = LiveTiledAtlasTemporalDiagnostics(
        retainedFrameCount: 0,
        retainedDecodedBytes: 0,
        lastFilterMilliseconds: 0,
        lastConsensusPixelCount: 0,
        lastImmediatePixelCount: 0
    )
}

/// Immutable evidence for the flat, live world atlas.  The image must already
/// have its HUD and other rejected regions masked transparent.
struct LiveTiledAtlasObservation: Identifiable {
    let id: Int
    let maskedImage: CGImage
    let solveWidth: CGFloat
    let cameraPosition: CGPoint
    let captureIdentity: Int64
    let timestamp: Double?
    let roomID: Int
    let captureGeneration: UInt64

    init(
        id: Int,
        maskedImage: CGImage,
        solveWidth: CGFloat,
        cameraPosition: CGPoint,
        captureIdentity: Int64? = nil,
        timestamp: Double? = nil,
        roomID: Int = 0,
        captureGeneration: UInt64 = 0
    ) {
        self.id = id
        self.maskedImage = maskedImage
        self.solveWidth = solveWidth
        self.cameraPosition = cameraPosition
        self.captureIdentity = captureIdentity ?? Int64(id)
        self.timestamp = timestamp
        self.roomID = roomID
        self.captureGeneration = captureGeneration
    }
}

struct LiveTiledAtlasTile: Identifiable {
    /// A reversible packing of the signed 32-bit tile coordinates.  Atlas
    /// coordinates are deliberately bounded to that range before projection.
    let id: Int
    let key: Int
    let tileX: Int
    let tileY: Int
    let image: CGImage
    let worldBounds: CGRect
    /// Retained source metadata for diagnostics and targeted invalidation.
    let contributionIDs: [Int]
}

struct LiveTiledAtlasSnapshot {
    let tiles: [LiveTiledAtlasTile]
    let revision: UInt64
    let totalBounds: CGRect
    /// Exact union of registered image footprints. Storage tiles deliberately
    /// extend past this rect and must not drive Fit framing.
    let contentBounds: CGRect
}

/// A sparse, source-backed flat map for the live path.  Unlike
/// a monolithic world image, it never reallocates the entire map or
/// resets when the world becomes large.  A caller always reads one published
/// snapshot, so pose corrections cannot expose a partly rebuilt map.
final class LiveTiledAtlas {
    static let tileSize = 256
    private static let temporalWindowCapacity = 7
    private static let temporalWindowDuration = 1.0
    private static let temporalAgreementTolerance = 24
    private static let newestQuiltOverlap = 8
    private static let quiltPatchSize = 32
    private static let quiltOverlap = 8
    private static let quiltStride = quiltPatchSize - quiltOverlap
    private static let quiltDescriptorStep = 4
    private static let quiltMinimumDescriptorSamples = 24
    private static let quiltTextureTolerance = 18
    private static let quiltIlluminationTolerance = 72

    private struct TileCoordinate: Hashable {
        let x: Int
        let y: Int

        var bounds: CGRect {
            CGRect(
                x: x * LiveTiledAtlas.tileSize,
                y: y * LiveTiledAtlas.tileSize,
                width: LiveTiledAtlas.tileSize,
                height: LiveTiledAtlas.tileSize
            )
        }

        var stableID: Int {
            let high = UInt(UInt32(truncatingIfNeeded: x)) << 32
            let low = UInt(UInt32(truncatingIfNeeded: y))
            return Int(bitPattern: high | low)
        }
    }

    private struct Contribution {
        let observationID: Int
        let imageWidth: Int
        let imageHeight: Int
        let solveWidth: CGFloat
        var cameraPosition: CGPoint
        let insertionOrder: UInt64
        let captureIdentity: Int64
        let timestamp: Double?
        let roomID: Int
        let captureGeneration: UInt64
        let temporalWindowIDs: [Int]
        var tileCoordinates: Set<TileCoordinate>
    }

    private struct TemporalCaptureKey: Hashable {
        let identity: Int64
        let roomID: Int
        let captureGeneration: UInt64
    }

    private struct TemporalContext: Equatable {
        let imageWidth: Int
        let imageHeight: Int
        let roomID: Int
        let captureGeneration: UInt64
    }

    private struct DecodedSource {
        let contribution: Contribution
        let pixels: [UInt8]
        let baseX: Int
        let baseY: Int
    }

    private struct QuiltPatchMember {
        let sourceIndex: Int
        let meanRed: Int
        let meanGreen: Int
        let meanBlue: Int
        let brightness: Int
        let textureEnergy: Int
    }

    private struct NewestQuiltSeams {
        let minSourceX: Int
        let minSourceY: Int
        let left: (offset: Int, values: [Int])?
        let right: (offset: Int, values: [Int])?
        let bottom: (offset: Int, values: [Int])?
        let top: (offset: Int, values: [Int])?
    }

    private final class TemporalCandidateBuffers {
        var width = 0
        var height = 0
        var originX = 0
        var originY = 0
        var rgba = [UInt8]()
        var hasEnoughSamples = [Bool]()
        var hasConsensus = [Bool]()

        func reset(width: Int, height: Int, originX: Int, originY: Int) {
            self.width = width
            self.height = height
            self.originX = originX
            self.originY = originY
            let pixelCount = width * height
            refill(&rgba, count: pixelCount * 4, with: UInt8(0))
            refill(&hasEnoughSamples, count: pixelCount, with: false)
            refill(&hasConsensus, count: pixelCount, with: false)
        }

        private func refill<Element>(
            _ values: inout [Element],
            count: Int,
            with value: Element
        ) {
            values.removeAll(keepingCapacity: true)
            values.append(contentsOf: repeatElement(value, count: count))
        }
    }

    private struct TemporalWork {
        var consensusPixelCount = 0
        var immediatePixelCount = 0
    }

    private enum TileRenderResult {
        case tile(LiveTiledAtlasTile)
        case empty
        case cancelled
    }

    private let lock = NSLock()
    private let temporalLog = Logger(
        subsystem: "com.ballroller.hollow-knight-vision",
        category: "atlas-temporal"
    )
    private let sourceProvider: ((Int) -> CGImage?)?
    private let sourceCacheLimit: Int
    private let pixelPolicy: LiveTiledAtlasPixelPolicy
    private var sourceCache = [Int: CGImage]()
    private var sourceCacheOrder = [Int]()
    private var decodedSourceCache = [Int: [UInt8]]()
    private var decodedSourceCacheOrder = [Int]()
    private var contributions = [Int: Contribution]()
    private var contributionIDsByTile = [TileCoordinate: Set<Int>]()
    private var anchorPosition: CGPoint?
    private var nextInsertionOrder: UInt64 = 0
    private var temporalContext: TemporalContext?
    private var temporalHistoryIDs = [Int]()
    private var observationIDByCaptureKey = [TemporalCaptureKey: Int]()
    private var temporalInsertCount = 0
    private var temporalWork = TemporalWork()
    private var temporalDiagnosticsValue = LiveTiledAtlasTemporalDiagnostics.empty
    private let temporalCandidateBuffers = TemporalCandidateBuffers()
    private var published = LiveTiledAtlasSnapshot(
        tiles: [], revision: 0, totalBounds: .zero, contentBounds: .zero
    )
    // Diagnostic work counter for the last publication.  An incremental insert
    // counts only its source once per affected tile; a rebuild counts every
    // source it rasterizes.  This makes the live-path cost testable without a
    // wall-clock threshold.
    private var lastRasterizedContributionCount = 0

    init(
        context _: CIContext,
        anchorPosition: CGPoint? = nil,
        sourceProvider: ((Int) -> CGImage?)? = nil,
        sourceCacheLimit: Int = 12,
        pixelPolicy: LiveTiledAtlasPixelPolicy = .newestOpaque
    ) {
        self.anchorPosition = Self.validPose(anchorPosition) ? anchorPosition : nil
        self.sourceProvider = sourceProvider
        self.sourceCacheLimit = max(0, sourceCacheLimit)
        self.pixelPolicy = pixelPolicy
    }

    /// The fixed origin, expressed in solve units.  It is selected by the
    /// first accepted observation unless a caller supplies one at construction.
    var fixedAnchorPosition: CGPoint? {
        lock.lock(); defer { lock.unlock() }
        return anchorPosition
    }

    var snapshot: LiveTiledAtlasSnapshot {
        lock.lock(); defer { lock.unlock() }
        return published
    }

    var tiles: [LiveTiledAtlasTile] { snapshot.tiles }
    var revision: UInt64 { snapshot.revision }
    var totalBounds: CGRect { snapshot.totalBounds }

    var lastPublicationRasterizedContributionCount: Int {
        lock.lock(); defer { lock.unlock() }
        return lastRasterizedContributionCount
    }

    var temporalDiagnostics: LiveTiledAtlasTemporalDiagnostics {
        lock.lock(); defer { lock.unlock() }
        return temporalDiagnosticsValue
    }

    /// The retained links are metadata only; no per-pixel candidate history is
    /// kept between rebuilds.
    var retainedContributionTileLinkCount: Int {
        lock.lock(); defer { lock.unlock() }
        return contributionIDsByTile.values.reduce(0) { $0 + $1.count }
    }

    func reset(anchorPosition: CGPoint? = nil) {
        lock.lock(); defer { lock.unlock() }
        contributions.removeAll(keepingCapacity: true)
        contributionIDsByTile.removeAll(keepingCapacity: true)
        sourceCache.removeAll(keepingCapacity: true)
        sourceCacheOrder.removeAll(keepingCapacity: true)
        decodedSourceCache.removeAll(keepingCapacity: true)
        decodedSourceCacheOrder.removeAll(keepingCapacity: true)
        temporalContext = nil
        temporalHistoryIDs.removeAll(keepingCapacity: true)
        observationIDByCaptureKey.removeAll(keepingCapacity: true)
        temporalInsertCount = 0
        temporalWork = TemporalWork()
        temporalDiagnosticsValue = .empty
        self.anchorPosition = Self.validPose(anchorPosition) ? anchorPosition : nil
        nextInsertionOrder = 0
        lastRasterizedContributionCount = 0
        published = LiveTiledAtlasSnapshot(
            tiles: [], revision: published.revision &+ 1,
            totalBounds: .zero, contentBounds: .zero
        )
    }

    @discardableResult
    func insert(_ observation: LiveTiledAtlasObservation) -> Bool {
        insert(
            observationID: observation.id,
            maskedImage: observation.maskedImage,
            solveWidth: observation.solveWidth,
            cameraPosition: observation.cameraPosition,
            captureIdentity: observation.captureIdentity,
            timestamp: observation.timestamp,
            roomID: observation.roomID,
            captureGeneration: observation.captureGeneration
        )
    }

    /// Ends the live voting window without changing committed atlas pixels.
    /// Rebuild metadata remains attached to each immutable contribution.
    func endTemporalWindow() {
        lock.lock(); defer { lock.unlock() }
        temporalContext = nil
        temporalHistoryIDs.removeAll(keepingCapacity: true)
        decodedSourceCache.removeAll(keepingCapacity: true)
        decodedSourceCacheOrder.removeAll(keepingCapacity: true)
        updateTemporalDiagnostics(duration: 0, work: TemporalWork(), shouldLog: false)
    }

    /// Inserting an existing ID updates its retained evidence in place.  The
    /// identifier itself remains immutable and is never reassigned.
    @discardableResult
    func insert(
        observationID: Int,
        maskedImage: CGImage,
        solveWidth: CGFloat,
        cameraPosition: CGPoint,
        captureIdentity: Int64? = nil,
        timestamp: Double? = nil,
        roomID: Int = 0,
        captureGeneration: UInt64 = 0,
        shouldCancel: () -> Bool = { false }
    ) -> Bool {
        guard observationID >= 0,
              validImage(maskedImage),
              solveWidth.isFinite, solveWidth > 0,
              Self.validPose(cameraPosition),
              !shouldCancel()
        else { return false }

        guard timestamp == nil || timestamp?.isFinite == true else { return false }
        lock.lock(); defer { lock.unlock() }
        guard !shouldCancel() else { return false }
        let resolvedCaptureIdentity = captureIdentity ?? Int64(observationID)
        let captureKey = TemporalCaptureKey(
            identity: resolvedCaptureIdentity,
            roomID: roomID,
            captureGeneration: captureGeneration
        )
        if pixelPolicy.usesTemporalEvidence,
           let existingID = observationIDByCaptureKey[captureKey],
           existingID != observationID {
            return true
        }
        let previousAnchorPosition = anchorPosition
        let previousInsertionOrder = nextInsertionOrder
        let previousCachedImage = sourceCache[observationID]
        let previousCacheOrder = sourceCacheOrder
        let previousDecodedSourceCache = decodedSourceCache
        let previousDecodedOrder = decodedSourceCacheOrder
        let previousTemporalContext = temporalContext
        let previousTemporalHistoryIDs = temporalHistoryIDs
        let previousCaptureObservation = observationIDByCaptureKey[captureKey]
        let previousTemporalWork = temporalWork
        if anchorPosition == nil { anchorPosition = cameraPosition }
        guard let fixedAnchorPosition = anchorPosition else { return false }

        var dirty = Set<TileCoordinate>()
        let replacing = contributions[observationID] != nil
        let oldContribution = contributions.removeValue(forKey: observationID)
        if let old = oldContribution {
            dirty.formUnion(old.tileCoordinates)
            unlink(old)
        }
        let temporalWindowIDs: [Int]
        if let oldContribution {
            temporalWindowIDs = oldContribution.temporalWindowIDs
        } else if pixelPolicy.usesTemporalEvidence {
            temporalWindowIDs = prepareTemporalWindow(
                observationID: observationID,
                imageWidth: maskedImage.width,
                imageHeight: maskedImage.height,
                timestamp: timestamp,
                roomID: roomID,
                captureGeneration: captureGeneration
            )
            observationIDByCaptureKey[captureKey] = observationID
        } else {
            temporalWindowIDs = [observationID]
        }
        nextInsertionOrder &+= 1
        var contribution = Contribution(
            observationID: observationID,
            imageWidth: maskedImage.width,
            imageHeight: maskedImage.height,
            solveWidth: solveWidth,
            cameraPosition: cameraPosition,
            insertionOrder: nextInsertionOrder,
            captureIdentity: resolvedCaptureIdentity,
            timestamp: timestamp,
            roomID: roomID,
            captureGeneration: captureGeneration,
            temporalWindowIDs: temporalWindowIDs,
            tileCoordinates: []
        )
        contribution.tileCoordinates = projectedTiles(for: contribution, anchor: fixedAnchorPosition)
        contributions[observationID] = contribution
        link(contribution)
        decodedSourceCache.removeValue(forKey: observationID)
        decodedSourceCacheOrder.removeAll { $0 == observationID }
        cache(maskedImage, for: observationID)
        dirty.formUnion(contribution.tileCoordinates)
        temporalWork = TemporalWork()
        let temporalStarted = ProcessInfo.processInfo.systemUptime
        let published: Bool
        if replacing {
            published = publishRebuild(
                dirty,
                incrementingRevision: true,
                shouldCancel: shouldCancel
            )
        } else {
            published = publishIncremental(
                contribution,
                sourceImage: maskedImage,
                shouldCancel: shouldCancel
            )
        }
        guard published else {
            unlink(contribution)
            contributions.removeValue(forKey: observationID)
            if let oldContribution {
                contributions[observationID] = oldContribution
                link(oldContribution)
            }
            if let previousCachedImage {
                sourceCache[observationID] = previousCachedImage
            } else {
                sourceCache.removeValue(forKey: observationID)
            }
            sourceCacheOrder = previousCacheOrder
            decodedSourceCache = previousDecodedSourceCache
            decodedSourceCacheOrder = previousDecodedOrder
            temporalContext = previousTemporalContext
            temporalHistoryIDs = previousTemporalHistoryIDs
            temporalWork = previousTemporalWork
            if let previousCaptureObservation {
                observationIDByCaptureKey[captureKey] = previousCaptureObservation
            } else {
                observationIDByCaptureKey.removeValue(forKey: captureKey)
            }
            anchorPosition = previousAnchorPosition
            nextInsertionOrder = previousInsertionOrder
            return false
        }
        if pixelPolicy.usesTemporalEvidence {
            let retainedIDs = Set(temporalHistoryIDs)
            decodedSourceCache = decodedSourceCache.filter { retainedIDs.contains($0.key) }
            decodedSourceCacheOrder.removeAll { !retainedIDs.contains($0) }
            temporalInsertCount &+= 1
            updateTemporalDiagnostics(
                duration: ProcessInfo.processInfo.systemUptime - temporalStarted,
                work: temporalWork,
                shouldLog: temporalInsertCount == 1 || temporalInsertCount.isMultiple(of: 60)
            )
        }
        return true
    }

    @discardableResult
    func update(_ observation: LiveTiledAtlasObservation) -> Bool {
        insert(observation)
    }

    /// Applies a complete pose-graph revision.  A correction based on an old
    /// visible snapshot, or one missing any retained observation, is rejected.
    /// All changed tiles are rendered before the new snapshot is assigned.
    @discardableResult
    func replaceCameraPositions(
        _ positions: [Int: CGPoint],
        baseRevision: UInt64,
        shouldCancel: () -> Bool = { false }
    ) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard baseRevision == published.revision,
              positions.count == contributions.count,
              Set(positions.keys) == Set(contributions.keys),
              positions.values.allSatisfy(Self.validPose)
        else { return false }

        guard let anchorPosition else { return contributions.isEmpty }
        guard !shouldCancel() else { return false }
        let previousDecodedSourceCache = decodedSourceCache
        let previousDecodedOrder = decodedSourceCacheOrder
        let previousTemporalWork = temporalWork
        var dirty = Set<TileCoordinate>()
        var originalPositions = [Int: CGPoint]()
        for id in contributions.keys.sorted() {
            if shouldCancel() {
                restoreCameraPositions(originalPositions, anchor: anchorPosition)
                return false
            }
            guard var contribution = contributions[id], let position = positions[id] else { continue }
            guard contribution.cameraPosition != position else { continue }
            originalPositions[id] = contribution.cameraPosition
            dirty.formUnion(contribution.tileCoordinates)
            unlink(contribution)
            contribution.cameraPosition = position
            contribution.tileCoordinates = projectedTiles(for: contribution, anchor: anchorPosition)
            contributions[id] = contribution
            link(contribution)
            dirty.formUnion(contribution.tileCoordinates)
        }
        // A successfully accepted graph revision is observable even when the
        // solved positions happen to be unchanged.
        guard publishRebuild(
            dirty,
            incrementingRevision: true,
            shouldCancel: shouldCancel
        ) else {
            restoreCameraPositions(originalPositions, anchor: anchorPosition)
            decodedSourceCache = previousDecodedSourceCache
            decodedSourceCacheOrder = previousDecodedOrder
            temporalWork = previousTemporalWork
            return false
        }
        return true
    }

    private func restoreCameraPositions(
        _ positions: [Int: CGPoint], anchor: CGPoint
    ) {
        for id in positions.keys.sorted() {
            guard var contribution = contributions[id], let position = positions[id] else { continue }
            unlink(contribution)
            contribution.cameraPosition = position
            contribution.tileCoordinates = projectedTiles(for: contribution, anchor: anchor)
            contributions[id] = contribution
            link(contribution)
        }
    }

    @discardableResult
    private func publishRebuild(
        _ dirty: Set<TileCoordinate>,
        incrementingRevision: Bool,
        shouldCancel: () -> Bool = { false }
    ) -> Bool {
        lastRasterizedContributionCount = 0
        var current = Dictionary(uniqueKeysWithValues: published.tiles.map {
            (TileCoordinate(x: $0.tileX, y: $0.tileY), $0)
        })
        for coordinate in dirty.sorted(by: Self.tileOrder) {
            guard !shouldCancel() else { return false }
            switch renderTile(at: coordinate, shouldCancel: shouldCancel) {
            case .tile(let tile):
                current[coordinate] = tile
            case .empty:
                current.removeValue(forKey: coordinate)
            case .cancelled:
                return false
            }
        }
        let tiles = current.values.sorted {
            Self.tileOrder(
                TileCoordinate(x: $0.tileX, y: $0.tileY),
                TileCoordinate(x: $1.tileX, y: $1.tileY)
            )
        }
        let bounds = tiles.reduce(CGRect.null) { $0.union($1.worldBounds) }
        published = LiveTiledAtlasSnapshot(
            tiles: tiles,
            revision: incrementingRevision ? published.revision &+ 1 : published.revision,
            totalBounds: bounds.isNull ? .zero : bounds,
            contentBounds: registeredContentBounds()
        )
        return true
    }

    /// A new observation is always newest evidence, so it can be painted over
    /// the currently published dirty tiles directly.  Replacements and pose
    /// changes still use `publishRebuild`, because they may reveal pixels from
    /// older evidence underneath the changed source.
    @discardableResult
    private func publishIncremental(
        _ contribution: Contribution,
        sourceImage: CGImage,
        shouldCancel: () -> Bool = { false }
    ) -> Bool {
        if pixelPolicy.usesTemporalEvidence {
            return publishTemporalIncremental(
                contribution,
                sourceImage: sourceImage,
                shouldCancel: shouldCancel
            )
        }
        lastRasterizedContributionCount = 0
        guard !shouldCancel() else { return false }
        guard let anchorPosition,
              let source = rgbaPixels(sourceImage),
              sourceImage.width == contribution.imageWidth,
              sourceImage.height == contribution.imageHeight
        else {
            // This cannot normally fail after insert validation, but rebuilding
            // preserves the existing source-provider/cache fallback semantics.
            return publishRebuild(
                contribution.tileCoordinates,
                incrementingRevision: true,
                shouldCancel: shouldCancel
            )
        }

        var current = Dictionary(uniqueKeysWithValues: published.tiles.map {
            (TileCoordinate(x: $0.tileX, y: $0.tileY), $0)
        })
        for coordinate in contribution.tileCoordinates.sorted(by: Self.tileOrder) {
            guard !shouldCancel() else { return false }
            let existing = current[coordinate]
            switch composite(
                contribution,
                source: source,
                at: coordinate,
                over: existing,
                anchor: anchorPosition,
                shouldCancel: shouldCancel
            ) {
            case .tile(let tile): current[coordinate] = tile
            case .empty: current.removeValue(forKey: coordinate)
            case .cancelled: return false
            }
        }
        guard !shouldCancel() else { return false }
        let tiles = current.values.sorted {
            Self.tileOrder(
                TileCoordinate(x: $0.tileX, y: $0.tileY),
                TileCoordinate(x: $1.tileX, y: $1.tileY)
            )
        }
        let bounds = tiles.reduce(CGRect.null) { $0.union($1.worldBounds) }
        published = LiveTiledAtlasSnapshot(
            tiles: tiles,
            revision: published.revision &+ 1,
            totalBounds: bounds.isNull ? .zero : bounds,
            contentBounds: registeredContentBounds()
        )
        return true
    }

    private func registeredContentBounds() -> CGRect {
        guard let anchorPosition else { return .zero }
        let combined = contributions.values.reduce(CGRect.null) { bounds, contribution in
            let placement = placementBounds(for: contribution, anchor: anchorPosition)
            guard !placement.isNull, placement.width > 0, placement.height > 0 else {
                return bounds
            }
            return bounds.union(placement)
        }
        return combined.isNull ? .zero : combined
    }

    private func projectedTiles(for contribution: Contribution, anchor: CGPoint) -> Set<TileCoordinate> {
        let placement = placementBounds(for: contribution, anchor: anchor)
        guard !placement.isNull, placement.width > 0, placement.height > 0,
              placement.minX >= CGFloat(Int32.min), placement.maxX <= CGFloat(Int32.max),
              placement.minY >= CGFloat(Int32.min), placement.maxY <= CGFloat(Int32.max)
        else { return [] }
        let minX = Int(placement.minX)
        let minY = Int(placement.minY)
        let maxX = Int(placement.maxX) - 1
        let maxY = Int(placement.maxY) - 1
        guard minX <= maxX, minY <= maxY else { return [] }
        var result = Set<TileCoordinate>()
        for y in Self.floorDiv(minY, Self.tileSize)...Self.floorDiv(maxY, Self.tileSize) {
            for x in Self.floorDiv(minX, Self.tileSize)...Self.floorDiv(maxX, Self.tileSize) {
                result.insert(TileCoordinate(x: x, y: y))
            }
        }
        return result
    }

    private func placementBounds(for contribution: Contribution, anchor: CGPoint) -> CGRect {
        let offset = WorldPlacement.offset(
            cameraPosition: contribution.cameraPosition,
            anchorPosition: anchor,
            presentationWidth: CGFloat(contribution.imageWidth),
            solveWidth: contribution.solveWidth,
            gain: 1
        )
        guard offset.x.isFinite, offset.y.isFinite else { return .null }
        // Match the existing presentation coordinate rule exactly, including
        // its integral raster footprint.
        return CGRect(
            x: offset.x,
            y: offset.y,
            width: CGFloat(contribution.imageWidth),
            height: CGFloat(contribution.imageHeight)
        ).integral
    }

    private func renderTile(
        at coordinate: TileCoordinate,
        shouldCancel: () -> Bool = { false }
    ) -> TileRenderResult {
        if pixelPolicy.usesTemporalEvidence {
            return renderTemporalTile(at: coordinate, shouldCancel: shouldCancel)
        }
        guard !shouldCancel() else { return .cancelled }
        guard let ids = contributionIDsByTile[coordinate], !ids.isEmpty else { return .empty }
        let ordered = ids.compactMap { contributions[$0] }.sorted {
            if $0.insertionOrder != $1.insertionOrder { return $0.insertionOrder < $1.insertionOrder }
            return $0.observationID < $1.observationID
        }
        guard let anchorPosition else { return .empty }
        let pixelCount = Self.tileSize * Self.tileSize
        var rgba = [UInt8](repeating: 0, count: pixelCount * 4)
        var selectedIDs = Set<Int>()
        var observed = false
        for contribution in ordered {
            guard !shouldCancel() else { return .cancelled }
            guard let image = sourceImage(for: contribution.observationID),
                  image.width == contribution.imageWidth,
                  image.height == contribution.imageHeight,
                  let source = rgbaPixels(image)
            else { continue }
            lastRasterizedContributionCount += 1
            guard let contributed = rasterNewestContribution(
                contribution,
                source: source,
                coordinate: coordinate,
                anchor: anchorPosition,
                rgba: &rgba,
                shouldCancel: shouldCancel
            ) else { return .cancelled }
            observed = observed || contributed
            if contributed { selectedIDs.insert(contribution.observationID) }
        }
        guard observed, let image = makeImage(rgba) else { return .empty }
        return .tile(LiveTiledAtlasTile(
            id: coordinate.stableID,
            key: coordinate.stableID,
            tileX: coordinate.x,
            tileY: coordinate.y,
            image: image,
            worldBounds: coordinate.bounds,
            contributionIDs: selectedIDs.sorted()
        ))
    }

    /// Paints one newest source over a tile.  Alpha zero means no observation,
    /// so it deliberately leaves the prior pixel intact; opaque black is an
    /// observed value and is copied like every other nonzero-alpha pixel.
    private func composite(
        _ contribution: Contribution,
        source: [UInt8],
        at coordinate: TileCoordinate,
        over existing: LiveTiledAtlasTile?,
        anchor: CGPoint,
        shouldCancel: () -> Bool = { false }
    ) -> TileRenderResult {
        guard !shouldCancel() else { return .cancelled }
        var rgba: [UInt8]
        var selectedIDs: Set<Int>
        if let existing {
            guard let existingPixels = rgbaPixels(existing.image) else { return .tile(existing) }
            rgba = existingPixels
            selectedIDs = Set(existing.contributionIDs)
        } else {
            rgba = [UInt8](repeating: 0, count: Self.tileSize * Self.tileSize * 4)
            selectedIDs = []
        }

        lastRasterizedContributionCount += 1
        guard let contributed = rasterNewestContribution(
            contribution,
            source: source,
            coordinate: coordinate,
            anchor: anchor,
            rgba: &rgba,
            shouldCancel: shouldCancel
        ) else { return .cancelled }
        guard contributed else { return existing.map(TileRenderResult.tile) ?? .empty }
        selectedIDs.insert(contribution.observationID)
        guard !shouldCancel() else { return .cancelled }
        guard let image = makeImage(rgba) else {
            return existing.map(TileRenderResult.tile) ?? .empty
        }
        return .tile(LiveTiledAtlasTile(
            id: coordinate.stableID,
            key: coordinate.stableID,
            tileX: coordinate.x,
            tileY: coordinate.y,
            image: image,
            worldBounds: coordinate.bounds,
            contributionIDs: selectedIDs.sorted()
        ))
    }

    /// Newest-quilted mode writes the current observation directly. Only an
    /// eight-pixel band at each screen edge may retain older atlas pixels, and
    /// the boundary through that band follows the lowest RGB disagreement.
    /// This removes temporal blur without drawing a straight screen rectangle.
    private func rasterNewestContribution(
        _ contribution: Contribution,
        source: [UInt8],
        coordinate: TileCoordinate,
        anchor: CGPoint,
        rgba: inout [UInt8],
        shouldCancel: () -> Bool
    ) -> Bool? {
        let placement = placementBounds(for: contribution, anchor: anchor)
        let baseX = Int(placement.minX)
        let baseY = Int(placement.minY)
        let tileMinX = coordinate.x * Self.tileSize
        let tileMinY = coordinate.y * Self.tileSize
        let minSourceX = max(0, tileMinX - baseX)
        let maxSourceX = min(
            contribution.imageWidth - 1,
            tileMinX + Self.tileSize - 1 - baseX
        )
        let minSourceY = max(0, tileMinY - baseY)
        let maxSourceY = min(
            contribution.imageHeight - 1,
            tileMinY + Self.tileSize - 1 - baseY
        )
        guard minSourceX <= maxSourceX, minSourceY <= maxSourceY else { return false }
        guard !shouldCancel() else { return nil }
        let seams = pixelPolicy == .newestQuilted
            ? newestQuiltSeams(
                contribution: contribution,
                source: source,
                coordinate: coordinate,
                rgba: rgba,
                baseX: baseX,
                baseY: baseY,
                minSourceX: minSourceX,
                maxSourceX: maxSourceX,
                minSourceY: minSourceY,
                maxSourceY: maxSourceY
            )
            : nil

        var contributed = false
        for sourceY in minSourceY...maxSourceY {
            guard !shouldCancel() else { return nil }
            let sourceTopY = contribution.imageHeight - 1 - sourceY
            for sourceX in minSourceX...maxSourceX {
                let sourceIndex = (sourceTopY * contribution.imageWidth + sourceX) * 4
                guard source[sourceIndex + 3] > 0 else { continue }
                let localX = baseX + sourceX - tileMinX
                let localY = baseY + sourceY - tileMinY
                let destination = ((Self.tileSize - 1 - localY) * Self.tileSize + localX) * 4
                guard shouldCopyPixel(
                    source: source,
                    sourceIndex: sourceIndex,
                    destination: rgba,
                    destinationIndex: destination
                ) else { continue }
                if let seams,
                   rgba[destination + 3] > 0,
                   !newestQuiltUsesNewPixel(
                        sourceX: sourceX,
                        sourceY: sourceY,
                        imageWidth: contribution.imageWidth,
                        imageHeight: contribution.imageHeight,
                        seams: seams
                   ) {
                    continue
                }
                rgba[destination] = source[sourceIndex]
                rgba[destination + 1] = source[sourceIndex + 1]
                rgba[destination + 2] = source[sourceIndex + 2]
                rgba[destination + 3] = source[sourceIndex + 3]
                contributed = true
            }
        }
        return contributed
    }

    private func newestQuiltSeams(
        contribution: Contribution,
        source: [UInt8],
        coordinate: TileCoordinate,
        rgba: [UInt8],
        baseX: Int,
        baseY: Int,
        minSourceX: Int,
        maxSourceX: Int,
        minSourceY: Int,
        maxSourceY: Int
    ) -> NewestQuiltSeams {
        let overlap = Self.newestQuiltOverlap
        let leftMax = min(maxSourceX, overlap - 1)
        let left = minSourceX <= leftMax
            ? newestVerticalEdgeSeam(
                bandRange: minSourceX...leftMax,
                sourceXForBand: { $0 },
                contribution: contribution,
                source: source,
                coordinate: coordinate,
                rgba: rgba,
                baseX: baseX,
                baseY: baseY,
                minSourceY: minSourceY,
                maxSourceY: maxSourceY
            )
            : nil

        let rightMin = max(0, contribution.imageWidth - 1 - maxSourceX)
        let rightMax = min(
            overlap - 1,
            contribution.imageWidth - 1 - minSourceX
        )
        let right = rightMin <= rightMax
            ? newestVerticalEdgeSeam(
                bandRange: rightMin...rightMax,
                sourceXForBand: { contribution.imageWidth - 1 - $0 },
                contribution: contribution,
                source: source,
                coordinate: coordinate,
                rgba: rgba,
                baseX: baseX,
                baseY: baseY,
                minSourceY: minSourceY,
                maxSourceY: maxSourceY
            )
            : nil

        let bottomMax = min(maxSourceY, overlap - 1)
        let bottom = minSourceY <= bottomMax
            ? newestHorizontalEdgeSeam(
                bandRange: minSourceY...bottomMax,
                sourceYForBand: { $0 },
                contribution: contribution,
                source: source,
                coordinate: coordinate,
                rgba: rgba,
                baseX: baseX,
                baseY: baseY,
                minSourceX: minSourceX,
                maxSourceX: maxSourceX
            )
            : nil

        let topMin = max(0, contribution.imageHeight - 1 - maxSourceY)
        let topMax = min(
            overlap - 1,
            contribution.imageHeight - 1 - minSourceY
        )
        let top = topMin <= topMax
            ? newestHorizontalEdgeSeam(
                bandRange: topMin...topMax,
                sourceYForBand: { contribution.imageHeight - 1 - $0 },
                contribution: contribution,
                source: source,
                coordinate: coordinate,
                rgba: rgba,
                baseX: baseX,
                baseY: baseY,
                minSourceX: minSourceX,
                maxSourceX: maxSourceX
            )
            : nil

        return NewestQuiltSeams(
            minSourceX: minSourceX,
            minSourceY: minSourceY,
            left: left,
            right: right,
            bottom: bottom,
            top: top
        )
    }

    private func newestVerticalEdgeSeam(
        bandRange: ClosedRange<Int>,
        sourceXForBand: (Int) -> Int,
        contribution: Contribution,
        source: [UInt8],
        coordinate: TileCoordinate,
        rgba: [UInt8],
        baseX: Int,
        baseY: Int,
        minSourceY: Int,
        maxSourceY: Int
    ) -> (offset: Int, values: [Int])? {
        let rows = maxSourceY - minSourceY + 1
        let columns = bandRange.count
        var cost = [Int](repeating: 0, count: rows * columns)
        var comparable = false
        let tileMinX = coordinate.x * Self.tileSize
        let tileMinY = coordinate.y * Self.tileSize
        for row in 0..<rows {
            let sourceY = minSourceY + row
            let sourceTopY = contribution.imageHeight - 1 - sourceY
            for (column, band) in bandRange.enumerated() {
                let sourceX = sourceXForBand(band)
                let sourceIndex = (sourceTopY * contribution.imageWidth + sourceX) * 4
                guard source[sourceIndex + 3] > 0 else { continue }
                let localX = baseX + sourceX - tileMinX
                let localY = baseY + sourceY - tileMinY
                guard localX >= 0, localX < Self.tileSize,
                      localY >= 0, localY < Self.tileSize else { continue }
                let destination = ((Self.tileSize - 1 - localY) * Self.tileSize + localX) * 4
                guard rgba[destination + 3] > 0 else { continue }
                comparable = true
                cost[row * columns + column] = Self.colorDistance(
                    source, sourceIndex, rgba, destination
                )
            }
        }
        guard comparable else { return nil }
        let local = Self.minimumVerticalSeam(cost: cost, rows: rows, columns: columns)
        return (bandRange.lowerBound, local.map { $0 + bandRange.lowerBound })
    }

    private func newestHorizontalEdgeSeam(
        bandRange: ClosedRange<Int>,
        sourceYForBand: (Int) -> Int,
        contribution: Contribution,
        source: [UInt8],
        coordinate: TileCoordinate,
        rgba: [UInt8],
        baseX: Int,
        baseY: Int,
        minSourceX: Int,
        maxSourceX: Int
    ) -> (offset: Int, values: [Int])? {
        let rows = bandRange.count
        let columns = maxSourceX - minSourceX + 1
        var cost = [Int](repeating: 0, count: rows * columns)
        var comparable = false
        let tileMinX = coordinate.x * Self.tileSize
        let tileMinY = coordinate.y * Self.tileSize
        for (row, band) in bandRange.enumerated() {
            let sourceY = sourceYForBand(band)
            let sourceTopY = contribution.imageHeight - 1 - sourceY
            for column in 0..<columns {
                let sourceX = minSourceX + column
                let sourceIndex = (sourceTopY * contribution.imageWidth + sourceX) * 4
                guard source[sourceIndex + 3] > 0 else { continue }
                let localX = baseX + sourceX - tileMinX
                let localY = baseY + sourceY - tileMinY
                guard localX >= 0, localX < Self.tileSize,
                      localY >= 0, localY < Self.tileSize else { continue }
                let destination = ((Self.tileSize - 1 - localY) * Self.tileSize + localX) * 4
                guard rgba[destination + 3] > 0 else { continue }
                comparable = true
                cost[row * columns + column] = Self.colorDistance(
                    source, sourceIndex, rgba, destination
                )
            }
        }
        guard comparable else { return nil }
        let local = Self.minimumHorizontalSeam(cost: cost, rows: rows, columns: columns)
        return (bandRange.lowerBound, local.map { $0 + bandRange.lowerBound })
    }

    private func newestQuiltUsesNewPixel(
        sourceX: Int,
        sourceY: Int,
        imageWidth: Int,
        imageHeight: Int,
        seams: NewestQuiltSeams
    ) -> Bool {
        let row = sourceY - seams.minSourceY
        let column = sourceX - seams.minSourceX
        if sourceX < Self.newestQuiltOverlap,
           let left = seams.left,
           sourceX < left.values[row] {
            return false
        }
        let rightBand = imageWidth - 1 - sourceX
        if rightBand < Self.newestQuiltOverlap,
           let right = seams.right,
           rightBand < right.values[row] {
            return false
        }
        if sourceY < Self.newestQuiltOverlap,
           let bottom = seams.bottom,
           sourceY < bottom.values[column] {
            return false
        }
        let topBand = imageHeight - 1 - sourceY
        if topBand < Self.newestQuiltOverlap,
           let top = seams.top,
           topBand < top.values[column] {
            return false
        }
        return true
    }

    private func shouldCopyPixel(
        source: [UInt8],
        sourceIndex: Int,
        destination: [UInt8],
        destinationIndex: Int
    ) -> Bool {
        guard destination[destinationIndex + 3] > 0 else { return true }
        guard pixelPolicy == .preserveBrighter else { return true }
        let sourcePeak = max(
            source[sourceIndex],
            max(source[sourceIndex + 1], source[sourceIndex + 2])
        )
        let destinationPeak = max(
            destination[destinationIndex],
            max(destination[destinationIndex + 1], destination[destinationIndex + 2])
        )
        return sourcePeak >= destinationPeak
    }

    private func prepareTemporalWindow(
        observationID: Int,
        imageWidth: Int,
        imageHeight: Int,
        timestamp: Double?,
        roomID: Int,
        captureGeneration: UInt64
    ) -> [Int] {
        let nextContext = TemporalContext(
            imageWidth: imageWidth,
            imageHeight: imageHeight,
            roomID: roomID,
            captureGeneration: captureGeneration
        )
        var discontinuity = temporalContext != nil && temporalContext != nextContext
        if let lastID = temporalHistoryIDs.last,
           let last = contributions[lastID]?.timestamp {
            guard let timestamp else {
                discontinuity = true
                temporalContext = nextContext
                temporalHistoryIDs.removeAll(keepingCapacity: true)
                temporalHistoryIDs.append(observationID)
                return temporalHistoryIDs
            }
            if timestamp < last || timestamp - last > Self.temporalWindowDuration {
                discontinuity = true
            }
        } else if temporalHistoryIDs.last != nil, timestamp != nil {
            discontinuity = true
        }
        if discontinuity {
            temporalHistoryIDs.removeAll(keepingCapacity: true)
        }
        temporalContext = nextContext
        if let timestamp {
            temporalHistoryIDs.removeAll { id in
                guard let earlier = contributions[id]?.timestamp else { return true }
                return timestamp < earlier || timestamp - earlier > Self.temporalWindowDuration
            }
        }
        temporalHistoryIDs.append(observationID)
        if temporalHistoryIDs.count > Self.temporalWindowCapacity {
            temporalHistoryIDs.removeFirst(
                temporalHistoryIDs.count - Self.temporalWindowCapacity
            )
        }
        return temporalHistoryIDs
    }

    @discardableResult
    private func publishTemporalIncremental(
        _ contribution: Contribution,
        sourceImage: CGImage,
        shouldCancel: () -> Bool
    ) -> Bool {
        lastRasterizedContributionCount = 0
        guard !shouldCancel(), let anchorPosition else { return false }
        guard let currentPixels = decodedPixels(
            for: contribution.observationID,
            suppliedImage: sourceImage
        ) else {
            return publishRebuild(
                contribution.tileCoordinates,
                incrementingRevision: true,
                shouldCancel: shouldCancel
            )
        }
        let sharedQuiltCandidates: TemporalCandidateBuffers?
        if pixelPolicy == .temporalQuilting,
           contribution.temporalWindowIDs.count >= 3 {
            guard let candidates = temporalQuiltCandidates(
                for: contribution,
                anchor: anchorPosition,
                shouldCancel: shouldCancel
            ) else { return false }
            sharedQuiltCandidates = candidates
        } else {
            sharedQuiltCandidates = nil
        }
        var current = Dictionary(uniqueKeysWithValues: published.tiles.map {
            (TileCoordinate(x: $0.tileX, y: $0.tileY), $0)
        })
        for coordinate in contribution.tileCoordinates.sorted(by: Self.tileOrder) {
            guard !shouldCancel() else { return false }
            let existing = current[coordinate]
            switch compositeTemporal(
                contribution,
                currentPixels: currentPixels,
                at: coordinate,
                over: existing,
                anchor: anchorPosition,
                temporalCandidates: sharedQuiltCandidates,
                shouldCancel: shouldCancel
            ) {
            case .tile(let tile): current[coordinate] = tile
            case .empty: current.removeValue(forKey: coordinate)
            case .cancelled: return false
            }
        }
        guard !shouldCancel() else { return false }
        let tiles = current.values.sorted {
            Self.tileOrder(
                TileCoordinate(x: $0.tileX, y: $0.tileY),
                TileCoordinate(x: $1.tileX, y: $1.tileY)
            )
        }
        let bounds = tiles.reduce(CGRect.null) { $0.union($1.worldBounds) }
        published = LiveTiledAtlasSnapshot(
            tiles: tiles,
            revision: published.revision &+ 1,
            totalBounds: bounds.isNull ? .zero : bounds,
            contentBounds: registeredContentBounds()
        )
        return true
    }

    private func renderTemporalTile(
        at coordinate: TileCoordinate,
        shouldCancel: () -> Bool
    ) -> TileRenderResult {
        guard !shouldCancel(), let anchorPosition else { return .cancelled }
        guard let ids = contributionIDsByTile[coordinate], !ids.isEmpty else {
            return .empty
        }
        let ordered = ids.compactMap { contributions[$0] }.sorted {
            if $0.insertionOrder != $1.insertionOrder {
                return $0.insertionOrder < $1.insertionOrder
            }
            return $0.observationID < $1.observationID
        }
        var rgba = [UInt8](repeating: 0, count: Self.tileSize * Self.tileSize * 4)
        var selectedIDs = Set<Int>()
        var observed = false
        for contribution in ordered {
            guard !shouldCancel() else { return .cancelled }
            guard let currentPixels = decodedPixels(for: contribution.observationID) else {
                continue
            }
            lastRasterizedContributionCount += 1
            let result = applyTemporalContribution(
                contribution,
                currentPixels: currentPixels,
                coordinate: coordinate,
                anchor: anchorPosition,
                rgba: &rgba,
                selectedIDs: &selectedIDs,
                shouldCancel: shouldCancel
            )
            guard let changed = result else { return .cancelled }
            observed = observed || changed
        }
        guard observed, let image = makeImage(rgba) else { return .empty }
        return .tile(LiveTiledAtlasTile(
            id: coordinate.stableID,
            key: coordinate.stableID,
            tileX: coordinate.x,
            tileY: coordinate.y,
            image: image,
            worldBounds: coordinate.bounds,
            contributionIDs: selectedIDs.sorted()
        ))
    }

    private func compositeTemporal(
        _ contribution: Contribution,
        currentPixels: [UInt8],
        at coordinate: TileCoordinate,
        over existing: LiveTiledAtlasTile?,
        anchor: CGPoint,
        temporalCandidates: TemporalCandidateBuffers? = nil,
        shouldCancel: () -> Bool
    ) -> TileRenderResult {
        var rgba: [UInt8]
        var selectedIDs: Set<Int>
        if let existing {
            guard let pixels = rgbaPixels(existing.image) else { return .tile(existing) }
            rgba = pixels
            selectedIDs = Set(existing.contributionIDs)
        } else {
            rgba = [UInt8](repeating: 0, count: Self.tileSize * Self.tileSize * 4)
            selectedIDs = []
        }
        lastRasterizedContributionCount += 1
        guard let changed = applyTemporalContribution(
            contribution,
            currentPixels: currentPixels,
            coordinate: coordinate,
            anchor: anchor,
            rgba: &rgba,
            selectedIDs: &selectedIDs,
            precomputedCandidates: temporalCandidates,
            shouldCancel: shouldCancel
        ) else { return .cancelled }
        guard changed, let image = makeImage(rgba) else {
            return existing.map(TileRenderResult.tile) ?? .empty
        }
        return .tile(LiveTiledAtlasTile(
            id: coordinate.stableID,
            key: coordinate.stableID,
            tileX: coordinate.x,
            tileY: coordinate.y,
            image: image,
            worldBounds: coordinate.bounds,
            contributionIDs: selectedIDs.sorted()
        ))
    }

    /// Returns nil only when cancelled. True means at least one atlas pixel was
    /// written. A first observation fills empty pixels immediately; later writes
    /// require temporal and local-neighborhood agreement.
    private func applyTemporalContribution(
        _ contribution: Contribution,
        currentPixels: [UInt8],
        coordinate: TileCoordinate,
        anchor: CGPoint,
        rgba: inout [UInt8],
        selectedIDs: inout Set<Int>,
        precomputedCandidates: TemporalCandidateBuffers? = nil,
        shouldCancel: () -> Bool
    ) -> Bool? {
        let placement = placementBounds(for: contribution, anchor: anchor)
        let baseX = Int(placement.minX)
        let baseY = Int(placement.minY)
        let tileMinX = coordinate.x * Self.tileSize
        let tileMinY = coordinate.y * Self.tileSize
        let minSourceX = max(0, tileMinX - baseX)
        let maxSourceX = min(
            contribution.imageWidth - 1,
            tileMinX + Self.tileSize - 1 - baseX
        )
        let minSourceY = max(0, tileMinY - baseY)
        let maxSourceY = min(
            contribution.imageHeight - 1,
            tileMinY + Self.tileSize - 1 - baseY
        )
        guard minSourceX <= maxSourceX, minSourceY <= maxSourceY else { return false }
        let candidates: TemporalCandidateBuffers?
        if let precomputedCandidates {
            candidates = precomputedCandidates
        } else if contribution.temporalWindowIDs.count >= 3 {
            let computed: TemporalCandidateBuffers?
            switch pixelPolicy {
            case .temporalQuilting:
                computed = temporalQuiltCandidates(
                    for: contribution,
                    anchor: anchor,
                    shouldCancel: shouldCancel
                )
            case .temporalAgreement:
                computed = temporalCandidates(
                    for: contribution,
                    coordinate: coordinate,
                    anchor: anchor,
                    shouldCancel: shouldCancel
                )
            case .newestOpaque, .newestQuilted, .preserveBrighter:
                computed = nil
            }
            guard let computed else { return nil }
            candidates = computed
        } else {
            candidates = nil
        }

        var changed = false
        var usedConsensus = false
        for sourceY in minSourceY...maxSourceY {
            guard !shouldCancel() else { return nil }
            let sourceTopY = contribution.imageHeight - 1 - sourceY
            for sourceX in minSourceX...maxSourceX {
                let sourceIndex = (sourceTopY * contribution.imageWidth + sourceX) * 4
                guard currentPixels[sourceIndex + 3] > 0 else { continue }
                let worldX = baseX + sourceX
                let worldY = baseY + sourceY
                let localX = worldX - tileMinX
                let localY = worldY - tileMinY
                let destination = ((Self.tileSize - 1 - localY) * Self.tileSize + localX) * 4
                if let candidates {
                    let candidateX = worldX - candidates.originX
                    let candidateY = worldY - candidates.originY
                    let candidateIndex = candidateY * candidates.width + candidateX
                    if candidates.hasConsensus[candidateIndex],
                       (pixelPolicy == .temporalQuilting
                        || temporalNeighborhoodAgrees(
                            candidateX: candidateX,
                            candidateY: candidateY,
                            buffers: candidates
                        )) {
                        let candidateRGBA = candidateIndex * 4
                        rgba[destination] = candidates.rgba[candidateRGBA]
                        rgba[destination + 1] = candidates.rgba[candidateRGBA + 1]
                        rgba[destination + 2] = candidates.rgba[candidateRGBA + 2]
                        rgba[destination + 3] = candidates.rgba[candidateRGBA + 3]
                        temporalWork.consensusPixelCount += 1
                        changed = true
                        usedConsensus = true
                        continue
                    }
                }
                if rgba[destination + 3] == 0 {
                    rgba[destination] = currentPixels[sourceIndex]
                    rgba[destination + 1] = currentPixels[sourceIndex + 1]
                    rgba[destination + 2] = currentPixels[sourceIndex + 2]
                    rgba[destination + 3] = currentPixels[sourceIndex + 3]
                    temporalWork.immediatePixelCount += 1
                    changed = true
                    selectedIDs.insert(contribution.observationID)
                }
            }
        }
        if usedConsensus {
            selectedIDs.formUnion(contribution.temporalWindowIDs)
        }
        return changed
    }

    private func temporalCandidates(
        for contribution: Contribution,
        coordinate: TileCoordinate,
        anchor: CGPoint,
        shouldCancel: () -> Bool
    ) -> TemporalCandidateBuffers? {
        let halo = 1
        let tileMinX = coordinate.x * Self.tileSize
        let tileMinY = coordinate.y * Self.tileSize
        let placement = placementBounds(for: contribution, anchor: anchor)
        let centralMinX = max(tileMinX, Int(placement.minX))
        let centralMaxX = min(tileMinX + Self.tileSize - 1, Int(placement.maxX) - 1)
        let centralMinY = max(tileMinY, Int(placement.minY))
        let centralMaxY = min(tileMinY + Self.tileSize - 1, Int(placement.maxY) - 1)
        guard centralMinX <= centralMaxX, centralMinY <= centralMaxY else { return nil }
        let originX = centralMinX - halo
        let originY = centralMinY - halo
        let width = centralMaxX - centralMinX + 1 + halo * 2
        let height = centralMaxY - centralMinY + 1 + halo * 2
        let sources = contribution.temporalWindowIDs.compactMap { id -> DecodedSource? in
            guard let item = contributions[id],
                  let pixels = decodedPixels(for: id) else { return nil }
            let placement = placementBounds(for: item, anchor: anchor)
            return DecodedSource(
                contribution: item,
                pixels: pixels,
                baseX: Int(placement.minX),
                baseY: Int(placement.minY)
            )
        }
        let buffers = temporalCandidateBuffers
        buffers.reset(
            width: width,
            height: height,
            originX: originX,
            originY: originY
        )
        var red = [UInt8](repeating: 0, count: Self.temporalWindowCapacity)
        var green = red
        var blue = red
        var alpha = red
        var sortedRed = red
        var sortedGreen = red
        var sortedBlue = red

        for y in 0..<height {
            guard !shouldCancel() else { return nil }
            let worldY = originY + y
            for x in 0..<width {
                let worldX = originX + x
                var sampleCount = 0
                for source in sources {
                    let sourceX = worldX - source.baseX
                    let sourceY = worldY - source.baseY
                    guard sourceX >= 0, sourceX < source.contribution.imageWidth,
                          sourceY >= 0, sourceY < source.contribution.imageHeight
                    else { continue }
                    let topY = source.contribution.imageHeight - 1 - sourceY
                    let sourceIndex = (topY * source.contribution.imageWidth + sourceX) * 4
                    guard source.pixels[sourceIndex + 3] > 0 else { continue }
                    red[sampleCount] = source.pixels[sourceIndex]
                    green[sampleCount] = source.pixels[sourceIndex + 1]
                    blue[sampleCount] = source.pixels[sourceIndex + 2]
                    alpha[sampleCount] = source.pixels[sourceIndex + 3]
                    sampleCount += 1
                }
                guard sampleCount >= 3 else { continue }
                let index = y * width + x
                buffers.hasEnoughSamples[index] = true
                for sample in 0..<sampleCount {
                    sortedRed[sample] = red[sample]
                    sortedGreen[sample] = green[sample]
                    sortedBlue[sample] = blue[sample]
                }
                Self.insertionSort(&sortedRed, count: sampleCount)
                Self.insertionSort(&sortedGreen, count: sampleCount)
                Self.insertionSort(&sortedBlue, count: sampleCount)
                let medianIndex = sampleCount / 2
                let medianRed = sortedRed[medianIndex]
                let medianGreen = sortedGreen[medianIndex]
                let medianBlue = sortedBlue[medianIndex]
                var agreementCount = 0
                var closestSample = -1
                var closestDistance = Int.max
                for sample in 0..<sampleCount {
                    let agrees = abs(Int(red[sample]) - Int(medianRed)) <= Self.temporalAgreementTolerance
                        && abs(Int(green[sample]) - Int(medianGreen)) <= Self.temporalAgreementTolerance
                        && abs(Int(blue[sample]) - Int(medianBlue)) <= Self.temporalAgreementTolerance
                    guard agrees else { continue }
                    agreementCount += 1
                    let redDistance = Int(red[sample]) - Int(medianRed)
                    let greenDistance = Int(green[sample]) - Int(medianGreen)
                    let blueDistance = Int(blue[sample]) - Int(medianBlue)
                    let distance = redDistance * redDistance
                        + greenDistance * greenDistance
                        + blueDistance * blueDistance
                    if distance <= closestDistance {
                        closestDistance = distance
                        closestSample = sample
                    }
                }
                guard agreementCount >= 3,
                      agreementCount > sampleCount / 2,
                      closestSample >= 0 else { continue }
                buffers.hasConsensus[index] = true
                let destination = index * 4
                buffers.rgba[destination] = red[closestSample]
                buffers.rgba[destination + 1] = green[closestSample]
                buffers.rgba[destination + 2] = blue[closestSample]
                buffers.rgba[destination + 3] = alpha[closestSample]
            }
        }
        return buffers
    }

    /// Builds a world-aligned texture quilt from overlapping 32-pixel patches.
    /// Patch matching ignores uniform illumination shifts. Matching observations
    /// are averaged pixelwise, then neighboring patches meet at minimum-error
    /// seams so the 32-pixel processing grid does not become the visible result.
    private func temporalQuiltCandidates(
        for contribution: Contribution,
        anchor: CGPoint,
        shouldCancel: () -> Bool
    ) -> TemporalCandidateBuffers? {
        // One overlap reaches the preceding globally anchored patch, which is
        // enough to reproduce the seam while avoiding work outside the frame.
        let halo = Self.quiltOverlap
        let placement = placementBounds(for: contribution, anchor: anchor)
        let centralMinX = Int(placement.minX)
        let centralMaxX = Int(placement.maxX) - 1
        let centralMinY = Int(placement.minY)
        let centralMaxY = Int(placement.maxY) - 1
        guard centralMinX <= centralMaxX, centralMinY <= centralMaxY else { return nil }
        let originX = centralMinX - halo
        let originY = centralMinY - halo
        let width = centralMaxX - centralMinX + 1 + halo * 2
        let height = centralMaxY - centralMinY + 1 + halo * 2
        let sources = contribution.temporalWindowIDs.compactMap { id -> DecodedSource? in
            guard let item = contributions[id],
                  let pixels = decodedPixels(for: id) else { return nil }
            let sourcePlacement = placementBounds(for: item, anchor: anchor)
            return DecodedSource(
                contribution: item,
                pixels: pixels,
                baseX: Int(sourcePlacement.minX),
                baseY: Int(sourcePlacement.minY)
            )
        }
        let buffers = temporalCandidateBuffers
        buffers.reset(width: width, height: height, originX: originX, originY: originY)
        guard sources.count >= 3 else { return buffers }

        let patchPixelCount = Self.quiltPatchSize * Self.quiltPatchSize
        var patchRGBA = [UInt8](repeating: 0, count: patchPixelCount * 4)
        var patchValid = [Bool](repeating: false, count: patchPixelCount)
        let firstPatchX = Self.floorDiv(originX, Self.quiltStride) * Self.quiltStride
        let firstPatchY = Self.floorDiv(originY, Self.quiltStride) * Self.quiltStride
        let lastX = originX + width - 1
        let lastY = originY + height - 1

        for patchY in Swift.stride(
            from: firstPatchY,
            through: lastY,
            by: Self.quiltStride
        ) {
            guard !shouldCancel() else { return nil }
            for patchX in Swift.stride(
                from: firstPatchX,
                through: lastX,
                by: Self.quiltStride
            ) {
                guard !shouldCancel() else { return nil }
                guard let members = quiltPatchMembers(
                    patchX: patchX,
                    patchY: patchY,
                    sources: sources
                ) else { continue }

                for index in patchRGBA.indices { patchRGBA[index] = 0 }
                for index in patchValid.indices { patchValid[index] = false }
                for localY in 0..<Self.quiltPatchSize {
                    let worldY = patchY + localY
                    for localX in 0..<Self.quiltPatchSize {
                        let worldX = patchX + localX
                        guard let color = quiltPixel(
                            worldX: worldX,
                            worldY: worldY,
                            members: members,
                            sources: sources
                        ) else { continue }
                        let pixel = localY * Self.quiltPatchSize + localX
                        let destination = pixel * 4
                        patchRGBA[destination] = color.0
                        patchRGBA[destination + 1] = color.1
                        patchRGBA[destination + 2] = color.2
                        patchRGBA[destination + 3] = color.3
                        patchValid[pixel] = true
                    }
                }

                let leftSeam = quiltVerticalSeam(
                    patchX: patchX,
                    patchY: patchY,
                    patchRGBA: patchRGBA,
                    patchValid: patchValid,
                    buffers: buffers
                )
                let lowerSeam = quiltHorizontalSeam(
                    patchX: patchX,
                    patchY: patchY,
                    patchRGBA: patchRGBA,
                    patchValid: patchValid,
                    buffers: buffers
                )
                for localY in 0..<Self.quiltPatchSize {
                    let bufferY = patchY + localY - originY
                    guard bufferY >= 0, bufferY < height else { continue }
                    for localX in 0..<Self.quiltPatchSize {
                        let bufferX = patchX + localX - originX
                        guard bufferX >= 0, bufferX < width else { continue }
                        let patchPixel = localY * Self.quiltPatchSize + localX
                        guard patchValid[patchPixel] else { continue }
                        let bufferPixel = bufferY * width + bufferX
                        var useNew = !buffers.hasConsensus[bufferPixel]
                        if !useNew {
                            useNew = true
                            if localX < Self.quiltOverlap {
                                useNew = leftSeam.map { localX >= $0[localY] } ?? false
                            }
                            if useNew, localY < Self.quiltOverlap {
                                useNew = lowerSeam.map { localY >= $0[localX] } ?? false
                            }
                        }
                        guard useNew else { continue }
                        let source = patchPixel * 4
                        let destination = bufferPixel * 4
                        buffers.rgba[destination] = patchRGBA[source]
                        buffers.rgba[destination + 1] = patchRGBA[source + 1]
                        buffers.rgba[destination + 2] = patchRGBA[source + 2]
                        buffers.rgba[destination + 3] = patchRGBA[source + 3]
                        buffers.hasEnoughSamples[bufferPixel] = true
                        buffers.hasConsensus[bufferPixel] = true
                    }
                }
            }
        }
        return buffers
    }

    private func quiltPatchMembers(
        patchX: Int,
        patchY: Int,
        sources: [DecodedSource]
    ) -> [QuiltPatchMember]? {
        var descriptors = [QuiltPatchMember]()
        descriptors.reserveCapacity(sources.count)
        for sourceIndex in sources.indices {
            let source = sources[sourceIndex]
            var redTotal = 0
            var greenTotal = 0
            var blueTotal = 0
            var sampleCount = 0
            for localY in Swift.stride(
                from: Self.quiltDescriptorStep / 2,
                to: Self.quiltPatchSize,
                by: Self.quiltDescriptorStep
            ) {
                for localX in Swift.stride(
                    from: Self.quiltDescriptorStep / 2,
                    to: Self.quiltPatchSize,
                    by: Self.quiltDescriptorStep
                ) {
                    guard let index = sourcePixelIndex(
                        source,
                        worldX: patchX + localX,
                        worldY: patchY + localY
                    ) else { continue }
                    redTotal += Int(source.pixels[index])
                    greenTotal += Int(source.pixels[index + 1])
                    blueTotal += Int(source.pixels[index + 2])
                    sampleCount += 1
                }
            }
            guard sampleCount >= Self.quiltMinimumDescriptorSamples else { continue }
            let meanRed = redTotal / sampleCount
            let meanGreen = greenTotal / sampleCount
            let meanBlue = blueTotal / sampleCount
            var textureTotal = 0
            for localY in Swift.stride(
                from: Self.quiltDescriptorStep / 2,
                to: Self.quiltPatchSize,
                by: Self.quiltDescriptorStep
            ) {
                for localX in Swift.stride(
                    from: Self.quiltDescriptorStep / 2,
                    to: Self.quiltPatchSize,
                    by: Self.quiltDescriptorStep
                ) {
                    guard let index = sourcePixelIndex(
                        source,
                        worldX: patchX + localX,
                        worldY: patchY + localY
                    ) else { continue }
                    textureTotal += abs(Int(source.pixels[index]) - meanRed)
                    textureTotal += abs(Int(source.pixels[index + 1]) - meanGreen)
                    textureTotal += abs(Int(source.pixels[index + 2]) - meanBlue)
                }
            }
            descriptors.append(QuiltPatchMember(
                sourceIndex: sourceIndex,
                meanRed: meanRed,
                meanGreen: meanGreen,
                meanBlue: meanBlue,
                brightness: (meanRed + meanGreen + meanBlue) / 3,
                textureEnergy: textureTotal / (sampleCount * 3)
            ))
        }
        guard descriptors.count >= 3 else { return nil }

        let descriptorCount = descriptors.count
        var errors = [Int](repeating: Int.max, count: descriptorCount * descriptorCount)
        var support = [Int](repeating: 1, count: descriptorCount)
        for index in descriptors.indices { errors[index * descriptorCount + index] = 0 }
        for left in descriptors.indices {
            guard left + 1 < descriptorCount else { continue }
            for right in (left + 1)..<descriptorCount {
                guard let error = quiltPatchError(
                    descriptors[left],
                    descriptors[right],
                    patchX: patchX,
                    patchY: patchY,
                    sources: sources
                ) else { continue }
                errors[left * descriptorCount + right] = error
                errors[right * descriptorCount + left] = error
                if error <= Self.quiltTextureTolerance {
                    support[left] += 1
                    support[right] += 1
                }
            }
        }
        let minimumSupport = descriptorCount / 2 + 1
        let qualified = descriptors.indices.filter {
            support[$0] >= 3 && support[$0] >= minimumSupport
        }
        guard let selectedIndex = qualified.max(by: { left, right in
            let lhs = descriptors[left]
            let rhs = descriptors[right]
            if lhs.brightness != rhs.brightness { return lhs.brightness < rhs.brightness }
            if support[left] != support[right] { return support[left] < support[right] }
            return lhs.sourceIndex < rhs.sourceIndex
        }) else { return nil }
        let selected = descriptors[selectedIndex]
        // A flat near-black cohort is commonly a fade. Empty atlas pixels still
        // receive their first observation through the immediate path.
        if selected.brightness < 8, selected.textureEnergy < 2 { return nil }
        var peers = descriptors.indices.compactMap { index -> QuiltPatchMember? in
            guard index != selectedIndex,
                  errors[selectedIndex * descriptorCount + index]
                    <= Self.quiltTextureTolerance else { return nil }
            return descriptors[index]
        }
        peers.sort {
            if $0.brightness != $1.brightness { return $0.brightness > $1.brightness }
            return $0.sourceIndex > $1.sourceIndex
        }
        return [selected] + peers
    }

    private func quiltPatchError(
        _ left: QuiltPatchMember,
        _ right: QuiltPatchMember,
        patchX: Int,
        patchY: Int,
        sources: [DecodedSource]
    ) -> Int? {
        guard abs(left.meanRed - right.meanRed) <= Self.quiltIlluminationTolerance,
              abs(left.meanGreen - right.meanGreen) <= Self.quiltIlluminationTolerance,
              abs(left.meanBlue - right.meanBlue) <= Self.quiltIlluminationTolerance
        else { return nil }
        let leftSource = sources[left.sourceIndex]
        let rightSource = sources[right.sourceIndex]
        var total = 0
        var sampleCount = 0
        for localY in Swift.stride(
            from: Self.quiltDescriptorStep / 2,
            to: Self.quiltPatchSize,
            by: Self.quiltDescriptorStep
        ) {
            for localX in Swift.stride(
                from: Self.quiltDescriptorStep / 2,
                to: Self.quiltPatchSize,
                by: Self.quiltDescriptorStep
            ) {
                let worldX = patchX + localX
                let worldY = patchY + localY
                guard let leftIndex = sourcePixelIndex(
                    leftSource, worldX: worldX, worldY: worldY
                ), let rightIndex = sourcePixelIndex(
                    rightSource, worldX: worldX, worldY: worldY
                ) else { continue }
                total += abs(
                    Int(leftSource.pixels[leftIndex]) - left.meanRed
                        - Int(rightSource.pixels[rightIndex]) + right.meanRed
                )
                total += abs(
                    Int(leftSource.pixels[leftIndex + 1]) - left.meanGreen
                        - Int(rightSource.pixels[rightIndex + 1]) + right.meanGreen
                )
                total += abs(
                    Int(leftSource.pixels[leftIndex + 2]) - left.meanBlue
                        - Int(rightSource.pixels[rightIndex + 2]) + right.meanBlue
                )
                sampleCount += 1
            }
        }
        guard sampleCount >= Self.quiltMinimumDescriptorSamples else { return nil }
        return total / (sampleCount * 3)
    }

    private func quiltPixel(
        worldX: Int,
        worldY: Int,
        members: [QuiltPatchMember],
        sources: [DecodedSource]
    ) -> (UInt8, UInt8, UInt8, UInt8)? {
        var redTotal = 0
        var greenTotal = 0
        var blueTotal = 0
        var alphaTotal = 0
        var sampleCount = 0
        for member in members {
            let source = sources[member.sourceIndex]
            guard let index = sourcePixelIndex(source, worldX: worldX, worldY: worldY) else {
                continue
            }
            redTotal += Int(source.pixels[index])
            greenTotal += Int(source.pixels[index + 1])
            blueTotal += Int(source.pixels[index + 2])
            alphaTotal += Int(source.pixels[index + 3])
            sampleCount += 1
        }
        guard sampleCount >= 3 else { return nil }
        let rounding = sampleCount / 2
        return (
            UInt8((redTotal + rounding) / sampleCount),
            UInt8((greenTotal + rounding) / sampleCount),
            UInt8((blueTotal + rounding) / sampleCount),
            UInt8((alphaTotal + rounding) / sampleCount)
        )
    }

    private func sourcePixelIndex(
        _ source: DecodedSource,
        worldX: Int,
        worldY: Int
    ) -> Int? {
        let sourceX = worldX - source.baseX
        let sourceY = worldY - source.baseY
        guard sourceX >= 0, sourceX < source.contribution.imageWidth,
              sourceY >= 0, sourceY < source.contribution.imageHeight else { return nil }
        let topY = source.contribution.imageHeight - 1 - sourceY
        let index = (topY * source.contribution.imageWidth + sourceX) * 4
        return source.pixels[index + 3] > 0 ? index : nil
    }

    private func quiltVerticalSeam(
        patchX: Int,
        patchY: Int,
        patchRGBA: [UInt8],
        patchValid: [Bool],
        buffers: TemporalCandidateBuffers
    ) -> [Int]? {
        let rows = Self.quiltPatchSize
        let columns = Self.quiltOverlap
        var cost = [Int](repeating: 0, count: rows * columns)
        var comparable = false
        for row in 0..<rows {
            let bufferY = patchY + row - buffers.originY
            for column in 0..<columns {
                let bufferX = patchX + column - buffers.originX
                let patchPixel = row * Self.quiltPatchSize + column
                let index = row * columns + column
                guard bufferX >= 0, bufferX < buffers.width,
                      bufferY >= 0, bufferY < buffers.height,
                      patchValid[patchPixel] else { continue }
                let bufferPixel = bufferY * buffers.width + bufferX
                guard buffers.hasConsensus[bufferPixel] else { continue }
                comparable = true
                cost[index] = Self.colorDistance(
                    patchRGBA,
                    patchPixel * 4,
                    buffers.rgba,
                    bufferPixel * 4
                )
            }
        }
        guard comparable else { return nil }
        return Self.minimumVerticalSeam(cost: cost, rows: rows, columns: columns)
    }

    private func quiltHorizontalSeam(
        patchX: Int,
        patchY: Int,
        patchRGBA: [UInt8],
        patchValid: [Bool],
        buffers: TemporalCandidateBuffers
    ) -> [Int]? {
        let rows = Self.quiltOverlap
        let columns = Self.quiltPatchSize
        var cost = [Int](repeating: 0, count: rows * columns)
        var comparable = false
        for row in 0..<rows {
            let bufferY = patchY + row - buffers.originY
            for column in 0..<columns {
                let bufferX = patchX + column - buffers.originX
                let patchPixel = row * Self.quiltPatchSize + column
                let index = row * columns + column
                guard bufferX >= 0, bufferX < buffers.width,
                      bufferY >= 0, bufferY < buffers.height,
                      patchValid[patchPixel] else { continue }
                let bufferPixel = bufferY * buffers.width + bufferX
                guard buffers.hasConsensus[bufferPixel] else { continue }
                comparable = true
                cost[index] = Self.colorDistance(
                    patchRGBA,
                    patchPixel * 4,
                    buffers.rgba,
                    bufferPixel * 4
                )
            }
        }
        guard comparable else { return nil }
        return Self.minimumHorizontalSeam(cost: cost, rows: rows, columns: columns)
    }

    private static func minimumVerticalSeam(
        cost: [Int],
        rows: Int,
        columns: Int
    ) -> [Int] {
        var accumulated = cost
        var parent = [Int](repeating: 0, count: rows * columns)
        if rows > 1 {
            for row in 1..<rows {
                for column in 0..<columns {
                    var previousColumn = column
                    var previousCost = accumulated[(row - 1) * columns + column]
                    if column > 0,
                       accumulated[(row - 1) * columns + column - 1] < previousCost {
                        previousColumn = column - 1
                        previousCost = accumulated[(row - 1) * columns + column - 1]
                    }
                    if column + 1 < columns,
                       accumulated[(row - 1) * columns + column + 1] < previousCost {
                        previousColumn = column + 1
                        previousCost = accumulated[(row - 1) * columns + column + 1]
                    }
                    let index = row * columns + column
                    accumulated[index] += previousCost
                    parent[index] = previousColumn
                }
            }
        }
        let finalRow = rows - 1
        var column = (0..<columns).min {
            accumulated[finalRow * columns + $0] < accumulated[finalRow * columns + $1]
        } ?? 0
        var seam = [Int](repeating: 0, count: rows)
        for row in Swift.stride(from: finalRow, through: 0, by: -1) {
            seam[row] = column
            if row > 0 { column = parent[row * columns + column] }
        }
        return seam
    }

    private static func minimumHorizontalSeam(
        cost: [Int],
        rows: Int,
        columns: Int
    ) -> [Int] {
        var transposed = [Int](repeating: 0, count: rows * columns)
        for row in 0..<rows {
            for column in 0..<columns {
                transposed[column * rows + row] = cost[row * columns + column]
            }
        }
        return minimumVerticalSeam(
            cost: transposed,
            rows: columns,
            columns: rows
        )
    }

    private static func colorDistance(
        _ left: [UInt8],
        _ leftIndex: Int,
        _ right: [UInt8],
        _ rightIndex: Int
    ) -> Int {
        let red = Int(left[leftIndex]) - Int(right[rightIndex])
        let green = Int(left[leftIndex + 1]) - Int(right[rightIndex + 1])
        let blue = Int(left[leftIndex + 2]) - Int(right[rightIndex + 2])
        return red * red + green * green + blue * blue
    }

    private func temporalNeighborhoodAgrees(
        candidateX: Int,
        candidateY: Int,
        buffers: TemporalCandidateBuffers
    ) -> Bool {
        var validCount = 0
        var agreementCount = 0
        for y in (candidateY - 1)...(candidateY + 1) {
            for x in (candidateX - 1)...(candidateX + 1) {
                guard x >= 0, x < buffers.width, y >= 0, y < buffers.height else {
                    continue
                }
                let index = y * buffers.width + x
                guard buffers.hasEnoughSamples[index] else { continue }
                validCount += 1
                if buffers.hasConsensus[index] { agreementCount += 1 }
            }
        }
        return validCount > 0 && agreementCount * 3 >= validCount * 2
    }

    private static func insertionSort(_ values: inout [UInt8], count: Int) {
        guard count > 1 else { return }
        for index in 1..<count {
            let value = values[index]
            var insertion = index
            while insertion > 0, values[insertion - 1] > value {
                values[insertion] = values[insertion - 1]
                insertion -= 1
            }
            values[insertion] = value
        }
    }

    private func link(_ contribution: Contribution) {
        for coordinate in contribution.tileCoordinates {
            contributionIDsByTile[coordinate, default: []].insert(contribution.observationID)
        }
    }

    private func unlink(_ contribution: Contribution) {
        for coordinate in contribution.tileCoordinates {
            guard var ids = contributionIDsByTile[coordinate] else { continue }
            ids.remove(contribution.observationID)
            if ids.isEmpty {
                contributionIDsByTile.removeValue(forKey: coordinate)
            } else {
                contributionIDsByTile[coordinate] = ids
            }
        }
    }

    private func sourceImage(for observationID: Int) -> CGImage? {
        if let image = sourceCache[observationID] {
            touchCache(observationID)
            return image
        }
        guard let image = sourceProvider?(observationID), validImage(image) else { return nil }
        cache(image, for: observationID)
        return image
    }

    private func decodedPixels(
        for observationID: Int,
        suppliedImage: CGImage? = nil
    ) -> [UInt8]? {
        if let pixels = decodedSourceCache[observationID] {
            touchDecodedCache(observationID)
            return pixels
        }
        guard let image = suppliedImage ?? sourceImage(for: observationID),
              let pixels = rgbaPixels(image) else { return nil }
        decodedSourceCache[observationID] = pixels
        touchDecodedCache(observationID)
        while decodedSourceCacheOrder.count > Self.temporalWindowCapacity {
            decodedSourceCache.removeValue(forKey: decodedSourceCacheOrder.removeFirst())
        }
        return pixels
    }

    private func touchDecodedCache(_ observationID: Int) {
        decodedSourceCacheOrder.removeAll { $0 == observationID }
        decodedSourceCacheOrder.append(observationID)
    }

    private func updateTemporalDiagnostics(
        duration: Double,
        work: TemporalWork,
        shouldLog: Bool
    ) {
        let decodedBytes = decodedSourceCache.values.reduce(0) { $0 + $1.count }
        temporalDiagnosticsValue = LiveTiledAtlasTemporalDiagnostics(
            retainedFrameCount: decodedSourceCache.count,
            retainedDecodedBytes: decodedBytes,
            lastFilterMilliseconds: max(0, duration) * 1_000,
            lastConsensusPixelCount: work.consensusPixelCount,
            lastImmediatePixelCount: work.immediatePixelCount
        )
        guard shouldLog else { return }
        temporalLog.notice(
            "filter ms=\(self.temporalDiagnosticsValue.lastFilterMilliseconds, privacy: .public) frames=\(self.temporalDiagnosticsValue.retainedFrameCount, privacy: .public) bytes=\(self.temporalDiagnosticsValue.retainedDecodedBytes, privacy: .public) consensus=\(self.temporalDiagnosticsValue.lastConsensusPixelCount, privacy: .public) immediate=\(self.temporalDiagnosticsValue.lastImmediatePixelCount, privacy: .public)"
        )
    }

    private func cache(_ image: CGImage, for observationID: Int) {
        guard sourceCacheLimit > 0 else { return }
        sourceCache[observationID] = image
        touchCache(observationID)
        while sourceCacheOrder.count > sourceCacheLimit {
            sourceCache.removeValue(forKey: sourceCacheOrder.removeFirst())
        }
    }

    private func touchCache(_ observationID: Int) {
        sourceCacheOrder.removeAll { $0 == observationID }
        sourceCacheOrder.append(observationID)
    }

    private static func validPose(_ point: CGPoint?) -> Bool {
        guard let point else { return false }
        return point.x.isFinite && point.y.isFinite
    }

    private func validImage(_ image: CGImage) -> Bool {
        image.width > 0 && image.height > 0 && image.width <= Int.max / 4
            && image.width * 4 <= Int.max / image.height
    }

    private func rgbaPixels(_ image: CGImage) -> [UInt8]? {
        guard validImage(image) else { return nil }
        let bytesPerRow = image.width * 4
        var pixels = [UInt8](repeating: 0, count: bytesPerRow * image.height)
        guard let bitmap = CGContext(
            data: &pixels,
            width: image.width,
            height: image.height,
            bitsPerComponent: 8,
            bytesPerRow: bytesPerRow,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        bitmap.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return pixels
    }

    private func makeImage(_ rgba: [UInt8]) -> CGImage? {
        let data = Data(rgba)
        guard let provider = CGDataProvider(data: data as CFData) else { return nil }
        return CGImage(
            width: Self.tileSize,
            height: Self.tileSize,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: Self.tileSize * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }

    private static func tileOrder(_ lhs: TileCoordinate, _ rhs: TileCoordinate) -> Bool {
        if lhs.y != rhs.y { return lhs.y < rhs.y }
        return lhs.x < rhs.x
    }

    private static func floorDiv(_ value: Int, _ divisor: Int) -> Int {
        let quotient = value / divisor
        return value % divisor < 0 ? quotient - 1 : quotient
    }
}
