import AppKit
import XCTest
@testable import HollowKnightVision

final class LiveDashboardViewTests: XCTestCase {
    func testRoomResetRequiresOneUnmodifiedRKeyDown() throws {
        func matches(_ letter: String = "r", type: NSEvent.EventType = .keyDown,
                     modifiers: NSEvent.ModifierFlags = [], repeatKey: Bool = false) throws -> Bool {
            let event = try XCTUnwrap(NSEvent.keyEvent(with: type, location: .zero, modifierFlags: modifiers,
                timestamp: 1, windowNumber: 0, context: nil, characters: letter,
                charactersIgnoringModifiers: letter, isARepeat: repeatKey, keyCode: 15))
            return RoomResetShortcut.matches(event)
        }
        XCTAssertTrue(try matches())
        XCTAssertTrue(try matches("R", modifiers: .capsLock))
        XCTAssertFalse(try matches("k"))
        XCTAssertFalse(try matches(type: .keyUp))
        XCTAssertFalse(try matches(repeatKey: true))
        for modifier: NSEvent.ModifierFlags in [.command, .option, .control, .shift] {
            XCTAssertFalse(try matches(modifiers: modifier))
        }
    }
    func testObjectiveAndPathAreSeparateHackerEditingTargets() {
        XCTAssertEqual(HackerEditingTarget.defaultTarget, .objective)
        XCTAssertTrue(HackerEditingTarget.door.editsTerrain)
        XCTAssertFalse(HackerEditingTarget.objective.editsTerrain)
        XCTAssertEqual(HackerEditingTarget.allCases.filter(\.editsPaths), [.path, .objective])
        XCTAssertEqual(AtlasPathConnectionStore.Tool.allCases, [.jump, .doubleJump, .delete])
    }

    func testPathOverlayKeepsAuthoredWallsDoorsAndSpikes() {
        let kinds: [LayerSceneFeatureReviewLineKind] = [.positiveGroundTruth, .doorGroundTruth, .spikeGroundTruth]
        let terrain = LayerSceneFeatureReviewOverlay(markers: [], lines: kinds.map {
            .init(start: .zero, end: CGPoint(x: 10, y: 10), kind: $0)
        })
        let result = HackerTerrainOverlay.paths(terrain: terrain, computed: [], selection: [])
        XCTAssertEqual(result.lines.map(\.kind), kinds)
    }

    func testEstimatedRoomOutlinesCanBeHiddenWithoutHidingMeasuredDoorways() {
        let overlay = LayerSceneFeatureReviewOverlay(markers: [], lines: [
            .init(start: .zero, end: CGPoint(x: 10, y: 0), kind: .provisionalIsland),
            .init(start: .zero, end: CGPoint(x: 0, y: 10), kind: .doorwayFloor)
        ])
        let hidden = LiveIslandOverlayGeometry.visibleOverlay(overlay,
            showsEstimatedPlacement: false, hasGameplayStarted: true)
        XCTAssertEqual(hidden.lines.map(\.kind), [.doorwayFloor])
        XCTAssertEqual(LiveIslandOverlayGeometry.visibleOverlay(overlay,
            showsEstimatedPlacement: true, hasGameplayStarted: true).lines.map(\.kind),
            [.provisionalIsland, .doorwayFloor])
        XCTAssertTrue(LiveIslandOverlayGeometry.visibleOverlay(overlay,
            showsEstimatedPlacement: true, hasGameplayStarted: false).lines.isEmpty)
    }

    func testBottomBarControlsAreScopedToGameplayAndModdedGameState() {
        XCTAssertEqual(DashboardControlVisibility.workspaces, [.gameplay, .label, .stencils, .model, .bot, .hacker])
        XCTAssertFalse(DashboardControlVisibility.shortcutWorkspaces.contains(.bot), "B must remain bot start/stop")
        for workspace in DashboardWorkspace.allCases {
            for complete in [false, true] {
                for context in [nil, "Main Title", "Gameplay"] as [String?] {
                    for hackerGameplay in [false, true] {
                        XCTAssertEqual(DashboardControlVisibility.showsFraming(workspace: workspace,
                            phaseOneComplete: complete, recognizedContext: context, hackerGameplayFrame: hackerGameplay),
                            workspace == .hacker ? hackerGameplay : (workspace == .gameplay && complete && context == "Gameplay"))
                    }
                }
            }
            for mode in [HollowKnightRuntimeMode.normal, .modded] {
                for context in [nil, "Main Title", "Gameplay"] as [String?] {
                    XCTAssertEqual(DashboardControlVisibility.showsOps(workspace: workspace, mode: mode, recognizedContext: context),
                                   workspace == .gameplay && mode == .modded && context == "Gameplay")
                }
            }
        }
    }

