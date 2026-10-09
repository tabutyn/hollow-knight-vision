import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

final class GroundHypothesisTrackerTests: XCTestCase {
    func testFeatureOverlayDoesNotDrawASecondLiveGroundLine() throws {
        let width = 20
        let image = try XCTUnwrap(GroundHypothesisTracker.overlayImage(
            for: .empty,
            width: width,
            height: 12
        ))
        let data = try XCTUnwrap(image.dataProvider?.data) as Data
        XCTAssertTrue(data.allSatisfy { $0 == 0 })
    }

    func testFeatureOverlayDoesNotPaintPersistentAtlasLineIntoLiveRaster() throws {
        let tracking = GroundHypothesisTrackingResult(
            features: [], cameraTranslation: nil, inlierCount: 0, residualRMS: nil,
            globalFeatureCount: 0, globalMatchCount: 0, globalCameraPosition: nil,
            globalCorrection: nil, verticalLineCorrection: nil, groundSegmentCount: 1,
            atlasFeatures: [],
            atlasLines: [.init(
                segmentID: 1,
                atlasStart: CGPoint(x: 2, y: 6),
                atlasEnd: CGPoint(x: 17, y: 6)
            )]
        )
        let image = try XCTUnwrap(GroundHypothesisTracker.overlayImage(
            for: tracking,
            width: 20,
            height: 12
        ))
        let data = try XCTUnwrap(image.dataProvider?.data) as Data
        XCTAssertTrue(data.allSatisfy { $0 == 0 })
    }

    func testFeatureOverlayDrawsRectangularGroundCell() throws {
        let feature = GroundHypothesisFeature(
            id: 1,
            segmentID: 1,
            sequenceIndex: 0,
            imageRect: CGRect(x: 10, y: 4, width: 16, height: 12),
            classification: .candidate,
            motionResidual: nil,
            photometricError: nil
        )
        let tracking = GroundHypothesisTrackingResult(
            features: [feature], cameraTranslation: nil, inlierCount: 0,
            residualRMS: nil, globalFeatureCount: 0, globalMatchCount: 0,
            globalCameraPosition: nil, globalCorrection: nil,
            verticalLineCorrection: nil, groundSegmentCount: 1,
            atlasFeatures: [], atlasLines: []
        )
        let width = 32
        let image = try XCTUnwrap(GroundHypothesisTracker.overlayImage(
            for: tracking,
            width: width,
            height: 24
        ))
        let data = try XCTUnwrap(image.dataProvider?.data) as Data
        func pixel(x: Int, y: Int) -> [UInt8] {
            let offset = (y * width + x) * 4
            return Array(data[offset..<(offset + 4)])
        }
        XCTAssertEqual(pixel(x: 10, y: 4), [0, 200, 255, 255])
        XCTAssertEqual(pixel(x: 10, y: 15), [0, 200, 255, 255])
        XCTAssertEqual(pixel(x: 8, y: 4), [0, 0, 0, 0])
    }

    func testRANSACSelectsDominantGroundMotionAndRejectsDepthOutliers() throws {
        let ground = (0..<20).map { index in
            GroundFeatureMotion(
                id: index,
                imageTranslation: CGVector(
                    dx: 3 + CGFloat(index % 3 - 1) * 0.2,
                    dy: -2 + CGFloat(index % 2) * 0.2
                ),
                photometricError: 4
            )
        }
        let otherDepth = (20..<28).map { index in
            GroundFeatureMotion(
                id: index,
                imageTranslation: CGVector(dx: -6, dy: 7),
                photometricError: 4
            )
        }
        let fit = try XCTUnwrap(GroundTranslationRANSAC.fit(ground + otherDepth))
        XCTAssertEqual(fit.imageTranslation.dx, 3, accuracy: 0.1)
        XCTAssertEqual(fit.imageTranslation.dy, -1.9, accuracy: 0.1)
        XCTAssertEqual(fit.inlierIDs.count, 20)
        XCTAssertTrue(fit.inlierIDs.isDisjoint(with: Set(20..<28)))
        XCTAssertLessThan(fit.residualRMS, 0.3)
    }

    func testTrackerTilesEverySixteenPixelsAndSolvesFrameTranslation() throws {
        let width = 640
        let height = 120
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let first = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        let seeded = tracker.update(
            frame: first,
            floorLines: [CleanFloorLine(row: 40, xRange: 40...600)]
        )
        XCTAssertEqual(seeded.features.count, 35)
        XCTAssertEqual(
            seeded.features.map { Int($0.imageRect.minX) },
            Array(stride(from: 40, through: 584, by: 16))
        )
        XCTAssertTrue(seeded.features.allSatisfy {
            $0.imageRect.width == 16 && $0.imageRect.height == 12 && $0.imageRect.minY == 40
        })
        assertNoHorizontalOverlap(seeded.features)

        let second = try textureImage(width: width, height: height, shiftX: -4, shiftY: 2)
        let tracked = tracker.update(
            frame: second,
            floorLines: [CleanFloorLine(row: 42, xRange: 36...596)]
        )
        let camera = try XCTUnwrap(tracked.cameraTranslation)
        XCTAssertEqual(camera.dx, 4, accuracy: 0.5)
        XCTAssertEqual(camera.dy, 2, accuracy: 0.5)
        XCTAssertGreaterThanOrEqual(tracked.inlierCount, 24)
        XCTAssertGreaterThanOrEqual(
            tracked.features.filter { $0.classification == .groundPlane }.count,
            24
        )
        XCTAssertTrue(tracked.features.allSatisfy {
            $0.imageRect.minY == 42
                && $0.imageRect.minX >= 36
                && $0.imageRect.maxX - 1 <= 596
        })
        assertNoHorizontalOverlap(tracked.features)
        XCTAssertEqual(tracker.latestStabilizedFloorLines.map(\.row), [42])
    }

