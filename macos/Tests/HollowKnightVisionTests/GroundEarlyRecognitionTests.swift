import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundEarlyRecognitionTests: XCTestCase {
    private func comparison(includeLowerFloor: Bool) -> GroundComparisonAnalysis {
        var pixels = [UInt8](repeating: 0, count: 320 * 140)
        func line(_ row: Int, _ range: ClosedRange<Int>) {
            for x in range { pixels[row * 320 + x] = 80 }
        }
        line(40, 0...111); line(40, 208...319)
        line(4, 260...289) // Short foreground decoration, not a floor.
        line(67, 20...79) // Weaker parallel edge within the ground body.
        if includeLowerFloor { line(85, 128...191) }
        return GroundComparisonAnalysis(width: 320, height: 140, groundPixels: pixels)
    }

    func testLowerSurfaceSplitsBridgeAndRejectsDecorativeEdges() {
        let lines = GroundLineDetector.cleanFloorLines(
            from: comparison(includeLowerFloor: true), tuning: .default)
        XCTAssertEqual(lines.map(\.row), [40,40,85])
        XCTAssertEqual(lines.filter { $0.row == 40 }.map(\.xRange), [0...111,208...319])
    }

    func testOrdinaryOcclusionStillBridgesWithoutLowerSurfaceEvidence() {
        let lines = GroundLineDetector.cleanFloorLines(
            from: comparison(includeLowerFloor: false), tuning: .default)
        XCTAssertEqual(lines.map(\.row), [40])
        XCTAssertEqual(lines.first?.xRange, 0...319)
    }

    func testShortObservationAdmitsNewGroundAndPreservesItAcrossOcclusion() throws {
        let ledger = GroundLinePresence(minimumObservationSeconds: 0.2)
        let line = CleanFloorLine(row: 40, xRange: 16...143)
        for index in 0...12 {
            ledger.update(lines: [line], camera: .zero, width: 160, height: 100,
                timestamp: Double(index)/60, poseVerified: true, exclusions: [])
        }
        let confirmed = try XCTUnwrap(ledger.reviews(camera: .zero, width: 160, height: 100).first)
        XCTAssertEqual(confirmed.state, .confirmed)
        for index in 13...120 {
            ledger.update(lines: [], camera: .zero, width: 160, height: 100,
                timestamp: Double(index)/60, poseVerified: true,
                exclusions: [CGRect(x: 0,y: 30,width: 160,height: 40)])
        }
        let retained = try XCTUnwrap(ledger.reviews(camera: .zero, width: 160, height: 100).first)
        XCTAssertEqual(retained.id, confirmed.id)
        XCTAssertEqual(retained.state, .confirmed)
        XCTAssertEqual(retained.detectedFraction, 1)
    }

    func testConfirmedGroundSurvivesBriefMissButRejectsPersistentAbsence() throws {
        let ledger = GroundLinePresence(minimumObservationSeconds: 0.2,
            confirmedLossGraceSeconds: 3)
        let line = CleanFloorLine(row: 40, xRange: 16...143)
        func update(_ index: Int, detected: Bool) {
            ledger.update(lines: detected ? [line] : [], camera: .zero,
                width: 160, height: 100, timestamp: Double(index)/60,
                poseVerified: true, exclusions: [])
        }
        for index in 0...60 { update(index, detected: true) }
        let original = try XCTUnwrap(ledger.reviews(camera: .zero, width: 160, height: 100).first)
        for index in 61...220 { update(index, detected: false) }
        let hidden = try XCTUnwrap(ledger.reviews(camera: .zero, width: 160, height: 100).first)
        XCTAssertEqual(hidden.state, .confirmed)
        XCTAssertEqual(hidden.id, original.id)
        update(221, detected: true)
        XCTAssertEqual(ledger.reviews(camera: .zero, width: 160, height: 100).first?.state, .confirmed)
        for index in 222...410 { update(index, detected: false) }
        XCTAssertEqual(ledger.reviews(camera: .zero, width: 160, height: 100).first?.state, .rejected)
    }

    func testPersistentUpperEndsDoNotMergeAcrossKnownLowerFloor() throws {
        let width=320, height=140
        let bytes: [UInt8] = (0..<(width*height)).map { index in
            var hash = UInt64((index%width)*73_856_093) ^ UInt64((index/width)*19_349_663)
            hash ^= hash >> 13; hash &*= 1_274_126_177
            return UInt8(truncatingIfNeeded: hash >> 11)
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(bytes) as CFData))
        let image = try XCTUnwrap(CGImage(width: width,height: height,bitsPerComponent: 8,
            bitsPerPixel: 8,bytesPerRow: width,space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: 0),provider: provider,decode: nil,
            shouldInterpolate: false,intent: .defaultIntent))
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let lines = [CleanFloorLine(row: 40,xRange: 0...111),
                     CleanFloorLine(row: 40,xRange: 208...319),
                     CleanFloorLine(row: 85,xRange: 128...191)]
        var result = GroundHypothesisTrackingResult.empty
        for index in 0...8 {
            result = tracker.update(frame: image,floorLines: lines,
                lineSeparation: 36,occlusionMergeGap: 180,timestamp: Double(index)/60)
        }
        XCTAssertEqual(result.groundSegmentCount, 3)
        let ids = Set(result.atlasLines.map(\.segmentID))
        for index in 9...11 {
            // The raw detector bridges the upper ends when the lower edge is
            // masked. Stored topology must still preserve the real opening.
            let bridged = CleanFloorLine(row: 40,xRange: 0...319,
                evidenceRanges: [0...111,208...319])
            result = tracker.update(frame: image,floorLines: [bridged],
                lineSeparation: 36,occlusionMergeGap: 180,timestamp: Double(index)/60)
        }
        XCTAssertEqual(result.groundSegmentCount, 3)
        XCTAssertEqual(Set(result.atlasLines.map(\.segmentID)), ids)
        XCTAssertEqual(result.atlasLines.filter { abs($0.atlasStart.y-100)<1 }.count, 2)
    }
}
