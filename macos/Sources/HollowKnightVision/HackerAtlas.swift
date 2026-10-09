import CoreGraphics
import CoreImage
import Foundation

struct HackerCapturedFrame: @unchecked Sendable {
    let unityFrame: Int
    let image: CGImage
    let capturedAt: TimeInterval
    let integratesAtlas: Bool
}

struct HackerMatchedFrame: @unchecked Sendable {
    let capture: HackerCapturedFrame
    let sample: ReceiverGroundTruthSample
    let sampleObservedAt: TimeInterval
}

/// Joins capture and mod telemetry by Unity's rendered frame number. It never
/// falls back to timestamps: a missing side means the frame is not atlas data.
final class HackerFrameSynchronizer: @unchecked Sendable {
    private struct SampleValue {
        let sample: ReceiverGroundTruthSample
        let observedAt: TimeInterval
    }

    private let lock = NSLock()
    private let capacity: Int
    private var captures = [Int: HackerCapturedFrame]()
    private var samples = [Int: SampleValue]()
    private var captureOrder = [Int]()
    private var sampleOrder = [Int]()

    init(capacity: Int = 180) { self.capacity = max(4, capacity) }

    static func key(for unityFrame: Int64) -> Int {
        Int(UInt64(bitPattern: unityFrame) & 0x00FF_FFFF)
    }

    func append(
        capture: HackerCapturedFrame
    ) -> HackerMatchedFrame? {
        lock.lock(); defer { lock.unlock() }
        if let value = samples.removeValue(forKey: capture.unityFrame) {
            sampleOrder.removeAll { $0 == capture.unityFrame }
            return HackerMatchedFrame(
                capture: capture,
                sample: value.sample,
                sampleObservedAt: value.observedAt
            )
        }
        if captures[capture.unityFrame] == nil { captureOrder.append(capture.unityFrame) }
        captures[capture.unityFrame] = capture
        trim(&captures, order: &captureOrder)
        return nil
    }

    func append(
        sample: ReceiverGroundTruthSample,
        observedAt: TimeInterval
    ) -> HackerMatchedFrame? {
        guard sample.cameraAvailable, sample.hasFiniteCoordinates else { return nil }
        let key = Self.key(for: sample.unityFrame)
        lock.lock(); defer { lock.unlock() }
        if let capture = captures.removeValue(forKey: key) {
            captureOrder.removeAll { $0 == key }
            return HackerMatchedFrame(
                capture: capture,
                sample: sample,
                sampleObservedAt: observedAt
            )
        }
        if samples[key] == nil { sampleOrder.append(key) }
        samples[key] = SampleValue(sample: sample, observedAt: observedAt)
        trim(&samples, order: &sampleOrder)
        return nil
    }

    func reset() {
        lock.lock(); defer { lock.unlock() }
        captures.removeAll(keepingCapacity: true)
        samples.removeAll(keepingCapacity: true)
        captureOrder.removeAll(keepingCapacity: true)
        sampleOrder.removeAll(keepingCapacity: true)
    }

    private func trim<Value>(_ values: inout [Int: Value], order: inout [Int]) {
        while order.count > capacity {
            values.removeValue(forKey: order.removeFirst())
        }
    }
}

struct HackerPreparedFrame: @unchecked Sendable {
    let liveImage: CGImage
    let liveBounds: CGRect
    let focusPoint: CGPoint
    let cameraTransform: GroundTruthCameraTransform
    let sample: ReceiverGroundTruthSample
    let anchorPosition: CGPoint
    let sceneName: String
    let unityFrame: Int
    let capturedAt: TimeInterval
    let integratesAtlas: Bool
    let roomPlacementReady: Bool
    let hudStencil: HUDStencilResult?
}

struct HackerAtlasOutput: @unchecked Sendable {
    let atlasTiles: [LiveAtlasOutput.AtlasLayer]
    let atlasBounds: CGRect?
    let rooms: [HackerAtlasRoom]
    let connections: [HackerRoomConnection]
    let sceneName: String
    let unityFrame: Int
    let roomCount: Int
    let knownScenes: [String]
}

enum HackerRoomSide: String, Sendable {
    case left, right, bottom, top

    var opposite: HackerRoomSide {
        switch self {
        case .left: .right
        case .right: .left
        case .bottom: .top
        case .top: .bottom
        }
    }

