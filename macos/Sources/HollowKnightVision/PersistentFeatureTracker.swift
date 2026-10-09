import CoreGraphics

enum MapFeatureKind: Equatable {
    case candidate
    case landmark
    case relocalized
}

struct MapFeature: Equatable {
    let id: Int
    let point: CGPoint
    let confidence: CGFloat
    let depth: CGFloat
    let depthConfidence: CGFloat
    let kind: MapFeatureKind
}

struct WorldMapFeature: Equatable {
    let id: Int
    let worldPoint: CGPoint
    let registrationCameraPosition: CGPoint
    let depth: CGFloat
    let confidence: CGFloat
}

struct FeatureTrackingResult: Equatable {
    let features: [MapFeature]
    let worldFeatures: [WorldMapFeature]
    let cameraPosition: CGPoint
    let landmarkCount: Int
    let relocalizedCount: Int
    /// The world-space adjustment made to the proposed camera pose from
    /// persistent landmark consensus. A zero vector means the proposal was
    /// retained.
    let cameraPoseCorrection: CGVector
    /// Number of persistent landmark inliers that agreed on the camera pose.
    /// This can be non-zero when the proposed pose was already correct.
    let cameraPoseCorrectionSupport: Int

    init(
        features: [MapFeature],
        worldFeatures: [WorldMapFeature],
        cameraPosition: CGPoint,
        landmarkCount: Int,
        relocalizedCount: Int,
        cameraPoseCorrection: CGVector = .zero,
        cameraPoseCorrectionSupport: Int = 0
    ) {
        self.features = features
        self.worldFeatures = worldFeatures
        self.cameraPosition = cameraPosition
        self.landmarkCount = landmarkCount
        self.relocalizedCount = relocalizedCount
        self.cameraPoseCorrection = cameraPoseCorrection
        self.cameraPoseCorrectionSupport = cameraPoseCorrectionSupport
    }

    var hasCameraPoseConsensus: Bool {
        cameraPoseCorrectionSupport >= 3
            && cameraPosition.x.isFinite && cameraPosition.y.isFinite
            && cameraPoseCorrection.dx.isFinite && cameraPoseCorrection.dy.isFinite
    }

    var didCorrectCameraPose: Bool {
        hasCameraPoseConsensus && hypot(cameraPoseCorrection.dx, cameraPoseCorrection.dy) >= 0.05
    }

    static let empty = FeatureTrackingResult(
        features: [],
        worldFeatures: [],
        cameraPosition: .zero,
        landmarkCount: 0,
        relocalizedCount: 0,
        cameraPoseCorrection: .zero,
        cameraPoseCorrectionSupport: 0
    )
}

/// Maintains a small set of durable image landmarks. Candidates must remain
/// static relative to the camera solve before entering the atlas. Landmarks
/// survive normal screen exits and can correct accumulated camera drift when
/// the player backtracks into a previously mapped area.
final class PersistentFeatureTracker {
    static let targetFeatureCount = 40

    private static let sampleWidth = 240
    private static let patchRadius = 4
    private static let activeSearchRadius = 4
    private static let relocalizationSearchRadius = 12
    private static let maximumClosureMatches = 20
    private static let maximumClosureAttempts = 48
    private static let minimumCornerStrength: CGFloat = 900
    private static let minimumSpacing: CGFloat = 12
    private static let maturationFrames = 4
    private static let minimumDepthConfidence: CGFloat = 0.05
    private static let maximumDepthRefinementConfidence: CGFloat = 0.75
    private static let maximumDescriptorError: CGFloat = 0.46
    private static let maximumMotionResidual: CGFloat = 3.2

    private var active = [Int: ActiveFeature]()
    private var landmarks = [Int: Landmark]()
    private var rejectedWorldPoints = [CGPoint]()
    private var nextID = 1
    private var frameIndex = 0
    private var previousCameraPosition: CGPoint?

    func reset() {
        active.removeAll(keepingCapacity: true)
        landmarks.removeAll(keepingCapacity: true)
        rejectedWorldPoints.removeAll(keepingCapacity: true)
        nextID = 1
        frameIndex = 0
        previousCameraPosition = nil
    }

    func resetTracking(keepingLandmarks: Bool) {
        active.removeAll(keepingCapacity: true)
        previousCameraPosition = nil
        if !keepingLandmarks {
            landmarks.removeAll(keepingCapacity: true)
            rejectedWorldPoints.removeAll(keepingCapacity: true)
            nextID = 1
        }
    }

