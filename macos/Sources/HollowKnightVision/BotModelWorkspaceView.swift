import AppKit
import SwiftUI

enum BotModelTab: String, CaseIterable, Identifiable {
    case summary = "Summary"
    case gameplay = "Gameplay"
    case hacker = "Hacker"
    var id: Self { self }
    var domain: BotDomain? {
        switch self { case .summary: nil; case .gameplay: .gameplay; case .hacker: .hacker }
    }
}

struct BotModelWorkspaceView: View, Equatable {
    @ObservedObject var controller: BotTrainingWorkspaceController
    let trainingAllowed: Bool
    let reloadModel: () -> Void
    @State private var selectedIDs = [BotDomain: UUID]()
    @State private var playing = false
    @State private var tab = BotModelTab.summary
    @State private var pendingDeletion: BotSnippet?
    @State private var shownError: String?
    private var current: BotTrainingController { controller.model(tab.domain ?? .hacker) }
    private var snippet: BotSnippet? { current.snippets.first { $0.id == selectedIDs[current.store.domain] } ?? current.snippets.first }
    static func == (lhs: Self, rhs: Self) -> Bool { lhs.controller === rhs.controller && lhs.trainingAllowed == rhs.trainingAllowed }

    var body: some View {
        VStack(spacing: 0) {
            controls
            Divider()
            if tab == .summary {
                ScrollView {
                    HStack(alignment: .top, spacing: 16) {
                        ForEach(BotDomain.allCases) { domain in summary(controller.model(domain)) }
                    }.padding(16)
                }
            } else {
                HSplitView {
                    recordings.frame(minWidth: 280, idealWidth: 360, maxWidth: 460)
                    ScrollView {
                        if let snippet { BotClipPreview(clip: snippet, playing: $playing).padding(16) }
                        else { Text("No \(current.store.domain.title) captures yet.").foregroundStyle(.secondary).padding() }
                    }.frame(minWidth: 400)
                }
            }
        }
        .buttonStyle(.bordered).background(Color.black)
        .onAppear { controller.refresh() }
        .onChange(of: selectedIDs) { _, _ in playing = false }
        .onChange(of: tab) { _, _ in playing = false }
        .onDisappear { playing = false }
        .onChange(of: controller.gameplay.errorMessage) { _, message in if let message { shownError = message } }
        .onChange(of: controller.hacker.errorMessage) { _, message in if let message { shownError = message } }
        .alert("Bot", isPresented: Binding(get: { shownError != nil }, set: { visible in
            if !visible {
                shownError = nil
                controller.gameplay.clearError()
                controller.hacker.clearError()
            }
        })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(shownError ?? "")
        }
        .confirmationDialog("Permanently delete \(pendingDeletion.map { controller.model($0.domain).clipName($0) } ?? "clip")?",
            isPresented: Binding(get: { pendingDeletion != nil }, set: { if !$0 { pendingDeletion = nil } }), titleVisibility: .visible) {
                Button("Delete Clip", role: .destructive) {
                    if let clip = pendingDeletion { controller.model(clip.domain).delete(clip) }
                    pendingDeletion = nil; playing = false
                }
                Button("Cancel", role: .cancel) { pendingDeletion = nil }
            } message: {
                Text("This permanently removes the clip. Deleting the last clip from a run also removes its replay and captured source frames.")
            }
    }

