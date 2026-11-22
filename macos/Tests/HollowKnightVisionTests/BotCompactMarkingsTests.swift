import XCTest
@testable import HollowKnightVision

final class BotCompactMarkingsTests: XCTestCase {
    func testMatchesPythonFixturesIncludingUnknownChannelsClippingAndOverflow() throws {
        struct Fixture: Decodable { let geometry: BotScreenTerrain?; let knightX, knightY: Double; let expected: [Float] }
        let url = try XCTUnwrap(Bundle.module.url(forResource: "compact-markings-cases", withExtension: "json"))
        let fixtures = try JSONDecoder().decode([Fixture].self, from: Data(contentsOf: url))
        for fixture in fixtures {
            let actual = BotCompactMarkings.features(fixture.geometry, knightX: fixture.knightX, knightY: fixture.knightY)
            XCTAssertEqual(actual.count, 105)
            for (a, b) in zip(actual, fixture.expected) { XCTAssertEqual(a, b, accuracy: 0.000001) }
        }
    }
    func testSharedSlotsRetainAllFourTypesWithinCapacity() {
        var geometry = BotScreenTerrain(width: 640, height: 360)
        geometry.floors = (0..<10).map { .init(minimumX: Double($0 * 20), maximumX: Double($0 * 20 + 10), y: 100, confidence: 1) }
        geometry.walls = (0..<3).map { .init(x: Double($0 * 40), bottomY: 30, topY: 90, solidSide: 0) }
        geometry.spikes = [.init(x1: 10, y1: 80, x2: 40, y2: 80)]
        geometry.doors = [.init(x1: 300, y1: 40, x2: 300, y2: 90)]
        let values = BotCompactMarkings.features(geometry, knightX: 0.5, knightY: 0.3)
        XCTAssertEqual(Array(values.prefix(4)), [1, 1, 1, 1])
        let expectedCounts: [Float] = [10, 3, 1, 1].map { Float($0) / 16 }
        XCTAssertEqual(Array(values[4..<8]), expectedCounts)
        XCTAssertEqual(values[8], 0)
        let kinds = stride(from: 13, to: values.count, by: 6).filter { values[$0 + 1] == 1 }.map { values[$0] }
        XCTAssertEqual(kinds.filter { $0 == 0.33 }.count, 10)
        XCTAssertEqual(kinds.filter { $0 == 0.66 }.count, 3)
        XCTAssertEqual(kinds.filter { $0 == 0 }.count, 1)
        XCTAssertEqual(kinds.filter { $0 == 1 }.count, 1)
        geometry.floors.reverse(); geometry.walls.reverse()
        XCTAssertEqual(values, BotCompactMarkings.features(geometry, knightX: 0.5, knightY: 0.3))
    }
    func testCurrentPolicyDropsLegacyListsGridsAndJoniButOlderContractsStillLoad() throws {
        let names = BotObservationContract.compactGeometryNames
        XCTAssertEqual(BotObservationContract.compactScalarNames.count, 100)
        XCTAssertEqual(names.count, 205)
        XCTAssertFalse(names.contains { $0.hasPrefix("joni_") || $0.hasPrefix("screen_") || $0.hasPrefix("terrain_0_") || $0.hasPrefix("wall_left_") })
        XCTAssertEqual(BotObservationContract.policyNames(for: 12)?.count, 1202)
        var features = [Double](repeating: 0, count: 155)
        features[0] = 0.5; features[1] = 0.3; features[4] = 1; features[6] = 1
        var row = BotObservation(type: "observation", captureTimestamp: 10, capturedAt: 10, availableAt: 10,
            generation: 1, roomID: 1, roomRevision: 0, detectionTimestamp: 10, detectionFrameID: 1,
            modelVersion: nil, bestKnightConfidence: nil, features: features, issues: [], terrainSource: "human-authored")
        row.authoredTerrain = BotScreenTerrain(width: 640, height: 360)
        var history = BotObservationHistory()
        let input = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 10,
            includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
            includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true,
            includeDoors: true, includeSharedPath: true, includeCompactGeometry: true))
        XCTAssertEqual(input.count, 820)
        XCTAssertEqual(input[3 * 205 + 8], 1)
        XCTAssertEqual(Array(input.suffix(105).prefix(4)), [1, 1, 0, 0])

        let version = BotObservationContract.groundContactPolicyVersion
        XCTAssertEqual(BotObservationContract.policyNames(for: version), names + ["knight_ground_contact"])
        let cases: [(Bool?, Float)] = [(true, 1), (false, -1), (nil, 0)]
        for (grounded, expected) in cases {
            row.grounded = grounded
            history.reset()
            let contact = try XCTUnwrap(history.input(row, previousAction: .neutral, now: 10,
                includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
                includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true,
                includeDoors: true, includeSharedPath: true, includeCompactGeometry: true,
                compactGeometryVersion: version))
            XCTAssertEqual(contact.count, 824)
            XCTAssertEqual(Array(contact.prefix(618)), Array(repeating: 0, count: 618))
            XCTAssertEqual(Array(contact.suffix(206).dropLast()), Array(input.suffix(205)))
            XCTAssertEqual(contact.last, expected)
        }
    }

    func testOptionalLandingGeometryUsesRelativeEdgesAndClearsAtRoomBoundary() throws {
        var geometry = BotScreenTerrain(width: 640, height: 360)
        geometry.floors = [
            .init(minimumX: 328, maximumX: 392, y: 128, confidence: 1),
            .init(minimumX: 100, maximumX: 520, y: 80, confidence: 1),
        ]
        func observation(_ time: Double, x: Double, y: Double, room: Int = 1) -> BotObservation {
            var features = [Double](repeating: 0, count: BotObservationContract.featureCount)
            features[0] = x / 640; features[1] = y / 640; features[3] = 32 / 640
            features[4] = 1; features[6] = 1
            return BotObservation(type: "observation", captureTimestamp: time, capturedAt: time, availableAt: time,
                generation: 1, roomID: room, roomRevision: 0, detectionTimestamp: time, detectionFrameID: 1,
                modelVersion: nil, bestKnightConfidence: nil, features: features, issues: [],
                terrainSource: "human-authored", authoredTerrain: geometry, grounded: false)
        }
        var history = BotObservationHistory()
        func encode(_ row: BotObservation) throws -> [Float] {
            try XCTUnwrap(history.input(row, previousAction: .neutral, now: row.availableAt,
                includeScreenTerrain: true, includeBlockade: true, includeGeoCaches: true, includeAtlas: true,
                includeAtlasMap: true, includeRoad: true, includePickup: true, includeCocoons: true,
                includeDoors: true, includeSharedPath: true, includeCompactGeometry: true,
                compactGeometryVersion: BotObservationContract.landingGeometryPolicyVersion))
        }
        let first = try encode(observation(10, x: 320, y: 175))
        let moved = try encode(observation(10.1, x: 336, y: 182))
        let shifted = try encode(observation(10.2, x: 336, y: 182, room: 2))
        XCTAssertEqual(moved.count, 848)
        let fixtures: [([Float], [Float])] = [
            (first, [1, 0.25, 2.25, 0.46875, 0, 0]),
            (moved, [1, -0.25, 1.75, 0.6875, 0.5, 0.21875]),
            (shifted, [1, -0.25, 1.75, 0.6875, 0, 0]),
        ]
        for (input, expected) in fixtures {
            for (actual, wanted) in zip(input.suffix(6), expected) {
                XCTAssertEqual(actual, wanted, accuracy: 0.00001)
            }
        }
        var grounded = observation(10.3, x: 336, y: 182, room: 2)
        grounded.grounded = true
        XCTAssertEqual(Array(try encode(grounded).suffix(6)), BotLandingGeometry.absent)
    }
}
