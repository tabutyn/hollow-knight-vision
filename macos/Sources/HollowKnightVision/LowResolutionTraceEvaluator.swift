import CoreGraphics
import Foundation

enum LowResolutionTraceEvaluatorError: Error, Equatable {
    case invalidArgument(String)
    case missingTracePath
    case missingPersistedWorldEvidence
}

struct LowResolutionTraceErrorStatistics: Codable, Equatable {
    let count: Int
    let rms: Double?
    let p50: Double?
    let p95: Double?
    let maximum: Double?
}

struct LowResolutionTraceWorstMotion: Codable, Equatable {
    let offset: TimeInterval
    let sceneName: String
    let expectedDX: Double
    let expectedDY: Double
    let predictedDX: Double?
    let predictedDY: Double?
    let errorPixels: Double
}

struct LowResolutionTraceRejectedTravelSummary: Codable, Equatable {
    let comparisons: Int
    let expectedDistancePixels: LowResolutionTraceErrorStatistics
    let signedExpectedX: Double
    let signedExpectedY: Double
    let absoluteExpectedX: Double
    let absoluteExpectedY: Double
}

struct LowResolutionTraceBridgeEvent: Codable, Equatable {
    let offset: TimeInterval
    let sceneName: String
    let expectedDirection: String?
    let rejection: String
    let expectedDX: Double
    let expectedDY: Double
    let bridgedDX: Double?
    let bridgedDY: Double?
}

struct LowResolutionTraceGroundLossEvent: Codable, Equatable {
    let offset: TimeInterval
    let sceneName: String
    let expectedDirection: String?
    let rejection: String?
    let expectedDX: Double
    let expectedDY: Double
    let measuredDX: Double?
    let measuredDY: Double?
    let bridgedDX: Double?
    let bridgedDY: Double?
}

struct LowResolutionTraceMotionSummary: Codable, Equatable {
    let comparisons: Int
    let representableComparisons: Int
    let acceptedComparisons: Int
    let predictedAcceptedComparisons: Int
    let acceptedRepresentableComparisons: Int
    let rejectedRepresentableComparisons: Int
    let directionGateRejections: Int
    let translationRejections: [String: Int]
    /// Hacker-measured travel hidden by each rejection class. Counts alone
    /// cannot distinguish harmless static rejects from a short blackout that
    /// discards most of the route's motion.
    let rejectedTravelByReason: [String: LowResolutionTraceRejectedTravelSummary]
    let outOfRangeComparisons: Int
    let acceptedOutOfRangeComparisons: Int
    let representableCoveragePercent: Double?
    let horizontalDirectionAgreementPercent: Double?
    let vectorErrorPixels: LowResolutionTraceErrorStatistics
    let integratedDriftPixels: LowResolutionTraceErrorStatistics
    let horizontalBiasPixels: Double?
    let verticalBiasPixels: Double?
    let worstMotion: LowResolutionTraceWorstMotion?
}

struct LowResolutionTracePlaceSummary: Codable, Equatable {
    let matches: Int
    let matchesOver24Pixels: Int
    let errorPixels: LowResolutionTraceErrorStatistics
}

struct LowResolutionTraceConnectedCameraSummary: Codable, Equatable {
    let source: String
    let exactFramePairs: Int
    let sceneNames: [String]
    let errorPixels: LowResolutionTraceErrorStatistics
    let sceneErrorPixels: [String: LowResolutionTraceErrorStatistics]
    let joinedSceneErrorPixels: LowResolutionTraceErrorStatistics
    let firstErrorOverPixelsAtOffset: [String: TimeInterval]
    let maximumErrorOffset: TimeInterval?
    let maximumErrorScene: String?
    let joinedSceneFirstOffset: TimeInterval?
    let joinedSceneFirstErrorX: Double?
    let joinedSceneFirstErrorY: Double?
    let endErrorX: Double?
    let endErrorY: Double?
}

struct LowResolutionTraceEvaluationReport: Codable, Equatable {
    static let currentSchemaVersion = 10

    let schemaVersion: Int
    let pathID: UUID
    let replayIteration: Int?
    let traceFrames: Int
    let validTraceFrames: Int
    let exactTruthFrames: Int
    let exactTruthCoveragePercent: Double?
    let groundReliableFrames: Int
    let groundUnverifiedFrames: Int
    let traceIntervalSeconds: LowResolutionTraceErrorStatistics
    let evaluable: Bool
    let solveWidth: Double
    let motionMinimumImprovement: Double
    let allMotion: LowResolutionTraceMotionSummary
    let groundLossMotion: LowResolutionTraceMotionSummary
    /// Same ground-loss intervals passed through the production odometry,
    /// including its bounded same-direction overlap-gap prediction.
    let groundLossBridgedMotion: LowResolutionTraceMotionSummary
    let groundLossBridgeEvents: [LowResolutionTraceBridgeEvent]
    /// Every exact-truth comparison in a ground-loss interval. This exposes
    /// accepted but wrong vectors that rejection-only diagnostics cannot see.
    let groundLossEvents: [LowResolutionTraceGroundLossEvent]
    let oracleSeededPlaceRecognition: LowResolutionTracePlaceSummary
    /// Capture-rate pose actually presented while the solver runs behind.
    let connectedCamera: LowResolutionTraceConnectedCameraSummary
    /// Pose published by completed Vision solves and used for atlas admission.
    let connectedTrackingCamera: LowResolutionTraceConnectedCameraSummary
    let notes: [String]
}

