import CoreGraphics
import Foundation

/// A compact, persistent appearance record for a frame that can be searched without a pose prior.
struct GlobalFeatureReference: Codable, Equatable {
  struct Feature: Codable, Equatable {
    var id: Int
    var samplePoint: CGPoint
    /// Luma values in a 5 by 5 patch, stored row-major.
    var descriptor: [UInt8]
    var strength: Int
    /// Kept with the appearance observation so a later multi-plane solve can use it.
    var depth: Double
    var depthConfidence: Double
  }

  var id: UUID
  var cameraPose: CGPoint
  var solveWidth: Double
  var sourceWidth: Int
  var sourceHeight: Int
  var sampleWidth: Int
  var sampleHeight: Int
  var features: [Feature]
}

struct GlobalFeatureMatch: Equatable {
  var referenceID: UUID
  /// Camera position in the reference's solve coordinate system.
  var proposedCameraPose: CGPoint
  /// Reference sample coordinate minus current sample coordinate.
  var measuredTranslation: CGPoint
  var inlierReferenceFeatureIDs: [Int]
  var inlierCurrentFeatureIndices: [Int]
  /// Mean translation error in sample pixels.
  var residual: Double
  var confidence: Double
  /// Runner-up confidence divided by selected confidence. Lower is less ambiguous.
  var ambiguityRatio: Double
}

/// Searches keyframes by local appearance. It deliberately accepts no predicted camera pose.
final class GlobalFeatureMatcher {
  private let sampleWidth = 160
  private let maximumFeatures = 96
  private let maximumReferences = 32
  private let consensusRadius: CGFloat = 3
  private let ambiguityLimit = 0.80
  /// Advances only the bounded search window; it is never derived from a pose.
  private var referenceScanOffset = 0
  private struct AmbiguityCacheEntry {
    let fingerprint: UInt64
    let ambiguous: Bool
  }
  private var ambiguityByReferenceID = [UUID: AmbiguityCacheEntry]()
  /// Exposed to the module's tests so the repeated-texture guard can be kept bounded.
  private(set) var lastSelfPlacementPairChecks = 0
  private(set) var lastSelfPlacementWasAmbiguous = false

  func makeReference(
    id: UUID = UUID(), frame: CGImage, cameraPose: CGPoint, solveWidth: Double,
    depth: Double = 1, depthConfidence: Double = 0, excluding: [CGRect] = []
  ) -> GlobalFeatureReference? {
    guard solveWidth.isFinite, solveWidth > 0, cameraPose.x.isFinite, cameraPose.y.isFinite,
      depth.isFinite, depth > 0, depthConfidence.isFinite, (0...1).contains(depthConfidence),
      excluding.allSatisfy(Self.validRect),
      let grid = sample(frame), grid.width > 0, grid.height > 0
    else { return nil }
    let features = corners(in: grid, allowed: { point in
      let sourcePoint = CGPoint(
        x: point.x * CGFloat(frame.width) / CGFloat(grid.width),
        y: CGFloat(frame.height) - 1 - point.y * CGFloat(frame.height) / CGFloat(grid.height))
      return !excluding.contains(where: { $0.insetBy(dx: -2, dy: -2).contains(sourcePoint) })
    }).enumerated().map { index, corner in
      GlobalFeatureReference.Feature(
        id: index, samplePoint: corner.point, descriptor: corner.descriptor,
        strength: corner.strength, depth: depth, depthConfidence: depthConfidence)
    }
    guard features.count >= 6 else { return nil }
    return GlobalFeatureReference(
      id: id, cameraPose: cameraPose, solveWidth: solveWidth,
      sourceWidth: frame.width, sourceHeight: frame.height,
      sampleWidth: grid.width, sampleHeight: grid.height, features: features)
  }

