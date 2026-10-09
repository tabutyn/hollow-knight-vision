import Foundation

/// The wire contract shared by Vision and the in-game receiver.  This file is
/// deliberately Foundation-only so its safety rules can be tested without a
/// running capture session or a game process.
enum InputBridgeProtocol {
    static let version = 1
    static let watchdogInterval: TimeInterval = 0.250
}

/// Receiver-level controls are negotiated separately from the version-one
/// input stream so older receiver builds fail closed instead of freezing the
/// game without a confirmed recovery path.
enum ReceiverControlProtocol {
    static let version = 2
    static let pauseCapability = "pause-lease-v1"
    static let menuShortcutsCapability = "menu-shortcuts-v1"
    static let pointerCapability = "pointer-events-v1"
    static let playerCheckpointCapability = "player-checkpoint-v1"
    static let playerPosePlaybackCapability = "player-pose-playback-v1"
    static let groundTruthCapability = "ground-truth-telemetry-v1"
    static let playerOpsCapability = "player-ops-v1"
}

struct ReceiverCapabilityRequest: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID

    let renderFrameMarker: Bool?

    init(sessionID: UUID, renderFrameMarker: Bool = false) {
        self.renderFrameMarker = renderFrameMarker ? true : nil
        version = ReceiverControlProtocol.version
        type = "hello"
        self.sessionID = sessionID
    }
}

struct ReceiverCapabilitiesAcknowledgement: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let capabilities: [String]
    let pauseLeaseMilliseconds: Int
}

struct ReceiverPauseCommand: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let leaseMilliseconds: Int?

    static func pause(sessionID: UUID, leaseMilliseconds: Int) -> Self {
        Self(
            version: ReceiverControlProtocol.version,
            type: "pause",
            sessionID: sessionID,
            commandID: UUID(),
            leaseMilliseconds: leaseMilliseconds
        )
    }

    static func resume(sessionID: UUID) -> Self {
        Self(
            version: ReceiverControlProtocol.version,
            type: "resume",
            sessionID: sessionID,
            commandID: UUID(),
            leaseMilliseconds: nil
        )
    }
}

struct ReceiverPauseAcknowledgement: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let paused: Bool
}

struct ReceiverPointerCommand: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let sequence: UInt64
    let kind: String
    let normalizedX: Double
    let normalizedY: Double
    let clickCount: Int

    init(sessionID: UUID, sequence: UInt64, event: GamePointerEvent) {
        version = ReceiverControlProtocol.version
        type = "pointer"
        self.sessionID = sessionID
        self.sequence = sequence
        switch event.kind {
        case .moved: kind = "moved"
        case .leftDown: kind = "leftDown"
        case .leftDragged: kind = "leftDragged"
        case .leftUp: kind = "leftUp"
        case .exited: kind = "exited"
        }
        normalizedX = Double(event.normalizedPoint.x)
        normalizedY = Double(event.normalizedPoint.y)
        clickCount = max(1, event.clickCount)
    }
}

struct RecordedGameCheckpoint: Codable, Equatable, Sendable {
    let sceneName: String
    /// A real TransitionPoint in the saved scene. Cross-room replay uses it
    /// to initialize Hollow Knight's scene-transition state before restoring
    /// the exact recorded pose.
    let entryGateName: String?
    let heroX: Double
    let heroY: Double
    let heroZ: Double
    let velocityX: Double
    let velocityY: Double
    let facingRight: Bool
    let grounded: Bool
    let cameraX: Double
    let cameraY: Double
    let cameraZ: Double
    let cameraTargetX: Double
    let cameraTargetY: Double
    let cameraTargetZ: Double
    let enemies: [RecordedEnemyCheckpoint]?
    let isFirstGame: Bool?
    let enteredTutorialFirstTime: Bool?
    let visitedDirtmouth: Bool?
    let visitedCrossroads: Bool?
    let openedTown: Bool?
    let openedCrossroads: Bool?
    let scenesVisited: [String]?