    private var controls: some View {
        let domain = tab.domain
        let count = domain.map { controller.model($0).newClipCount } ?? controller.newClipCount
        let running = domain.map { controller.model($0).isRunning } ?? controller.isRunning
        let canTrain = domain.map { controller.model($0).canTrain } ?? controller.canTrain
        return ZStack {
            HStack(spacing: 4) {
                Spacer(minLength: 8)
                HStack(spacing: 3) { Text("+\(count)"); Text("🤖") }
                    .font(.caption.monospacedDigit()).foregroundStyle(count > 0 ? .blue : .secondary)
                    .accessibilityIdentifier("bot-new-sample-count")
                    .accessibilityLabel("\(count) new clips")
                    .help(domain == nil
                        ? "New recordings since each bot last finished training. Paired recordings count once."
                        : "New clips since this bot last finished training.")
                if running {
                    Button("Stop Training") { if let domain { controller.model(domain).cancel() } else { controller.cancel() } }.tint(.red)
                    ProgressView().controlSize(.small)
                } else {
                    Button {
                        playing = false
                        if let domain { controller.model(domain).start(reload: reloadModel) }
                        else { controller.start(reload: reloadModel) }
                    } label: {
                        HStack(spacing: 5) { if count > 0 { Circle().fill(.blue).frame(width: 7, height: 7) }; Text("Train") }
                    }
                    .disabled(!trainingAllowed || !canTrain).accessibilityIdentifier("bot-train-model")
                    .help(domain.map { "Train \($0.title) from all its completed captures." } ?? "Train Gameplay and Hacker in tandem from their own captures.")
                }
                Text(domain.map { controller.model($0).versionLabel(controller.model($0).activeID) }
                     ?? "G \(controller.gameplay.versionLabel(controller.gameplay.activeID)) · H \(controller.hacker.versionLabel(controller.hacker.activeID))")
                    .monospacedDigit().foregroundStyle(.secondary).accessibilityIdentifier("bot-active-version")
            }
            StableSegmentedPicker(label: "Bot section", choices: BotModelTab.allCases,
                title: { $0.rawValue }, selection: $tab).frame(width: 300)
        }
        .font(.caption).buttonStyle(.bordered).padding(.horizontal, 12).padding(.vertical, 8)
        .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading).background(.regularMaterial)
    }

    private var recordings: some View {
        List(selection: Binding(get: { snippet?.id }, set: { selectedIDs[current.store.domain] = $0 })) {
            ForEach(current.snippets) { clip in
                HStack(spacing: 8) {
                    Text(current.clipName(clip)).font(.callout)
                    if !clip.canTrain { Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange).help(clip.error ?? clip.manifest.status) }
                    Spacer(minLength: 4)
                    Text(clip.manifest.createdAt, format: .dateTime.month().day().hour().minute().second())
                    Text(String(format: "· %.1fs", clip.manifest.duration))
                    Button { pendingDeletion = clip; playing = false } label: { Image(systemName: "trash") }
                        .buttonStyle(.borderless).disabled(current.isRunning || clip.manifest.status == "recording")
                        .accessibilityLabel("Delete \(current.clipName(clip))")
                }.font(.caption.monospacedDigit()).lineLimit(1).padding(.vertical, 4).tag(clip.id)
            }
        }.listStyle(.sidebar)
    }

    private func summary(_ model: BotTrainingController) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(model.store.domain.title).font(.title2.bold())
            Text("Captures \(model.captureManifests.count.formatted()) · Frames \(model.summaryFrameCount.formatted()) · New Clips \(model.newClipCount.formatted())")
                .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            Divider()
            HStack {
                Text("Versions").font(.headline); Spacer()
                Button("Restore Previous") { model.restore(reload: reloadModel) }
                    .disabled(model.isRunning || !model.canRestore)
            }
            ForEach(model.versions) { version in
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text(model.versionLabel(version.id)).bold(); Spacer()
                        if version.id == model.activeID { Label("Active", systemImage: "checkmark.circle.fill").foregroundStyle(.green) }
                        else { Button("Use Version") { model.activate(version, reload: reloadModel) }.disabled(model.isRunning || !model.store.canActivate(version)) }
                    }
                    if let result = version.result {
                        score("Action agreement", result.candidate.exactActionAccuracy, result.baseline?.exactActionAccuracy)
                        if let jump = result.candidate.heads.first(where: { $0.name == "jump" }) {
                            score("Jump balanced accuracy", jump.balancedAccuracy, result.baseline?.heads.first(where: { $0.name == "jump" })?.balancedAccuracy)
                        }
                        if let feedback = result.candidateSequential { score("Own-action agreement", feedback.exactActionAccuracy, result.baselineSequential?.exactActionAccuracy) }
                    }

                }.padding(12).background(.white.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
            }
        }.frame(maxWidth: .infinity, alignment: .topLeading)
    }
    private func score(_ title: String, _ value: Double, _ baseline: Double?) -> some View {
        HStack { Text(title).foregroundStyle(.secondary); Spacer(); Text(baseline.map { String(format: "%.0f%% → %.0f%%", $0 * 100, value * 100) } ?? String(format: "%.0f%%", value * 100)).monospacedDigit() }.font(.caption)
    }
}