  func match(
    frame: CGImage, references: [GlobalFeatureReference], preferredReferenceID: UUID? = nil,
    currentSolveWidth: Double? = nil,
    excluding: [CGRect] = []
  ) -> GlobalFeatureMatch? {
    guard currentSolveWidth.map({ $0.isFinite && $0 > 0 }) ?? true else { return nil }
    guard let currentGrid = sample(frame) else { return nil }
    let currentFeatures = corners(in: currentGrid, allowed: { point in
      let sourcePoint = CGPoint(
        x: point.x * CGFloat(frame.width) / CGFloat(currentGrid.width),
        y: CGFloat(frame.height) - 1 - point.y * CGFloat(frame.height) / CGFloat(currentGrid.height))
      return !excluding.contains(where: { $0.insetBy(dx: -2, dy: -2).contains(sourcePoint) })
    })
    guard currentFeatures.count >= 6 else { return nil }

    // Score every saved reference cheaply, then run full reciprocal matching
    // on a bounded, spatially diverse slice. Subsequent scans rotate through
    // the ranked list, so a UUID tie cannot permanently hide a distant room.
    let currentSignature = appearanceSignature(currentFeatures.map(\.descriptor))
    let selectedReferences = selectReferences(
      references, matching: currentSignature, preferredReferenceID: preferredReferenceID)
    var allCandidates = [Candidate]()
    for reference in selectedReferences {
      allCandidates.append(contentsOf: candidatePlacements(
        reference: reference, current: currentFeatures, currentGrid: currentGrid,
        currentSolveWidth: CGFloat(currentSolveWidth ?? reference.solveWidth)))
    }
    let ordered = allCandidates.sorted(by: candidateOrder)
    guard let best = ordered.first else { return nil }
    // Adjacent keyframes often overlap and independently vote for the same
    // camera position. They are corroboration, not an ambiguous second place.
    // Only a comparably strong, geometrically distinct placement is a reason
    // to reject the scan.
    let distinctPlacementDistance = max(
      CGFloat(currentSolveWidth ?? best.reference.solveWidth) * 0.025,
      4
    )
    let runnerUp = ordered.dropFirst().first {
      distance($0.camera, best.camera) > distinctPlacementDistance
    }
    let ambiguity = min(1, (runnerUp?.confidence ?? 0) / max(best.confidence, .leastNonzeroMagnitude))
    guard ambiguity < ambiguityLimit else { return nil }
    return GlobalFeatureMatch(
      referenceID: best.reference.id, proposedCameraPose: best.camera,
      measuredTranslation: best.translation,
      inlierReferenceFeatureIDs: best.inliers.map { best.reference.features[$0.reference].id }.sorted(),
      inlierCurrentFeatureIndices: best.inliers.map(\.current).sorted(), residual: best.residual,
      confidence: best.confidence, ambiguityRatio: ambiguity)
  }

  private struct Grid { var pixels: [UInt8]; var width: Int; var height: Int }
  private struct Corner { var point: CGPoint; var descriptor: [UInt8]; var strength: Int }
  private struct Pair { var reference: Int; var current: Int; var score: Int; var translation: CGPoint }
  private struct Candidate {
    var reference: GlobalFeatureReference
    var inliers: [Pair]
    var translation: CGPoint
    var residual: Double
    var confidence: Double
    var camera: CGPoint
  }
  private struct CoverageCell: Hashable { var x: Int; var y: Int }

  private func selectReferences(
    _ references: [GlobalFeatureReference], matching signature: [Int], preferredReferenceID: UUID?
  ) -> [GlobalFeatureReference] {
    let ranked = references.filter(valid).sorted {
      let left = signatureDistance(appearanceSignature($0.features.map(\.descriptor)), signature)
      let right = signatureDistance(appearanceSignature($1.features.map(\.descriptor)), signature)
      return left == right ? $0.id.uuidString < $1.id.uuidString : left < right
    }
    guard ranked.count > maximumReferences else { return ranked }

    // Reserve part of the budget for map coverage before filling the rest
    // from the appearance ranking. This makes a matching keyframe beyond the
    // first 32 UUID-tied records visible on the first scan when it represents
    // another area; the rotating window covers the remaining records later.
    let coverageBudget = maximumReferences / 4
    var firstByCell = [CoverageCell: GlobalFeatureReference]()
    for reference in ranked {
      let cellSize = max(1, reference.solveWidth * 4)
      let cell = CoverageCell(
        x: Int((Double(reference.cameraPose.x) / cellSize).rounded(.down)),
        y: Int((Double(reference.cameraPose.y) / cellSize).rounded(.down)))
      if firstByCell[cell] == nil { firstByCell[cell] = reference }
    }
    let coverage = firstByCell.values.sorted {
      let left = signatureDistance(appearanceSignature($0.features.map(\.descriptor)), signature)
      let right = signatureDistance(appearanceSignature($1.features.map(\.descriptor)), signature)
      return left == right ? $0.id.uuidString < $1.id.uuidString : left < right
    }
    let preferred = preferredReferenceID.flatMap { id in
      ranked.first(where: { $0.id == id })
    }
    var selected = preferred.map { [$0] } ?? []
    let rotatedCoverage = rotated(coverage, by: referenceScanOffset)
    for reference in rotatedCoverage where !selected.contains(where: { $0.id == reference.id }) {
      selected.append(reference)
      if selected.count == coverageBudget + (preferred == nil ? 0 : 1) { break }
    }
    let rotatedRanked = rotated(ranked, by: referenceScanOffset)
    for reference in rotatedRanked where !selected.contains(where: { $0.id == reference.id }) {
      selected.append(reference)
      if selected.count == maximumReferences { break }
    }
    referenceScanOffset = (referenceScanOffset + maximumReferences - coverageBudget) % ranked.count
    return selected
  }

