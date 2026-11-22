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

    private var selectedState: AtlasSavedState? {
        savedStates.first { $0.id == selectedStateID }
    }

    private var autoSaveSelected: Bool {
        selectedStateID == AtlasAutoSaveSummary.activeID
    }

    private var isHacker: Bool { workspace == .hacker }
    private var savedStates: [AtlasSavedState] { isHacker ? model.hackerAtlasSavedStates : model.atlasSavedStates }
    private var autoSave: AtlasAutoSaveSummary { isHacker ? model.hackerAtlasAutoSave : model.atlasAutoSave }
    private var managementBusy: Bool { isHacker ? model.hackerAtlasManagementBusy : model.atlasManagementBusy }
    private var managementIssue: String? { isHacker ? model.hackerAtlasManagementIssue : model.atlasManagementIssue }

    var body: some View { atlasManager }

    private var atlasManager: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField(visionText("State name"), text: $stateName)
                    .textFieldStyle(.roundedBorder)
                Button(visionText("Save State")) {
                    if isHacker { model.saveHackerAtlasState(name: stateName) }
                    else { model.saveAtlasState(name: stateName) }
                }
                .disabled(stateName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    || managementBusy)
            }

            List(selection: $selectedStateID) {
                autoSaveRow
                    .tag(AtlasAutoSaveSummary.activeID)
                ForEach(savedStates) { state in
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

            if let issue = managementIssue {
                Text(issue)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
            HStack {
                Button(visionText("New")) {
                    if isHacker { model.newHackerAtlas() } else { model.newAtlas() }
                }
                Spacer()
                Button(visionText("Delete"), role: .destructive) {
                    showingPermanentDeleteConfirmation = true
                }
                .disabled((selectedState == nil && !autoSaveSelected) || managementBusy)
                Button(visionText("Load")) {
                    if let selectedState {
                        if isHacker { model.loadHackerAtlasState(selectedState) }
                        else { model.loadAtlasState(selectedState) }
                    }
                }
                .disabled(selectedState == nil || autoSaveSelected || managementBusy)
            }
            .disabled(managementBusy)
            if managementBusy {
                ProgressView()
                    .controlSize(.small)
            }
        }

        .onAppear {
            if isHacker { model.refreshHackerAtlasSavedStates() }
            else { model.refreshAtlasSavedStates() }
        }
        .confirmationDialog(
            visionText("deleteAtlas"),
            isPresented: $showingPermanentDeleteConfirmation,
            titleVisibility: .visible
        ) {
            Button(visionText("Delete Permanently"), role: .destructive) {
                if autoSaveSelected {
                    if isHacker { model.deleteHackerAutoSave() } else { model.deleteAutoSave() }
                } else if let selectedState {
                    if isHacker { model.deleteHackerAtlasState(selectedState) }
                    else { model.deleteAtlasState(selectedState) }
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
                    Text([autoSave.profile?.name, visionText("Auto Save")].compactMap { $0 }.joined(separator: " · "))
                        .fontWeight(.semibold)
                    Text(visionText("registrations", ["count": String(autoSave.registrationCount)]))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(autoSaveSizeText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                let age = max(0, Int(context.date.timeIntervalSince(autoSave.createdAt)))
                Text(visionText("secondsOld", ["count": String(age)]))
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var autoSaveSizeText: String {
        guard let bytes = autoSave.totalByteCount else {
            return visionText("calculatingSize")
        }
        return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
