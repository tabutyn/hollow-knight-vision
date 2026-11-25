import XCTest
import ImageIO
@testable import HollowKnightVision

final class ThinkingEventTests: XCTestCase {
    func testSignNPCDeadEndAndArrivalAreEdges() {
        var gate = ThinkingEventDetector()
        let route = BotRouteTarget(chart: "Pass", goal: .init(100, 0), waypoint: .init(50, 0),
            hint: .init(dx: 1, dy: 0, clearance: 0, distance: 0.4, frontier: true))
        var state = ThinkingSignals(profile: UUID(), generation: 1, room: "Pass", mode: "modded",
            scene: .init(room: 1, grounded: true, recovery: nil, enemies: [], cocoons: [], routes: [route],
                         navigationPosition: .init(chart: "Pass", feet: .init(0, 0), arrivalRadius: 3)))
        XCTAssertEqual(gate.events(state, at: 0), ["First observation"])
        XCTAssertEqual(gate.events(state, at: 65), [])
        state.sign = true
        XCTAssertEqual(gate.events(state, at: 66), ["Sign visible"])
        state.npc = true
        XCTAssertEqual(gate.events(state, at: 67), ["NPC visible"])
        XCTAssertEqual(gate.events(state, at: 68), [])
        state.scene?.navigationPosition = .init(chart: "Pass", feet: .init(98, 0), arrivalRadius: 3)
        state.scene?.routes = []
        XCTAssertEqual(gate.events(state, at: 69), ["Explore destination reached"])
        XCTAssertEqual(gate.events(state, at: 70), [])
        XCTAssertEqual(gate.events(state, at: 71), ["Dead end"])
        XCTAssertEqual(gate.events(state, at: 72), [])
        state.outcomeID = 1
        state.outcome = "Waypoint reached"
        XCTAssertEqual(gate.events(state, at: 73), ["Waypoint reached"])
        XCTAssertEqual(gate.events(state, at: 74), [])
    }

    func testSelectedRouteTakesPrecedenceOverTheUnchosenFrontier() {
        let frontier = BotRouteTarget(chart: "Pass", goal: .init(80, 0), waypoint: .init(80, 0),
            hint: .init(dx: 1, dy: 0, clearance: 0, distance: 0.3, frontier: true))
        let selected = BotRouteTarget(chart: "Pass", goal: .init(-70, 0), waypoint: .init(-70, 0),
            hint: .init(dx: -1, dy: 0, clearance: 0, distance: 0.2, frontier: false))
        var state = ThinkingSignals(profile: nil, generation: 1, room: "Pass", mode: "modded",
            scene: .init(room: 1, grounded: true, recovery: nil, enemies: [], cocoons: [], routes: [frontier, selected],
                         navigationPosition: .init(chart: "Pass", feet: .init(0, 0), arrivalRadius: 3)))
        var gate = ThinkingEventDetector()
        _ = gate.events(state, at: 0)
        state.activeRoute = selected
        state.scene?.navigationPosition = .init(chart: "Pass", feet: frontier.goal, arrivalRadius: 3)
        XCTAssertEqual(gate.events(state, at: 1), [])
        state.scene?.navigationPosition = .init(chart: "Pass", feet: selected.goal, arrivalRadius: 3)
        XCTAssertEqual(gate.events(state, at: 2), ["Target reached"])
        XCTAssertEqual(gate.events(state, at: 3), [])
    }

    func testNewTrackedLandmarksAndContinuousPrompts() {
        var gate = ThinkingEventDetector()
        var state = ThinkingSignals(profile: nil, generation: 1, room: "Pass", mode: "modded",
                                    sign: true, npc: true, signTracks: [1], npcTracks: [2])
        XCTAssertEqual(gate.events(state, at: 0), ["First observation", "Sign visible", "NPC visible"])
        XCTAssertEqual(gate.events(state, at: 90), [])
        state.signTracks.insert(3)
        XCTAssertEqual(gate.events(state, at: 91), ["Sign visible"])
        state.npcTracks.insert(4)
        XCTAssertEqual(gate.events(state, at: 92), ["NPC visible"])
        state.sign = false
        XCTAssertEqual(gate.events(state, at: 93), [])
        state.sign = true
        XCTAssertEqual(gate.events(state, at: 94), [])
        state.sign = false
        XCTAssertEqual(gate.events(state, at: 95), [])
        XCTAssertEqual(gate.events(state, at: 98), [])
        state.sign = true
        XCTAssertEqual(gate.events(state, at: 99), ["Sign visible"])
    }

    @MainActor
    func testRecordedTabletScreenshotsBecomeUniqueNotebookTextWithoutAModel() async throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_QUEST_TABLET_FIXTURE"] else {
            throw XCTSkip("Set HKV_QUEST_TABLET_FIXTURE to a recorded inspect.png and tablet.png directory")
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let profile = SaveProfileAtlas(slot: 1)
        let reader = GameTextReader(directory: dir)
        reader.selectProfile(profile)
        let fixture = URL(fileURLWithPath: path)
        func scan(_ filename: String) async throws -> GameTextScan {
            let src = try XCTUnwrap(CGImageSourceCreateWithURL(fixture.appendingPathComponent(filename) as CFURL, nil))
            let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(src, 0, nil))
            return try await GameTextReader.recognize(image, language: "en", context: "King’s Pass")
        }
        let prompt = try await scan("inspect.png")
        try await reader.store(prompt, previous: prompt.lines)
        let page = try await scan("tablet.png")
        try await reader.store(page, previous: page.lines)
        let before = reader.memory.map(\.id)
        XCTAssertGreaterThan(before.count, 1)
        for _ in 0..<4 { try await reader.store(page, previous: page.lines) }
        XCTAssertEqual(reader.memory.map(\.id), before)
        let reopened = GameTextReader(directory: dir)
        reopened.selectProfile(profile)
        let evidence = try await reopened.policyContext()
        XCTAssertTrue(evidence.localizedCaseInsensitiveContains("enemies") || evidence.localizedCaseInsensitiveContains("striking"), evidence)
        XCTAssertTrue(evidence.contains("INSPECT"), evidence)
        XCTAssertEqual(reopened.memory.map(\.id), before)
        reopened.selectProfile(.init(slot: 1))
        let fresh = try await reopened.policyContext()
        XCTAssertFalse(fresh.contains("INSPECT"))
    }
}
