import SwiftUI

/// Reads the same notebook, captured inputs and validated outputs used for playback.
struct ThinkingContextPanel: View {
    @ObservedObject var reader: GameTextReader
    @ObservedObject var session: ThinkingModelSession
    @ObservedObject var inspector: ThinkingInspector
    @ObservedObject var player: BotPlaybackController
    @Binding var expanded: Bool
    let readsText: Bool
    private enum Tab: String, CaseIterable { case context = "Context", screenshot = "Screenshot", json = "JSON" }
    @State private var tab = Tab.context
    private enum Message: String, CaseIterable { case current = "Latest request", gameplay = "Gameplay", menu = "Menu" }
    @State private var message = Message.current

    var body: some View {
        Group {
            if expanded {
                VStack(spacing: 8) {
                    HStack {
                        Picker("View", selection: $tab) {
                            ForEach(Tab.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                        }.labelsHidden().pickerStyle(.segmented).frame(maxWidth: 310)
                        Spacer()
                        Button { expanded = false } label: { Image(systemName: "chevron.down") }
                            .accessibilityLabel("Collapse Thinking context")
                    }
                    switch tab {
                    case .context: contextColumns
                    case .screenshot: screenshot
                    case .json: reply
                    }
                    Divider()
                    controls
                }.padding(10)
            } else {
                ZStack(alignment: .topTrailing) {
                    notebook(compact: true)
                    Image(systemName: "chevron.up").font(.caption2).foregroundStyle(.orange)
                }
                .padding(7).contentShape(Rectangle()).onTapGesture { expanded = true }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Expand Thinking context")
                .accessibilityAddTraits(.isButton)
                .accessibilityAction { expanded = true }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(.black.opacity(expanded ? 0.95 : 0.83), in: RoundedRectangle(cornerRadius: 8))
        .overlay { RoundedRectangle(cornerRadius: 8).stroke(.orange.opacity(0.45)).allowsHitTesting(false) }
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
    }

    private var contextColumns: some View {
        GeometryReader { area in
            ScrollView(.horizontal) {
                HStack(alignment: .top, spacing: 0) {
                    column("Text log", width: max(165, area.size.width * 0.22)) {
                        Text(reader.profile.map { "Save slot \($0.slot)" } ?? "Before save selection").font(.caption2)
                        notebook(compact: false)
                    }
                    Divider()
                    column("Game information", width: max(177, area.size.width * 0.24)) {
                        scrollText(inspector.input?.liveEvidence, empty: "Waiting for game evidence.")
                    }
                    Divider()
                    column("Reasoning", width: max(170, area.size.width * 0.23)) {
                        scrollText(inspector.reasoning, empty: session.isEnabled ? "Waiting for a recognized state." : "Thinking is off.")
                    }
                    Divider()
                    column("Schema explorer", width: max(212, area.size.width * 0.31 - 4)) {
                        Picker("Message", selection: $message) {
                            ForEach(Message.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                        }.labelsHidden()
                        ScrollView {
                            VStack(alignment: .leading, spacing: 8) {
                                switch message {
                                case .current:
                                    if let input = inspector.input {
                                        Text(input.frame.capturedAt.formatted(date: .omitted, time: .standard)).font(.caption2)
                                        DisclosureGroup("System message") { block(input.context.system) }
                                        DisclosureGroup("User message") { block(input.context.user) }
                                        Text("Expected response schema").font(.caption.bold())
                                        block(input.context.schemaText)
                                    } else { block("Waiting for the first request.") }
                                case .gameplay, .menu:
                                    Text(message == .gameplay ? "Explore, Traverse or Interact" : "Select, more or wait").font(.caption)
                                    let schema = message == .gameplay ? ThinkingPolicyContract.policySchemaText : ThinkingPolicyContract.menuSchemaText
                                    block(Self.pretty(schema))
                                }
                            }.frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                }.frame(height: area.size.height)
            }
        }
    }

    private func column<V: View>(_ name: String, width: CGFloat, @ViewBuilder content: () -> V) -> some View {
        VStack(alignment: .leading, spacing: 7) { Text(name).font(.headline); content() }
            .padding(.horizontal, 6).frame(width: width, alignment: .leading)
    }

    private func notebook(compact: Bool) -> some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: compact ? 2 : 7) {
                if reader.entries.isEmpty {
                    Text(readsText ? "Waiting for text…" : "Text reading is off")
                } else {
                    ForEach(reader.entries) { entry in
                        Text(verbatim: entry.reading.displayText).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }.padding(.trailing, compact ? 14 : 0).frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.system(size: compact ? 10 : 12, design: .monospaced))
        .foregroundStyle(.orange)
    }

    private var screenshot: some View {
        VStack(alignment: .leading, spacing: 5) {
            if let frame = inspector.frame {
                Text(frame.event + " · " + frame.capturedAt.formatted(date: .omitted, time: .standard)).font(.caption)
                Image(decorative: frame.image, scale: 1).resizable().scaledToFit()
                    .accessibilityLabel("Latest event screenshot")
            } else { Text("Waiting for the first event screenshot.").font(.caption) }
        }.frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var reply: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let output = inspector.output {
                Text(output.input.model.title + " · " + output.input.frame.event + " · " +
                     output.input.frame.capturedAt.formatted(date: .omitted, time: .standard)).font(.caption)
                if let status = output.issue ?? output.handoff { Text(status).font(.caption) }
                if let file = output.file {
                    Button("Show decision JSON in Finder") { NSWorkspace.shared.activateFileViewerSelecting([file]) }
                }
                if output.input.frame.id != inspector.frame?.id, let image = output.input.context.image {
                    DisclosureGroup("Screenshot for this reply") {
                        Image(decorative: image, scale: 1).resizable().scaledToFit().frame(maxHeight: 120)
                    }
                }
                scrollText(output.text, empty: "Empty reply")
            } else { scrollText(nil, empty: "Waiting for the first JSON reply.") }
        }.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var controls: some View {
        ViewThatFits(in: .horizontal) {
            HStack { modelOptions; Spacer(); status }
            VStack(alignment: .leading) { modelOptions; status }
        }.font(.caption)
    }

    @ViewBuilder private var modelOptions: some View {
        if session.isEnabled {
            Picker("Model", selection: Binding(get: { session.model }, set: { session.select($0) })) {
                ForEach(ThinkingModel.allCases) { Text($0.title).tag($0) }
            }.fixedSize()
            Toggle("Automatic policies", isOn: $player.automaticPoliciesEnabled)
        } else { Text("Thinking is off") }
    }

    @ViewBuilder private var status: some View {
        if session.isEnabled {
            switch session.state {
            case .loading:
                Text([session.loadDetail, session.loadLayers, session.loadBuffers].compactMap { $0 }.joined(separator: " · "))
            case .failed(_, let reason):
                Text(reason).foregroundStyle(.orange)
                Button("Retry") { session.select(session.model, force: true) }
            case .ready: Text(inspector.issue ?? inspector.status ?? "Model ready")
            case .unloaded, .idle: Text("Waiting for model")
            }
        }
    }

    private func scrollText(_ value: String?, empty: String) -> some View {
        ScrollView { block(value.flatMap { $0.isEmpty ? nil : $0 } ?? empty) }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func block(_ text: String) -> some View {
        Text(verbatim: text).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private static func pretty(_ json: String) -> String {
        guard let value = try? JSONSerialization.jsonObject(with: Data(json.utf8)),
              let data = try? JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys]) else { return json }
        return String(decoding: data, as: UTF8.self)
    }
}