    init(
        sceneName: String,
        entryGateName: String? = nil,
        heroX: Double,
        heroY: Double,
        heroZ: Double,
        velocityX: Double,
        velocityY: Double,
        facingRight: Bool,
        grounded: Bool,
        cameraX: Double,
        cameraY: Double,
        cameraZ: Double,
        cameraTargetX: Double,
        cameraTargetY: Double,
        cameraTargetZ: Double,
        enemies: [RecordedEnemyCheckpoint]? = nil,
        isFirstGame: Bool? = nil,
        enteredTutorialFirstTime: Bool? = nil,
        visitedDirtmouth: Bool? = nil,
        visitedCrossroads: Bool? = nil,
        openedTown: Bool? = nil,
        openedCrossroads: Bool? = nil,
        scenesVisited: [String]? = nil
    ) {
        self.sceneName = sceneName
        self.entryGateName = entryGateName
        self.heroX = heroX
        self.heroY = heroY
        self.heroZ = heroZ
        self.velocityX = velocityX
        self.velocityY = velocityY
        self.facingRight = facingRight
        self.grounded = grounded
        self.cameraX = cameraX
        self.cameraY = cameraY
        self.cameraZ = cameraZ
        self.cameraTargetX = cameraTargetX
        self.cameraTargetY = cameraTargetY
        self.cameraTargetZ = cameraTargetZ
        self.enemies = enemies
        self.isFirstGame = isFirstGame
        self.enteredTutorialFirstTime = enteredTutorialFirstTime
        self.visitedDirtmouth = visitedDirtmouth
        self.visitedCrossroads = visitedCrossroads
        self.openedTown = openedTown
        self.openedCrossroads = openedCrossroads
        self.scenesVisited = scenesVisited
    }

    var hasFiniteCoordinates: Bool {
        [
            heroX, heroY, heroZ, velocityX, velocityY,
            cameraX, cameraY, cameraZ,
            cameraTargetX, cameraTargetY, cameraTargetZ,
        ].allSatisfy { $0.isFinite && abs($0) <= 1_000_000 }
            && !sceneName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}

struct RecordedEnemyCheckpoint: Codable, Equatable, Sendable {
    let name: String
    let x: Double
    let y: Double
    let z: Double
    let hp: Int
    let active: Bool
}

struct ReceiverCheckpointCommand: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let checkpoint: RecordedGameCheckpoint?

    static func capture(sessionID: UUID, commandID: UUID = UUID()) -> Self {
        Self(
            version: ReceiverControlProtocol.version,
            type: "captureCheckpoint",
            sessionID: sessionID,
            commandID: commandID,
            checkpoint: nil
        )
    }

    static func restore(
        sessionID: UUID,
        checkpoint: RecordedGameCheckpoint,
        commandID: UUID = UUID()
    ) -> Self {
        Self(
            version: ReceiverControlProtocol.version,
            type: "restoreCheckpoint",
            sessionID: sessionID,
            commandID: commandID,
            checkpoint: checkpoint
        )
    }
}

struct ReceiverCheckpointAcknowledgement: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let accepted: Bool
    let checkpoint: RecordedGameCheckpoint?
    let failure: String?
}

struct PlayerTestState: Codable, Equatable, Sendable {
    var maxHealth: Int
    var health: Int
    var lifebloodSeed: Int
    var mana: Int
    var extraManaSlots: Int
    var geo: Int
    var invincible: Bool = true

    static let defaults = Self(
        maxHealth: 5,
        health: 5,
        lifebloodSeed: 0,
        mana: 99,
        extraManaSlots: 0,
        geo: 0,
        invincible: true
    )

    var normalized: Self {
        let safeMaxHealth = min(9, max(1, maxHealth))
        let safeExtraManaSlots = min(3, max(0, extraManaSlots))
        let manaCapacity = 99 + safeExtraManaSlots * 33
        return Self(
            maxHealth: safeMaxHealth,
            health: min(safeMaxHealth, max(1, health)),
            lifebloodSeed: min(9, max(0, lifebloodSeed)),
            mana: min(manaCapacity, max(0, mana)),
            extraManaSlots: safeExtraManaSlots,
            geo: min(9_999_999, max(0, geo)),
            invincible: invincible
        )
    }