    func update(
        frame: CGImage,
        cameraPosition proposedCameraPosition: CGPoint,
        solveWidth: CGFloat,
        excluding excludedRects: [CGRect]
    ) -> FeatureTrackingResult {
        guard let sample = makeSample(from: frame), solveWidth > 0 else {
            return FeatureTrackingResult(
                features: [],
                worldFeatures: landmarks.values.map(worldMapFeature),
                cameraPosition: proposedCameraPosition,
                landmarkCount: landmarks.count,
                relocalizedCount: 0
            )
        }
        frameIndex += 1
        let cameraStep = previousCameraPosition.map {
            CGVector(
                dx: proposedCameraPosition.x - $0.x,
                dy: proposedCameraPosition.y - $0.y
            )
        } ?? .zero
        let solveToSample = CGFloat(sample.width) / solveWidth
        let frameSize = CGSize(width: frame.width, height: frame.height)
        let activeIDs = Set(active.keys)
        var matches = [FeatureMatch]()
        var failedIDs = [Int]()

        for feature in Array(active.values) {
            let expected = samplePoint(
                worldPoint: feature.worldPoint,
                cameraPosition: proposedCameraPosition,
                scale: solveToSample,
                sampleHeight: sample.height,
                depth: feature.depth
            )
            guard isInside(expected, sample: sample, margin: Self.patchRadius + 1) else {
                // Exiting through the frame boundary is normal. The landmark
                // remains in the atlas for later backtracking.
                active.removeValue(forKey: feature.id)
                continue
            }
            if isExcluded(expected, sample: sample, frameSize: frameSize, rects: excludedRects) {
                // A known foreground sprite temporarily covering a landmark is
                // an occlusion, not evidence that the world feature was bad.
                active.removeValue(forKey: feature.id)
                continue
            }
            guard let match = bestMatch(
                descriptor: feature.referenceDescriptor,
                near: expected,
                radius: Self.activeSearchRadius,
                sample: sample
            ) else {
                failedIDs.append(feature.id)
                continue
            }
            matches.append(FeatureMatch(
                id: feature.id,
                point: match.point,
                expectedPoint: expected,
                descriptor: match.descriptor,
                error: match.error,
                depth: feature.depth,
                isPersistent: feature.isPersistent
                    && feature.depthConfidence >= Self.minimumDepthConfidence,
                isRelocalized: false
            ))
        }

        // Search dormant world landmarks even while all 40 current slots are
        // occupied. These matches are loop-closure constraints first; filling
        // an open current slot is only a secondary use.
        let dormantLandmarks = landmarks.values
            .filter {
                !activeIDs.contains($0.id)
                    && $0.depthConfidence >= Self.minimumDepthConfidence
            }
            .sorted {
                if $0.observationCount != $1.observationCount {
                    return $0.observationCount > $1.observationCount
                }
                return $0.strength > $1.strength
            }
        let liveFeatureMatches = matches
        var closureAttempts = 0
        var closureMatches = 0
        for landmark in dormantLandmarks {
            guard closureMatches < Self.maximumClosureMatches,
                  closureAttempts < Self.maximumClosureAttempts else { break }
            let expected = samplePoint(
                worldPoint: landmark.worldPoint,
                cameraPosition: proposedCameraPosition,
                scale: solveToSample,
                sampleHeight: sample.height,
                depth: landmark.depth
            )
            guard isInside(expected, sample: sample, margin: Self.relocalizationSearchRadius + 1),
                  !isExcluded(expected, sample: sample, frameSize: frameSize, rects: excludedRects) else {
                continue
            }
            closureAttempts += 1
            guard let match = worldMatch(
                landmark: landmark,
                near: expected,
                liveMatches: liveFeatureMatches,
                sample: sample
            ), !matches.contains(where: {
                $0.isRelocalized && distance($0.point, match.point) < Self.minimumSpacing / 2
            }) else { continue }
            matches.append(FeatureMatch(
                id: landmark.id,
                point: match.point,
                expectedPoint: expected,
                descriptor: match.descriptor,
                error: match.error,
                depth: landmark.depth,
                isPersistent: true,
                isRelocalized: true
            ))
            closureMatches += 1
        }

        let currentMatches = matches.filter { !$0.isRelocalized }
        let loopClosureMatches = matches.filter(\.isRelocalized)
        let allMotion = consensusResidual(in: currentMatches, persistentOnly: false)
        let loopClosureMotion = consensusResidual(
            in: loopClosureMatches,
            persistentOnly: true,
            normalizeForDepth: true
        )
        let globalMotion = loopClosureMotion
            ?? consensusResidual(in: matches, persistentOnly: true, normalizeForDepth: true)
        let cameraPoseCorrection = cameraCorrection(
            residual: globalMotion?.residual,
            solveToSample: solveToSample
        )
        let correctedCameraPosition = CGPoint(
            x: proposedCameraPosition.x + cameraPoseCorrection.dx,
            y: proposedCameraPosition.y + cameraPoseCorrection.dy
        )
        let cameraPoseCorrectionSupport = globalMotion?.support ?? 0

        for id in failedIDs {
            guard var feature = active[id] else { continue }
            feature.failureCount += 1
            if feature.failureCount >= failureLimit(isPersistent: feature.isPersistent) {
                // A feature that vanishes while predicted well inside the view
                // has a lifecycle or was destroyed; it is not a map landmark.
                rejectFeature(id: id)
            } else {
                active[id] = feature
            }
        }

        var relocalizedCount = 0
        for match in matches {
            let residual = CGVector(
                dx: match.point.x - match.expectedPoint.x,
                dy: match.point.y - match.expectedPoint.y
            )
            let motionReference = (match.isRelocalized ? loopClosureMotion : allMotion)?.residual
            let residualForConsensus = match.isRelocalized && abs(match.depth) >= 0.25
                ? CGVector(dx: residual.dx / match.depth, dy: residual.dy / match.depth)
                : residual
            let motionDeviation = motionReference.map {
                hypot(residualForConsensus.dx - $0.dx, residualForConsensus.dy - $0.dy)
            } ?? 0
            let stable = match.error <= Self.maximumDescriptorError
                && motionDeviation <= Self.maximumMotionResidual
                && (!match.isRelocalized || loopClosureMotion != nil)
            if var feature = active[match.id] {
                if stable {
                    let refinedDepth = loopClosureMotion == nil
                        && feature.depthConfidence < Self.maximumDepthRefinementConfidence
                        && updateDepth(
                            feature: &feature,
                            matchedPoint: match.point,
                            cameraStep: cameraStep,
                            solveToSample: solveToSample
                        )
                    feature.point = match.point
                    feature.worldPoint = blendedWorldPoint(
                        old: feature.worldPoint,
                        samplePoint: match.point,
                        cameraPosition: correctedCameraPosition,
                        scale: solveToSample,
                        sampleHeight: sample.height,
                        depth: feature.depth,
                        alpha: feature.isPersistent ? (refinedDepth ? 1 : 0) : 0.18
                    )
                    feature.lastDescriptor = match.descriptor
                    feature.age += 1
                    feature.stableFrames += 1
                    feature.failureCount = 0
                    feature.confidence = max(0, 1 - match.error / Self.maximumDescriptorError)
                    feature.justRelocalized = false
                    active[match.id] = feature
                    if feature.isPersistent, var landmark = landmarks[match.id] {
                        landmark.worldPoint = feature.worldPoint
                        landmark.depth = feature.depth
                        landmark.depthConfidence = feature.depthConfidence
                        if refinedDepth {
                            landmark.registrationCameraPosition = correctedCameraPosition
                        }
                        landmarks[match.id] = landmark
                    }
                } else {
                    feature.failureCount += 1
                    if feature.failureCount >= failureLimit(isPersistent: feature.isPersistent) {
                        rejectFeature(id: match.id)
                    } else {
                        active[match.id] = feature
                    }
                }
            } else if match.isRelocalized, let landmark = landmarks[match.id], stable,
                      active.count < Self.targetFeatureCount,
                      !active.values.contains(where: {
                          distance($0.point, match.point) < Self.minimumSpacing
                      }) {
                active[match.id] = ActiveFeature(
                    id: landmark.id,
                    point: match.point,
                    worldPoint: landmark.worldPoint,
                    referenceDescriptor: landmark.descriptor,
                    lastDescriptor: match.descriptor,
                    strength: landmark.strength,
                    confidence: max(0, 1 - match.error / Self.maximumDescriptorError),
                    depth: landmark.depth,
                    depthConfidence: landmark.depthConfidence,
                    age: landmark.observationCount,
                    stableFrames: Self.maturationFrames,
                    failureCount: 0,
                    isPersistent: true,
                    justRelocalized: true
                )
            }
            if match.isRelocalized, stable { relocalizedCount += 1 }
            if match.isPersistent, var landmark = landmarks[match.id] {
                landmark.lastSeenFrame = frameIndex
                landmark.observationCount += 1
                landmarks[match.id] = landmark
            }
        }

        matureCandidates(
            cameraPosition: correctedCameraPosition,
            solveToSample: solveToSample,
            sampleHeight: sample.height
        )
        replenishFeatures(
            sample: sample,
            cameraPosition: correctedCameraPosition,
            solveToSample: solveToSample,
            frameSize: frameSize,
            excludedRects: excludedRects
        )
        trimAtlasIfNeeded()
        previousCameraPosition = correctedCameraPosition

        let output = active.values
            .sorted { $0.id < $1.id }
            .prefix(Self.targetFeatureCount)
            .map { feature in
                MapFeature(
                    id: feature.id,
                    point: framePoint(from: feature.point, sample: sample, frameSize: frameSize),
                    confidence: feature.confidence,
                    depth: feature.depth,
                    depthConfidence: feature.depthConfidence,
                    kind: feature.justRelocalized ? .relocalized : (feature.isPersistent ? .landmark : .candidate)
                )
            }
        return FeatureTrackingResult(
            features: Array(output),
            worldFeatures: landmarks.values.sorted { $0.id < $1.id }.map(worldMapFeature),
            cameraPosition: correctedCameraPosition,
            landmarkCount: landmarks.count,
            relocalizedCount: relocalizedCount,
            cameraPoseCorrection: cameraPoseCorrection,
            cameraPoseCorrectionSupport: cameraPoseCorrectionSupport
        )
    }

