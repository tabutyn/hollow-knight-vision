import CoreGraphics
import Foundation

enum LiveWorldModelError: Error, Equatable {
    case unsupportedSchema(Int)
    case invalidRevision
    case staleRevision(expected: Int, actual: Int)
    case duplicateObservationID(Int)
    case duplicateKeyframeID(Int)
    case duplicateLandmarkID(Int)
    case duplicateRelativeEdgeID(Int)
    case duplicateLoopEdgeID(Int)
    case danglingObservationID(Int)
    case danglingKeyframeID(Int)
    case danglingLandmarkID(Int)
    case invalidGeometry
    case unanchoredWorld
    case invalidIslandMembership
    case nonRigidIslandCorrection(Int)
    case invalidDoorwayFloorConstraint
}

/// Immutable capture evidence and its two solve-unit poses. `sourceObservationID`
/// names the separately persisted masked source supplied by `SceneSessionStore`.
struct LiveWorldObservation: Codable, Equatable, Identifiable {
    let id: Int
    let sourceObservationID: Int
    let timestamp: Double
    /// Exact image presentation uptime, independent of wall-clock/worker delay.
    /// Absent in older snapshots; never infer an exact capture from receipt time.
    let captureTimestamp: Double?
    let sourceWidth: Int
    let sourceHeight: Int
    let solveWidth: Double
    let sampleWidth: Int
    let excludedRects: [CGRect]
    /// Capture-local odometry coordinates. These remain stable when the
    /// world-from-local basis is corrected by a loop closure.
    let localPose: CGPoint
    let captureGeneration: UInt64
    let localEpoch: UInt64
    let basisRevision: UInt64
    /// Stable visual-room ownership. Zero decodes legacy one-room sessions.
    let roomID: Int
    let rawPose: CGPoint
    let optimizedPose: CGPoint
    let groundGeometry: LiveWorldGroundGeometry?
    let observedFloorCandidates: LiveObservedFloorCandidates?
    /// Detected player bounds in capture-local solve pixels. Optional evidence;
    /// absence never blocks tracking, but cannot prove a grounded traversal.
    let observedPlayerBounds: CGRect?

    init(
        id: Int,
        sourceObservationID: Int,
        timestamp: Double,
        captureTimestamp: Double? = nil,
        sourceWidth: Int,
        sourceHeight: Int,
        solveWidth: Double,
        sampleWidth: Int,
        excludedRects: [CGRect],
        localPose: CGPoint? = nil,
        captureGeneration: UInt64 = 0,
        localEpoch: UInt64 = 0,
        basisRevision: UInt64 = 0,
        roomID: Int = 0,
        rawPose: CGPoint,
        optimizedPose: CGPoint,
        groundGeometry: LiveWorldGroundGeometry? = nil,
        observedFloorCandidates: LiveObservedFloorCandidates? = nil,
        observedPlayerBounds: CGRect? = nil
    ) {
        self.id = id
        self.sourceObservationID = sourceObservationID
        self.timestamp = timestamp
        self.captureTimestamp = captureTimestamp
        self.sourceWidth = sourceWidth
        self.sourceHeight = sourceHeight
        self.solveWidth = solveWidth
        self.sampleWidth = sampleWidth
        self.excludedRects = excludedRects
        self.localPose = localPose ?? rawPose
        self.captureGeneration = captureGeneration
        self.localEpoch = localEpoch
        self.basisRevision = basisRevision
        self.roomID = roomID
        self.rawPose = rawPose
        self.optimizedPose = optimizedPose
        self.groundGeometry = groundGeometry
        self.observedFloorCandidates = observedFloorCandidates
        self.observedPlayerBounds = observedPlayerBounds
    }

