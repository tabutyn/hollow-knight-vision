import CoreGraphics
import Foundation

struct LiveWorldPatchReference {
    let observation: LiveWorldObservation
    let reference: GlobalFeatureReference
}

/// Bounded near-view appearance search. Existing landmark pixels survive moving
/// illumination even when the current image no longer selects the same corners.
/// This proposes a loop only: independent floor verification and fresh confirmation
/// remain the world tracker's responsibility. No predicted camera pose is accepted.
final class LivePatchClosureMatcher {
    private struct SourceIdentity: Equatable {
        let sourceID: Int
        let timestamp: Double
        let width: Int
        let height: Int
        let exclusions: [CGRect]
        init(_ observation: LiveWorldObservation) {
            sourceID = observation.sourceObservationID; timestamp = observation.timestamp
            width = observation.sourceWidth; height = observation.sourceHeight
            exclusions = observation.excludedRects
        }
    }
    private struct CachedGrid {
        let identity: SourceIdentity
        let grid: CalibratedSceneMotionTracker.Grid
    }
    static let maximumReferences = 32
    static let maximumPoints = 80
    static let maximumCachedGrids = 64
    private let backend: GroundComputeBackend
    private var cache = [UUID: CachedGrid]()
    private var cacheOrder = [UUID]()
    private(set) var lastReferenceCount = 0
    private(set) var lastSourceLoadCount = 0
    var cachedGridCount: Int { cache.count }

    init(backend: GroundComputeBackend = .automatic) { self.backend = backend }

    func reset() {
        cache.removeAll(keepingCapacity: true); cacheOrder.removeAll(keepingCapacity: true)
        lastReferenceCount = 0; lastSourceLoadCount = 0
    }

    func match(frame: CGImage, solveWidth: Double, excluding: [CGRect],
               references: [LiveWorldPatchReference],
               sourceProvider: (Int) -> CGImage?) -> GlobalFeatureMatch? {
        lastReferenceCount = 0; lastSourceLoadCount = 0
        guard solveWidth.isFinite, solveWidth > 0,
              Set(references.map { $0.reference.id }).count == references.count,
              let current = Self.grid(frame, excluding: excluding) else { return nil }
        var candidates = [GlobalFeatureMatch]()
        for input in references.prefix(Self.maximumReferences) {
            lastReferenceCount += 1
            let reference = input.reference, observation = input.observation
            guard reference.solveWidth == solveWidth,
                  reference.sampleWidth == current.width, reference.sampleHeight == current.height,
                  observation.groundGeometry?.lines.isEmpty == false,
                  reference.features.count >= 6 else { continue }
            let identity = SourceIdentity(observation)
            let prior: CalibratedSceneMotionTracker.Grid
            if let saved = cache[reference.id], saved.identity == identity {
                prior = saved.grid
            } else {
                lastSourceLoadCount += 1
                guard let image = sourceProvider(observation.sourceObservationID),
                      image.width == observation.sourceWidth, image.height == observation.sourceHeight,
                      let grid = Self.grid(image, excluding: observation.excludedRects) else { continue }
                prior = grid
                if cache[reference.id] == nil { cacheOrder.append(reference.id) }
                cache[reference.id] = CachedGrid(identity: identity, grid: grid)
                while cacheOrder.count > Self.maximumCachedGrids {
                    cache.removeValue(forKey: cacheOrder.removeFirst())
                }
            }
            guard prior.width == current.width, prior.height == current.height else { continue }
            let features = Array(reference.features.prefix(Self.maximumPoints))
            let fits = CalibratedSceneMotionTracker.fitBatch(prior, current,
                points: features.map(\.samplePoint), backend: backend)
            let matches = fits.enumerated().compactMap { index, fit in fit.map { (index, $0) } }
            guard matches.count >= 6 else { continue }
            let motion = CGVector(dx: Self.median(matches.map { $0.1.motion.dx }),
                                  dy: Self.median(matches.map { $0.1.motion.dy }))
            let agreeing = matches.filter {
                hypot($0.1.motion.dx - motion.dx, $0.1.motion.dy - motion.dy) <= 0.65
            }
            // Competing coherent motions cannot become a loop by winning a
            // narrow majority. Individual patches also reject repeated texture.
            guard agreeing.count >= 6, agreeing.count * 100 >= matches.count * 65 else { continue }
            let xs = agreeing.map { features[$0.0].samplePoint.x }
            let ys = agreeing.map { features[$0.0].samplePoint.y }
            guard xs.max()! - xs.min()! >= 20, ys.max()! - ys.min()! >= 18 else { continue }
            let dx = Self.median(agreeing.map { $0.1.motion.dx })
            let dy = Self.median(agreeing.map { $0.1.motion.dy })
            let residual = agreeing.map { hypot($0.1.motion.dx - dx, $0.1.motion.dy - dy) }
                .reduce(0, +) / CGFloat(agreeing.count)
            let correlation = agreeing.map { $0.1.correlation }.reduce(0, +) / Double(agreeing.count)
            let confidence = Double(agreeing.count) / Double(features.count) * correlation
                * (1 - min(0.9, Double(residual) / 0.65))
            let gain = solveWidth / Double(current.width)
            candidates.append(GlobalFeatureMatch(referenceID: reference.id,
                proposedCameraPose: CGPoint(x: reference.cameraPose.x - dx * gain,
                                            y: reference.cameraPose.y + dy * gain),
                measuredTranslation: CGPoint(x: -dx, y: -dy),
                inlierReferenceFeatureIDs: agreeing.map { features[$0.0].id }.sorted(),
                inlierCurrentFeatureIndices: agreeing.map(\.0).sorted(),
                residual: Double(residual), confidence: confidence, ambiguityRatio: 0))
        }
        candidates.sort {
            if $0.confidence != $1.confidence { return $0.confidence > $1.confidence }
            if $0.residual != $1.residual { return $0.residual < $1.residual }
            return $0.referenceID.uuidString < $1.referenceID.uuidString
        }
        guard var best = candidates.first else { return nil }
        let distance = max(solveWidth * 0.025, 4)
        let rival = candidates.dropFirst().first {
            hypot($0.proposedCameraPose.x - best.proposedCameraPose.x,
                  $0.proposedCameraPose.y - best.proposedCameraPose.y) > distance
        }
        best.ambiguityRatio = min(1, (rival?.confidence ?? 0) / max(best.confidence, .leastNonzeroMagnitude))
        guard best.ambiguityRatio < 0.80 else { return nil }
        return best
    }

    private static func grid(_ image: CGImage, excluding: [CGRect]) -> CalibratedSceneMotionTracker.Grid? {
        guard let pixels = GroundPixels.luma(image, width: image.width, height: image.height) else { return nil }
        return CalibratedSceneMotionTracker.grid(pixels: pixels, width: image.width,
            height: image.height, excluding: excluding)
    }

    private static func median(_ values: [CGFloat]) -> CGFloat {
        let sorted = values.sorted(), middle = sorted.count / 2
        return sorted.count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) * 0.5 : sorted[middle]
    }
}