/// Replays the saved 64x36 matcher input through the production translation
/// and place-memory implementations. Hacker telemetry is joined only by the
/// exact rendered Unity frame, so receipt timing can never make a weak nearest
/// sample look accurate.
enum LowResolutionTraceEvaluator {
    private struct ComparableFrame {
        let recorded: RecordedLowResolutionFrame
        let grid: LowResolutionMotionGrid
        let truth: ReceiverGroundTruthSample
        let truthPosition: CGPoint
        let observedVisionPosition: CGPoint?
        let pixelScale: Double
        let expectedDirection: VisualRoomDirection?
    }

    private struct MotionAccumulator {
        private struct RejectedTravelAccumulator {
            var distances = [Double]()
            var signedX = 0.0
            var signedY = 0.0
            var absoluteX = 0.0
            var absoluteY = 0.0

            mutating func append(dx: Double, dy: Double) {
                distances.append(hypot(dx, dy))
                signedX += dx
                signedY += dy
                absoluteX += abs(dx)
                absoluteY += abs(dy)
            }

            func summary() -> LowResolutionTraceRejectedTravelSummary {
                LowResolutionTraceRejectedTravelSummary(
                    comparisons: distances.count,
                    expectedDistancePixels: statistics(distances),
                    signedExpectedX: signedX,
                    signedExpectedY: signedY,
                    absoluteExpectedX: absoluteX,
                    absoluteExpectedY: absoluteY
                )
            }
        }

        var comparisons = 0
        var representable = 0
        var accepted = 0
        var predictedAccepted = 0
        var acceptedRepresentable = 0
        var rejectedRepresentable = 0
        var directionGateRejections = 0
        var translationRejections = [String: Int]()
        private var rejectedTravel = [String: RejectedTravelAccumulator]()
        var outOfRange = 0
        var acceptedOutOfRange = 0
        var errors = [Double]()
        var integratedErrors = [Double]()
        var xErrors = [Double]()
        var yErrors = [Double]()
        var horizontalDirections = 0
        var matchingHorizontalDirections = 0
        var worst: LowResolutionTraceWorstMotion?
        var integratedExpectedX = 0.0
        var integratedExpectedY = 0.0
        var integratedPredictedX = 0.0
        var integratedPredictedY = 0.0

        mutating func resetIntegration() {
            integratedExpectedX = 0
            integratedExpectedY = 0
            integratedPredictedX = 0
            integratedPredictedY = 0
        }

        mutating func append(
            current: ComparableFrame,
            expectedDX: Double,
            expectedDY: Double,
            motion: LowResolutionMotionVector?,
            isRepresentable: Bool,
            rejectedByDirectionGate: Bool,
            translationRejection: LowResolutionTranslationRejection?,
            isPredicted: Bool = false
        ) {
            comparisons += 1
            if isRepresentable {
                representable += 1
                integratedExpectedX += expectedDX
                integratedExpectedY += expectedDY
            } else {
                outOfRange += 1
                resetIntegration()
            }
            guard let motion else {
                if isRepresentable { rejectedRepresentable += 1 }
                if rejectedByDirectionGate {
                    directionGateRejections += 1
                    if isRepresentable {
                        rejectedTravel["directionGate", default: .init()].append(
                            dx: expectedDX, dy: expectedDY
                        )
                    }
                }
                if let translationRejection {
                    translationRejections[translationRejection.rawValue, default: 0] += 1
                    if isRepresentable, !rejectedByDirectionGate {
                        rejectedTravel[
                            translationRejection.rawValue, default: .init()
                        ].append(dx: expectedDX, dy: expectedDY)
                    }
                }
                if isRepresentable {
                    integratedErrors.append(hypot(
                        integratedPredictedX - integratedExpectedX,
                        integratedPredictedY - integratedExpectedY
                    ))
                }
                let error = hypot(expectedDX, expectedDY)
                replaceWorstIfNeeded(
                    current: current,
                    expectedDX: expectedDX,
                    expectedDY: expectedDY,
                    predictedDX: nil,
                    predictedDY: nil,
                    error: error
                )
                return
            }
            accepted += 1
            if isPredicted { predictedAccepted += 1 }
            if isRepresentable {
                acceptedRepresentable += 1
            } else {
                acceptedOutOfRange += 1
            }
            let predictedDX = -motion.screenShiftX * current.pixelScale
            let predictedDY = motion.screenShiftY * current.pixelScale
            if isRepresentable {
                integratedPredictedX += predictedDX
                integratedPredictedY += predictedDY
                integratedErrors.append(hypot(
                    integratedPredictedX - integratedExpectedX,
                    integratedPredictedY - integratedExpectedY
                ))
            }
            let xError = predictedDX - expectedDX
            let yError = predictedDY - expectedDY
            let error = hypot(xError, yError)
            errors.append(error)
            xErrors.append(xError)
            yErrors.append(yError)
            if abs(expectedDX) >= 1, abs(predictedDX) >= 0.25 {
                horizontalDirections += 1
                if (expectedDX < 0) == (predictedDX < 0) {
                    matchingHorizontalDirections += 1
                }
            }
            replaceWorstIfNeeded(
                current: current,
                expectedDX: expectedDX,
                expectedDY: expectedDY,
                predictedDX: predictedDX,
                predictedDY: predictedDY,
                error: error
            )
        }

