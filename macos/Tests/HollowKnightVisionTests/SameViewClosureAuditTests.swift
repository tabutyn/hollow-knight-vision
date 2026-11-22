import CoreGraphics
import CoreImage
import Foundation
import XCTest
@testable import HollowKnightVision

/// Image-only diagnosis of a missed return under moving player illumination.
/// This audit neither accepts graph edges nor reads game telemetry.
final class SameViewClosureAuditTests: XCTestCase {
    func testInspectSavedSameViewReturn() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let input = environment["HKV_SAME_VIEW_AUDIT_INPUT"],
              let output = environment["HKV_SAME_VIEW_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set HKV_SAME_VIEW_AUDIT_INPUT/OUTPUT for the saved-view audit")
        }
        let store = try LiveWorldSessionStore(rootURL:
            URL(fileURLWithPath: input).appendingPathComponent("world"))
        let world = try store.load().snapshot
        var reader: VisualReplayArchiveReader?
        if let recording = environment["HKV_SAME_VIEW_AUDIT_RECORDING"] {
            struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
            let path = URL(fileURLWithPath: recording)
            let recorded = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
            reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
                summary: recorded.visualFrameArchive, expectedPathID: recorded.id)
        }
        let source = try XCTUnwrap(world.observations.first { $0.id == 0 })
        let image = try store.source(for: source.sourceObservationID)
        let reference = try XCTUnwrap(GlobalFeatureMatcher().makeReference(
            frame: image, cameraPose: source.optimizedPose, solveWidth: source.solveWidth,
            excluding: source.excludedRects))
        let sourceGrid = try grid(image, exclusions: source.excludedRects)
        var rows = [[String: Any]]()
        for targetID in [1, 3, 13, 42, 46, 47, 132, 343, 622] {
            let target = try XCTUnwrap(world.observations.first { $0.id == targetID })
            let current = try store.source(for: target.sourceObservationID)
            let currentGrid = try grid(current, exclusions: target.excludedRects)
            let points = reference.features.map(\.samplePoint)
            let fits = CalibratedSceneMotionTracker.fitBatch(sourceGrid, currentGrid,
                points: points, backend: .automatic)
            let matches = zip(reference.features, fits).compactMap { feature, fit in
                fit.map { (feature, $0) }
            }
            var row: [String: Any] = ["sourceID": source.id, "targetID": target.id,
                "patchMatches": matches.count, "acceptedEdge": false, "floorVerified": false,
                "confirmedGroundSpans": target.groundGeometry?.lines.count ?? 0]
            if matches.count >= 6 {
                let motion = CGVector(dx: median(matches.map { $0.1.motion.dx }),
                                      dy: median(matches.map { $0.1.motion.dy }))
                let agreeing = matches.filter {
                    hypot($0.1.motion.dx - motion.dx, $0.1.motion.dy - motion.dy) <= 0.65
                }
                row["consensus"] = agreeing.count
                if agreeing.count >= 6 {
                    let xs = agreeing.map { $0.0.samplePoint.x }
                    let ys = agreeing.map { $0.0.samplePoint.y }
                    let spanX = xs.max()! - xs.min()!, spanY = ys.max()! - ys.min()!
                    row["spanX"] = spanX; row["spanY"] = spanY
                    row["meanCorrelation"] = agreeing.map { $0.1.correlation }.reduce(0, +)
                        / Double(agreeing.count)
                    if spanX >= 20, spanY >= 18,
                       let sourceGround = source.groundGeometry {
                        let scale = source.solveWidth / Double(sourceGrid.width)
                        let proposal = CGPoint(
                            x: source.optimizedPose.x - median(agreeing.map { $0.1.motion.dx }) * scale,
                            y: source.optimizedPose.y + median(agreeing.map { $0.1.motion.dy }) * scale)
                        let verified = target.groundGeometry.flatMap { targetGround in
                            GroundPlaneClosureVerifier.verify(reference: image,
                                referenceGround: sourceGround, referencePose: source.optimizedPose,
                                current: current, currentGround: targetGround,
                                proposedPose: proposal, solveWidth: source.solveWidth)
                        }
                        row["floorVerified"] = verified != nil
                        if let verified {
                            row["cameraDelta"] = [verified.cameraPose.x - source.optimizedPose.x,
                                                   verified.cameraPose.y - source.optimizedPose.y]
                            row["floorSupport"] = verified.supportedPatches
                            row["sourceLandmarkIDs"] = agreeing.map { $0.0.id }
                        }
                        // Raw detected spans are diagnostic candidates only.
                        // They are never saved as confirmed atlas geometry.
                        if let reader, let timestamp = target.captureTimestamp,
                           let frame = reader.manifest.frames.first(where: { $0.captureTimestamp == timestamp }),
                           let input = reader.manifest.groundInputs.first(where: { $0.captureTimestamp == timestamp }) {
                            let captured = try reader.image(for: frame)
                            let analysis = GroundLineDetector.analyze(captured, excluding: input.excludingRects)
                            let theory = analysis.map(GroundLineDetector.compare).flatMap {
                                GroundLineDetector.theory(from: $0, tuning: input.tuning)
                            }
                            let lines = GroundLineDetector.rejectingKnownForegroundLines(
                                theory.map(GroundLineDetector.cleanFloorLines) ?? [], imageHeight: captured.height,
                                foregroundRects: input.excludingRects, knightRect: input.knightRect)
                            let scale = CGFloat(source.solveWidth) / CGFloat(captured.width)
                            let evidence = lines.enumerated().flatMap { index, line in
                                line.evidenceRanges.filter { $0.upperBound - $0.lowerBound >= 16 }.map { range in
                                    LiveGroundLineEvidence(segmentID: index,
                                        start: CGPoint(x: CGFloat(range.lowerBound) * scale,
                                            y: CGFloat(captured.height - line.row) * scale),
                                        end: CGPoint(x: CGFloat(range.upperBound + 1) * scale,
                                            y: CGFloat(captured.height - line.row) * scale))
                                }
                            }
                            row["rawDetectedSpans"] = evidence.count
                            let candidate = GroundPlaneClosureVerifier.verify(reference: image,
                                referenceGround: sourceGround, referencePose: source.optimizedPose,
                                current: current, currentGround: .init(lines: evidence),
                                proposedPose: proposal, solveWidth: source.solveWidth)
                            row["candidateFloorVerified"] = candidate != nil
                            if let candidate {
                                row["candidateCameraDelta"] = [candidate.cameraPose.x - source.optimizedPose.x,
                                                               candidate.cameraPose.y - source.optimizedPose.y]
                                row["candidateFloorSupport"] = candidate.supportedPatches
                            }
                        }
                    }
                }
            }
            rows.append(row)
        }
        try JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        XCTAssertEqual(try store.load().snapshot, world)
        print("Same-view audit saved \(rows.count) image-only comparisons; no graph mutation")
    }

    func testRecordedReturnUsesTwoFreshFloorVotesThroughProductionPipeline() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let input = environment["HKV_SAME_VIEW_AUDIT_INPUT"],
              let recording = environment["HKV_SAME_VIEW_AUDIT_RECORDING"] else {
            throw XCTSkip("Set saved-view audit paths for the recorded production regression")
        }
        let store = try LiveWorldSessionStore(rootURL:
            URL(fileURLWithPath: input).appendingPathComponent("world"))
        let original = try store.load().snapshot
        // Decode archive identity only. Reference poses/telemetry never enter this test.
        struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
        let path = URL(fileURLWithPath: recording)
        let recorded = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: recorded.visualFrameArchive, expectedPathID: recorded.id)
        let observations = try [0, 46, 47].map { id in
            try XCTUnwrap(original.observations.first { $0.id == id })
        }
        let images = try observations.map { try store.source(for: $0.sourceObservationID) }
        let floorCandidates = try observations.map { observation -> LiveObservedFloorCandidates? in
            let timestamp = try XCTUnwrap(observation.captureTimestamp)
            let frame = try XCTUnwrap(reader.manifest.frames.first { $0.captureTimestamp == timestamp })
            let input = try XCTUnwrap(reader.manifest.groundInputs.first { $0.captureTimestamp == timestamp })
            let image = try reader.image(for: frame)
            let analysis = GroundLineDetector.analyze(image, excluding: input.excludingRects)
            let theory = analysis.map(GroundLineDetector.compare).flatMap {
                GroundLineDetector.theory(from: $0, tuning: input.tuning)
            }
            let lines = GroundLineDetector.rejectingKnownForegroundLines(
                theory.map(GroundLineDetector.cleanFloorLines) ?? [], imageHeight: image.height,
                foregroundRects: input.excludingRects, knightRect: input.knightRect)
            return LiveObservedFloorCandidates.captured(detected: lines,
                frameSize: CGSize(width: image.width, height: image.height),
                solveWidth: observation.solveWidth, measured: true)
        }
        XCTAssertNil(observations[1].groundGeometry)
        XCTAssertNil(observations[2].groundGeometry)
        XCTAssertNotNil(floorCandidates[1]); XCTAssertNotNil(floorCandidates[2])
        let context = CIContext(options: [.useSoftwareRenderer: true])
        for provideCandidates in [false, true] {
            let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: root) }
            let pipeline = LiveAtlasPipeline(context: context, atlasFramesPerSecond: 60,
                runsIntegrationSynchronously: true, worldRootURL: root)
            pipeline.setIslandClosureSearchAllowed { true }
            var updates = [LiveWorldPipelineUpdate]()
            pipeline.setWorldUpdateHandler { updates.append($0) }
            let anchor = observations[0].optimizedPose
            for index in observations.indices {
                let source = observations[index]
                let time = try XCTUnwrap(source.captureTimestamp)
                let drift = index == 0 ? CGPoint.zero : CGPoint(x: 15, y: 6)
                let pose = CGPoint(x: anchor.x + drift.x, y: anchor.y + drift.y)
                let capture = LiveIslandCapture(id: index == 0 ? 0 : 1, roomID: 0,
                    startedAt: index == 0 ? observations[0].timestamp : observations[1].timestamp,
                    worldFromLocal: pose, localPose: .zero,
                    placement: index == 0 ? .reference : .estimated,
                    uncertainty: index == 0 ? .reference : .unknown, breakReason: "gap", measured: true)
                let regions = SceneRegions(knight: nil, health: .zero, geo: .zero, mana: .zero,
                    hudStencilRects: source.excludedRects)
                XCTAssertTrue(pipeline.submitAtlasObservation(frame: images[index], regions: regions,
                    cameraPosition: pose, solveWidth: source.solveWidth, timestamp: time,
                    observationPoseTimestamp: time, hasGameplaySignal: true, captureGeneration: 1,
                    worldTimestamp: source.timestamp, submittedAt: time, groundAnchoredPose: true,
                    islandCapture: capture, groundGeometry: index == 0 ? source.groundGeometry : nil,
                    observedFloorCandidates: provideCandidates ? floorCandidates[index] : nil))
                if index == 1 {
                    XCTAssertTrue(pipeline.currentWorldSnapshot().loopClosureEdges.isEmpty)
                    XCTAssertNil(updates.last?.acceptedClosure)
                }
            }
            XCTAssertLessThan(observations[2].timestamp - observations[1].timestamp, 0.5)
            let result = try XCTUnwrap(updates.last)
            XCTAssertEqual(result.snapshot.observations.count, 3)
            XCTAssertEqual(result.snapshot.loopClosureEdges.count, provideCandidates ? 1 : 0)
            if provideCandidates {
                XCTAssertNotNil(result.acceptedClosure)
                for observation in result.snapshot.observations.dropFirst() {
                    XCTAssertEqual(observation.optimizedPose.x, anchor.x, accuracy: 1)
                    XCTAssertEqual(observation.optimizedPose.y, anchor.y, accuracy: 1)
                    XCTAssertEqual(observation.rawPose.x, anchor.x + 15)
                    XCTAssertNil(observation.groundGeometry)
                    XCTAssertNotNil(observation.observedFloorCandidates)
                }
                XCTAssertFalse(result.atlasTiles.isEmpty)
            }
            // Verifying raw floor candidates must never mint confirmed atlas ground.
            XCTAssertEqual(result.snapshot.ownedGroundLines.count,
                observations[0].groundGeometry?.lines.count)
            XCTAssertTrue(result.snapshot.doorwayFloorConstraints.isEmpty)
            XCTAssertEqual(try LiveWorldSessionStore(rootURL: root).load().snapshot, result.snapshot)
            XCTAssertNil(pipeline.worldPersistenceIssue)
        }
        XCTAssertEqual(try store.load().snapshot, original)
    }

    func testCompareConfirmedAndRawFloorEvidenceWhenRequested() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let input = environment["HKV_FLOOR_PRIORITY_AUDIT_INPUT"],
              let output = environment["HKV_FLOOR_PRIORITY_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set floor priority audit input/output")
        }
        let store = try LiveWorldSessionStore(rootURL:
            URL(fileURLWithPath: input).appendingPathComponent("world"))
        let world = try store.load().snapshot
        var reader: VisualReplayArchiveReader?
        if let recording = environment["HKV_SAME_VIEW_AUDIT_RECORDING"] {
            struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
            let path = URL(fileURLWithPath: recording)
            let recorded = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
            reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
                summary: recorded.visualFrameArchive, expectedPathID: recorded.id)
        }
        func originalImage(_ observation: LiveWorldObservation, fallback: CGImage) throws -> CGImage {
            guard let reader, let timestamp = observation.captureTimestamp,
                  let frame = reader.manifest.frames.first(where: { $0.captureTimestamp == timestamp }) else { return fallback }
            return try reader.image(for: frame)
        }
        var rows = [[String: Any]]()
        let defaultPairs: [(Int, Int)] = [(146, 647), (146, 648), (204, 691), (204, 692),
                                    (353, 775), (353, 776), (407, 791), (407, 792),
                                    (171, 641), (171, 645), (171, 646), (146, 646), (153, 646),
                                    (64, 65), (64, 66), (68, 69), (68, 70),
                                    (71, 72), (71, 73), (74, 75), (74, 76),
                                    (84, 85), (84, 86), (175, 176), (175, 177),
                                    (327, 328), (327, 329), (339, 340), (339, 341),
                                    (342, 343), (342, 344), (342, 345)]
        let configuredPairs = try environment["HKV_FLOOR_PRIORITY_PAIRS"].map {
            try JSONDecoder().decode([[Int]].self, from: Data($0.utf8))
        }
        let pairs = try configuredPairs?.prefix(256).map { pair -> (Int, Int) in
            guard pair.count == 2 else { throw NSError(domain: "invalid-pair", code: 1) }
            return (pair[0], pair[1])
        } ?? defaultPairs
        for (sourceID, targetID) in pairs {
            let source = try XCTUnwrap(world.observations.first { $0.id == sourceID })
            let target = try XCTUnwrap(world.observations.first { $0.id == targetID })
            let referenceImage = try store.source(for: source.sourceObservationID)
            let current = try store.source(for: target.sourceObservationID)
            let reference = try XCTUnwrap(GlobalFeatureMatcher().makeReference(frame: try originalImage(source, fallback: referenceImage),
                cameraPose: source.optimizedPose, solveWidth: source.solveWidth,
                excluding: source.excludedRects))
            let matcher = GlobalFeatureMatcher()
            let match = matcher.match(frame: try originalImage(target, fallback: current), references: [reference],
                currentSolveWidth: target.solveWidth, excluding: target.excludedRects)
                ?? LivePatchClosureMatcher().match(frame: current, solveWidth: target.solveWidth,
                    excluding: target.excludedRects,
                    references: [.init(observation: source, reference: reference)],
                    sourceProvider: { try? store.source(for: $0) })
            var row: [String: Any] = ["sourceID": sourceID, "targetID": targetID,
                "sceneryMatch": match != nil]
            if let match, let sourceFloor = source.groundGeometry {
                row["proposalDelta"] = [match.proposedCameraPose.x - source.optimizedPose.x,
                                        match.proposedCameraPose.y - source.optimizedPose.y]
                for (name, geometry) in [("confirmed", target.groundGeometry),
                                        ("raw", target.observedFloorCandidates?.verificationGeometry)] {
                    let result = geometry.flatMap { geometry in
                        GroundPlaneClosureVerifier.verify(reference: referenceImage,
                            referenceGround: sourceFloor, referencePose: source.optimizedPose,
                            current: current, currentGround: geometry,
                            proposedPose: match.proposedCameraPose, solveWidth: source.solveWidth)
                    }
                    row[name + "Verified"] = result != nil
                    if let result {
                        row[name + "Support"] = result.supportedPatches
                        row[name + "Delta"] = [result.cameraPose.x - source.optimizedPose.x,
                                                result.cameraPose.y - source.optimizedPose.y]
                    }
                }
            }
            if let match, let sourceFloor = source.groundGeometry,
               let geometry = target.observedFloorCandidates?.verificationGeometry ?? target.groundGeometry,
               let text = environment["HKV_FLOOR_PRIORITY_Y_OFFSETS"] {
                let offsets = try JSONDecoder().decode([Double].self, from: Data(text.utf8))
                var variants = [[String: Any]]()
                for offset in offsets.prefix(21) where offset.isFinite && abs(offset) <= 1 {
                    let shift = offset * source.solveWidth / 640
                    let shifted = LiveWorldGroundGeometry(lines: geometry.lines.map {
                        .init(segmentID: $0.segmentID,
                            start: CGPoint(x: $0.start.x, y: $0.start.y + shift),
                            end: CGPoint(x: $0.end.x, y: $0.end.y + shift))
                    })
                    let result = GroundPlaneClosureVerifier.verify(reference: referenceImage,
                        referenceGround: sourceFloor, referencePose: source.optimizedPose,
                        current: current, currentGround: shifted,
                        proposedPose: match.proposedCameraPose, solveWidth: source.solveWidth)
                    var variant: [String: Any] = ["rowOffset": offset, "verified": result != nil]
                    if let result {
                        variant["support"] = result.supportedPatches
                        variant["correlation"] = result.meanCorrelation
                        variant["delta"] = [result.cameraPose.x - source.optimizedPose.x,
                                            result.cameraPose.y - source.optimizedPose.y]
                    }
                    variants.append(variant)
                }
                row["subpixelFloorVariants"] = variants
            }
            rows.append(row)
        }
        try JSONSerialization.data(withJSONObject: rows, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        XCTAssertEqual(try store.load().snapshot, world)
    }

    func testInspectRecordedClosureSearchCadenceWhenRequested() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let input = environment["HKV_CLOSURE_CADENCE_INPUT"],
              let recording = environment["HKV_SAME_VIEW_AUDIT_RECORDING"],
              let output = environment["HKV_CLOSURE_CADENCE_OUTPUT"] else {
            throw XCTSkip("Set closure cadence audit paths")
        }
        let store = try LiveWorldSessionStore(rootURL:
            URL(fileURLWithPath: input).appendingPathComponent("world"))
        let world = try store.load().snapshot
        struct Source: Decodable { let id: UUID; let visualFrameArchive: RecordedVisualFrameArchive }
        let path = URL(fileURLWithPath: recording)
        let recorded = try JSONDecoder().decode(Source.self, from: Data(contentsOf: path))
        let reader = try VisualReplayArchiveReader(rootURL: path.deletingLastPathComponent(),
            summary: recorded.visualFrameArchive, expectedPathID: recorded.id)
        let frames = Dictionary(uniqueKeysWithValues: reader.manifest.frames.map { ($0.captureTimestamp, $0) })
        var owners = [Int: (LiveTrackingIsland, LiveIslandMember)]()
        for island in world.trackingIslands {
            for member in island.members { owners[member.observationID] = (island, member) }
        }
        var policies = [[String: Any]]()
        for policy in ["current", "confirmed-first", "fixed-cadence", "bounded-probes"] {
            let tracker = LiveWorldTracker(), patchMatcher = LivePatchClosureMatcher()
            var lastSearch: Double?, rows = [[String: Any]]()
            for observation in world.observations where observation.id < 830 {
                try autoreleasepool {
                    let time = try XCTUnwrap(observation.captureTimestamp)
                    let frame = try reader.image(for: XCTUnwrap(frames[time]))
                    let masked = try store.source(for: observation.sourceObservationID)
                    let (island, member) = try XCTUnwrap(owners[observation.id])
                    let capture = LiveIslandCapture(id: island.id, roomID: island.roomID,
                        startedAt: island.startedAt,
                        worldFromLocal: CGPoint(x: observation.rawPose.x - member.localPose.x,
                                                y: observation.rawPose.y - member.localPose.y),
                        localPose: member.localPose,
                        placement: island.placement == .reference ? .reference : .estimated,
                        uncertainty: island.placement == .reference ? .reference : .unknown,
                        breakReason: island.breakReason, measured: member.measured)
                    let hasFloor = observation.groundGeometry?.lines.isEmpty == false
                        || observation.observedFloorCandidates?.lines.isEmpty == false
                    let periodicDue = lastSearch.map { observation.timestamp - $0 >= 0.5 } ?? true
                    let shouldSearch = member.measured && hasFloor
                        && (periodicDue || (policy != "fixed-cadence" && tracker.hasPendingClosure))
                    let isProbe = policy == "bounded-probes" && shouldSearch && !periodicDue
                    var verifications = [[String: Any]](), patchProposed = false
                    let result = try tracker.ingest(observationID: observation.id,
                        timestamp: observation.timestamp, captureTimestamp: time, frame: frame,
                        proposedCameraPose: observation.rawPose, solveWidth: observation.solveWidth,
                        exclusions: observation.excludedRects, searchForClosure: shouldSearch,
                        isClosureConfirmationProbe: isProbe,
                        localPose: observation.localPose, captureGeneration: observation.captureGeneration,
                        localEpoch: observation.localEpoch, basisRevision: observation.basisRevision,
                        roomID: observation.roomID, islandCapture: capture,
                        groundGeometry: observation.groundGeometry,
                        observedFloorCandidates: observation.observedFloorCandidates,
                        verifyClosure: { source, proposal in
                            guard let reference = try? store.source(for: source.sourceObservationID),
                                  let sourceFloor = source.groundGeometry else { return nil }
                            let raw = observation.observedFloorCandidates?.verificationGeometry
                            let geometries = policy == "confirmed-first"
                                ? [observation.groundGeometry, raw] : [raw ?? observation.groundGeometry]
                            for geometry in geometries.compactMap({ $0 }) {
                                let verified = GroundPlaneClosureVerifier.verify(reference: reference,
                                    referenceGround: sourceFloor, referencePose: source.optimizedPose,
                                    current: masked, currentGround: geometry,
                                    proposedPose: proposal, solveWidth: observation.solveWidth)
                                verifications.append(["sourceID": source.id, "verified": verified != nil,
                                    "proposal": [proposal.x, proposal.y]])
                                if let verified { return verified.cameraPose }
                            }
                            return nil
                        }, proposePatchClosure: { references in
                            let match = patchMatcher.match(frame: masked, solveWidth: observation.solveWidth,
                                excluding: observation.excludedRects, references: references,
                                sourceProvider: { try? store.source(for: $0) })
                            patchProposed = match != nil
                            return match
                        })
                    if shouldSearch && !isProbe { lastSearch = observation.timestamp }
                    rows.append(["id": observation.id, "searched": shouldSearch,
                        "patchProposed": patchProposed, "verifications": verifications,
                        "rejections": result.review.rejections.map { String(describing: $0) },
                        "accepted": result.acceptedClosure != nil])
                }
            }
            let encoder = JSONEncoder()
            let edgeJSON = try JSONSerialization.jsonObject(with: encoder.encode(tracker.snapshot.loopClosureEdges))
            policies.append(["policy": policy, "rows": rows, "edges": edgeJSON])
        }
        try JSONSerialization.data(withJSONObject: policies, options: [.prettyPrinted, .sortedKeys])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        XCTAssertEqual(try store.load().snapshot, world)
    }

    private func grid(_ image: CGImage, exclusions: [CGRect]) throws -> CalibratedSceneMotionTracker.Grid {
        let pixels = try XCTUnwrap(GroundPixels.luma(image, width: image.width, height: image.height))
        return try XCTUnwrap(CalibratedSceneMotionTracker.grid(pixels: pixels,
            width: image.width, height: image.height, excluding: exclusions))
    }

    private func median(_ values: [CGFloat]) -> CGFloat {
        let ordered = values.sorted(), mid = ordered.count / 2
        return ordered.count % 2 == 0 ? (ordered[mid - 1] + ordered[mid]) * 0.5 : ordered[mid]
    }
}
