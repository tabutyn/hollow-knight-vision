import SwiftUI

struct AtlasManagerView: View {
    @ObservedObject var model: LiveCaptureModel
    let workspace: DashboardWorkspace
    @Environment(\.dismiss) private var dismiss
    @State private var stateName = ""
    @State private var selectedStateID: UUID?
    @State private var showingPermanentDeleteConfirmation = false

    private var selectedState: AtlasSavedState? {
        model.atlasSavedStates.first { $0.id == selectedStateID }
    }

    private var autoSaveSelected: Bool {
        selectedStateID == AtlasAutoSaveSummary.activeID
    }

    var body: some View {
        Group {
            if workspace == .hacker {
                hackerManager
            } else {
                gameplayManager
            }
        }
    }

    private var hackerManager: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Manage Hacker Atlas")
                    .font(.title3.weight(.semibold))
                Spacer()
                Button("Done") { dismiss() }
            }
            Spacer()
            Button("Delete Atlas", role: .destructive) {
                showingPermanentDeleteConfirmation = true
            }
        }
        .padding(18)
        .frame(minWidth: 360, minHeight: 150)
        .confirmationDialog(
            "Permanently delete the Hacker atlas?",
            isPresented: $showingPermanentDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button("Delete Permanently", role: .destructive) {
                model.deleteHackerAtlas()
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private var gameplayManager: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Manage Atlas")
                    .font(.title3.weight(.semibold))
                Spacer()
                Button("Done") { dismiss() }
            }
            Text("The active atlas auto-saves. Save State creates a named restore point; New archives the active atlas before clearing it.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                TextField("State name", text: $stateName)
                    .textFieldStyle(.roundedBorder)
                Button("Save State") {
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
            .frame(minHeight: 180)

            if let issue = model.atlasManagementIssue {
                Text(issue)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
            HStack {
                Button("New", action: model.newAtlas)
                Spacer()
                Button("Delete", role: .destructive) {
                    showingPermanentDeleteConfirmation = true
                }
                .disabled((selectedState == nil && !autoSaveSelected) || model.atlasManagementBusy)
                Button("Load") {
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
        .padding(18)
        .frame(minWidth: 480, minHeight: 330)
        .onAppear { model.refreshAtlasSavedStates() }
        .confirmationDialog(
            "Permanently delete this atlas?",
            isPresented: $showingPermanentDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button("Delete Permanently", role: .destructive) {
                if autoSaveSelected {
                    model.deleteAutoSave()
                } else if let selectedState {
                    model.deleteAtlasState(selectedState)
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Its registered images will be purged to free disk space. This cannot be undone.")
        }
    }

    private var autoSaveRow: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Auto Save")
                        .fontWeight(.semibold)
                    Text("\(model.atlasAutoSave.registrationCount) registrations")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(autoSaveSizeText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                let age = max(0, Int(context.date.timeIntervalSince(model.atlasAutoSave.createdAt)))
                Text("\(age) seconds old")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var autoSaveSizeText: String {
        guard let bytes = model.atlasAutoSave.totalByteCount else {
            return "Calculating size…"
        }
        return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