    var isHorizontal: Bool { self == .left || self == .right }
}

/// A doorway observed while moving between two playable scenes. Coordinates
/// are room-local, so the connector remains attached while rooms are laid out
/// automatically or dragged by hand.
struct HackerRoomConnection: Identifiable, Sendable {
    let id: UUID
    let fromRoomID: UUID
    let toRoomID: UUID
    let fromSide: HackerRoomSide
    let toSide: HackerRoomSide
    let fromCoordinate: CGFloat
    let toCoordinate: CGFloat
}

struct HackerRoomConnectorSegment: Identifiable, Sendable {
    let id: UUID
    let start: CGPoint
    let end: CGPoint
    let isFreeform: Bool
}

/// One independently movable Hacker room. Tile bounds are room-local while
/// `position` places the room in Hacker atlas space.
struct HackerAtlasRoom: Identifiable, @unchecked Sendable {
    let id: UUID
    let sceneName: String
    let position: CGPoint
    let bounds: CGRect
    let atlasTiles: [LiveAtlasOutput.AtlasLayer]

    var worldBounds: CGRect {
        bounds.offsetBy(dx: position.x, dy: position.y)
    }
}

struct HackerRoomLayout: Sendable {
    static let roomGap: CGFloat = 24
    static let empty = HackerRoomLayout(positions: [:], freeformConnectionIDs: [])

    let positions: [UUID: CGPoint]
    let freeformConnectionIDs: Set<UUID>