        func summary() -> LowResolutionTraceMotionSummary {
            LowResolutionTraceMotionSummary(
                comparisons: comparisons,
                representableComparisons: representable,
                acceptedComparisons: accepted,
                predictedAcceptedComparisons: predictedAccepted,
                acceptedRepresentableComparisons: acceptedRepresentable,
                rejectedRepresentableComparisons: rejectedRepresentable,
                directionGateRejections: directionGateRejections,
                translationRejections: translationRejections,
                rejectedTravelByReason: rejectedTravel.mapValues { $0.summary() },
                outOfRangeComparisons: outOfRange,
                acceptedOutOfRangeComparisons: acceptedOutOfRange,
                representableCoveragePercent: representable > 0
                    ? Double(acceptedRepresentable) / Double(representable) * 100 : nil,
                horizontalDirectionAgreementPercent: horizontalDirections > 0
                    ? Double(matchingHorizontalDirections)
                        / Double(horizontalDirections) * 100 : nil,
                vectorErrorPixels: statistics(errors),
                integratedDriftPixels: statistics(integratedErrors),
                horizontalBiasPixels: mean(xErrors),
                verticalBiasPixels: mean(yErrors),
                worstMotion: worst
            )
        }

        private mutating func replaceWorstIfNeeded(
            current: ComparableFrame,
            expectedDX: Double,
            expectedDY: Double,
            predictedDX: Double?,
            predictedDY: Double?,
            error: Double
        ) {
            guard worst.map({ error > $0.errorPixels }) ?? true else { return }
            worst = LowResolutionTraceWorstMotion(
                offset: current.recorded.offset,
                sceneName: current.truth.sceneName,
                expectedDX: expectedDX,
                expectedDY: expectedDY,
                predictedDX: predictedDX,
                predictedDY: predictedDY,
                errorPixels: error
            )
        }
    }

    static func run(arguments: [String]) throws -> LowResolutionTraceEvaluationReport {
        var traceURL: URL?
        var reportURL: URL?
        var worldEvidenceURL: URL?
        var minimumImprovement = LowResolutionRoomMotionTracker.minimumImprovement
        var index = 0
        while index < arguments.count {
            let flag = arguments[index]
            guard [
                "--evaluate-low-resolution-trace",
                "--low-resolution-report",
                "--low-resolution-world-evidence",
                "--low-resolution-minimum-improvement",
            ]
                .contains(flag), index + 1 < arguments.count else {
                throw LowResolutionTraceEvaluatorError.invalidArgument(flag)
            }
            let value = arguments[index + 1]
            if flag == "--evaluate-low-resolution-trace" {
                traceURL = URL(fileURLWithPath: value)
            } else if flag == "--low-resolution-world-evidence" {
                worldEvidenceURL = URL(fileURLWithPath: value, isDirectory: true)
            } else if flag == "--low-resolution-minimum-improvement" {
                guard let parsed = Double(value), parsed.isFinite,
                      parsed >= 0, parsed <= 1 else {
                    throw LowResolutionTraceEvaluatorError.invalidArgument(value)
                }
                minimumImprovement = parsed
            } else {
                reportURL = URL(fileURLWithPath: value)
            }
            index += 2
        }
        guard let traceURL else {
            throw LowResolutionTraceEvaluatorError.missingTracePath
        }
        let path = try InputPathStore(
            rootURL: traceURL.deletingLastPathComponent()
        ).load(from: traceURL)
        let report: LowResolutionTraceEvaluationReport
        if let worldEvidenceURL {
            let frames = try persistedWorldFrames(
                for: path,
                worldRootURL: worldEvidenceURL
            )
            report = evaluate(
                path,
                frames: frames,
                minimumImprovement: minimumImprovement,
                additionalNotes: [
                    "64x36 input was reconstructed from persisted masked atlas PNG evidence.",
                ]
            )
        } else {
            report = evaluate(path, minimumImprovement: minimumImprovement)
        }
        if let reportURL {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            try encoder.encode(report).write(to: reportURL, options: .atomic)
        }
        return report
    }

    static func evaluate(_ path: RecordedInputPath) -> LowResolutionTraceEvaluationReport {
        evaluate(
            path,
            frames: path.lowResolutionFrameTrace,
            minimumImprovement: LowResolutionRoomMotionTracker.minimumImprovement
        )
    }

