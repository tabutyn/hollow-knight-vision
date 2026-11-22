import CoreGraphics
import XCTest
@testable import HollowKnightVision

final class BotAtlasExplorationTests: XCTestCase {
    func testHeldAttackCostsOnlyRepeatedDecisions() throws {
        let right = try BotAction(values: [2, 0, 0, 0])
        let attack = try BotAction(values: [2, 0, 0, 1])
        var run = BotAtlasRun()
        let inputs = [BotAction.neutral, attack, attack, attack, right, attack]
        for (previous, current) in zip(inputs, inputs.dropFirst()) { run.recordAction(current, after: previous) }
        XCTAssertEqual(run.score.heldAttackDecisions, 2)
        XCTAssertEqual(run.score.reward, -0.10, accuracy: 1e-10)
        let legacy = Data(#"{"newCells":0,"damage":0,"dwellDecisions":0}"#.utf8)
        XCTAssertEqual(try JSONDecoder().decode(BotAtlasScore.self, from: legacy).heldAttackDecisions, 0)
    }

    private func map(_ xs: [Double], revision: Int) throws -> LiveWorldSnapshot {
        let rows = xs.enumerated().map { (i, x) in
            let point = CGPoint(x: x, y: 0)
            return LiveWorldObservation(id: i, sourceObservationID: i, timestamp: Double(i),
                sourceWidth: 640, sourceHeight: 360, solveWidth: 640, sampleWidth: 160,
                excludedRects: [], rawPose: point, optimizedPose: point)
        }
        let island = LiveTrackingIsland(id: 0, roomID: 0, startedAt: 0, worldFromLocal: .zero,
            placement: .reference, uncertainty: .reference, breakReason: "test",
            members: rows.map { .init(observationID: $0.id, localPose: $0.optimizedPose, measured: true) })
        return try LiveWorldSnapshot(mapRevision: revision, anchoredObservationID: 0, observations: rows,
                                     trackingIslands: [island])
    }

    func testRevisitsAndPoseCorrectionsEarnNothingAndFrontierReversesAtOtherEnd() throws {
        var coverage = BotAtlasCoverage()
        let initial = try map([0], revision: 0)
        coverage.update(initial) { _ in .unbounded }
        XCTAssertEqual(coverage.earned, 0)
        let extended = try map([0, 128, 256], revision: 1)
        coverage.update(extended) { _ in .unbounded }
        XCTAssertGreaterThan(coverage.earned, 0)
        let reward = coverage.earned
        let left = try XCTUnwrap(coverage.view(pose: .zero, width: 640, height: 360, room: 0, health: nil))
        let right = try XCTUnwrap(coverage.view(pose: CGPoint(x: 256,y: 0), width: 640, height: 360, room: 0, health: nil))
        XCTAssertGreaterThan(left.left, left.right)
        XCTAssertGreaterThan(right.right, right.left)
        let revisited = try map([0, 128, 256, 128], revision: 2)
        coverage.update(revisited) { _ in .unbounded }
        coverage.update(try revisited.translatingIslands([0: CGVector(dx: 64, dy: 0)], baseRevision: 2)) { _ in .unbounded }
        XCTAssertEqual(coverage.earned, reward)
    }

    func testLostMasksAndRemainingInSameAreaCostPoints() {
        var run = BotAtlasRun()
        func observe(_ tick: Int, _ masks: Int?, _ earned: Int) {
            let time = Double(tick) / 10
            let view = BotAtlasView(left: 1, right: 1, camera: .zero, width: 640, coveredCells: 40,
                                    earnedCells: earned, revision: 3, health: masks)
            var frame = BotObservation(type: "observation", captureTimestamp: time, capturedAt: time,
                availableAt: time, generation: 1, roomID: 0, roomRevision: 1, detectionTimestamp: time,
                detectionFrameID: nil, modelVersion: nil, bestKnightConfidence: nil,
                features: Array(repeating: 0, count: 155), issues: [])
            frame.pathProgress = tick == 5 ? 0.75 : 0
            run.observe(frame, view: view)
        }
        for t in 0..<22 { observe(t, t == 7 ? 3 : 5, t > 4 ? 112 : 100) }
        XCTAssertEqual(run.score.damage, 0)
        for t in 22...28 { observe(t, 4, 112) }
        XCTAssertEqual(run.score.damage, 1)
        XCTAssertEqual(run.score.newCells, 12)
        XCTAssertEqual(run.score.dwellDecisions, 9)
        XCTAssertEqual(run.score.pathProgress, 0.75)
        XCTAssertEqual(run.score.reward, 3 - 32 - 9 * 0.12, accuracy: 1e-6)
    }

    func testOlderAtlasScoresDecodeWithoutBlueHealth() throws {
        let data = Data(#"{"newCells":1,"damage":2,"dwellDecisions":3}"#.utf8)
        XCTAssertEqual(try JSONDecoder().decode(BotAtlasScore.self, from: data).blueHealthGained, 0)
    }

    func testBlueHealthGainScoresOnceAcrossRecordedSamples() {
        var run = BotAtlasRun()
        let epoch = UUID()
        var row = BotObservation(type: "observation", captureTimestamp: 1, capturedAt: 1, availableAt: 1,
            generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: 1, detectionFrameID: nil,
            modelVersion: nil, bestKnightConfidence: nil, features: .init(repeating: 0, count: 155), issues: [])
        row.blueHealthOutcomes = .init(epoch: epoch, gained: 0)
        run.observe(row, view: nil)
        row.blueHealthOutcomes = .init(epoch: epoch, gained: 2)
        run.observe(row, view: nil)
        run.observe(row, view: nil)
        XCTAssertEqual(run.score.blueHealthGained, 2)
        XCTAssertEqual(run.score.reward, 2)
        row.blueHealthOutcomes = .init(epoch: epoch, gained: 1)
        run.observe(row, view: nil)
        row.blueHealthOutcomes = .init(epoch: epoch, gained: 2)
        run.observe(row, view: nil)
        XCTAssertEqual(run.score.blueHealthGained, 2)
        row.blueHealthOutcomes = nil
        run.observe(row, view: nil)
        row.blueHealthOutcomes = .init(epoch: epoch, gained: 5)
        run.observe(row, view: nil)
        XCTAssertEqual(run.score.blueHealthGained, 2)
    }

    func testMapFeaturesEncodeMissingAreaAndHealthForTheGeneralActor() throws {
        var history = BotObservationHistory()
        var row = BotObservation(type: "observation", captureTimestamp: 1, capturedAt: 1, availableAt: 1,
            generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: 1, detectionFrameID: nil,
            modelVersion: nil, bestKnightConfidence: nil, features: .init(repeating: 0, count: 155), issues: [])
        row.features[4] = 1; row.features[6] = 1
        let view = BotAtlasCoverage.frontiers(pose: .zero, width: 640, height: 360, covered: 20, earned: 0,
            revision: 1, health: 4) { x, _ in x >= 0 && x < 40 }
        XCTAssertGreaterThan(view.left, view.right)
        let grid = try XCTUnwrap(view.missing)
        XCTAssertEqual(grid[5], 1); XCTAssertEqual(grid[9], 0)
        row.atlasView = view
        let input = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 1, includeScreenTerrain: true,
            includeBlockade: true, includeGeoCaches: true, includeAtlas: true, includeAtlasMap: true))
        XCTAssertEqual(input.count, BotObservationContract.atlasMapNames.count * 4)
        XCTAssertEqual(Array(input.suffix(21)), [1, Float(view.left), Float(view.right), 1, 0.25, 1] + grid.map(Float.init))
    }