    /// Builds the largest possible rectilinear forest. Tree edges keep their
    /// doorways exactly aligned with a small gap. A conflicting loop or an
    /// overlap starts a fanned island, joined by a free-form cyan connector.
    static func make(
        rooms: [HackerAtlasRoom],
        connections: [HackerRoomConnection],
        gap: CGFloat = roomGap
    ) -> HackerRoomLayout {
        guard !rooms.isEmpty else { return .empty }
        let roomsByID = Dictionary(uniqueKeysWithValues: rooms.map { ($0.id, $0) })
        var positions = [UUID: CGPoint]()
        var freeform = Set<UUID>()
        var pending = connections

        func worldBounds(_ room: HackerAtlasRoom, at position: CGPoint) -> CGRect {
            room.bounds.offsetBy(dx: position.x, dy: position.y)
        }

        func endpoint(
            room: HackerAtlasRoom,
            position: CGPoint,
            side: HackerRoomSide,
            coordinate: CGFloat
        ) -> CGPoint {
            let bounds = worldBounds(room, at: position)
            if side.isHorizontal {
                return CGPoint(
                    x: side == .left ? bounds.minX : bounds.maxX,
                    y: position.y + min(max(coordinate, room.bounds.minY), room.bounds.maxY)
                )
            }
            return CGPoint(
                x: position.x + min(max(coordinate, room.bounds.minX), room.bounds.maxX),
                y: side == .bottom ? bounds.minY : bounds.maxY
            )
        }

        func childPosition(
            parent: HackerAtlasRoom,
            parentPosition: CGPoint,
            parentSide: HackerRoomSide,
            parentCoordinate: CGFloat,
            child: HackerAtlasRoom,
            childSide: HackerRoomSide,
            childCoordinate: CGFloat
        ) -> CGPoint {
            let parentBounds = worldBounds(parent, at: parentPosition)
            switch parentSide {
            case .right:
                return CGPoint(
                    x: parentBounds.maxX + gap - child.bounds.minX,
                    y: parentPosition.y + parentCoordinate - childCoordinate
                )
            case .left:
                return CGPoint(
                    x: parentBounds.minX - gap - child.bounds.maxX,
                    y: parentPosition.y + parentCoordinate - childCoordinate
                )
            case .top:
                return CGPoint(
                    x: parentPosition.x + parentCoordinate - childCoordinate,
                    y: parentBounds.maxY + gap - child.bounds.minY
                )
            case .bottom:
                return CGPoint(
                    x: parentPosition.x + parentCoordinate - childCoordinate,
                    y: parentBounds.minY - gap - child.bounds.maxY
                )
            }
        }

        func collides(_ room: HackerAtlasRoom, at candidate: CGPoint) -> Bool {
            let candidateBounds = worldBounds(room, at: candidate).insetBy(dx: -gap * 0.5, dy: -gap * 0.5)
            return positions.contains { id, position in
                guard id != room.id, let placedRoom = roomsByID[id] else { return false }
                return candidateBounds.intersects(
                    worldBounds(placedRoom, at: position).insetBy(dx: -gap * 0.5, dy: -gap * 0.5)
                )
            }
        }

        func fannedPosition(
            room: HackerAtlasRoom,
            candidate: CGPoint,
            connectionSide: HackerRoomSide
        ) -> CGPoint {
            guard collides(room, at: candidate) else { return candidate }
            let stride = connectionSide.isHorizontal
                ? max(64, room.bounds.height + gap)
                : max(64, room.bounds.width + gap)
            for distance in 1...max(8, rooms.count * 2) {
                for sign: CGFloat in [1, -1] {
                    var shifted = candidate
                    if connectionSide.isHorizontal {
                        shifted.y += CGFloat(distance) * stride * sign
                    } else {
                        shifted.x += CGFloat(distance) * stride * sign
                    }
                    if !collides(room, at: shifted) { return shifted }
                }
            }
            return candidate
        }

        func place(
            child childID: UUID,
            from parentID: UUID,
            parentSide: HackerRoomSide,
            childSide: HackerRoomSide,
            parentCoordinate: CGFloat,
            childCoordinate: CGFloat,
            connectionID: UUID
        ) {
            guard let parent = roomsByID[parentID], let child = roomsByID[childID],
                  let parentPosition = positions[parentID] else { return }
            let candidate = childPosition(
                parent: parent, parentPosition: parentPosition,
                parentSide: parentSide, parentCoordinate: parentCoordinate,
                child: child, childSide: childSide, childCoordinate: childCoordinate
            )
            let fanned = fannedPosition(
                room: child, candidate: candidate, connectionSide: parentSide
            )
            positions[childID] = fanned
            if fanned != candidate { freeform.insert(connectionID) }
        }

        while !pending.isEmpty {
            var progressed = false
            var remainder = [HackerRoomConnection]()
            for connection in pending {
                guard let fromRoom = roomsByID[connection.fromRoomID],
                      let toRoom = roomsByID[connection.toRoomID] else { continue }
                let fromPlaced = positions[connection.fromRoomID] != nil
                let toPlaced = positions[connection.toRoomID] != nil
                if !fromPlaced && !toPlaced {
                    if positions.isEmpty {
                        positions[fromRoom.id] = fromRoom.position
                    } else {
                        remainder.append(connection)
                        continue
                    }
                }
                if positions[connection.fromRoomID] != nil,
                   positions[connection.toRoomID] == nil {
                    place(
                        child: toRoom.id, from: fromRoom.id,
                        parentSide: connection.fromSide, childSide: connection.toSide,
                        parentCoordinate: connection.fromCoordinate,
                        childCoordinate: connection.toCoordinate,
                        connectionID: connection.id
                    )
                    progressed = true
                } else if positions[connection.toRoomID] != nil,
                          positions[connection.fromRoomID] == nil {
                    place(
                        child: fromRoom.id, from: toRoom.id,
                        parentSide: connection.toSide, childSide: connection.fromSide,
                        parentCoordinate: connection.toCoordinate,
                        childCoordinate: connection.fromCoordinate,
                        connectionID: connection.id
                    )
                    progressed = true
                } else if let fromPosition = positions[fromRoom.id],
                          let toPosition = positions[toRoom.id] {
                    let fromPoint = endpoint(
                        room: fromRoom, position: fromPosition,
                        side: connection.fromSide, coordinate: connection.fromCoordinate
                    )
                    let toPoint = endpoint(
                        room: toRoom, position: toPosition,
                        side: connection.toSide, coordinate: connection.toCoordinate
                    )
                    let aligned = connection.fromSide.isHorizontal
                        ? abs(fromPoint.y - toPoint.y) < 0.5
                        : abs(fromPoint.x - toPoint.x) < 0.5
                    if !aligned { freeform.insert(connection.id) }
                    progressed = true
                }
            }
            pending = remainder
            if !progressed, let disconnected = pending.first,
               let room = roomsByID[disconnected.fromRoomID] {
                positions[room.id] = room.position
            }
        }

        for room in rooms where positions[room.id] == nil {
            positions[room.id] = room.position
        }
        return HackerRoomLayout(
            positions: positions,
            freeformConnectionIDs: freeform
        )
    }
}