    func testModelWorkspaceSelectionRetainsTabAndRowsAcrossViewRecreation() {
        let object = UUID()
        let screenshot = UUID()
        let failure = UUID()
        let selection = LabelingModelWorkspaceSelection(
            tab: .screenshots,
            objectExampleIdentifier: object,
            screenshotIdentifier: screenshot,
            screenshotGroup: LabelingContext.brightness.storageIdentifier,
            summaryReviewClassIdentifier: "enemies.crawlid",
            summaryFailureIdentifier: failure
        )

        XCTAssertEqual(selection.tab, .screenshots)
        XCTAssertEqual(selection.objectExampleIdentifier, object)
        XCTAssertEqual(selection.screenshotIdentifier, screenshot)
        XCTAssertEqual(selection.screenshotGroup, "brightness")
        XCTAssertEqual(selection.summaryReviewClassIdentifier, "enemies.crawlid")
        XCTAssertEqual(selection.summaryFailureIdentifier, failure)
    }

    func testDashboardWorkspacesUseFirstLetterShortcuts() {
        XCTAssertEqual(DashboardWorkspace.allCases.map(\.rawValue), [
            "Gameplay", "Label", "Stencil", "Model", "Bot", "Hacker", "Ops",
        ])
        XCTAssertEqual(DashboardWorkspace.allCases.map(\.shortcutCharacter), [
            "g", "l", "s", "m", "b", "h", "o",
        ])
        XCTAssertFalse(DashboardWorkspace.gameplay.supportsGameplayExitShortcut)
        XCTAssertTrue(DashboardWorkspace.label.supportsGameplayExitShortcut)
        XCTAssertTrue(DashboardWorkspace.model.supportsGameplayExitShortcut)
        XCTAssertTrue(DashboardWorkspace.bot.supportsGameplayExitShortcut)
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

    func testLExitsLabelDropdownButPreservesTextEntryAndOtherWorkspaces() {
        XCTAssertTrue(WorkspaceExitShortcutPolicy.shouldHandle(workspace: .label, eventType: .keyDown,
            keyCode: 37, modifierFlags: [], isRepeat: false))
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(workspace: .model, eventType: .keyDown,
            keyCode: 37, modifierFlags: [], isRepeat: false))
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(workspace: .label, eventType: .keyDown,
            keyCode: 37, modifierFlags: [], isRepeat: false, isEditingText: true))
        XCTAssertFalse(WorkspaceExitShortcutPolicy.shouldHandle(workspace: .label, eventType: .keyDown,
            keyCode: 37, modifierFlags: .command, isRepeat: false))
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

    func testPathsFollowCurrentGameStateAndDebugSetting() {
        let contexts: [LabelingContext?] = [nil, .game, .mainTitle, .selectProfile, nil, .game]
        XCTAssertEqual(contexts.map {
            LiveAtlasPresentationPolicy.showsPaths(enabled: true, context: $0, presentedGameplayFrame: true)
        }, [false, true, false, false, false, true])
        for context in LabelingContext.allCases {
            XCTAssertFalse(LiveAtlasPresentationPolicy.showsPaths(enabled: false, context: context,
                presentedGameplayFrame: true))
            XCTAssertFalse(LiveAtlasPresentationPolicy.showsPaths(enabled: true, context: context,
                presentedGameplayFrame: false))
            XCTAssertEqual(LiveAtlasPresentationPolicy.showsPaths(enabled: true, context: context,
                presentedGameplayFrame: true), context == .game)
        }
    }

