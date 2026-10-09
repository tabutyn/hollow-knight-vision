import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class GlobalFeatureMatcherTests: XCTestCase {
  func testRecoversLargeHorizontalAndVerticalOffsetWithoutPosePrior() throws {
    let matcher = GlobalFeatureMatcher()
    let id = UUID()
    let reference = try XCTUnwrap(matcher.makeReference(
      id: id, frame: texture(), cameraPose: CGPoint(x: 500, y: 900), solveWidth: 320))
    // 72 by -36 source pixels is far beyond the local revisit's 12 pixel radius.
    let match = matcher.match(frame: texture(offset: CGPoint(x: 72, y: -36)), references: [reference])
    XCTAssertEqual(match?.referenceID, id)
    XCTAssertEqual(match?.proposedCameraPose.x ?? 0, 428, accuracy: 5)
    XCTAssertEqual(match?.proposedCameraPose.y ?? 0, 864, accuracy: 5)
    XCTAssertGreaterThanOrEqual(match?.inlierReferenceFeatureIDs.count ?? 0, 6)
    XCTAssertLessThan(match?.ambiguityRatio ?? 1, 0.8)
  }

  func testRepeatPatternIsRejectedAsAmbiguous() throws {
    let matcher = GlobalFeatureMatcher()
    let reference = try XCTUnwrap(matcher.makeReference(frame: tiledTexture(), cameraPose: .zero, solveWidth: 320))
    XCTAssertNil(matcher.match(frame: tiledTexture(offset: CGPoint(x: 32, y: 0)), references: [reference]))
  }

  func testHUDExclusionCannotProvideMatch() throws {
    let matcher = GlobalFeatureMatcher()
    let reference = try XCTUnwrap(matcher.makeReference(frame: hudOnly(world: false), cameraPose: .zero, solveWidth: 320))
    XCTAssertNil(matcher.makeReference(
      frame: hudOnly(world: false), cameraPose: .zero, solveWidth: 320,
      excluding: [CGRect(x: 0, y: 128, width: 320, height: 52)]))
    XCTAssertNil(matcher.match(
      frame: hudOnly(world: true), references: [reference],
      excluding: [CGRect(x: 0, y: 128, width: 320, height: 52)]))
  }

  func testUnrelatedFrameIsRejected() throws {
    let matcher = GlobalFeatureMatcher()
    let reference = try XCTUnwrap(matcher.makeReference(frame: texture(), cameraPose: .zero, solveWidth: 320))
    XCTAssertNil(matcher.match(frame: unrelatedTexture(), references: [reference]))
  }

  func testReferenceOrderingIsDeterministic() throws {
    let matcher = GlobalFeatureMatcher()
    let a = try XCTUnwrap(matcher.makeReference(id: UUID(uuidString: "00000000-0000-0000-0000-000000000001")!, frame: texture(), cameraPose: CGPoint(x: 100, y: 100), solveWidth: 320))
    let b = try XCTUnwrap(matcher.makeReference(id: UUID(uuidString: "00000000-0000-0000-0000-000000000002")!, frame: texture(), cameraPose: CGPoint(x: 700, y: 700), solveWidth: 320))
    let first = matcher.match(frame: texture(offset: CGPoint(x: 40, y: 20)), references: [b, a])
    let second = matcher.match(frame: texture(offset: CGPoint(x: 40, y: 20)), references: [a, b])
    XCTAssertEqual(first, second)
    XCTAssertNil(first, "Identical placements from separate references must remain ambiguous.")
  }

  func testOverlappingReferencesThatAgreeOnWorldPoseReinforceMatch() throws {
    let matcher = GlobalFeatureMatcher()
    let frame = texture(offset: CGPoint(x: 40, y: 20))
    let a = try XCTUnwrap(matcher.makeReference(
      id: UUID(uuidString: "00000000-0000-0000-0000-000000000001")!,
      frame: texture(), cameraPose: CGPoint(x: 100, y: 100), solveWidth: 320))
    var b = a
    b.id = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!
    b.cameraPose = CGPoint(x: 102, y: 101)

    let match = matcher.match(frame: frame, references: [a, b], currentSolveWidth: 320)

    XCTAssertNotNil(match)
    XCTAssertEqual(match?.proposedCameraPose.x ?? 0, 60, accuracy: 5)
    XCTAssertEqual(match?.proposedCameraPose.y ?? 0, 120, accuracy: 5)
  }

  func testSpatiallyDiverseShortlistFindsTargetBeyondSignatureCap() throws {
    let matcher = GlobalFeatureMatcher()
    let targetID = UUID(uuidString: "00000000-0000-0000-0000-0000000000ff")!
    let target = try XCTUnwrap(matcher.makeReference(
      id: targetID, frame: texture(), cameraPose: CGPoint(x: 4_000, y: 4_000), solveWidth: 320))
    let distractors: [GlobalFeatureReference] = (1...32).map { index in
      var reference = target
      reference.id = UUID(uuidString: String(format: "00000000-0000-0000-0000-%012x", index))!
      reference.cameraPose = .zero
      reference.features = reference.features.map { feature in
        var changed = feature
        changed.descriptor.reverse()
        return changed
      }
      return reference
    }
    let frame = texture(offset: CGPoint(x: 24, y: -12))
    let first = matcher.match(frame: frame, references: distractors + [target])
    XCTAssertEqual(first?.referenceID, targetID)
    // The confirmation scan advances the normal rotation, but reserves the
    // accepted reference so a two-scan closure can still confirm it.
    let confirmation = matcher.match(
      frame: frame, references: distractors + [target], preferredReferenceID: targetID)
    XCTAssertEqual(confirmation?.referenceID, targetID)
  }

  func testSelfPlacementCheckUsesBoundedDescriptorCandidates() throws {
    let matcher = GlobalFeatureMatcher()
    let reference = try XCTUnwrap(matcher.makeReference(
      frame: texture(), cameraPose: .zero, solveWidth: 320))
    _ = matcher.match(frame: texture(offset: CGPoint(x: 18, y: 9)), references: [reference])
    let count = reference.features.count
    XCTAssertGreaterThan(matcher.lastSelfPlacementPairChecks, 0)
    XCTAssertLessThanOrEqual(matcher.lastSelfPlacementPairChecks, count * (count - 1))
  }

  func testSelfPlacementRejectsConsensusSplitAcrossHistogramBoundary() throws {
    let matcher = GlobalFeatureMatcher()
    var reference = try XCTUnwrap(matcher.makeReference(
      frame: texture(), cameraPose: .zero, solveWidth: 320))
    reference.features = (0..<6).flatMap { index -> [GlobalFeatureReference.Feature] in
      let descriptor = [UInt8](repeating: UInt8(index * 50), count: 25)
      let source = CGPoint(x: 10 + index * 10, y: 30 + index * 8)
      let repeated = CGPoint(x: source.x + 20 + CGFloat(index), y: source.y)
      return [
        .init(id: index * 2, samplePoint: source, descriptor: descriptor, strength: 100,
              depth: 1, depthConfidence: 0),
        .init(id: index * 2 + 1, samplePoint: repeated, descriptor: descriptor, strength: 100,
              depth: 1, depthConfidence: 0),
      ]
    }
    _ = matcher.match(frame: texture(), references: [reference])
    XCTAssertTrue(matcher.lastSelfPlacementWasAmbiguous)
  }

  func testCurrentSolveWidthUsesCurrentFrameScaleForCamera() throws {
    let reference = try XCTUnwrap(GlobalFeatureMatcher().makeReference(
      frame: texture(), cameraPose: CGPoint(x: 500, y: 900), solveWidth: 320))
    let frame = texture(offset: CGPoint(x: 72, y: -36))
    let referenceScaleMatch = GlobalFeatureMatcher().match(frame: frame, references: [reference])
    let currentScaleMatch = GlobalFeatureMatcher().match(
      frame: frame, references: [reference], currentSolveWidth: 160)
    let referenceScale = try XCTUnwrap(referenceScaleMatch)
    let currentScale = try XCTUnwrap(currentScaleMatch)
    XCTAssertGreaterThan(currentScale.proposedCameraPose.x, referenceScale.proposedCameraPose.x + 80)
    XCTAssertGreaterThan(currentScale.proposedCameraPose.y, referenceScale.proposedCameraPose.y + 20)
  }

  private func texture(offset: CGPoint = .zero) -> CGImage {
    image { x, y in
      let sx = x - Int(offset.x), sy = y - Int(offset.y)
      let value = (sx * 17 + sy * 31 + (sx * sy) % 89) & 255
      return (value, 255 - value, value / 3, 255)
    }
  }
  private func unrelatedTexture() -> CGImage {
    image { x, y in
      let value = ((x * 7) ^ (y * 53) ^ ((x + y) * 19)) & 255
      return (255 - value, value / 5, value, 255)
    }
  }
  private func tiledTexture(offset: CGPoint = .zero) -> CGImage {
    image { x, y in
      let sx = ((x - Int(offset.x)) % 32 + 32) % 32
      let sy = ((y - Int(offset.y)) % 32 + 32) % 32
      let value = ((sx * 23 + sy * 11 + sx * sy) & 255)
      return (value, 255 - value, value / 2, 255)
    }
  }
  private func hudOnly(world: Bool) -> CGImage {
    image { x, y in
      if y < 52 {
        let value = ((x * 17 + y * 29 + x * y) & 255)
        return (value, 255 - value, value / 3, 255)
      }
      return world ? ((x * 3 + y * 5) & 63, 0, 0, 255) : (0, (x * 7 + y * 11) & 63, 0, 255)
    }
  }
  private func image(_ color: (Int, Int) -> (Int, Int, Int, Int)) -> CGImage {
    let width = 320, height = 180
    var bytes = [UInt8](repeating: 0, count: width * height * 4)
    for y in 0..<height { for x in 0..<width {
      let i = (y * width + x) * 4; let c = color(x, y)
      bytes[i] = UInt8(c.0); bytes[i + 1] = UInt8(c.1); bytes[i + 2] = UInt8(c.2); bytes[i + 3] = UInt8(c.3)
    } }
    return CGContext(data: &bytes, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!.makeImage()!
  }
}