    private func matureCandidates(
        cameraPosition: CGPoint,
        solveToSample: CGFloat,
        sampleHeight: Int
    ) {
        for id in Array(active.keys) {
            guard var feature = active[id], !feature.isPersistent,
                  feature.stableFrames >= Self.maturationFrames else { continue }
            feature.isPersistent = true
            feature.worldPoint = worldPoint(
                samplePoint: feature.point,
                cameraPosition: cameraPosition,
                scale: solveToSample,
                sampleHeight: sampleHeight,
                depth: feature.depth
            )
            active[id] = feature
            landmarks[id] = Landmark(
                id: id,
                worldPoint: feature.worldPoint,
                descriptor: feature.referenceDescriptor,
                strength: feature.strength,
                depth: feature.depth,
                depthConfidence: feature.depthConfidence,
                registrationCameraPosition: cameraPosition,
                observationCount: feature.age,
                lastSeenFrame: frameIndex
            )
        }
    }

    private func replenishFeatures(
        sample: Sample,
        cameraPosition: CGPoint,
        solveToSample: CGFloat,
        frameSize: CGSize,
        excludedRects: [CGRect]
    ) {
        guard active.count < Self.targetFeatureCount else { return }
        let needed = Self.targetFeatureCount - active.count
        let existingPoints = active.values.map(\.point)
        let candidates = strongCorners(in: sample)
        var accepted = [CGPoint]()
        for candidate in candidates {
            let candidateWorldPoint = worldPoint(
                samplePoint: candidate.point,
                cameraPosition: cameraPosition,
                scale: solveToSample,
                sampleHeight: sample.height,
                depth: 1
            )
            guard accepted.count < needed,
                  !isExcluded(candidate.point, sample: sample, frameSize: frameSize, rects: excludedRects),
                  !rejectedWorldPoints.contains(where: { distance($0, candidateWorldPoint) < 18 }),
                  !existingPoints.contains(where: { distance($0, candidate.point) < Self.minimumSpacing }),
                  !accepted.contains(where: { distance($0, candidate.point) < Self.minimumSpacing }),
                  let descriptor = descriptor(at: candidate.point, in: sample) else { continue }
            let id = nextID
            nextID += 1
            let feature = ActiveFeature(
                id: id,
                point: candidate.point,
                worldPoint: candidateWorldPoint,
                referenceDescriptor: descriptor,
                lastDescriptor: descriptor,
                strength: candidate.strength,
                confidence: 0.35,
                depth: 1,
                depthConfidence: 0,
                age: 1,
                stableFrames: 1,
                failureCount: 0,
                isPersistent: false,
                justRelocalized: false
            )
            active[id] = feature
            accepted.append(candidate.point)
        }
    }