    func testPathsRequireGameplayEvidenceOnTheCurrentFrame() {
        struct Frame {
            var hasSceneSignal = true
            var recognized: LiveGameStatus.Context? = nil
        }
        let frames = [
            Frame(recognized: .gameplay),
            Frame(), // The status tracker still reports Gameplay during a cutscene.
            Frame(recognized: .labelSet(.pause)),
            Frame(recognized: .labelSet(.inventory)),
            Frame(hasSceneSignal: false, recognized: .gameplay),
            Frame(recognized: .gameplay),
        ]
        var evidence = LiveAtlasPathFrameEvidence()
        let visibility = frames.enumerated().map { index, frame in
            let currentFrame = LiveAtlasPresentationPolicy.admitsPathFrame(
                hasSceneSignal: frame.hasSceneSignal, recognizedContext: frame.recognized
            )
            let timestamp = TimeInterval(index)
            evidence.observe(gameplay: currentFrame, at: timestamp)
            return LiveAtlasPresentationPolicy.showsPaths(enabled: true, context: .game,
                presentedGameplayFrame: evidence.admits(timestamp))
        }
        XCTAssertEqual(visibility, [true, false, false, false, false, true])
    }

    func testPathsRequireEvidenceForTheExactPreviewFrame() {
        var evidence = LiveAtlasPathFrameEvidence()
        evidence.observe(gameplay: true, at: 10)
        evidence.observe(gameplay: true, at: 12)
        let frames: [TimeInterval?] = [nil, 9, 10, 11, 12, 13]
        XCTAssertEqual(frames.map { evidence.admits($0) }, [false, false, true, false, true, false])

        evidence.observe(gameplay: false, at: 13)
        evidence.observe(gameplay: true, at: 12) // Late completion from before the menu.
        XCTAssertEqual(frames.map { evidence.admits($0) }, Array(repeating: false, count: frames.count))
        evidence.observe(gameplay: true, at: 14)
        XCTAssertTrue(evidence.admits(14))
    }

    func testHackerPathSurvivesMissedVisualRecognitionUntilPauseOrBlackout() {
        var evidence = LiveAtlasPathFrameEvidence()
        for index in 1...90 {
            let time = TimeInterval(index)
            let context: LiveGameStatus.Context? = index.isMultiple(of: 2) ? .gameplay : nil
            evidence.observe(gameplay: context != nil, at: time)
            let frame = HackerPathFrameState(hasSceneSignal: true, recognized: context)
            evidence.presentHackerFrame(at: time, gameplay: frame.admits(receiverGameplay: true))
            XCTAssertTrue(evidence.admitsHackerPreview)
            XCTAssertEqual(evidence.admits(time), context != nil)
        }
        evidence.observeReceiver(gameplay: false, at: 91)
        XCTAssertFalse(evidence.admitsHackerPreview)
        evidence.observeReceiver(gameplay: true, at: 92)
        XCTAssertFalse(evidence.admitsHackerPreview)
        evidence.presentHackerFrame(at: 93, gameplay: true)
        XCTAssertTrue(evidence.admitsHackerPreview)
        evidence.resetVisualFrames()
        XCTAssertFalse(evidence.admitsHackerPreview)

        XCTAssertFalse(HackerPathFrameState(hasSceneSignal: false, recognized: .gameplay).admits(receiverGameplay: true))
        XCTAssertFalse(HackerPathFrameState(hasSceneSignal: true, recognized: .labelSet(.pause)).admits(receiverGameplay: true))
        XCTAssertFalse(HackerPathFrameState.gameplay.admits(receiverGameplay: false))
        XCTAssertTrue(HackerPathFrameState.gameplay.admits(receiverGameplay: nil))
        XCTAssertFalse(HackerPathFrameState.unrecognized.admits(receiverGameplay: nil))
    }