enum HackerAtlasCameraPlacement {
    static func cameraPosition(_ transform: GroundTruthCameraTransform) -> CGPoint {
        CGPoint(
            x: transform.cameraWorld.x * transform.pixelsPerWorldUnitX,
            y: transform.cameraWorld.y * transform.pixelsPerWorldUnitY
        )
    }

    static func liveBounds(
        image: CGImage,
        position: CGPoint,
        anchor: CGPoint
    ) -> CGRect {
        CGRect(
            x: position.x - anchor.x,
            y: position.y - anchor.y,
            width: CGFloat(image.width),
            height: CGFloat(image.height)
        ).integral
    }
}

/// Prepares every exact capture/telemetry match for immediate presentation.
/// This remains independent from the slower atlas compositor so atlas work can
/// never limit the live Hacker frame rate.
final class HackerFramePreparer: @unchecked Sendable {
    private struct PlayableReference {
        let liveBounds: CGRect
        let heroScreenPoint: CGPoint?
        let velocity: CGVector
        let facingRight: Bool
    }

    private let context: CIContext
    private let hudStencilTracker = HUDStencilTracker()
    private var anchorPositions = [String: CGPoint]()
    private var lastPlayableReference: PlayableReference?
    private var hudGeneration: UInt64 = 1

    init(context: CIContext = CIContext(options: [.cacheIntermediates: false])) {
        self.context = context
    }

    func reset() {
        anchorPositions.removeAll(keepingCapacity: true)
        lastPlayableReference = nil
        hudGeneration &+= 1
        hudStencilTracker.reset()
    }

    func process(_ match: HackerMatchedFrame) -> HackerPreparedFrame? {
        let image = match.capture.image
        guard let transform = GroundTruthCameraTransform(
            sample: match.sample,
            observedAt: match.sampleObservedAt,
            frameSize: CGSize(width: image.width, height: image.height)
        ) else { return nil }

        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let marker = markerBounds(in: extent)
        guard let cleanedFrame = frameReplacingMarker(image, markerBounds: marker) else {
            return nil
        }
        let hudStencil = hudStencilTracker.observe(
            cleanedFrame,
            gameplay: match.sample.heroAvailable,
            timestamp: match.capture.capturedAt,
            generation: hudGeneration
        )

        let position = HackerAtlasCameraPlacement.cameraPosition(transform)
        let heroScreenPoint = Self.heroScreenPoint(
            sample: match.sample,
            frameSize: extent.size
        )
        let suppliesHeroProjection = Self.suppliesHeroProjection(match.sample)
        let canInitializeRoom = match.sample.heroAvailable
            && (heroScreenPoint != nil || !suppliesHeroProjection)
        let anchor: CGPoint
        let roomPlacementReady: Bool
        if let knownAnchor = anchorPositions[transform.sceneName] {
            anchor = knownAnchor
            roomPlacementReady = true
        } else if canInitializeRoom {
            let origin = transitionOrigin(
                imageSize: extent.size,
                heroScreenPoint: heroScreenPoint,
                previous: lastPlayableReference
            )
            anchor = CGPoint(x: position.x - origin.x, y: position.y - origin.y)
            anchorPositions[transform.sceneName] = anchor
            roomPlacementReady = true
        } else if let previous = lastPlayableReference {
            // Transient cutscene scenes do not own atlas space. Keep their live
            // frame at the doorway instead of flashing back to world origin.
            anchor = CGPoint(
                x: position.x - previous.liveBounds.minX,
                y: position.y - previous.liveBounds.minY
            )
            roomPlacementReady = false
        } else {
            anchor = position
            roomPlacementReady = false
        }
        let liveBounds = HackerAtlasCameraPlacement.liveBounds(
            image: image,
            position: position,
            anchor: anchor
        )
        if roomPlacementReady && match.sample.heroAvailable
            && (heroScreenPoint != nil || !suppliesHeroProjection) {
            lastPlayableReference = PlayableReference(
                liveBounds: liveBounds,
                heroScreenPoint: heroScreenPoint,
                velocity: CGVector(
                    dx: match.sample.velocityX,
                    dy: match.sample.velocityY
                ),
                facingRight: match.sample.facingRight
            )
        }
        return HackerPreparedFrame(
            liveImage: cleanedFrame,
            liveBounds: liveBounds,
            focusPoint: CGPoint(x: liveBounds.midX, y: liveBounds.midY),
            cameraTransform: transform,
            sample: match.sample,
            anchorPosition: anchor,
            sceneName: transform.sceneName,
            unityFrame: match.capture.unityFrame,
            capturedAt: match.capture.capturedAt,
            integratesAtlas: match.capture.integratesAtlas,
            roomPlacementReady: roomPlacementReady,
            hudStencil: hudStencil
        )
    }