    static func evaluate(
        _ path: RecordedInputPath,
        minimumImprovement: Double
    ) -> LowResolutionTraceEvaluationReport {
        evaluate(
            path,
            frames: path.lowResolutionFrameTrace,
            minimumImprovement: minimumImprovement
        )
    }

    /// Reconstructs the old recording's low-resolution trace from its durable
    /// atlas PNGs. This is diagnosis only: persisted frames contain the same
    /// masks used for atlas admission, so they are a harder place-recognition
    /// input than the unmasked capture-rate grid used by the live bridge.
    static func persistedWorldFrames(
        for path: RecordedInputPath,
        worldRootURL: URL
    ) throws -> [RecordedLowResolutionFrame] {
        let store = try SceneSessionStore(rootURL: worldRootURL)
        let createdAt = path.createdAt.timeIntervalSinceReferenceDate
        let candidates = store.manifest.observations.filter {
            $0.timestamp >= createdAt - 0.25
                && $0.timestamp <= createdAt + path.duration + 1.5
        }
        guard let first = candidates.first else {
            throw LowResolutionTraceEvaluatorError.missingPersistedWorldEvidence
        }
        let trackingPairs: [(Int, RecordedTrackingSample)] = path.trackingSamples.compactMap {
            sample in
                guard let renderedGameFrame = sample.renderedGameFrame,
                      sample.captureOffset?.isFinite == true else { return nil }
                return (renderedGameFrame, sample)
            }
        let trackingByRenderedFrame = Dictionary(
            trackingPairs,
            uniquingKeysWith: { _, latest in latest }
        )
        guard !trackingByRenderedFrame.isEmpty else {
            throw LowResolutionTraceEvaluatorError.missingPersistedWorldEvidence
        }

        var result = [RecordedLowResolutionFrame]()
        for observation in candidates {
            let offset = observation.timestamp - first.timestamp
            guard offset >= -0.05, offset <= path.duration + 0.25 else { continue }
            guard let image = try? store.source(for: observation),
                  let renderedGameFrame = RenderedFrameMarker.decode(image),
                  let tracking = trackingByRenderedFrame[renderedGameFrame],
                  let trackingOffset = tracking.captureOffset,
                  let grid = FrameRegionRenderer.signalProfile(in: image).motionGrid
            else { continue }
            result.append(RecordedLowResolutionFrame(
                offset: trackingOffset,
                captureTimestamp: tracking.captureTimestamp,
                renderedGameFrame: renderedGameFrame,
                roomID: tracking.roomID ?? 0,
                width: grid.width,
                height: grid.height,
                luma: Data(grid.luma),
                groundTrackingReliable: GroundMotionEvidence.isMeasured(tracking)
            ))
        }
        guard !result.isEmpty else {
            throw LowResolutionTraceEvaluatorError.missingPersistedWorldEvidence
        }
        return result
    }