  private func rotated<T>(_ values: [T], by offset: Int) -> [T] {
    guard !values.isEmpty else { return [] }
    let start = offset % values.count
    return Array(values[start...]) + Array(values[..<start])
  }

  private func valid(_ reference: GlobalFeatureReference) -> Bool {
    reference.solveWidth.isFinite && reference.solveWidth > 0
      && reference.cameraPose.x.isFinite && reference.cameraPose.y.isFinite
      && reference.sourceWidth > 0 && reference.sourceHeight > 0
      && reference.sampleWidth > 0 && reference.sampleHeight > 0
      && reference.features.count >= 6
      && reference.features.allSatisfy {
        $0.descriptor.count == 25 && $0.samplePoint.x.isFinite && $0.samplePoint.y.isFinite
          && $0.samplePoint.x >= 0 && $0.samplePoint.x < CGFloat(reference.sampleWidth)
          && $0.samplePoint.y >= 0 && $0.samplePoint.y < CGFloat(reference.sampleHeight)
          && $0.depth.isFinite && $0.depth > 0
          && $0.depthConfidence.isFinite && (0...1).contains($0.depthConfidence)
      }
  }

  private func candidatePlacements(
    reference: GlobalFeatureReference, current: [Corner], currentGrid: Grid,
    currentSolveWidth: CGFloat
  ) -> [Candidate] {
    let referenceFeatures = Array(reference.features.prefix(maximumFeatures))
    guard referenceFeatures.count >= 6 else { return [] }
    let referenceCorners = referenceFeatures.map {
      Corner(point: $0.samplePoint, descriptor: $0.descriptor, strength: $0.strength)
    }
    // Repeated texture can provide several equally valid placements within one keyframe.
    // Detect that before arbitrary nearest-neighbour ordering turns it into a false closure.
    guard !hasAmbiguousSelfPlacement(
      referenceID: reference.id,
      features: referenceCorners
    ) else { return [] }
    // Current feature points are normalized into the reference sample plane before consensus.
    let normalizedCurrent = current.map {
      Corner(
        point: CGPoint(
          x: $0.point.x * CGFloat(reference.sampleWidth) / CGFloat(currentGrid.width),
          y: $0.point.y * CGFloat(reference.sampleHeight) / CGFloat(currentGrid.height)),
        descriptor: $0.descriptor, strength: $0.strength)
    }
    let referenceNearest = referenceCorners.map { nearest($0.descriptor, in: normalizedCurrent) }
    let currentNearest = normalizedCurrent.map { nearest($0.descriptor, in: referenceCorners) }
    var pairs = [Pair]()
    for index in referenceFeatures.indices {
      guard let choice = referenceNearest[index], choice.accepted,
        let reverse = currentNearest[choice.index], reverse.accepted, reverse.index == index
      else { continue }
      pairs.append(Pair(
        reference: index, current: choice.index, score: choice.distance,
        translation: CGPoint(
          x: referenceFeatures[index].samplePoint.x - normalizedCurrent[choice.index].point.x,
          y: referenceFeatures[index].samplePoint.y - normalizedCurrent[choice.index].point.y)))
    }
    guard pairs.count >= 6 else { return [] }

    var result = [Candidate]()
    var remaining = pairs
    while remaining.count >= 6, result.count < 3 {
      guard let seed = consensusSeed(remaining) else { break }
      let inliers = remaining.filter { distance($0.translation, seed) <= consensusRadius }
      let translation = average(inliers.map(\.translation))
      let refined = remaining.filter { distance($0.translation, translation) <= consensusRadius }
      guard refined.count >= 6, spatiallySpread(refined.map { referenceFeatures[$0.reference].samplePoint })
      else {
        remaining.removeAll { distance($0.translation, seed) <= consensusRadius }
        continue
      }
      let residual = refined.map { Double(distance($0.translation, translation)) }.reduce(0, +)
        / Double(refined.count)
      let descriptorQuality = 1 - min(0.9, Double(refined.map(\.score).reduce(0, +)) / Double(refined.count * 2_000))
      let coverage = Double(refined.count) / Double(max(referenceFeatures.count, normalizedCurrent.count))
      let confidence = coverage * descriptorQuality * (1 - min(0.9, residual / Double(consensusRadius)))
      let referenceGain = CGFloat(reference.solveWidth) / CGFloat(reference.sampleWidth)
      let currentGain = currentSolveWidth / CGFloat(currentGrid.width)
      // Solve coordinates are tied to the current frame, not the saved one.
      // Recover the camera from each landmark in its native frame scale, then
      // average the inlier estimates in the reference coordinate system.
      let camera = average(refined.map { pair in
        let referencePoint = referenceFeatures[pair.reference].samplePoint
        let currentPoint = current[pair.current].point
        return CGPoint(
          x: reference.cameraPose.x + referencePoint.x * referenceGain - currentPoint.x * currentGain,
          y: reference.cameraPose.y
            + (CGFloat(reference.sampleHeight) - referencePoint.y) * referenceGain
            - (CGFloat(currentGrid.height) - currentPoint.y) * currentGain)
      })
      result.append(Candidate(
        reference: reference, inliers: refined, translation: translation, residual: residual,
        confidence: confidence, camera: camera))
      // Alternatives need a genuinely different placement, not another seed in this mode.
      remaining.removeAll { distance($0.translation, translation) <= consensusRadius * 2 }
    }
    return result
  }