    static func randomized(
        invincible: Bool = true,
        using generator: inout some RandomNumberGenerator
    ) -> Self {
        let maxHealth = Int.random(in: 1...9, using: &generator)
        let extraManaSlots = Int.random(in: 0...3, using: &generator)
        let manaCapacity = 99 + extraManaSlots * 33
        let geoRanges: [ClosedRange<Int>] = [0...9, 10...99, 100...999, 1_000...9_999]
        let geoRange = geoRanges.randomElement(using: &generator) ?? 0...9
        return Self(
            maxHealth: maxHealth,
            health: Int.random(in: 1...maxHealth, using: &generator),
            lifebloodSeed: Int.random(in: 0...9, using: &generator),
            mana: Int.random(in: 0...manaCapacity, using: &generator),
            extraManaSlots: extraManaSlots,
            geo: Int.random(in: geoRange, using: &generator),
            invincible: invincible
        )
    }
}

struct ReceiverPlayerOpsCommand: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let operation: String
    let state: PlayerTestState?

    static func query(sessionID: UUID, commandID: UUID = UUID()) -> Self {
        Self(version: ReceiverControlProtocol.version, type: "playerOps", sessionID: sessionID,
             commandID: commandID, operation: "query", state: nil)
    }

    static func apply(
        sessionID: UUID,
        state: PlayerTestState,
        commandID: UUID = UUID()
    ) -> Self {
        Self(version: ReceiverControlProtocol.version, type: "playerOps", sessionID: sessionID,
             commandID: commandID, operation: "apply", state: state.normalized)
    }

    static func restoreEnemies(sessionID: UUID, commandID: UUID = UUID()) -> Self {
        Self(version: ReceiverControlProtocol.version, type: "playerOps", sessionID: sessionID,
             commandID: commandID, operation: "restoreEnemies", state: nil)
    }
}

struct ReceiverPlayerOpsAcknowledgement: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let commandID: UUID
    let accepted: Bool
    let state: PlayerTestState?
    let enemiesRestored: Int?
    let failure: String?
}

struct ReceiverPlayerPoseCommand: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let sequence: UInt64
    let sceneName: String
    let heroX: Double
    let heroY: Double
    let heroZ: Double
    let velocityX: Double
    let velocityY: Double
    let facingRight: Bool
    let grounded: Bool

    init(sessionID: UUID, sequence: UInt64, sample: ReceiverGroundTruthSample) {
        version = ReceiverControlProtocol.version
        type = "playerPose"
        self.sessionID = sessionID
        self.sequence = sequence
        sceneName = sample.sceneName
        heroX = sample.heroX
        heroY = sample.heroY
        heroZ = sample.heroZ
        velocityX = sample.velocityX
        velocityY = sample.velocityY
        facingRight = sample.facingRight
        grounded = sample.grounded
    }
}

/// Development-only measurements emitted after the modified game's camera
/// update. They are an oracle for evaluating visual tracking, not an input the
/// shipping visual solver requires.
struct ReceiverGroundTruthSample: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let sequence: UInt64
    let unityFrame: Int64
    let unityRealtime: Double
    let sceneName: String
    let heroAvailable: Bool
    let heroX: Double
    let heroY: Double
    let heroZ: Double
    let velocityX: Double
    let velocityY: Double
    let facingRight: Bool
    let grounded: Bool
    let cameraAvailable: Bool
    let cameraX: Double
    let cameraY: Double
    let cameraZ: Double
    let cameraTargetX: Double
    let cameraTargetY: Double
    let cameraTargetZ: Double
    let orthographicSize: Double
    let pixelsPerWorldUnitX: Double?
    let pixelsPerWorldUnitY: Double?
    let heroScreenX: Double?
    let heroScreenY: Double?
    let projectionPixelWidth: Int?
    let projectionPixelHeight: Int?
    let screenWidth: Int
    let screenHeight: Int

    var hasFiniteCoordinates: Bool {
        let values = [
            unityRealtime, heroX, heroY, heroZ, velocityX, velocityY,
            cameraX, cameraY, cameraZ,
            cameraTargetX, cameraTargetY, cameraTargetZ, orthographicSize,
        ] + [
            pixelsPerWorldUnitX, pixelsPerWorldUnitY, heroScreenX, heroScreenY,
        ].compactMap { $0 }
        return valuesAreFinite(values)
    }

    private func valuesAreFinite(_ values: [Double]) -> Bool {
        values.allSatisfy { $0.isFinite && abs($0) <= 1_000_000 }
            && unityFrame >= 0 && orthographicSize >= 0
            && screenWidth >= 0 && screenHeight >= 0
            && (projectionPixelWidth ?? 0) >= 0 && (projectionPixelHeight ?? 0) >= 0
    }

    func pixelsPerWorldUnit(frameHeight: Int) -> Double? {
        guard cameraAvailable, frameHeight > 0 else { return nil }
        if let pixelsPerWorldUnitY, pixelsPerWorldUnitY > 0,
           let projectionPixelHeight, projectionPixelHeight > 0 {
            return pixelsPerWorldUnitY * Double(frameHeight) / Double(projectionPixelHeight)
        }
        guard orthographicSize > 0 else { return nil }
        return Double(frameHeight) / (2 * orthographicSize)
    }
}

