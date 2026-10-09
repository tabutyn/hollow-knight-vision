import CoreGraphics
import CoreImage
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundReferenceTrackerTests: XCTestCase {
    func testKnownForegroundRejectsShortObjectLineButKeepsLongFloor() {
        let shortObjectLine = CleanFloorLine(row: 30, xRange: 40...79)
        let longFloor = CleanFloorLine(row: 30, xRange: 0...199)
        // Bottom-left input converts to top-left y=20...60 in a 100px image.
        let foreground = CGRect(x: 38, y: 40, width: 44, height: 40)

        let filtered = GroundLineDetector.rejectingKnownForegroundLines(
            [shortObjectLine, longFloor],
            imageHeight: 100,
            foregroundRects: [foreground]
        )

        XCTAssertEqual(filtered, [longFloor])
    }

    func testKnownForegroundAtDifferentRowDoesNotRejectLine() {
        let line = CleanFloorLine(row: 80, xRange: 40...79)
        let foreground = CGRect(x: 38, y: 40, width: 44, height: 40)

        XCTAssertEqual(GroundLineDetector.rejectingKnownForegroundLines(
            [line],
            imageHeight: 100,
            foregroundRects: [foreground]
        ), [line])
    }

    func testKnightUpperBodyRejectsLongFalseLineButKeepsFloorAtFeet() {
        let falseUpperLine = CleanFloorLine(row: 35, xRange: 20...180,
                                            evidenceRanges: [85...115])
        let floorAtFeet = CleanFloorLine(row: 76, xRange: 0...199)
        // Bottom-left 35...75 becomes top-left 25...65 before expansion.
        let knight = CGRect(x: 85, y: 25, width: 30, height: 40)

        let filtered = GroundLineDetector.rejectingKnownForegroundLines(
            [falseUpperLine, floorAtFeet],
            imageHeight: 100,
            foregroundRects: [knight],
            knightRect: knight
        )

        XCTAssertEqual(filtered, [floorAtFeet])
    }

    func testSmallKnightDetectionInfersBodyAboveDetectedPatch() {
        let falseUpperLine = CleanFloorLine(row: 306, xRange: 150...400,
                                            evidenceRanges: [290...320])
        let floorAtFeet = CleanFloorLine(row: 350, xRange: 0...639)
        // Weak model output may cover only a 32x14 patch near the Knight's
        // lower body. Ground filtering must still protect full body height.
        let weakKnightPatch = CGRect(x: 287, y: 22, width: 32, height: 14)

        let filtered = GroundLineDetector.rejectingKnownForegroundLines(
            [falseUpperLine, floorAtFeet],
            imageHeight: 360,
            foregroundRects: [weakKnightPatch],
            knightRect: weakKnightPatch
        )

        XCTAssertEqual(filtered, [floorAtFeet])
    }

    func testJitteringKnightBoxCannotEraseRoomWideFloorEvidence() {
        let floor = CleanFloorLine(row: 262, xRange: 16...624,
                                   evidenceRanges: [16...270, 330...624])
        for bottom in 76...92 {
            let knight = CGRect(x: 280, y: bottom, width: 40, height: 26)
            XCTAssertEqual(GroundLineDetector.rejectingKnownForegroundLines(
                [floor], imageHeight: 360, foregroundRects: [knight], knightRect: knight), [floor])
        }
    }

    func testRequestedTuningDefaultsAreLockedIn() {
        XCTAssertEqual(GroundTheoryTuning.default.groundThreshold, 10)
        XCTAssertEqual(GroundTheoryTuning.default.minimumSegmentLength, 48)
        XCTAssertEqual(GroundTheoryTuning.default.lineSeparation, 36)
        XCTAssertEqual(GroundTheoryTuning.default.occlusionMergeGap, 180)
    }

    func testLaunchTuningOverridesAndClampsDeveloperSweepValues() {
        let tuning = GroundTheoryTuning.launchTuning(arguments: [
            "HollowKnightVision",
            "--ground-threshold=20",
            "--ground-minimum-segment=44",
            "--ground-line-separation=-2",
            "--ground-occlusion-gap=180",
        ])
        XCTAssertEqual(tuning.groundThreshold, 20)
        XCTAssertEqual(tuning.minimumSegmentLength, 44)
        XCTAssertEqual(tuning.lineSeparation, 0)
        XCTAssertEqual(tuning.occlusionMergeGap, 180)
    }

    func testGroundEdgeDebugImageContainsFullResolutionOnePixelDifference() throws {
        let image = try groundImage(width: 320, height: 180, groundFrameY: 54)
        let debugImage = try XCTUnwrap(GroundLineDetector.debugImage(in: image))
        XCTAssertEqual(debugImage.width, image.width)
        XCTAssertEqual(debugImage.height, image.height)
        let pixels = try grayscalePixels(debugImage)
        XCTAssertGreaterThan(pixels.max() ?? 0, 100)
        XCTAssertTrue(pixels.contains(0))
    }

    func testGroundDetectKernelMatchesRequestedVerticalPolarity() {
        let width = 40
        let height = 12
        var matchingEdges = [UInt8](repeating: 0, count: width * height)
        for row in 4...7 {
            for x in 0..<width { matchingEdges[row * width + x] = 255 }
        }
        let matching = GroundEdgeAnalysis(
            width: width,
            height: height,
            groundEdgePixels: matchingEdges
        )
        let matchingResult = GroundLineDetector.groundDetectPixels(from: matching)
        XCTAssertEqual(matchingResult[4 * width + 20], 255)

        var oppositeEdges = [UInt8](repeating: 0, count: width * height)
        for row in 0...3 {
            for x in 0..<width { oppositeEdges[row * width + x] = 255 }
        }
        let opposite = GroundEdgeAnalysis(
            width: width,
            height: height,
            groundEdgePixels: oppositeEdges
        )
        let oppositeResult = GroundLineDetector.groundDetectPixels(from: opposite)
        XCTAssertEqual(oppositeResult[4 * width + 20], 0)
    }

    func testGroundStageImageRequiresContiguousMinimumSegment() throws {
        let width = 12
        let height = 2
        var ground = [UInt8](repeating: 0, count: width * height)
        ground[0] = 200
        for x in 0...5 { ground[width + x] = 200 }
        for x in 7...11 { ground[width + x] = 200 }
        let comparison = GroundComparisonAnalysis(
            width: width,
            height: height,
            groundPixels: ground
        )
        let image = try XCTUnwrap(GroundLineDetector.groundStageImage(
            from: comparison,
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 6
            )
        ))
        let rgba = try rgbaPixels(image)
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 0, y: 0), [0, 230, 255, 255])
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 1, y: 1), [70, 255, 120, 255])
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 6, y: 1), [0, 0, 0, 0])
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 8, y: 1), [0, 230, 255, 255])
    }

    func testGroundDetectorDoesNotBridgeThresholdGaps() {
        let width = 12
        var ground = [UInt8](repeating: 0, count: width)
        for x in 0...3 { ground[x] = 255 }
        for x in 5...8 { ground[x] = 255 }
        XCTAssertNil(GroundLineDetector.detect(
            in: GroundComparisonAnalysis(
                width: width,
                height: 1,
                groundPixels: ground
            ),
            searchFrameYRange: 0...1,
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 5
            )
        ))
    }

    func testCleanedImageDrawsAcceptedGroundLine() throws {
        let width = 20
        let height = 20
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 2...17 { ground[5 * width + x] = 255 }
        let image = try XCTUnwrap(GroundLineDetector.cleanedImage(
            from: GroundComparisonAnalysis(
                width: width,
                height: height,
                groundPixels: ground
            ),
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 10
            )
        ))
        let rgba = try rgbaPixels(image)
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 3, y: 5), [70, 255, 120, 255])
        XCTAssertEqual(rgbaPixel(rgba, width: width, x: 9, y: 5), [70, 255, 120, 255])
    }

    func testTheoryDetectorKeepsMultipleLongLevelsAndRejectsShortIsland() throws {
        let width = 200
        let height = 100
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 10...150 { ground[20 * width + x] = 255 }
        for x in 40...180 { ground[60 * width + x] = 230 }
        for x in 10...30 { ground[40 * width + x] = 255 }
        let detection = try XCTUnwrap(GroundLineDetector.detect(
            in: GroundComparisonAnalysis(
                width: width,
                height: height,
                groundPixels: ground
            ),
            searchFrameYRange: 20...90,
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 40
            )
        ))
        XCTAssertEqual(detection.segments.count, 2)
        XCTAssertEqual(Set(detection.segments.map { Int($0.frameY.rounded()) }), [40, 80])
    }

    func testLineSeparationKeepsLongerOverlappingLineButPreservesOtherColumns() {
        let width = 220
        let height = 40
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 10...190 { ground[10 * width + x] = 220 }
        for x in 40...130 { ground[14 * width + x] = 255 }
        for x in 195...219 { ground[13 * width + x] = 255 }

        let lines = GroundLineDetector.cleanFloorLines(
            from: GroundComparisonAnalysis(
                width: width,
                height: height,
                groundPixels: ground
            ),
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 20,
                lineSeparation: 6,
                occlusionMergeGap: 0
            )
        )

        XCTAssertTrue(lines.contains { $0.row == 10 && $0.xRange == 10...190 })
        XCTAssertFalse(lines.contains { $0.row == 14 })
        XCTAssertTrue(lines.contains { $0.row == 13 && $0.xRange == 195...219 })
    }

    func testOcclusionBridgeDoesNotSuppressIndependentPlatformInsideGap() {
        let width = 220
        let height = 30
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 0...39 { ground[10 * width + x] = 220 }
        for x in 160...199 { ground[10 * width + x] = 220 }
        for x in 40...159 { ground[14 * width + x] = 255 }

        let lines = GroundLineDetector.cleanFloorLines(
            from: GroundComparisonAnalysis(
                width: width,
                height: height,
                groundPixels: ground
            ),
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 30,
                lineSeparation: 6,
                occlusionMergeGap: 120
            )
        )

        XCTAssertTrue(lines.contains {
            $0.row == 10
                && $0.xRange == 0...199
                && $0.evidenceRanges == [0...39, 160...199]
        })
        XCTAssertTrue(lines.contains { $0.row == 14 && $0.xRange == 40...159 })
    }

    func testDominantFloorSuppressesShortForegroundTopInsideOcclusion() {
        let width = 320
        let height = 80
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 16...110 { ground[40 * width + x] = 220 }
        for x in 160...300 { ground[40 * width + x] = 220 }
        for x in 112...150 { ground[24 * width + x] = 255 }

        let lines = GroundLineDetector.cleanFloorLines(
            from: GroundComparisonAnalysis(
                width: width,
                height: height,
                groundPixels: ground
            ),
            tuning: GroundTheoryTuning(
                groundThreshold: 128,
                minimumSegmentLength: 30,
                lineSeparation: 40,
                occlusionMergeGap: 300
            )
        )

        XCTAssertTrue(lines.contains {
            $0.row == 40 && $0.xRange == 16...300
                && $0.evidenceRanges == [16...110, 160...300]
        })
        XCTAssertFalse(lines.contains { $0.row == 24 })
    }

    func testSparseLongBridgeCannotOutrankSolidFloor() {
        let width = 320, height = 80
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 20...180 { pixels[30 * width + x] = 180 }
        for x in 20...39 { pixels[35 * width + x] = 200 }
        for x in 280...299 { pixels[35 * width + x] = 200 }
        let lines = GroundLineDetector.cleanFloorLines(from:
            GroundComparisonAnalysis(width: width, height: height, groundPixels: pixels),
            tuning: GroundTheoryTuning(groundThreshold: 60, minimumSegmentLength: 30,
                lineSeparation: 40, occlusionMergeGap: 300))
        XCTAssertTrue(lines.contains { $0.row == 30 })
        XCTAssertFalse(lines.contains { $0.row == 35 })
    }

    func testEmptyGapDoesNotCountTowardMinimumEvidenceLength() {
        let width = 320, height = 80
        var pixels = [UInt8](repeating: 0, count: width * height)
        for x in 20...23 { pixels[30 * width + x] = 255 }
        for x in 280...283 { pixels[30 * width + x] = 255 }
        let lines = GroundLineDetector.cleanFloorLines(from:
            GroundComparisonAnalysis(width: width, height: height, groundPixels: pixels),
            tuning: GroundTheoryTuning(groundThreshold: 60, minimumSegmentLength: 30,
                lineSeparation: 40, occlusionMergeGap: 300))
        XCTAssertTrue(lines.isEmpty)
    }

    func testCapturePaddingCannotSetKernelMaximumOrBecomeFloor() {
        let width = 100, height = 80
        var edges = [UInt8](repeating: 0, count: width * height)
        for x in 0..<width {
            for row in 30...33 { edges[row * width + x] = 30 }
            edges[71 * width + x] = 255
        }
        let bounded = GroundEdgeAnalysis(width: width, height: height,
            groundEdgePixels: edges, validRows: 0..<72)
        let values = GroundLineDetector.groundDetectPixels(from: bounded)
        XCTAssertGreaterThan(values[30 * width + 50], 0)
        XCTAssertTrue(values[(68 * width)...].allSatisfy { $0 == 0 })
    }

    func testOcclusionMergeGapJoinsDistantEvidenceIntoOneGroundLine() {
        let width = 280
        let height = 30
        var ground = [UInt8](repeating: 0, count: width * height)
        for x in 10...60 { ground[12 * width + x] = 255 }
        for x in 201...260 { ground[12 * width + x] = 255 }
        let comparison = GroundComparisonAnalysis(
            width: width,
            height: height,
            groundPixels: ground
        )
        let base = GroundTheoryTuning(
            groundThreshold: 128,
            minimumSegmentLength: 100,
            lineSeparation: 0,
            occlusionMergeGap: 139
        )
        XCTAssertTrue(GroundLineDetector.cleanFloorLines(
            from: comparison,
            tuning: base
        ).isEmpty)

        var joining = base
        joining.occlusionMergeGap = 140
        let joined = GroundLineDetector.cleanFloorLines(from: comparison, tuning: joining)
        XCTAssertEqual(joined.count, 1)
        XCTAssertEqual(joined.first?.row, 12)
        XCTAssertEqual(joined.first?.xRange, 10...260)
        XCTAssertEqual(joined.first?.evidenceRanges, [10...60, 201...260])
    }

    func testDefaultDetectorBridgesKnownObjectOcclusion() throws {
        let image = try groundImage(
            width: 320,
            height: 180,
            groundFrameY: 54,
            occludedXRange: 120..<200
        )
        let detection = try XCTUnwrap(GroundLineDetector.detect(
            in: image,
            searchFrameYRange: 30...80,
            excluding: [CGRect(x: 120, y: 0, width: 80, height: 180)]
        ))
        XCTAssertEqual(detection.frameY, 54, accuracy: 3)
        XCTAssertTrue(detection.segments.contains {
            $0.minimumFrameX < 120 && $0.maximumFrameX > 200
        })
    }

    func testTrackerAcquiresFromStableImageEvidenceAndFollowsVerticalCamera() throws {
        let tracker = GroundReferenceTracker()
        let first = try groundImage(width: 320, height: 180, groundFrameY: 54)
        var result: GroundReferenceEstimate?
        for _ in 0..<GroundReferenceTracker.minimumStableObservations {
            result = tracker.observe(
                frame: first,
                cameraPosition: .zero,
                solveWidth: 320,
                knightRect: CGRect(x: 145, y: 55, width: 30, height: 50),
                excluding: [CGRect(x: 145, y: 55, width: 30, height: 50)]
            )
        }
        XCTAssertEqual(result?.worldY ?? 0, 54, accuracy: 3)
        XCTAssertGreaterThanOrEqual(result?.supportingObservationCount ?? 0, 8)

        let cameraMoved = try groundImage(width: 320, height: 180, groundFrameY: 34)
        for _ in 0..<3 {
            result = tracker.observe(
                frame: cameraMoved,
                cameraPosition: CGPoint(x: 20, y: 20),
                solveWidth: 320,
                knightRect: CGRect(x: 145, y: 35, width: 30, height: 50),
                excluding: [CGRect(x: 145, y: 35, width: 30, height: 50)]
            )
        }
        XCTAssertEqual(result?.worldY ?? 0, 54, accuracy: 3)
        XCTAssertGreaterThan(result?.minimumWorldX ?? 0, 15)
    }

    func testTrackerSwitchesToSustainedHigherGroundAndAcceptsWorldCorrection() throws {
        let tracker = GroundReferenceTracker()
        let low = try groundImage(width: 320, height: 180, groundFrameY: 45)
        for _ in 0..<GroundReferenceTracker.minimumStableObservations {
            _ = tracker.observe(
                frame: low,
                cameraPosition: .zero,
                solveWidth: 320,
                knightRect: CGRect(x: 145, y: 46, width: 30, height: 50),
                excluding: []
            )
        }

        let high = try groundImage(width: 320, height: 180, groundFrameY: 105)
        var result: GroundReferenceEstimate?
        for _ in 0..<GroundReferenceTracker.minimumStableObservations {
            result = tracker.observe(
                frame: high,
                cameraPosition: .zero,
                solveWidth: 320,
                knightRect: CGRect(x: 145, y: 106, width: 30, height: 50),
                excluding: []
            )
        }
        XCTAssertEqual(result?.worldY ?? 0, 105, accuracy: 3)

        let corrected = tracker.applyWorldCorrection(CGVector(dx: 20, dy: -7))
        XCTAssertEqual(corrected?.worldY ?? 0, 98, accuracy: 3)
        XCTAssertTrue(corrected?.notchWorldXs.allSatisfy { $0 >= 20 } ?? false)
    }

    private func groundImage(
        width: Int,
        height: Int,
        groundFrameY: Int,
        occludedXRange: Range<Int>? = nil
    ) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for topY in 0..<height {
            let frameY = height - 1 - topY
            for x in 0..<width {
                let occluded = occludedXRange?.contains(x) == true
                let value: UInt8
                if occluded {
                    value = 0
                } else if frameY >= groundFrameY {
                    value = 185
                } else {
                    value = 25
                }
                let offset = (topY * width + x) * 4
                pixels[offset] = value
                pixels[offset + 1] = value
                pixels[offset + 2] = value
                pixels[offset + 3] = 255
            }
        }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(
                    rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
                        | CGBitmapInfo.byteOrder32Big.rawValue
                ),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              )
        else {
            throw NSError(domain: "GroundReferenceTrackerTests", code: 1)
        }
        return image
    }

    private func solidImage(width: Int, height: Int, value: UInt8) throws -> CGImage {
        var pixels = [UInt8](repeating: value, count: width * height * 4)
        for index in stride(from: 3, to: pixels.count, by: 4) { pixels[index] = 255 }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(
                    rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
                        | CGBitmapInfo.byteOrder32Big.rawValue
                ),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              )
        else { throw NSError(domain: "GroundReferenceTrackerTests", code: 4) }
        return image
    }

    private func grayscalePixels(_ image: CGImage) throws -> [UInt8] {
        var pixels = [UInt8](repeating: 0, count: image.width * image.height)
        let drew = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: image.width,
                space: CGColorSpaceCreateDeviceGray(),
                bitmapInfo: CGImageAlphaInfo.none.rawValue
            ) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            return true
        }
        guard drew else { throw NSError(domain: "GroundReferenceTrackerTests", code: 2) }
        return pixels
    }

    private func rgbaPixels(_ image: CGImage) throws -> [UInt8] {
        var pixels = [UInt8](repeating: 0, count: image.width * image.height * 4)
        let drew = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: image.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                    | CGBitmapInfo.byteOrder32Big.rawValue
            ) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
            return true
        }
        guard drew else { throw NSError(domain: "GroundReferenceTrackerTests", code: 3) }
        return pixels
    }

    private func rgbaPixel(
        _ pixels: [UInt8],
        width: Int,
        x: Int,
        y: Int
    ) -> [UInt8] {
        let offset = (y * width + x) * 4
        return Array(pixels[offset..<(offset + 4)])
    }
}