    private func cameraCorrection(
        residual: CGVector?,
        solveToSample: CGFloat
    ) -> CGVector {
        guard let residual, solveToSample > 0 else { return .zero }
        var correction = CGVector(
            dx: -residual.dx / solveToSample,
            dy: residual.dy / solveToSample
        )
        let magnitude = hypot(correction.dx, correction.dy)
        if magnitude > 28 {
            correction.dx *= 28 / magnitude
            correction.dy *= 28 / magnitude
        }
        return correction
    }

    private func consensusResidual(
        in matches: [FeatureMatch],
        persistentOnly: Bool,
        normalizeForDepth: Bool = false
    ) -> ResidualConsensus? {
        let eligible = matches.filter {
            (!persistentOnly || $0.isPersistent)
                && $0.error <= Self.maximumDescriptorError
                && (!normalizeForDepth || abs($0.depth) >= 0.25)
        }
        let minimum = persistentOnly ? 3 : 5
        guard eligible.count >= minimum else { return nil }
        func residual(_ match: FeatureMatch) -> CGVector {
            let divisor = normalizeForDepth ? match.depth : 1
            return CGVector(
                dx: (match.point.x - match.expectedPoint.x) / divisor,
                dy: (match.point.y - match.expectedPoint.y) / divisor
            )
        }
        let residuals = eligible.map(residual)
        let initial = CGVector(
            dx: median(residuals.map(\.dx)),
            dy: median(residuals.map(\.dy))
        )
        let inliers = residuals.filter {
            hypot(
                $0.dx - initial.dx,
                $0.dy - initial.dy
            ) <= Self.maximumMotionResidual
        }
        guard inliers.count >= minimum else { return nil }
        return ResidualConsensus(
            residual: CGVector(
                dx: median(inliers.map(\.dx)),
                dy: median(inliers.map(\.dy))
            ),
            support: inliers.count
        )
    }