private struct ReceiverWireEnvelope: Decodable {
    let type: String
}

/// Buttons are transmitted as a complete bitset.  A complete state is safe to
/// repeat and means packet loss can never turn a release into a held key.
struct InputBridgeButtons: OptionSet, Codable, Equatable, Hashable, Sendable {
    let rawValue: UInt16

    init(rawValue: UInt16) {
        self.rawValue = rawValue
    }

    static let left = Self(rawValue: 1 << 0)
    static let right = Self(rawValue: 1 << 1)
    static let down = Self(rawValue: 1 << 2)
    static let up = Self(rawValue: 1 << 3)
    static let actionA = Self(rawValue: 1 << 4)
    static let actionZ = Self(rawValue: 1 << 5)
    static let actionX = Self(rawValue: 1 << 6)
    static let inventory = Self(rawValue: 1 << 7)
    static let pauseMenu = Self(rawValue: 1 << 8)

    static let directional: Self = [.left, .right, .down, .up]
}

struct InputBridgeSnapshot: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let sequence: UInt64
    let enabled: Bool
    let heldButtons: InputBridgeButtons

    init(
        version: Int = InputBridgeProtocol.version,
        sessionID: UUID,
        sequence: UInt64,
        enabled: Bool,
        heldButtons: InputBridgeButtons
    ) {
        self.version = version
        self.type = "state"
        self.sessionID = sessionID
        self.sequence = sequence
        self.enabled = enabled
        self.heldButtons = heldButtons
    }
}

/// The receiver can use this acknowledgement for status and latency display.
struct InputBridgeAcknowledgement: Codable, Equatable, Sendable {
    let version: Int
    let type: String
    let sessionID: UUID
    let sequence: UInt64
    let appliedButtons: InputBridgeButtons
    let enabled: Bool

    init(
        version: Int = InputBridgeProtocol.version,
        sessionID: UUID,
        sequence: UInt64,
        appliedButtons: InputBridgeButtons,
        enabled: Bool
    ) {
        self.version = version
        self.type = "ack"
        self.sessionID = sessionID
        self.sequence = sequence
        self.appliedButtons = appliedButtons
        self.enabled = enabled
    }
}

/// Owns the sender's held state.  Every mutation produces an ordered complete
/// snapshot; callers can additionally call `heartbeat()` at 60 Hz.
struct InputBridgeSenderState {
    private(set) var sessionID: UUID
    private(set) var heldButtons: InputBridgeButtons = []
    private(set) var isEnabled = false
    private var nextSequence: UInt64 = 0

    init(sessionID: UUID = UUID()) {
        self.sessionID = sessionID
    }

    mutating func beginNewSession(_ sessionID: UUID = UUID()) -> InputBridgeSnapshot {
        self.sessionID = sessionID
        heldButtons = []
        isEnabled = false
        nextSequence = 0
        return makeSnapshot()
    }

    mutating func setEnabled(_ enabled: Bool) -> InputBridgeSnapshot {
        isEnabled = enabled
        if !enabled {
            heldButtons = []
        }
        return makeSnapshot()
    }