    private static func evaluate(
        _ path: RecordedInputPath,
        frames: [RecordedLowResolutionFrame],
        minimumImprovement: Double,
        additionalNotes: [String] = []
    ) -> LowResolutionTraceEvaluationReport {
        // Traces recorded before schema 10 called a newly seeded, zero-support
        // ground coordinate reliable. Reconstruct the current production rule
        // from the exact tracking record so old saved pixels remain useful.
        let groundMotionByRenderedFrame = Dictionary(
            path.trackingSamples.compactMap { sample -> (Int, Bool)? in
                guard let frame = sample.renderedGameFrame else { return nil }
                return (frame, GroundMotionEvidence.isMeasured(sample))
            },
            uniquingKeysWith: { _, latest in latest }
        )
        let orderedFrames = frames.map { frame -> RecordedLowResolutionFrame in
            guard frame.groundTrackingReliable,
                  let renderedGameFrame = frame.renderedGameFrame,
                  groundMotionByRenderedFrame[renderedGameFrame] == false else {
                return frame
            }
            var corrected = frame
            corrected.groundTrackingReliable = false
            return corrected
        }.sorted { $0.offset < $1.offset }
        let solveWidth = Double(path.runtimeMetadata?["captureWidth"] ?? "")
            .flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 640
        var truthByFrame = [Int64: ReceiverGroundTruthSample]()
        for recorded in path.groundTruthTrace where recorded.sample.cameraAvailable
            && recorded.sample.hasFiniteCoordinates {
            truthByFrame[recorded.sample.unityFrame] = recorded.sample
        }
        var observedVisionPositionByFrame = [Int: CGPoint]()
        for tracking in path.trackingSamples {
            guard let renderedGameFrame = tracking.renderedGameFrame else { continue }
            let x = tracking.publishedCameraX ?? tracking.cameraX
            let y = tracking.publishedCameraY ?? tracking.cameraY
            guard let x, let y, x.isFinite, y.isFinite else { continue }
            observedVisionPositionByFrame[renderedGameFrame] = CGPoint(x: x, y: y)
        }
        let defaultTraceHeight = orderedFrames.first.map {
            solveWidth * Double($0.height) / Double(max(1, $0.width))
        } ?? solveWidth * 9 / 16
        let connectedCamera = connectedCameraSummary(
            path,
            solveWidth: solveWidth,
            solveHeight: defaultTraceHeight,
            forceTrackingSamples: false
        )
        let connectedTrackingCamera = connectedCameraSummary(
            path,
            solveWidth: solveWidth,
            solveHeight: defaultTraceHeight,
            forceTrackingSamples: true
        )
        let orderedTrackingSamples = path.trackingSamples.sorted {
            $0.captureTimestamp < $1.captureTimestamp
        }
        let reliableVelocitySeeds: [LowResolutionCameraVelocitySeed] = zip(
            orderedTrackingSamples,
            orderedTrackingSamples.dropFirst()
        ).compactMap { pair in
            let (prior, current) = pair
            guard GroundMotionEvidence.isMeasured(prior),
                  GroundMotionEvidence.isMeasured(current),
                  prior.poseSource == "ground", current.poseSource == "ground",
                  prior.roomID == current.roomID,
                  prior.roomRevision == current.roomRevision,
                  current.globalCorrectionX == nil,
                  current.globalCorrectionY == nil,
                  let oldX = prior.publishedCameraX ?? prior.cameraX,
                  let oldY = prior.publishedCameraY ?? prior.cameraY,
                  let newX = current.publishedCameraX ?? current.cameraX,
                  let newY = current.publishedCameraY ?? current.cameraY else {
                return nil
            }
            let velocity = PresentationPosePrediction.velocity(
                from: CGPoint(x: oldX, y: oldY),
                at: prior.captureTimestamp,
                to: CGPoint(x: newX, y: newY),
                at: current.captureTimestamp,
                continuous: true
            )
            guard velocity.dx != 0 || velocity.dy != 0 else { return nil }
            return LowResolutionCameraVelocitySeed(
                velocity: velocity,
                timestamp: current.captureTimestamp
            )
        }

        var comparable = [ComparableFrame]()
        comparable.reserveCapacity(frames.count)
        var validFrameCount = 0
        let events = path.events.sorted { $0.offset < $1.offset }
        var eventIndex = 0
        var leftHeld = false
        var rightHeld = false
        for frame in orderedFrames {
            while eventIndex < events.count,
                  events[eventIndex].offset <= frame.offset {
                let event = events[eventIndex]
                if event.button == .left {
                    leftHeld = event.transition == .pressed
                } else if event.button == .right {
                    rightHeld = event.transition == .pressed
                }
                eventIndex += 1
            }
            let expectedDirection: VisualRoomDirection? = leftHeld == rightHeld
                ? nil : (leftHeld ? .left : .right)
            guard frame.width > 0, frame.height > 0,
                  frame.luma.count == frame.width * frame.height else { continue }
            validFrameCount += 1
            guard let renderedGameFrame = frame.renderedGameFrame,
                  let truth = truthByFrame[Int64(renderedGameFrame)],
                  let truthPosition = truthPosition(
                    truth, solveWidth: solveWidth,
                    solveHeight: solveWidth * Double(frame.height) / Double(frame.width)
                  ) else { continue }
            comparable.append(ComparableFrame(
                recorded: frame,
                grid: LowResolutionMotionGrid(
                    width: frame.width,
                    height: frame.height,
                    luma: Array(frame.luma)
                ),
                truth: truth,
                truthPosition: truthPosition,
                observedVisionPosition: observedVisionPositionByFrame[renderedGameFrame],
                pixelScale: solveWidth / Double(frame.width),
                expectedDirection: expectedDirection
            ))
        }

        var allMotion = MotionAccumulator()
        var groundLossMotion = MotionAccumulator()
        var groundLossBridgedMotion = MotionAccumulator()
        var productionOdometry = LowResolutionTransitionOdometry()
        var groundLossBridgeEvents = [LowResolutionTraceBridgeEvent]()
        var groundLossEvents = [LowResolutionTraceGroundLossEvent]()
        var latestReliableVelocitySeed: LowResolutionCameraVelocitySeed?
        var reliableVelocitySeedIndex = 0
        var previous: ComparableFrame?
        var placeMemory = LowResolutionPlaceMemory()
        var placeErrors = [Double]()

        for current in comparable {
            while reliableVelocitySeedIndex < reliableVelocitySeeds.count,
                  reliableVelocitySeeds[reliableVelocitySeedIndex].timestamp
                    <= current.recorded.captureTimestamp {
                latestReliableVelocitySeed = reliableVelocitySeeds[
                    reliableVelocitySeedIndex
                ]
                reliableVelocitySeedIndex += 1
            }
            if current.recorded.groundTrackingReliable {
                placeMemory.remember(
                    grid: current.grid,
                    roomID: current.recorded.roomID,
                    cameraPosition: current.truthPosition,
                    timestamp: current.recorded.captureTimestamp
                )
            } else if let match = placeMemory.observe(
                grid: current.grid,
                roomID: current.recorded.roomID,
                solveWidth: solveWidth,
                timestamp: current.recorded.captureTimestamp
            ) {
                placeErrors.append(hypot(
                    match.cameraPosition.x - current.truthPosition.x,
                    match.cameraPosition.y - current.truthPosition.y
                ))
            }

            defer { previous = current }
            guard let reference = previous,
                  comparableEpoch(reference, current),
                  reference.recorded.roomID == current.recorded.roomID else {
                allMotion.resetIntegration()
                groundLossMotion.resetIntegration()
                groundLossBridgedMotion.resetIntegration()
                productionOdometry.reset()
                continue
            }
            let age = current.recorded.captureTimestamp
                - reference.recorded.captureTimestamp
            guard LowResolutionRoomMotionTracker.comparisonAge.contains(age) else {
                allMotion.resetIntegration()
                groundLossMotion.resetIntegration()
                groundLossBridgedMotion.resetIntegration()
                productionOdometry.reset()
                continue
            }
            let expectedDX = current.truthPosition.x - reference.truthPosition.x
            let expectedDY = current.truthPosition.y - reference.truthPosition.y
            let expectedShiftX = -expectedDX / current.pixelScale
            let expectedShiftY = expectedDY / current.pixelScale
            let representable = abs(expectedShiftX)
                    <= Double(LowResolutionRoomMotionTracker.maximumShift)
                && abs(expectedShiftY)
                    <= Double(LowResolutionRoomMotionTracker.maximumVerticalShift)
            let diagnostic = LowResolutionRoomMotionTracker.diagnoseTranslation(
                from: reference.grid,
                to: current.grid,
                requiredImprovement: minimumImprovement
            )
            let rawMotion = diagnostic.motion
            let rejectedByDirectionGate = rawMotion.map {
                !LowResolutionTransitionOdometry.acceptsDirection(
                    $0,
                    expectedDirection: current.expectedDirection
                )
            } ?? false
            let motion = rejectedByDirectionGate ? nil : rawMotion
            allMotion.append(
                current: current,
                expectedDX: expectedDX,
                expectedDY: expectedDY,
                motion: motion,
                isRepresentable: representable,
                rejectedByDirectionGate: rejectedByDirectionGate,
                translationRejection: diagnostic.finalRejection
            )
            if !current.recorded.groundTrackingReliable {
                if reference.recorded.groundTrackingReliable {
                    groundLossMotion.resetIntegration()
                    groundLossBridgedMotion.resetIntegration()
                    productionOdometry.arm(
                        grid: reference.grid,
                        timestamp: reference.recorded.captureTimestamp
                    )
                    productionOdometry.primeHorizontalVelocity(
                        latestReliableVelocitySeed,
                        expectedDirection: current.expectedDirection
                    )
                }
                groundLossMotion.append(
                    current: current,
                    expectedDX: expectedDX,
                    expectedDY: expectedDY,
                    motion: motion,
                    isRepresentable: representable,
                    rejectedByDirectionGate: rejectedByDirectionGate,
                    translationRejection: diagnostic.finalRejection
                )
                let beforeX = productionOdometry.worldDeltaX
                let beforeY = productionOdometry.worldDeltaY
                let bridgedStep = productionOdometry.observe(
                    grid: current.grid,
                    timestamp: current.recorded.captureTimestamp,
                    solveWidth: solveWidth,
                    expectedDirection: current.expectedDirection
                )
                let bridgedMotion = bridgedStep.map { _ in
                    LowResolutionMotionVector(
                        screenShiftX: -Double(
                            productionOdometry.worldDeltaX - beforeX
                        ) / current.pixelScale,
                        screenShiftY: Double(
                            productionOdometry.worldDeltaY - beforeY
                        ) / current.pixelScale,
                        confidence: 0
                    )
                }
                groundLossEvents.append(LowResolutionTraceGroundLossEvent(
                    offset: current.recorded.offset,
                    sceneName: current.truth.sceneName,
                    expectedDirection: current.expectedDirection.map {
                        $0 == .left ? "left" : "right"
                    },
                    rejection: rejectedByDirectionGate
                        ? "directionGate" : diagnostic.finalRejection?.rawValue,
                    expectedDX: expectedDX,
                    expectedDY: expectedDY,
                    measuredDX: motion.map {
                        -$0.screenShiftX * current.pixelScale
                    },
                    measuredDY: motion.map {
                        $0.screenShiftY * current.pixelScale
                    },
                    bridgedDX: bridgedMotion.map {
                        -$0.screenShiftX * current.pixelScale
                    },
                    bridgedDY: bridgedMotion.map {
                        $0.screenShiftY * current.pixelScale
                    }
                ))
                if motion == nil {
                    groundLossBridgeEvents.append(LowResolutionTraceBridgeEvent(
                        offset: current.recorded.offset,
                        sceneName: current.truth.sceneName,
                        expectedDirection: current.expectedDirection.map {
                            $0 == .left ? "left" : "right"
                        },
                        rejection: rejectedByDirectionGate
                            ? "directionGate"
                            : (diagnostic.finalRejection?.rawValue ?? "unknown"),
                        expectedDX: expectedDX,
                        expectedDY: expectedDY,
                        bridgedDX: bridgedMotion.map {
                            -$0.screenShiftX * current.pixelScale
                        },
                        bridgedDY: bridgedMotion.map {
                            $0.screenShiftY * current.pixelScale
                        }
                    ))
                }
                groundLossBridgedMotion.append(
                    current: current,
                    expectedDX: expectedDX,
                    expectedDY: expectedDY,
                    motion: bridgedMotion,
                    isRepresentable: representable,
                    rejectedByDirectionGate: bridgedMotion == nil
                        && rejectedByDirectionGate,
                    translationRejection: bridgedMotion == nil
                        ? diagnostic.finalRejection : nil,
                    isPredicted: bridgedMotion != nil && motion == nil
                )
            } else {
                groundLossMotion.resetIntegration()
                groundLossBridgedMotion.resetIntegration()
                productionOdometry.reset()
            }
        }

        return LowResolutionTraceEvaluationReport(
            schemaVersion: LowResolutionTraceEvaluationReport.currentSchemaVersion,
            pathID: path.id,
            replayIteration: path.replayIteration,
            traceFrames: frames.count,
            validTraceFrames: validFrameCount,
            exactTruthFrames: comparable.count,
            exactTruthCoveragePercent: validFrameCount > 0
                ? Double(comparable.count) / Double(validFrameCount) * 100 : nil,
            groundReliableFrames: comparable.count {
                $0.recorded.groundTrackingReliable
            },
            groundUnverifiedFrames: comparable.count {
                !$0.recorded.groundTrackingReliable
            },
            traceIntervalSeconds: statistics(zip(
                comparable.dropFirst(), comparable
            ).map {
                $0.0.recorded.captureTimestamp - $0.1.recorded.captureTimestamp
            }.filter { $0.isFinite && $0 >= 0 }),
            evaluable: comparable.count >= 2,
            solveWidth: solveWidth,
            motionMinimumImprovement: minimumImprovement,
            allMotion: allMotion.summary(),
            groundLossMotion: groundLossMotion.summary(),
            groundLossBridgedMotion: groundLossBridgedMotion.summary(),
            groundLossBridgeEvents: groundLossBridgeEvents,
            groundLossEvents: groundLossEvents,
            oracleSeededPlaceRecognition: LowResolutionTracePlaceSummary(
                matches: placeErrors.count,
                matchesOver24Pixels: placeErrors.count { $0 > 24 },
                errorPixels: statistics(placeErrors)
            ),
            connectedCamera: connectedCamera,
            connectedTrackingCamera: connectedTrackingCamera,
            notes: [
                "Motion truth uses exact rendered Unity frames; no nearest-time substitution.",
                "Place memory is seeded with Hacker camera poses on frames marked ground-reliable.",
                "Connected-camera error keeps one route-start baseline across scene changes.",
                "connectedCamera is capture-rate presentation; connectedTrackingCamera is the atlas-producing Vision stream."
            ] + additionalNotes
        )
    }