    func testTrackedTextureStabilizesOnePixelDetectorRowJitter() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        _ = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        let stabilized = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 41, xRange: 0...159)]
        )

        XCTAssertEqual(stabilized.cameraTranslation?.dy, 0)
        XCTAssertEqual(tracker.latestStabilizedFloorLines.map(\.row), [40])
        XCTAssertTrue(stabilized.features.allSatisfy { $0.imageRect.minY == 40 })
    }

    func testLifecycleAddsUnlimitedTilesToFillNewlyVisibleScreenArea() throws {
        let width = 640
        let height = 120
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let first = tracker.update(
            frame: try textureImage(width: width, height: height, shiftX: 0, shiftY: 0),
            floorLines: [CleanFloorLine(row: 40, xRange: 40...420)]
        )
        let firstIDs = Set(first.features.map(\.id))
        XCTAssertEqual(first.features.count, 23)

        let moved = tracker.update(
            frame: try textureImage(width: width, height: height, shiftX: -12, shiftY: 0),
            floorLines: [CleanFloorLine(row: 40, xRange: 28...620)], timestamp: 0.1
        )
        let newFeatures = moved.features.filter { !firstIDs.contains($0.id) }
        XCTAssertEqual(moved.features.count, 37)
        XCTAssertFalse(newFeatures.isEmpty)
        XCTAssertTrue(newFeatures.contains { $0.imageRect.minX > 500 })
        XCTAssertTrue(moved.features.allSatisfy {
            $0.imageRect.minY == 40
                && $0.imageRect.minX >= 28
                && $0.imageRect.maxX - 1 <= 620
        })
        assertNoHorizontalOverlap(moved.features)
    }

    func testEveryCleanLineIsFullyTiledWithoutThirtyTwoFeatureLimit() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let result = tracker.update(
            frame: try textureImage(width: 640, height: 140, shiftX: 0, shiftY: 0),
            floorLines: [
                CleanFloorLine(row: 24, xRange: 0...639),
                CleanFloorLine(row: 80, xRange: 0...639),
            ]
        )

        XCTAssertEqual(result.features.count, 80)
        XCTAssertEqual(result.features.filter { $0.imageRect.minY == 24 }.count, 40)
        XCTAssertEqual(result.features.filter { $0.imageRect.minY == 80 }.count, 40)
        assertNoHorizontalOverlap(result.features)
    }

    func testPermanentSegmentDoesNotRephaseWhenDetectedEndpointJitters() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        let first = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 40...200)]
        )
        let firstPositions = Dictionary(uniqueKeysWithValues: first.features.map {
            ($0.id, $0.imageRect.minX)
        })

        let jittered = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 42...202)]
        )
        XCTAssertEqual(jittered.groundSegmentCount, 1)
        XCTAssertEqual(jittered.features.first?.imageRect.minX, 40)
        for feature in jittered.features {
            if let priorX = firstPositions[feature.id] {
                XCTAssertEqual(feature.imageRect.minX, priorX)
            }
        }
    }

    func testPermanentSequenceKeepsOrangeCellsAcrossOcclusion() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let result = tracker.update(
            frame: try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0),
            floorLines: [CleanFloorLine(
                row: 40,
                xRange: 0...159,
                evidenceRanges: [0...47, 112...159]
            )]
        )

        XCTAssertEqual(result.groundSegmentCount, 1)
        XCTAssertEqual(result.features.count, 10)
        XCTAssertEqual(result.features.map(\.sequenceIndex), Array(0..<10))
        XCTAssertEqual(
            result.features.filter { $0.classification == .occluded }.map(\.sequenceIndex),
            [3, 4, 5, 6]
        )
    }

    func testInteriorOccludedCellsRemainInPersistentLineLattice() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        let merged = CleanFloorLine(
            row: 40,
            xRange: 0...159,
            evidenceRanges: [0...47, 112...159]
        )

        var result = tracker.update(frame: frame, floorLines: [merged])
        XCTAssertEqual(
            result.features.filter { $0.classification == .occluded }.map(\.sequenceIndex),
            [3, 4, 5, 6]
        )
        for _ in 1..<5 {
            result = tracker.update(frame: frame, floorLines: [merged])
        }

        XCTAssertEqual(result.features.map(\.sequenceIndex), Array(0..<10))
        XCTAssertTrue(result.atlasFeatures.contains { (3...6).contains($0.sequenceIndex) })
    }

    func testKnightCoveredCellsAreUnobservedInsteadOfRejected() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.1)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        let fullLine = [CleanFloorLine(row: 40, xRange: 0...159)]
        for _ in 0..<20 { _ = tracker.update(frame: frame, floorLines: fullLine) }

        let knight = CGRect(x: 48, y: 40, width: 64, height: 32)
        var hidden = GroundHypothesisTrackingResult.empty
        for _ in 0..<5 {
            hidden = tracker.update(
                frame: frame,
                floorLines: [],
                protectedOcclusions: [knight]
            )
        }

        XCTAssertEqual(hidden.features.map(\.sequenceIndex), Array(0..<10))
        XCTAssertTrue(hidden.features.allSatisfy { $0.classification == .occluded })
        XCTAssertEqual(hidden.atlasFeatures.map(\.sequenceIndex), Array(0..<10))

        let visibleAgain = tracker.update(frame: frame, floorLines: fullLine)
        XCTAssertEqual(visibleAgain.features.map(\.sequenceIndex), Array(0..<10))
        XCTAssertTrue(visibleAgain.features.allSatisfy {
            $0.classification != .occluded
        })
    }

    func testShortPersistentLineInsideKnownForegroundIsRejected() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        _ = tracker.update(frame: frame, floorLines: [],
            protectedOcclusions: [CGRect(x: 48, y: 36, width: 64, height: 32)])

        let object = CGRect(x: 48, y: 36, width: 64, height: 32)
        var result = GroundHypothesisTrackingResult.empty
        for _ in 0..<5 {
            result = tracker.update(
                frame: frame,
                floorLines: [],
                protectedOcclusions: [object]
            )
        }

        XCTAssertTrue(result.features.isEmpty)
        XCTAssertTrue(result.atlasFeatures.isEmpty)
        XCTAssertEqual(result.groundSegmentCount, 0)
    }

    func testUnverifiedFeaturelessFrameCannotOverwriteAtlasPatches() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let line = [CleanFloorLine(row: 40, xRange: 0...159)]
        let first = tracker.update(
            frame: try solidImage(width: 160, height: 100, value: 40),
            floorLines: line
        )
        XCTAssertTrue(first.atlasFeatures.allSatisfy {
            $0.referencePixels.count == GroundHypothesisTracker.featurePixelCount
                && Set($0.referencePixels) == [40]
        })

        let refreshed = tracker.update(
            frame: try solidImage(width: 160, height: 100, value: 90),
            floorLines: line
        )
        XCTAssertTrue(refreshed.atlasFeatures.allSatisfy {
            $0.referencePixels.count == GroundHypothesisTracker.featurePixelCount
                && Set($0.referencePixels) == [40]
        })
        XCTAssertFalse(refreshed.poseVerified)
        XCTAssertEqual(refreshed.atlasFeatures.map(\.referenceOpacity),
                       first.atlasFeatures.map(\.referenceOpacity))
    }

    func testCameraConsistentPixelsGainOpacity() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        let line = [CleanFloorLine(row: 40, xRange: 0...159)]
        let first = tracker.update(frame: frame, floorLines: line)
        let firstMean = opacityMean(first.atlasFeatures)
        let second = tracker.update(frame: frame, floorLines: line)
        let secondMean = opacityMean(second.atlasFeatures)

        XCTAssertGreaterThan(secondMean, firstMean)
    }

    func testEntireUnsupportedLineExpiresTogether() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        _ = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        var result = GroundHypothesisTrackingResult.empty
        for _ in 0..<5 {
            result = tracker.update(frame: frame, floorLines: [])
        }

        XCTAssertEqual(result.groundSegmentCount, 0)
        XCTAssertTrue(result.features.isEmpty)
        XCTAssertTrue(result.atlasFeatures.isEmpty)
    }

    func testMergingSegmentsRebuildsOneOrderedSequence() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        let separate = tracker.update(
            frame: frame,
            floorLines: [
                CleanFloorLine(row: 40, xRange: 0...79),
                CleanFloorLine(row: 40, xRange: 200...279),
            ],
            occlusionMergeGap: 50
        )
        XCTAssertEqual(separate.groundSegmentCount, 2)

        let merged = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...279)],
            occlusionMergeGap: 300
        )
        XCTAssertEqual(merged.groundSegmentCount, 1)
        XCTAssertEqual(merged.features.map(\.sequenceIndex), Array(0..<17))
        assertNoHorizontalOverlap(merged.features)
    }

    func testDisconnectedSegmentsShareGlobalPhaseThenMergeIntoOneRectifiedGrid() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        let separate = tracker.update(
            frame: frame,
            floorLines: [
                CleanFloorLine(row: 40, xRange: 3...82),
                CleanFloorLine(row: 40, xRange: 202...281),
            ],
            occlusionMergeGap: 50
        )

        XCTAssertEqual(separate.groundSegmentCount, 2)
        XCTAssertEqual(
            separate.features.map { Int($0.imageRect.minX) },
            [3, 19, 35, 51, 67, 211, 227, 243, 259]
        )
        XCTAssertTrue(separate.features.allSatisfy {
            (Int($0.imageRect.minX) - 3).isMultiple(of: 16)
        })

        let merged = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 3...281)],
            occlusionMergeGap: 300
        )

        XCTAssertEqual(merged.groundSegmentCount, 1)
        XCTAssertEqual(merged.features.map(\.sequenceIndex), Array(0..<17))
        XCTAssertEqual(
            merged.features.map { Int($0.imageRect.minX) },
            Array(stride(from: 3, through: 259, by: 16))
        )
        assertNoHorizontalOverlap(merged.features)
    }

    func testFeaturelessTilesNeverEnterGlobalSet() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try solidImage(width: 320, height: 100, value: 90)
        var result = GroundHypothesisTrackingResult.empty
        for _ in 0..<5 {
            result = tracker.update(
                frame: frame,
                floorLines: [CleanFloorLine(row: 40, xRange: 0...319)]
            )
        }

        XCTAssertEqual(result.features.count, 20)
        XCTAssertEqual(result.globalFeatureCount, 0)
        XCTAssertFalse(result.features.contains { $0.classification == .globalMatch })
    }

    func testReturnViewReacquiresPersistentFeaturesAndCorrectsGlobalPose() throws {
        let width = 320
        let height = 100
        let line = [CleanFloorLine(row: 40, xRange: 0...319)]
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        var learned = GroundHypothesisTrackingResult.empty
        for _ in 0..<3 {
            learned = tracker.update(frame: origin, floorLines: line)
        }
        XCTAssertGreaterThanOrEqual(learned.globalFeatureCount, 16)
        let originalLineID = try XCTUnwrap(learned.atlasLines.first).segmentID

        // Discard all live tracks while retaining the persistent map, as if
        // the starting floor had gone fully offscreen.
        tracker.reset(keepingGlobalFeatures: true)
        let right = tracker.update(
            frame: try textureImage(width: width, height: height, shiftX: -64, shiftY: 0),
            floorLines: line
        )
        XCTAssertGreaterThanOrEqual(right.globalMatchCount, 8)
        XCTAssertEqual(try XCTUnwrap(right.globalCameraPosition).x, 64, accuracy: 0.5)

        let returned = tracker.update(frame: origin, floorLines: line, timestamp: 0.5)
        XCTAssertGreaterThanOrEqual(returned.globalMatchCount, 8)
        XCTAssertEqual(try XCTUnwrap(returned.globalCameraPosition).x, 0, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(returned.cameraPosition).x, 0, accuracy: 1)
        XCTAssertGreaterThanOrEqual(
            returned.features.filter { $0.classification == .globalMatch }.count,
            8
        )
        XCTAssertGreaterThanOrEqual(returned.atlasFeatures.count, 8)
        XCTAssertFalse(returned.atlasLines.isEmpty)
        XCTAssertEqual(returned.groundSegmentCount, 1)
        XCTAssertTrue(returned.atlasLines.allSatisfy { $0.segmentID == originalLineID })
    }

    func testBackgroundRecoveryConfirmsReturnAndSurvivesReset() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0, backgroundGlobalSearch: true)
        let origin = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        let line = [CleanFloorLine(row: 40, xRange: 0...319)]
        for index in 0..<5 {
            _ = tracker.update(frame: origin, floorLines: line, timestamp: Double(index) / 60)
        }
        tracker.reset(keepingGlobalFeatures: true)
        let shifted = try textureImage(width: 320, height: 100, shiftX: -64, shiftY: 0)
        var recovered = GroundHypothesisTrackingResult.empty
        for index in 0..<80 {
            recovered = tracker.update(frame: shifted, floorLines: line,
                timestamp: 1 + Double(index) * 0.025)
            if recovered.globalMatchCount >= 6 { break }
            Thread.sleep(forTimeInterval: 0.01)
        }
        XCTAssertGreaterThanOrEqual(recovered.globalMatchCount, 6)
        XCTAssertEqual(try XCTUnwrap(recovered.cameraPosition).x, 64, accuracy: 1)
        tracker.reset()
        for index in 0..<5 {
            let result = tracker.update(frame: origin, floorLines: line,
                timestamp: 5 + Double(index) * 0.025)
            XCTAssertEqual(try XCTUnwrap(result.cameraPosition).x, 0, accuracy: 1)
            Thread.sleep(forTimeInterval: 0.01)
        }
    }

    func testBackgroundRecoverySurvivesRepeatedFloorlessReanchors() throws {
        let tracker = GroundHypothesisTracker(
            minimumObservationSeconds: 0,
            backgroundGlobalSearch: true
        )
        let origin = try textureImage(
            width: 320, height: 100, shiftX: 0, shiftY: 0
        )
        let line = [CleanFloorLine(row: 40, xRange: 0...319)]
        for index in 0..<5 {
            _ = tracker.update(
                frame: origin,
                floorLines: line,
                timestamp: Double(index) / 60
            )
        }

        let fallback = CGPoint(x: 96, y: 0)
        tracker.reanchorLocalCamera(to: fallback)
        var recovered = GroundHypothesisTrackingResult.empty
        for index in 0..<120 {
            let timestamp = 1 + Double(index) * 0.025
            recovered = tracker.update(
                frame: origin,
                floorLines: line,
                timestamp: timestamp
            )
            if recovered.globalMatchCount >= 6,
               abs((recovered.cameraPosition?.x ?? .infinity)) <= 1 {
                break
            }
            // Live floorless fallback is published after the ground update.
            // It may refresh provisional local odometry, but must not starve
            // the independent persistent-place search or its confirmation.
            tracker.reanchorLocalCamera(
                to: fallback,
                frame: origin,
                floorLines: line,
                timestamp: timestamp
            )
            Thread.sleep(forTimeInterval: 0.01)
        }

        XCTAssertGreaterThanOrEqual(recovered.globalMatchCount, 6)
        XCTAssertEqual(try XCTUnwrap(recovered.cameraPosition).x, 0, accuracy: 1)
    }

    func testReturnSearchUsesVisibleTextureBelowFragmentedFloorEdge() throws {
        let width = 320, height = 100
        let origin = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        let gaps = stride(from: 0, to: width - 8, by: 12).map { $0...($0 + 7) }
        for isKnownPlace in [true, false] {
            let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
            for index in 0..<3 {
                _ = tracker.update(frame: origin,
                    floorLines: [CleanFloorLine(row: 40, xRange: 0...319)],
                    timestamp: Double(index) / 60)
            }
            tracker.reset(keepingGlobalFeatures: true)
            let result = tracker.update(
                frame: try textureImage(width: width, height: height,
                    shiftX: isKnownPlace ? -62 : -5000, shiftY: 5),
                floorLines: [CleanFloorLine(row: 45, xRange: 0...319,
                    evidenceRanges: gaps)], timestamp: 0.5)
            if isKnownPlace {
                XCTAssertGreaterThanOrEqual(result.globalMatchCount, 6)
                XCTAssertEqual(try XCTUnwrap(result.globalCameraPosition).x, 62, accuracy: 0.5)
                XCTAssertEqual(try XCTUnwrap(result.globalCameraPosition).y, 5, accuracy: 0.5)
            } else {
                XCTAssertEqual(result.globalMatchCount, 0,
                    "A continuous floor span must not turn unrelated texture into a closure")
            }
        }
    }

    func testGlobalReturnSearchIsIndependentOfSixteenPixelLatticePhase() throws {
        let width = 320
        let height = 100
        let line = [CleanFloorLine(row: 40, xRange: 0...319)]
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        for index in 0..<3 {
            _ = tracker.update(frame: origin, floorLines: line,
                timestamp: Double(index) / 60)
        }

        tracker.reset(keepingGlobalFeatures: true)
        let shifted = tracker.update(
            frame: try textureImage(width: width, height: height, shiftX: -62, shiftY: 5),
            floorLines: [CleanFloorLine(row: 45, xRange: 0...319)],
            timestamp: 0.5
        )

        XCTAssertGreaterThanOrEqual(shifted.globalMatchCount, 6)
        XCTAssertEqual(try XCTUnwrap(shifted.globalCameraPosition).x, 62, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(shifted.globalCameraPosition).y, 5, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(shifted.cameraPosition).x, 62, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(shifted.cameraPosition).y, 5, accuracy: 0.5)
    }

    func testGlobalReturnSearchHandlesScreenSpaceToneChange() throws {
        let width = 320
        let height = 100
        let line = [CleanFloorLine(row: 40, xRange: 0...319)]
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        for index in 0..<3 {
            _ = tracker.update(frame: origin, floorLines: line,
                timestamp: Double(index) / 60)
        }

        tracker.reset(keepingGlobalFeatures: true)
        let shifted = tracker.update(
            frame: try textureImage(width: width, height: height,
                shiftX: -62, shiftY: 5, gain: 0.6, bias: 50),
            floorLines: [CleanFloorLine(row: 45, xRange: 0...319)],
            timestamp: 0.5
        )

        XCTAssertGreaterThanOrEqual(shifted.globalMatchCount, 6)
        XCTAssertEqual(try XCTUnwrap(shifted.globalCameraPosition).x, 62, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(shifted.globalCameraPosition).y, 5, accuracy: 0.5)
    }

    func testOneFrameLineNeverCreatesPersistentFeatures() throws {
        let tracker = GroundHypothesisTracker()
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)

        let appeared = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 32...127)]
        )
        let gone = tracker.update(frame: frame, floorLines: [])
        let expired = tracker.update(frame: frame, floorLines: [])

        XCTAssertEqual(appeared.groundSegmentCount, 0)
        XCTAssertTrue(appeared.features.isEmpty)
        XCTAssertTrue(gone.features.isEmpty)
        XCTAssertTrue(expired.features.isEmpty)
    }

    func testStableShortPlatformNeedsCameraConsistentMotionBeforeAtlasAdmission() throws {
        let tracker = GroundHypothesisTracker()
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        let initialLines = [CleanFloorLine(row: 40, xRange: 40...71),
                            CleanFloorLine(row: 70, xRange: 0...159)]
        var result = GroundHypothesisTrackingResult.empty

        for index in 0...120 {
            result = tracker.update(
                frame: frame,
                floorLines: initialLines,
                timestamp: Double(index) / 60
            )
        }
        XCTAssertEqual(result.groundSegmentCount, 1)

        for shift in stride(from: 2, through: 12, by: 2) {
            result = tracker.update(
                frame: try textureImage(
                    width: 160, height: 100,
                    shiftX: -shift, shiftY: 0
                ),
                floorLines: [
                    CleanFloorLine(row: 40, xRange: (40 - shift)...(71 - shift)),
                    CleanFloorLine(row: 70, xRange: 0...159),
                ],
                timestamp: 2 + Double(shift) / 60
            )
        }
        let movedFrame = try textureImage(
            width: 160, height: 100, shiftX: -12, shiftY: 0
        )
        let movedLines = [CleanFloorLine(row: 40, xRange: 28...59),
                          CleanFloorLine(row: 70, xRange: 0...159)]
        // A detector gap longer than the provisional-candidate lifetime must
        // not erase a line whose texture already passed motion verification.
        for index in 1...8 {
            _ = tracker.update(
                frame: movedFrame,
                floorLines: [movedLines[1]],
                timestamp: 2.2 + Double(index) / 60
            )
        }
        for index in 1...120 {
            result = tracker.update(
                frame: movedFrame,
                floorLines: movedLines,
                timestamp: 2.4 + Double(index) / 60
            )
        }

        XCTAssertEqual(result.groundSegmentCount, 2)
        let platform = result.features.filter { Int($0.imageRect.minY) == 40 }
        // The dominant line establishes phase zero. Only the complete tile
        // 48...63 in world space fits this short platform; no partial tiles.
        XCTAssertEqual(platform.map(\.sequenceIndex), [0])
        XCTAssertEqual(platform.map { Int($0.imageRect.minX) }, [36])
        XCTAssertTrue(platform.allSatisfy { Int($0.imageRect.minY) == 40 })
    }

    func testOneFrameExtensionDoesNotGrowPersistentSequence() throws {
        let tracker = GroundHypothesisTracker()
        let frame = try textureImage(width: 240, height: 100, shiftX: 0, shiftY: 0)
        let base = [CleanFloorLine(row: 40, xRange: 40...119)]
        for _ in 0...120 { _ = tracker.update(frame: frame, floorLines: base) }

        let transient = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 40...199)]
        )
        _ = tracker.update(frame: frame, floorLines: base)
        let expired = tracker.update(frame: frame, floorLines: base)

        XCTAssertEqual(transient.features.count, 5)
        XCTAssertEqual(expired.features.count, 5)
        XCTAssertEqual(expired.features.map { Int($0.imageRect.minX) }, [40, 56, 72, 88, 104])
    }

    func testPartiallyVisibleLineKeepsCompleteLattice() throws {
        let tracker = GroundHypothesisTracker()
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        let long = [CleanFloorLine(row: 40, xRange: 0...159)]
        for _ in 0...120 { _ = tracker.update(frame: frame, floorLines: long) }

        let short = [CleanFloorLine(row: 40, xRange: 0...79)]
        var result = GroundHypothesisTrackingResult.empty
        for _ in 0..<8 { result = tracker.update(frame: frame, floorLines: short) }

        XCTAssertEqual(result.features.count, 10)
        XCTAssertEqual(result.features.map(\.sequenceIndex), Array(0..<10))
        XCTAssertGreaterThanOrEqual(result.globalFeatureCount, 5)
    }

    func testRejectedLineRemovesItsEntirePersistentSequence() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let width = 320
        let height = 100
        let line = [CleanFloorLine(row: 40, xRange: 0...(width - 1))]
        for shift in stride(from: 0, through: -96, by: -12) {
            _ = tracker.update(
                frame: try textureImage(
                    width: width,
                    height: height,
                    shiftX: shift,
                    shiftY: 0
                ),
                floorLines: line, timestamp: Double(-shift) / 120
            )
        }

        var cleaned = GroundHypothesisTrackingResult.empty
        let lastFrame = try textureImage(
            width: width,
            height: height,
            shiftX: -96,
            shiftY: 0
        )
        for _ in 0..<120 {
            cleaned = tracker.update(frame: lastFrame, floorLines: [])
        }

        XCTAssertTrue(cleaned.features.isEmpty)
        XCTAssertTrue(cleaned.atlasFeatures.isEmpty, "rejected line cannot keep registered tiles")
        XCTAssertTrue(cleaned.atlasLines.isEmpty)
    }

    func testRejectedLineKeepsHiddenLoopClosureEvidence() throws {
        let width = 320
        let height = 100
        let line = [CleanFloorLine(row: 40, xRange: 0...(width - 1))]
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: width, height: height, shiftX: 0, shiftY: 0)
        var learned = GroundHypothesisTrackingResult.empty
        for index in 0..<3 {
            learned = tracker.update(frame: origin, floorLines: line,
                timestamp: Double(index) / 60)
        }
        XCTAssertGreaterThanOrEqual(learned.globalFeatureCount, 16)

        var cleaned = learned
        for index in 3..<130 {
            cleaned = tracker.update(frame: origin, floorLines: [],
                timestamp: Double(index) / 60)
        }
        XCTAssertTrue(cleaned.atlasFeatures.isEmpty)
        XCTAssertTrue(cleaned.atlasLines.isEmpty)
        XCTAssertGreaterThanOrEqual(cleaned.globalFeatureCount, 16,
            "visual cleanup must not erase loop-closure descriptors")

        tracker.reset(keepingGlobalFeatures: true)
        let returned = tracker.update(
            frame: try textureImage(width: width, height: height,
                shiftX: -62, shiftY: 5),
            floorLines: [CleanFloorLine(row: 45, xRange: 0...(width - 1))],
            timestamp: 3
        )
        XCTAssertGreaterThanOrEqual(returned.globalMatchCount, 6)
        XCTAssertEqual(try XCTUnwrap(returned.globalCameraPosition).x, 62, accuracy: 0.5)
        XCTAssertEqual(try XCTUnwrap(returned.globalCameraPosition).y, 5, accuracy: 0.5)
    }

    func testPersistentLineImmediatelyCorrectsVerticalCameraPose() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0)
        _ = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )

        let shifted = try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 5)
        let corrected = tracker.update(
            frame: shifted,
            floorLines: [CleanFloorLine(row: 45, xRange: 0...159)]
        )
        XCTAssertEqual(try XCTUnwrap(corrected.cameraTranslation?.dy), 5, accuracy: 0.001)
        XCTAssertTrue(corrected.features.allSatisfy { $0.imageRect.minY == 45 })

        let stable = tracker.update(
            frame: shifted,
            floorLines: [CleanFloorLine(row: 45, xRange: 0...159)]
        )
        XCTAssertEqual(try XCTUnwrap(stable.cameraTranslation?.dy), 0, accuracy: 0.001)
    }

    func testPersistentAtlasLineDoesNotMoveDuringVerticalCameraRoundTrip() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let originFrame = try textureImage(
            width: 160, height: 100, shiftX: 0, shiftY: 0
        )
        let origin = tracker.update(
            frame: originFrame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        let originalLines = origin.atlasLines
        let originalPositions = origin.atlasFeatures.map(\.atlasPosition)

        let down = tracker.update(
            frame: try textureImage(
                width: 160, height: 100, shiftX: 0, shiftY: 8
            ),
            floorLines: [CleanFloorLine(row: 48, xRange: 0...159)]
        )
        XCTAssertEqual(down.atlasLines, originalLines)
        XCTAssertEqual(down.atlasFeatures.map(\.atlasPosition), originalPositions)

        let returned = tracker.update(
            frame: originFrame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        XCTAssertEqual(returned.atlasLines, originalLines)
        XCTAssertEqual(returned.atlasFeatures.map(\.atlasPosition), originalPositions)
    }

    func testGroundOnlyTrackingKeepsLineIDAcrossLargeVerticalLook() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = tracker.update(
            frame: try textureImage(width: 160, height: 180, shiftX: 0, shiftY: 0),
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)])
        let lineID = try XCTUnwrap(origin.atlasLines.first?.segmentID)
        for shift in Array(stride(from: 5, through: 110, by: 5))
            + Array(stride(from: 105, through: 0, by: -5)) {
            let result = tracker.update(
                frame: try textureImage(width: 160, height: 180, shiftX: 0, shiftY: shift),
                floorLines: [CleanFloorLine(row: 40 + shift, xRange: 0...159)])
            XCTAssertTrue(result.poseVerified)
            XCTAssertEqual(result.cameraPosition?.y ?? .nan, CGFloat(shift), accuracy: 0.5)
            XCTAssertEqual(result.groundSegmentCount, 1)
            XCTAssertTrue(result.atlasLines.allSatisfy { $0.segmentID == lineID })
        }
    }

    func testFeaturelessVerticalLookMustHoldPose() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try solidImage(width: 160, height: 240, value: 90)
        let learned = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        let lineOneID = try XCTUnwrap(learned.atlasLines.first?.segmentID)

        let looked = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 150, xRange: 0...159)],
            timestamp: 1
        )

        XCTAssertFalse(looked.poseVerified)
        XCTAssertEqual(looked.cameraPosition, .zero)
        XCTAssertEqual(looked.groundSegmentCount, 1)
        XCTAssertTrue(looked.atlasLines.allSatisfy { $0.segmentID == lineOneID })
        XCTAssertTrue(looked.features.allSatisfy { $0.imageRect.minY == 40 })
    }

    func testUnresolvedVerticalLookCannotCreateOrDeleteDuplicateLineOne() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(
            width: 160, height: 180, shiftX: 0, shiftY: 0
        )
        let learned = tracker.update(
            frame: origin,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )
        let lineOneID = try XCTUnwrap(learned.atlasLines.first?.segmentID)
        let shifted = try textureImage(
            width: 160, height: 180, shiftX: 0, shiftY: 110
        )

        var unresolved = GroundHypothesisTrackingResult.empty
        for _ in 0..<12 {
            unresolved = tracker.update(
                frame: shifted,
                floorLines: [CleanFloorLine(row: 150, xRange: 0...159)]
            )
        }

        XCTAssertEqual(unresolved.groundSegmentCount, 1)
        XCTAssertTrue(unresolved.atlasLines.allSatisfy { $0.segmentID == lineOneID })
    }

    func testParallelLinesCannotAlignWithoutFeatureConfirmation() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try solidImage(width: 160, height: 100, value: 90)
        _ = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )

        let ambiguous = tracker.update(
            frame: frame,
            floorLines: [
                CleanFloorLine(row: 42, xRange: 0...79),
                CleanFloorLine(row: 70, xRange: 0...159),
            ]
        )

        XCTAssertNil(ambiguous.verticalLineCorrection)
    }

    func testFeatureMatchesChooseCorrectParallelLineBeforeOverlap() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        _ = tracker.update(
            frame: try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 0),
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )

        let aligned = tracker.update(
            frame: try textureImage(width: 160, height: 100, shiftX: 0, shiftY: 5),
            floorLines: [
                CleanFloorLine(row: 45, xRange: 0...79),
                CleanFloorLine(row: 55, xRange: 0...159),
            ]
        )

        XCTAssertEqual(try XCTUnwrap(aligned.cameraTranslation).dy, 5, accuracy: 0.5)
        XCTAssertNil(aligned.verticalLineCorrection, "No overlap-only correction after texture solve")
    }

    func testDistantUnidentifiedLineCannotMovePersistentGroundPose() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let frame = try textureImage(width: 160, height: 160, shiftX: 0, shiftY: 0)
        _ = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...159)]
        )

        let unrelated = tracker.update(
            frame: frame,
            floorLines: [CleanFloorLine(row: 89, xRange: 0...159)]
        )
        XCTAssertNil(unrelated.verticalLineCorrection)
    }

    private func assertNoHorizontalOverlap(
        _ features: [GroundHypothesisFeature],
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        for row in Dictionary(grouping: features, by: { $0.imageRect.minY }).values {
            let sorted = row.sorted { $0.imageRect.minX < $1.imageRect.minX }
            for pair in zip(sorted, sorted.dropFirst()) {
                XCTAssertLessThanOrEqual(
                    pair.0.imageRect.maxX,
                    pair.1.imageRect.minX,
                    file: file,
                    line: line
                )
            }
        }
    }

    private func opacityMean(_ features: [GroundHypothesisAtlasFeature]) -> Double {
        let values = features.flatMap(\.referenceOpacity)
        guard !values.isEmpty else { return 0 }
        return Double(values.reduce(0) { $0 + Int($1) }) / Double(values.count)
    }

    func testNewFloorEnteringWithoutCompletePatchEventuallyPassesMotionChecks() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2)
        var result = GroundHypothesisTrackingResult.empty
        for index in 0...100 {
            let shift = max(0, index - 30) * 2
            // World texture continues beyond the right edge of the first view.
            let bytes: [UInt8] = (0..<(320*140)).map { index in
                var hash = UInt64((index % 320 + shift) * 73_856_093)
                    ^ UInt64((index / 320) * 19_349_663)
                hash ^= hash >> 13; hash &*= 1_274_126_177
                return UInt8(truncatingIfNeeded: hash >> 11)
            }
            let provider = try XCTUnwrap(CGDataProvider(data: Data(bytes) as CFData))
            let frame = try XCTUnwrap(CGImage(width: 320, height: 140,
                bitsPerComponent: 8, bitsPerPixel: 8, bytesPerRow: 320,
                space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGBitmapInfo(rawValue: 0),
                provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
            var lines = [CleanFloorLine(row: 40, xRange: 0...319)]
            if index >= 30 {
                lines.append(CleanFloorLine(row: 90, xRange: max(0, 314-shift)...319))
            }
            result = tracker.update(frame: frame, floorLines: lines,
                timestamp: Double(index)/60)
        }
        XCTAssertTrue(result.atlasLines.contains { abs($0.atlasStart.y - 50) < 4 })
    }

    func testMeasuredFallbackSeedsCurrentCaptureWithoutLosingNextMotion() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        var initial = GroundHypothesisTrackingResult.empty
        for index in 0..<3 {
            initial = tracker.update(frame: origin,
                floorLines: [CleanFloorLine(row: 40, xRange: 0...319)],
                timestamp: Double(index) / 60)
        }
        let originalLine = try XCTUnwrap(initial.atlasLines.first)
        tracker.reanchorLocalCamera(to: CGPoint(x: 12, y: 4),
            frame: try textureImage(width: 320, height: 100, shiftX: -12, shiftY: 4),
            floorLines: [CleanFloorLine(row: 44, xRange: 0...319)], timestamp: 0.05)
        let resumed = tracker.update(
            frame: try textureImage(width: 320, height: 100, shiftX: -16, shiftY: 8),
            floorLines: [CleanFloorLine(row: 48, xRange: 0...319)], timestamp: 0.07)
        XCTAssertTrue(resumed.poseVerified)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).x, 16, accuracy: 0.01)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).y, 8, accuracy: 0.01)
        let preservedLine = try XCTUnwrap(resumed.atlasLines.first {
            $0.segmentID == originalLine.segmentID
        })
        XCTAssertEqual(preservedLine.atlasStart.y, originalLine.atlasStart.y)
    }

    func testMeasuredFallbackDefinesEpochBeforeAnyGroundIsEstablished() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0.2)
        let frame = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        _ = tracker.update(frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...319)], timestamp: 0)
        tracker.reanchorLocalCamera(to: CGPoint(x: 13, y: 0), frame: frame,
            floorLines: [CleanFloorLine(row: 40, xRange: 0...319)], timestamp: 0.01)

        var result = GroundHypothesisTrackingResult.empty
        for index in 1...20 {
            result = tracker.update(frame: frame,
                floorLines: [CleanFloorLine(row: 40, xRange: 0...319)],
                timestamp: 0.01 + Double(index) / 60)
        }
        XCTAssertTrue(result.poseVerified)
        XCTAssertTrue(result.hasConfirmedGround)
        XCTAssertEqual(try XCTUnwrap(result.cameraPosition).x, 13, accuracy: 0.01)
    }

    func testProvisionalContinuitySurvivesBriefTextureMissThenExpires() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        for index in 0..<3 {
            _ = tracker.update(frame: origin,
                floorLines: [CleanFloorLine(row: 40, xRange: 0...319)],
                timestamp: Double(index) / 60)
        }
        tracker.reanchorLocalCamera(to: CGPoint(x: 12, y: 4),
            frame: try textureImage(width: 320, height: 100, shiftX: -12, shiftY: 4),
            floorLines: [CleanFloorLine(row: 44, xRange: 0...319)], timestamp: 0.05)

        let missed = tracker.update(
            frame: try solidImage(width: 320, height: 100, value: 1),
            floorLines: [], timestamp: 0.10)
        XCTAssertFalse(missed.poseVerified)
        XCTAssertTrue(missed.provisionalContinuityActive)

        let stillRecoverable = tracker.update(
            frame: try solidImage(width: 320, height: 100, value: 2),
            floorLines: [], timestamp: 0.39)
        XCTAssertTrue(stillRecoverable.provisionalContinuityActive)

        let expired = tracker.update(
            frame: try solidImage(width: 320, height: 100, value: 3),
            floorLines: [], timestamp: 0.41)
        XCTAssertFalse(expired.provisionalContinuityActive)
    }


    func testBiasedFallbackDoesNotReplaceTrustedGroundReference() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: 320, height: 100, shiftX: 0, shiftY: 0)
        for index in 0..<3 {
            _ = tracker.update(frame: origin,
                floorLines: [CleanFloorLine(row: 40, xRange: 0...319)],
                timestamp: Double(index) / 60)
        }
        // Actual camera is (12,4), but the fallback is biased by (+4,+6).
        tracker.reanchorLocalCamera(to: CGPoint(x: 16, y: 10),
            frame: try textureImage(width: 320, height: 100, shiftX: -12, shiftY: 4),
            floorLines: [CleanFloorLine(row: 44, xRange: 0...319)], timestamp: 0.05)
        let resumed = tracker.update(
            frame: try textureImage(width: 320, height: 100, shiftX: -16, shiftY: 8),
            floorLines: [CleanFloorLine(row: 48, xRange: 0...319)], timestamp: 0.07)
        XCTAssertTrue(resumed.poseVerified)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).x, 16, accuracy: 0.01)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).y, 8, accuracy: 0.01)
    }

    func testDeferredPlacementKeepsAtlasEvidenceAndRecoversAgainstTrustedPixels() throws {
        let tracker = GroundHypothesisTracker(minimumObservationSeconds: 0)
        let origin = try textureImage(width: 640, height: 160, shiftX: 0, shiftY: 0)
        var initial = GroundHypothesisTrackingResult.empty
        for index in 0..<3 {
            initial = tracker.update(frame: origin,
                floorLines: [CleanFloorLine(row: 40, xRange: 16...623)],
                timestamp: Double(index) / 60)
        }
        XCTAssertFalse(initial.atlasLines.isEmpty)
        var proposals = [GroundPlacementRecovery.Proposal]()
        var recovery = GroundPlacementRecovery()
        let deferred = tracker.update(
            frame: try textureImage(width: 640, height: 160, shiftX: -4, shiftY: 48),
            floorLines: [CleanFloorLine(row: 88, xRange: 16...623)], timestamp: 0.05,
            placementValidator: {
                proposals.append($0)
                return recovery.accepts($0, current: .zero, solveWidth: 640,
                    timestamp: 0.05, captureElapsed: 1 / 60)
            })
        XCTAssertEqual(proposals.count, 1)
        XCTAssertFalse(deferred.poseVerified)
        XCTAssertTrue(recovery.retainsCandidate(at: 0.05))
        XCTAssertFalse(recovery.allowsAtlasWrite)
        XCTAssertEqual(deferred.atlasLines.map(\.atlasStart), initial.atlasLines.map(\.atlasStart))
        XCTAssertEqual(deferred.atlasLines.map(\.atlasEnd), initial.atlasLines.map(\.atlasEnd))
        XCTAssertEqual(deferred.globalFeatureCount, initial.globalFeatureCount)
        let resumed = tracker.update(
            frame: try textureImage(width: 640, height: 160, shiftX: -8, shiftY: 44),
            floorLines: [CleanFloorLine(row: 84, xRange: 16...623)], timestamp: 0.067,
            placementValidator: {
                proposals.append($0)
                return recovery.accepts($0, current: CGPoint(x: 0, y: 3), solveWidth: 640,
                    timestamp: 0.067, captureElapsed: 0.017)
            })
        XCTAssertTrue(resumed.poseVerified)
        XCTAssertTrue(recovery.allowsAtlasWrite)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).x, 8, accuracy: 0.01)
        XCTAssertEqual(try XCTUnwrap(resumed.cameraPosition).y, 44, accuracy: 0.01)
        XCTAssertTrue(try XCTUnwrap(proposals.last).distinctFrame)
    }

    private func textureImage(
        width: Int,
        height: Int,
        shiftX: Int,
        shiftY: Int,
        gain: Double = 1,
        bias: Double = 0
    ) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let sourceX = x - shiftX
                let sourceY = y - shiftY
                let value: UInt8
                if (0..<width).contains(sourceX), (0..<height).contains(sourceY) {
                    var hash = UInt64(sourceX &* 73_856_093) ^ UInt64(sourceY &* 19_349_663)
                    hash ^= hash >> 13
                    hash &*= 1_274_126_177
                    let original = UInt8(truncatingIfNeeded: hash >> 11)
                    value = UInt8(max(0, min(255,
                        Int((Double(original) * gain + bias).rounded()))))
                } else {
                    value = 0
                }
                let offset = (y * width + x) * 4
                pixels[offset] = value
                pixels[offset + 1] = value
                pixels[offset + 2] = value
                pixels[offset + 3] = 255
            }
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
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
        else { throw NSError(domain: "GroundHypothesisTrackerTests", code: 1) }
        return image
    }

    private func solidImage(width: Int, height: Int, value: UInt8) throws -> CGImage {
        var pixels = [UInt8](repeating: value, count: width * height * 4)
        for index in stride(from: 3, to: pixels.count, by: 4) { pixels[index] = 255 }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
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
        else { throw NSError(domain: "GroundHypothesisTrackerTests", code: 2) }
        return image
    }

}