    /// Starts a newly focused control session with the first real press
    /// already held. This avoids an enabled-but-neutral tick before movement
    /// or an action reaches the game.
    mutating func beginFreshPress(_ button: InputBridgeButtons) -> InputBridgeSnapshot {
        isEnabled = true
        heldButtons = [button]
        return makeSnapshot()
    }

    mutating func setHeld(_ button: InputBridgeButtons, isHeld: Bool) -> InputBridgeSnapshot? {
        guard isEnabled else { return nil }
        let updated = isHeld ? heldButtons.union(button) : heldButtons.subtracting(button)
        guard updated != heldButtons else { return nil }
        heldButtons = updated
        return makeSnapshot()
    }

    /// Sends the latest complete state without changing it.
    mutating func heartbeat() -> InputBridgeSnapshot {
        makeSnapshot()
    }

    /// A focus loss is always represented by an explicit neutral state, even
    /// when no button was currently held.
    mutating func releaseAll() -> InputBridgeSnapshot {
        heldButtons = []
        isEnabled = false
        return makeSnapshot()
    }

    private mutating func makeSnapshot() -> InputBridgeSnapshot {
        defer { nextSequence &+= 1 }
        return InputBridgeSnapshot(
            sessionID: sessionID,
            sequence: nextSequence,
            enabled: isEnabled,
            heldButtons: isEnabled ? heldButtons : []
        )
    }
}

/// Tracks only state-changing snapshots that the receiver must acknowledge.
/// Heartbeats are deliberately excluded: they keep the receiver watchdog fed,
/// but cannot make the UI appear responsive when no input was applied.
struct InputAcknowledgementTracker {
    enum Result: Equatable {
        case matched(latency: TimeInterval)
        case ignoredIncompatible
        case ignoredOldSession
        case ignoredUnknownSequence
        case rejectedStateMismatch
    }

    private struct EffectiveState: Equatable {
        let enabled: Bool
        let buttons: InputBridgeButtons

        init(snapshot: InputBridgeSnapshot) {
            enabled = snapshot.enabled
            buttons = snapshot.enabled ? Self.normalized(snapshot.heldButtons) : []
        }

        private static func normalized(_ buttons: InputBridgeButtons) -> InputBridgeButtons {
            var result = buttons
            if result.contains(.left) && result.contains(.right) {
                result.subtract([.left, .right])
            }
            if result.contains(.up) && result.contains(.down) {
                result.subtract([.up, .down])
            }
            return result
        }
    }

    private struct Pending {
        let state: EffectiveState
        let sentAt: TimeInterval
    }

    private let maximumOutstanding: Int
    private let maximumLatencySamples: Int
    private(set) var sessionID: UUID?
    private var lastSentState: EffectiveState?
    private var pending = [UInt64: Pending]()
    private var pendingOrder = [UInt64]()
    private var latencies = [TimeInterval]()

    init(maximumOutstanding: Int = 64, maximumLatencySamples: Int = 120) {
        self.maximumOutstanding = max(1, maximumOutstanding)
        self.maximumLatencySamples = max(1, maximumLatencySamples)
    }

    var outstandingCount: Int { pending.count }

    var p95Latency: TimeInterval? {
        guard !latencies.isEmpty else { return nil }
        let ordered = latencies.sorted()
        let index = Int(ceil(Double(ordered.count) * 0.95)) - 1
        return ordered[max(0, min(index, ordered.count - 1))]
    }

    mutating func beginSession(_ sessionID: UUID) {
        self.sessionID = sessionID
        lastSentState = nil
        pending.removeAll(keepingCapacity: true)
        pendingOrder.removeAll(keepingCapacity: true)
        latencies.removeAll(keepingCapacity: true)
    }

    /// Returns true only when this snapshot represents an acknowledgement
    /// worth measuring. The initial neutral state and every enabled/held-state
    /// transition are included; repeated heartbeats are not.
    @discardableResult
    mutating func recordSent(_ snapshot: InputBridgeSnapshot, at sentAt: TimeInterval) -> Bool {
        if sessionID != snapshot.sessionID {
            beginSession(snapshot.sessionID)
        }
        let state = EffectiveState(snapshot: snapshot)
        guard state != lastSentState else { return false }
        lastSentState = state
        if pending[snapshot.sequence] != nil {
            pendingOrder.removeAll { $0 == snapshot.sequence }
        }
        pending[snapshot.sequence] = Pending(state: state, sentAt: sentAt)
        pendingOrder.append(snapshot.sequence)
        while pendingOrder.count > maximumOutstanding {
            pending.removeValue(forKey: pendingOrder.removeFirst())
        }
        return true
    }