    func testHackerFitTracksMappedExtentThroughMovingPreviewsAndMapGrowth() {
        let fit = LiveAtlasPresentationPolicy.hackerFitBounds
        let preview = CGRect(x: 250, y: 40, width: 640, height: 360)
        let mapped = CGRect(x: 0, y: -130, width: 3196, height: 514)
        let path = CGRect(x: 200, y: 60, width: 3250, height: 90)
        XCTAssertNil(fit(nil, .zero, .null))
        XCTAssertEqual(fit(nil, preview, .null), preview)
        for x in stride(from: -800, through: 4000, by: 50) {
            XCTAssertEqual(fit(mapped, preview.offsetBy(dx: CGFloat(x), dy: 0), path), mapped.union(path))
        }
        let expanded = mapped.union(CGRect(x: 3800, y: 100, width: 100, height: 100))
        XCTAssertEqual(fit(expanded, preview, path), expanded)
    }

    func testPathsHideWhenTheModReportsAStateChangeDespiteVisualGameplayEvidence() {
        var evidence = LiveAtlasPathFrameEvidence()
        evidence.observe(gameplay: true, at: 10)
        evidence.observeReceiver(gameplay: true, at: 10)
        XCTAssertTrue(evidence.admits(10))

        evidence.observe(gameplay: true, at: 11)
        evidence.observeReceiver(gameplay: false, at: 11)
        XCTAssertFalse(evidence.admits(10)) // The Hacker preview can still be a frame behind.
        XCTAssertFalse(evidence.admits(11))
        evidence.observe(gameplay: true, at: 12)
        XCTAssertFalse(evidence.admits(12))

        evidence.observeReceiver(gameplay: true, at: 13)
        evidence.observe(gameplay: true, at: 13)
        evidence.observeReceiver(gameplay: false, at: 12) // Late menu telemetry.
        XCTAssertFalse(evidence.admits(12))
        XCTAssertTrue(evidence.admits(13))
    }

