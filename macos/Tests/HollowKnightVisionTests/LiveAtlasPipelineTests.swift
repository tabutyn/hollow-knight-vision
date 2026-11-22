import CoreImage
import CoreMedia
import XCTest
@testable import HollowKnightVision

final class LiveAtlasPipelineTests: XCTestCase {
    private enum PersistenceFailure: Error {
        case append
    }

    func testSweptKnightMaskCannotBecomeContactEvidenceAndMeasuredBoundsScaleToSolvePixels() throws {
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, worldRootURL: root)
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 320, height: 180), context: context)
        let mask = CGRect(x: 20, y: 30, width: 40, height: 50)
        let body = CGRect(x: 30, y: 35, width: 20, height: 30)
        for index in 0..<2 {
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame,
                regions: SceneRegions(knight: mask, health: .zero, geo: .zero, mana: .zero),
                cameraPosition: .zero, solveWidth: 640, timestamp: Double(index),
                observationPoseTimestamp: Double(index), hasGameplaySignal: true,
                submittedAt: Double(index), observedPlayerBounds: index == 0 ? nil : body))
        }
        let observations = pipeline.currentWorldSnapshot().observations
        XCTAssertEqual(observations.count, 2)
        XCTAssertNil(observations[0].observedPlayerBounds)
        XCTAssertEqual(observations[1].observedPlayerBounds, CGRect(x: 60, y: 70, width: 40, height: 60))
    }

    func testDoorwayFloorCommitFailureRollsBackAndNextCapturePublishesAlignedWorld() throws {
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let context = CIContext(options: [.useSoftwareRenderer: true])
        var attempts = 0
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, worldRootURL: root,
            persistentCommitter: { store, snapshot, expected, next in
                attempts += 1
                if attempts == 6 { return false }
                return try store.commit(snapshot, expectedRevision: expected, newRevision: next)
            })
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 320, height: 180), context: context)
        let transition = LiveDoorwayTransition(sourceRoomID: 0, targetRoomID: 1,
            departureTimestamp: 0.45, arrivalTimestamp: 2, direction: .right)
        var updates = [LiveWorldPipelineUpdate]()
        pipeline.setWorldUpdateHandler { updates.append($0) }
        for (index, time) in [0.0, 0.2, 0.4, 2, 2.2, 2.4, 2.5].enumerated() {
            let room = index < 3 ? 0 : 1
            let pose = CGPoint(x: room * 400, y: room == 0 ? 0 : -8)
            let capture = LiveIslandCapture(id: room, roomID: room, startedAt: Double(room) * 2,
                worldFromLocal: pose, localPose: .zero, placement: room == 0 ? .reference : .estimated,
                uncertainty: room == 0 ? .reference : .unknown, breakReason: "cut", measured: true)
            let groundY: CGFloat = room == 0 ? 20 : 48
            let regions = SceneRegions(knight: CGRect(x: room == 0 ? 278 : 24,
                y: groundY, width: 20, height: 20), health: .zero, geo: .zero, mana: .zero)
            let ground = LiveWorldGroundGeometry(lines: [.init(segmentID: room,
                start: CGPoint(x: 0, y: groundY), end: CGPoint(x: 320, y: groundY))])
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
                cameraPosition: pose, solveWidth: 320, timestamp: time,
                observationPoseTimestamp: time, hasGameplaySignal: true, captureGeneration: 1,
                worldTimestamp: time + 100, submittedAt: time, groundAnchoredPose: true,
                roomID: room, islandCapture: capture, groundGeometry: ground,
                observedPlayerBounds: regions.knight,
                doorwayTransition: room == 0 ? nil : transition))
            if index == 5 {
                XCTAssertEqual(updates.count, 5)
                XCTAssertTrue(pipeline.currentWorldSnapshot().doorwayFloorConstraints.isEmpty)
                XCTAssertEqual(pipeline.currentWorldSnapshot().observations.last?.optimizedPose.y, -8)
            }
        }
        let result = try XCTUnwrap(updates.last)
        XCTAssertEqual(result.snapshot.observations.count, 6)
        for observation in result.snapshot.observations {
            XCTAssertEqual(try XCTUnwrap(observation.captureTimestamp),
                observation.timestamp - 100, accuracy: 1e-8)
        }
        XCTAssertEqual(result.snapshot.doorwayFloorConstraints.count, 1)
        XCTAssertEqual(result.snapshot.observations.filter { $0.roomID == 1 }.map(\.optimizedPose.y), [-28, -28, -28])
        XCTAssertEqual(result.snapshot.observations.filter { $0.roomID == 1 }.map(\.rawPose.y), [-8, -8, -8])
        XCTAssertTrue(result.snapshot.ownedGroundLines.allSatisfy { $0.start.y == 20 })
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot, result.snapshot)
        let output = try XCTUnwrap(pipeline.process(frame: frame,
            regions: SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero),
            cameraPosition: .zero, solveWidth: 320, timestamp: 3, hasGameplaySignal: true,
            allowAtlasWrite: false))
        XCTAssertEqual(output.worldRevision, result.worldRevision)
        XCTAssertFalse(result.atlasTiles.isEmpty)
    }

    func testGroundVerifiedClosurePersistsAndMovesAllIslandPixelsTogether() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, worldRootURL: root)
        pipeline.setIslandClosureSearchAllowed { true }
        let frame = try closureTexture()
        let geometry = LiveWorldGroundGeometry(lines: [LiveGroundLineEvidence(segmentID: 0,
            start: CGPoint(x: 0, y: 30), end: CGPoint(x: 320, y: 30))])
        var updates = [LiveWorldPipelineUpdate]()
        pipeline.setWorldUpdateHandler { updates.append($0) }
        for index in 0..<3 {
            let pose = CGPoint(x: index == 0 ? 0 : 8, y: 0)
            let capture = LiveIslandCapture(id: index == 0 ? 0 : 1, roomID: 0,
                startedAt: index == 0 ? 0 : 1, worldFromLocal: pose, localPose: .zero,
                placement: index == 0 ? .reference : .estimated,
                uncertainty: index == 0 ? .reference : .unknown, breakReason: "gap", measured: true)
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame,
                regions: SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero),
                cameraPosition: pose, solveWidth: 320, timestamp: Double(index),
                observationPoseTimestamp: Double(index), hasGameplaySignal: true,
                captureGeneration: 1, submittedAt: Double(index),
                groundAnchoredPose: true,
                alignmentExclusions: [CGRect(x: 0, y: 40, width: 320, height: 140)],
                islandCapture: capture, groundGeometry: geometry))
        }
        let result = try XCTUnwrap(updates.last)
        XCTAssertNotNil(result.acceptedClosure)
        XCTAssertEqual(result.snapshot.observations.count, 3)
        for observation in result.snapshot.observations {
            XCTAssertEqual(observation.optimizedPose.x, 0, accuracy: 0.01)
            XCTAssertEqual(observation.optimizedPose.y, 0, accuracy: 0.01)
        }
        XCTAssertEqual(result.snapshot.observations.map(\.rawPose.x), [0, 8, 8])
        let disk = try LiveWorldSessionStore(rootURL: root).load().snapshot
        XCTAssertEqual(disk, result.snapshot)
        XCTAssertEqual(disk.ownedGroundLines.map(\.start.y), [30, 30, 30])
        XCTAssertFalse(result.atlasTiles.isEmpty)
        XCTAssertNil(pipeline.worldPersistenceIssue)
    }

    func testInconclusiveEarlyProbesDoNotCancelOrPostponeScheduledConfirmation() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let pipeline = LiveAtlasPipeline(context: CIContext(options: [.useSoftwareRenderer: true]),
            atlasFramesPerSecond: 60, runsIntegrationSynchronously: true, worldRootURL: root)
        pipeline.setIslandClosureSearchAllowed { true }
        let frame = try closureTexture()
        let sourceGround = LiveWorldGroundGeometry(lines: [.init(segmentID: 0,
            start: CGPoint(x: 0, y: 30), end: CGPoint(x: 320, y: 30))])
        let unavailableFloor = LiveObservedFloorCandidates(lines: [.init(segmentID: 0,
            start: CGPoint(x: 0, y: 60), end: CGPoint(x: 320, y: 60))])
        var updates = [LiveWorldPipelineUpdate]()
        pipeline.setWorldUpdateHandler { updates.append($0) }
        for (index, time) in [0.0, 1, 1.15, 1.3, 1.5].enumerated() {
            let pose = CGPoint(x: index == 0 ? 0 : 8, y: 0)
            let capture = LiveIslandCapture(id: index == 0 ? 0 : 1, roomID: 0,
                startedAt: index == 0 ? 0 : 1, worldFromLocal: pose, localPose: .zero,
                placement: index == 0 ? .reference : .estimated,
                uncertainty: index == 0 ? .reference : .unknown, breakReason: "gap", measured: true)
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame,
                regions: SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero),
                cameraPosition: pose, solveWidth: 320, timestamp: time,
                observationPoseTimestamp: time, hasGameplaySignal: true,
                captureGeneration: 1, submittedAt: time, groundAnchoredPose: true,
                islandCapture: capture, groundGeometry: index == 0 ? sourceGround : nil,
                observedFloorCandidates: [2, 3].contains(index) ? unavailableFloor
                    : LiveObservedFloorCandidates(lines: sourceGround.lines)))
            if index < 4 { XCTAssertTrue(pipeline.currentWorldSnapshot().loopClosureEdges.isEmpty) }
        }
        let result = try XCTUnwrap(updates.last)
        XCTAssertNotNil(result.acceptedClosure)
        XCTAssertEqual(result.snapshot.observations.count, 5)
        XCTAssertTrue(result.snapshot.observations.allSatisfy { abs($0.optimizedPose.x) < 0.01 })
        XCTAssertEqual(result.snapshot.ownedGroundLines.count, 1)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot, result.snapshot)
    }

    private func closureTexture() throws -> CGImage {
        let width = 320, height = 180
        var bytes = [UInt8](repeating: 0, count: width * height)
        for y in 0..<height {
            for x in 0..<width {
                var value = UInt64(x / 4 + 1000) &* 0x9e3779b185ebca87
                value ^= UInt64(y / 4 + 1000) &* 0xc2b2ae3d27d4eb4f
                value ^= value >> 33; value = value &* 0xff51afd7ed558ccd
                bytes[y * width + x] = UInt8(truncatingIfNeeded: value >> 24)
            }
        }
        let provider = try XCTUnwrap(CGDataProvider(data: Data(bytes) as CFData))
        return try XCTUnwrap(CGImage(width: width, height: height, bitsPerComponent: 8,
            bitsPerPixel: 8, bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGBitmapInfo(rawValue: 0), provider: provider, decode: nil,
            shouldInterpolate: false, intent: .defaultIntent))
    }

    func testLiveRoomBoundaryAuditWhenRequested() throws {
        guard ProcessInfo.processInfo.environment["HKV_LIVE_ROOM_AUDIT"] == "1" else {
            throw XCTSkip("Live room audit is opt-in")
        }
        let root = LiveWorldSessionStore.defaultRootURL()
        let state = try LiveWorldSessionStore(rootURL: root).load()
        XCTAssertEqual(Set(state.snapshot.observations.map(\.roomID)), Set([0, 1]))
        let pipeline = LiveAtlasPipeline(
            context: CIContext(options: [.useSoftwareRenderer: true]),
            runsIntegrationSynchronously: true,
            worldRootURL: root,
            restoresPersistedWorldOnInitialization: false
        )
        XCTAssertNil(pipeline.worldPersistenceIssue)
        let evidence = try XCTUnwrap(pipeline.boundaryEvidence(
            leftRoomID: 1, rightRoomID: 0
        ))
        XCTAssertNotNil(evidence.rightRoomLeftBand)
        XCTAssertNotNil(evidence.leftRoomRightBand)
    }

    func testGroundAnchoredObservationKeepsCapturePoseAcrossLaterBasisRevision() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true
        )
        let basis = WorldBasisController()
        let generation = basis.beginCapture(worldFromLocal: CGPoint(x: 100, y: 50))
        pipeline.setWorldPoseResolver { basis.rebasedObservation(for: $0) }
        var updates = [LiveWorldPipelineUpdate]()
        pipeline.setWorldUpdateHandler { updates.append($0) }
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let frame = try image(color: .green, extent: extent, context: context)
        let regions = SceneRegions(
            knight: nil, health: .zero, geo: .zero, mana: .zero
        )

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 40,
            timestamp: 0,
            observationPoseTimestamp: 0,
            hasGameplaySignal: true,
            captureGeneration: generation,
            submittedAt: 0
        ))
        let ticket = try XCTUnwrap(basis.observe(
            localPose: CGPoint(x: 20, y: 10),
            captureTimestamp: 1
        ))
        XCTAssertEqual(basis.rebasedWorldPose(for: ticket), CGPoint(x: 120, y: 60))
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame,
            regions: regions,
            cameraPosition: CGPoint(x: 20, y: 10),
            solveWidth: 40,
            timestamp: 1,
            observationPoseTimestamp: 1,
            hasGameplaySignal: true,
            captureGeneration: generation,
            submittedAt: 1,
            basisTicket: ticket,
            groundAnchoredPose: true
        ))

        let second = try XCTUnwrap(updates.last?.snapshot.observations.last)
        XCTAssertEqual(second.rawPose, CGPoint(x: 20, y: 10))
        XCTAssertEqual(second.localPose, CGPoint(x: 20, y: 10))

        let published = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
            cameraPosition: CGPoint(x: 20, y: 10), solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true, allowAtlasWrite: false))
        XCTAssertEqual(published.worldRevision, updates.last?.snapshot.mapRevision)
        XCTAssertFalse(published.atlasTiles.isEmpty)
    }

    func testCadencePresentsAtCaptureRateAndWritesAtlasAtFiveFPS() {
        var cadence = LiveAtlasCadence(
            presentationFramesPerSecond: 15,
            atlasFramesPerSecond: 5
        )

        XCTAssertEqual(cadence.decision(at: 0, canIntegrate: true).present, true)
        XCTAssertEqual(cadence.decision(at: 0.03, canIntegrate: true).present, false)
        let nextPresentation = cadence.decision(at: 0.07, canIntegrate: true)
        XCTAssertTrue(nextPresentation.present)
        XCTAssertFalse(nextPresentation.integrate)
        let nextAtlasWrite = cadence.decision(at: 0.21, canIntegrate: true)
        XCTAssertTrue(nextAtlasWrite.present)
        XCTAssertTrue(nextAtlasWrite.integrate)
    }

    func testCompositionRebuildPublishesItsNewOriginWithAtlasPixels() throws {
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, worldRootURL: root)
        XCTAssertTrue(pipeline.reset(anchorPosition: .zero))
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 80, height: 50), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        let pose = CGPoint(x: 0, y: 49)
        XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
            cameraPosition: pose, solveWidth: 80, timestamp: 1,
            observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1))
        let before = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
            cameraPosition: pose, solveWidth: 80, timestamp: 1, hasGameplaySignal: true))
        XCTAssertEqual(before.liveBounds.minY, 49)
        XCTAssertEqual(try XCTUnwrap(before.atlas).contentBounds, before.liveBounds)

        pipeline.setRoomCompositionBoundsProvider { _ in .unbounded }
        pipeline.drainVisualReplay()
        let after = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
            cameraPosition: pose, solveWidth: 80, timestamp: 2, hasGameplaySignal: true))
        XCTAssertEqual(after.anchorPosition, pose)
        XCTAssertEqual(after.liveBounds, CGRect(x: 0, y: 0, width: 80, height: 50))
        let atlas = try XCTUnwrap(after.atlas)
        XCTAssertEqual(atlas.contentBounds, after.liveBounds)
        XCTAssertGreaterThan(rgba(in: atlas.image, at: CGPoint(x: 10, y: 10), context: context).red, 180)
    }

    func testFirstFailedSubmissionDoesNotOffsetLiveFrameFromCommittedAtlas() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        var cancel = true
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, compositionShouldCancel: { cancel })
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 80, height: 50), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
            cameraPosition: .zero, solveWidth: 80, timestamp: 0,
            observationPoseTimestamp: 0, hasGameplaySignal: true, submittedAt: 0))
        XCTAssertTrue(pipeline.currentWorldSnapshot().observations.isEmpty)
        cancel = false
        let committedPose = CGPoint(x: 12, y: 35)
        XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
            cameraPosition: committedPose, solveWidth: 80, timestamp: 1,
            observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1))
        let output = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
            cameraPosition: committedPose, solveWidth: 80, timestamp: 1, hasGameplaySignal: true))
        XCTAssertEqual(output.anchorPosition, committedPose)
        XCTAssertEqual(output.liveBounds, CGRect(x: 0, y: 0, width: 80, height: 50))
        XCTAssertEqual(try XCTUnwrap(output.atlas).contentBounds,
                       CGRect(x: 0, y: 0, width: 80, height: 50))
    }

    func testAtlasAndLiveFrameShareVerticalPixelCoordinates() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true)
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let bottom = CIImage(color: .red).cropped(to: extent)
        let top = CIImage(color: .green).cropped(to: CGRect(x: 0, y: 25, width: 80, height: 25))
        let frame = try XCTUnwrap(context.createCGImage(top.composited(over: bottom), from: extent))
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        let pose = CGPoint(x: 0, y: 30)
        XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
            cameraPosition: pose, solveWidth: 80, timestamp: 0,
            observationPoseTimestamp: 0, hasGameplaySignal: true, submittedAt: 0))
        let output = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
            cameraPosition: pose, solveWidth: 80, timestamp: 0, hasGameplaySignal: true))
        let atlas = try XCTUnwrap(output.atlas)
        for y in [10.0, 40.0] {
            let current = rgba(in: output.liveImage, at: CGPoint(x: 10, y: y), context: context)
            let saved = rgba(in: atlas.image, at: CGPoint(x: 10, y: y), context: context)
            XCTAssertEqual(saved.red, current.red)
            XCTAssertEqual(saved.green, current.green)
        }
    }

    func testSolvedCameraPosePlacesCurrentFrameOverAtlas() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 10,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let first = try image(color: CIColor(red: 0.05, green: 0.75, blue: 0.15), extent: extent, context: context)
        let current = try image(color: CIColor(red: 0.85, green: 0.08, blue: 0.04), extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: first,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 80,
            timestamp: 0,
            observationPoseTimestamp: 0,
            hasGameplaySignal: true,
            submittedAt: 0
        ))
        _ = try XCTUnwrap(pipeline.process(
            frame: first,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 80,
            timestamp: 0,
            hasGameplaySignal: true
        ))
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: current,
            regions: regions,
            cameraPosition: CGPoint(x: 20, y: 0),
            solveWidth: 80,
            timestamp: 0.2,
            observationPoseTimestamp: 0.2,
            hasGameplaySignal: true,
            submittedAt: 0.2
        ))
        let output = try XCTUnwrap(pipeline.process(
            frame: current,
            regions: regions,
            cameraPosition: CGPoint(x: 20, y: 0),
            solveWidth: 80,
            timestamp: 0.2,
            hasGameplaySignal: true
        ))

        let atlas = try XCTUnwrap(output.atlas)
        XCTAssertEqual(atlas.image.width, LiveTiledAtlas.tileSize)
        XCTAssertEqual(
            atlas.bounds,
            CGRect(x: 0, y: 0, width: LiveTiledAtlas.tileSize, height: LiveTiledAtlas.tileSize)
        )
        XCTAssertEqual(output.liveBounds, CGRect(x: 20, y: 0, width: 80, height: 50))
        XCTAssertEqual(output.focusPoint.x, 60, accuracy: 0.001)
        let retainedAtlas = rgba(in: atlas.image, at: CGPoint(x: 10, y: 25), context: context)
        let transformedCurrent = rgba(in: output.liveImage, at: CGPoint(x: 10, y: 25), context: context)
        XCTAssertGreaterThan(retainedAtlas.green, 150)
        XCTAssertLessThan(retainedAtlas.red, 80)
        XCTAssertGreaterThan(transformedCurrent.red, 180)
        XCTAssertLessThan(transformedCurrent.green, 80)
    }

    func testStableGameplayFramesRefineAtlasButMenuDoesNot() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let first = try image(color: .red, extent: extent, context: context)
        let current = try image(color: .green, extent: extent, context: context)
        let menu = try image(color: .blue, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: first, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true,
            submittedAt: 1
        ))
        for timestamp in [2.0, 2.1, 2.2] {
            XCTAssertTrue(pipeline.submitAtlasObservation(
                frame: current, regions: regions, cameraPosition: .zero, solveWidth: 40,
                timestamp: timestamp, observationPoseTimestamp: timestamp,
                hasGameplaySignal: true, submittedAt: timestamp
            ))
        }
        XCTAssertFalse(pipeline.submitAtlasObservation(
            frame: menu, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 3, observationPoseTimestamp: 3, hasGameplaySignal: false,
            submittedAt: 3
        ))
        let output = try XCTUnwrap(pipeline.process(
            frame: menu, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 3, hasGameplaySignal: false
        ))
        let atlas = try XCTUnwrap(output.atlas)
        let pixel = rgba(in: atlas.image, at: CGPoint(x: 10, y: 10), context: context)
        XCTAssertGreaterThan(pixel.green, 180)
        XCTAssertLessThan(pixel.red, 80)
        XCTAssertLessThan(pixel.blue, 80)
    }

    func testGameplayAtlasUsesNewestPixelImmediately() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let bright = try image(color: .white, extent: extent, context: context)
        let dark = try image(
            color: CIColor(red: 0.1, green: 0.1, blue: 0.1),
            extent: extent,
            context: context
        )
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: bright, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true,
            submittedAt: 1
        ))
        for timestamp in [1.1] {
            XCTAssertTrue(pipeline.submitAtlasObservation(
                frame: dark, regions: regions, cameraPosition: .zero, solveWidth: 40,
                timestamp: timestamp, observationPoseTimestamp: timestamp,
                hasGameplaySignal: true, submittedAt: timestamp
            ))
        }
        let output = try XCTUnwrap(pipeline.process(
            frame: dark, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true
        ))
        let pixel = rgba(
            in: try XCTUnwrap(output.atlas).image,
            at: CGPoint(x: 10, y: 10),
            context: context
        )
        XCTAssertLessThan(pixel.red, 40)
        XCTAssertLessThan(pixel.green, 40)
        XCTAssertLessThan(pixel.blue, 40)
    }

    func testTransitionBridgeRectangularEdgeMaskPreservesMappedRoom() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let first = try image(color: .red, extent: extent, context: context)
        let transition = try image(color: .green, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: first, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true,
            submittedAt: 1
        ))
        for timestamp in [2.0, 2.1, 2.2] {
            XCTAssertTrue(pipeline.submitAtlasObservation(
                frame: transition, regions: regions, cameraPosition: .zero, solveWidth: 40,
                timestamp: timestamp, observationPoseTimestamp: timestamp,
                hasGameplaySignal: true, submittedAt: timestamp,
                transitionBridgePose: true,
                compositionOmittedRects: [CGRect(x: 0, y: 0, width: 10, height: 30)]
            ))
        }
        let output = try XCTUnwrap(pipeline.process(
            frame: transition, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true
        ))
        let atlas = try XCTUnwrap(output.atlas)
        let preserved = rgba(in: atlas.image, at: CGPoint(x: 5, y: 10), context: context)
        XCTAssertGreaterThan(preserved.red, 180)
        XCTAssertLessThan(preserved.green, 80)
        let replaced = rgba(in: atlas.image, at: CGPoint(x: 20, y: 10), context: context)
        XCTAssertLessThan(replaced.red, 80)
        XCTAssertGreaterThan(replaced.green, 180)
    }

    func testMenuPreviewCannotAnchorFirstGameplayAtlasPlacement() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let menu = try image(color: .blue, extent: extent, context: context)
        let gameplay = try image(color: .green, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        _ = try XCTUnwrap(pipeline.process(
            frame: menu, regions: regions, cameraPosition: CGPoint(x: 300, y: 0),
            solveWidth: 40, timestamp: 1, hasGameplaySignal: false
        ))
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: gameplay, regions: regions, cameraPosition: .zero,
            solveWidth: 40, timestamp: 2, observationPoseTimestamp: 2,
            hasGameplaySignal: true, submittedAt: 2
        ))
        let output = try XCTUnwrap(pipeline.process(
            frame: gameplay, regions: regions, cameraPosition: .zero,
            solveWidth: 40, timestamp: 2, hasGameplaySignal: true
        ))
        XCTAssertEqual(output.liveBounds.origin, .zero)
        XCTAssertEqual(output.atlas?.bounds.origin, .zero)
    }

    func testPresentationFocusUsesGameViewportCenter() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 10
        )
        let extent = CGRect(x: 0, y: 0, width: 80, height: 50)
        let frame = try image(color: .black, extent: extent, context: context)
        let regions = SceneRegions(
            knight: CGRect(x: 2, y: 3, width: 12, height: 20),
            health: .zero,
            geo: .zero,
            mana: .zero
        )

        let output = try XCTUnwrap(pipeline.process(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 80,
            timestamp: 0,
            observationPoseTimestamp: 0,
            hasGameplaySignal: true
        ))

        XCTAssertEqual(output.focusPoint.x, 40, accuracy: 0.001)
        XCTAssertEqual(output.focusPoint.y, 25, accuracy: 0.001)
    }

    func testAnnotatedFrameDoesNotRestoreLegacyHUDElementOrKnightBoxes() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 160, height: 90)
        let frame = try image(color: CIColor(red: 0.02, green: 0.03, blue: 0.04), extent: extent, context: context)
        let regions = SceneRegions(
            knight: CGRect(x: 70, y: 20, width: 24, height: 30),
            health: CGRect(x: 20, y: 72, width: 30, height: 8),
            geo: CGRect(x: 20, y: 62, width: 18, height: 8),
            mana: CGRect(x: 10, y: 64, width: 10, height: 18)
        )
        let annotated = try XCTUnwrap(FrameRegionRenderer.annotatedFrame(
            from: frame,
            regions: regions,
            context: context
        ))
        let healthPixel = rgba(
            in: annotated,
            at: CGPoint(x: 21, y: regions.health.midY),
            context: context
        )
        let geoPixel = rgba(
            in: annotated,
            at: CGPoint(x: 21, y: regions.geo.midY),
            context: context
        )
        let soulPixel = rgba(
            in: annotated,
            at: CGPoint(x: 11, y: regions.mana.midY),
            context: context
        )
        let knightPixel = rgba(
            in: annotated,
            at: CGPoint(x: 71, y: regions.knight!.midY),
            context: context
        )
        for pixel in [healthPixel, geoPixel, soulPixel, knightPixel] {
            XCTAssertLessThan(pixel.red, 20)
            XCTAssertLessThan(pixel.green, 20)
            XCTAssertLessThan(pixel.blue, 20)
        }
    }

    func testAtlasWorldTrackerReceivesGroundAlignmentExclusions() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var capturedExclusions = [CGRect]()
        let pipeline = LiveAtlasPipeline(
            context: context,
            runsIntegrationSynchronously: true,
            worldRootURL: root,
            persistentAppender: { store, image, timestamp, position, solveWidth, exclusions in
                capturedExclusions = exclusions
                return try store.append(
                    maskedImage: image,
                    timestamp: timestamp,
                    rawCameraPose: position,
                    solveWidth: solveWidth,
                    excludedRects: exclusions
                )
            }
        )
        let frame = try image(
            color: CIColor(red: 0.1, green: 0.2, blue: 0.3),
            extent: CGRect(x: 0, y: 0, width: 80, height: 50),
            context: context
        )
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        let groundOnly = [
            CGRect(x: 0, y: 30, width: 80, height: 20),
            CGRect(x: 0, y: 0, width: 20, height: 30),
        ]
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 80,
            timestamp: 1,
            observationPoseTimestamp: 1,
            hasGameplaySignal: true,
            submittedAt: 1,
            alignmentExclusions: groundOnly
        ))
        XCTAssertEqual(capturedExclusions, groundOnly)
        XCTAssertEqual(
            try SceneSessionStore(rootURL: root).manifest.observations.first?.excludedRects,
            groundOnly
        )
    }

    func testLiveDetectorCanSkipParticleWork() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let extent = CGRect(x: 0, y: 0, width: 160, height: 90)
        let frame = try image(color: CIColor(red: 0.1, green: 0.1, blue: 0.1), extent: extent, context: context)
        let regions = SceneRegionDetector().detect(in: frame, includeParticles: false)
        XCTAssertTrue(regions.particles.isEmpty)
    }

    func testCaptureConfigurationExcludesCursor() {
        let configuration = HollowKnightCaptureConfiguration.make(
            windowSize: CGSize(width: 1600, height: 900)
        )
        XCTAssertEqual(configuration.width, 640)
        XCTAssertEqual(configuration.minimumFrameInterval, .zero)
        XCTAssertEqual(HollowKnightCaptureConfiguration.motionSampleStride, 1)
        XCTAssertEqual(HollowKnightCaptureConfiguration.objectInferenceStride, 4)
        XCTAssertEqual(HollowKnightCaptureConfiguration.atlasSampleStride, 8)
        XCTAssertFalse(configuration.showsCursor)
        XCTAssertFalse(configuration.capturesAudio)
    }

    func testAtlasWriteCanBeSkippedWhilePresentationContinues() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 30,
            atlasFramesPerSecond: 30
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let frame = try image(color: .red, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        let output = try XCTUnwrap(pipeline.process(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 40,
            timestamp: 0,
            observationPoseTimestamp: 0,
            hasGameplaySignal: true,
            allowAtlasWrite: false
        ))

        XCTAssertFalse(output.integratedFrame)
        XCTAssertNil(output.atlas)
        XCTAssertEqual(output.liveImage.width, 40)
    }

    func testNewCaptureGenerationAcceptsRestartedPresentationTimestamps() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 30
        )
        let frame = try image(
            color: .red,
            extent: CGRect(x: 0, y: 0, width: 40, height: 30),
            context: context
        )
        let regions = SceneRegions(
            knight: nil, health: .zero, geo: .zero, mana: .zero
        )

        XCTAssertNotNil(pipeline.process(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 40,
            timestamp: 100,
            hasGameplaySignal: false
        ))

        pipeline.beginCaptureGeneration()

        XCTAssertNotNil(pipeline.process(
            frame: frame,
            regions: regions,
            cameraPosition: .zero,
            solveWidth: 40,
            timestamp: 1,
            hasGameplaySignal: false
        ))
    }

    func testStalePosePresentsFrameButCannotWriteAtlas() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 30,
            atlasFramesPerSecond: 30
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 30)
        let frame = try image(color: .red, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        let output = try XCTUnwrap(pipeline.process(
            frame: frame,
            regions: regions,
            cameraPosition: CGPoint(x: 50, y: 0),
            solveWidth: 40,
            timestamp: 2,
            observationPoseTimestamp: 1,
            hasGameplaySignal: true
        ))

        XCTAssertFalse(output.integratedFrame)
        XCTAssertEqual(output.focusPoint.x, 20, accuracy: 0.001)
    }

    func testDelayedExactPoseFrameCannotReplaceNewerLiveWindowOrEnterAtlas() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 20)
        let red = try image(color: .red, extent: extent, context: context)
        let blue = try image(color: .blue, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        let newest = try XCTUnwrap(pipeline.process(
            frame: red, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 10, hasGameplaySignal: true
        ))
        XCTAssertGreaterThan(rgba(in: newest.liveImage, at: CGPoint(x: 20, y: 10), context: context).red, 180)
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: red, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 10, observationPoseTimestamp: 10, hasGameplaySignal: true, submittedAt: 1
        ))
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: blue, regions: regions, cameraPosition: CGPoint(x: 300, y: 0), solveWidth: 40,
            timestamp: 9, observationPoseTimestamp: 9, hasGameplaySignal: true, submittedAt: 2
        ))
        let later = try XCTUnwrap(pipeline.process(
            frame: red, regions: regions, cameraPosition: CGPoint(x: 4, y: 0), solveWidth: 40,
            timestamp: 11, observationPoseTimestamp: nil, hasGameplaySignal: true
        ))
        XCTAssertGreaterThan(rgba(in: later.liveImage, at: CGPoint(x: 20, y: 10), context: context).red, 180)
        XCTAssertEqual(later.focusPoint.x, 24, accuracy: 0.001)
        XCTAssertEqual(later.atlasTiles.count, 1)
    }

    func testSlowAtlasWorkerDoesNotDelayLatestLivePresentation() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let integrationStarted = expectation(description: "slow atlas worker started")
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            integrationDelay: 0.20,
            integrationStarted: { integrationStarted.fulfill() }
        )
        let extent = CGRect(x: 0, y: 0, width: 40, height: 20)
        let first = try image(color: .red, extent: extent, context: context)
        let latest = try image(color: .green, extent: extent, context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        _ = pipeline.process(
            frame: first, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 0, hasGameplaySignal: true
        )
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: first, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 0, observationPoseTimestamp: 0, hasGameplaySignal: true, submittedAt: 1
        ))
        wait(for: [integrationStarted], timeout: 1)
        let started = ProcessInfo.processInfo.systemUptime
        let output = try XCTUnwrap(pipeline.process(
            frame: latest, regions: regions, cameraPosition: CGPoint(x: 8, y: 0), solveWidth: 40,
            timestamp: 0.04, observationPoseTimestamp: nil, hasGameplaySignal: true
        ))
        XCTAssertLessThan(ProcessInfo.processInfo.systemUptime - started, 0.08)
        XCTAssertGreaterThan(rgba(in: output.liveImage, at: CGPoint(x: 20, y: 10), context: context).green, 180)
        XCTAssertEqual(output.focusPoint.x, 28, accuracy: 0.001)
    }

    func testResetInvalidatesActiveTicketBeforeItCanPersistOrPublish() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let started = expectation(description: "integration started")
        let noWorldUpdate = expectation(description: "no stale world callback")
        noWorldUpdate.isInverted = true
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            integrationDelay: 0.15,
            integrationStarted: { started.fulfill() },
            worldRootURL: root
        )
        pipeline.setWorldUpdateHandler { _ in noWorldUpdate.fulfill() }
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1
        ))
        wait(for: [started], timeout: 1)
        pipeline.reset()
        wait(for: [noWorldUpdate], timeout: 0.3)

        XCTAssertTrue(try SceneSessionStore(rootURL: root).manifest.observations.isEmpty)
        let output = try XCTUnwrap(pipeline.process(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true
        ))
        XCTAssertNil(output.atlas)
    }

    func testResetCancelsPersistedAtlasRestoration() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let frame = try image(
            color: .red,
            extent: CGRect(x: 0, y: 0, width: 8, height: 8),
            context: context
        )
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        do {
            let writer = LiveAtlasPipeline(
                context: context,
                presentationFramesPerSecond: 60,
                atlasFramesPerSecond: 60,
                runsIntegrationSynchronously: true,
                worldRootURL: root
            )
            for id in 0..<12 {
                XCTAssertTrue(writer.submitAtlasObservation(
                    frame: frame,
                    regions: regions,
                    cameraPosition: CGPoint(x: id, y: 0),
                    solveWidth: 8,
                    timestamp: Double(id),
                    observationPoseTimestamp: Double(id),
                    hasGameplaySignal: true,
                    submittedAt: Double(id)
                ))
            }
        }
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count, 12)

        let restorationStarted = expectation(description: "persisted restoration started")
        let pipeline = LiveAtlasPipeline(
            context: context,
            restorationObservationStarted: { id in
                if id == 0 { restorationStarted.fulfill() }
                Thread.sleep(forTimeInterval: 0.05)
            },
            worldRootURL: root
        )
        wait(for: [restorationStarted], timeout: 1)

        let started = ProcessInfo.processInfo.systemUptime
        XCTAssertTrue(pipeline.reset())
        XCTAssertLessThan(ProcessInfo.processInfo.systemUptime - started, 0.3)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count, 0)
    }

    func testAutoSaveAgeUsesAtlasCreationRatherThanLatestManifestWrite() throws {
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let pipeline = LiveAtlasPipeline(
            context: CIContext(options: [.useSoftwareRenderer: true]),
            worldRootURL: root
        )
        let displayNow = Date().addingTimeInterval(3_600)
        let before = pipeline.activeAutoSaveSummary(now: displayNow)
        try FileManager.default.setAttributes(
            [.modificationDate: displayNow.addingTimeInterval(-1)],
            ofItemAtPath: root.appendingPathComponent("manifest.json").path
        )
        let after = pipeline.activeAutoSaveSummary(now: displayNow)

        XCTAssertEqual(after.createdAt, before.createdAt)
        XCTAssertGreaterThan(displayNow.timeIntervalSince(after.createdAt), 3_500)
    }

    func testPersistentAppendFailureDoesNotFallBackToTransientAtlasOrTracker() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var callbackCount = 0
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            worldRootURL: root,
            persistentAppender: { _, _, _, _, _, _ in throw PersistenceFailure.append }
        )
        pipeline.setWorldUpdateHandler { _ in callbackCount += 1 }
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1
        ))

        XCTAssertTrue(try SceneSessionStore(rootURL: root).manifest.observations.isEmpty)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count, 0)
        XCTAssertEqual(callbackCount, 0)
        let output = try XCTUnwrap(pipeline.process(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true
        ))
        XCTAssertNil(output.atlas)
    }

    func testCorruptPersistentWorldBlocksAtlasInsteadOfFallingBackToMemory() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let store = try SceneSessionStore(rootURL: root)
        XCTAssertTrue(try store.saveScene(
            data: Data("not a world snapshot".utf8),
            expectedRevision: 0,
            newRevision: 1
        ))
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            worldRootURL: root
        )
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertNotNil(pipeline.worldPersistenceIssue)
        XCTAssertFalse(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1
        ))
        let output = try XCTUnwrap(pipeline.process(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, hasGameplaySignal: true
        ))
        XCTAssertTrue(output.atlasTiles.isEmpty)
    }

    func testFailedPersistentCASRollsBackTrackerAndSuppressesAtlasAndCallback() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var callbackCount = 0
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            worldRootURL: root,
            persistentCommitter: { _, _, _, _ in false }
        )
        pipeline.setWorldUpdateHandler { _ in callbackCount += 1 }
        let frame = try image(color: .green, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 1, observationPoseTimestamp: 1, hasGameplaySignal: true, submittedAt: 1
        ))

        XCTAssertEqual(try SceneSessionStore(rootURL: root).manifest.observations.count, 1)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count, 0)
        XCTAssertEqual(callbackCount, 0)
        let output = try XCTUnwrap(pipeline.process(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 2, hasGameplaySignal: true
        ))
        XCTAssertNil(output.atlas)
    }

    func testFailedCompositionCannotCommitGraphAndLaterFrameCanRecover() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var cancelComposition = false
        var commitAttempts = 0
        var updates = [LiveWorldPipelineUpdate]()
        let pipeline = LiveAtlasPipeline(context: context,
            presentationFramesPerSecond: 60, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            compositionShouldCancel: { cancelComposition }, worldRootURL: root,
            persistentCommitter: { store, snapshot, expected, next in
                commitAttempts += 1
                return try store.commit(snapshot, expectedRevision: expected, newRevision: next)
            })
        pipeline.setWorldUpdateHandler { updates.append($0) }
        let frame = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        for index in 1...3 {
            cancelComposition = index == 2
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: frame, regions: regions,
                cameraPosition: CGPoint(x: index * 40, y: 0), solveWidth: 40,
                timestamp: Double(index), observationPoseTimestamp: Double(index),
                hasGameplaySignal: true, submittedAt: Double(index)))
            let expectedCount = index == 3 ? 2 : 1
            let disk = try LiveWorldSessionStore(rootURL: root).load().snapshot
            XCTAssertEqual(disk.observations.count, expectedCount)
            XCTAssertEqual(commitAttempts, expectedCount)
            XCTAssertEqual(updates.count, expectedCount)
            let output = try XCTUnwrap(pipeline.process(frame: frame, regions: regions,
                cameraPosition: .zero, solveWidth: 40, timestamp: Double(index),
                hasGameplaySignal: true, allowAtlasWrite: false))
            XCTAssertEqual(output.worldRevision, disk.mapRevision)
        }
        XCTAssertEqual(updates.last?.snapshot.observations.map(\.timestamp), [1, 3])
    }

    func testFailedCommitCannotLeakPixelsIntoLaterSuccessfulPublication() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var commitAttempts = 0
        let pipeline = LiveAtlasPipeline(context: context,
            presentationFramesPerSecond: 60, atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true, worldRootURL: root,
            persistentCommitter: { store, snapshot, expected, next in
                commitAttempts += 1
                if commitAttempts == 2 { return false }
                return try store.commit(snapshot, expectedRevision: expected, newRevision: next)
            })
        let red = try image(color: .red, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let green = try image(color: .green, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        for index in 1...3 {
            XCTAssertTrue(pipeline.submitAtlasObservation(frame: index == 2 ? green : red,
                regions: regions, cameraPosition: CGPoint(x: index == 3 ? 80 : 0, y: 0), solveWidth: 40,
                timestamp: Double(index), observationPoseTimestamp: Double(index),
                hasGameplaySignal: true, submittedAt: Double(index)))
        }
        let disk = try LiveWorldSessionStore(rootURL: root).load().snapshot
        XCTAssertEqual(disk.observations.map(\.timestamp), [1, 3])
        let output = try XCTUnwrap(pipeline.process(frame: red, regions: regions,
            cameraPosition: .zero, solveWidth: 40, timestamp: 4,
            hasGameplaySignal: true, allowAtlasWrite: false))
        XCTAssertEqual(output.worldRevision, disk.mapRevision)
        let atlas = try XCTUnwrap(output.atlas)
        let originalPixel = rgba(in: atlas.image, at: CGPoint(x: 10, y: 10), context: context)
        XCTAssertGreaterThan(originalPixel.red, 180)
        XCTAssertLessThan(originalPixel.green, 80)
    }

    func testDelayedWorldTimestampIsDiscardedWithoutRewindingTrackerVisit() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        var updates = [LiveWorldPipelineUpdate]()
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            worldRootURL: root
        )
        pipeline.setWorldUpdateHandler { updates.append($0) }
        let frame = try image(color: .blue, extent: CGRect(x: 0, y: 0, width: 40, height: 20), context: context)
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)

        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 10, observationPoseTimestamp: 10, hasGameplaySignal: true,
            captureGeneration: 7, worldTimestamp: 100, submittedAt: 1
        ))
        XCTAssertTrue(pipeline.submitAtlasObservation(
            frame: frame, regions: regions, cameraPosition: CGPoint(x: 300, y: 0), solveWidth: 40,
            timestamp: 9, observationPoseTimestamp: 9, hasGameplaySignal: true,
            captureGeneration: 7, worldTimestamp: 99, submittedAt: 2
        ))

        XCTAssertEqual(updates.count, 1)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.map(\.timestamp), [100])
        XCTAssertEqual(try SceneSessionStore(rootURL: root).manifest.observations.count, 1)
        let output = try XCTUnwrap(pipeline.process(
            frame: frame, regions: regions, cameraPosition: .zero, solveWidth: 40,
            timestamp: 11, hasGameplaySignal: true
        ))
        XCTAssertEqual(output.atlasTiles.count, 1)
    }

    func testRecoverySearchIsTransientAndReturnsConfirmedWorldPlacement() throws {
        let context = CIContext(options: [.useSoftwareRenderer: true])
        let root = temporaryWorldRoot()
        defer { removeTemporaryWorldRoot(root) }
        let basis = WorldBasisController()
        let generation = basis.beginCapture()
        let pipeline = LiveAtlasPipeline(
            context: context,
            presentationFramesPerSecond: 60,
            atlasFramesPerSecond: 60,
            runsIntegrationSynchronously: true,
            worldRootURL: root
        )
        pipeline.setWorldPoseResolver { basis.rebasedObservation(for: $0) }
        var updates = [LiveWorldPipelineUpdate]()
        pipeline.setWorldUpdateHandler { updates.append($0) }
        let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero)
        let frames = [recoveryTexture(0), recoveryTexture(1), recoveryTexture(2), recoveryTexture(3)]
        let poses = [CGPoint.zero, CGPoint(x: 100, y: 0), CGPoint(x: 100, y: 100), CGPoint(x: 0, y: 100)]
        for index in frames.indices {
            let timestamp = Double(index)
            let ticket = try XCTUnwrap(basis.observe(localPose: poses[index], captureTimestamp: timestamp))
            XCTAssertTrue(pipeline.submitAtlasObservation(
                frame: frames[index], regions: regions, cameraPosition: poses[index], solveWidth: 320,
                timestamp: timestamp, observationPoseTimestamp: timestamp, hasGameplaySignal: true,
                captureGeneration: generation, worldTimestamp: timestamp, submittedAt: timestamp,
                basisTicket: ticket
            ))
        }
        let committedCount = try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count

        basis.loseLocalTracking()
        let firstTicket = try XCTUnwrap(basis.observe(localPose: CGPoint(x: 80, y: 0), captureTimestamp: 10))
        XCTAssertTrue(pipeline.submitRecoveryObservation(
            frame: frames[0], regions: regions, solveWidth: 320, timestamp: 10,
            captureGeneration: generation, basisTicket: firstTicket, submittedAt: 10
        ))
        let secondTicket = try XCTUnwrap(basis.observe(localPose: CGPoint(x: 80, y: 0), captureTimestamp: 11))
        XCTAssertTrue(pipeline.submitRecoveryObservation(
            frame: frames[0], regions: regions, solveWidth: 320, timestamp: 11,
            captureGeneration: generation, basisTicket: secondTicket, submittedAt: 11
        ))

        let recovery = try XCTUnwrap(updates.last(where: { $0.confirmedRecoveryPose != nil }))
        XCTAssertEqual(recovery.confirmedRecoveryPose, .zero)
        XCTAssertEqual(recovery.correction.dx, -80, accuracy: 0.01)
        XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot.observations.count, committedCount)
        XCTAssertEqual(try SceneSessionStore(rootURL: root).manifest.observations.count, committedCount)
    }

    private func temporaryWorldRoot() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
    }

    private func removeTemporaryWorldRoot(_ root: URL) {
        let fileManager = FileManager.default
        try? fileManager.removeItem(at: root)
        let archivePrefix = root.lastPathComponent + ".archive-"
        let parent = root.deletingLastPathComponent()
        for item in (try? fileManager.contentsOfDirectory(at: parent, includingPropertiesForKeys: nil)) ?? []
        where item.lastPathComponent.hasPrefix(archivePrefix) {
            try? fileManager.removeItem(at: item)
        }
    }

    private func image(color: CIColor, extent: CGRect, context: CIContext) throws -> CGImage {
        try XCTUnwrap(context.createCGImage(CIImage(color: color).cropped(to: extent), from: extent))
    }

    private func recoveryTexture(_ seed: Int) -> CGImage {
        let width = 320, height = 180
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let value = seed == 0
                    ? (x * 17 + y * 31 + (x * y) % 89) & 255
                    : ((x * (7 + seed) ^ y * (53 + seed * 3)) + seed * 41) & 255
                let offset = (y * width + x) * 4
                bytes[offset] = UInt8(value)
                bytes[offset + 1] = UInt8(255 - value)
                bytes[offset + 2] = UInt8(value / 3)
                bytes[offset + 3] = 255
            }
        }
        return CGContext(
            data: &bytes, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!.makeImage()!
    }

    private func rgba(in image: CGImage, at point: CGPoint, context: CIContext) -> (red: UInt8, green: UInt8, blue: UInt8) {
        var pixel = [UInt8](repeating: 0, count: 4)
        context.render(
            CIImage(cgImage: image),
            toBitmap: &pixel,
            rowBytes: 4,
            bounds: CGRect(x: floor(point.x), y: floor(point.y), width: 1, height: 1),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return (pixel[0], pixel[1], pixel[2])
    }
}