    private func bestMatch(
        descriptor reference: [Float],
        near expected: CGPoint,
        radius: Int,
        sample: Sample,
        maximumError: CGFloat = 0.46
    ) -> DescriptorMatch? {
        let centerX = Int(expected.x.rounded())
        let centerY = Int(expected.y.rounded())
        var best: DescriptorMatch?
        for dy in -radius...radius {
            for dx in -radius...radius {
                let point = CGPoint(x: centerX + dx, y: centerY + dy)
                // Most points in a search window lose. Score them directly
                // from the fixed 9x9 gradient patch so they do not each build
                // and normalize a transient 162-float Array.
                guard let error = descriptorError(reference, at: point, in: sample) else { continue }
                if error < (best?.error ?? .greatestFiniteMagnitude) {
                    best = DescriptorMatch(point: point, error: error)
                }
            }
        }
        guard let best,
              best.error <= maximumError,
              let descriptor = descriptor(at: best.point, in: sample) else { return nil }
        return DescriptorMatch(point: best.point, descriptor: descriptor, error: best.error)
    }

    private func worldMatch(
        landmark: Landmark,
        near expected: CGPoint,
        liveMatches: [FeatureMatch],
        sample: Sample
    ) -> DescriptorMatch? {
        // Prefer an explicit current-feature-to-world-feature comparison. The
        // local pixel search is retained for returns where the current tracker
        // has not populated this part of the screen yet.
        var currentMatch: DescriptorMatch?
        for liveMatch in liveMatches where distance(
            liveMatch.point,
            expected
        ) <= CGFloat(Self.relocalizationSearchRadius) {
            let error = descriptorDistance(landmark.descriptor, liveMatch.descriptor)
            if error < (currentMatch?.error ?? .greatestFiniteMagnitude) {
                currentMatch = DescriptorMatch(
                    point: liveMatch.point,
                    descriptor: liveMatch.descriptor,
                    error: error
                )
            }
        }
        if let currentMatch, currentMatch.error <= 0.40 {
            return currentMatch
        }
        return bestMatch(
            descriptor: landmark.descriptor,
            near: expected,
            radius: Self.relocalizationSearchRadius,
            sample: sample,
            maximumError: 0.40
        )
    }