    private enum CodingKeys: String, CodingKey {
        case id, sourceObservationID, timestamp, captureTimestamp, sourceWidth, sourceHeight, solveWidth
        case sampleWidth, excludedRects, localPose, captureGeneration, localEpoch, basisRevision
        case roomID, rawPose, optimizedPose, groundGeometry, observedFloorCandidates, observedPlayerBounds
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let rawPose = try container.decode(CGPoint.self, forKey: .rawPose)
        self.init(
            id: try container.decode(Int.self, forKey: .id),
            sourceObservationID: try container.decode(Int.self, forKey: .sourceObservationID),
            timestamp: try container.decode(Double.self, forKey: .timestamp),
            captureTimestamp: try container.decodeIfPresent(Double.self, forKey: .captureTimestamp),
            sourceWidth: try container.decode(Int.self, forKey: .sourceWidth),
            sourceHeight: try container.decode(Int.self, forKey: .sourceHeight),
            solveWidth: try container.decode(Double.self, forKey: .solveWidth),
            sampleWidth: try container.decode(Int.self, forKey: .sampleWidth),
            excludedRects: try container.decode([CGRect].self, forKey: .excludedRects),
            localPose: try container.decodeIfPresent(CGPoint.self, forKey: .localPose),
            captureGeneration: try container.decodeIfPresent(UInt64.self, forKey: .captureGeneration) ?? 0,
            localEpoch: try container.decodeIfPresent(UInt64.self, forKey: .localEpoch) ?? 0,
            basisRevision: try container.decodeIfPresent(UInt64.self, forKey: .basisRevision) ?? 0,
            roomID: try container.decodeIfPresent(Int.self, forKey: .roomID) ?? 0,
            rawPose: rawPose,
            optimizedPose: try container.decode(CGPoint.self, forKey: .optimizedPose),
            groundGeometry: try container.decodeIfPresent(LiveWorldGroundGeometry.self, forKey: .groundGeometry),
            observedFloorCandidates: try container.decodeIfPresent(LiveObservedFloorCandidates.self, forKey: .observedFloorCandidates),
            observedPlayerBounds: try container.decodeIfPresent(CGRect.self, forKey: .observedPlayerBounds)
        )
    }
}

struct LiveWorldKeyframe: Codable, Equatable, Identifiable {
    let id: Int
    let observationID: Int
    let sourceObservationID: Int
}

struct LiveWorldLandmark: Codable, Equatable, Identifiable {
    let id: Int
    let keyframeID: Int
    let sourceObservationID: Int
    let samplePoint: CGPoint
    let worldPoint: CGPoint
    let descriptor: [Float]
    let depth: Double
    let depthConfidence: Double
}

struct LiveWorldRelativeMotionEdge: Codable, Equatable, Identifiable {
    let id: Int
    let fromObservationID: Int
    let toObservationID: Int
    let deltaX: Double
    let deltaY: Double
    let support: Int
}

struct LiveWorldLoopClosureEdge: Codable, Equatable, Identifiable {
    let id: Int
    let fromKeyframeID: Int
    let toKeyframeID: Int
    let landmarkIDs: [Int]
    let deltaX: Double
    let deltaY: Double
    let support: Int
    let ambiguity: Double
    /// Mean geometric fit residual converted from matcher sample pixels.
    var residualPixels: Double? = nil
}

/// Versioned, one-room input/output for global pose optimization. It contains no
/// image bytes: source IDs refer to immutable masked captures owned by the session store.
struct LiveWorldSnapshot: Codable, Equatable {
    static let currentSchemaVersion = 3

    let schemaVersion: Int
    let mapRevision: Int
    let anchoredObservationID: Int?
    let observations: [LiveWorldObservation]
    let keyframes: [LiveWorldKeyframe]
    let landmarks: [LiveWorldLandmark]
    let relativeMotionEdges: [LiveWorldRelativeMotionEdge]
    let loopClosureEdges: [LiveWorldLoopClosureEdge]
    let trackingIslands: [LiveTrackingIsland]
    let doorwayFloorConstraints: [LiveDoorwayFloorConstraint]

