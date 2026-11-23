import SwiftUI

struct GameTextDebugView: View {
    @ObservedObject var reader: GameTextReader
    @Binding var enabled: Bool
    @Environment(\.dismiss) private var dismiss
    @State private var showingMemory = true
    @State private var selected: Int?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Game Text Log").font(.title2.bold())
                Spacer()
                Toggle("Read game text", isOn: $enabled)
                Button("Done") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            Picker("Text source", selection: $showingMemory) {
                Text("On screen").tag(false)
                Text("Log (\(reader.entries.count))").tag(true)
            }.pickerStyle(.segmented).labelsHidden()
            if let issue = reader.issue { Text(issue).foregroundStyle(.orange).textSelection(.enabled) }
            if showingMemory {
                Text(reader.profile.map { "Save slot \($0.slot)" } ?? "Before save selection")
                    .font(.headline)
                if enabled, let status = reader.cleanupStatus {
                    Text(status).font(.caption).foregroundStyle(.secondary)
                }
                if reader.entries.isEmpty {
                    ContentUnavailableView("No text logged yet", systemImage: "book")
                } else {
                    List(reader.entries) { record in
                        let entry = record.reading
                        VStack(alignment: .leading, spacing: 4) {
                            if let interpretation = entry.interpretation {
                                Text(interpretation.displayLabel).font(.headline)
                                Text(entry.reading).textSelection(.enabled)
                                DisclosureGroup("Original OCR") {
                                    Text(entry.text).textSelection(.enabled)
                                    Text(interpretation.label.title + " · " + interpretation.model)
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            } else {
                                Text("Awaiting review").font(.caption.bold())
                                Text(entry.text).textSelection(.enabled)
                            }
                            Text("\(record.contexts.joined(separator: " · ")) · \(record.updated.formatted())")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 4)
                    }
                }
            } else if enabled, let scan = reader.scan {
                let readings = reader.memory.filter { $0.context == scan.context && $0.language == scan.language && $0.interpretation != nil }
                let pageText = RememberedGameText.key(scan.lines.map(\.text).joined(separator: " "))
                let page = readings.last { $0.isPassage == true && pageText.contains(RememberedGameText.key($0.text)) }
                HStack(alignment: .top, spacing: 12) {
                    GeometryReader { area in
                        let scale = min(area.size.width / CGFloat(scan.image.width), area.size.height / CGFloat(scan.image.height))
                        let fitted = CGSize(width: CGFloat(scan.image.width) * scale, height: CGFloat(scan.image.height) * scale)
                        ZStack(alignment: .topLeading) {
                            Image(decorative: scan.image, scale: 1).resizable()
                            ForEach(scan.lines) { line in
                                let box = line.displayRect(in: fitted)
                                Rectangle().stroke(selected == line.id ? Color.orange : Color.yellow, lineWidth: 2)
                                    .frame(width: box.width, height: box.height).offset(x: box.minX, y: box.minY)
                                Text(String(line.id)).font(.caption2.bold()).foregroundStyle(.black)
                                    .padding(.horizontal, 2).background(.yellow)
                                    .offset(x: max(0, box.minX - 14), y: max(0, box.minY - 10))
                            }
                        }
                        .frame(width: fitted.width, height: fitted.height)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                    .background(.black)
                    VStack(alignment: .leading, spacing: 8) {
                        Text("\(scan.lines.count) lines · \(scan.milliseconds, specifier: "%.0f") ms").font(.caption)
                        if scan.lines.isEmpty { Text("No text detected in this frame.").foregroundStyle(.secondary) }
                        ScrollView {
                            VStack(alignment: .leading, spacing: 8) {
                                if let page, let interpreted = page.interpretation {
                                    Text(interpreted.displayLabel).font(.caption.bold())
                                    Text(interpreted.text).textSelection(.enabled)
                                    Divider()
                                }
                                ForEach(scan.lines) { line in
                                    HStack(alignment: .firstTextBaseline) {
                                        Button(String(line.id)) { selected = line.id }
                                        VStack(alignment: .leading) {
                                            if let value = readings.last(where: { $0.text == line.text })?.interpretation {
                                                Text(value.displayLabel).font(.caption.bold())
                                                Text(value.text).textSelection(.enabled)
                                                if value.text != line.text { Text(line.text).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
                                            } else { Text(line.text).textSelection(.enabled) }
                                        }
                                        Spacer()
                                        Text("\(Int(line.confidence * 100))%").font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    }.frame(width: 260)
                }
            } else {
                ContentUnavailableView(enabled ? "Waiting for a game frame" : "Text reading is off", systemImage: "text.viewfinder")
            }
            Spacer(minLength: 0)
        }
        .padding(18)
        .frame(minWidth: 720, idealWidth: 900, minHeight: 380, idealHeight: 520)
    }
}

struct GameplayNotebookReadout: View {
    @ObservedObject var reader: GameTextReader
    @ObservedObject var session: ThinkingModelSession
    let enabled: Bool

    private var content: String {
        if !reader.loggedText.isEmpty { return reader.loggedText.joined(separator: "\n") }
        let status: String
        if !enabled { status = "Text reading is off" }
        else { status = reader.cleanupStatus ?? "Waiting for a corrected reading" }
        var lines = ["Goal · " + reader.currentGoal, status]
        if case .loading(let model) = session.state { lines.append(model.title + " · " + session.loadDetail) }
        if case .failed(_, let reason) = session.state { lines.append(reason) }
        return lines.joined(separator: "\n")
    }

    var body: some View {
        Text(content)
            .font(.system(size: 7.5, weight: .semibold, design: .monospaced))
            .foregroundStyle(.orange)
            .multilineTextAlignment(.leading)
            .lineLimit(4, reservesSpace: true)
            .frame(width: 320, alignment: .leading)
            .padding(.horizontal, 7).padding(.vertical, 4)
            .background(.black.opacity(0.62), in: RoundedRectangle(cornerRadius: 5))
            .accessibilityLabel("Notebook")
            .accessibilityValue(content)
    }
}