private struct BotClipPreview: View {
    let clip: BotSnippet
    @Binding var playing: Bool
    @State private var playbackTime = 0.0
    @State private var lastTick: Date?
    @State private var recoveredBlockades: (UUID, [BotBlockadeReplay.Row])?
    @StateObject private var playback = BotClipPlayback()
    private let timer = Timer.publish(every: 1.0 / 60.0, on: .main, in: .common).autoconnect()
    var body: some View {
        Group {
        if !clip.observations.isEmpty {
            let playhead = clip.manifest.startedAt + playbackTime
            // Legacy review stills can be a second apart. The recorded keys and
            // terrain still have their original timing between those images.
            let timestamp = playback.usesSourceFrames ? (playback.displayedTimestamp ?? playhead) : playhead
            let index = observationIndex(in: clip.observations, at: timestamp)
            let observation = clip.observations[index]
            let obstacle = observation.blockade ?? (recoveredBlockades?.0 == clip.id ? recoveredBlockades?.1.first {
                $0.generation == observation.generation && $0.captureTimestamp == observation.captureTimestamp
            }?.blockade : nil)
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .top, spacing: 10) {
                    if let image = playback.image {
                        Image(nsImage: image).resizable().scaledToFit().frame(maxWidth: .infinity)
                    } else {
                        Color.black.aspectRatio(16 / 9, contentMode: .fit).frame(maxWidth: .infinity)
                    }
                    BotObservationPreview(observation: observation, blockade: obstacle, pathHint: clip.pathHint(at: index))
                        .aspectRatio(observation.screenTerrain.map { $0.width / $0.height } ?? 16 / 9, contentMode: .fit)
                        .frame(maxWidth: .infinity)
                }
                if clip.pathHint(at: index) != nil {
                    Text("Purple arrow · Hacker path direction").font(.caption).foregroundStyle(.purple)
                }
                if clip.domain == .hacker, observation.authoredTerrain == nil {
                    Text("This capture predates authored wall recording. Only its recorded floors are shown.")
                        .font(.caption2).foregroundStyle(.secondary)
                }
                if let obstacle {
                    Text(obstacle.mark.direction > 0 ? "Orange marker · blocked moving right" : "Orange marker · blocked moving left")
                        .font(.caption).foregroundStyle(.orange)
                }
                HStack {
                    Button(playing ? "Pause Clip" : "Play Clip") {
                        if playbackTime >= clip.manifest.duration { playbackTime = 0 }
                        lastTick = nil
                        playing.toggle()
                    }.accessibilityIdentifier("bot-play-snippet")
                    if clip.manifest.duration > 0 {
                        Slider(value: Binding(get: { playbackTime }, set: { value in
                            playbackTime = value
                            lastTick = nil
                            playback.request(at: clip.manifest.startedAt + value)
                        }), in: 0...clip.manifest.duration).accessibilityLabel("Training clip timeline")
                    }
                    Text(String(format: "%.1f / %.1fs", playbackTime, clip.manifest.duration)).font(.caption.monospacedDigit())
                }
                BotKeyboard(held: clip.heldButtons(at: timestamp))
                if clip.maskedKnightFrames.contains(index) {
                    Text("Knight occluded · recovery training can use the recorded controls").font(.caption).foregroundStyle(.secondary)
                } else if !observation.issues.isEmpty {
                    Text("Excluded frame: " + observation.issues.joined(separator: ", ")).font(.caption).foregroundStyle(.orange)
                }
            }.task(id: clip.id) {
                playbackTime = 0
                lastTick = nil
                playback.load(clip)
                playback.request(at: clip.manifest.startedAt + playbackTime)
                guard clip.observations.contains(where: { $0.driveDirection == nil }) else { return }
                let rows = await Task.detached(priority: .utility) { try? BotBlockadeReplay.recover(clip).rows }.value
                if !Task.isCancelled, let rows { recoveredBlockades = (clip.id, rows) }
            }
        }
        }
        .onReceive(timer) { now in
            guard playing else { lastTick = nil; return }
            let elapsed = lastTick.map { max(0, now.timeIntervalSince($0)) } ?? 0
            lastTick = now
            playbackTime = min(clip.manifest.duration, playbackTime + elapsed)
            playback.request(at: clip.manifest.startedAt + playbackTime)
            if playbackTime >= clip.manifest.duration { playing = false; lastTick = nil }
        }
    }
    private func observationIndex(in observations: [BotObservation], at time: Double) -> Int {
        var low = 0, high = observations.count
        while low < high {
            let middle = (low + high) / 2
            if observations[middle].availableAt <= time { low = middle + 1 } else { high = middle }
        }
        return max(0, low - 1)
    }

}