    private func strongCorners(in sample: Sample) -> [Corner] {
        let margin = Self.patchRadius + 2
        guard sample.width > margin * 2, sample.height > margin * 2 else { return [] }
        var corners = [Corner]()
        for y in margin..<(sample.height - margin) {
            for x in margin..<(sample.width - margin) {
                let strength = cornerStrength(x: x, y: y, sample: sample)
                if strength >= Self.minimumCornerStrength {
                    corners.append(Corner(point: CGPoint(x: x, y: y), strength: strength))
                }
            }
        }
        return corners.sorted { $0.strength > $1.strength }
    }

    private func cornerStrength(x: Int, y: Int, sample: Sample) -> CGFloat {
        var xx = CGFloat.zero
        var xy = CGFloat.zero
        var yy = CGFloat.zero
        for offsetY in -1...1 {
            for offsetX in -1...1 {
                let gx = CGFloat(Int(sample[x + offsetX + 1, y + offsetY])
                    - Int(sample[x + offsetX - 1, y + offsetY]))
                let gy = CGFloat(Int(sample[x + offsetX, y + offsetY + 1])
                    - Int(sample[x + offsetX, y + offsetY - 1]))
                xx += gx * gx
                xy += gx * gy
                yy += gy * gy
            }
        }
        return max(0, (xx + yy - sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / 2)
    }

    private func descriptor(at point: CGPoint, in sample: Sample) -> [Float]? {
        let x = Int(point.x.rounded())
        let y = Int(point.y.rounded())
        let margin = Self.patchRadius + 1
        guard x >= margin, x < sample.width - margin,
              y >= margin, y < sample.height - margin else { return nil }
        var values = [Float]()
        values.reserveCapacity((Self.patchRadius * 2 + 1) * (Self.patchRadius * 2 + 1) * 2)
        var energy = Float.zero
        for offsetY in -Self.patchRadius...Self.patchRadius {
            for offsetX in -Self.patchRadius...Self.patchRadius {
                let gx = Float(Int(sample[x + offsetX + 1, y + offsetY])
                    - Int(sample[x + offsetX - 1, y + offsetY]))
                let gy = Float(Int(sample[x + offsetX, y + offsetY + 1])
                    - Int(sample[x + offsetX, y + offsetY - 1]))
                values.append(gx)
                values.append(gy)
                energy += gx * gx + gy * gy
            }
        }
        guard energy >= 400 else { return nil }
        let inverseNorm = 1 / sqrt(energy)
        return values.map { $0 * inverseNorm }
    }

    private func descriptorDistance(_ first: [Float], _ second: [Float]) -> CGFloat {
        guard first.count == second.count else { return .greatestFiniteMagnitude }
        var squared = Float.zero
        for index in first.indices {
            let difference = first[index] - second[index]
            squared += difference * difference
        }
        return CGFloat(sqrt(squared))
    }

    /// Scores a descriptor without allocating descriptor storage. The caller
    /// materializes only the single winning patch with `descriptor(at:in:)`.
    private func descriptorError(
        _ reference: [Float],
        at point: CGPoint,
        in sample: Sample
    ) -> CGFloat? {
        let x = Int(point.x.rounded())
        let y = Int(point.y.rounded())
        let margin = Self.patchRadius + 1
        guard x >= margin, x < sample.width - margin,
              y >= margin, y < sample.height - margin,
              reference.count == (Self.patchRadius * 2 + 1) * (Self.patchRadius * 2 + 1) * 2 else {
            return nil
        }
        var energy = Float.zero
        for offsetY in -Self.patchRadius...Self.patchRadius {
            for offsetX in -Self.patchRadius...Self.patchRadius {
                let gx = Float(Int(sample[x + offsetX + 1, y + offsetY])
                    - Int(sample[x + offsetX - 1, y + offsetY]))
                let gy = Float(Int(sample[x + offsetX, y + offsetY + 1])
                    - Int(sample[x + offsetX, y + offsetY - 1]))
                energy += gx * gx + gy * gy
            }
        }
        guard energy >= 400 else { return nil }
        let inverseNorm = 1 / sqrt(energy)
        var squared = Float.zero
        var index = 0
        for offsetY in -Self.patchRadius...Self.patchRadius {
            for offsetX in -Self.patchRadius...Self.patchRadius {
                let gx = Float(Int(sample[x + offsetX + 1, y + offsetY])
                    - Int(sample[x + offsetX - 1, y + offsetY])) * inverseNorm
                let gy = Float(Int(sample[x + offsetX, y + offsetY + 1])
                    - Int(sample[x + offsetX, y + offsetY - 1])) * inverseNorm
                let xDifference = reference[index] - gx
                let yDifference = reference[index + 1] - gy
                squared += xDifference * xDifference + yDifference * yDifference
                index += 2
            }
        }
        return CGFloat(sqrt(squared))
    }

    private func blendedWorldPoint(
        old: CGPoint,
        samplePoint: CGPoint,
        cameraPosition: CGPoint,
        scale: CGFloat,
        sampleHeight: Int,
        depth: CGFloat,
        alpha: CGFloat
    ) -> CGPoint {
        guard alpha > 0 else { return old }
        let measured = worldPoint(
            samplePoint: samplePoint,
            cameraPosition: cameraPosition,
            scale: scale,
            sampleHeight: sampleHeight,
            depth: depth
        )
        return CGPoint(
            x: old.x * (1 - alpha) + measured.x * alpha,
            y: old.y * (1 - alpha) + measured.y * alpha
        )
    }

    private func worldPoint(
        samplePoint: CGPoint,
        cameraPosition: CGPoint,
        scale: CGFloat,
        sampleHeight: Int,
        depth: CGFloat
    ) -> CGPoint {
        CGPoint(
            x: cameraPosition.x * depth + samplePoint.x / scale,
            y: cameraPosition.y * depth + (CGFloat(sampleHeight) - samplePoint.y) / scale
        )
    }

    private func samplePoint(
        worldPoint: CGPoint,
        cameraPosition: CGPoint,
        scale: CGFloat,
        sampleHeight: Int,
        depth: CGFloat
    ) -> CGPoint {
        CGPoint(
            x: (worldPoint.x - cameraPosition.x * depth) * scale,
            y: CGFloat(sampleHeight) - (worldPoint.y - cameraPosition.y * depth) * scale
        )
    }

    private func framePoint(from point: CGPoint, sample: Sample, frameSize: CGSize) -> CGPoint {
        CGPoint(
            x: point.x * frameSize.width / CGFloat(sample.width),
            y: frameSize.height - point.y * frameSize.height / CGFloat(sample.height)
        )
    }

    private func isExcluded(
        _ point: CGPoint,
        sample: Sample,
        frameSize: CGSize,
        rects: [CGRect]
    ) -> Bool {
        let pointInFrame = framePoint(from: point, sample: sample, frameSize: frameSize)
        return rects.contains { $0.insetBy(dx: -4, dy: -4).contains(pointInFrame) }
    }

    private func isInside(_ point: CGPoint, sample: Sample, margin: Int) -> Bool {
        point.x >= CGFloat(margin) && point.x < CGFloat(sample.width - margin)
            && point.y >= CGFloat(margin) && point.y < CGFloat(sample.height - margin)
    }

    private func median(_ values: [CGFloat]) -> CGFloat {
        let sorted = values.sorted()
        guard !sorted.isEmpty else { return 0 }
        let middle = sorted.count / 2
        return sorted.count.isMultiple(of: 2)
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle]
    }

