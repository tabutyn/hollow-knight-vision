import CoreGraphics
import Foundation

/// Connectivity is axis-specific: a measured horizontal doorway constrains
/// floor elevation only, whereas an image loop measures both X and Y.
enum LiveIslandAxisSolver {
    struct Link: Equatable {
        let from: Int
        let to: Int
        var horizontal: Bool = true
        var vertical: Bool = true
    }

    struct Solution: Equatable {
        let shifts: [Int: CGVector]
        let horizontalComponent: Set<Int>
        let verticalComponent: Set<Int>
    }

    enum Failure: Error, Equatable {
        case invalidGraph
        case inconsistentKnownComponent
        case conflictingAnchors
    }

    static func solve(islandIDs: Set<Int>, anchored: Set<Int>, links: [Link],
                      newLink: Link, correction: CGVector,
                      tolerance: CGFloat = 4) throws -> Solution {
        guard !islandIDs.isEmpty, anchored.isSubset(of: islandIDs),
              islandIDs.allSatisfy({ $0 >= 0 }), tolerance.isFinite, tolerance >= 0,
              correction.dx.isFinite, correction.dy.isFinite,
              newLink.horizontal || newLink.vertical,
              (links + [newLink]).allSatisfy({
                  islandIDs.contains($0.from) && islandIDs.contains($0.to)
                      && ($0.horizontal || $0.vertical)
              }) else { throw Failure.invalidGraph }
        var shifts = [Int: CGVector]()
        var joined = [Set<Int>(), Set<Int>()]
        var unresolved = CGVector.zero
        var anchorConflict = false
        for horizontal in [true, false] {
            guard horizontal ? newLink.horizontal : newLink.vertical else { continue }
            var neighbors = [Int: Set<Int>]()
            for link in links where horizontal ? link.horizontal : link.vertical {
                neighbors[link.from, default: []].insert(link.to)
                neighbors[link.to, default: []].insert(link.from)
            }
            func component(_ start: Int) -> Set<Int> {
                var seen: Set<Int> = [start], queue = [start]
                while let id = queue.popLast() {
                    for next in (neighbors[id] ?? []).sorted() where seen.insert(next).inserted {
                        queue.append(next)
                    }
                }
                return seen
            }
            let from = component(newLink.from), to = component(newLink.to)
            joined[horizontal ? 0 : 1] = from.union(to)
            let delta = horizontal ? correction.dx : correction.dy
            let fromFixed = !from.isDisjoint(with: anchored)
            let toFixed = !to.isDisjoint(with: anchored)
            if from == to || (fromFixed && toFixed) {
                if horizontal { unresolved.dx = delta } else { unresolved.dy = delta }
                anchorConflict = anchorConflict || (from != to && fromFixed && toFixed)
                continue
            }
            let moveTo = fromFixed || (!toFixed && from.min()! < to.min()!)
            for id in moveTo ? to : from {
                var shift = shifts[id] ?? .zero
                if horizontal { shift.dx = moveTo ? delta : -delta }
                else { shift.dy = moveTo ? delta : -delta }
                shifts[id] = shift
            }
        }
        // Keep the vector tolerance for visual loops. Independently accepting
        // 3 px in each axis must not weaken a 4 px geometric consistency gate.
        guard hypot(unresolved.dx, unresolved.dy) <= tolerance else {
            throw anchorConflict ? Failure.conflictingAnchors : Failure.inconsistentKnownComponent
        }
        return Solution(shifts: shifts, horizontalComponent: joined[0], verticalComponent: joined[1])
    }
}
