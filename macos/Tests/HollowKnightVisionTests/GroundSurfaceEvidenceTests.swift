import XCTest
@testable import HollowKnightVision

final class GroundSurfaceEvidenceTests: XCTestCase {
    func testStrongCoreKeepsWeakEndpointsButWeakOnlyLineIsRejected() {
        let width = 200, height = 100
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 20...119 { pixels[20 * width + x] = 8 }
        for x in 45...94 { pixels[20 * width + x] = 9 }
        for x in 20...119 { pixels[70 * width + x] = 8 }
        let analysis = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: pixels, sourceLuma: [UInt8](repeating: 20, count: width * height))
        XCTAssertEqual(GroundLineDetector.semanticFloorLines(from: analysis,
            tuning: .semanticDefault),
            [CleanFloorLine(row: 20, xRange: 20...119)])
    }

    func testSplitFragmentCannotBorrowBodySupportAcrossStep() {
        let width = 200, height = 100
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 0...59 { pixels[20 * width + x] = 180 }
        for x in 140...199 { pixels[20 * width + x] = 180 }
        for x in 60...139 { pixels[60 * width + x] = 180 }
        var luma = [UInt8](repeating: 20, count: width * height)
        for row in 21...32 {
            for x in 0...59 { luma[row * width + x] = 100 }
        }
        let analysis = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: pixels, sourceLuma: luma)
        let lines = GroundLineDetector.semanticFloorLines(from: analysis,
            tuning: .semanticDefault)
        XCTAssertEqual(lines, [CleanFloorLine(row: 20, xRange: 140...199),
                               CleanFloorLine(row: 60, xRange: 60...139)])
    }

    func testVisibleBottomBandRejectsDecorationAndKeepsClippedFloor() {
        let width = 100, height = 60, row = 54
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 0..<width { pixels[row * width + x] = 180 }
        let bright = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: pixels, sourceLuma: [UInt8](repeating: 120, count: width * height))
        XCTAssertTrue(GroundLineDetector.semanticFloorLines(from: bright,
            tuning: .semanticDefault).isEmpty)
        let dark = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: pixels, sourceLuma: [UInt8](repeating: 20, count: width * height))
        XCTAssertEqual(GroundLineDetector.semanticFloorLines(from: dark,
            tuning: .semanticDefault),
            [CleanFloorLine(row: row, xRange: 0...99)])
    }

    func testRejectedDecorationCannotSuppressNearbyRealFloor() {
        let width = 200, height = 80
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 10...189 { pixels[20 * width + x] = 180 }
        for x in 60...139 { pixels[40 * width + x] = 150 }
        var luma = [UInt8](repeating: 120, count: width * height)
        for row in 41...55 {
            for x in 60...139 { luma[row * width + x] = 20 }
        }
        let analysis = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: pixels, sourceLuma: luma)
        XCTAssertEqual(GroundLineDetector.semanticFloorLines(from: analysis,
            tuning: .semanticDefault),
            [CleanFloorLine(row: 40, xRange: 60...139)])
    }

    func testEvidenceScoringDoesNotLetDarkGapApproveBrightEdge() {
        let width = 200, height = 80
        var luma = [UInt8](repeating: 0, count: width * height)
        for row in 21...32 {
            for x in 0...39 { luma[row * width + x] = 120 }
            for x in 160...199 { luma[row * width + x] = 120 }
        }
        let surface = GroundSurfaceEvidence(GroundComparisonAnalysis(width: width, height: height,
            groundPixels: [], sourceLuma: luma))
        XCTAssertTrue(surface.supports(row: 20, ranges: [0...199]))
        XCTAssertFalse(surface.supports(row: 20, ranges: [0...39, 160...199]))
    }

    func testMaskedPixelsDoNotChangeSupportedBodyVote() {
        let width = 100, height = 60
        var luma = [UInt8](repeating: 20, count: width * height)
        var valid = [UInt8](repeating: 1, count: width * height)
        for row in 21...32 {
            for x in 0...60 {
                luma[row * width + x] = 255
                valid[row * width + x] = 0
            }
        }
        let surface = GroundSurfaceEvidence(GroundComparisonAnalysis(width: width, height: height,
            groundPixels: [], sourceLuma: luma, validSourcePixels: valid))
        XCTAssertTrue(surface.supports(row: 20, ranges: [0...99]))
    }
}
