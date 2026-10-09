import CoreImage
import XCTest
@testable import HollowKnightVision

final class LiveTiledAtlasTests: XCTestCase {
    private let context = CIContext(options: [.useSoftwareRenderer: true])

    func testLargeWorldKeepsBothEndsAndStableTileIDs() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.red, size: CGSize(width: 64, height: 32))
        XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: image, solveWidth: 64, cameraPosition: .zero))
        XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: image, solveWidth: 64, cameraPosition: CGPoint(x: 5_000, y: 0)))
        let snapshot = atlas.snapshot
        XCTAssertGreaterThan(snapshot.totalBounds.width, 4_096)
        XCTAssertGreaterThanOrEqual(snapshot.tiles.count, 2)
        XCTAssertEqual(Set(snapshot.tiles.map(\.id)).count, snapshot.tiles.count)
        XCTAssertEqual(snapshot.tiles.map(\.id), snapshot.tiles.map(\.key))
    }

    func testPoseCorrectionReplacesOldPlacementAtomically() throws {
        let atlas = LiveTiledAtlas(context: context)
        let red = try solid(.red, size: CGSize(width: 16, height: 16))
        XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: red, solveWidth: 16, cameraPosition: .zero))
        XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: red, solveWidth: 16, cameraPosition: CGPoint(x: 300, y: 0)))
        let old = atlas.snapshot
        XCTAssertTrue(atlas.replaceCameraPositions([1: .zero, 2: CGPoint(x: 40, y: 0)], baseRevision: old.revision))
        let corrected = atlas.snapshot
        XCTAssertGreaterThan(corrected.revision, old.revision)
        XCTAssertFalse(corrected.tiles.contains(where: { $0.tileX == 1 }))
        XCTAssertTrue(corrected.tiles.contains(where: { $0.tileX == 0 }))
    }

    func testContentBoundsGrowInsideOneStorageTile() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.white, size: CGSize(width: 64, height: 32))
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: image,
            solveWidth: 64, cameraPosition: .zero
        ))
        let first = atlas.snapshot
        XCTAssertEqual(first.contentBounds, CGRect(x: 0, y: 0, width: 64, height: 32))

        XCTAssertTrue(atlas.insert(
            observationID: 2, maskedImage: image,
            solveWidth: 64, cameraPosition: CGPoint(x: 1, y: 0)
        ))
        let second = atlas.snapshot
        XCTAssertEqual(second.totalBounds, first.totalBounds)
        XCTAssertEqual(second.contentBounds.width, first.contentBounds.width + 1)
        XCTAssertEqual(second.contentBounds.height, first.contentBounds.height)
    }

    func testRejectsStaleAndIncompletePoseCorrections() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.white, size: CGSize(width: 8, height: 8))
        XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: image, solveWidth: 8, cameraPosition: .zero))
        XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: image, solveWidth: 8, cameraPosition: CGPoint(x: 16, y: 0)))
        let revision = atlas.revision
        XCTAssertFalse(atlas.replaceCameraPositions([1: .zero], baseRevision: revision))
        XCTAssertFalse(atlas.replaceCameraPositions([1: .zero, 2: CGPoint(x: 16, y: 0)], baseRevision: revision - 1))
        XCTAssertEqual(atlas.revision, revision)
    }

    func testCancelledPoseCorrectionRollsBackAndCanBeRetried() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.white, size: CGSize(width: 16, height: 16))
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: image, solveWidth: 16,
            cameraPosition: .zero
        ))
        XCTAssertTrue(atlas.insert(
            observationID: 2, maskedImage: image, solveWidth: 16,
            cameraPosition: CGPoint(x: 300, y: 0)
        ))
        let before = atlas.snapshot
        let corrected = [1: CGPoint.zero, 2: CGPoint(x: 40, y: 0)]
        var cancellationChecks = 0

        XCTAssertFalse(atlas.replaceCameraPositions(
            corrected,
            baseRevision: before.revision,
            shouldCancel: {
                cancellationChecks += 1
                return cancellationChecks >= 3
            }
        ))
        XCTAssertEqual(atlas.snapshot.revision, before.revision)
        XCTAssertEqual(atlas.snapshot.tiles.map(\.id), before.tiles.map(\.id))
        XCTAssertEqual(atlas.snapshot.totalBounds, before.totalBounds)

        XCTAssertTrue(atlas.replaceCameraPositions(
            corrected,
            baseRevision: before.revision
        ))
        XCTAssertFalse(atlas.snapshot.tiles.contains(where: { $0.tileX == 1 }))
    }

    func testPoseCorrectionCanCancelInsideOneCrowdedTile() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.white, size: CGSize(width: 1, height: 1))
        let count = 20
        for id in 0..<count {
            XCTAssertTrue(atlas.insert(
                observationID: id,
                maskedImage: image,
                solveWidth: 1,
                cameraPosition: .zero
            ))
        }
        let before = atlas.snapshot
        let corrected = Dictionary(uniqueKeysWithValues: (0..<count).map {
            ($0, CGPoint(x: 1, y: 0))
        })
        var cancellationChecks = 0

        XCTAssertFalse(atlas.replaceCameraPositions(
            corrected,
            baseRevision: before.revision,
            shouldCancel: {
                cancellationChecks += 1
                return cancellationChecks >= count + 7
            }
        ))
        XCTAssertGreaterThanOrEqual(cancellationChecks, count + 7)
        XCTAssertLessThan(atlas.lastPublicationRasterizedContributionCount, count)
        XCTAssertEqual(atlas.snapshot.revision, before.revision)
        XCTAssertEqual(atlas.snapshot.tiles.map(\.id), before.tiles.map(\.id))
        XCTAssertEqual(atlas.snapshot.totalBounds, before.totalBounds)
    }

    func testIncrementalInsertCanCancelInsidePixelWorkAndRollBack() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.white, size: CGSize(width: 256, height: 256))
        XCTAssertTrue(atlas.insert(
            observationID: 1,
            maskedImage: image,
            solveWidth: 256,
            cameraPosition: .zero
        ))
        let before = atlas.snapshot
        let linksBefore = atlas.retainedContributionTileLinkCount
        var cancellationChecks = 0

        XCTAssertFalse(atlas.insert(
            observationID: 2,
            maskedImage: image,
            solveWidth: 256,
            cameraPosition: .zero,
            shouldCancel: {
                cancellationChecks += 1
                return cancellationChecks >= 7
            }
        ))
        XCTAssertEqual(atlas.snapshot.revision, before.revision)
        XCTAssertEqual(atlas.snapshot.tiles.map(\.id), before.tiles.map(\.id))
        XCTAssertEqual(atlas.retainedContributionTileLinkCount, linksBefore)

        XCTAssertTrue(atlas.insert(
            observationID: 2,
            maskedImage: image,
            solveWidth: 256,
            cameraPosition: .zero
        ))
    }

    func testIncrementalOpaqueBlackWinsWhileTransparentLeavesPriorPixel() throws {
        let atlas = LiveTiledAtlas(context: context)
        let green = try solid(.green, size: CGSize(width: 2, height: 1))
        let transparentThenBlack = try pixels([
            0, 0, 0, 0,
            0, 0, 0, 255
        ], width: 2, height: 1)
        XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: green, solveWidth: 2, cameraPosition: .zero))
        XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: transparentThenBlack, solveWidth: 2, cameraPosition: .zero))
        let tile = try XCTUnwrap(atlas.tiles.first)
        let left = try pixel(tile.image, x: 0, y: 0)
        let right = try pixel(tile.image, x: 1, y: 0)
        XCTAssertGreaterThan(left.1, 200)
        XCTAssertEqual(right.0, 0)
        XCTAssertEqual(right.1, 0)
        XCTAssertEqual(right.2, 0)
        XCTAssertGreaterThan(right.3, 200)
    }

    func testPreserveBrighterRejectsDarkOverwriteIncrementallyAndOnRebuild() throws {
        let atlas = LiveTiledAtlas(
            context: context,
            pixelPolicy: .preserveBrighter
        )
        let green = try solid(.green, size: CGSize(width: 1, height: 1))
        let black = try solid(.black, size: CGSize(width: 1, height: 1))
        let white = try solid(.white, size: CGSize(width: 1, height: 1))
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: green,
            solveWidth: 1, cameraPosition: .zero
        ))
        XCTAssertTrue(atlas.insert(
            observationID: 2, maskedImage: black,
            solveWidth: 1, cameraPosition: .zero
        ))
        var value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 0, y: 0)
        XCTAssertGreaterThan(value.1, 200)

        // Replacing an existing observation forces a full tile rebuild.
        XCTAssertTrue(atlas.insert(
            observationID: 2, maskedImage: black,
            solveWidth: 1, cameraPosition: .zero
        ))
        value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 0, y: 0)
        XCTAssertGreaterThan(value.1, 200)

        XCTAssertTrue(atlas.insert(
            observationID: 3, maskedImage: white,
            solveWidth: 1, cameraPosition: .zero
        ))
        value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 0, y: 0)
        XCTAssertGreaterThan(value.0, 240)
        XCTAssertGreaterThan(value.1, 240)
        XCTAssertGreaterThan(value.2, 240)
    }

    func testNewestQuiltedOverwritesInteriorAndFollowsLowErrorEdgeSeam() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .newestQuilted)
        let width = 64
        let height = 64
        var reference = [UInt8](repeating: 0, count: width * height * 4)
        for topY in 0..<height {
            let seamX = 2 + (topY % 3)
            for x in 0..<width {
                let index = (topY * width + x) * 4
                let component: UInt8 = x == seamX ? 255 : 0
                reference[index] = component
                reference[index + 1] = component
                reference[index + 2] = component
                reference[index + 3] = 255
            }
        }
        let initial = try pixels(reference, width: width, height: height)
        let newest = try solidRGBA(255, 255, 255, size: CGSize(width: width, height: height))
        XCTAssertTrue(atlas.insert(
            observationID: 0,
            maskedImage: initial,
            solveWidth: CGFloat(width),
            cameraPosition: .zero
        ))
        XCTAssertTrue(atlas.insert(
            observationID: 1,
            maskedImage: newest,
            solveWidth: CGFloat(width),
            cameraPosition: .zero
        ))
        let tile = try XCTUnwrap(atlas.tiles.first)
        XCTAssertEqual(try pixel(tile.image, x: 32, y: 32).0, 255)
        XCTAssertEqual(try pixel(tile.image, x: 0, y: 32).0, 0)
        XCTAssertEqual(try pixel(tile.image, x: 7, y: 32).0, 255)
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 0)
    }

    func testNewestQuiltedIncrementalOutputMatchesCrossTileRebuild() throws {
        let incremental = LiveTiledAtlas(context: context, pixelPolicy: .newestQuilted)
        let rebuilt = LiveTiledAtlas(context: context, pixelPolicy: .newestQuilted)
        let width = 300
        var images = [CGImage]()
        for frame in 0..<4 {
            images.append(try texturedImage(
                width: width,
                height: 64,
                brightnessOffset: frame * 6,
                particle: frame == 3 ? CGPoint(x: 255, y: 31) : nil
            ))
        }
        for atlas in [incremental, rebuilt] {
            for frame in images.indices {
                XCTAssertTrue(atlas.insert(
                    observationID: frame,
                    maskedImage: images[frame],
                    solveWidth: CGFloat(width),
                    cameraPosition: .zero
                ))
            }
        }
        XCTAssertTrue(rebuilt.insert(
            observationID: 3,
            maskedImage: images[3],
            solveWidth: CGFloat(width),
            cameraPosition: .zero
        ))
        XCTAssertEqual(incremental.tiles.map(\.worldBounds), rebuilt.tiles.map(\.worldBounds))
        XCTAssertEqual(
            try incremental.tiles.map { try tileBytes($0.image) },
            try rebuilt.tiles.map { try tileBytes($0.image) }
        )
    }

    func testTemporalAgreementRemovesInitialBrightAttackAfterThreeBackgroundSamples() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let attack = try solidRGBA(255, 255, 255, size: CGSize(width: 3, height: 3))
        let backgrounds = try [10, 12, 14].map {
            try solidRGBA($0, $0 + 1, $0 + 2, size: CGSize(width: 3, height: 3))
        }
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: attack, solveWidth: 3,
            cameraPosition: .zero, timestamp: 0
        ))
        for (offset, image) in backgrounds.enumerated() {
            XCTAssertTrue(atlas.insert(
                observationID: offset + 2, maskedImage: image, solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(offset + 1) * 0.1
            ))
        }
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertLessThan(value.0, 30)
        XCTAssertLessThan(value.1, 30)
        XCTAssertLessThan(value.2, 30)
    }

    func testTemporalAgreementRejectsOneDarkOccluderAndKeepsStaticWhiteScenery() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let white = try solidRGBA(245, 246, 247, size: CGSize(width: 3, height: 3))
        let black = try solidRGBA(0, 0, 0, size: CGSize(width: 3, height: 3))
        for id in 1...3 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: white, solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        XCTAssertTrue(atlas.insert(
            observationID: 4, maskedImage: black, solveWidth: 3,
            cameraPosition: .zero, timestamp: 0.4
        ))
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertGreaterThan(value.0, 240)
        XCTAssertGreaterThan(value.1, 240)
        XCTAssertGreaterThan(value.2, 240)
    }

    func testTemporalAgreementLetsStableDarkSceneryReplaceBrightProvisionalPixels() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let white = try solidRGBA(255, 255, 255, size: CGSize(width: 3, height: 3))
        let dark = try solidRGBA(18, 20, 22, size: CGSize(width: 3, height: 3))
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: white, solveWidth: 3,
            cameraPosition: .zero, timestamp: 0
        ))
        for id in 2...4 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: dark, solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertEqual(value.0, 18)
        XCTAssertEqual(value.1, 20)
        XCTAssertEqual(value.2, 22)
    }

    func testTemporalAgreementSelectsAnObservedMedoidInsteadOfAveraging() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        for (id, component) in [10, 20, 30].enumerated() {
            let image = try solidRGBA(
                component, component, component,
                size: CGSize(width: 3, height: 3)
            )
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: image, solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertEqual(value.0, 20)
        XCTAssertEqual(value.1, 20)
        XCTAssertEqual(value.2, 20)
    }

    func testTemporalAgreementCountsDuplicateCaptureOnlyOnce() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let white = try solidRGBA(255, 255, 255, size: CGSize(width: 3, height: 3))
        let dark = try solidRGBA(16, 16, 16, size: CGSize(width: 3, height: 3))
        XCTAssertTrue(atlas.insert(
            observationID: 1, maskedImage: white, solveWidth: 3,
            cameraPosition: .zero, captureIdentity: 100, timestamp: 0
        ))
        let revision = atlas.revision
        XCTAssertTrue(atlas.insert(
            observationID: 2, maskedImage: dark, solveWidth: 3,
            cameraPosition: .zero, captureIdentity: 100, timestamp: 0
        ))
        XCTAssertEqual(atlas.revision, revision)
        for id in 3...4 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: dark, solveWidth: 3,
                cameraPosition: .zero, captureIdentity: Int64(id),
                timestamp: Double(id) * 0.1
            ))
        }
        var value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertGreaterThan(value.0, 240)
        XCTAssertTrue(atlas.insert(
            observationID: 5, maskedImage: dark, solveWidth: 3,
            cameraPosition: .zero, captureIdentity: 5, timestamp: 0.5
        ))
        value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 1, y: 1)
        XCTAssertEqual(value.0, 16)
    }

    func testTemporalAgreementWindowIsBoundedAndClearsAcrossDiscontinuity() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let image = try solidRGBA(40, 50, 60, size: CGSize(width: 4, height: 4))
        for id in 0..<10 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: image, solveWidth: 4,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 7)
        XCTAssertLessThanOrEqual(atlas.temporalDiagnostics.retainedDecodedBytes, 7 * 4 * 4 * 4)
        XCTAssertTrue(atlas.insert(
            observationID: 10, maskedImage: image, solveWidth: 4,
            cameraPosition: .zero, timestamp: 3
        ))
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 1)
        atlas.endTemporalWindow()
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 0)
        XCTAssertEqual(atlas.temporalDiagnostics.retainedDecodedBytes, 0)
    }

    func testTemporalAgreementAlignsMovingFramesInWorldCoordinates() throws {
        let atlas = LiveTiledAtlas(
            context: context,
            anchorPosition: .zero,
            pixelPolicy: .temporalAgreement
        )
        let colors: [(UInt8, UInt8, UInt8)] = [
            (20, 30, 40), (50, 60, 70), (80, 90, 100),
            (110, 120, 130), (140, 150, 160), (170, 180, 190),
            (200, 210, 220),
        ]
        for frame in 0..<4 {
            var row = Array(colors[frame..<(frame + 4)])
            if frame == 1 {
                row[2] = (255, 255, 255) // One-frame attack at world x=3.
            }
            let image = try threeRowImage(row)
            XCTAssertTrue(atlas.insert(
                observationID: frame,
                maskedImage: image,
                solveWidth: 4,
                cameraPosition: CGPoint(x: frame, y: 0),
                timestamp: Double(frame) * 0.1
            ))
        }
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 3, y: 1)
        XCTAssertEqual(value.0, 110)
        XCTAssertEqual(value.1, 120)
        XCTAssertEqual(value.2, 130)
    }

    func testTemporalAgreementCancellationRollsBackWindowAndPublishedPixels() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let first = try solidRGBA(30, 40, 50, size: CGSize(width: 64, height: 64))
        let second = try solidRGBA(80, 90, 100, size: CGSize(width: 64, height: 64))
        for id in 0..<7 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: first, solveWidth: 64,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        let before = atlas.snapshot
        let diagnostics = atlas.temporalDiagnostics
        var checks = 0
        XCTAssertFalse(atlas.insert(
            observationID: 7,
            maskedImage: second,
            solveWidth: 64,
            cameraPosition: .zero,
            timestamp: 0.7,
            shouldCancel: {
                checks += 1
                return checks >= 12
            }
        ))
        XCTAssertEqual(atlas.snapshot.revision, before.revision)
        XCTAssertEqual(
            try tileBytes(XCTUnwrap(atlas.tiles.first).image),
            try tileBytes(XCTUnwrap(before.tiles.first).image)
        )
        XCTAssertEqual(atlas.temporalDiagnostics, diagnostics)
        XCTAssertTrue(atlas.insert(
            observationID: 7, maskedImage: second, solveWidth: 64,
            cameraPosition: .zero, timestamp: 0.7
        ))
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 7)
        let value = try pixel(XCTUnwrap(atlas.tiles.first).image, x: 32, y: 32)
        XCTAssertEqual(value.0, 30)
        XCTAssertEqual(value.1, 40)
        XCTAssertEqual(value.2, 50)
    }

    func testTemporalAgreementTreatsTransparentPixelsAsNoEvidenceAndBlackAsScenery() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let green = try solidRGBA(0, 180, 0, size: CGSize(width: 3, height: 3))
        var blackCenter = [UInt8](repeating: 0, count: 3 * 3 * 4)
        let center = (1 * 3 + 1) * 4
        blackCenter[center + 3] = 255
        let masked = try pixels(blackCenter, width: 3, height: 3)
        XCTAssertTrue(atlas.insert(
            observationID: 0, maskedImage: green, solveWidth: 3,
            cameraPosition: .zero, timestamp: 0
        ))
        for id in 1...3 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: masked, solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(id) * 0.1
            ))
        }
        let tile = try XCTUnwrap(atlas.tiles.first)
        let untouched = try pixel(tile.image, x: 0, y: 0)
        let acceptedBlack = try pixel(tile.image, x: 1, y: 1)
        XCTAssertGreaterThan(untouched.1, 170)
        XCTAssertEqual(acceptedBlack.0, 0)
        XCTAssertEqual(acceptedBlack.1, 0)
        XCTAssertEqual(acceptedBlack.2, 0)
        XCTAssertEqual(acceptedBlack.3, 255)
    }

    func testTemporalAgreementUsesNeighborhoodAcrossTileBoundary() throws {
        let atlas = LiveTiledAtlas(
            context: context,
            anchorPosition: .zero,
            pixelPolicy: .temporalAgreement
        )
        let bright = try solidRGBA(255, 255, 255, size: CGSize(width: 3, height: 3))
        let dark = try solidRGBA(24, 25, 26, size: CGSize(width: 3, height: 3))
        XCTAssertTrue(atlas.insert(
            observationID: 0, maskedImage: bright, solveWidth: 3,
            cameraPosition: CGPoint(x: 255, y: 0), timestamp: 0
        ))
        for id in 1...3 {
            XCTAssertTrue(atlas.insert(
                observationID: id, maskedImage: dark, solveWidth: 3,
                cameraPosition: CGPoint(x: 255, y: 0), timestamp: Double(id) * 0.1
            ))
        }
        let leftTile = try XCTUnwrap(atlas.tiles.first { $0.tileX == 0 })
        let rightTile = try XCTUnwrap(atlas.tiles.first { $0.tileX == 1 })
        XCTAssertEqual(try pixel(leftTile.image, x: 255, y: 1).0, 24)
        XCTAssertEqual(try pixel(rightTile.image, x: 0, y: 1).0, 24)
    }

    func testTemporalQuiltingAveragesTextureAndAttenuatesOneFrameParticle() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalQuilting)
        let width = 64
        let height = 64
        for frame in 0..<4 {
            let image = try texturedImage(
                width: width,
                height: height,
                brightnessOffset: frame * 8,
                particle: frame == 3 ? CGPoint(x: 20, y: 20) : nil
            )
            XCTAssertTrue(atlas.insert(
                observationID: frame,
                maskedImage: image,
                solveWidth: CGFloat(width),
                cameraPosition: .zero,
                timestamp: Double(frame) * 0.1
            ))
        }
        let tile = try XCTUnwrap(atlas.tiles.first)
        let clean = try pixel(tile.image, x: 10, y: 10)
        let baseClean = Int(texturedComponent(x: 10, y: 10, brightnessOffset: 0))
        let averageClean = UInt8(baseClean + 12)
        XCTAssertEqual(clean.0, averageClean)
        XCTAssertEqual(clean.1, averageClean)
        XCTAssertEqual(clean.2, averageClean)

        let repaired = try pixel(tile.image, x: 20, y: 20)
        let baseParticle = Int(texturedComponent(x: 20, y: 20, brightnessOffset: 0))
        let averageParticle = UInt8((baseParticle * 3 + 24 + 255 + 2) / 4)
        XCTAssertEqual(repaired.0, averageParticle)
        XCTAssertEqual(repaired.1, averageParticle)
        XCTAssertEqual(repaired.2, averageParticle)
        XCTAssertLessThan(repaired.0, 100)
    }

    func testTemporalQuiltingIncrementalOutputMatchesOrderedRebuild() throws {
        let incremental = LiveTiledAtlas(context: context, pixelPolicy: .temporalQuilting)
        let rebuilt = LiveTiledAtlas(context: context, pixelPolicy: .temporalQuilting)
        let width = 300 // Crosses the 256-pixel atlas tile boundary.
        var images = [CGImage]()
        for frame in 0..<4 {
            images.append(try texturedImage(
                width: width,
                height: 64,
                brightnessOffset: frame * 6,
                particle: frame == 3 ? CGPoint(x: 255, y: 31) : nil
            ))
        }
        for atlas in [incremental, rebuilt] {
            for frame in images.indices {
                XCTAssertTrue(atlas.insert(
                    observationID: frame,
                    maskedImage: images[frame],
                    solveWidth: CGFloat(width),
                    cameraPosition: .zero,
                    timestamp: Double(frame) * 0.1
                ))
            }
        }
        XCTAssertTrue(rebuilt.insert(
            observationID: 3,
            maskedImage: images[3],
            solveWidth: CGFloat(width),
            cameraPosition: .zero,
            timestamp: 0.3
        ))
        XCTAssertEqual(incremental.tiles.map(\.worldBounds), rebuilt.tiles.map(\.worldBounds))
        XCTAssertEqual(
            try incremental.tiles.map { try tileBytes($0.image) },
            try rebuilt.tiles.map { try tileBytes($0.image) }
        )
    }

    func testTemporalQuiltingWindowRemainsBounded() throws {
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalQuilting)
        for frame in 0..<10 {
            let image = try texturedImage(
                width: 64,
                height: 64,
                brightnessOffset: frame % 4
            )
            XCTAssertTrue(atlas.insert(
                observationID: frame,
                maskedImage: image,
                solveWidth: 64,
                cameraPosition: .zero,
                timestamp: Double(frame) * 0.1
            ))
        }
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 7)
        XCTAssertLessThanOrEqual(
            atlas.temporalDiagnostics.retainedDecodedBytes,
            7 * 64 * 64 * 4
        )
    }

    func testTemporalAgreementIncrementalOutputMatchesPoseCorrectedRebuild() throws {
        let images = try [25, 27, 255, 29].map {
            try solidRGBA($0, $0, $0, size: CGSize(width: 3, height: 3))
        }
        let incremental = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let corrected = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        for index in images.indices {
            let finalPosition = index == 0 ? CGPoint.zero : CGPoint(x: 1, y: 0)
            XCTAssertTrue(incremental.insert(
                observationID: index, maskedImage: images[index], solveWidth: 3,
                cameraPosition: finalPosition, timestamp: Double(index) * 0.1
            ))
            XCTAssertTrue(corrected.insert(
                observationID: index, maskedImage: images[index], solveWidth: 3,
                cameraPosition: .zero, timestamp: Double(index) * 0.1
            ))
        }
        let beforeCorrection = corrected.revision
        let positions = Dictionary(uniqueKeysWithValues: images.indices.map {
            ($0, $0 == 0 ? CGPoint.zero : CGPoint(x: 1, y: 0))
        })
        XCTAssertTrue(corrected.replaceCameraPositions(
            positions,
            baseRevision: beforeCorrection
        ))
        XCTAssertEqual(incremental.tiles.map(\.worldBounds), corrected.tiles.map(\.worldBounds))
        XCTAssertEqual(
            try incremental.tiles.map { try tileBytes($0.image) },
            try corrected.tiles.map { try tileBytes($0.image) }
        )
    }

    func testTemporalAgreementFullResolutionFiveRunBenchmark() throws {
        guard ProcessInfo.processInfo.environment["HKV_RUN_ATLAS_BENCHMARKS"] == "1" else {
            throw XCTSkip("Full-resolution atlas benchmark is opt-in")
        }
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalAgreement)
        let image = try solidRGBA(32, 48, 64, size: CGSize(width: 640, height: 360))
        var timings = [Double]()
        for id in 0..<7 {
            XCTAssertTrue(atlas.insert(
                observationID: id,
                maskedImage: image,
                solveWidth: 640,
                cameraPosition: .zero,
                timestamp: Double(id) / 7.5
            ))
            if id >= 2 {
                timings.append(atlas.temporalDiagnostics.lastFilterMilliseconds)
            }
        }
        XCTAssertEqual(timings.count, 5)
        XCTAssertLessThan(
            timings.max() ?? .infinity,
            133,
            "temporal filtering must stay inside the 7.5 fps atlas interval: \(timings)"
        )
        XCTAssertLessThanOrEqual(
            atlas.temporalDiagnostics.retainedDecodedBytes,
            7 * 640 * 360 * 4
        )
        print("temporalBenchmark640x360 runs=5 ms=\(timings)")
    }

    func testTemporalQuiltingFullResolutionFiveRunBenchmark() throws {
        guard ProcessInfo.processInfo.environment["HKV_RUN_QUILT_BENCHMARKS"] == "1" else {
            throw XCTSkip("Full-resolution quilting benchmark is opt-in")
        }
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .temporalQuilting)
        var timings = [Double]()
        for frame in 0..<7 {
            let image = try texturedImage(
                width: 640,
                height: 360,
                brightnessOffset: frame % 4,
                particle: frame == 4 ? CGPoint(x: 320, y: 180) : nil
            )
            XCTAssertTrue(atlas.insert(
                observationID: frame,
                maskedImage: image,
                solveWidth: 640,
                cameraPosition: .zero,
                timestamp: Double(frame) / 7.5
            ))
            if frame >= 2 {
                timings.append(atlas.temporalDiagnostics.lastFilterMilliseconds)
            }
        }
        XCTAssertEqual(timings.count, 5)
        XCTAssertLessThan(
            timings.max() ?? .infinity,
            133,
            "Hacker quilting must stay inside the 7.5 fps atlas interval: \(timings)"
        )
        XCTAssertLessThanOrEqual(
            atlas.temporalDiagnostics.retainedDecodedBytes,
            7 * 640 * 360 * 4
        )
        print("quiltBenchmark640x360 runs=5 ms=\(timings)")
    }

    func testNewestQuiltedFullResolutionFiveRunBenchmark() throws {
        guard ProcessInfo.processInfo.environment["HKV_RUN_QUILT_BENCHMARKS"] == "1" else {
            throw XCTSkip("Full-resolution newest-quilt benchmark is opt-in")
        }
        let atlas = LiveTiledAtlas(context: context, pixelPolicy: .newestQuilted)
        var timings = [Double]()
        for frame in 0..<6 {
            let image = try texturedImage(
                width: 640,
                height: 360,
                brightnessOffset: frame % 4,
                particle: frame == 4 ? CGPoint(x: 320, y: 180) : nil
            )
            let started = ProcessInfo.processInfo.systemUptime
            XCTAssertTrue(atlas.insert(
                observationID: frame,
                maskedImage: image,
                solveWidth: 640,
                cameraPosition: .zero
            ))
            if frame > 0 {
                timings.append((ProcessInfo.processInfo.systemUptime - started) * 1_000)
            }
        }
        XCTAssertEqual(timings.count, 5)
        XCTAssertLessThan(
            timings.max() ?? .infinity,
            133,
            "newest quilting must stay inside the 7.5 fps atlas interval: \(timings)"
        )
        XCTAssertEqual(atlas.temporalDiagnostics.retainedFrameCount, 0)
        print("newestQuiltBenchmark640x360 runs=5 ms=\(timings)")
    }

    func testTemporalAgreementAgainstSavedFullResolutionEvidence() throws {
        guard ProcessInfo.processInfo.environment["HKV_RUN_SAVED_ATLAS_EVALUATION"] == "1",
              let rootPath = ProcessInfo.processInfo.environment["HKV_SAVED_WORLD_ROOT"]
        else {
            throw XCTSkip("Saved full-resolution atlas evaluation is opt-in")
        }
        let store = try SceneSessionStore(
            rootURL: URL(fileURLWithPath: rootPath, isDirectory: true)
        )
        let observations = store.manifest.observations
        var selected = [StoredFrameObservation]()
        for end in observations.indices.reversed() {
            let newest = observations[end]
            var candidate = [StoredFrameObservation]()
            var index = end
            while candidate.count < 7 {
                let observation = observations[index]
                if observation.roomID == newest.roomID,
                   observation.visitID == newest.visitID,
                   newest.timestamp - observation.timestamp <= 1 {
                    candidate.append(observation)
                }
                if index == observations.startIndex { break }
                index = observations.index(before: index)
            }
            if candidate.count == 7 {
                selected = candidate.reversed()
                break
            }
        }
        guard selected.count == 7 else {
            throw XCTSkip("No seven-frame, one-second saved evidence window")
        }
        let brighter = LiveTiledAtlas(
            context: context,
            anchorPosition: selected[0].cameraPosition,
            pixelPolicy: .preserveBrighter
        )
        let temporal = LiveTiledAtlas(
            context: context,
            anchorPosition: selected[0].cameraPosition,
            pixelPolicy: .temporalAgreement
        )
        for observation in selected {
            let image = try store.source(for: observation)
            XCTAssertTrue(brighter.insert(
                observationID: observation.id,
                maskedImage: image,
                solveWidth: CGFloat(observation.solveWidth),
                cameraPosition: observation.cameraPosition
            ))
            XCTAssertTrue(temporal.insert(
                observationID: observation.id,
                maskedImage: image,
                solveWidth: CGFloat(observation.solveWidth),
                cameraPosition: observation.cameraPosition,
                captureIdentity: Int64(observation.id),
                timestamp: observation.timestamp
            ))
        }
        let brighterTiles = Dictionary(uniqueKeysWithValues: brighter.tiles.map { ($0.id, $0) })
        var comparedPixels = 0
        var suppressedBrightOutliers = 0
        var acceptedDarkerCorrections = 0
        for temporalTile in temporal.tiles {
            guard let brighterTile = brighterTiles[temporalTile.id] else { continue }
            let temporalBytes = try tileBytes(temporalTile.image)
            let brighterBytes = try tileBytes(brighterTile.image)
            for index in stride(from: 0, to: temporalBytes.count, by: 4) {
                guard temporalBytes[index + 3] > 0, brighterBytes[index + 3] > 0 else {
                    continue
                }
                comparedPixels += 1
                let temporalPeak = max(
                    temporalBytes[index],
                    max(temporalBytes[index + 1], temporalBytes[index + 2])
                )
                let brighterPeak = max(
                    brighterBytes[index],
                    max(brighterBytes[index + 1], brighterBytes[index + 2])
                )
                if Int(brighterPeak) - Int(temporalPeak) > 24 {
                    suppressedBrightOutliers += 1
                }
                if temporalPeak < brighterPeak { acceptedDarkerCorrections += 1 }
            }
        }
        XCTAssertGreaterThan(comparedPixels, 0)
        XCTAssertGreaterThan(suppressedBrightOutliers, 0)
        XCTAssertGreaterThan(acceptedDarkerCorrections, 0)
        print(
            "savedEvidenceComparison frames=7 pixels=\(comparedPixels) "
                + "suppressedBright=\(suppressedBrightOutliers) "
                + "darkerCorrections=\(acceptedDarkerCorrections)"
        )
    }

    func testIncrementalInsertionMatchesFullRebuild() throws {
        let first = try pixels([
            255, 0, 0, 255,
            0, 0, 0, 0
        ], width: 2, height: 1)
        let second = try pixels([
            0, 0, 0, 0,
            0, 0, 255, 255
        ], width: 2, height: 1)
        let newest = try pixels([
            0, 255, 0, 255,
            0, 0, 0, 0
        ], width: 2, height: 1)
        let incremental = LiveTiledAtlas(context: context)
        let rebuilt = LiveTiledAtlas(context: context)
        for atlas in [incremental, rebuilt] {
            XCTAssertTrue(atlas.insert(observationID: 1, maskedImage: first, solveWidth: 2, cameraPosition: .zero))
            XCTAssertTrue(atlas.insert(observationID: 2, maskedImage: second, solveWidth: 2, cameraPosition: .zero))
            XCTAssertTrue(atlas.insert(observationID: 3, maskedImage: newest, solveWidth: 2, cameraPosition: .zero))
        }
        // Replacing the newest source with identical evidence forces the
        // rebuild path without changing the latest-valid-pixel result.
        XCTAssertTrue(rebuilt.insert(observationID: 3, maskedImage: newest, solveWidth: 2, cameraPosition: .zero))

        let incrementalTile = try XCTUnwrap(incremental.tiles.first)
        let rebuiltTile = try XCTUnwrap(rebuilt.tiles.first)
        XCTAssertEqual(incrementalTile.id, rebuiltTile.id)
        XCTAssertEqual(incrementalTile.worldBounds, rebuiltTile.worldBounds)
        XCTAssertEqual(incrementalTile.contributionIDs, rebuiltTile.contributionIDs)
        XCTAssertEqual(try tileBytes(incrementalTile.image), try tileBytes(rebuiltTile.image))
    }

    func testUniqueInsertRasterizesOnlyNewSourcePerDirtyTile() throws {
        let atlas = LiveTiledAtlas(context: context)
        let image = try solid(.red, size: CGSize(width: 8, height: 8))
        for id in 0..<24 {
            XCTAssertTrue(atlas.insert(observationID: id, maskedImage: image, solveWidth: 8, cameraPosition: .zero))
            // All observations overlap one tile.  A full replay would grow
            // with `id`; the incremental publication always paints one source.
            XCTAssertEqual(atlas.lastPublicationRasterizedContributionCount, 1)
        }
        XCTAssertEqual(try XCTUnwrap(atlas.tiles.first).contributionIDs.count, 24)
    }

    private func solid(_ color: CIColor, size: CGSize) throws -> CGImage {
        try XCTUnwrap(context.createCGImage(CIImage(color: color).cropped(to: CGRect(origin: .zero, size: size)), from: CGRect(origin: .zero, size: size)))
    }

    private func pixels(_ values: [UInt8], width: Int, height: Int) throws -> CGImage {
        let data = Data(values)
        let provider = try XCTUnwrap(CGDataProvider(data: data as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue), provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent))
    }

    private func solidRGBA(
        _ red: Int,
        _ green: Int,
        _ blue: Int,
        size: CGSize
    ) throws -> CGImage {
        let width = Int(size.width)
        let height = Int(size.height)
        let pixel = [UInt8(red), UInt8(green), UInt8(blue), 255]
        return try pixels(
            Array(repeating: pixel, count: width * height).flatMap { $0 },
            width: width,
            height: height
        )
    }

    private func threeRowImage(
        _ row: [(UInt8, UInt8, UInt8)]
    ) throws -> CGImage {
        var rgba = [UInt8]()
        rgba.reserveCapacity(row.count * 4)
        for color in row {
            rgba.append(contentsOf: [color.0, color.1, color.2, UInt8(255)])
        }
        return try pixels(rgba + rgba + rgba, width: row.count, height: 3)
    }

    private func texturedImage(
        width: Int,
        height: Int,
        brightnessOffset: Int,
        particle: CGPoint? = nil
    ) throws -> CGImage {
        var rgba = [UInt8]()
        rgba.reserveCapacity(width * height * 4)
        for topY in 0..<height {
            let worldY = height - 1 - topY
            for x in 0..<width {
                let isParticle = particle.map {
                    abs(x - Int($0.x)) <= 1 && abs(worldY - Int($0.y)) <= 1
                } ?? false
                let component = isParticle
                    ? UInt8(255)
                    : texturedComponent(x: x, y: worldY, brightnessOffset: brightnessOffset)
                rgba.append(contentsOf: [component, component, component, 255])
            }
        }
        return try pixels(rgba, width: width, height: height)
    }

    private func texturedComponent(
        x: Int,
        y: Int,
        brightnessOffset: Int
    ) -> UInt8 {
        UInt8(30 + (x * 7 + y * 11) % 40 + brightnessOffset)
    }

    private func pixel(_ image: CGImage, x: Int, y: Int) throws -> (UInt8, UInt8, UInt8, UInt8) {
        let bytesPerRow = image.width * 4
        var values = [UInt8](repeating: 0, count: bytesPerRow * image.height)
        let bitmap = try XCTUnwrap(CGContext(data: &values, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: bytesPerRow, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue))
        bitmap.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        let index = ((image.height - 1 - y) * image.width + x) * 4
        return (values[index], values[index + 1], values[index + 2], values[index + 3])
    }

    private func tileBytes(_ image: CGImage) throws -> [UInt8] {
        let bytesPerRow = image.width * 4
        var values = [UInt8](repeating: 0, count: bytesPerRow * image.height)
        let bitmap = try XCTUnwrap(CGContext(data: &values, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: bytesPerRow, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue))
        bitmap.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return values
    }
}