    init(
        schemaVersion: Int = LiveWorldSnapshot.currentSchemaVersion,
        mapRevision: Int = 0,
        anchoredObservationID: Int? = nil,
        observations: [LiveWorldObservation] = [],
        keyframes: [LiveWorldKeyframe] = [],
        landmarks: [LiveWorldLandmark] = [],
        relativeMotionEdges: [LiveWorldRelativeMotionEdge] = [],
        loopClosureEdges: [LiveWorldLoopClosureEdge] = [],
        trackingIslands: [LiveTrackingIsland]? = nil,
        doorwayFloorConstraints: [LiveDoorwayFloorConstraint] = []
    ) throws {
        guard (1...Self.currentSchemaVersion).contains(schemaVersion) else {
            throw LiveWorldModelError.unsupportedSchema(schemaVersion)
        }
        self.schemaVersion = Self.currentSchemaVersion
        self.mapRevision = mapRevision
        self.anchoredObservationID = anchoredObservationID
        self.observations = observations
        self.keyframes = keyframes
        self.landmarks = landmarks
        self.relativeMotionEdges = relativeMotionEdges
        self.loopClosureEdges = loopClosureEdges
        self.trackingIslands = schemaVersion >= 2
            ? trackingIslands ?? observations.map(LiveTrackingIsland.legacy)
            : observations.map(LiveTrackingIsland.legacy)
        self.doorwayFloorConstraints = schemaVersion >= 3 ? doorwayFloorConstraints : []
        try validate()
    }

    func replacingOptimizedPoses(
        _ poses: [Int: CGPoint],
        baseRevision: Int
    ) throws -> LiveWorldSnapshot {
        guard baseRevision == mapRevision else {
            throw LiveWorldModelError.staleRevision(expected: mapRevision, actual: baseRevision)
        }
        guard mapRevision < Int.max, Set(poses.keys) == Set(observations.map(\.id)),
              poses.values.allSatisfy(Self.isFinite) else {
            throw LiveWorldModelError.invalidRevision
        }
        // Each island has exactly one world translation. A correction cannot
        // independently bend its screenshots while claiming reliable geometry.
        let movedIslands = try trackingIslands.map { island -> LiveTrackingIsland in
            guard let first = island.members.first, let firstPose = poses[first.observationID] else {
                throw LiveWorldModelError.invalidIslandMembership
            }
            let translation = CGPoint(x: firstPose.x - first.localPose.x,
                                      y: firstPose.y - first.localPose.y)
            for member in island.members {
                guard let pose = poses[member.observationID],
                      hypot(pose.x - member.localPose.x - translation.x,
                            pose.y - member.localPose.y - translation.y) <= 0.000001 else {
                    throw LiveWorldModelError.nonRigidIslandCorrection(island.id)
                }
            }
            return island.replacingTranslation(translation)
        }
        let replaced = observations.map { observation in
            LiveWorldObservation(
                id: observation.id,
                sourceObservationID: observation.sourceObservationID,
                timestamp: observation.timestamp,
                captureTimestamp: observation.captureTimestamp,
                sourceWidth: observation.sourceWidth,
                sourceHeight: observation.sourceHeight,
                solveWidth: observation.solveWidth,
                sampleWidth: observation.sampleWidth,
                excludedRects: observation.excludedRects,
                localPose: observation.localPose,
                captureGeneration: observation.captureGeneration,
                localEpoch: observation.localEpoch,
                basisRevision: observation.basisRevision,
                roomID: observation.roomID,
                rawPose: observation.rawPose,
                optimizedPose: poses[observation.id]!,
                groundGeometry: observation.groundGeometry,
                observedFloorCandidates: observation.observedFloorCandidates,
                observedPlayerBounds: observation.observedPlayerBounds
            )
        }
        let originalObservationByID = Dictionary(uniqueKeysWithValues: observations.map { ($0.id, $0) })
        let replacementByID = Dictionary(uniqueKeysWithValues: replaced.map { ($0.id, $0) })
        let keyframeByID = Dictionary(uniqueKeysWithValues: keyframes.map { ($0.id, $0) })
        let movedLandmarks = landmarks.map { landmark -> LiveWorldLandmark in
            guard let keyframe = keyframeByID[landmark.keyframeID],
                  let oldObservation = originalObservationByID[keyframe.observationID],
                  let newObservation = replacementByID[keyframe.observationID] else {
                return landmark
            }
            let depth = CGFloat(landmark.depth)
            return LiveWorldLandmark(
                id: landmark.id,
                keyframeID: landmark.keyframeID,
                sourceObservationID: landmark.sourceObservationID,
                samplePoint: landmark.samplePoint,
                worldPoint: CGPoint(
                    x: landmark.worldPoint.x
                        + (newObservation.optimizedPose.x - oldObservation.optimizedPose.x) * depth,
                    y: landmark.worldPoint.y
                        + (newObservation.optimizedPose.y - oldObservation.optimizedPose.y) * depth
                ),
                descriptor: landmark.descriptor,
                depth: landmark.depth,
                depthConfidence: landmark.depthConfidence
            )
        }
        return try LiveWorldSnapshot(
            schemaVersion: schemaVersion,
            mapRevision: mapRevision + 1,
            anchoredObservationID: anchoredObservationID,
            observations: replaced,
            keyframes: keyframes,
            landmarks: movedLandmarks,
            relativeMotionEdges: relativeMotionEdges,
            loopClosureEdges: loopClosureEdges,
            trackingIslands: movedIslands,
            doorwayFloorConstraints: doorwayFloorConstraints
        )
    }

