import CoreGraphics
import Foundation

enum LiveWorldReplayEvaluatorError: Error, Equatable {
    case emptySession
    case invalidRepeatCount
    case invalidReturnThreshold
    case invalidLoopDetectionThreshold
    case persistentReplayCommitFailed
    case persistentReplayObservationMismatch
    case invalidArgument(String)
}

enum LiveWorldReplayRouteMode: String, Codable, Equatable {
    case automatic
    case oneWay
    case returning
}

/// Configuration for repeatedly replaying one persisted capture route. Searches
/// are deliberately capped at two per second, even when the recording is denser.
struct LiveWorldReplayOptions: Codable, Equatable {
    let repeatCount: Int
    let returnThresholdPixels: Double
    let loopDetectionThresholdPixels: Double
    let reopenSnapshotBetweenPasses: Bool
    let routeMode: LiveWorldReplayRouteMode

    init(
        repeatCount: Int = 3,
        returnThresholdPixels: Double = 12,
        loopDetectionThresholdPixels: Double = 24,
        reopenSnapshotBetweenPasses: Bool = false,
        routeMode: LiveWorldReplayRouteMode = .oneWay
    ) {
        self.repeatCount = repeatCount
        self.returnThresholdPixels = returnThresholdPixels
        self.loopDetectionThresholdPixels = loopDetectionThresholdPixels
        self.reopenSnapshotBetweenPasses = reopenSnapshotBetweenPasses
        self.routeMode = routeMode
    }
}

struct LiveWorldReplayPassReport: Codable, Equatable {
    let passIndex: Int
    let observationCount: Int
    let globalSearchCount: Int
    let rawReturnError: Double
    let optimizedReturnError: Double
    /// True when the final frame directly relocalizes against the first saved
    /// frame. In that case return error is measured against the visually
    /// recovered endpoint instead of assuming both camera views are identical.
    let endpointFeatureMatchFound: Bool
    let appliedCorrectionX: Double
    let appliedCorrectionY: Double
    /// A route is only expected to close when its recorded camera returns to its
    /// starting pose within the configured practical threshold.
    let recordingReturnsToStart: Bool
    let newLoopClosures: Int
    let closureRequired: Bool
    let closureRequirementMet: Bool
    /// A false closure that collapsed a one-way route's optimized endpoint onto
    /// its recorded start. Internal revisits do not make the whole route a loop.
    let unexpectedClosure: Bool
    let savedLandmarkCount: Int
    let savedKeyframeCount: Int
    let savedRevision: Int
    let passed: Bool
}

struct LiveWorldReplayReport: Codable, Equatable {
    static let currentSchemaVersion = 2

    let schemaVersion: Int
    let repeatCount: Int
    let globalSearchHz: Double
    let returnThresholdPixels: Double
    /// A wider bound used only to classify whether the source route intended
    /// to return. The tighter return threshold still decides whether the
    /// optimized map is aligned well enough to pass.
    let loopDetectionThresholdPixels: Double
    let reopenedSnapshotBetweenPasses: Bool
    let routeMode: LiveWorldReplayRouteMode
    let passes: [LiveWorldReplayPassReport]
    let passed: Bool
    let failureReasons: [String]
}

/// Replays a `SceneSessionStore` without mutating it. With reopen enabled, each
/// replay observation and graph revision is written to an isolated live-world
/// store; later passes reconstruct both the store and tracker from disk.
final class LiveWorldReplayEvaluator {
    private let store: SceneSessionStore
    private let maximumGlobalSearchHz = 2.0

    init(store: SceneSessionStore) {
        self.store = store
    }

    convenience init(sessionRootURL: URL) throws {
        try self.init(store: SceneSessionStore(rootURL: sessionRootURL))
    }

