import XCTest
@testable import HollowKnightVision

final class BotStarterPolicyTests: XCTestCase {
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
                bot.queue(.init(objective: "Test", success: "Control observed", choices: [choice], shopping: []),
                          context: sight.context, generation: sight.generation)
                bot.start(domain: domain)
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