    /// A revision-checked whole-island correction. All member screenshots and
    /// their owned landmarks move through the same existing replacement path.
    func translatingIslands(_ translations: [Int: CGVector], baseRevision: Int) throws -> LiveWorldSnapshot {
        guard Set(translations.keys).isSubset(of: Set(trackingIslands.map(\.id))),
              translations.values.allSatisfy({ $0.dx.isFinite && $0.dy.isFinite }) else {
            throw LiveWorldModelError.invalidIslandMembership
        }
        var poses = Dictionary(uniqueKeysWithValues: observations.map { ($0.id, $0.optimizedPose) })
        for island in trackingIslands {
            guard let shift = translations[island.id] else { continue }
            for member in island.members {
                guard let old = poses[member.observationID] else { throw LiveWorldModelError.invalidIslandMembership }
                poses[member.observationID] = CGPoint(x: old.x + shift.dx, y: old.y + shift.dy)
            }
        }
        return try replacingOptimizedPoses(poses, baseRevision: baseRevision)
    }

    func translatingRoom(
        _ roomID: Int,
        by translation: CGVector
    ) throws -> LiveWorldSnapshot {
        guard roomID >= 0, translation.dx.isFinite, translation.dy.isFinite else {
            throw LiveWorldModelError.invalidGeometry
        }
        var positions = Dictionary(uniqueKeysWithValues: observations.map {
            ($0.id, $0.optimizedPose)
        })
        for observation in observations where observation.roomID == roomID {
            positions[observation.id] = CGPoint(
                x: observation.optimizedPose.x + translation.dx,
                y: observation.optimizedPose.y + translation.dy
            )
        }
        let optimized = try replacingOptimizedPoses(
            positions,
            baseRevision: mapRevision
        )
        // Raw poses are immutable capture evidence and are checked against the
        // append-only frame manifest. Room placement is an optimization-layer
        // correction, so only optimized poses and their landmarks may move.
        return optimized
    }