    private func transitionOrigin(
        imageSize: CGSize,
        heroScreenPoint: CGPoint?,
        previous: PlayableReference?
    ) -> CGPoint {
        guard let previous else { return .zero }
        if let oldHero = previous.heroScreenPoint, let newHero = heroScreenPoint {
            // The Knight crosses the same doorway point. Aligning that point
            // connects arbitrary scene-local camera coordinate systems.
            return CGPoint(
                x: previous.liveBounds.minX + oldHero.x - newHero.x,
                y: previous.liveBounds.minY + oldHero.y - newHero.y
            )
        }

        let overlapX = min(32, (imageSize.width * 0.1).rounded())
        let overlapY = min(24, (imageSize.height * 0.1).rounded())
        if abs(previous.velocity.dy) > abs(previous.velocity.dx),
           abs(previous.velocity.dy) > 0.05 {
            return CGPoint(
                x: previous.liveBounds.minX,
                y: previous.velocity.dy > 0
                    ? previous.liveBounds.maxY - overlapY
                    : previous.liveBounds.minY - imageSize.height + overlapY
            )
        }
        let exitsRight = abs(previous.velocity.dx) > 0.05
            ? previous.velocity.dx > 0 : previous.facingRight
        return CGPoint(
            x: exitsRight
                ? previous.liveBounds.maxX - overlapX
                : previous.liveBounds.minX - imageSize.width + overlapX,
            y: previous.liveBounds.minY
        )
    }

    private static func heroScreenPoint(
        sample: ReceiverGroundTruthSample,
        frameSize: CGSize
    ) -> CGPoint? {
        guard sample.heroAvailable,
              let screenX = sample.heroScreenX,
              let screenY = sample.heroScreenY,
              let projectionWidth = sample.projectionPixelWidth,
              let projectionHeight = sample.projectionPixelHeight,
              projectionWidth > 0, projectionHeight > 0 else { return nil }
        let point = CGPoint(
            x: CGFloat(screenX) * frameSize.width / CGFloat(projectionWidth),
            y: CGFloat(screenY) * frameSize.height / CGFloat(projectionHeight)
        )
        guard point.x >= 0, point.x <= frameSize.width,
              point.y >= 0, point.y <= frameSize.height else { return nil }
        return point
    }

    private static func suppliesHeroProjection(_ sample: ReceiverGroundTruthSample) -> Bool {
        sample.heroScreenX != nil && sample.heroScreenY != nil
            && (sample.projectionPixelWidth ?? 0) > 0
            && (sample.projectionPixelHeight ?? 0) > 0
    }

    private func markerBounds(in extent: CGRect) -> CGRect {
        CGRect(
            x: extent.minX,
            y: extent.maxY - extent.height * 6 / 360,
            width: extent.width * 120 / 640,
            height: extent.height * 6 / 360
        ).integral.intersection(extent)
    }

    /// Opaque replacement lets a newer exact frame erase any marker already
    /// visible through an overlapping atlas observation. Transparent masking
    /// would leave that older strip intact.
    private func frameReplacingMarker(
        _ image: CGImage,
        markerBounds: CGRect
    ) -> CGImage? {
        guard !markerBounds.isEmpty else { return image }
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let sourceBounds = markerBounds.offsetBy(dx: 0, dy: -markerBounds.height)
            .intersection(extent)
        guard sourceBounds.width == markerBounds.width,
              sourceBounds.height == markerBounds.height else { return image }
        let source = CIImage(cgImage: image)
        let replacement = source.cropped(to: sourceBounds).transformed(
            by: CGAffineTransform(translationX: 0, y: markerBounds.height)
        )
        let cleaned = replacement.composited(over: source).cropped(to: extent)
        return context.createCGImage(cleaned, from: extent)
    }

}

