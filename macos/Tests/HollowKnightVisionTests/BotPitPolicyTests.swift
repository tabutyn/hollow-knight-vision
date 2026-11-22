import XCTest
@testable import HollowKnightVision

final class BotPitPolicyTests: XCTestCase {
    private func row(x: Double?, foot: Double, gap: ClosedRange<Double>, floor: Bool = true,
                     time: Double = 1) -> BotObservation {
        var f = [Double](repeating: 0, count: BotObservationContract.featureCount)
        if let x { f[0] = x; f[1] = foot+0.025; f[3] = 0.05; f[4] = 0.9 }
        f[6] = 1
        var lines = [[0.0, gap.lowerBound, 0.14], [gap.upperBound, 1.0, 0.14]]
        if floor { lines.append([gap.lowerBound+0.01,gap.upperBound-0.01,0.07]) }
        for (i,line) in lines.enumerated() {
            let k = 16 + i*5
            f[k] = line[0]-f[0]; f[k+1] = line[1]-f[0]; f[k+2] = line[2]-f[1]; f[k+4] = 1
        }
        return .init(type: "observation", captureTimestamp: time, capturedAt: time, availableAt: time,
                     generation: 1, roomID: 1, roomRevision: 1, detectionTimestamp: time, detectionFrameID: 1,
                     modelVersion: nil, bestKnightConfidence: nil, features: f, issues: x == nil ? ["missing-knight"] : [],
                     terrainSource: "detected")
    }

    func testUsesRelativeGeometryAndVisibilityAcrossDifferentStartingPositions() throws {
        for x in [0.4, 0.48, 0.55] {
            var pit = BotPitPerception()
            let v = try XCTUnwrap(pit.input(row(x:x,foot:0.07,gap:0.38...0.57),action:.neutral))
            XCTAssertEqual(v[0],Float((0.38-x)*6),accuracy:0.001)
            XCTAssertEqual(v[1],Float((0.57-x)*6),accuracy:0.001)
            XCTAssertEqual(v[2],0.42,accuracy:0.001)
            XCTAssertEqual(v[5],1)
        }
        var pit = BotPitPerception()
        XCTAssertEqual(pit.input(row(x:nil,foot:0,gap:0.31...0.46),action:.neutral)?[5],0)
    }

    func testKeepsControlInAirAndReleasesAfterTwoLedgeObservations() {
        var pit = BotPitPerception()
        XCTAssertNotNil(pit.input(row(x:0.5,foot:0.07,gap:0.4...0.56),action:.neutral))
        XCTAssertNotNil(pit.input(row(x:0.6,foot:0.23,gap:0.4...0.56,time:1.1),action:.neutral))
        XCTAssertNotNil(pit.input(row(x:0.62,foot:0.14,gap:0.4...0.56,time:1.2),action:.neutral))
        XCTAssertNil(pit.input(row(x:0.63,foot:0.14,gap:0.4...0.56,time:1.3),action:.neutral))
        XCTAssertNil(pit.input(row(x:0.65,foot:0.14,gap:0.4...0.56,time:1.4),action:.neutral))
    }

    func testFlatGroundAndNarrowDetectorBreaksDoNotEngage() {
        var pit = BotPitPerception()
        XCTAssertNil(pit.input(row(x:0.5,foot:0.14,gap:0.49...0.51,floor:false),action:.neutral))
        XCTAssertNil(pit.input(row(x:nil,foot:0,gap:0.49...0.51,floor:false),action:.neutral))
    }

    func testExplorationNeedsADropWhenTheKnightOccludesTheFlatFloor() {
        var pit = BotPitPerception()
        XCTAssertNil(pit.input(row(x: nil, foot: 0, gap: 0.36...0.57, floor: false),
                               action: .neutral, requireDropEvidence: true))
        XCTAssertNil(pit.input(row(x: 0.5, foot: 0.117, gap: 0.36...0.57, floor: false),
                               action: .neutral, requireDropEvidence: true))
        XCTAssertNotNil(pit.input(row(x: nil, foot: 0, gap: 0.36...0.57),
                                  action: .neutral, requireDropEvidence: true))
    }

    func testExportedPitCheckpointMatchesSB3() throws {
        let env = ProcessInfo.processInfo.environment
        guard let dir = env["HKV_PIT_MODEL"], let path = env["HKV_PIT_PARITY"] else {
            throw XCTSkip("Provide a trained Gameplay bundle and its SB3 parity fixture.")
        }
        struct Fixture: Decodable { let inputs: [[Float]]; let actions: [[Int]] }
        let bundle = try BotNativePolicy.load(from: URL(fileURLWithPath: dir))
        let pit = try XCTUnwrap(bundle.document.pitPolicy)
        XCTAssertNil(bundle.document.recoveryManeuver)
        let fixture = try JSONDecoder().decode(Fixture.self,from: Data(contentsOf: URL(fileURLWithPath: path)))
        for (input, expected) in zip(fixture.inputs,fixture.actions) {
            XCTAssertEqual(try pit.action(input).values,[expected[0],0,expected[1],0])
        }
    }
}
