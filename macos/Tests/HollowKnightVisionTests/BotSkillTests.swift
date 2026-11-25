import XCTest
import ImageIO
@testable import HollowKnightVision

final class BotSkillTests: XCTestCase {
    func testBindingsAndPlanEligibility() throws {
        XCTAssertEqual(Set(BotSkill.allCases.flatMap(\.controls)), Set(SkillControl.allCases))
        XCTAssertEqual(SkillControl.castUp.buttons(elapsed: 0), [.quickCast, .up])
        XCTAssertEqual(SkillControl.focus.buttons(elapsed: 1), .actionA)
        XCTAssertEqual(SkillControl.doubleJump.buttons(elapsed: 0.22), [])
        XCTAssertEqual(SkillControl.doubleJump.buttons(elapsed: 0.35), .actionZ)
        for control in SkillControl.allCases where control != .native {
            let buttons = control.buttons(elapsed: 0)
            let action = BotAction(buttons: buttons)
            XCTAssertEqual(action.buttons, buttons)
            XCTAssertEqual(try JSONDecoder().decode(BotAction.self, from: JSONEncoder().encode(action)), action)
        }
        let menu = SkillSight(context: "Pause", capturedAt: 2, generation: 1, gameplay: false,
                              menu: .pause, selected: "pause.resume", lines: [], hasAbilityControls: false)
        XCTAssertTrue(menu.allows(.init(policy: .interact, control: .confirm, points: 65, cue: "pause.resume")))
        XCTAssertFalse(menu.allows(.init(policy: .interact, control: .confirm, points: 80, cue: "pause.options")))
        var plan = BotSkillPlan.initial
        XCTAssertEqual(try BotSkillPlan.decode(JSONEncoder().encode(plan)), plan)
        let sight = SkillSight(context: "Room 1", capturedAt: 10, generation: 1, gameplay: true,
                               menu: nil, selected: nil, lines: ["INSPECT"], hasAbilityControls: false)
        XCTAssertEqual(plan.choose(sight)?.control, .inspect)
        XCTAssertFalse(sight.allows(.init(policy: .fight, control: .cast, points: 99, cue: "")))
        plan = .init(objective: "Buy navigation supplies", success: "Item in inventory",
                     choices: plan.choices, shopping: [
                        .init(item: "Compass", desire: 95, maxGeo: 220, reserveGeo: 30),
                        .init(item: "Map", desire: 80, maxGeo: 40, reserveGeo: 30)],
                     shop: .init(wallet: 100, offers: [.init(item: "Compass", price: 200), .init(item: "Map", price: 30)]))
        XCTAssertEqual(plan.recommendedPurchase(visibleText: ["Map 30", "100"])?.0.item, "Map")
        XCTAssertNil(plan.recommendedPurchase(visibleText: ["Compass", "200", "100"]))
        XCTAssertThrowsError(try BotSkillPlan.decode(Data(#"{"objective":"Try","success":"Done","choices":[{"policy":"fight","control":"confirm","cue":"yes","points":101}],"shopping":[]}"#.utf8)))
    }

    func testLearnedAbilityPreservesTheMotorAndRequiresTheRightSkill() throws {
        let names = BotObservationContract.legacyFeatureNames
        let width = names.count * BotObservationContract.historyLength
        let motor = try BotNativePolicy(document: .init(schemaVersion: 1, observationSchemaVersion: 1,
            modelName: "test", featureNames: names, historyLength: 4, actionSizes: [3, 3, 2, 2], sourceCheckpointSHA256: "test",
            layers: [.init(inputSize: width, outputSize: 10, activation: "linear", weights: .init(repeating: 0, count: 10 * width), biases: .init(repeating: 0, count: 10))]))
        let head = BotLearnedSkill(version: 1, domain: .gameplay, skill: .traverse, observationSchema: 1, historyLength: 4,
                                   labels: ["none", "dash"], layers: [.init(inputSize: width, outputSize: 2, activation: "linear",
                                   weights: .init(repeating: 0, count: 2 * width), biases: [0, 1])])
        try head.validate(domain: .gameplay, skill: .traverse, policy: motor)
        XCTAssertThrowsError(try head.validate(domain: .hacker, skill: .traverse, policy: motor))
        XCTAssertThrowsError(try head.validate(domain: .gameplay, skill: .recover, policy: motor))
        let action = try head.action(for: .init(repeating: 0, count: width), base: BotAction(buttons: .left))
        XCTAssertEqual(action.buttons, [.left, .dash])

        var compact = BotLearnedSkill(version: 2, domain: .gameplay, skill: .traverse,
            observationSchema: 1, historyLength: 4, labels: ["none", "dash"],
            layers: [.init(inputSize: 2, outputSize: 2, activation: "linear",
                           weights: [0, 0, -1, 1], biases: [0.5, 0])], inputColumns: [2, width - 1])
        compact = try JSONDecoder().decode(BotLearnedSkill.self, from: JSONEncoder().encode(compact))
        try compact.validate(domain: .gameplay, skill: .traverse, policy: motor)
        var raw = [Float](repeating: 0, count: width)
        raw[0] = 3
        raw[width - 1] = 1
        XCTAssertEqual(try compact.action(for: raw, base: BotAction(buttons: .left)).buttons, [.left, .dash])
        raw[2] = 1
        XCTAssertEqual(try compact.action(for: raw, base: BotAction(buttons: .left)).buttons, [.left])
        XCTAssertThrowsError(try compact.action(for: [], base: BotAction(buttons: .left)))
        for invalid: [Int]? in [nil, [], [0], [-1, 2], [1, width], [1, 1], [2, 1]] {
            compact.inputColumns = invalid
            XCTAssertThrowsError(try compact.validate(domain: .gameplay, skill: .traverse, policy: motor))
        }
    }

    @MainActor
    func testLocalOllamaProposesExecutablePlan() async throws {
        guard let path = ProcessInfo.processInfo.environment["HKV_PLANNER_TEST_SCREENSHOT"] else {
            throw XCTSkip("Set HKV_PLANNER_TEST_SCREENSHOT to a captured tablet prompt")
        }
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let scan = try await GameTextReader.recognize(image, language: "en", context: "King's Pass")
        let goal = BotSkillPlan.defaultObjective
        let visible = scan.lines.map(\.text)
        let evidence = "Current goal: " + goal + "\nVisible OCR:\n" + visible.joined(separator: "\n")
        let sight = SkillSight(context: "King's Pass", capturedAt: scan.capturedAt, generation: 1, gameplay: true, menu: nil,
                              selected: nil, lines: visible, hasAbilityControls: true)
        let context = ThinkingContext(evidence: evidence, image: image, sight: sight, visible: visible)
        XCTAssertEqual(context.contract.rows().map(\.policy), ["interact"], "Tablet OCR: \(visible)")
        let answer = try await ThinkingClient.answer(model: .gemma, context: context)
        let plan = try context.contract.resolve(Data(answer.utf8))
        let row = try XCTUnwrap(context.contract.rows(selectedBy: plan).first { $0.selected }, answer)
        let player = BotPlaybackController(loadModel: false, now: { scan.capturedAt }, hasFocus: { true })
        player.skillSight = { sight }
        var selected: BotSkillChoice?, applied: InputBridgeButtons?
        player.onSkillEvent = { _, choice, _, event, _ in if event == "selected" { selected = choice } }
        player.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in
            applied = action.buttons; return true
        }, neutralize: { _ in }, end: { _ in })
        defer { player.stop() }
        let receipt = player.dispatch(plan, context: sight.context, generation: sight.generation, domain: .gameplay)
        XCTAssertEqual(selected?.candidateID, row.id, receipt)
        XCTAssertEqual(selected?.control, .inspect)
        XCTAssertEqual(selected?.cue, "Inspect")
        XCTAssertEqual(applied, .up)
        print("Local Gemma reply \(answer) · \(receipt) · \(row.target)")
    }

    /// Opt-in end-to-end test. The stock tutorial renders both frames and the game ignores all save slots.
    @MainActor
    func testPrivateGameInspectsTabletAndRecallsPassageAfterReopen() async throws {
        guard let game = ProcessInfo.processInfo.environment["HKV_TABLET_TEST_GAME"] else {
            throw XCTSkip("Set HKV_TABLET_TEST_GAME to the installed modded game executable")
        }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let episode = UUID().uuidString.lowercased()
        func write(_ info: [String: Any], _ name: String) throws {
            try JSONSerialization.data(withJSONObject: info).write(to: directory.appendingPathComponent(name), options: .atomic)
        }
        try write(["episode": episode, "seed": 441, "domain": "gameplay", "practice": "inspect-tablet"], "episode.json")
        let worker = Process()
        worker.executableURL = URL(fileURLWithPath: game)
        worker.currentDirectoryURL = directory
        worker.arguments = ["-batchmode", "-screen-width", "960", "-screen-height", "540", "-logFile", directory.appendingPathComponent("unity.log").path]
        worker.environment = ProcessInfo.processInfo.environment.merging(["HKV_GYM_DIRECTORY": directory.path, "SteamAppId": "367520", "SteamGameId": "367520"]) { _, new in new }
        worker.standardOutput = FileHandle.nullDevice
        worker.standardError = FileHandle.nullDevice
        try worker.run()
        defer { if worker.isRunning { worker.terminate(); worker.waitUntilExit() } }
        var step = 0
        var gameTime = 0.0
        func frame(_ buttons: InputBridgeButtons?, ticks: Int = 30) async throws -> CGImage {
            if let buttons {
                step += 1
                try write(["episode": episode, "step": step, "buttons": Int(buttons.rawValue), "ticks": ticks], "request.json")
            }
            let reply = directory.appendingPathComponent("reply.json")
            let deadline = Date().addingTimeInterval(step == 0 ? 120 : 20)
            while !FileManager.default.fileExists(atPath: reply.path) {
                if let error = try? String(contentsOf: directory.appendingPathComponent("error.txt"), encoding: .utf8) {
                    throw BotRuntimeError.incompatible(error)
                }
                guard worker.isRunning, Date() < deadline else { throw BotRuntimeError.incompatible("Private game timed out at step " + String(step)) }
                try await Task.sleep(for: .milliseconds(40))
            }
            let row = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: reply)) as? [String: Any])
            try FileManager.default.removeItem(at: reply)
            XCTAssertEqual(row["noSave"] as? Bool, true)
            XCTAssertEqual(row["step"] as? Int, step)
            gameTime = Double(try XCTUnwrap(row["ticks"] as? Int)) / 60
            XCTAssertEqual(row["sceneName"] as? String, "Tutorial_01")
            let pixels = try Data(contentsOf: directory.appendingPathComponent("frame.rgb"))
            XCTAssertEqual(pixels.count, 640 * 360 * 3)
            var raster = Data(capacity: pixels.count)
            let stride = 640 * 3
            for y in (0..<360).reversed() { raster.append(pixels[y*stride..<(y+1)*stride]) }
            let provider = try XCTUnwrap(CGDataProvider(data: raster as CFData))
            return try XCTUnwrap(CGImage(width: 640, height: 360, bitsPerComponent: 8, bitsPerPixel: 24,
                bytesPerRow: stride, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: [], provider: provider,
                decode: nil, shouldInterpolate: false, intent: .defaultIntent))
        }
        _ = try await frame(nil)
        _ = try await frame([])
        let notebookURL = directory.appendingPathComponent("notebook.json")
        let reader = GameTextReader(notebook: GameTextNotebook(url: notebookURL))
        let room = "King's Pass tablet"
        let prompt = try await GameTextReader.recognize(frame([]), language: "en", context: room)
        let sight = SkillSight(context: room, capturedAt: gameTime, generation: 1, gameplay: true,
                              menu: nil, selected: nil, lines: prompt.lines.map(\.text), hasAbilityControls: true)
        let plan: BotSkillPlan
        if ProcessInfo.processInfo.environment["HKV_TABLET_TEST_GEMMA"] == "1" {
            let request = ThinkingContext(evidence: "Room: \(room)\nVisible OCR: \(sight.lines.joined(separator: "\n"))",
                image: prompt.image, sight: sight, visible: sight.lines)
            let reply = try await ThinkingClient.answer(model: .gemma, context: request)
            plan = try request.contract.resolve(Data(reply.utf8))
            let choice = try XCTUnwrap(plan.choose(sight), reply)
            let row = try XCTUnwrap(request.contract.rows(selectedBy: plan).first { $0.id == choice.candidateID })
            XCTAssertEqual(row.policy, "interact")
            print("Tutorial_01 · Gemma \(reply) · \(row.id) \(row.policy) \(row.target)")
        } else if let path = ProcessInfo.processInfo.environment["HKV_TABLET_TEST_REPLY"] {
            let contract = ThinkingPolicyContract(sight: sight, visible: sight.lines)
            plan = try contract.resolve(Data(contentsOf: URL(fileURLWithPath: path)))
        } else if let path = ProcessInfo.processInfo.environment["HKV_TABLET_TEST_PLAN"] {
            plan = try BotSkillPlan.decode(Data(contentsOf: URL(fileURLWithPath: path)))
        } else { plan = .initial }
        XCTAssertEqual(plan.choose(sight)?.control, .inspect)
        let player = BotPlaybackController(loadModel: false, domain: .gameplay, now: { gameTime }, hasFocus: { true })
        let recorder = BotEvaluationRecorder(root: directory.appendingPathComponent("evaluations"))
        XCTAssertTrue(recorder.start(domain: .gameplay, model: nil, reference: GroundTruthLineDocument()))
        defer { recorder.stop() }
        player.onSkillEvent = { plan, choice, context, event, timestamp in
            recorder.recordSkill(plan, choice: choice, context: context, event: event, at: timestamp)
        }
        var applied = InputBridgeButtons()
        player.skillSight = { sight }
        player.configure(canContinue: { true }, begin: { UUID() }, apply: { action, _, _ in applied = action.buttons; return true },
                         neutralize: { _ in }, end: { _ in })
        player.didInspect = { view, start in reader.beginInspection(view, at: start) }
        _ = player.dispatch(plan, context: room, generation: 1, domain: .gameplay)
        XCTAssertEqual(applied, .up)
        let press = applied
        player.stop()
        _ = try await frame(press, ticks: 7)
        var finalImage = prompt.image
        var previous = [GameTextLine]()
        for _ in 0..<22 {
            let pixels = try await frame([])
            let read = try await GameTextReader.recognize(pixels, language: "en", context: room)
            // The Gym freezes Unity between actions while the test runner reads the pixels.
            let scan = GameTextScan(image: pixels, language: read.language, context: read.context, lines: read.lines,
                                    capturedAt: gameTime, milliseconds: read.milliseconds)
            try await reader.store(scan, previous: previous)
            previous = scan.lines
            finalImage = scan.image
            if reader.skillReading?.passage != nil { break }
        }
        let verifiedScan = try XCTUnwrap(reader.skillReading)
        let passage = try XCTUnwrap(verifiedScan.passage, "Final OCR at game time \(gameTime): \(verifiedScan.visible)")
        let reopened = try await GameTextNotebook(url: notebookURL).entries()
        let recall = GameTextNotebook.recall(reopened, question: "What did the sign say about Focus?", context: "Dirtmouth", current: [], language: "en")
        XCTAssertTrue(passage.localizedCaseInsensitiveContains("Focus your soul"), passage)
        XCTAssertTrue(recall.contains { $0.contains(passage) })
        let freshReader = GameTextReader(notebook: GameTextNotebook(url: notebookURL))
        let evidence = await freshReader.evidence(image: prompt.image, context: "Dirtmouth", language: "en", question: "What did the sign say about Focus?", enabled: false)
        XCTAssertTrue(evidence.contains(passage))
        let eventURL = try XCTUnwrap(recorder.lastDirectory).appendingPathComponent("skill-events.jsonl")
        let events = try Data(contentsOf: eventURL)
        let first = try XCTUnwrap(events.split(separator: 10).first)
        let selection = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(first)) as? [String: Any])
        XCTAssertEqual(selection["event"] as? String, "selected")
        let choice = try XCTUnwrap(selection["choice"] as? [String: Any])
        XCTAssertEqual(choice["policy"] as? String, "interact")
        XCTAssertEqual(choice["control"] as? String, "inspect")
        if let output = ProcessInfo.processInfo.environment["HKV_TABLET_TEST_ARTIFACTS"] {
            let root = URL(fileURLWithPath: output)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            for (name, image) in [("inspect.png", prompt.image), ("tablet.png", finalImage)] {
                let dest = try XCTUnwrap(CGImageDestinationCreateWithURL(root.appendingPathComponent(name) as CFURL, "public.png" as CFString, 1, nil))
                CGImageDestinationAddImage(dest, image, nil)
                XCTAssertTrue(CGImageDestinationFinalize(dest))
            }
            try events.write(to: root.appendingPathComponent("skill-events.jsonl"), options: .atomic)
            let result: [String: Any] = ["plan": try JSONSerialization.jsonObject(with: JSONEncoder().encode(plan)), "scene": "Tutorial_01", "saveDisabled": true, "button": press.rawValue,
                                         "episode": episode, "skillEvents": "skill-events.jsonl", "spawn": "beside-tablet",
                                         "prompt": sight.lines, "storedPassage": passage, "recallAfterReopen": evidence]
            try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys, .prettyPrinted])
                .write(to: root.appendingPathComponent("recall.json"), options: .atomic)
        }
        print("Private game tablet, persisted and recalled: " + passage)
    }
}