    private func distance(_ first: CGPoint, _ second: CGPoint) -> CGFloat {
        hypot(first.x - second.x, first.y - second.y)
    }

    private func failureLimit(isPersistent: Bool) -> Int {
        // Keep a world landmark through short occlusions and lighting changes;
        // candidates still fail quickly, while truly destroyed landmarks are
        // removed after a sustained mismatch.
        isPersistent ? 6 : 2
    }

    @discardableResult
    private func updateDepth(
        feature: inout ActiveFeature,
        matchedPoint: CGPoint,
        cameraStep: CGVector,
        solveToSample: CGFloat
    ) -> Bool {
        let expectedMotion = CGVector(
            dx: -cameraStep.dx * solveToSample,
            dy: cameraStep.dy * solveToSample
        )
        let magnitudeSquared = expectedMotion.dx * expectedMotion.dx
            + expectedMotion.dy * expectedMotion.dy
        guard magnitudeSquared >= 0.75 * 0.75 else { return false }
        let observed = CGVector(
            dx: matchedPoint.x - feature.point.x,
            dy: matchedPoint.y - feature.point.y
        )
        let magnitude = sqrt(magnitudeSquared)
        let measuredDepth = (observed.dx * expectedMotion.dx + observed.dy * expectedMotion.dy)
            / magnitudeSquared
        let perpendicular = abs(observed.dx * expectedMotion.dy - observed.dy * expectedMotion.dx)
            / magnitude
        guard measuredDepth >= -0.25, measuredDepth <= 3.5, perpendicular <= 2.5 else {
            return false
        }
        let evidence = min(1, magnitude / 3) * max(0, 1 - perpendicular / 2.5)
        let alpha = feature.depthConfidence == 0 ? 1 : max(0.08, min(0.32, evidence * 0.28))
        feature.depth = feature.depth * (1 - alpha) + measuredDepth * alpha
        feature.depthConfidence = min(1, feature.depthConfidence + evidence * 0.16)
        return true
    }