    private static func connectedCameraSummary(
        _ path: RecordedInputPath,
        solveWidth: Double,
        solveHeight: Double,
        forceTrackingSamples: Bool
    ) -> LowResolutionTraceConnectedCameraSummary {
        struct PresentedFrame {
            let offset: TimeInterval
            let renderedGameFrame: Int
            let position: CGPoint
        }
        let frameSize = CGSize(width: solveWidth, height: solveHeight)
        var connector = ConnectedGroundTruthCameraTracker()
        var truthByFrame = [Int: ConnectedGroundTruthCameraSample]()
        for recorded in path.groundTruthTrace.sorted(by: { $0.offset < $1.offset }) {
            if let connected = connector.observe(
                recorded.sample,
                observedAt: recorded.receivedTimestamp,
                frameSize: frameSize
            ) {
                truthByFrame[connected.frameKey] = connected
            }
        }
        var baseline: CGPoint?
        var firstScene: String?
        var scenes = Set<String>()
        var errors = [Double]()
        var errorsByScene = [String: [Double]]()
        var joinedErrors = [Double]()
        var thresholdOffsets = [String: TimeInterval]()
        var maximumError: (value: Double, offset: TimeInterval, scene: String)?
        var joinedSceneFirstOffset: TimeInterval?
        var joinedSceneFirstErrorX: Double?
        var joinedSceneFirstErrorY: Double?
        var endErrorX: Double?
        var endErrorY: Double?
        let source: String
        let presentedFrames: [PresentedFrame]
        if forceTrackingSamples || path.coarseMotionTrace.isEmpty {
            source = "published-tracking"
            presentedFrames = path.trackingSamples.compactMap { sample in
                guard let frame = sample.renderedGameFrame,
                      let x = sample.publishedCameraX,
                      let y = sample.publishedCameraY,
                      x.isFinite, y.isFinite else { return nil }
                return PresentedFrame(
                    offset: sample.captureOffset ?? sample.offset,
                    renderedGameFrame: frame,
                    position: CGPoint(x: x, y: y)
                )
            }
        } else {
            source = "capture-rate-coarse"
            presentedFrames = path.coarseMotionTrace.compactMap { sample in
                guard let frame = sample.renderedGameFrame,
                      sample.presentedCameraX.isFinite,
                      sample.presentedCameraY.isFinite else { return nil }
                return PresentedFrame(
                    offset: sample.offset,
                    renderedGameFrame: frame,
                    position: CGPoint(
                        x: sample.presentedCameraX,
                        y: sample.presentedCameraY
                    )
                )
            }
        }
        for sample in presentedFrames.sorted(by: { $0.offset < $1.offset }) {
            guard let truth = truthByFrame[sample.renderedGameFrame] else { continue }
            let presented = sample.position
            if baseline == nil {
                baseline = CGPoint(
                    x: presented.x - truth.cameraPosition.x,
                    y: presented.y - truth.cameraPosition.y
                )
                firstScene = truth.sceneName
            }
            guard let baseline else { continue }
            let x = presented.x - baseline.x - truth.cameraPosition.x
            let y = presented.y - baseline.y - truth.cameraPosition.y
            let error = Double(hypot(x, y))
            errors.append(error)
            errorsByScene[truth.sceneName, default: []].append(error)
            scenes.insert(truth.sceneName)
            if truth.sceneName != firstScene {
                joinedErrors.append(error)
                if joinedSceneFirstOffset == nil {
                    joinedSceneFirstOffset = sample.offset
                    joinedSceneFirstErrorX = x
                    joinedSceneFirstErrorY = y
                }
            }
            for threshold in [8, 16, 32, 64, 128, 256] where error > Double(threshold) {
                let key = String(threshold)
                if thresholdOffsets[key] == nil {
                    thresholdOffsets[key] = sample.offset
                }
            }
            if maximumError == nil || error > maximumError!.value {
                maximumError = (error, sample.offset, truth.sceneName)
            }
            endErrorX = x
            endErrorY = y
        }
        return LowResolutionTraceConnectedCameraSummary(
            source: source,
            exactFramePairs: errors.count,
            sceneNames: scenes.sorted(),
            errorPixels: statistics(errors),
            sceneErrorPixels: errorsByScene.mapValues { statistics($0) },
            joinedSceneErrorPixels: statistics(joinedErrors),
            firstErrorOverPixelsAtOffset: thresholdOffsets,
            maximumErrorOffset: maximumError?.offset,
            maximumErrorScene: maximumError?.scene,
            joinedSceneFirstOffset: joinedSceneFirstOffset,
            joinedSceneFirstErrorX: joinedSceneFirstErrorX,
            joinedSceneFirstErrorY: joinedSceneFirstErrorY,
            endErrorX: endErrorX,
            endErrorY: endErrorY
        )
    }