  private func candidateOrder(_ left: Candidate, _ right: Candidate) -> Bool {
    if left.confidence != right.confidence { return left.confidence > right.confidence }
    if left.residual != right.residual { return left.residual < right.residual }
    if left.reference.id != right.reference.id {
      return left.reference.id.uuidString < right.reference.id.uuidString
    }
    if left.translation.x != right.translation.x { return left.translation.x < right.translation.x }
    return left.translation.y < right.translation.y
  }

  private func nearest(_ descriptor: [UInt8], in candidates: [Corner]) -> (index: Int, distance: Int, accepted: Bool)? {
    guard !candidates.isEmpty else { return nil }
    var best: (index: Int, distance: Int)?
    var second: (index: Int, distance: Int)?
    for index in candidates.indices {
      let candidate = (index: index, distance: descriptorDistance(descriptor, candidates[index].descriptor))
      if best.map({ candidate.distance < $0.distance || (candidate.distance == $0.distance && candidate.index < $0.index) }) ?? true {
        second = best
        best = candidate
      } else if second.map({ candidate.distance < $0.distance || (candidate.distance == $0.distance && candidate.index < $0.index) }) ?? true {
        second = candidate
      }
    }
    guard let best, best.distance <= 1_200 else { return nil }
    let ratioPass = second.map { Double($0.distance) > Double(best.distance) * 1.12 } ?? true
    return (best.index, best.distance, ratioPass)
  }

  private func consensusSeed(_ pairs: [Pair]) -> CGPoint? {
    pairs.enumerated().min { left, right in
      let leftSupport = pairs.filter { distance($0.translation, left.element.translation) <= consensusRadius }.count
      let rightSupport = pairs.filter { distance($0.translation, right.element.translation) <= consensusRadius }.count
      if leftSupport != rightSupport { return leftSupport > rightSupport }
      let leftError = pairs.filter { distance($0.translation, left.element.translation) <= consensusRadius }
        .map { distance($0.translation, left.element.translation) }.reduce(0, +)
      let rightError = pairs.filter { distance($0.translation, right.element.translation) <= consensusRadius }
        .map { distance($0.translation, right.element.translation) }.reduce(0, +)
      if leftError != rightError { return leftError < rightError }
      return left.offset < right.offset
    }?.element.translation
  }