    static func run(arguments: [String]) throws -> LiveWorldReplayReport {
        var sessionURL: URL?
        var reportURL: URL?
        var repeats = 3
        var reopen = false
        // A replay pass is an independent visit. Never infer that its terminal
        // frame connects back to its first frame unless the caller explicitly
        // declares a returning route. This keeps Tutorial_01 -> Town one-way
        // even when Vision drift happens to place Town near the route start.
        var routeMode = LiveWorldReplayRouteMode.oneWay
        var index = 0
        while index < arguments.count {
            let flag = arguments[index]
            if flag == "--world-replay-reopen" {
                reopen = true
                index += 1
                continue
            }
            if flag == "--world-replay-one-way" {
                routeMode = .oneWay
                index += 1
                continue
            }
            if flag == "--world-replay-returning" {
                routeMode = .returning
                index += 1
                continue
            }
            guard ["--world-replay-session", "--world-replay-report", "--world-replay-repeats"].contains(flag),
                  index + 1 < arguments.count else {
                throw LiveWorldReplayEvaluatorError.invalidArgument(flag)
            }
            let value = arguments[index + 1]
            switch flag {
            case "--world-replay-session": sessionURL = URL(fileURLWithPath: value)
            case "--world-replay-report": reportURL = URL(fileURLWithPath: value)
            case "--world-replay-repeats":
                guard let parsed = Int(value), parsed > 0 else {
                    throw LiveWorldReplayEvaluatorError.invalidArgument(value)
                }
                repeats = parsed
            default: break
            }
            index += 2
        }
        guard let sessionURL else {
            throw LiveWorldReplayEvaluatorError.invalidArgument("--world-replay-session")
        }
        return try LiveWorldReplayEvaluator(sessionRootURL: sessionURL).evaluate(
            options: LiveWorldReplayOptions(
                repeatCount: repeats,
                reopenSnapshotBetweenPasses: reopen,
                routeMode: routeMode
            ),
            outputURL: reportURL
        )
    }