    private func validate() throws {
        guard schemaVersion == Self.currentSchemaVersion else {
            throw LiveWorldModelError.unsupportedSchema(schemaVersion)
        }
        guard mapRevision >= 0 else { throw LiveWorldModelError.invalidRevision }
        try Self.unique(observations.map(\.id), error: LiveWorldModelError.duplicateObservationID)
        try Self.unique(keyframes.map(\.id), error: LiveWorldModelError.duplicateKeyframeID)
        try Self.unique(landmarks.map(\.id), error: LiveWorldModelError.duplicateLandmarkID)
        try Self.unique(relativeMotionEdges.map(\.id), error: LiveWorldModelError.duplicateRelativeEdgeID)
        try Self.unique(loopClosureEdges.map(\.id), error: LiveWorldModelError.duplicateLoopEdgeID)

        let observationIDs = Set(observations.map(\.id))
        if observations.isEmpty {
            guard anchoredObservationID == nil, keyframes.isEmpty, landmarks.isEmpty,
                  relativeMotionEdges.isEmpty, loopClosureEdges.isEmpty else {
                throw LiveWorldModelError.unanchoredWorld
            }
        } else {
            guard let anchoredObservationID, observationIDs.contains(anchoredObservationID) else {
                throw LiveWorldModelError.unanchoredWorld
            }
        }
        guard observations.allSatisfy(Self.isValid) else { throw LiveWorldModelError.invalidGeometry }

        let observationByID = Dictionary(uniqueKeysWithValues: observations.map { ($0.id, $0) })
        var islandIDs = Set<Int>(), memberIDs = Set<Int>()
        for island in trackingIslands {
            guard island.id >= 0, islandIDs.insert(island.id).inserted,
                  island.roomID >= 0, island.startedAt.isFinite,
                  Self.isFinite(island.worldFromLocal), island.uncertainty.isValid,
                  !island.members.isEmpty else { throw LiveWorldModelError.invalidIslandMembership }
            for member in island.members {
                guard memberIDs.insert(member.observationID).inserted,
                      let observation = observationByID[member.observationID],
                      observation.roomID == island.roomID, Self.isFinite(member.localPose),
                      hypot(observation.optimizedPose.x - member.localPose.x - island.worldFromLocal.x,
                            observation.optimizedPose.y - member.localPose.y - island.worldFromLocal.y) <= 0.000001 else {
                    throw LiveWorldModelError.invalidIslandMembership
                }
            }
        }
        guard memberIDs == observationIDs else { throw LiveWorldModelError.invalidIslandMembership }
        guard Set(doorwayFloorConstraints.map(\.id)).count == doorwayFloorConstraints.count,
              doorwayFloorConstraints.allSatisfy({ $0.isValid(in: self, requiringAlignment: true) }) else {
            throw LiveWorldModelError.invalidDoorwayFloorConstraint
        }
        let keyframeIDs = Set(keyframes.map(\.id))
        for keyframe in keyframes {
            guard let observation = observationByID[keyframe.observationID] else {
                throw LiveWorldModelError.danglingObservationID(keyframe.observationID)
            }
            guard keyframe.id >= 0, keyframe.sourceObservationID >= 0,
                  keyframe.sourceObservationID == observation.sourceObservationID else {
                throw LiveWorldModelError.invalidGeometry
            }
        }
        let keyframeByID = Dictionary(uniqueKeysWithValues: keyframes.map { ($0.id, $0) })
        for landmark in landmarks {
            guard let keyframe = keyframeByID[landmark.keyframeID] else {
                throw LiveWorldModelError.danglingKeyframeID(landmark.keyframeID)
            }
            guard landmark.id >= 0, landmark.sourceObservationID >= 0,
                  landmark.sourceObservationID == keyframe.sourceObservationID,
                  Self.isFinite(landmark.samplePoint), Self.isFinite(landmark.worldPoint),
                  !landmark.descriptor.isEmpty, landmark.descriptor.allSatisfy({ $0.isFinite }),
                  landmark.depth.isFinite, landmark.depth > 0,
                  landmark.depthConfidence.isFinite, (0...1).contains(landmark.depthConfidence)
            else { throw LiveWorldModelError.invalidGeometry }
        }
        for edge in relativeMotionEdges {
            guard observationIDs.contains(edge.fromObservationID), observationIDs.contains(edge.toObservationID) else {
                throw LiveWorldModelError.danglingObservationID(
                    observationIDs.contains(edge.fromObservationID) ? edge.toObservationID : edge.fromObservationID
                )
            }
            guard edge.id >= 0, edge.fromObservationID != edge.toObservationID,
                  observationByID[edge.fromObservationID]?.roomID
                    == observationByID[edge.toObservationID]?.roomID,
                  edge.deltaX.isFinite, edge.deltaY.isFinite, edge.support > 0 else {
                throw LiveWorldModelError.invalidGeometry
            }
        }
        let landmarkByID = Dictionary(uniqueKeysWithValues: landmarks.map { ($0.id, $0) })
        let landmarkIDs = Set(landmarkByID.keys)
        for edge in loopClosureEdges {
            guard keyframeIDs.contains(edge.fromKeyframeID), keyframeIDs.contains(edge.toKeyframeID) else {
                throw LiveWorldModelError.danglingKeyframeID(
                    keyframeIDs.contains(edge.fromKeyframeID) ? edge.toKeyframeID : edge.fromKeyframeID
                )
            }
            guard !edge.landmarkIDs.isEmpty, Set(edge.landmarkIDs).count == edge.landmarkIDs.count,
                  edge.landmarkIDs.allSatisfy(landmarkIDs.contains) else {
                throw LiveWorldModelError.danglingLandmarkID(edge.landmarkIDs.first(where: { !landmarkIDs.contains($0) }) ?? -1)
            }
            let fromObservation = keyframeByID[edge.fromKeyframeID]
                .flatMap { observationByID[$0.observationID] }
            let toObservation = keyframeByID[edge.toKeyframeID]
                .flatMap { observationByID[$0.observationID] }
            guard edge.id >= 0, edge.fromKeyframeID != edge.toKeyframeID,
                  fromObservation?.roomID == toObservation?.roomID,
                  edge.landmarkIDs.allSatisfy({ landmarkByID[$0]?.keyframeID == edge.fromKeyframeID }),
                  edge.deltaX.isFinite, edge.deltaY.isFinite, edge.support >= 3,
                  edge.ambiguity.isFinite, edge.ambiguity >= 0,
                  edge.residualPixels.map({ $0.isFinite && $0 >= 0 }) ?? true else {
                throw LiveWorldModelError.invalidGeometry
            }
        }
    }

