import XCTest
@testable import HollowKnightVision

final class GroundSurfaceEvidenceTests: XCTestCase {
    func testClearanceRejectsOnlyFullyEnclosedShorterFloorsWithinKnightHeight() {
        let outer = CleanFloorLine(row: 100, xRange: 10...119)
        let above = CleanFloorLine(row: 81, xRange: 10...40)
        let below = CleanFloorLine(row: 119, xRange: 60...119)
        let headroom = CleanFloorLine(row: 120, xRange: 10...40)
        let offset = CleanFloorLine(row: 98, xRange: 120...170)
        let knight = CGRect(x: 0, y: 0, width: 10, height: 20)
        XCTAssertEqual(GroundClearance.floors([above, outer, below, headroom, offset], imageHeight: 360,
            knight: knight), [outer, headroom, offset])
        let bridge = CleanFloorLine(row: 100, xRange: 0...120, evidenceRanges: [0...20, 100...120])
        let ledge = CleanFloorLine(row: 112, xRange: 40...75)
        XCTAssertEqual(GroundClearance.floors([bridge, ledge], imageHeight: 360, knight: nil), [bridge, ledge])
    }

    func testTipsMeetWithoutDeletingEitherPlatformOrKeepingEvidenceBeyondTheCut() {
        let a = CleanFloorLine(row: 80, xRange: 0...99)
        let b = CleanFloorLine(row: 90, xRange: 90...189)
        let cut = GroundClearance.floors([a, b], imageHeight: 360, knight: nil)
        XCTAssertEqual(cut, [.init(row: 80, xRange: 0...94), .init(row: 90, xRange: 95...189)])
        XCTAssertEqual(GroundClearance.floors([b, a], imageHeight: 360, knight: nil), cut.reversed())
        let c = CleanFloorLine(row: 90, xRange: 50...149)
        XCTAssertEqual(GroundClearance.floors([a, c], imageHeight: 360, knight: nil), [a, c])
        let thinTip = CleanFloorLine(row: 80, xRange: 0...99, evidenceRanges: [0...89, 99...99])
        let supported = GroundClearance.floors([thinTip, b], imageHeight: 360, knight: nil)
        XCTAssertEqual(supported[0].evidenceRanges, [0...89])
        XCTAssertEqual(supported[1], b)
    }

    func testNearbyRidgesWithInterleavedEvidenceProduceOneLine() {
        let width = 350, height = 60
        var response = [UInt8](repeating: 0, count: width * height)
        for (row, spans) in [(20, [10...40, 110...150]), (22, [41...70, 140...169, 290...339])] {
            for span in spans {
                for x in span { response[row * width + x] = 80 }
            }
        }
        let comparison = GroundComparisonAnalysis(width: width, height: height, groundPixels: response,
            sourceLuma: [UInt8](repeating: 20, count: width * height))
        let tuning = GroundTheoryTuning(groundThreshold: 8, minimumSegmentLength: 40,
            lineSeparation: 40, occlusionMergeGap: 90)
        XCTAssertEqual(GroundLineDetector.semanticFloorLines(from: comparison, tuning: tuning), [
            CleanFloorLine(row: 20, xRange: 10...150, evidenceRanges: [10...40, 110...150]),
            CleanFloorLine(row: 22, xRange: 290...339)
        ])
    }

    func testCaptionStrokesCannotExtendAnAdjacentPlatform() {
        let width = 240, height = 64, row = 22
        var response = [UInt8](repeating: 0, count: width * height)
        var luma = [UInt8](repeating: 20, count: width * height)
        for x in 12...85 { response[row * width + x] = 100 }
        for x in 140...203 { response[row * width + x] = 100 }
        for x in stride(from: 140, through: 203, by: 5) {
            for y in 21...27 { luma[y * width + x] = 210 }
        }
        let comparison = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: response, sourceLuma: luma)
        let semantic = GroundLineDetector.semanticFloorLines(from: comparison, tuning: .semanticDefault)
        XCTAssertEqual(semantic, [CleanFloorLine(row: row, xRange: 12...85)])
        let tracking = GroundLineDetector.cleanFloorLines(from: comparison, tuning: .default)
        XCTAssertEqual(tracking.first?.xRange, 12...203)
    }

    func testBrightOpeningSeparatesTwoPlatformsWithLitCaps() {
        let width = 320, height = 80, row = 24
        var response = [UInt8](repeating: 0, count: width * height)
        var luma = [UInt8](repeating: 140, count: width * height)
        for bank in [20...75, 216...271] {
            for x in bank {
                response[row * width + x] = 40
                for y in (row + 1)..<height {
                    luma[y * width + x] = y <= row + 12 ? 85 : 20
                }
            }
        }
        let comparison = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: response, sourceLuma: luma)
        let lines = GroundLineDetector.semanticFloorLines(from: comparison, tuning: .semanticDefault)
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines.contains { $0.row == row && $0.xRange.contains(45) })
        XCTAssertTrue(lines.contains { $0.row == row && $0.xRange.contains(245) })
        XCTAssertFalse(lines.contains { $0.xRange.contains(145) })

        var valid = [UInt8](repeating: 1, count: width * height)
        for y in (row + 13)...(row + 24) {
            for x in 216...271 { valid[y * width + x] = 0 }
        }
        let masked = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: response, sourceLuma: luma, validSourcePixels: valid)
        XCTAssertFalse(GroundLineDetector.semanticFloorLines(from: masked, tuning: .semanticDefault)
            .contains { $0.xRange.contains(245) })
    }

    func testLocalKernelTrimsOverhangAndRecoversAnUnderestimatedEndpoint() throws {
        let width = 150, height = 60, row = 20
        var response = [UInt8](repeating: 0, count: width * height)
        var luma = [UInt8](repeating: 125, count: width * height)
        for x in 24...88 { response[row * width + x] = 50 }
        for y in (row + 1)..<height {
            for x in 35...100 { luma[y * width + x] = 12 }
        }
        let comparison = GroundComparisonAnalysis(width: width, height: height,
            groundPixels: response, sourceLuma: luma)
        let line = try XCTUnwrap(GroundLineDetector.semanticFloorLines(from: comparison, tuning: .semanticDefault).first)
        XCTAssertLessThanOrEqual(abs(line.xRange.lowerBound - 35), 3)
        XCTAssertLessThanOrEqual(abs(line.xRange.upperBound - 100), 3)
        XCTAssertEqual(line.evidenceRanges, [line.xRange])
    }

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