/// A direct-camera atlas with storage and origin wholly separate from Gameplay.
/// It sees every prepared frame so it can detect fades, but rasterizes only
/// cadence frames and fade recovery references. Live presentation never waits
/// for its masking or tile rasterization.
final class HackerAtlasPipeline: @unchecked Sendable {
    private struct FadeReference {
        let prepared: HackerPreparedFrame
        let profile: FrameSignalProfile
        let cameraPosition: CGPoint
    }

    private final class RoomState {
        let id = UUID()
        let sceneName: String
        let position: CGPoint
        let composer: LiveTiledAtlas
        var fadeReference: FadeReference?
        var lastIntegratedUnityFrame: Int?

        init(sceneName: String, position: CGPoint, context: CIContext) {
            self.sceneName = sceneName
            self.position = position
            composer = LiveTiledAtlas(
                context: context,
                anchorPosition: .zero,
                sourceCacheLimit: 48,
                pixelPolicy: .newestQuilted
            )
        }
    }

    private let context: CIContext
    private var rooms = [String: RoomState]()
    private var connections = [HackerRoomConnection]()
    private var lastPlayableFrame: HackerPreparedFrame?
    private var nextObservationID = 0
    private var revision: UInt64 = 0

    init(context: CIContext = CIContext(options: [.cacheIntermediates: false])) {
        self.context = context
    }

    func reset() {
        rooms.removeAll(keepingCapacity: true)
        connections.removeAll(keepingCapacity: true)
        lastPlayableFrame = nil
        nextObservationID = 0
        revision &+= 1
    }

    func process(_ prepared: HackerPreparedFrame) -> HackerAtlasOutput? {
        let profile = FrameRegionRenderer.signalProfile(in: prepared.liveImage)
        let position = HackerAtlasCameraPlacement.cameraPosition(
            prepared.cameraTransform
        )
        let state: RoomState
        if let existing = rooms[prepared.sceneName] {
            state = existing
        } else {
            // A cutscene may expose a camera and scene name without a playable
            // room. Do not turn that transient scene into atlas ownership.
            guard prepared.roomPlacementReady,
                  prepared.sample.heroAvailable,
                  profile.hasGameplaySignal else {
                return nil
            }
            let created = RoomState(
                sceneName: prepared.sceneName,
                position: prepared.liveBounds.origin,
                context: context
            )
            rooms[prepared.sceneName] = created
            state = created
        }

        if isFadeOrCutscene(
            prepared,
            profile: profile,
            cameraPosition: position,
            reference: state.fadeReference
        ) {
            // A cadenced insert can occur partway through a fade. Reinsert the
            // brightest nearby pre-fade frame once so dim/black observations
            // cannot remain painted over the room edge.
            guard let reference = state.fadeReference,
                  state.lastIntegratedUnityFrame != reference.prepared.unityFrame
            else { return nil }
            return integrate(reference.prepared, into: state)
        }

        if let previous = lastPlayableFrame,
           previous.sceneName != prepared.sceneName,
           let previousState = rooms[previous.sceneName] {
            previousState.composer.endTemporalWindow()
            state.composer.endTemporalWindow()
            recordConnection(
                from: previous, room: previousState,
                to: prepared, room: state
            )
        }
        lastPlayableFrame = prepared

        updateFadeReference(
            prepared,
            profile: profile,
            cameraPosition: position,
            state: state
        )
        guard prepared.integratesAtlas else { return nil }
        return integrate(prepared, into: state)
    }