    private func worldMapFeature(_ landmark: Landmark) -> WorldMapFeature {
        WorldMapFeature(
            id: landmark.id,
            worldPoint: landmark.worldPoint,
            registrationCameraPosition: landmark.registrationCameraPosition,
            depth: landmark.depth,
            confidence: landmark.depthConfidence
        )
    }

    private func trimAtlasIfNeeded() {
        guard landmarks.count > 1_200 else { return }
        let obsolete = landmarks.values
            .sorted { $0.lastSeenFrame < $1.lastSeenFrame }
            .prefix(landmarks.count - 1_200)
            .map(\.id)
        for id in obsolete where active[id] == nil {
            landmarks.removeValue(forKey: id)
        }
    }

    private func rejectFeature(id: Int) {
        if let worldPoint = active[id]?.worldPoint ?? landmarks[id]?.worldPoint {
            rejectedWorldPoints.append(worldPoint)
            if rejectedWorldPoints.count > 1_000 {
                rejectedWorldPoints.removeFirst(rejectedWorldPoints.count - 1_000)
            }
        }
        active.removeValue(forKey: id)
        landmarks.removeValue(forKey: id)
    }

    private func makeSample(from image: CGImage) -> Sample? {
        let height = max(1, Int(
            (CGFloat(image.height) * CGFloat(Self.sampleWidth) / CGFloat(max(1, image.width))).rounded()
        ))
        var pixels = [UInt8](repeating: 0, count: Self.sampleWidth * height)
        let created = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: Self.sampleWidth,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: Self.sampleWidth,
                space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGImageAlphaInfo.none.rawValue
            ) else { return false }
            context.interpolationQuality = .low
            context.draw(image, in: CGRect(x: 0, y: 0, width: Self.sampleWidth, height: height))
            return true
        }
        return created ? Sample(pixels: pixels, width: Self.sampleWidth, height: height) : nil
    }

    private struct Sample {
        let pixels: [UInt8]
        let width: Int
        let height: Int

        subscript(_ x: Int, _ y: Int) -> UInt8 { pixels[y * width + x] }
    }

    private struct ActiveFeature {
        let id: Int
        var point: CGPoint
        var worldPoint: CGPoint
        let referenceDescriptor: [Float]
        var lastDescriptor: [Float]
        let strength: CGFloat
        var confidence: CGFloat
        var depth: CGFloat
        var depthConfidence: CGFloat
        var age: Int
        var stableFrames: Int
        var failureCount: Int
        var isPersistent: Bool
        var justRelocalized: Bool
    }

    private struct Landmark {
        let id: Int
        var worldPoint: CGPoint
        let descriptor: [Float]
        let strength: CGFloat
        var depth: CGFloat
        var depthConfidence: CGFloat
        var registrationCameraPosition: CGPoint
        var observationCount: Int
        var lastSeenFrame: Int
    }

    private struct FeatureMatch {
        let id: Int
        let point: CGPoint
        let expectedPoint: CGPoint
        let descriptor: [Float]
        let error: CGFloat
        let depth: CGFloat
        let isPersistent: Bool
        let isRelocalized: Bool
    }

    private struct ResidualConsensus {
        let residual: CGVector
        let support: Int
    }

    private struct DescriptorMatch {
        let point: CGPoint
        let descriptor: [Float]
        let error: CGFloat

        init(point: CGPoint, descriptor: [Float] = [], error: CGFloat) {
            self.point = point
            self.descriptor = descriptor
            self.error = error
        }
    }

    private struct Corner {
        let point: CGPoint
        let strength: CGFloat
    }
}
