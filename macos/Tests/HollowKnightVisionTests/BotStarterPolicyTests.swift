import XCTest
@testable import HollowKnightVision

final class BotStarterPolicyTests: XCTestCase {
    func testAutomaticDispatchRejectsLostCueStaleFrameAndPausedPlayback() {
        let bot = BotPlaybackController(loadModel: false, now: { 10 }, hasFocus: { true })
        var leases = 0, pressed = [InputBridgeButtons]()
        var cue = [String](), capture = 10.0
        bot.skillSight = { SkillSight(context: "Pass", capturedAt: capture, generation: 3, gameplay: true,
            menu: nil, selected: nil, lines: cue, hasAbilityControls: false) }
        bot.configure(canContinue: { true }, begin: { leases += 1; return UUID() },
            apply: { a, _, _ in pressed.append(a.buttons); return true }, neutralize: { _ in }, end: { _ in })
        let plan = BotSkillPlan(objective: "Read", success: "Passage", choices: [
            .init(policy: .interact, control: .inspect, points: 90, cue: "INSPECT")], shopping: [])
        func send(_ generation: UInt64 = 3) { _ = bot.dispatch(plan, context: "Pass", generation: generation, domain: .gameplay) }
        send()
        cue = ["INSPECT"]; capture = 9; send()
        capture = 10; send(2)
        bot.automaticPoliciesEnabled = false; send()
        XCTAssertEqual(leases, 0)
        bot.automaticPoliciesEnabled = true; send(); send()
        XCTAssertEqual(leases, 1)
        XCTAssertEqual(pressed, [.up])
        bot.stop()
    }

    func testAutomaticManeuverReturnsToAnExistingMotorLease() {
        var time = 10.0, acquired = 0, released = 0
        let bot = BotPlaybackController(loadModel: false, now: { time }, hasFocus: { true })
        bot.skillSight = { SkillSight(context: "Pass", capturedAt: time, generation: 3, gameplay: true,
            menu: nil, selected: nil, lines: [], hasAbilityControls: false) }
        bot.configure(canContinue: { true }, begin: { acquired += 1; return UUID() },
            apply: { _, _, _ in true }, neutralize: { _ in }, end: { _ in released += 1 })
        bot.start(domain: .gameplay)
        let plan = BotSkillPlan(objective: "Cross", success: "Landing", choices: [
            .init(policy: .traverse, control: .jumpRight, points: 65, cue: "")], shopping: [])
        var events = [String]()
        let ended = expectation(description: "Bounded maneuver completes")
        bot.onSkillEvent = { _, _, _, event, _ in
            events.append(event)
            if event.contains("maneuver finished") { ended.fulfill() }
        }
        _ = bot.dispatch(plan, context: "Pass", generation: 3, domain: .gameplay)
        XCTAssertEqual(bot.lastAction.buttons, [.right, .actionZ])
        time += 0.3
        wait(for: [ended], timeout: 2)
        XCTAssertEqual(events.first, "selected")
        XCTAssertTrue(bot.isActive)
        XCTAssertEqual(acquired, 1)
        XCTAssertEqual(released, 0)
        bot.stop()
        XCTAssertEqual(released, 1)
    }

    func testAllSixPoliciesExecuteWithoutWeightsThroughPlayback() throws {
        let expected: [BotSkill: InputBridgeButtons] = [.explore: .left, .traverse: .left,
            .fight: [.left, .actionX], .recover: .right, .progress: [.right, .actionX], .interact: .up]
        let widths: [BotSkill: Int] = [.explore: 123, .traverse: 274, .fight: 130, .recover: 139, .progress: 153]
        for domain in BotDomain.allCases {
            for skill in BotSkill.allCases {
                let bot = BotPlaybackController(loadModel: false, domain: domain, now: { 10 }, hasFocus: { true })
                let choice = BotSkillChoice(policy: skill, control: skill == .interact ? .inspect : .native,
                                            points: skill.initialPoints, cue: skill == .interact ? "INSPECT" : "")
                let sight = SkillSight(context: "Pass", capturedAt: 10, generation: 7, gameplay: true, menu: nil,
                                       selected: nil, lines: ["INSPECT"], hasAbilityControls: false)
                bot.skillSight = { sight }
                var sent = [InputBridgeButtons](), released = false
                bot.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in
                    sent.append(action.buttons); return true
                }, neutralize: { _ in }, end: { _ in released = true })
                _ = bot.dispatch(.init(objective: "Test", success: "Control observed", choices: [choice], shopping: []),
                          context: sight.context, generation: sight.generation, domain: domain)
                XCTAssertTrue(bot.isActive)
                if let width = widths[skill] {
                    XCTAssertEqual(try BotSkillInputLayout(skill: skill, schema: 14).columns.count, width)
                    var f = [Double](repeating: 0, count: BotObservationContract.featureCount)
                    f[0] = 0.5; f[4] = 1; f[96] = -0.06; f[101] = 1
                    var obs = BotObservation(type: "observation", captureTimestamp: 10, capturedAt: 10, availableAt: 10,
                        generation: 7, roomID: 1, roomRevision: 1, detectionTimestamp: 10, detectionFrameID: nil,
                        modelVersion: nil, bestKnightConfidence: 1, features: f, issues: [],
                        terrainSource: domain == .hacker ? "human-authored" : "detected")
                    obs.roadHint = .init(dx: -0.5, dy: 0, clearance: 1, distance: 0.5, frontier: true)
                    obs.geoCaches = [.init(x: 0.55, y: 0, width: 0.03, height: 0.03)]
                    bot.observe(obs)
                    let tick = expectation(description: skill.rawValue)
                    DispatchQueue.main.async { tick.fulfill() }
                    wait(for: [tick], timeout: 1)
                }
                XCTAssertEqual(sent.last, expected[skill], "\(domain) / \(skill)")
                bot.stop()
                XCTAssertTrue(released)
            }
        }
    }
}