  private func hasAmbiguousSelfPlacement(referenceID: UUID, features: [Corner]) -> Bool {
    struct TranslationBin: Hashable { var x: Int; var y: Int }
    struct DescriptorBin: Hashable { var first: Int; var middle: Int; var last: Int }
    var fingerprint: UInt64 = UInt64(features.count)
    for feature in features {
      for value in feature.descriptor {
        fingerprint = (fingerprint &* 1_099_511_628_211) ^ UInt64(value)
      }
    }
    if let cached = ambiguityByReferenceID[referenceID], cached.fingerprint == fingerprint {
      lastSelfPlacementWasAmbiguous = cached.ambiguous
      lastSelfPlacementPairChecks = 0
      return cached.ambiguous
    }

    let binSize = max(1, consensusRadius)
    var translations = [CGPoint]()
    // L1 descriptor distance <= 40 implies that each sampled component is
    // also within 40. Three 41-wide component bins therefore give a complete
    // candidate index while avoiding an all-pairs descriptor scan.
    let descriptorBinWidth = 41
    var descriptorBins = [DescriptorBin: [Int]]()
    for index in features.indices {
      let descriptor = features[index].descriptor
      let bin = DescriptorBin(
        first: Int(descriptor[0]) / descriptorBinWidth,
        middle: Int(descriptor[12]) / descriptorBinWidth,
        last: Int(descriptor[24]) / descriptorBinWidth
      )
      descriptorBins[bin, default: []].append(index)
    }
    lastSelfPlacementPairChecks = 0
    lastSelfPlacementWasAmbiguous = false
    for first in features.indices {
      let descriptor = features[first].descriptor
      let center = DescriptorBin(
        first: Int(descriptor[0]) / descriptorBinWidth,
        middle: Int(descriptor[12]) / descriptorBinWidth,
        last: Int(descriptor[24]) / descriptorBinWidth
      )
      for firstOffset in -1...1 {
        for middleOffset in -1...1 {
          for lastOffset in -1...1 {
            let candidates = descriptorBins[DescriptorBin(
              first: center.first + firstOffset,
              middle: center.middle + middleOffset,
              last: center.last + lastOffset
            )] ?? []
            for second in candidates where first != second {
              lastSelfPlacementPairChecks += 1
              guard descriptorDistance(features[first].descriptor, features[second].descriptor) <= 40 else { continue }
              let translation = CGPoint(
                x: features[first].point.x - features[second].point.x,
                y: features[first].point.y - features[second].point.y)
              guard hypot(translation.x, translation.y) >= 8 else { continue }
              translations.append(translation)
            }
          }
        }
      }
    }
    guard translations.count >= 6 else {
      ambiguityByReferenceID[referenceID] = AmbiguityCacheEntry(
        fingerprint: fingerprint,
        ambiguous: false
      )
      return false
    }
    var bins = [TranslationBin: [CGPoint]]()
    for translation in translations {
      let bin = TranslationBin(
        x: Int((translation.x / binSize).rounded(.down)),
        y: Int((translation.y / binSize).rounded(.down))
      )
      bins[bin, default: []].append(translation)
    }
    for seed in translations {
      let center = TranslationBin(
        x: Int((seed.x / binSize).rounded(.down)),
        y: Int((seed.y / binSize).rounded(.down))
      )
      var support = 0
      for y in (center.y - 1)...(center.y + 1) {
        for x in (center.x - 1)...(center.x + 1) {
          for candidate in bins[TranslationBin(x: x, y: y), default: []]
          where distance(candidate, seed) <= consensusRadius {
            support += 1
            if support >= 6 {
              lastSelfPlacementWasAmbiguous = true
              ambiguityByReferenceID[referenceID] = AmbiguityCacheEntry(
                fingerprint: fingerprint,
                ambiguous: true
              )
              return true
            }
          }
        }
      }
    }
    ambiguityByReferenceID[referenceID] = AmbiguityCacheEntry(
      fingerprint: fingerprint,
      ambiguous: false
    )
    return false
  }