    func testHackerAtlasScoresNewOpaqueCellsAfterRestoreOnlyOnce() throws {
        let context = try XCTUnwrap(CGContext(data: nil, width: 256, height: 256, bitsPerComponent: 8,
            bytesPerRow: 1024, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo.byteOrder32Big.rawValue | CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(gray: 1, alpha: 1)
        context.fill(CGRect(x: 0, y: 0, width: 256, height: 128))
        let image = try XCTUnwrap(context.makeImage())
        func tile(_ x: Int) -> LiveTiledAtlasTile {
            .init(id: x, key: x, tileX: x, tileY: 0, image: image,
                  worldBounds: CGRect(x: x * 256, y: 0, width: 256, height: 256), contributionIDs: [x])
        }
        var coverage = BotHackerAtlasCoverage()
        let first = LiveTiledAtlasSnapshot(tiles: [tile(0)], revision: 1, totalBounds: .zero, contentBounds: .zero)
        coverage.update(first)
        coverage.update(first, camera: .zero, width: 640)
        let initial = try XCTUnwrap(coverage.view(camera: .zero, size: .init(width: 640, height: 360), health: 5))
        XCTAssertEqual(initial.coveredCells, 8)
        XCTAssertEqual(initial.earnedCells, 0)
        let extended = LiveTiledAtlasSnapshot(tiles: [tile(0), tile(1)], revision: 2, totalBounds: .zero, contentBounds: .zero)
        coverage.update(extended, camera: CGPoint(x: 100, y: 0), width: 640)
        coverage.update(extended, camera: .zero, width: 640)
        XCTAssertEqual(coverage.earned, 8)
        XCTAssertEqual(coverage.view(camera: .zero, size: .init(width: 640, height: 360), health: nil)?.coveredCells, 16)
    }

    func testSB3WorldDirectionParity() throws {
        let env = ProcessInfo.processInfo.environment
        guard let path = env["HKV_ATLAS_MODEL"], let file = env["HKV_ATLAS_PARITY"] else {
            throw XCTSkip("Provide the exported Atlas model and SB3 direction fixture.")
        }
        struct Fixture: Decodable { let inputs: [[Float]]; let worldActions: [Int] }
        let actor = try XCTUnwrap(BotNativePolicy.load(from: URL(fileURLWithPath: path)).document.atlasPolicy)
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: URL(fileURLWithPath: file)))
        for (x, action) in zip(fixture.inputs, fixture.worldActions) { XCTAssertEqual(try actor.direction(x), action) }
    }
}