    mutating func receive(_ acknowledgement: InputBridgeAcknowledgement, at receivedAt: TimeInterval) -> Result {
        guard acknowledgement.type == "ack", acknowledgement.version == InputBridgeProtocol.version else {
            return .ignoredIncompatible
        }
        guard acknowledgement.sessionID == sessionID else { return .ignoredOldSession }
        guard let expected = pending.removeValue(forKey: acknowledgement.sequence) else {
            return .ignoredUnknownSequence
        }
        pendingOrder.removeAll { $0 == acknowledgement.sequence }
        let applied = EffectiveState(
            snapshot: InputBridgeSnapshot(
                sessionID: acknowledgement.sessionID,
                sequence: acknowledgement.sequence,
                enabled: acknowledgement.enabled,
                heldButtons: acknowledgement.appliedButtons
            )
        )
        guard expected.state == applied else { return .rejectedStateMismatch }
        let latency = max(0, receivedAt - expected.sentAt)
        latencies.append(latency)
        if latencies.count > maximumLatencySamples { latencies.removeFirst(latencies.count - maximumLatencySamples) }
        return .matched(latency: latency)
    }
}

enum InputBridgeWireCodec {
    static func encode(_ snapshot: InputBridgeSnapshot) throws -> Data {
        var data = try JSONEncoder().encode(snapshot)
        data.append(0x0A)
        return data
    }

    static func decodeSnapshot(line: Data) throws -> InputBridgeSnapshot {
        try JSONDecoder().decode(InputBridgeSnapshot.self, from: line)
    }

    static func encode(_ acknowledgement: InputBridgeAcknowledgement) throws -> Data {
        var data = try JSONEncoder().encode(acknowledgement)
        data.append(0x0A)
        return data
    }

    static func encode(_ request: ReceiverCapabilityRequest) throws -> Data {
        try encodeControl(request)
    }

    static func encode(_ command: ReceiverPauseCommand) throws -> Data {
        try encodeControl(command)
    }

    static func encode(_ command: ReceiverPointerCommand) throws -> Data {
        try encodeControl(command)
    }

    static func encode(_ command: ReceiverCheckpointCommand) throws -> Data {
        try encodeControl(command)
    }

    static func encode(_ command: ReceiverPlayerPoseCommand) throws -> Data {
        try encodeControl(command)
    }

    static func encode(_ command: ReceiverPlayerOpsCommand) throws -> Data {
        try encodeControl(command)
    }

    static func messageType(line: Data) throws -> String {
        try JSONDecoder().decode(ReceiverWireEnvelope.self, from: line).type
    }

    static func decodeCapabilitiesAcknowledgement(line: Data) throws -> ReceiverCapabilitiesAcknowledgement {
        try JSONDecoder().decode(ReceiverCapabilitiesAcknowledgement.self, from: line)
    }

    static func decodePauseAcknowledgement(line: Data) throws -> ReceiverPauseAcknowledgement {
        try JSONDecoder().decode(ReceiverPauseAcknowledgement.self, from: line)
    }

    static func decodeCheckpointAcknowledgement(line: Data) throws -> ReceiverCheckpointAcknowledgement {
        try JSONDecoder().decode(ReceiverCheckpointAcknowledgement.self, from: line)
    }

    static func decodePlayerOpsAcknowledgement(line: Data) throws -> ReceiverPlayerOpsAcknowledgement {
        try JSONDecoder().decode(ReceiverPlayerOpsAcknowledgement.self, from: line)
    }

    static func decodeGroundTruth(line: Data) throws -> ReceiverGroundTruthSample {
        try JSONDecoder().decode(ReceiverGroundTruthSample.self, from: line)
    }

    private static func encodeControl<T: Encodable>(_ value: T) throws -> Data {
        var data = try JSONEncoder().encode(value)
        data.append(0x0A)
        return data
    }
}