    @discardableResult
    func evaluate(
        options: LiveWorldReplayOptions = LiveWorldReplayOptions(),
        outputURL: URL? = nil
    ) throws -> LiveWorldReplayReport {
        guard options.repeatCount > 0 else { throw LiveWorldReplayEvaluatorError.invalidRepeatCount }
        guard options.returnThresholdPixels.isFinite, options.returnThresholdPixels > 0 else {
            throw LiveWorldReplayEvaluatorError.invalidReturnThreshold
        }
        guard options.loopDetectionThresholdPixels.isFinite,
              options.loopDetectionThresholdPixels >= options.returnThresholdPixels else {
            throw LiveWorldReplayEvaluatorError.invalidLoopDetectionThreshold
        }

        // Manifest order is the durable capture order. Do not reorder by timestamp:
        // a damaged clock must not rearrange its PNG evidence.
        let observations = store.manifest.observations
        guard !observations.isEmpty else { throw LiveWorldReplayEvaluatorError.emptySession }
        // SceneSessionStore owns UUID room identities while LiveWorldTracker uses
        // compact integer identities. Preserve first-seen room ownership across
        // every pass. Flattening the source to room 0 lets repeated imagery in a
        // later room falsely close against the route's first room.
        var nextRoomID = 0
        var trackerRoomIDs = [UUID: Int]()
        for observation in observations where trackerRoomIDs[observation.roomID] == nil {
            trackerRoomIDs[observation.roomID] = nextRoomID
            nextRoomID += 1
        }
        let frames = try observations.map(store.source(for:))
        let pixelsPerSolveUnit = Double(frames[0].width) / observations[0].solveWidth
        let endpointMatcher = GlobalFeatureMatcher()
        let endpointReference = endpointMatcher.makeReference(
            frame: frames[0],
            cameraPose: observations[0].cameraPosition,
            solveWidth: observations[0].solveWidth,
            excluding: observations[0].excludedRects
        )
        let endpointMatch = endpointReference.flatMap {
            endpointMatcher.match(
                frame: frames[frames.count - 1],
                references: [$0],
                currentSolveWidth: observations[observations.count - 1].solveWidth,
                excluding: observations[observations.count - 1].excludedRects
            )
        }
        let rawEndpointDistance = Double(distance(
            observations[0].cameraPosition,
            observations[observations.count - 1].cameraPosition
        )) * pixelsPerSolveUnit
        // Appearance can repeat at a distant location and across room
        // boundaries. It is supporting alignment evidence, never route
        // topology. A route returns only when its recorded endpoint is in the
        // same room and is geometrically near its recorded start.
        let inferredReturnToStart = observations.count >= 2
            && observations[0].roomID == observations[observations.count - 1].roomID
            && rawEndpointDistance <= options.loopDetectionThresholdPixels
        // Route topology is test input, not something Vision should be allowed
        // to rewrite from its own drifting poses. One-way is the safe default.
        // Automatic remains only for programmatic evaluation of older sessions.
        let recordingReturnsToStart: Bool
        switch options.routeMode {
        case .oneWay:
            recordingReturnsToStart = false
        case .returning:
            recordingReturnsToStart = true
        case .automatic:
            recordingReturnsToStart = inferredReturnToStart
        }
        let expectedEndPose = recordingReturnsToStart
            ? (endpointMatch?.proposedCameraPose ?? observations[0].cameraPosition)
            : observations[observations.count - 1].cameraPosition
        let rawReturnError = Double(distance(
            expectedEndPose,
            observations[observations.count - 1].cameraPosition
        )) * pixelsPerSolveUnit

        let persistentReplayRoot = options.reopenSnapshotBetweenPasses
            ? FileManager.default.temporaryDirectory.appendingPathComponent(
                "hkv-world-replay-\(UUID().uuidString)",
                isDirectory: true
            )
            : nil
        defer {
            if let persistentReplayRoot {
                try? FileManager.default.removeItem(at: persistentReplayRoot)
            }
        }
        var persistentWorldStore = try persistentReplayRoot.map(LiveWorldSessionStore.init(rootURL:))
        var persistentRevision = 0

        var tracker = LiveWorldTracker()
        var nextObservationID = 0
        var previousTimestamp = -Double.infinity
        var reports = [LiveWorldReplayPassReport]()
        let sourceStart = observations[0].timestamp
        let sourceSpan = max(
            observations.last!.timestamp - sourceStart,
            Double(observations.count) * 0.001
        ) + 1

        for passIndex in 0..<options.repeatCount {
            if passIndex > 0 {
                if options.reopenSnapshotBetweenPasses {
                    guard let persistentReplayRoot else {
                        throw LiveWorldReplayEvaluatorError.persistentReplayCommitFailed
                    }
                    let reopened = try LiveWorldSessionStore(rootURL: persistentReplayRoot)
                    let state = try reopened.load()
                    tracker = try LiveWorldTracker(snapshot: state.snapshot)
                    persistentWorldStore = reopened
                    persistentRevision = state.sceneRevision
                }
                // Reset visit-local motion and closure confirmation state while
                // retaining saved descriptors for immediate global relocalization.
                tracker.beginVisit()
            }

            let closureCountBefore = tracker.snapshot.loopClosureEdges.count
            var lastReplayID = nextObservationID
            var lastSearchTimestamp: Double?
            var globalSearchCount = 0
            var appliedCorrection = CGPoint.zero

            for (index, source) in observations.enumerated() {
                let candidateTimestamp = Double(passIndex) * sourceSpan + (source.timestamp - sourceStart)
                let timestamp = max(candidateTimestamp, previousTimestamp + 0.001)
                previousTimestamp = timestamp
                let shouldSearch = lastSearchTimestamp.map { timestamp - $0 >= 1 / maximumGlobalSearchHz } ?? true
                if shouldSearch {
                    lastSearchTimestamp = timestamp
                    globalSearchCount += 1
                }

                let proposedPose = CGPoint(
                    x: source.cameraPosition.x + appliedCorrection.x,
                    y: source.cameraPosition.y + appliedCorrection.y
                )
                let storedObservation = try persistentWorldStore?.append(
                    maskedImage: frames[index],
                    timestamp: timestamp,
                    rawCameraPose: proposedPose,
                    solveWidth: source.solveWidth,
                    excludedRects: source.excludedRects
                )
                let replayObservationID = storedObservation?.id ?? nextObservationID
                guard replayObservationID == nextObservationID else {
                    throw LiveWorldReplayEvaluatorError.persistentReplayObservationMismatch
                }
                let update = try tracker.ingest(
                    observationID: replayObservationID,
                    sourceObservationID: storedObservation?.id ?? source.id,
                    timestamp: timestamp,
                    frame: frames[index],
                    proposedCameraPose: proposedPose,
                    solveWidth: source.solveWidth,
                    exclusions: source.excludedRects,
                    searchForClosure: shouldSearch,
                    roomID: trackerRoomIDs[source.roomID]!
                )
                if update.acceptedClosure != nil {
                    appliedCorrection.x += update.correction.x
                    appliedCorrection.y += update.correction.y
                }
                if let persistentWorldStore {
                    let committed = try persistentWorldStore.commit(
                        update.snapshot,
                        expectedRevision: persistentRevision,
                        newRevision: update.revision
                    )
                    guard committed else {
                        throw LiveWorldReplayEvaluatorError.persistentReplayCommitFailed
                    }
                    persistentRevision = update.revision
                }
                lastReplayID = nextObservationID
                nextObservationID += 1
            }

            let last = tracker.snapshot.observations.first(where: { $0.id == lastReplayID })!
            let optimizedReturnError = Double(distance(expectedEndPose, last.optimizedPose)) * pixelsPerSolveUnit
            let optimizedEndpointDistanceFromStart = Double(distance(
                observations[0].cameraPosition,
                last.optimizedPose
            )) * pixelsPerSolveUnit
            let newLoopClosures = tracker.snapshot.loopClosureEdges.count - closureCountBefore
            let closureRequired = recordingReturnsToStart
            let closureRequirementMet = !closureRequired || newLoopClosures >= 1
            let unexpectedClosure = !recordingReturnsToStart
                && newLoopClosures > 0
                && optimizedEndpointDistanceFromStart
                    <= options.loopDetectionThresholdPixels
            let passed = closureRequired
                ? closureRequirementMet && optimizedReturnError <= options.returnThresholdPixels
                : !unexpectedClosure
                    && optimizedReturnError <= options.returnThresholdPixels

            reports.append(LiveWorldReplayPassReport(
                passIndex: passIndex,
                observationCount: observations.count,
                globalSearchCount: globalSearchCount,
                rawReturnError: Double(rawReturnError),
                optimizedReturnError: Double(optimizedReturnError),
                endpointFeatureMatchFound: endpointMatch != nil,
                appliedCorrectionX: Double(appliedCorrection.x) * pixelsPerSolveUnit,
                appliedCorrectionY: Double(appliedCorrection.y) * pixelsPerSolveUnit,
                recordingReturnsToStart: recordingReturnsToStart,
                newLoopClosures: newLoopClosures,
                closureRequired: closureRequired,
                closureRequirementMet: closureRequirementMet,
                unexpectedClosure: unexpectedClosure,
                savedLandmarkCount: tracker.snapshot.landmarks.count,
                savedKeyframeCount: tracker.snapshot.keyframes.count,
                savedRevision: tracker.snapshot.mapRevision,
                passed: passed
            ))
        }

        let failures = reports.compactMap { pass -> String? in
            guard !pass.passed else { return nil }
            if pass.unexpectedClosure {
                return "pass \(pass.passIndex): one-way endpoint collapsed onto the route start"
            }
            if !pass.closureRequirementMet {
                return "pass \(pass.passIndex): no loop closure accepted for a returning recording"
            }
            let kind = pass.recordingReturnsToStart ? "return" : "terminal"
            return "pass \(pass.passIndex): optimized \(kind) error \(pass.optimizedReturnError) exceeds \(options.returnThresholdPixels) pixels"
        }
        let report = LiveWorldReplayReport(
            schemaVersion: LiveWorldReplayReport.currentSchemaVersion,
            repeatCount: options.repeatCount,
            globalSearchHz: maximumGlobalSearchHz,
            returnThresholdPixels: options.returnThresholdPixels,
            loopDetectionThresholdPixels: options.loopDetectionThresholdPixels,
            reopenedSnapshotBetweenPasses: options.reopenSnapshotBetweenPasses,
            routeMode: options.routeMode,
            passes: reports,
            passed: failures.isEmpty,
            failureReasons: failures
        )
        if let outputURL {
            try FileManager.default.createDirectory(
                at: outputURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(report).write(to: outputURL, options: .atomic)
        }
        return report
    }

    private func distance(_ lhs: CGPoint, _ rhs: CGPoint) -> CGFloat {
        hypot(lhs.x - rhs.x, lhs.y - rhs.y)
    }
}
