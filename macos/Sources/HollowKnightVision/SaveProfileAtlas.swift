import Foundation

/// Identity of one playthrough. Reusing a slot creates a different identity with the same display name.
struct SaveProfileAtlas: Codable, Equatable {
    let slot: Int
    var id = UUID()
    var name: String { "Atlas\(slot)" }

    static func read(from world: URL) throws -> Self? {
        let url = world.appendingPathComponent("save-profile.json")
        do {
            let value = try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
            guard (1...4).contains(value.slot) else { throw CocoaError(.fileReadCorruptFile) }
            return value
        } catch let error as CocoaError where error.code == .fileReadNoSuchFile { return nil }
    }

    func write(to world: URL) throws {
        try JSONEncoder().encode(self).write(to: world.appendingPathComponent("save-profile.json"), options: .atomic)
    }
}

struct ProfileLaunch: Equatable {
    let slot: Int
    let isNew: Bool
}

/// Observe the selected profile card through the opening movie. A missing menu match is
/// common during fades. Only a new Gameplay HUD can commit the last stable selection.
struct ProfileLaunchDetector {
    private var generation: UInt64?
    private var candidate: ProfileLaunch?
    private var firstSeen = 0.0
    private var lastMenu = -Double.infinity
    private var armed: ProfileLaunch?
    private var knownOccupied = Set<Int>()
    private var firstHUD: Double?

    var awaitingGameplay: Bool { armed != nil }

    mutating func observe(_ observation: BotDemoMenuObservation) -> ProfileLaunch? {
        if generation != observation.generation {
            self = Self()
            generation = observation.generation
        }
        if observation.gameplay {
            guard let armed else { return nil }
            guard let firstHUD else { firstHUD = observation.frameTimestamp; return nil }
            guard observation.frameTimestamp - firstHUD >= 0.12 else { return nil }
            self = Self()
            return armed
        }
        firstHUD = nil
        guard let context = observation.context else { return nil }
        if context != .selectProfile {
            if [.mainTitle, .clearSave, .quitToMenu, .quitGame, .pause].contains(context) { self = Self() }
            return nil
        }
        guard let time = observation.menuTimestamp, time > lastMenu else { return nil }
        let gap = time - lastMenu
        lastMenu = time
        knownOccupied.formUnion(observation.occupiedSlots)
        if let armed, armed.isNew, knownOccupied.contains(armed.slot) { self.armed = nil }
        guard let selected = observation.selectedIdentifier else { return nil }
        guard let slot = (1...4).first(where: { selected == "select-profile.slot-\($0)" }) else {
            candidate = nil
            armed = nil
            return nil
        }
        if armed?.slot != slot { armed = nil }
        guard observation.checkedProfileSlots.contains(slot) else { return nil }
        let next = ProfileLaunch(slot: slot, isNew: !knownOccupied.contains(slot))
        if next != candidate || gap > 0.75 {
            candidate = next
            firstSeen = time
        } else if time - firstSeen >= 0.12 {
            armed = next
        }
        return nil
    }
}

extension SaveProfileAtlas {
    static func target(for launch: ProfileLaunch, active: Self?, saved: [AtlasSavedState]) -> Self {
        if launch.isNew { return Self(slot: launch.slot) }
        if let active, active.slot == launch.slot { return active }
        if let previous = saved.first(where: { $0.profile?.slot == launch.slot })?.profile { return previous }
        return Self(slot: launch.slot)
    }
}

extension LiveAtlasPipeline {
    func activateProfile(_ profile: SaveProfileAtlas, in library: AtlasStateStore, adoptingUnassigned: Bool = false) throws -> LiveAtlasLoadedState? {
        guard let root = activeAutoSaveRootURL else { throw LiveAtlasStateError.activeWorldUnavailable }
        let current = try SaveProfileAtlas.read(from: root)
        guard current != profile else { return nil }
        if let state = try library.list().first(where: { $0.profile == profile }) {
            let world = try library.worldURL(for: state)
            guard try SaveProfileAtlas.read(from: world) == profile else { throw CocoaError(.fileReadCorruptFile) }
            return try loadSavedWorld(from: world)
        }
        if adoptingUnassigned && current == nil {
            try profile.write(to: root)
            return nil
        }
        guard reset(anchorPosition: .zero, profile: profile) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSLocalizedDescriptionKey:
                worldPersistenceIssue ?? "Could not create \(profile.name)."])
        }
        return LiveAtlasLoadedState(snapshot: try LiveWorldSnapshot(), atlasTiles: [],
                                    archivedPreviousWorldURL: mostRecentResetArchiveURL)
    }
}

extension HackerAtlasArchive {
    var emptyForCurrentProfile: Self {
        var fresh = Self.empty
        fresh.profile = profile
        return fresh
    }

    func switching(to profile: SaveProfileAtlas, library: AtlasStateStore, adoptingUnassigned: Bool = false) throws -> Self? {
        guard self.profile != profile else { return nil }
        let previous = try library.list().first { $0.profile == profile }
        if adoptingUnassigned && self.profile == nil && previous == nil {
            var adopted = self
            adopted.profile = profile
            return adopted
        }
        var next = Self.empty
        if let previous {
            next = try HackerAtlasArchiveStore.read(from: library.worldURL(for: previous))
            guard next.profile == profile else { throw CocoaError(.fileReadCorruptFile) }
        } else {
            next.profile = profile
        }
        if self.profile != nil || !rooms.isEmpty {
            _ = try library.save(name: self.profile?.name ?? "Atlas before \(profile.name)", profile: self.profile) {
                try HackerAtlasArchiveStore.write(self, to: $0)
            }
        }
        return next
    }
}