private struct BotObservationPreview: View {

    let observation: BotObservation
    let blockade: BotBlockadeEvidence?
    let pathHint: BotRoadHint?
    var body: some View {
        Canvas { context, size in
            let f = observation.features
            guard [BotObservationContract.legacyFeatureCount, BotObservationContract.featureCount].contains(f.count) else { return }
            let scale = size.width
            func point(_ x: Double, _ y: Double) -> CGPoint { CGPoint(x: x * scale, y: size.height - y * scale) }
            func stroke(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double, color: Color) {
                var line = Path()
                line.move(to: point(x1, y1))
                line.addLine(to: point(x2, y2))
                context.stroke(line, with: .color(color), lineWidth: 2)
            }
            let hacker = BotDomain.of(observation) == .hacker
            defer {
                if let blockade {
                    let x = f[0] + blockade.dx, y = f[1] + blockade.dy
                    stroke(x, y - 0.035, x, y + 0.035, color: .orange)
                    stroke(x - 0.012, y + 0.035, x + 0.012, y + 0.035, color: .orange)
                }
            }
            if let geometry = observation.screenTerrain, geometry.width > 0 {
                let color: Color = hacker ? .green : .cyan
                let width = geometry.width
                for floor in geometry.floors {
                    stroke(floor.minimumX / width, floor.y / width, floor.maximumX / width, floor.y / width, color: color)
                }
                for wall in geometry.walls {
                    stroke(wall.x / width, wall.bottomY / width, wall.x / width, wall.topY / width, color: color)
                }
                for spike in geometry.spikes ?? [] {
                    stroke(spike.x1 / width, spike.y1 / width, spike.x2 / width, spike.y2 / width, color: .red)
                }
                for door in geometry.doors ?? [] {
                    stroke(door.x1 / width, door.y1 / width, door.x2 / width, door.y2 / width, color: .cyan)
                }
            } else {
                for i in 0..<BotObservationContract.terrainCapacity {
                    let start = 16 + i * 5
                    guard f[start + 4] > 0 else { continue }
                    let y = f[1] + f[start + 2]
                    stroke(f[0] + f[start], y, f[0] + f[start + 1], y, color: hacker ? .green : .cyan)
                }
                if !hacker, f.count == BotObservationContract.featureCount {
                    for base in [145, 150] where f[base + 4] == 1 {
                        let x = f[0] + f[base]
                        stroke(x, f[1] + f[base + 1], x, f[1] + f[base + 2], color: .cyan)
                    }
                }
            }
            if let hint = pathHint, hint.dx.isFinite, hint.dy.isFinite {
                let length = hypot(hint.dx, hint.dy)
                if length > 0.001 {
                    let dx = hint.dx / length, dy = hint.dy / length
                    let start = f[4] > 0 ? point(f[0], f[1]) : CGPoint(x: size.width / 2, y: size.height / 2)
                    let end = CGPoint(x: start.x + dx * scale * 0.12, y: start.y - dy * scale * 0.12)
                    var arrow = Path()
                    arrow.move(to: start); arrow.addLine(to: end)
                    let head = max(5, size.width * 0.018)
                    let ux = CGFloat(dx), uy = CGFloat(-dy)
                    arrow.move(to: CGPoint(x: end.x - head * ux + head * 0.55 * uy, y: end.y - head * uy - head * 0.55 * ux))
                    arrow.addLine(to: end)
                    arrow.addLine(to: CGPoint(x: end.x - head * ux - head * 0.55 * uy, y: end.y - head * uy + head * 0.55 * ux))
                    context.stroke(arrow, with: .color(.purple), style: StrokeStyle(lineWidth: 3, lineCap: .round, lineJoin: .round))
                }
            }
            if f[4] > 0 {
                let knight = point(f[0], f[1])
                context.fill(Path(ellipseIn: CGRect(x: knight.x - 4, y: knight.y - 6, width: 8, height: 12)), with: .color(.white))
            }
            func ring(_ cache: BotObjectBounds, color: Color) {
                let center = point(cache.x, cache.y)
                let w = max(7, cache.width * scale), h = max(7, cache.height * scale)
                context.stroke(Path(ellipseIn: CGRect(x: center.x - w / 2, y: center.y - h / 2, width: w, height: h)),
                               with: .color(color), lineWidth: 2)
            }
            for cache in observation.geoCaches ?? [] where cache.valid { ring(cache, color: .yellow) }
            for cocoon in observation.lifebloodCocoons ?? [] where cocoon.valid { ring(cocoon, color: .cyan) }
            for i in 0..<8 where f[96 + i * 6 + 5] > 0 {
                let enemy = point(f[0] + f[96 + i * 6], f[1] + f[97 + i * 6])
                context.fill(Path(ellipseIn: CGRect(x: enemy.x - 4, y: enemy.y - 4, width: 8, height: 8)), with: .color(.red))
            }
        }.background(.white.opacity(0.04)).clipped()
    }
}

