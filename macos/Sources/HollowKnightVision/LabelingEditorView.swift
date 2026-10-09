import AppKit
import CoreGraphics
import Foundation
import SwiftUI

enum LabelingContext: String, CaseIterable, Identifiable {
    case mainTitle = "Main Title"
    case options = "Options"
    case gameOptions = "Game Options"
    case audio = "Audio"
    case video = "Video"
    case screenScale = "Screen Scale"
    case brightness = "Brightness"
    case videoAdvancedSettings = "Video Advanced Settings"
    case controller = "Controller"
    case remapController = "Remap Controller"
    case controllerAdvancedSettings = "Controller Advanced Settings"
    case keyboard = "Keyboard"
    case mods = "Mods"
    case achievements = "Achievements"
    case extras = "Extras"
    case credits = "Credits"
    case hiddenDreams = "Hidden Dreams"
    case grimmTroupe = "The Grimm Troupe"
    case lifeblood = "Lifeblood"
    case godmaster = "Godmaster"
    case selectProfile = "Select Profile"
    case clearSave = "Clear Save"
    case pause = "Pause"
    case quitToMenu = "Quit To Menu"
    case inventory = "Inventory"
    case game = "Gameplay"
    case quitGame = "Quit Game"
    case enemies = "Enemies"
    case world = "World"
    case shared = "Shared"

    var id: String { rawValue }

    var storageIdentifier: String {
        switch self {
        case .mainTitle: return "main-title"
        case .options: return "options"
        case .gameOptions: return "game-options"
        case .audio: return "audio"
        case .video: return "video"
        case .screenScale: return "screen-scale"
        case .brightness: return "brightness"
        case .videoAdvancedSettings: return "video-advanced-settings"
        case .controller: return "controller"
        case .remapController: return "remap-controller"
        case .controllerAdvancedSettings: return "controller-advanced-settings"
        case .keyboard: return "keyboard"
        case .mods: return "mods"
        case .achievements: return "achievements"
        case .extras: return "extras"
        case .credits: return "credits"
        case .hiddenDreams: return "hidden-dreams"
        case .grimmTroupe: return "the-grimm-troupe"
        case .lifeblood: return "lifeblood"
        case .godmaster: return "godmaster"
        case .selectProfile: return "select-profile"
        case .clearSave: return "clear-save"
        case .pause: return "pause"
        case .quitToMenu: return "quit-to-menu"
        case .inventory: return "inventory"
        case .game: return "game"
        case .quitGame: return "quit-game"
        case .enemies: return "enemies"
        case .world: return "world"
        case .shared: return "shared"
        }
    }

    init?(storageIdentifier: String) {
        switch storageIdentifier {
        case "main-title": self = .mainTitle
        case "options": self = .options
        case "game-options": self = .gameOptions
        case "audio": self = .audio
        case "video": self = .video
        case "screen-scale": self = .screenScale
        case "brightness": self = .brightness
        case "video-advanced-settings": self = .videoAdvancedSettings
        case "controller": self = .controller
        case "remap-controller": self = .remapController
        case "controller-advanced-settings": self = .controllerAdvancedSettings
        case "keyboard": self = .keyboard
        case "mods": self = .mods
        case "achievements": self = .achievements
        case "extras": self = .extras
        case "credits": self = .credits
        case "hidden-dreams": self = .hiddenDreams
        case "the-grimm-troupe": self = .grimmTroupe
        case "lifeblood": self = .lifeblood
        case "godmaster": self = .godmaster
        case "select-profile": self = .selectProfile
        case "clear-save": self = .clearSave
        case "pause": self = .pause
        case "quit-to-menu": self = .quitToMenu
        case "inventory": self = .inventory
        case "game": self = .game
        case "quit-game": self = .quitGame
        case "enemies": self = .enemies
        case "world": self = .world
        case "shared": self = .shared
        default: return nil
        }
    }