    private static func unique(_ ids: [Int], error: (Int) -> LiveWorldModelError) throws {
        var seen = Set<Int>()
        for id in ids {
            guard id >= 0 else { throw LiveWorldModelError.invalidGeometry }
            guard seen.insert(id).inserted else { throw error(id) }
        }
    }

    private static func isValid(_ observation: LiveWorldObservation) -> Bool {
        observation.id >= 0 && observation.sourceObservationID >= 0 && observation.roomID >= 0
            && observation.timestamp.isFinite
            && (observation.captureTimestamp?.isFinite ?? true)
            && observation.sourceWidth > 0 && observation.sourceHeight > 0 && observation.sampleWidth > 0
            && observation.solveWidth.isFinite && observation.solveWidth > 0
            && isFinite(observation.localPose) && isFinite(observation.rawPose) && isFinite(observation.optimizedPose)
            && (observation.groundGeometry?.isValid(solveWidth: observation.solveWidth,
                sourceWidth: observation.sourceWidth, sourceHeight: observation.sourceHeight) ?? true)
            && (observation.observedFloorCandidates?.isValid(solveWidth: observation.solveWidth,
                sourceWidth: observation.sourceWidth, sourceHeight: observation.sourceHeight) ?? true)
            && (observation.observedPlayerBounds.map {
                $0.minX.isFinite && $0.minY.isFinite && $0.width.isFinite && $0.height.isFinite
                    && $0.width > 0 && $0.height > 0
            } ?? true)
            && observation.excludedRects.allSatisfy {
                $0.origin.x.isFinite && $0.origin.y.isFinite && $0.width.isFinite && $0.height.isFinite
                    && $0.width >= 0 && $0.height >= 0
            }
    }

    private static func isFinite(_ point: CGPoint) -> Bool {
        point.x.isFinite && point.y.isFinite
    }

    private enum CodingKeys: String, CodingKey {
        case schemaVersion, mapRevision, anchoredObservationID, observations, keyframes, landmarks
        case relativeMotionEdges, loopClosureEdges, trackingIslands, doorwayFloorConstraints
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(
            schemaVersion: container.decode(Int.self, forKey: .schemaVersion),
            mapRevision: container.decode(Int.self, forKey: .mapRevision),
            anchoredObservationID: container.decodeIfPresent(Int.self, forKey: .anchoredObservationID),
            observations: container.decode([LiveWorldObservation].self, forKey: .observations),
            keyframes: container.decode([LiveWorldKeyframe].self, forKey: .keyframes),
            landmarks: container.decode([LiveWorldLandmark].self, forKey: .landmarks),
            relativeMotionEdges: container.decode([LiveWorldRelativeMotionEdge].self, forKey: .relativeMotionEdges),
            loopClosureEdges: container.decode([LiveWorldLoopClosureEdge].self, forKey: .loopClosureEdges),
            trackingIslands: container.decodeIfPresent([LiveTrackingIsland].self, forKey: .trackingIslands),
            doorwayFloorConstraints: container.decodeIfPresent([LiveDoorwayFloorConstraint].self,
                forKey: .doorwayFloorConstraints) ?? []
        )
    }
}
