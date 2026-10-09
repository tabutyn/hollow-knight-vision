import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class LiveDashboardViewTests: XCTestCase {
    func testHackerInteractionModesFormOneOrderedControlGroup() {
        XCTAssertEqual(HackerInteractionMode.allCases.map(\.rawValue), [
            "Ground", "Rooms", "Fit", "Free Fly", "Current",
        ])
        XCTAssertEqual(HackerInteractionMode.ground.framingMode, .freeFly)
        XCTAssertEqual(HackerInteractionMode.rooms.framingMode, .freeFly)
        XCTAssertEqual(HackerInteractionMode.fit.framingMode, .fit)
        XCTAssertEqual(HackerInteractionMode.freeFly.framingMode, .freeFly)
        XCTAssertEqual(HackerInteractionMode.current.framingMode, .current)
    }

    func testModelWorkspaceSelectionRetainsTabAndRowsAcrossViewRecreation() {
        let object = UUID()
        let screenshot = UUID()
        let contract = UUID()
        let failure = UUID()
        let selection = LabelingModelWorkspaceSelection(
            tab: .screenshots,
            objectExampleIdentifier: object,
            screenshotIdentifier: screenshot,
            screenshotGroup: LabelingContext.brightness.storageIdentifier,
            contractExampleIdentifier: contract,
            summaryReviewClassIdentifier: "enemies.crawlid",
            summaryFailureIdentifier: failure
        )

        XCTAssertEqual(selection.tab, .screenshots)
        XCTAssertEqual(selection.objectExampleIdentifier, object)
        XCTAssertEqual(selection.screenshotIdentifier, screenshot)
        XCTAssertEqual(selection.screenshotGroup, "brightness")
        XCTAssertEqual(selection.contractExampleIdentifier, contract)
        XCTAssertEqual(selection.summaryReviewClassIdentifier, "enemies.crawlid")
        XCTAssertEqual(selection.summaryFailureIdentifier, failure)
    }

    func testDashboardWorkspacesUseFirstLetterShortcuts() {
        XCTAssertEqual(DashboardWorkspace.allCases.map(\.rawValue), [
            "Gameplay", "Label", "Model", "Hacker", "Ops",
        ])
        XCTAssertEqual(DashboardWorkspace.allCases.map(\.shortcutCharacter), [
            "g", "l", "m", "h", "o",
        ])
        XCTAssertFalse(DashboardWorkspace.gameplay.supportsGameplayExitShortcut)
        XCTAssertTrue(DashboardWorkspace.label.supportsGameplayExitShortcut)
        XCTAssertTrue(DashboardWorkspace.model.supportsGameplayExitShortcut)
        XCTAssertFalse(DashboardWorkspace.hacker.supportsGameplayExitShortcut)
        XCTAssertFalse(DashboardWorkspace.ops.supportsGameplayExitShortcut)
    }

    func testPathRecordingShortcutIsAvailableInGameplayAndHacker() {
        XCTAssertTrue(PathRecordingPresentationPolicy.showsControl(
            workspace: .gameplay,
            recognizedContext: "Gameplay"
        ))
        XCTAssertFalse(PathRecordingPresentationPolicy.showsControl(
            workspace: .gameplay,
            recognizedContext: "Main Title"
        ))
        XCTAssertTrue(PathRecordingPresentationPolicy.showsControl(
            workspace: .hacker,
            recognizedContext: nil
        ))
        XCTAssertFalse(PathRecordingPresentationPolicy.showsControl(
            workspace: .ops,
            recognizedContext: "Gameplay"
        ))
    }

    func testXExitCommandIsScopedToUnmodifiedLabelAndModelPresses() {
        for workspace in [DashboardWorkspace.label, .model] {
            XCTAssertTrue(WorkspaceExitShortcutPolicy.shouldHandle(
                workspace: workspace,
                eventType: .keyDown,
                keyCode: 7,
                modifierFlags: [],
                isRepeat: false
            ))
        }
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(
            workspace: .gameplay,
            eventType: .keyDown,
            keyCode: 7,
            modifierFlags: [],
            isRepeat: false
        ))
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(
            workspace: .model,
            eventType: .keyDown,
            keyCode: 7,
            modifierFlags: .command,
            isRepeat: false
        ))
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(
            workspace: .label,
            eventType: .keyUp,
            keyCode: 7,
            modifierFlags: [],
            isRepeat: false
        ))
    }

    func testMenuObjectParticlesSpawnAtBottomAndRecycleUpward() throws {
        let size = CGSize(width: 600, height: 400)
        XCTAssertNil(MenuObjectParticleMotion.placement(
            elapsed: 0,
            index: 1,
            count: 4,
            in: size
        ))

        let spawned = try XCTUnwrap(MenuObjectParticleMotion.placement(
            elapsed: 0,
            index: 0,
            count: 4,
            in: size
        ))
        XCTAssertEqual(spawned.position.y, 435, accuracy: 0.001)
        XCTAssertEqual(spawned.opacity, 0, accuracy: 0.001)

        let duration = MenuObjectParticleMotion.travelDuration(for: 0)
        let halfway = try XCTUnwrap(MenuObjectParticleMotion.placement(
            elapsed: duration / 2,
            index: 0,
            count: 4,
            in: size
        ))
        XCTAssertEqual(halfway.position.y, 200, accuracy: 0.001)
        XCTAssertEqual(
            halfway.opacity,
            0.10 + Double(MenuObjectParticleMotion.depth(for: 0)) * 0.18,
            accuracy: 0.001
        )

        let fadingAtTop = try XCTUnwrap(MenuObjectParticleMotion.placement(
            elapsed: duration * 0.95,
            index: 0,
            count: MenuObjectParticleMotion.defaultParticleCount,
            in: size
        ))
        XCTAssertLessThan(fadingAtTop.position.y, 0)
        XCTAssertLessThan(fadingAtTop.opacity, 0.1)

        let emitterPositions = (0..<MenuObjectParticleMotion.defaultParticleCount).compactMap {
            index in
            MenuObjectParticleMotion.placement(
                elapsed: Double(index) * MenuObjectParticleMotion.spawnInterval,
                index: index,
                count: MenuObjectParticleMotion.defaultParticleCount,
                in: size
            )?.position.x
        }
        XCTAssertLessThan(try XCTUnwrap(emitterPositions.min()), 100)
        XCTAssertGreaterThan(try XCTUnwrap(emitterPositions.max()), 500)
        XCTAssertTrue(emitterPositions.allSatisfy { $0 < 150 || $0 > 450 })

        let renderOrder = MenuObjectParticleMotion.renderOrder(
            count: MenuObjectParticleMotion.defaultParticleCount
        )
        let backIndex = try XCTUnwrap(renderOrder.first)
        let frontIndex = try XCTUnwrap(renderOrder.last)
        XCTAssertLessThan(
            MenuObjectParticleMotion.depth(for: backIndex),
            MenuObjectParticleMotion.depth(for: frontIndex)
        )
        XCTAssertLessThan(
            MenuObjectParticleMotion.baseScale(for: backIndex),
            MenuObjectParticleMotion.baseScale(for: frontIndex)
        )
        XCTAssertGreaterThan(
            MenuObjectParticleMotion.travelDuration(for: backIndex),
            MenuObjectParticleMotion.travelDuration(for: frontIndex)
        )

        let recycled = try XCTUnwrap(MenuObjectParticleMotion.placement(
            elapsed: duration,
            index: 0,
            count: 4,
            in: size
        ))
        XCTAssertEqual(recycled.position.y, spawned.position.y, accuracy: 0.001)
        XCTAssertEqual(recycled.opacity, 0, accuracy: 0.001)
    }

    func testGroundTrackingDebugToolsRequireGameplayInsideGameplayWorkspace() {
        XCTAssertTrue(DebugViewAvailabilityPolicy.showsGroundTrackingTools(
            workspace: .gameplay,
            recognizedContext: "Gameplay"
        ))
        XCTAssertFalse(DebugViewAvailabilityPolicy.showsGroundTrackingTools(
            workspace: .gameplay,
            recognizedContext: "Main Title"
        ))
        XCTAssertFalse(DebugViewAvailabilityPolicy.showsGroundTrackingTools(
            workspace: .label,
            recognizedContext: "Gameplay"
        ))
    }

    func testAtlasIsHiddenUntilGameplayStarts() {
        XCTAssertFalse(LiveAtlasPresentationPolicy.includesAtlas(hasGameplayStarted: false))
        XCTAssertTrue(LiveAtlasPresentationPolicy.includesAtlas(hasGameplayStarted: true))
    }

    func testBottomStatusReportsOnlyRecognizedContext() {
        XCTAssertEqual(LiveGameStatusPresentationPolicy.text(
            recognizedContext: "Select Profile - 1"
        ), "Select Profile - 1")
        XCTAssertEqual(LiveGameStatusPresentationPolicy.text(
            recognizedContext: nil
        ), "Finding State")
        XCTAssertEqual(LiveGameStatusPresentationPolicy.text(
            recognizedContext: "Gameplay", roomStatus: "Room 2"
        ), "Gameplay · Room 2")
    }

    func testObjectColorsAreStableUniqueAndCanonicalized() {
        let mana = LabelingVisualIdentity.rgb(for: "game.mana")
        XCTAssertEqual(mana.red, LabelingVisualIdentity.rgb(for: "game.mana").red)
        XCTAssertNotEqual(mana.red, LabelingVisualIdentity.rgb(for: "game.health").red)
        XCTAssertEqual(
            LabelingVisualIdentity.rgb(for: "main-title.options").red,
            LabelingVisualIdentity.rgb(for: LabelingClassIdentity.options).red
        )
    }

    func testTrainNotificationTracksLabelsNewerThanDataset() {
        let datasetDate = Date(timeIntervalSince1970: 100)
        XCTAssertFalse(LabelingTrainingNotificationPolicy.hasUntrainedChanges(
            exampleModificationDates: [], latestDatasetCreatedAt: nil
        ))
        XCTAssertTrue(LabelingTrainingNotificationPolicy.hasUntrainedChanges(
            exampleModificationDates: [Date(timeIntervalSince1970: 90)],
            latestDatasetCreatedAt: nil
        ))
        XCTAssertFalse(LabelingTrainingNotificationPolicy.hasUntrainedChanges(
            exampleModificationDates: [Date(timeIntervalSince1970: 90)],
            latestDatasetCreatedAt: datasetDate
        ))
        XCTAssertTrue(LabelingTrainingNotificationPolicy.hasUntrainedChanges(
            exampleModificationDates: [Date(timeIntervalSince1970: 110)],
            latestDatasetCreatedAt: datasetDate
        ))
        XCTAssertEqual(LabelingTrainingNotificationPolicy.untrainedImageCount(
            exampleModificationDates: [
                Date(timeIntervalSince1970: 90),
                Date(timeIntervalSince1970: 110),
                Date(timeIntervalSince1970: 120),
            ],
            latestDatasetCreatedAt: datasetDate
        ), 2)
        XCTAssertEqual(LabelingTrainingNotificationPolicy.untrainedImageCount(
            exampleModificationDates: [
                Date(timeIntervalSince1970: 90),
                Date(timeIntervalSince1970: 110),
            ],
            latestDatasetCreatedAt: nil
        ), 2)
    }

    func testGroundHypothesesProduceOnlyGroundReviewGeometry() {
        var tracking = GroundHypothesisTrackingResult(
            features: [
                GroundHypothesisFeature(
                    id: 12,
                    segmentID: 7,
                    sequenceIndex: 3,
                    imageRect: CGRect(x: 20, y: 32, width: 16, height: 12),
                    classification: .occluded,
                    motionResidual: nil,
                    photometricError: nil
                )
            ],
            cameraTranslation: nil,
            inlierCount: 0,
            residualRMS: nil,
            globalFeatureCount: 1,
            globalMatchCount: 0,
            globalCameraPosition: nil,
            globalCorrection: nil,
            verticalLineCorrection: nil,
            groundSegmentCount: 1,
            atlasFeatures: [
                GroundHypothesisAtlasFeature(
                    segmentID: 7,
                    sequenceIndex: 3,
                    atlasPosition: CGPoint(x: 24, y: 40),
                    referencePixels: [UInt8](
                        repeating: 91,
                        count: GroundHypothesisTracker.featurePixelCount
                    )
                )
            ],
            atlasLines: [
                GroundHypothesisAtlasLine(
                    segmentID: 7,
                    atlasStart: CGPoint(x: 16, y: 48),
                    atlasEnd: CGPoint(x: 48, y: 48)
                )
            ]
        )
        tracking.lineReviews = [GroundLinePresenceReview(
            id: 7,
            line: CleanFloorLine(row: 40, xRange: 20...51),
            state: .confirmed,
            visibleSeconds: 3,
            detectedFraction: 0.9
        )]

        let overlay = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking,
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360)
        )

        XCTAssertEqual(overlay.markers.count, 2)
        let atlasMarker = overlay.markers[0]
        let liveMarker = overlay.markers[1]
        XCTAssertEqual(atlasMarker.kind, .groundFeature)
        XCTAssertEqual(atlasMarker.worldPosition, CGPoint(x: 24, y: 40))
        XCTAssertEqual(atlasMarker.worldSize, CGSize(width: 16, height: 12))
        XCTAssertEqual(atlasMarker.groundFeatureDetails?.segmentID, 7)
        XCTAssertEqual(atlasMarker.groundFeatureDetails?.sequenceIndex, 3)
        XCTAssertEqual(
            atlasMarker.groundFeatureDetails?.referencePixels.count,
            GroundHypothesisTracker.featurePixelCount
        )
        XCTAssertTrue(atlasMarker.isVisible)
        XCTAssertEqual(atlasMarker.coordinateSpace, .atlas)
        XCTAssertEqual(liveMarker.worldPosition, CGPoint(x: 128, y: 522))
        XCTAssertTrue(liveMarker.isVisible)
        XCTAssertEqual(liveMarker.kind, .occludedGroundFeature)
        XCTAssertEqual(liveMarker.coordinateSpace, .live)
        XCTAssertEqual(overlay.lines.count, 2)
        let liveLine = overlay.lines.first { $0.coordinateSpace == .live }
        XCTAssertEqual(liveLine?.kind, .currentGround)
        XCTAssertEqual(liveLine?.start, CGPoint(x: 120, y: 519.5))
        XCTAssertEqual(liveLine?.end, CGPoint(x: 152, y: 519.5))
        let atlasLine = overlay.lines.first { $0.coordinateSpace == .atlas }
        XCTAssertEqual(atlasLine?.kind, .persistentGround)
        XCTAssertEqual(atlasLine?.start, CGPoint(x: 16, y: 48))
        XCTAssertEqual(atlasLine?.end, CGPoint(x: 48, y: 48))

        let selectedOverlay = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking,
            selectedGroundFeature: atlasMarker.groundFeatureDetails,
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360)
        )
        let liveHighlight = selectedOverlay.markers.first {
            $0.kind == .selectedGroundFeature && $0.coordinateSpace == .live
        }
        let atlasHighlight = selectedOverlay.markers.first {
            $0.kind == .selectedGroundFeature && $0.coordinateSpace == .atlas
        }
        XCTAssertEqual(liveHighlight?.coordinateSpace, .live)
        XCTAssertEqual(liveHighlight?.worldPosition, CGPoint(x: 128, y: 522))
        XCTAssertTrue(liveHighlight?.isVisible == true)
        XCTAssertEqual(atlasHighlight?.worldPosition, CGPoint(x: 24, y: 40))
        XCTAssertTrue(atlasHighlight?.isVisible == true)
    }

    func testPersistentCleanLineStaysAtFixedAtlasCoordinates() {
        func tracking(start: CGPoint, end: CGPoint) -> GroundHypothesisTrackingResult {
            GroundHypothesisTrackingResult(
                features: [], cameraTranslation: nil, inlierCount: 0, residualRMS: nil,
                globalFeatureCount: 0, globalMatchCount: 0, globalCameraPosition: nil,
                globalCorrection: nil, verticalLineCorrection: nil, groundSegmentCount: 1,
                atlasFeatures: [],
                atlasLines: [.init(
                    segmentID: 4, atlasStart: start, atlasEnd: end
                )]
            )
        }
        let first = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking(
                start: CGPoint(x: 16, y: 48), end: CGPoint(x: 48, y: 48)
            ),
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360)
        )
        let moved = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking(
                start: CGPoint(x: 16, y: 48), end: CGPoint(x: 48, y: 48)
            ),
            liveBounds: CGRect(x: 132, y: 190, width: 640, height: 360)
        )

        XCTAssertEqual(first.lines.first?.start, moved.lines.first?.start)
        XCTAssertEqual(first.lines.first?.end, moved.lines.first?.end)
    }

    func testTransitionPortalProducesTwoDoorsAndConnector() {
        let overlay = LiveFeatureReviewOverlay.make(
            transitionPortals: [VisualRoomPortal(
                id: 4,
                leftRoomID: 1,
                rightRoomID: 0,
                leftDoorWorldX: -140,
                rightDoorWorldX: -120,
                leftDoorYRange: 168...268,
                rightDoorYRange: 158...278
            )],
            liveBounds: .zero
        )

        XCTAssertEqual(overlay.markers.count, 2)
        XCTAssertTrue(overlay.markers.allSatisfy { $0.kind == .transitionDoor })
        XCTAssertEqual(overlay.markers.map(\.worldPosition.x), [-140, -120])
        XCTAssertEqual(overlay.lines.count, 1)
        XCTAssertEqual(overlay.lines[0].kind, .roomTransition)
        XCTAssertEqual(overlay.lines[0].start.x, -140)
        XCTAssertEqual(overlay.lines[0].end.x, -120)
        XCTAssertEqual(overlay.lines[0].start.y, 218)
        XCTAssertEqual(overlay.lines[0].end.y, 218)
    }

    func testTileUUIDRetainsHighBitsOfTileID() {
        let low = 1
        let high = Int(bitPattern: (UInt(1) << 48) | UInt(low))

        XCTAssertNotEqual(DashboardView.tileUUID(low), DashboardView.tileUUID(high))
        XCTAssertNotEqual(DashboardView.tileUUID(-1), DashboardView.tileUUID(low))
    }
}