private struct BotKeyboard: View {
    let held: Set<String>

    private func key(_ label: String, button: String, width: CGFloat = 25) -> some View {
        let pressed = held.contains(button)
            || (button == "i" && held.contains("inventory"))
            || (button == "p" && held.contains("pause"))
        return Text(label)
            .font(.system(size: 14, weight: .medium, design: .monospaced))
            .foregroundStyle(pressed ? Color.black : Color.white)
            .frame(width: width, height: 28)
            .background(pressed ? Color.white : Color.white.opacity(0.12), in: RoundedRectangle(cornerRadius: 5))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(Color.white.opacity(0.22)))
            .accessibilityLabel("\(button) \(pressed ? "pressed" : "released")")
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 2) {
                    ForEach(Array("QWERTYUIOP"), id: \.self) { letter in
                        key(String(letter), button: String(letter).lowercased())
                    }
                }
                HStack(spacing: 2) {
                    ForEach(Array("ASDFGHJKL"), id: \.self) { letter in
                        key(String(letter), button: String(letter).lowercased())
                    }
                }.padding(.leading, 12)
                HStack(spacing: 2) {
                    ForEach(Array("ZXCVBNM"), id: \.self) { letter in
                        key(String(letter), button: String(letter).lowercased())
                    }
                }.padding(.leading, 26)
            }
            VStack(spacing: 4) {
                key("↑", button: "up")
                HStack(spacing: 4) {
                    key("←", button: "left")
                    key("↓", button: "down")
                    key("→", button: "right")
                }
            }
        }
        .padding(.vertical, 4)
    }
}