    func testReceiverMenuStateSurvivesCaptureRestartAndRequiresFreshFrameOnResume() {
        var evidence = LiveAtlasPathFrameEvidence()
        evidence.observe(gameplay: true, at: 10)
        XCTAssertTrue(evidence.admits(10))

        evidence.observeReceiver(gameplay: false, at: 11)
        XCTAssertFalse(evidence.admits(10))
        evidence.resetVisualFrames()
        evidence.observe(gameplay: true, at: 12)
        XCTAssertFalse(evidence.admits(12))

        evidence.observeReceiver(gameplay: true, at: 13)
        XCTAssertFalse(evidence.admits(12))
        evidence.observe(gameplay: true, at: 14)
        XCTAssertTrue(evidence.admits(14))
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

    func testGroundTrackingPatchesAreVisibleByDefaultAlongsideOneLiveFloorStroke() {
        var tracking = GroundHypothesisTrackingResult(
            features: [
                GroundHypothesisFeature(
                    id: 12,
                    segmentID: 7,
                    sequenceIndex: 3,
                    imageRect: CGRect(x: 20, y: 40, width: 16, height: 12),
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

        tracking.detectedFloorLines = [.init(row: 40, xRange: 20...51), .init(row: 41, xRange: 22...50)]
        let bounds = CGRect(x: 100, y: 200, width: 640, height: 360)
        let overlay = LiveFeatureReviewOverlay.make(groundHypotheses: tracking, liveBounds: bounds)
        let linesOnly = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking, showsTrackingPatches: false, liveBounds: bounds)
        XCTAssertTrue(linesOnly.markers.isEmpty)
        XCTAssertEqual(linesOnly.lines.map(\.start), overlay.lines.map(\.start))
        XCTAssertEqual(linesOnly.lines.map(\.end), overlay.lines.map(\.end))
        XCTAssertEqual(overlay.lines.filter { $0.kind == .currentGround }.count, 1)

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
        XCTAssertEqual(liveMarker.worldPosition, CGPoint(x: 128, y: 514))
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

        let owned = LiveFeatureReviewOverlay.make(groundHypotheses: tracking, showsTrackingPatches: true,
            persistentGroundLines: [GroundHypothesisAtlasLine(segmentID: 7,
                atlasStart: CGPoint(x: 116, y: 148), atlasEnd: CGPoint(x: 148, y: 148))],
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360))
        XCTAssertEqual(owned.markers.count, 1, "Raw persistent ground-bank markers must not survive a correction")
        XCTAssertEqual(owned.markers.first?.coordinateSpace, .live)
        XCTAssertEqual(owned.markers.first?.groundFeatureDetails?.worldPosition, CGPoint(x: 128, y: 514))
        XCTAssertEqual(owned.lines.first { $0.coordinateSpace == .atlas }?.start, CGPoint(x: 116, y: 148))
        XCTAssertFalse(owned.lines.contains { $0.start == CGPoint(x: 16, y: 48) })

        let selectedOverlay = LiveFeatureReviewOverlay.make(
            groundHypotheses: tracking,
            showsTrackingPatches: true, selectedGroundFeature: atlasMarker.groundFeatureDetails,
            liveBounds: CGRect(x: 100, y: 200, width: 640, height: 360)
        )
        let liveHighlight = selectedOverlay.markers.first {
            $0.kind == .selectedGroundFeature && $0.coordinateSpace == .live
        }
        let atlasHighlight = selectedOverlay.markers.first {
            $0.kind == .selectedGroundFeature && $0.coordinateSpace == .atlas
        }
        XCTAssertEqual(liveHighlight?.coordinateSpace, .live)
        XCTAssertEqual(liveHighlight?.worldPosition, CGPoint(x: 128, y: 514))
        XCTAssertTrue(liveHighlight?.isVisible == true)
        XCTAssertEqual(atlasHighlight?.worldPosition, CGPoint(x: 24, y: 40))
        XCTAssertTrue(atlasHighlight?.isVisible == true)
    }

    func testLivePatchBoxesFollowDetectedPlatformsAndRetireStaleRows() {
        func feature(_ id: Int, _ y: Int) -> GroundHypothesisFeature {
            .init(id: id, segmentID: id, sequenceIndex: 0, imageRect: CGRect(x: 16, y: y, width: 16, height: 12),
                classification: .groundPlane, motionResidual: 0, photometricError: 0)
        }
        let tracking = GroundHypothesisTrackingResult(features: [feature(1, 50), feature(2, 40)],
            cameraTranslation: nil, inlierCount: 0, residualRMS: nil, globalFeatureCount: 0, globalMatchCount: 0,
            globalCameraPosition: nil, globalCorrection: nil, verticalLineCorrection: nil, groundSegmentCount: 0,
            atlasFeatures: [], atlasLines: [], detectedFloorLines: [.init(row: 50, xRange: 8...120)],
            matchedPatchRects: [CGRect(x: 16, y: 50, width: 16, height: 12), CGRect(x: 32, y: 50, width: 16, height: 12)])
        let overlay = LiveFeatureReviewOverlay.make(groundHypotheses: tracking, liveBounds: CGRect(x: 0, y: 0, width: 160, height: 100))
        XCTAssertEqual(overlay.markers.map(\.worldPosition), [CGPoint(x: 24, y: 44), CGPoint(x: 40, y: 44)])
        XCTAssertEqual(overlay.lines.count, 1)
    }

    func testCurrentPlatformsHaveOneStrokeEachEvenWhenAtlasAndTrackingSpanTheGap() {
        var tracking = GroundHypothesisTrackingResult.empty
        tracking.detectedFloorLines = [.init(row: 20, xRange: 0...40), .init(row: 21, xRange: 34...59),
            .init(row: 20, xRange: 75...99)]
        tracking.lineReviews = [.init(id: 1, line: .init(row: 22, xRange: 0...99),
            state: .confirmed, visibleSeconds: 5, detectedFraction: 1)]
        tracking.atlasLines = [.init(segmentID: 1, atlasStart: CGPoint(x: -20, y: 81),
            atlasEnd: CGPoint(x: 120, y: 81))]
        let bounds = CGRect(x: 0, y: 0, width: 100, height: 100)
        let overlay = LiveFeatureReviewOverlay.make(groundHypotheses: tracking, liveBounds: bounds)
        XCTAssertTrue(overlay.markers.isEmpty)
        let live = overlay.lines.filter { $0.coordinateSpace == .live }
        XCTAssertEqual(live.map(\.start), [CGPoint(x: 0, y: 79.5), CGPoint(x: 75, y: 79.5)])
        XCTAssertEqual(live.map(\.end), [CGPoint(x: 60, y: 79.5), CGPoint(x: 100, y: 79.5)])
        let saved = LayerSceneFeatureReviewGeometry.visibleAtlasLines(overlay.lines, liveFrame: bounds,
            zoom: 2, livePan: CGPoint(x: 12, y: 2), atlasPan: CGPoint(x: 4, y: 2))
        XCTAssertEqual(saved.map(\.start.x), [-20, 104])
        XCTAssertEqual(saved.map(\.end.x), [4, 120])
        tracking.detectedFloorLines = []
        XCTAssertFalse(LiveFeatureReviewOverlay.make(groundHypotheses: tracking, liveBounds: bounds)
            .lines.contains { $0.coordinateSpace == .live })
    }

    func testWalkedFloorStaysInLiveFrameAndSuppressesOnlyCoincidentDetectorColumnsWhilePanning() {
        var tracking = GroundHypothesisTrackingResult.empty
        tracking.detectedFloorLines = [.init(row: 20, xRange: 10...95)]
        let walked = GroundHypothesisAtlasLine(segmentID: LiveGroundLineEvidence.walkedSegmentID,
            atlasStart: .init(x: 20, y: 79), atlasEnd: .init(x: 80, y: 79))
        let frame = CGRect(x: 0, y: 0, width: 100, height: 100)
        let lines = LiveFeatureReviewOverlay.make(groundHypotheses: tracking, persistentGroundLines: [walked],
            liveBounds: frame).lines
        let atlasPan = CGPoint(x: 8, y: 0)
        let visible = LayerSceneFeatureReviewGeometry.visibleAtlasLines(lines, liveFrame: frame,
            zoom: 2, livePan: .zero, atlasPan: atlasPan)
        XCTAssertEqual(visible.map(\.kind), [.walkedGround])
        XCTAssertEqual(visible.first?.start, walked.atlasStart)
        let live = LayerSceneFeatureReviewGeometry.visibleLiveLines(lines, liveFrame: frame,
            zoom: 2, livePan: .zero, atlasPan: atlasPan)
        XCTAssertEqual(live.map(\.start.x), [10, 84])
        XCTAssertEqual(live.map(\.end.x), [24, 96])
    }

    func testGroundPresentationOnRecordedDetectionsWhenRequested() throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_GROUND_DISPLAY_AUDIT"] else {
            throw XCTSkip("Set HKV_GROUND_DISPLAY_AUDIT to a detector replay report")
        }
        struct Detection: Decodable { var row: Int; var x0: Int; var x1: Int }
        struct Frame: Decodable { var width: Int; var height: Int; var semanticLines: [Detection] }
        struct Report: Decodable { var frames: [Frame] }
        let frames = try JSONDecoder().decode(Report.self, from: Data(contentsOf: URL(fileURLWithPath: path))).frames
        var detections = 0, strokes = 0, consolidatedFrames = 0
        for frame in frames {
            var tracking = GroundHypothesisTrackingResult.empty
            tracking.detectedFloorLines = frame.semanticLines.map { .init(row: $0.row, xRange: $0.x0...$0.x1) }
            let overlay = LiveFeatureReviewOverlay.make(groundHypotheses: tracking,
                liveBounds: CGRect(x: 0, y: 0, width: frame.width, height: frame.height))
            XCTAssertTrue(overlay.markers.isEmpty)
            XCTAssertTrue(overlay.lines.allSatisfy { $0.kind == .currentGround && $0.start.y == $0.end.y })
            // Every measured column still has a green stroke within the grouping tolerance.
            for floor in frame.semanticLines {
                let y = CGFloat(frame.height - floor.row) - 0.5
                XCTAssertTrue(overlay.lines.contains {
                    abs($0.start.y - y) <= 3 && $0.start.x <= CGFloat(floor.x0) && $0.end.x >= CGFloat(floor.x1 + 1)
                })
            }
            detections += frame.semanticLines.count
            strokes += overlay.lines.count
            if frame.semanticLines.count != overlay.lines.count { consolidatedFrames += 1 }
        }
        XCTAssertFalse(frames.isEmpty)
        print("Ground display replay: \(frames.count) frames, \(detections) detected spans, \(strokes) strokes, \(consolidatedFrames) frames with consolidation")
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