    var labels: [LabelingClassDefinition] {
        switch self {
        case .mainTitle:
            return [
                .init(id: "main-title.hollow-knight-logo", name: "Hallow Knight Title"),
                .init(id: "main-title.start-game", name: "Start Game"),
                .init(id: LabelingClassIdentity.options, name: "Options"),
                .init(id: LabelingClassIdentity.achievements, name: "Achievements"),
                .init(id: LabelingClassIdentity.extras, name: "Extras"),
                .init(id: LabelingClassIdentity.quitGame, name: "Quit Game"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .options:
            return [
                .init(id: LabelingClassIdentity.options, name: "Options"),
                .init(id: "options.game", name: "Game"),
                .init(id: LabelingClassIdentity.audio, name: "Audio"),
                .init(id: LabelingClassIdentity.video, name: "Video"),
                .init(id: LabelingClassIdentity.controller, name: "Controller"),
                .init(id: LabelingClassIdentity.keyboard, name: "Keyboard"),
                .init(id: LabelingClassIdentity.mods, name: "Mods"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .gameOptions:
            return [
                .init(id: "game-options.game-options", name: "Game Options"),
                .init(id: "game-options.language", name: "Language"),
                .init(id: "game-options.camera-shake", name: "Camera Shake"),
                .init(id: "game-options.hud-appearance", name: "HUD Apperance"),
                .init(id: "game-options.show-achievements", name: "Show Achievements"),
                .init(id: "game-options.backer-credits", name: "Backer Credits"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .audio:
            return [
                .init(id: LabelingClassIdentity.audio, name: "Audio"),
                .init(id: "audio.master-volume", name: "Master Volume"),
                .init(id: "audio.sound-volume", name: "Sound Volume"),
                .init(id: "audio.music-volume", name: "Music Volume"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .video:
            return [
                .init(id: LabelingClassIdentity.video, name: "Video"),
                .init(id: "video.resolution", name: "Resolution"),
                .init(id: "video.full-screen", name: "Full Screen"),
                .init(id: "video.v-sync", name: "V-Sync"),
                .init(id: "video.frame-rate-cap", name: "Frame Rate Cap"),
                .init(id: "video.screen-scale", name: "Screen Scale"),
                .init(id: LabelingClassIdentity.brightness, name: "Brightness"),
                .init(id: LabelingClassIdentity.advancedSettings, name: "Advanced Settings"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .screenScale:
            return [
                .init(id: "screen-scale.scale", name: "Scale"),
                .init(id: "screen-scale.screen-corner", name: "Screen Corner"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
            ]
        case .brightness:
            return [
                .init(id: LabelingClassIdentity.brightness, name: "Brightness"),
                .init(id: "brightness.pointer", name: "Pointer"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .videoAdvancedSettings:
            return [
                .init(id: LabelingClassIdentity.advancedSettings, name: "Advanced Settings"),
                .init(id: "video-advanced.particle-effects", name: "Particle Effects"),
                .init(id: "video-advanced.blur-quality", name: "Blur Quality"),
                .init(id: "video-advanced.dithering", name: "Dithering"),
                .init(id: "video-advanced.film-grain", name: "Film Grain"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .controller:
            return [
                .init(id: LabelingClassIdentity.controller, name: "Controller"),
                .init(id: "controller.controller-diagram", name: "Controller Diagram"),
                .init(id: "controller.remap-controls", name: "Remap Controlls"),
                .init(id: LabelingClassIdentity.advancedSettings, name: "Advanced Settings"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .remapController:
            return [
                .init(id: "remap-controller.header", name: "Remap Controller"),
                .init(id: LabelingClassIdentity.jump, name: "Jump"),
                .init(id: LabelingClassIdentity.attack, name: "Attack"),
                .init(id: LabelingClassIdentity.dash, name: "Dash"),
                .init(id: LabelingClassIdentity.focusCast, name: "Focus / Cast"),
                .init(id: LabelingClassIdentity.quickMap, name: "Quick Map"),
                .init(id: LabelingClassIdentity.superDash, name: "Super Dash"),
                .init(id: LabelingClassIdentity.dreamNail, name: "Dream Nail"),
                .init(id: LabelingClassIdentity.quickCast, name: "Quick Cast"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: "remap-controller.done", name: "Done"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .controllerAdvancedSettings:
            return [
                .init(id: LabelingClassIdentity.advancedSettings, name: "Advanced Settings"),
                .init(id: "controller-advanced.vibration", name: "Vibration"),
                .init(id: "controller-advanced.native-input", name: "Native Controller Input"),
                .init(id: "controller-advanced.mfi", name: "MFI"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .keyboard:
            return [
                .init(id: LabelingClassIdentity.keyboard, name: "Keyboard"),
                .init(id: "keyboard.up", name: "Up"),
                .init(id: "keyboard.down", name: "Down"),
                .init(id: LabelingClassIdentity.jump, name: "Jump"),
                .init(id: LabelingClassIdentity.attack, name: "Attack"),
                .init(id: LabelingClassIdentity.dash, name: "Dash"),
                .init(id: LabelingClassIdentity.focusCast, name: "Focus / Cast"),
                .init(id: "keyboard.left", name: "Left"),
                .init(id: "keyboard.right", name: "Right"),
                .init(id: LabelingClassIdentity.quickMap, name: "Quick Map"),
                .init(id: LabelingClassIdentity.superDash, name: "Super Dash"),
                .init(id: LabelingClassIdentity.dreamNail, name: "Dream Nail"),
                .init(id: LabelingClassIdentity.quickCast, name: "Quick Cast"),
                .init(id: "inventory.inventory", name: "Inventory"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .mods:
            return [
                .init(id: LabelingClassIdentity.mods, name: "Mods"),
                .init(id: "mods.mod-decoration", name: "Mod Decoration"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .achievements:
            return [
                .init(id: LabelingClassIdentity.achievements, name: "Achievements"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .extras:
            return [
                .init(id: LabelingClassIdentity.extras, name: "Extras"),
                .init(id: "extras.menu-style", name: "Menu Style"),
                .init(id: "extras.credits", name: "Credits"),
                .init(id: "extras.hidden-dreams", name: "Hidden Dreams"),
                .init(id: "extras.the-grimm-troupe", name: "The Grimm Troupe"),
                .init(id: "extras.lifeblood", name: "Lifeblood"),
                .init(id: "extras.godmaster", name: "Godmaster"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .credits:
            return [
                .init(id: "extras.credits", name: "Credits"),
            ]
        case .hiddenDreams:
            return [
                .init(id: "extras.hidden-dreams", name: "Hidden Dreams"),
                .init(id: "hidden-dreams.poster", name: "Hidden Dreams Poster"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .grimmTroupe:
            return [
                .init(id: "extras.the-grimm-troupe", name: "The Grimm Troupe"),
                .init(id: "grimm-troupe.poster", name: "Grimm Troupe Poster"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .lifeblood:
            return [
                .init(id: "extras.lifeblood", name: "Lifeblood"),
                .init(id: "lifeblood.poster", name: "Lifeblood Poster"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .godmaster:
            return [
                .init(id: "extras.godmaster", name: "Godmaster"),
                .init(id: "godmaster.poster", name: "Godmaster Poster"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .selectProfile:
            return [
                .init(id: "select-profile.heading", name: "Select Profile"),
                .init(id: "select-profile.slot-1", name: "1."),
                .init(id: "select-profile.slot-2", name: "2."),
                .init(id: "select-profile.slot-3", name: "3."),
                .init(id: "select-profile.slot-4", name: "4."),
                .init(id: "select-profile.clear-save", name: "Clear Save"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .clearSave:
            return [
                .init(id: "clear-save.heading", name: "Clear Save?"),
                .init(id: LabelingClassIdentity.yes, name: "Yes"),
                .init(id: LabelingClassIdentity.no, name: "No"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .pause:
            return [
                .init(id: "pause.header", name: "Header"),
                .init(id: "pause.continue", name: "Continue"),
                .init(id: LabelingClassIdentity.options, name: "Options"),
                .init(id: LabelingClassIdentity.quitToMenu, name: "Quit To Menu"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .quitToMenu:
            return [
                .init(id: LabelingClassIdentity.quitToMenu, name: "Quit To Menu"),
                .init(id: LabelingClassIdentity.yes, name: "Yes"),
                .init(id: LabelingClassIdentity.no, name: "No"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .inventory:
            return [
                .init(id: "inventory.inventory", name: "Inventory"),
                .init(id: "inventory.corner", name: "Inventory Corner"),
            ]
        case .game:
            return [
                .init(id: "game.playable-knight", name: "Hallow Knight"),
                .init(id: "game.mana", name: "Mana"),
                .init(id: "game.health", name: "Health"),
                .init(id: "game.geo", name: "Geode"),
                .init(id: "enemies.crawlid", name: "Crawlid"),
                .init(id: "enemies.vengfly", name: "Vengfly"),
                .init(id: "enemies.shade", name: "Shade"),
                .init(id: "world.geo-deposit", name: "Geo Deposit"),
                .init(id: "world.lifeblood-cacoon", name: "Lifeblood Cacoon"),
                .init(id: "world.sign", name: "Sign"),
            ]
        case .quitGame:
            return [
                .init(id: LabelingClassIdentity.quitGame, name: "Quit Game"),
                .init(id: LabelingClassIdentity.yes, name: "Yes"),
                .init(id: LabelingClassIdentity.no, name: "No"),
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
            ]
        case .enemies:
            return [
                .init(id: "enemies.crawlid", name: "Crawlid"),
                .init(id: "enemies.vengfly", name: "Vengfly"),
                .init(id: "enemies.shade", name: "Shade"),
            ]
        case .world:
            return [
                .init(id: "world.geo-deposit", name: "Geo Deposit"),
                .init(id: "world.lifeblood-cacoon", name: "Lifeblood Cacoon"),
                .init(id: "world.sign", name: "Sign"),
            ]
        case .shared:
            return [
                .init(id: LabelingClassIdentity.selectDecoration, name: "Select Decoration"),
                .init(id: LabelingClassIdentity.back, name: "Back"),
                .init(id: LabelingClassIdentity.options, name: "Options"),
                .init(id: LabelingClassIdentity.achievements, name: "Achievements"),
                .init(id: LabelingClassIdentity.extras, name: "Extras"),
                .init(id: LabelingClassIdentity.audio, name: "Audio"),
                .init(id: LabelingClassIdentity.video, name: "Video"),
                .init(id: LabelingClassIdentity.controller, name: "Controller"),
                .init(id: LabelingClassIdentity.keyboard, name: "Keyboard"),
                .init(id: LabelingClassIdentity.mods, name: "Mods"),
                .init(id: LabelingClassIdentity.dash, name: "Dash"),
                .init(id: LabelingClassIdentity.focusCast, name: "Focus / Cast"),
                .init(id: LabelingClassIdentity.quickMap, name: "Quick Map"),
                .init(id: LabelingClassIdentity.superDash, name: "Super Dash"),
                .init(id: LabelingClassIdentity.dreamNail, name: "Dream Nail"),
                .init(id: LabelingClassIdentity.quickCast, name: "Quick Cast"),
                .init(id: LabelingClassIdentity.jump, name: "Jump"),
                .init(id: LabelingClassIdentity.attack, name: "Attack"),
                .init(id: LabelingClassIdentity.resetDefaults, name: "Reset Defaults"),
                .init(id: LabelingClassIdentity.brightness, name: "Brightness"),
                .init(id: LabelingClassIdentity.advancedSettings, name: "Advanced Settings"),
                .init(id: LabelingClassIdentity.yes, name: "Yes"),
                .init(id: LabelingClassIdentity.no, name: "No"),
            ]
        }
    }

    static func containing(classIdentifier: String) -> LabelingContext? {
        sets(containing: classIdentifier).first
    }

    static func sets(containing classIdentifier: String) -> [LabelingContext] {
        if UserObjectCatalogStore.classIdentifiers.contains(classIdentifier) {
            return [.game]
        }
        return selectableCases.filter { context in
            context.labels.contains { $0.id == classIdentifier }
        }
    }

    /// Object navigation includes organizational Enemies and World groups.
    /// Shared is an identity namespace, not a user-facing set.
    static var selectableCases: [LabelingContext] {
        contractSetCases
    }

    /// Screen-state label sets. Organizational object groups do not classify screenshots.
    static var contractSetCases: [LabelingContext] {
        allCases.filter { $0 != .shared && $0 != .enemies && $0 != .world }
    }

    /// Object browser keeps semantic categories even when they are not screen sets.
    static var objectCatalogCases: [LabelingContext] {
        contractSetCases + [.enemies, .world]
    }

    func cycledClassIdentifier(from current: String, offset: Int) -> String {
        guard !labels.isEmpty else { return current }
        let currentIndex = labels.firstIndex(where: { $0.id == current }) ?? 0
        let nextIndex = (currentIndex + offset % labels.count + labels.count) % labels.count
        return labels[nextIndex].id
    }

    func cycled(offset: Int) -> LabelingContext {
        let contexts = Self.selectableCases
        guard let currentIndex = contexts.firstIndex(of: self), !contexts.isEmpty else {
            return contexts.first ?? self
        }
        let nextIndex = (currentIndex + offset % contexts.count + contexts.count)
            % contexts.count
        return contexts[nextIndex]
    }
}

struct LabelingClassDefinition: Identifiable, Hashable {
    let id: String
    let name: String
}

/// The set/object navigator is shared by labeling and model review so both
/// modes operate on the same persisted selection and use the same controls.
struct LabelingObjectSelectionControls: View {
    @Binding var context: LabelingContext
    @Binding var selectedClassID: String
    @State private var customObjects = UserObjectCatalogStore.load().objects

    init(
        context: Binding<LabelingContext>,
        selectedClassID: Binding<String>
    ) {
        _context = context
        _selectedClassID = selectedClassID
    }

    private var availableLabels: [LabelingClassDefinition] {
        let custom = customObjects.filter { object in
            switch context {
            case .game: return true
            case .enemies: return object.group == UserObjectGroup.enemies.rawValue
            case .world: return object.group == UserObjectGroup.world.rawValue
            default: return false
            }
        }.map { LabelingClassDefinition(id: $0.identifier, name: $0.name) }
        return Array(Dictionary(
            (context.labels + custom).map { ($0.id, $0) },
            uniquingKeysWith: { first, _ in first }
        ).values).sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    var body: some View {
        HStack(spacing: 4) {
            Picker("Set", selection: contextSelection) {
                ForEach(LabelingContext.selectableCases) { item in
                    Text(item.rawValue).tag(item)
                }
            }
            .frame(width: 175)
            .labelsHidden()
            .pickerStyle(.menu)

            Picker("Object", selection: $selectedClassID) {
                ForEach(availableLabels) { item in
                    Text(item.name).tag(item.id)
                }
            }
            .frame(width: 175)
            .labelsHidden()
            .pickerStyle(.menu)

            Menu {
                ForEach(UserObjectGroup.allCases, id: \.rawValue) { group in
                    Button(group.title) { createObject(in: group) }
                }
            } label: {
                Text("+ Object")
            }
            .help("Create a named gameplay object for labeling and training")
        }
        .overlay {
            ZStack {
                Button("Previous Set") { cycleContext(by: -1) }
                    .keyboardShortcut(.leftArrow, modifiers: [])
                Button("Next Set") { cycleContext(by: 1) }
                    .keyboardShortcut(.rightArrow, modifiers: [])
                Button("Previous Object") { cycleObject(by: -1) }
                    .keyboardShortcut(.upArrow, modifiers: [])
                Button("Next Object") { cycleObject(by: 1) }
                    .keyboardShortcut(.downArrow, modifiers: [])
            }
            .frame(width: 1, height: 1)
            .opacity(0)
            .accessibilityHidden(true)
        }
    }

    private var contextSelection: Binding<LabelingContext> {
        Binding(get: { context }, set: selectContext)
    }

    private func selectContext(_ nextContext: LabelingContext) {
        context = nextContext
        if let firstClass = (nextContext.labels + UserObjectCatalogStore.definitions(for: nextContext)).first {
            selectedClassID = firstClass.id
        }
    }

    private func cycleContext(by offset: Int) {
        selectContext(context.cycled(offset: offset))
    }

    private func cycleObject(by offset: Int) {
        let labels = availableLabels
        guard !labels.isEmpty else { return }
        let index = labels.firstIndex(where: { $0.id == selectedClassID }) ?? 0
        selectedClassID = labels[(index + offset % labels.count + labels.count) % labels.count].id
    }

    private func createObject(in group: UserObjectGroup) {
        let alert = NSAlert()
        alert.messageText = "New \(group.title)"
        alert.informativeText = "Name the object you want to detect."
        alert.addButton(withTitle: "Add")
        alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 280, height: 24))
        field.placeholderString = "Object name"
        alert.accessoryView = field
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            let added = try UserObjectCatalogStore.add(name: field.stringValue, group: group)
            customObjects = UserObjectCatalogStore.load().objects
            context = .game
            selectedClassID = added.identifier
        } catch {
            let failure = NSAlert(error: error)
            failure.runModal()
        }
    }
}

struct LabelingDraftRectangle: Identifiable, Equatable {
    let id: UUID
    var classID: String
    var normalizedRect: CGRect
    var isNegative = false
}

enum LabelingTrainingEligibility {
    static func canTrain(
        classIdentifier: String,
        currentRectangles: [LabelingDraftRectangle],
        savedExampleIdentifiers: Set<UUID>,
        editingExampleIdentifier: UUID?
    ) -> Bool {
        canTrain(
            classIdentifiers: [classIdentifier],
            currentRectangles: currentRectangles,
            savedExampleIdentifiers: savedExampleIdentifiers,
            editingExampleIdentifier: editingExampleIdentifier
        )
    }

    static func canTrain(
        classIdentifiers: Set<String>,
        currentRectangles: [LabelingDraftRectangle],
        savedExampleIdentifiers: Set<UUID>,
        editingExampleIdentifier: UUID?
    ) -> Bool {
        if currentRectangles.contains(where: {
            !$0.isNegative && classIdentifiers.contains($0.classID)
        }) {
            return true
        }
        guard let editingExampleIdentifier else {
            return !savedExampleIdentifiers.isEmpty
        }
        return savedExampleIdentifiers.contains { $0 != editingExampleIdentifier }
    }
}

enum LabelingAutosavePolicy {
    static func shouldPersist(
        rectangles: [LabelingDraftRectangle],
        isEditingSavedExample: Bool
    ) -> Bool {
        isEditingSavedExample || !rectangles.isEmpty
    }
}

enum LabelingEditorMode: String, CaseIterable, Identifiable {
    case add = "Add"
    case modify = "Modify"
    case delete = "Delete"
    case negative = "Negative"

    var id: String { rawValue }

    var shortcutCharacter: Character {
        switch self {
        case .add: return "1"
        case .modify: return "2"
        case .delete: return "3"
        case .negative: return "4"
        }
    }

    var drawsRectangle: Bool { self == .add || self == .negative }
}

enum LabelingResizeCorner: CaseIterable, Hashable {
    case topLeft
    case topRight
    case bottomLeft
    case bottomRight
}

struct LabelingControlPointGeometry {
    static let markerSize: CGFloat = 5

    static func center(for corner: LabelingResizeCorner, displayRect: CGRect) -> CGPoint {
        let offset = markerSize / 2
        switch corner {
        case .topLeft:
            return CGPoint(x: displayRect.minX - offset, y: displayRect.minY - offset)
        case .topRight:
            return CGPoint(x: displayRect.maxX + offset, y: displayRect.minY - offset)
        case .bottomLeft:
            return CGPoint(x: displayRect.minX - offset, y: displayRect.maxY + offset)
        case .bottomRight:
            return CGPoint(x: displayRect.maxX + offset, y: displayRect.maxY + offset)
        }
    }
}

enum LabelingRectangleGeometry {
    static func translated(_ rectangle: CGRect, by offset: CGSize) -> CGRect {
        let standardized = rectangle.standardized
        let maximumX = max(0, 1 - standardized.width)
        let maximumY = max(0, 1 - standardized.height)
        return CGRect(
            x: min(maximumX, max(0, standardized.minX + offset.width)),
            y: min(maximumY, max(0, standardized.minY + offset.height)),
            width: standardized.width,
            height: standardized.height
        )
    }
}

struct LabelingDraftState: Equatable {
    private struct Snapshot: Equatable {
        let rectangles: [LabelingDraftRectangle]
        let selectedID: UUID?
    }

    private(set) var rectangles: [LabelingDraftRectangle] = []
    private(set) var selectedID: UUID?
    private var history: [Snapshot] = []

    init(rectangles: [LabelingDraftRectangle] = []) {
        self.rectangles = rectangles
        selectedID = nil
    }

    var selectedRectangle: LabelingDraftRectangle? {
        rectangles.first(where: { $0.id == selectedID })
    }

    var canDelete: Bool { selectedRectangle != nil }
    var canUndo: Bool { !history.isEmpty }

    mutating func reset() {
        rectangles = []
        selectedID = nil
        history = []
    }

    @discardableResult
    mutating func select(at point: CGPoint) -> LabelingDraftRectangle? {
        selectedID = rectangles.reversed().first(where: {
            $0.normalizedRect.standardized.contains(point)
        })?.id
        return selectedRectangle
    }

    @discardableResult
    mutating func select(classID: String, at point: CGPoint) -> LabelingDraftRectangle? {
        selectedID = rectangles.reversed().first(where: {
            $0.classID == classID && $0.normalizedRect.standardized.contains(point)
        })?.id
        return selectedRectangle
    }

    @discardableResult
    mutating func select(classID: String) -> LabelingDraftRectangle? {
        selectedID = rectangles.last(where: { $0.classID == classID })?.id
        return selectedRectangle
    }

    mutating func beginRectangle(
        classID: String,
        at point: CGPoint,
        isNegative: Bool = false
    ) -> UUID {
        recordSnapshot()
        let id = UUID()
        rectangles.append(LabelingDraftRectangle(
            id: id,
            classID: classID,
            normalizedRect: CGRect(origin: point, size: .zero),
            isNegative: isNegative
        ))
        selectedID = id
        return id
    }

    mutating func updateRectangle(id: UUID, normalizedRect: CGRect) {
        guard let index = rectangles.firstIndex(where: { $0.id == id }) else { return }
        rectangles[index].normalizedRect = normalizedRect
    }

    @discardableResult
    mutating func beginModification(id: UUID) -> Bool {
        guard rectangles.contains(where: { $0.id == id }) else { return false }
        recordSnapshot()
        selectedID = id
        return true
    }

    mutating func finishRectangle(id: UUID, minimumSize: CGFloat = 0.003) {
        guard let rectangle = rectangles.first(where: { $0.id == id }) else { return }
        if rectangle.normalizedRect.width < minimumSize
            || rectangle.normalizedRect.height < minimumSize {
            restoreLastSnapshot()
        } else if history.last == Snapshot(rectangles: rectangles, selectedID: selectedID) {
            history.removeLast()
        }
    }

    mutating func deleteSelected() {
        guard let selectedID,
              rectangles.contains(where: { $0.id == selectedID }) else { return }
        recordSnapshot()
        rectangles.removeAll(where: { $0.id == selectedID })
        self.selectedID = nil
    }

    /// Turns the topmost positive box at a point into an explicit hard-negative
    /// correction. The original class and bounds are retained, so the trainer
    /// learns that this exact model suggestion must be rejected.
    @discardableResult
    mutating func markNegative(at point: CGPoint) -> LabelingDraftRectangle? {
        guard let index = rectangles.indices.reversed().first(where: {
            !rectangles[$0].isNegative
                && rectangles[$0].normalizedRect.standardized.contains(point)
        }) else { return nil }
        recordSnapshot()
        rectangles[index].isNegative = true
        selectedID = rectangles[index].id
        return rectangles[index]
    }

    mutating func undo() {
        restoreLastSnapshot()
    }

    private mutating func recordSnapshot() {
        history.append(Snapshot(rectangles: rectangles, selectedID: selectedID))
    }

    private mutating func restoreLastSnapshot() {
        guard let snapshot = history.popLast() else { return }
        rectangles = snapshot.rectangles
        selectedID = snapshot.selectedID
    }
}

/// Converts between SwiftUI canvas points and top-left-origin image-relative coordinates.
/// Both coordinate spaces use the same displayed aspect-fit rectangle, so backing scale
/// and the source image's pixel dimensions cannot shift a saved annotation.
struct LabelingImageLayout: Equatable {
    let imageSize: CGSize
    let containerSize: CGSize
    let zoomScale: CGFloat
    let panOffset: CGSize

    init(
        imageSize: CGSize,
        containerSize: CGSize,
        zoomScale: CGFloat = 1,
        panOffset: CGSize = .zero
    ) {
        self.imageSize = imageSize
        self.containerSize = containerSize
        self.zoomScale = zoomScale
        self.panOffset = panOffset
    }

    var baseFittedRect: CGRect {
        guard Self.isValid(imageSize), Self.isValid(containerSize) else { return .zero }
        let scale = min(containerSize.width / imageSize.width, containerSize.height / imageSize.height)
        let size = CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
        return CGRect(
            x: (containerSize.width - size.width) / 2,
            y: (containerSize.height - size.height) / 2,
            width: size.width,
            height: size.height
        )
    }

    var fittedRect: CGRect {
        let base = baseFittedRect
        guard base.width > 0, base.height > 0,
              zoomScale.isFinite, zoomScale > 0,
              panOffset.width.isFinite, panOffset.height.isFinite else { return .zero }
        let size = CGSize(width: base.width * zoomScale, height: base.height * zoomScale)
        return CGRect(
            x: base.midX + panOffset.width - size.width / 2,
            y: base.midY + panOffset.height - size.height / 2,
            width: size.width,
            height: size.height
        )
    }

    func normalizedPoint(for point: CGPoint, clamped: Bool = false) -> CGPoint? {
        let fitted = fittedRect
        guard fitted.width > 0, fitted.height > 0 else { return nil }
        guard clamped || fitted.contains(point) else { return nil }
        return CGPoint(
            x: min(1, max(0, (point.x - fitted.minX) / fitted.width)),
            y: min(1, max(0, (point.y - fitted.minY) / fitted.height))
        )
    }

    func normalizedRect(from start: CGPoint, to end: CGPoint) -> CGRect? {
        guard let first = normalizedPoint(for: start),
              let last = normalizedPoint(for: end, clamped: true) else { return nil }
        return CGRect(
            x: min(first.x, last.x),
            y: min(first.y, last.y),
            width: abs(last.x - first.x),
            height: abs(last.y - first.y)
        )
    }

    func displayRect(for normalizedRect: CGRect) -> CGRect? {
        let fitted = fittedRect
        guard fitted.width > 0, fitted.height > 0,
              normalizedRect.minX.isFinite, normalizedRect.minY.isFinite,
              normalizedRect.maxX.isFinite, normalizedRect.maxY.isFinite else { return nil }
        let unit = CGRect(x: 0, y: 0, width: 1, height: 1)
        let clipped = normalizedRect.standardized.intersection(unit)
        guard !clipped.isNull, !clipped.isEmpty else { return nil }
        return CGRect(
            x: fitted.minX + clipped.minX * fitted.width,
            y: fitted.minY + clipped.minY * fitted.height,
            width: clipped.width * fitted.width,
            height: clipped.height * fitted.height
        )
    }

    private static func isValid(_ size: CGSize) -> Bool {
        size.width.isFinite && size.height.isFinite && size.width > 0 && size.height > 0
    }
}

enum LabelingViewportTransform {
    static let minimumZoom: CGFloat = 1
    static let maximumZoom: CGFloat = 8

    static func zoomedPan(
        currentPan: CGSize,
        currentZoom: CGFloat,
        newZoom: CGFloat,
        focus: CGPoint,
        containerSize: CGSize
    ) -> CGSize {
        guard currentZoom.isFinite, currentZoom > 0, newZoom.isFinite, newZoom > 0 else {
            return currentPan
        }
        let ratio = newZoom / currentZoom
        let oldCenter = CGPoint(
            x: containerSize.width / 2 + currentPan.width,
            y: containerSize.height / 2 + currentPan.height
        )
        let newCenter = CGPoint(
            x: focus.x - (focus.x - oldCenter.x) * ratio,
            y: focus.y - (focus.y - oldCenter.y) * ratio
        )
        return CGSize(
            width: newCenter.x - containerSize.width / 2,
            height: newCenter.y - containerSize.height / 2
        )
    }

}

struct LabelingEditorView: View {
    let image: CGImage
    let objectAtlas: LabelingObjectAtlasSnapshot?
    let onShowRecentFrames: () -> Void
    let onDraftCommit: (LabelingDraftState) -> Void

    @Binding var context: LabelingContext
    @Binding var draft: LabelingDraftState
    @Binding var selectedClassID: String
    @State private var editorMode: LabelingEditorMode
    @State private var activeDrawingID: UUID?
    @State private var activeMovingID: UUID?
    @State private var activeMoveStartRectangle: CGRect?
    @State private var gestureConsumed = false
    @State private var activeResizeCorner: LabelingResizeCorner?
    @State private var activeResizeAnchor: CGPoint?
    @State private var activeResizeStartCorner: CGPoint?
    @State private var zoomScale: CGFloat = 1
    @State private var panOffset: CGSize = .zero
    @State private var panStartOffset: CGSize = .zero

    init(
        image: CGImage,
        objectAtlas: LabelingObjectAtlasSnapshot? = nil,
        onShowRecentFrames: @escaping () -> Void,
        onDraftCommit: @escaping (LabelingDraftState) -> Void,
        context: Binding<LabelingContext>,
        draft: Binding<LabelingDraftState>,
        selectedClassID: Binding<String>,
        initialEditorMode: LabelingEditorMode = .add
    ) {
        self.image = image
        self.objectAtlas = objectAtlas
        self.onShowRecentFrames = onShowRecentFrames
        self.onDraftCommit = onDraftCommit
        _context = context
        _draft = draft
        _selectedClassID = selectedClassID
        _editorMode = State(initialValue: initialEditorMode)
    }

    var body: some View {
        VStack(spacing: 0) {
            editorControls
            Divider()

            GeometryReader { proxy in
                let layout = LabelingImageLayout(
                    imageSize: CGSize(width: image.width, height: image.height),
                    containerSize: proxy.size,
                    zoomScale: zoomScale,
                    panOffset: panOffset
                )
                let fitted = layout.fittedRect

                ZStack(alignment: .topLeading) {
                    Color.black
                    Image(decorative: image, scale: 1)
                        .resizable()
                        .frame(width: fitted.width, height: fitted.height)
                        .position(x: fitted.midX, y: fitted.midY)
                        .allowsHitTesting(false)
                        .accessibilityLabel("Frozen clean game screenshot")

                    // Zoomed image can extend beyond canvas. Keep drawing hit-region
                    // inside canvas so it never covers toolbar controls above.
                    Rectangle()
                        .fill(.clear)
                        .contentShape(Rectangle())
                        .frame(width: proxy.size.width, height: proxy.size.height)
                        .position(x: proxy.size.width / 2, y: proxy.size.height / 2)
                        .gesture(canvasGesture(layout: layout))

                    ForEach(draft.rectangles) { rectangle in
                        if let displayRect = layout.displayRect(for: rectangle.normalizedRect) {
                            annotationOverlay(
                                displayRect: displayRect,
                                classIdentifier: rectangle.classID,
                                isSelected: rectangle.id == draft.selectedID,
                                isNegative: rectangle.isNegative
                            )
                        }
                    }

                    if editorMode == .modify,
                       let selected = draft.selectedRectangle,
                       let displayRect = layout.displayRect(for: selected.normalizedRect) {
                        resizeHandles(
                            rectangleID: selected.id,
                            displayRect: displayRect,
                            layout: layout
                        )
                    }

                    LabelingCanvasInteractionView(
                        imageRect: fitted,
                        showsCrosshair: editorMode.drawsRectangle,
                        onScroll: { logarithmicDelta, location in
                            updateZoom(
                                logarithmicDelta: logarithmicDelta,
                                focus: location,
                                containerSize: proxy.size
                            )
                        },
                        onPanBegan: {
                            panStartOffset = panOffset
                        },
                        onPanChanged: { translation in
                            updatePan(translation: translation)
                        },
                        onFit: {
                            zoomScale = LabelingViewportTransform.minimumZoom
                            panOffset = .zero
                        }
                    )
                    .frame(width: proxy.size.width, height: proxy.size.height)
                    .accessibilityHidden(true)
                }
                .frame(width: proxy.size.width, height: proxy.size.height)
                .clipped()
                .coordinateSpace(name: "labeling-canvas")
            }
        }
        .background(Color.black)
    }

    private var editorControls: some View {
        HStack(spacing: 12) {
            HStack(spacing: 4) {
                LabelingObjectSelectionControls(
                    context: $context,
                    selectedClassID: $selectedClassID
                )
                if let icon = objectAtlas?.icon(for: selectedClassID) {
                    LabelingToolbarObjectIcon(
                        image: icon,
                        classIdentifier: selectedClassID
                    )
                }
            }
            .fixedSize(horizontal: true, vertical: false)

            Spacer(minLength: 12)
            StableSegmentedPicker(
                label: "Editing mode", choices: LabelingEditorMode.allCases,
                title: { $0.rawValue }, selection: $editorMode
            )
            .frame(width: 290)

            Button("Recent Frames", action: onShowRecentFrames)
                .keyboardShortcut("r", modifiers: [])
        }
        .font(.caption)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, minHeight: 46, alignment: .leading)
        .background(.regularMaterial)
        .overlay {
            ZStack {
                Button("Undo") {
                    undoLastChange()
                }
                .disabled(!draft.canUndo)
                .keyboardShortcut("z", modifiers: .command)
                ForEach(LabelingEditorMode.allCases) { mode in
                    Button("\(mode.rawValue) mode") {
                        editorMode = mode
                    }
                    .keyboardShortcut(
                        KeyEquivalent(mode.shortcutCharacter),
                        modifiers: []
                    )
                }
            }
            .frame(width: 1, height: 1)
            .opacity(0)
            .accessibilityHidden(true)
        }
        .onChange(of: selectedClassID) { _, newClassID in
            if editorMode != .modify || draft.selectedRectangle?.classID != newClassID {
                draft.select(classID: newClassID)
            }
        }
        .onAppear {
            draft.select(classID: selectedClassID)
        }
    }

    private func annotationOverlay(
        displayRect: CGRect,
        classIdentifier: String,
        isSelected: Bool,
        isNegative: Bool
    ) -> some View {
        let color: Color
        if isNegative {
            color = .pink
        } else {
            color = LabelingVisualIdentity.color(for: classIdentifier)
        }
        let lineWidth: CGFloat = isSelected ? 3 : 2
        return Rectangle()
            .fill(color.opacity(editorMode == .delete
                                ? (isSelected ? 0.24 : 0.10)
                                : (isSelected ? 0.14 : 0.07)))
            .overlay(
                Rectangle()
                    .strokeBorder(color, lineWidth: lineWidth)
                    .padding(-lineWidth)
            )
            .overlay {
                if isNegative {
                    Path { path in
                        path.move(to: CGPoint(x: 0, y: 0))
                        path.addLine(to: CGPoint(x: displayRect.width, y: displayRect.height))
                        path.move(to: CGPoint(x: displayRect.width, y: 0))
                        path.addLine(to: CGPoint(x: 0, y: displayRect.height))
                    }
                    .stroke(color, lineWidth: 1.5)
                }
            }
            .overlay(alignment: .topLeading) {
                if let icon = objectAtlas?.icon(for: classIdentifier) {
                    annotationIconBadge(
                        icon,
                        color: color,
                        isNegative: isNegative
                    )
                    // Keep the icon immediately attached to the box without
                    // covering any pixels inside the labeled rectangle.
                    .offset(
                        x: 0,
                        y: displayRect.minY >= 26 ? -24 : displayRect.height + 4
                    )
                }
            }
            .frame(width: displayRect.width, height: displayRect.height)
            .position(x: displayRect.midX, y: displayRect.midY)
            .allowsHitTesting(false)
    }

    private func annotationIconBadge(
        _ image: CGImage,
        color: Color,
        isNegative: Bool
    ) -> some View {
        let height: CGFloat = 20
        let aspect = image.height > 0
            ? CGFloat(image.width) / CGFloat(image.height)
            : 1
        let width = min(80, max(height, height * aspect))
        return Image(decorative: image, scale: 1)
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .padding(2)
            .frame(width: width, height: height)
            .background(.black.opacity(0.82), in: RoundedRectangle(cornerRadius: 3))
            .overlay(
                RoundedRectangle(cornerRadius: 3)
                    .stroke(isNegative ? Color.pink : color, lineWidth: 1)
            )
            .accessibilityHidden(true)
    }

    @ViewBuilder
    private func resizeHandles(
        rectangleID: UUID,
        displayRect: CGRect,
        layout: LabelingImageLayout
    ) -> some View {
        ForEach(LabelingResizeCorner.allCases, id: \.self) { corner in
            resizeHandle(
                rectangleID: rectangleID,
                corner: corner,
                position: LabelingControlPointGeometry.center(
                    for: corner,
                    displayRect: displayRect
                ),
                layout: layout
            )
        }
    }

    private func resizeHandle(
        rectangleID: UUID,
        corner: LabelingResizeCorner,
        position: CGPoint,
        layout: LabelingImageLayout
    ) -> some View {
        ZStack {
            Color.clear
            Rectangle()
                .fill(Color(red: 0.95, green: 0.67, blue: 0.20))
                .frame(
                    width: LabelingControlPointGeometry.markerSize,
                    height: LabelingControlPointGeometry.markerSize
                )
        }
        .frame(width: 18, height: 18)
        .contentShape(Rectangle())
        .position(position)
        .gesture(resizeGesture(rectangleID: rectangleID, corner: corner, layout: layout))
        .accessibilityLabel(resizeAccessibilityLabel(corner))
    }

    private func resizeAccessibilityLabel(_ corner: LabelingResizeCorner) -> String {
        switch corner {
        case .topLeft: return "Resize top left"
        case .topRight: return "Resize top right"
        case .bottomLeft: return "Resize bottom left"
        case .bottomRight: return "Resize bottom right"
        }
    }

    private func canvasGesture(layout: LabelingImageLayout) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("labeling-canvas"))
            .onChanged { value in
                if activeDrawingID == nil && activeMovingID == nil && !gestureConsumed {
                    guard let start = layout.normalizedPoint(for: value.startLocation) else { return }

                    switch editorMode {
                    case .delete:
                        if let selected = draft.selectedRectangle,
                           selected.normalizedRect.standardized.contains(start) {
                            draft.deleteSelected()
                        } else if draft.select(at: start) != nil {
                            draft.deleteSelected()
                        }
                        gestureConsumed = true
                    case .modify:
                        if let selected = draft.select(at: start),
                           draft.beginModification(id: selected.id) {
                            selectClassIdentifier(selected.classID)
                            activeMovingID = selected.id
                            activeMoveStartRectangle = selected.normalizedRect.standardized
                        } else {
                            gestureConsumed = true
                        }
                    case .add:
                        activeDrawingID = draft.beginRectangle(
                            classID: selectedClassID,
                            at: start
                        )
                    case .negative:
                        if let corrected = draft.markNegative(at: start) {
                            selectClassIdentifier(corrected.classID)
                            gestureConsumed = true
                        } else {
                            activeDrawingID = draft.beginRectangle(
                                classID: selectedClassID,
                                at: start,
                                isNegative: true
                            )
                        }
                    }
                }

                if editorMode == .modify,
                   let activeMovingID,
                   let startRectangle = activeMoveStartRectangle {
                    let fitted = layout.fittedRect
                    guard fitted.width > 0, fitted.height > 0 else { return }
                    draft.updateRectangle(
                        id: activeMovingID,
                        normalizedRect: LabelingRectangleGeometry.translated(
                            startRectangle,
                            by: CGSize(
                                width: value.translation.width / fitted.width,
                                height: value.translation.height / fitted.height
                            )
                        )
                    )
                    return
                }

                guard editorMode.drawsRectangle,
                      !gestureConsumed,
                      let activeDrawingID,
                      let normalized = layout.normalizedRect(
                        from: value.startLocation,
                        to: value.location
                      ) else { return }
                draft.updateRectangle(id: activeDrawingID, normalizedRect: normalized)
            }
            .onEnded { value in
                var addedClassIdentifier: String?
                if let activeDrawingID {
                    draft.finishRectangle(id: activeDrawingID)
                    if editorMode == .add,
                       let rectangle = draft.rectangles.first(where: { $0.id == activeDrawingID }),
                       !rectangle.isNegative {
                        addedClassIdentifier = rectangle.classID
                    }
                }
                if let activeMovingID {
                    draft.finishRectangle(id: activeMovingID)
                }
                activeDrawingID = nil
                activeMovingID = nil
                activeMoveStartRectangle = nil
                gestureConsumed = false
                onDraftCommit(draft)
                if let addedClassIdentifier {
                    advanceAfterAdding(addedClassIdentifier)
                }
            }
    }

    private func resizeGesture(
        rectangleID: UUID,
        corner: LabelingResizeCorner,
        layout: LabelingImageLayout
    ) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("labeling-canvas"))
            .onChanged { value in
                if activeResizeCorner == nil {
                    guard let rectangle = draft.selectedRectangle,
                          rectangle.id == rectangleID,
                          draft.beginModification(id: rectangleID) else { return }
                    let normalized = rectangle.normalizedRect.standardized
                    activeResizeCorner = corner
                    switch corner {
                    case .topLeft:
                        activeResizeAnchor = CGPoint(x: normalized.maxX, y: normalized.maxY)
                        activeResizeStartCorner = CGPoint(x: normalized.minX, y: normalized.minY)
                    case .topRight:
                        activeResizeAnchor = CGPoint(x: normalized.minX, y: normalized.maxY)
                        activeResizeStartCorner = CGPoint(x: normalized.maxX, y: normalized.minY)
                    case .bottomLeft:
                        activeResizeAnchor = CGPoint(x: normalized.maxX, y: normalized.minY)
                        activeResizeStartCorner = CGPoint(x: normalized.minX, y: normalized.maxY)
                    case .bottomRight:
                        activeResizeAnchor = CGPoint(x: normalized.minX, y: normalized.minY)
                        activeResizeStartCorner = CGPoint(x: normalized.maxX, y: normalized.maxY)
                    }
                }

                guard activeResizeCorner == corner,
                      let anchor = activeResizeAnchor,
                      let startCorner = activeResizeStartCorner else { return }
                let fitted = layout.fittedRect
                guard fitted.width > 0, fitted.height > 0 else { return }
                let point = CGPoint(
                    x: min(1, max(0, startCorner.x + value.translation.width / fitted.width)),
                    y: min(1, max(0, startCorner.y + value.translation.height / fitted.height))
                )
                draft.updateRectangle(
                    id: rectangleID,
                    normalizedRect: CGRect(
                        x: min(anchor.x, point.x),
                        y: min(anchor.y, point.y),
                        width: abs(point.x - anchor.x),
                        height: abs(point.y - anchor.y)
                    )
                )
            }
            .onEnded { _ in
                if activeResizeCorner == corner {
                    draft.finishRectangle(id: rectangleID)
                    onDraftCommit(draft)
                }
                activeResizeCorner = nil
                activeResizeAnchor = nil
                activeResizeStartCorner = nil
            }
    }

    private func undoLastChange() {
        draft.undo()
        if let selected = draft.selectedRectangle {
            selectClassIdentifier(selected.classID)
        }
        onDraftCommit(draft)
    }

    private func selectClassIdentifier(_ classIdentifier: String) {
        if !context.labels.contains(where: { $0.id == classIdentifier }),
           let selectedContext = LabelingContext.containing(classIdentifier: classIdentifier) {
            context = selectedContext
        }
        selectedClassID = classIdentifier
    }

    private func advanceAfterAdding(_ classIdentifier: String) {
        guard LabelingContractEvaluator.shouldAdvance(
            afterAdding: classIdentifier,
            in: context,
            rectangles: draft.rectangles
        ),
        let index = context.labels.firstIndex(where: {
            LabelingClassIdentity.matches($0.id, classIdentifier)
        }),
        index + 1 < context.labels.count else { return }
        selectedClassID = context.labels[index + 1].id
    }

    private func updateZoom(
        logarithmicDelta: CGFloat,
        focus: CGPoint,
        containerSize: CGSize
    ) {
        let factor = CGFloat(exp(Double(logarithmicDelta)))
        let newZoom = min(
            LabelingViewportTransform.maximumZoom,
            max(LabelingViewportTransform.minimumZoom, zoomScale * factor)
        )
        guard abs(newZoom - zoomScale) > 0.0001 else { return }
        panOffset = LabelingViewportTransform.zoomedPan(
            currentPan: panOffset,
            currentZoom: zoomScale,
            newZoom: newZoom,
            focus: focus,
            containerSize: containerSize
        )
        zoomScale = newZoom
    }

    private func updatePan(translation: CGSize) {
        panOffset = CGSize(
            width: panStartOffset.width + translation.width,
            height: panStartOffset.height + translation.height
        )
    }
}

private struct LabelingCanvasInteractionView: NSViewRepresentable {
    let imageRect: CGRect
    let showsCrosshair: Bool
    let onScroll: (CGFloat, CGPoint) -> Void
    let onPanBegan: () -> Void
    let onPanChanged: (CGSize) -> Void
    let onFit: () -> Void

    func makeNSView(context: Context) -> EventView {
        let view = EventView()
        update(view)
        return view
    }

    func updateNSView(_ nsView: EventView, context: Context) {
        update(nsView)
    }

    private func update(_ view: EventView) {
        view.onScroll = onScroll
        view.onPanBegan = onPanBegan
        view.onPanChanged = onPanChanged
        view.onFit = onFit
        view.configureCrosshair(imageRect: imageRect, visible: showsCrosshair)
    }

    final class EventView: NSView {
        var onScroll: ((CGFloat, CGPoint) -> Void)?
        var onPanBegan: (() -> Void)?
        var onPanChanged: ((CGSize) -> Void)?
        var onFit: (() -> Void)?
        private var imageRect = CGRect.zero
        private var showsCrosshair = false
        private var cursorLocation: CGPoint?
        private var dragMonitor: Any?
        private var dragOrigin: CGPoint?
        private var pushedCursor = false
        private var cursorTrackingArea: NSTrackingArea?

        override var isFlipped: Bool { true }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            window?.acceptsMouseMovedEvents = true
            if let dragMonitor { NSEvent.removeMonitor(dragMonitor) }
            dragMonitor = nil
            if window != nil {
                dragMonitor = NSEvent.addLocalMonitorForEvents(matching: .leftMouseDragged) { [weak self] event in
                    if let self, event.window === self.window {
                        self.updateCursor(self.convert(event.locationInWindow, from: nil))
                    }
                    return event
                }
            }
        }

        deinit { if let dragMonitor { NSEvent.removeMonitor(dragMonitor) } }

        func configureCrosshair(imageRect: CGRect, visible: Bool) {
            guard self.imageRect != imageRect || showsCrosshair != visible else { return }
            self.imageRect = imageRect
            showsCrosshair = visible
            needsDisplay = true
        }

        private func updateCursor(_ point: CGPoint?) {
            guard point != cursorLocation else { return }
            cursorLocation = point
            if showsCrosshair { needsDisplay = true }
        }

        override func draw(_ dirtyRect: NSRect) {
            guard showsCrosshair, let cursor = cursorLocation,
                  bounds.contains(cursor), imageRect.contains(cursor) else { return }
            let clipped = imageRect.intersection(bounds)
            let path = NSBezierPath()
            path.move(to: CGPoint(x: clipped.minX, y: cursor.y))
            path.line(to: CGPoint(x: clipped.maxX, y: cursor.y))
            path.move(to: CGPoint(x: cursor.x, y: clipped.minY))
            path.line(to: CGPoint(x: cursor.x, y: clipped.maxY))
            path.lineWidth = 1
            path.setLineDash([2, 3], count: 2, phase: 0)
            NSColor.white.withAlphaComponent(0.72).setStroke()
            path.stroke()
        }

        override func updateTrackingAreas() {
            if let cursorTrackingArea {
                removeTrackingArea(cursorTrackingArea)
            }
            let trackingArea = NSTrackingArea(
                rect: bounds,
                options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow],
                owner: self,
                userInfo: nil
            )
            addTrackingArea(trackingArea)
            cursorTrackingArea = trackingArea
            super.updateTrackingAreas()
        }

        override func hitTest(_ point: NSPoint) -> NSView? {
            guard let event = NSApp.currentEvent else { return nil }
            if event.type == .scrollWheel {
                return self
            }
            if event.type == .leftMouseDown, event.modifierFlags.contains(.shift) {
                return self
            }
            if event.type == .rightMouseDown {
                return self
            }
            return nil
        }

        override func scrollWheel(with event: NSEvent) {
            let rawDelta = event.scrollingDeltaY
            let multiplier: CGFloat = event.hasPreciseScrollingDeltas ? 0.012 : 0.12
            let logarithmicDelta = min(0.35, max(-0.35, rawDelta * multiplier))
            let location = convert(event.locationInWindow, from: nil)
            updateCursor(location)
            onScroll?(logarithmicDelta, location)
        }

        override func mouseDown(with event: NSEvent) {
            let location = convert(event.locationInWindow, from: nil)
            dragOrigin = location
            updateCursor(location)
            onPanBegan?()
            NSCursor.closedHand.push()
            pushedCursor = true
        }

        override func mouseDragged(with event: NSEvent) {
            guard let dragOrigin else { return }
            let location = convert(event.locationInWindow, from: nil)
            updateCursor(location)
            onPanChanged?(CGSize(
                width: location.x - dragOrigin.x,
                height: location.y - dragOrigin.y
            ))
        }

        override func mouseUp(with event: NSEvent) {
            updateCursor(convert(event.locationInWindow, from: nil))
            dragOrigin = nil
            if pushedCursor {
                NSCursor.pop()
                pushedCursor = false
            }
        }

        override func rightMouseDown(with event: NSEvent) {
            updateCursor(convert(event.locationInWindow, from: nil))
            onFit?()
        }

        override func mouseMoved(with event: NSEvent) {
            updateCursor(convert(event.locationInWindow, from: nil))
        }

        override func mouseEntered(with event: NSEvent) {
            updateCursor(convert(event.locationInWindow, from: nil))
        }

        override func mouseExited(with event: NSEvent) {
            updateCursor(nil)
        }
    }
}
