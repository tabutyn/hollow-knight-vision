import SwiftUI

struct AtlasManagerView: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    @ObservedObject var model: LiveCaptureModel
    let workspace: DashboardWorkspace
    @State private var stateName = ""
    @State private var selectedStateID: UUID?
    @State private var showingPermanentDeleteConfirmation = false
    @State private var showingHackerDeleteConfirmation = false

    private var selectedState: AtlasSavedState? {
        model.atlasSavedStates.first { $0.id == selectedStateID }
    }

    private var autoSaveSelected: Bool {
        selectedStateID == AtlasAutoSaveSummary.activeID
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            gameplayManager
            if workspace == .hacker { Divider(); hackerManager }
        }
    }

    private var hackerManager: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text(visionText("Manage Hacker Atlas"))
                    .font(.title3.weight(.semibold))
                Spacer()
            }
            Button(visionText("Delete Atlas"), role: .destructive) {
                showingHackerDeleteConfirmation = true
            }
        }

        .confirmationDialog(
            visionText("deleteHackerAtlas"),
            isPresented: $showingHackerDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button(visionText("Delete Permanently"), role: .destructive) {
                model.deleteHackerAtlas()
            }
            Button(visionText("Cancel"), role: .cancel) {}
        }
    }

    private var gameplayManager: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(visionText("Manage Atlas"))
                    .font(.title3.weight(.semibold))
                Spacer()
            }
            Text(visionText("The active atlas auto-saves. Save State creates a named restore point; New archives the active atlas before clearing it."))
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                TextField(visionText("State name"), text: $stateName)
                    .textFieldStyle(.roundedBorder)
                Button(visionText("Save State")) {
                    model.saveAtlasState(name: stateName)
                }
                .disabled(stateName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    || model.atlasManagementBusy)
            }

            List(selection: $selectedStateID) {
                autoSaveRow
                    .tag(AtlasAutoSaveSummary.activeID)
                ForEach(model.atlasSavedStates) { state in
                    HStack {
                        Text(state.name)
                        Spacer()
                        Text(state.createdAt, style: .date)
                            .foregroundStyle(.secondary)
                    }
                    .tag(state.id)
                }
            }
            .frame(height: 180)

            if let issue = model.atlasManagementIssue {
                Text(issue)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
            HStack {
                Button(visionText("New"), action: model.newAtlas)
                Spacer()
                Button(visionText("Delete"), role: .destructive) {
                    showingPermanentDeleteConfirmation = true
                }
                .disabled((selectedState == nil && !autoSaveSelected) || model.atlasManagementBusy)
                Button(visionText("Load")) {
                    if let selectedState { model.loadAtlasState(selectedState) }
                }
                .disabled(selectedState == nil || autoSaveSelected || model.atlasManagementBusy)
            }
            .disabled(model.atlasManagementBusy)
            if model.atlasManagementBusy {
                ProgressView()
                    .controlSize(.small)
            }
        }

        .onAppear { model.refreshAtlasSavedStates() }
        .confirmationDialog(
            visionText("deleteAtlas"),
            isPresented: $showingPermanentDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button(visionText("Delete Permanently"), role: .destructive) {
                if autoSaveSelected {
                    model.deleteAutoSave()
                } else if let selectedState {
                    model.deleteAtlasState(selectedState)
                }
            }
            Button(visionText("Cancel"), role: .cancel) {}
        } message: {
            Text(visionText("Its registered images will be purged to free disk space. This cannot be undone."))
        }
    }

    private var autoSaveRow: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(visionText("Auto Save"))
                        .fontWeight(.semibold)
                    Text(visionText("registrations", ["count": String(model.atlasAutoSave.registrationCount)]))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(autoSaveSizeText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                let age = max(0, Int(context.date.timeIntervalSince(model.atlasAutoSave.createdAt)))
                Text(visionText("secondsOld", ["count": String(age)]))
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var autoSaveSizeText: String {
        guard let bytes = model.atlasAutoSave.totalByteCount else {
            return visionText("calculatingSize")
        }
        return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