  private func sample(_ image: CGImage) -> Grid? {
    guard image.width > 0, image.height > 0 else { return nil }
    let height = max(1, Int((CGFloat(image.height) * CGFloat(sampleWidth) / CGFloat(image.width)).rounded()))
    var rgba = [UInt8](repeating: 0, count: sampleWidth * height * 4)
    guard let context = CGContext(
      data: &rgba, width: sampleWidth, height: height, bitsPerComponent: 8,
      bytesPerRow: sampleWidth * 4, space: CGColorSpaceCreateDeviceRGB(),
      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
    else { return nil }
    context.draw(image, in: CGRect(x: 0, y: 0, width: sampleWidth, height: height))
    var luma = [UInt8](repeating: 0, count: sampleWidth * height)
    for index in luma.indices {
      let red = Int(rgba[index * 4]) * 54
      let green = Int(rgba[index * 4 + 1]) * 183
      let blue = Int(rgba[index * 4 + 2]) * 19
      luma[index] = UInt8((red + green + blue) / 256)
    }
    return Grid(pixels: luma, width: sampleWidth, height: height)
  }

  private func corners(in grid: Grid, allowed: (CGPoint) -> Bool) -> [Corner] {
    guard grid.width >= 7, grid.height >= 7 else { return [] }
    var all = [Corner]()
    for y in 3..<(grid.height - 3) {
      for x in 3..<(grid.width - 3) {
        let point = CGPoint(x: x, y: y)
        guard allowed(point) else { continue }
        let strength = abs(value(grid, x + 1, y) - value(grid, x - 1, y))
          + abs(value(grid, x, y + 1) - value(grid, x, y - 1))
        guard strength >= 55 else { continue }
        var descriptor = [UInt8]()
        for dy in -2...2 { for dx in -2...2 { descriptor.append(UInt8(value(grid, x + dx, y + dy))) } }
        all.append(Corner(point: point, descriptor: descriptor, strength: strength))
      }
    }
    var selected = [Corner]()
    for corner in all.sorted(by: { $0.strength == $1.strength ? ($0.point.y, $0.point.x) < ($1.point.y, $1.point.x) : $0.strength > $1.strength }) {
      if selected.allSatisfy({ distance($0.point, corner.point) >= 7 }) {
        selected.append(corner)
        if selected.count == maximumFeatures { break }
      }
    }
    return selected
  }

  private func value(_ grid: Grid, _ x: Int, _ y: Int) -> Int { Int(grid.pixels[y * grid.width + x]) }
  private func descriptorDistance(_ a: [UInt8], _ b: [UInt8]) -> Int { zip(a, b).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) } }
  private func appearanceSignature(_ descriptors: [[UInt8]]) -> [Int] {
    var bins = [Int](repeating: 0, count: 16)
    for descriptor in descriptors where descriptor.count == 25 {
      for value in descriptor { bins[min(15, Int(value) / 16)] += 1 }
    }
    let total = max(1, bins.reduce(0, +))
    return bins.map { $0 * 4_096 / total }
  }
  private func signatureDistance(_ first: [Int], _ second: [Int]) -> Int {
    zip(first, second).reduce(0) { $0 + abs($1.0 - $1.1) }
  }
  private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat { hypot(a.x - b.x, a.y - b.y) }
  private func average(_ points: [CGPoint]) -> CGPoint {
    let total = points.reduce(CGPoint.zero) { CGPoint(x: $0.x + $1.x, y: $0.y + $1.y) }
    return CGPoint(x: total.x / CGFloat(points.count), y: total.y / CGFloat(points.count))
  }
  private func spatiallySpread(_ points: [CGPoint]) -> Bool {
    guard let first = points.first else { return false }
    let xSpan = (points.map(\.x).max() ?? first.x) - (points.map(\.x).min() ?? first.x)
    let ySpan = (points.map(\.y).max() ?? first.y) - (points.map(\.y).min() ?? first.y)
    let occupiedCells = Set(points.map { "\(Int($0.x / 40)): \(Int($0.y / 30))" })
    return xSpan >= 30 && ySpan >= 18 && occupiedCells.count >= 3
  }
  private static func validRect(_ rect: CGRect) -> Bool {
    rect.origin.x.isFinite && rect.origin.y.isFinite
      && rect.width.isFinite && rect.height.isFinite
      && rect.width >= 0 && rect.height >= 0
  }
}