    private func integrate(
        _ prepared: HackerPreparedFrame,
        into state: RoomState
    ) -> HackerAtlasOutput? {
        let image = prepared.liveImage
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let hero = heroBounds(sample: prepared.sample, frameSize: extent.size)
        let fallback = SceneRegions.hud(in: extent, knight: hero)
        let regions = SceneRegions(
            knight: hero,
            health: prepared.hudStencil?.healthBounds ?? fallback.health,
            geo: prepared.hudStencil?.geo ?? fallback.geo,
            mana: prepared.hudStencil?.manaBounds ?? fallback.mana,
            hudStencilRects: SceneRegions.atlasHUDMaskRects(
                in: extent,
                hudStencil: prepared.hudStencil
            )
        )
        guard let atlasFrame = FrameRegionRenderer.mapFrame(
            from: image,
            omitting: regions,
            context: context
        ) else { return nil }

        let observationID = nextObservationID
        nextObservationID += 1
        let localPosition = CGPoint(
            x: prepared.liveBounds.minX - state.position.x,
            y: prepared.liveBounds.minY - state.position.y
        )
        guard state.composer.insert(
            observationID: observationID,
            maskedImage: atlasFrame,
            solveWidth: CGFloat(image.width),
            cameraPosition: localPosition,
            captureIdentity: Int64(prepared.unityFrame),
            timestamp: prepared.capturedAt
        ) else { return nil }
        state.lastIntegratedUnityFrame = prepared.unityFrame
        revision &+= 1
        let roomSnapshots = rooms.values
            .sorted { $0.sceneName < $1.sceneName }
            .map { room -> HackerAtlasRoom in
                let snapshot = room.composer.snapshot
                let layers = snapshot.tiles.map {
                    LiveAtlasOutput.AtlasLayer(
                        id: $0.id,
                        image: $0.image,
                        bounds: $0.worldBounds,
                        revision: revision,
                        contentBounds: snapshot.contentBounds
                    )
                }
                return HackerAtlasRoom(
                    id: room.id,
                    sceneName: room.sceneName,
                    position: room.position,
                    bounds: snapshot.contentBounds,
                    atlasTiles: layers
                )
            }
        let atlasBounds = roomSnapshots.reduce(CGRect.null) {
            $0.union($1.worldBounds)
        }
        var flattened = [LiveAtlasOutput.AtlasLayer]()
        for room in roomSnapshots {
            for tile in room.atlasTiles {
                flattened.append(LiveAtlasOutput.AtlasLayer(
                    id: flattened.count,
                    image: tile.image,
                    bounds: tile.bounds.offsetBy(
                        dx: room.position.x,
                        dy: room.position.y
                    ),
                    revision: revision,
                    contentBounds: atlasBounds.isNull ? nil : atlasBounds
                ))
            }
        }
        return HackerAtlasOutput(
            atlasTiles: flattened,
            atlasBounds: atlasBounds.isNull || atlasBounds.isEmpty ? nil : atlasBounds,
            rooms: roomSnapshots,
            connections: connections,
            sceneName: prepared.sceneName,
            unityFrame: prepared.unityFrame,
            roomCount: rooms.count,
            knownScenes: rooms.keys.sorted()
        )
    }

    private func recordConnection(
        from previous: HackerPreparedFrame,
        room previousRoom: RoomState,
        to current: HackerPreparedFrame,
        room currentRoom: RoomState
    ) {
        let fromSide = transitionSide(from: previous, to: current)
        let toSide = fromSide.opposite
        let duplicate = connections.contains { connection in
            (connection.fromRoomID == previousRoom.id
                && connection.toRoomID == currentRoom.id
                && connection.fromSide == fromSide
                && connection.toSide == toSide)
            || (connection.fromRoomID == currentRoom.id
                && connection.toRoomID == previousRoom.id
                && connection.fromSide == toSide
                && connection.toSide == fromSide)
        }
        guard !duplicate else { return }
        connections.append(HackerRoomConnection(
            id: UUID(),
            fromRoomID: previousRoom.id,
            toRoomID: currentRoom.id,
            fromSide: fromSide,
            toSide: toSide,
            fromCoordinate: doorwayCoordinate(
                in: previous, room: previousRoom, side: fromSide
            ),
            toCoordinate: doorwayCoordinate(
                in: current, room: currentRoom, side: toSide
            )
        ))
    }

    private func transitionSide(
        from previous: HackerPreparedFrame,
        to current: HackerPreparedFrame
    ) -> HackerRoomSide {
        if let hero = heroScreenPoint(
            sample: previous.sample,
            frameSize: previous.liveBounds.size
        ) {
            let edgeDistances: [(HackerRoomSide, CGFloat)] = [
                (.left, hero.x),
                (.right, previous.liveBounds.width - hero.x),
                (.bottom, hero.y),
                (.top, previous.liveBounds.height - hero.y),
            ]
            if let nearest = edgeDistances.min(by: { $0.1 < $1.1 }),
               nearest.1 <= min(previous.liveBounds.width, previous.liveBounds.height) * 0.35 {
                return nearest.0
            }
        }

        let delta = CGPoint(
            x: current.liveBounds.midX - previous.liveBounds.midX,
            y: current.liveBounds.midY - previous.liveBounds.midY
        )
        if abs(delta.x) >= abs(delta.y), abs(delta.x) > 1 {
            return delta.x >= 0 ? .right : .left
        }
        if abs(delta.y) > 1 { return delta.y >= 0 ? .top : .bottom }
        if abs(previous.sample.velocityY) > abs(previous.sample.velocityX),
           abs(previous.sample.velocityY) > 0.05 {
            return previous.sample.velocityY >= 0 ? .top : .bottom
        }
        if abs(previous.sample.velocityX) > 0.05 {
            return previous.sample.velocityX >= 0 ? .right : .left
        }
        return previous.sample.facingRight ? .right : .left
    }