    private static func comparableEpoch(
        _ first: ComparableFrame,
        _ second: ComparableFrame
    ) -> Bool {
        first.truth.sessionID == second.truth.sessionID
            && first.truth.sceneName == second.truth.sceneName
            && first.truth.projectionPixelWidth == second.truth.projectionPixelWidth
            && first.truth.projectionPixelHeight == second.truth.projectionPixelHeight
            && abs(first.truth.orthographicSize - second.truth.orthographicSize) < 0.000_001
    }

    private static func truthPosition(
        _ truth: ReceiverGroundTruthSample,
        solveWidth: Double,
        solveHeight: Double
    ) -> CGPoint? {
        guard truth.cameraAvailable, solveWidth > 0, solveHeight > 0 else { return nil }
        let fallback = truth.pixelsPerWorldUnit(frameHeight: Int(solveHeight.rounded()))
        let xScale: Double
        if let pixelsPerWorldUnitX = truth.pixelsPerWorldUnitX,
           let projectionPixelWidth = truth.projectionPixelWidth,
           pixelsPerWorldUnitX > 0, projectionPixelWidth > 0 {
            xScale = pixelsPerWorldUnitX * solveWidth / Double(projectionPixelWidth)
        } else if let fallback {
            xScale = fallback
        } else {
            return nil
        }
        guard let yScale = truth.pixelsPerWorldUnit(frameHeight: Int(solveHeight.rounded())),
              xScale.isFinite, yScale.isFinite else { return nil }
        return CGPoint(x: truth.cameraX * xScale, y: truth.cameraY * yScale)
    }

    private static func statistics(
        _ values: [Double]
    ) -> LowResolutionTraceErrorStatistics {
        guard !values.isEmpty else {
            return LowResolutionTraceErrorStatistics(
                count: 0, rms: nil, p50: nil, p95: nil, maximum: nil
            )
        }
        let sorted = values.sorted()
        let rms = sqrt(values.reduce(0) { $0 + $1 * $1 } / Double(values.count))
        return LowResolutionTraceErrorStatistics(
            count: values.count,
            rms: rms,
            p50: percentile(sorted, 0.50),
            p95: percentile(sorted, 0.95),
            maximum: sorted.last
        )
    }

    private static func percentile(_ sorted: [Double], _ fraction: Double) -> Double {
        let index = Int((Double(sorted.count - 1) * fraction).rounded())
        return sorted[max(0, min(sorted.count - 1, index))]
    }

    private static func mean(_ values: [Double]) -> Double? {
        values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
    }
}
