import Foundation
import XCTest
@testable import HollowKnightVision

/// Opt-in because this launches the installed game and requires its authored King's Pass floors.
final class PolicyHandoffGameTests: XCTestCase {
    @MainActor
    func testPrivateGameExecutesFocusAndBothTraverseOffers() async throws {
        let env = ProcessInfo.processInfo.environment
        guard let executable = env["HKV_HANDOFF_GAME"], let labels = env["HKV_HANDOFF_LABELS"] else {
            throw XCTSkip("Set HKV_HANDOFF_GAME and HKV_HANDOFF_LABELS to run the save-disabled game")
        }
        let session = UUID().uuidString.lowercased()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("policy-handoff-" + session)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        func write(_ json: [String: Any], _ name: String) throws {
            try JSONSerialization.data(withJSONObject: json).write(to: root.appendingPathComponent(name), options: .atomic)
        }
        try write(["episode":session, "domain":"hacker", "seed":441, "practice":"policy-handoff"], "episode.json")
        let game = Process()
        game.executableURL = URL(fileURLWithPath: executable)
        game.arguments = ["-batchmode", "-screen-width", "960", "-screen-height", "540",
                          "-logFile", root.appendingPathComponent("unity.log").path]
        game.environment = env.merging(["HKV_GYM_DIRECTORY":root.path, "SteamAppId":"367520", "SteamGameId":"367520"]) { _, new in new }
        game.standardOutput = FileHandle.nullDevice
        game.standardError = FileHandle.nullDevice
        try game.run()
        defer { if game.isRunning { game.terminate(); game.waitUntilExit() } }

        var step = 0, ticks = 0
        var time = 0.0
        func sample(_ buttons: InputBridgeButtons?) async throws -> ReceiverGroundTruthSample {
            if let buttons {
                step += 1
                try write(["episode":session, "step":step, "ticks":6, "buttons":Int(buttons.rawValue)], "request.json")
            }
            let reply = root.appendingPathComponent("reply.json")
            let deadline = Date().addingTimeInterval(step == 0 ? 120 : 12)
            while !FileManager.default.fileExists(atPath: reply.path) {
                if let message = try? String(contentsOf: root.appendingPathComponent("error.txt"), encoding: .utf8) {
                    throw BotRuntimeError.incompatible(message)
                }
                guard Date() < deadline, game.isRunning else { throw BotRuntimeError.incompatible("Game stopped at step \(step)") }
                try await Task.sleep(for: .milliseconds(20))
            }
            let data = try Data(contentsOf: reply)
            try FileManager.default.removeItem(at: reply)
            let fields = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
            XCTAssertEqual(fields["noSave"] as? Bool, true)
            XCTAssertEqual(fields["step"] as? Int, step)
            ticks = try XCTUnwrap(fields["ticks"] as? Int)
            time = Double(ticks) / 60
            let frame = try JSONDecoder().decode(ReceiverGroundTruthSample.self, from: data)
            XCTAssertEqual(frame.sceneName, "Tutorial_01")
            return frame
        }
        var frame = try await sample(nil)
        // A measured nearby destination on the authored floor, independent of the automatic frontier.
        let path = try GymHackerPath(labels: URL(fileURLWithPath: labels), connections: root.appendingPathComponent("links.json"),
                                     objective: .init(x: frame.heroX - 5, y: frame.heroY))
        var sight = SkillSight(context: frame.sceneName, capturedAt: time, generation: 1, gameplay: true,
                              menu: nil, selected: nil, lines: [], hasAbilityControls: true)
        let bot = BotPlaybackController(loadModel: false, domain: .hacker, now: { time }, hasFocus: { true })
        bot.skillSight = { sight }
        var held = InputBridgeButtons()
        bot.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in held = action.buttons; return true },
                      neutralize: { _ in held = [] }, end: { _ in held = [] })
        defer { bot.stop() }
        func publish() async throws {
            let row = try XCTUnwrap(path.observe(frame, ticks: ticks, direction: held.contains(.right) ? 1 : held.contains(.left) ? -1 : 0))
            sight = SkillSight(context: frame.sceneName, capturedAt: time, generation: 1, gameplay: true, menu: nil,
                               selected: nil, lines: [], hasAbilityControls: true, scene: row.policyScene)
            bot.observe(row)
            try await Task.sleep(for: .milliseconds(65))
        }
        try await publish()
        var records = [[String: Any]]()
        for role in ["explore", "progress", "recover"] {
            let before = frame
            let contract = ThinkingPolicyContract(sight: sight, visible: [])
            let candidates = try XCTUnwrap(contract.offer["candidates"] as? [String: [String: Any]])
            let handle = try XCTUnwrap(candidates.keys.sorted().first { key in
                let entry = candidates[key]!, params = entry["params"] as? [String: Any]
                if role == "recover" { return params?["method"] as? String == "focus" }
                return entry["policy"] as? String == "traverse" && params?["road_frontier"] as? Bool == (role == "explore")
            }, "Missing \(role) at \(frame.heroX), \(frame.heroY): \(contract.offer) recovery \(String(describing: sight.scene?.recovery))")
            let scores = ["scores":[handle:99]]
            let plan = try contract.resolve(JSONSerialization.data(withJSONObject: scores))
            let choice = try XCTUnwrap(plan.choose(sight))
            let result = bot.dispatch(plan, context: frame.sceneName, generation: 1, domain: .hacker)
            XCTAssertTrue(result.contains("Dispatched"), result)
            var buttons = [Int]()
            for _ in 0..<(role == "recover" ? 20 : 7) {
                buttons.append(Int(held.rawValue))
                frame = try await sample(held)
                try await publish()
                if !bot.isActive { break }
            }
            let start = try XCTUnwrap(before.actors?.recovery), end = try XCTUnwrap(frame.actors?.recovery)
            if role == "recover" {
                XCTAssertTrue(buttons.contains(Int(InputBridgeButtons.actionA.rawValue)))
                XCTAssertGreaterThan(end.health, start.health)
                XCTAssertLessThan(end.soul, start.soul)
            } else {
                guard case .traverse(let target) = choice.binding?.target else { XCTFail("Lost target"); return }
                let direction: InputBridgeButtons = target.hint.dx > 0 ? .right : .left
                XCTAssertTrue(buttons.contains { $0 & Int(direction.rawValue) != 0 })
                XCTAssertGreaterThan((frame.heroX - before.heroX) * target.hint.dx, 0.04)
            }
            records.append(["selector":role, "offer":contract.offer, "reply":scores, "buttons":buttons,
                            "fromX":before.heroX, "toX":frame.heroX, "fromHealth":start.health,"toHealth":end.health,
                            "fromSoul":start.soul,"toSoul":end.soul])
            bot.stop()
            // Wait for the Knight to regain movement control after the Focus animation.
            for _ in 0..<(role == "recover" ? 8 : 2) {
                frame = try await sample([])
                try await publish()
            }
        }
        if env["HKV_HANDOFF_GEMMA"] == "1" {
            let request = ThinkingContext(evidence: "King’s Pass, grounded on an authored platform. Continue mapping the Atlas.",
                                          hasImage: false, sight: sight)
            let answer = try await ThinkingClient.answer(model: .gemma, image: nil, context: request)
            let plan = try request.contract.resolve(Data(answer.utf8))
            let choice = try XCTUnwrap(plan.choose(sight), answer)
            let result = bot.dispatch(plan, context: frame.sceneName, generation: 1, domain: .hacker)
            XCTAssertTrue(result.contains("Dispatched"), result)
            let before = frame
            var buttons = [Int]()
            for _ in 0..<7 {
                buttons.append(Int(held.rawValue))
                frame = try await sample(held)
                try await publish()
                if !bot.isActive { break }
            }
            XCTAssertTrue(buttons.contains { $0 != 0 })
            if case .traverse(let target) = choice.binding?.target {
                XCTAssertGreaterThan((frame.heroX - before.heroX) * target.hint.dx, 0.04)
            }
            records.append(["selector":"gemma", "model":ThinkingModel.gemma.rawValue,
                            "offer":request.contract.offer, "reply":try JSONSerialization.jsonObject(with: Data(answer.utf8)),
                            "selected":try XCTUnwrap(choice.candidateID), "buttons":buttons, "fromX":before.heroX, "toX":frame.heroX])
        }
        if let output = env["HKV_HANDOFF_TRACE"] {
            let trace: [String: Any] = ["scene":frame.sceneName,"seed":441,"saveDisabled":true,"scoreSource":"fixed scores for controller cases; local model for gemma case",
                                        "decisions":records]
            try JSONSerialization.data(withJSONObject: trace, options: [.prettyPrinted,.sortedKeys])
                .write(to: URL(fileURLWithPath: output), options: .atomic)
        }
    }
}