    private func doorwayCoordinate(
        in prepared: HackerPreparedFrame,
        room: RoomState,
        side: HackerRoomSide
    ) -> CGFloat {
        let hero = heroScreenPoint(
            sample: prepared.sample,
            frameSize: prepared.liveBounds.size
        ) ?? CGPoint(x: prepared.liveBounds.width * 0.5, y: prepared.liveBounds.height * 0.5)
        return side.isHorizontal
            ? prepared.liveBounds.minY + hero.y - room.position.y
            : prepared.liveBounds.minX + hero.x - room.position.x
    }

    private func heroScreenPoint(
        sample: ReceiverGroundTruthSample,
        frameSize: CGSize
    ) -> CGPoint? {
        guard sample.heroAvailable,
              let screenX = sample.heroScreenX,
              let screenY = sample.heroScreenY,
              let projectionWidth = sample.projectionPixelWidth,
              let projectionHeight = sample.projectionPixelHeight,
              projectionWidth > 0, projectionHeight > 0 else { return nil }
        return CGPoint(
            x: CGFloat(screenX) * frameSize.width / CGFloat(projectionWidth),
            y: CGFloat(screenY) * frameSize.height / CGFloat(projectionHeight)
        )
    }

    private func isFadeOrCutscene(
        _ prepared: HackerPreparedFrame,
        profile: FrameSignalProfile,
        cameraPosition: CGPoint,
        reference: FadeReference?
    ) -> Bool {
        guard prepared.sample.heroAvailable, profile.hasGameplaySignal else {
            return true
        }
        guard let reference else { return false }
        let distance = hypot(
            cameraPosition.x - reference.cameraPosition.x,
            cameraPosition.y - reference.cameraPosition.y
        )
        let nearbyLimit = max(24, CGFloat(prepared.liveImage.width) * 0.12)
        guard distance <= nearbyLimit else { return false }
        return profile.meanPeak < max(6, reference.profile.meanPeak * 0.62)
            && profile.visibleFraction < reference.profile.visibleFraction * 0.75
    }

    private func updateFadeReference(
        _ prepared: HackerPreparedFrame,
        profile: FrameSignalProfile,
        cameraPosition: CGPoint,
        state: RoomState
    ) {
        guard let current = state.fadeReference else {
            state.fadeReference = FadeReference(
                prepared: prepared,
                profile: profile,
                cameraPosition: cameraPosition
            )
            return
        }
        let distance = hypot(
            cameraPosition.x - current.cameraPosition.x,
            cameraPosition.y - current.cameraPosition.y
        )
        let nearbyLimit = max(24, CGFloat(prepared.liveImage.width) * 0.12)
        if distance > nearbyLimit
            || profile.meanPeak > current.profile.meanPeak * 1.02
            || profile.visibleFraction > current.profile.visibleFraction * 1.05 {
            state.fadeReference = FadeReference(
                prepared: prepared,
                profile: profile,
                cameraPosition: cameraPosition
            )
        }
    }

    private func heroBounds(
        sample: ReceiverGroundTruthSample,
        frameSize: CGSize
    ) -> CGRect? {
        guard sample.heroAvailable,
              let screenX = sample.heroScreenX,
              let screenY = sample.heroScreenY,
              let projectionWidth = sample.projectionPixelWidth,
              let projectionHeight = sample.projectionPixelHeight,
              projectionWidth > 0, projectionHeight > 0 else { return nil }
        let scaleX = frameSize.width / CGFloat(projectionWidth)
        let scaleY = frameSize.height / CGFloat(projectionHeight)
        let center = CGPoint(x: CGFloat(screenX) * scaleX, y: CGFloat(screenY) * scaleY)
        let width = 96 * frameSize.width / 640
        let height = 128 * frameSize.height / 360
        return CGRect(
            x: center.x - width * 0.5,
            y: center.y - height * 0.42,
            width: width,
            height: height
        )
    }
}
