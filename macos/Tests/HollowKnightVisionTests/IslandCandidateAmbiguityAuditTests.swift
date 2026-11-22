import CoreGraphics
import Foundation
import XCTest
@testable import HollowKnightVision

/// Diagnostic only: no edges are accepted and no saved world is changed.
/// Compares ambiguity within known local coordinate systems to pooled estimates.
final class IslandCandidateAmbiguityAuditTests: XCTestCase {
    func testInspectSavedIslandCandidateAmbiguity() throws {
        let env = ProcessInfo.processInfo.environment
        guard let input = env["HKV_ISLAND_CANDIDATE_AUDIT_INPUT"],
              let output = env["HKV_ISLAND_CANDIDATE_AUDIT_OUTPUT"] else {
            throw XCTSkip("Set island candidate audit input/output for image-only diagnosis")
        }
        let root = URL(fileURLWithPath: input)
        let store = try LiveWorldSessionStore(rootURL: root.appendingPathComponent("world"))
        let world = try store.load().snapshot
        let byID = Dictionary(uniqueKeysWithValues: world.observations.map { ($0.id, $0) })
        let owners = Dictionary(uniqueKeysWithValues: world.trackingIslands.flatMap { i in
            i.members.map { ($0.observationID, (i.id, $0.measured)) }
        })
        let maker = GlobalFeatureMatcher()
        var sources = [UUID: LiveWorldObservation]()
        var references = [GlobalFeatureReference]()
        var groups = [UUID: Int]()
        for key in world.keyframes {
            guard let observation = byID[key.observationID], let owner = owners[observation.id],
                  owner.1, observation.groundGeometry?.lines.isEmpty == false else { continue }
            let identity = UUID(uuidString: String(format: "00000000-0000-0000-0000-%012llx", observation.id))!
            if let reference = maker.makeReference(id: identity,
                frame: try store.source(for: observation.sourceObservationID),
                cameraPose: observation.optimizedPose, solveWidth: observation.solveWidth,
                excluding: observation.excludedRects) {
                references.append(reference); sources[identity] = observation; groups[identity] = owner.0
            }
        }
        var rows = [[String: Any]]()
        var pooledMisses = 0, partitionProposals = 0, verifiedMissedProposals = 0
        for currentReference in references {
            try autoreleasepool {
                let current = sources[currentReference.id]!
                let eligible = references.filter {
                    let source = sources[$0.id]!
                    return source.id < current.id && source.roomID == current.roomID
                        && groups[$0.id] != groups[currentReference.id]
                }
                guard !eligible.isEmpty, let currentGround = current.groundGeometry else { return }
                let frame = try store.source(for: current.sourceObservationID)
                let pooled = GlobalFeatureMatcher().match(frame: frame, references: eligible,
                    currentSolveWidth: current.solveWidth, excluding: current.excludedRects)
                guard pooled == nil else { return }
                pooledMisses += 1
                let partitions = Dictionary(grouping: eligible) { groups[$0.id]! }
                for groupID in partitions.keys.sorted() {
                    guard let match = GlobalFeatureMatcher().match(frame: frame,
                        references: partitions[groupID]!, currentSolveWidth: current.solveWidth,
                        excluding: current.excludedRects), let source = sources[match.referenceID],
                        let sourceGround = source.groundGeometry else { continue }
                    partitionProposals += 1
                    guard let ground = GroundPlaneClosureVerifier.verify(
                        reference: try store.source(for: source.sourceObservationID),
                        referenceGround: sourceGround, referencePose: source.optimizedPose,
                        current: frame, currentGround: currentGround,
                        proposedPose: match.proposedCameraPose, solveWidth: current.solveWidth) else { continue }
                    verifiedMissedProposals += 1
                    rows.append(["currentID": current.id, "sourceID": source.id,
                        "currentIsland": groups[currentReference.id]!, "sourceIsland": groupID,
                        "delta": [ground.cameraPose.x - source.optimizedPose.x,
                                  ground.cameraPose.y - source.optimizedPose.y],
                        "support": ground.supportedPatches, "span": ground.horizontalSpan,
                        "correlation": ground.meanCorrelation])
                }
            }
        }
        let report: [String: Any] = ["references": references.count, "pooledMisses": pooledMisses,
            "partitionProposals": partitionProposals, "groundVerifiedProposals": verifiedMissedProposals,
            "proposals": rows, "note": "Image-only diagnostic, no accepted graph edges or tracking change. Separate post-hoc reference scoring required; one vote is insufficient for closure."]
        try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys, .prettyPrinted])
            .write(to: URL(fileURLWithPath: output), options: .atomic)
        print("Candidate audit: refs=\(references.count) pooledMisses=\(pooledMisses) partitionProposals=\(partitionProposals) groundVerified=\(verifiedMissedProposals)")
        XCTAssertEqual(try store.load().snapshot, world)
    }
}
