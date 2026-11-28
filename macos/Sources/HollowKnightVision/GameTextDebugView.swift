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
                if showingMemory {
                    CopyTextButton(title: "Copy log", text: reader.transcript)
                } else {
                    CopyTextButton(title: "Copy on-screen text", text: reader.scan?.lines.map(\.text).joined(separator: "\n") ?? "")
                }
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
                if reader.entries.isEmpty {
                    ContentUnavailableView("No text logged yet", systemImage: "book")
                } else {
                    SelectableTranscript(text: reader.transcript, label: "Game text log")
                    DisclosureGroup("Reading details") {
                        readingDetails
                    }
                }
            } else if enabled, let scan = reader.scan {
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
                                ForEach(scan.lines) { line in
                                    HStack(alignment: .firstTextBaseline) {
                                        Button(String(line.id)) { selected = line.id }
                                        Text(line.text).textSelection(.enabled)
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

    private var readingDetails: some View {
        List(reader.entries) { record in
            let entry = record.reading
            VStack(alignment: .leading, spacing: 4) {
                Text(entry.isPassage == true ? "Recorded passage" : "Notebook text").font(.caption.bold())
                Text(entry.displayText).textSelection(.enabled)
                if entry.text != entry.displayText {
                    DisclosureGroup("First OCR reading") { Text(entry.text).textSelection(.enabled) }
                }
                if let annotation = entry.interpretation {
                    DisclosureGroup("Earlier model annotation") {
                        Text(annotation.displayLabel + " · " + annotation.text).textSelection(.enabled)
                    }
                }
                Text("\(record.contexts.joined(separator: " · ")) · \(record.updated.formatted())")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(.vertical, 4)
        }
    }

}
