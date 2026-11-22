import CoreGraphics
import Foundation

/// An image-only challenge to an already accepted ground match. Independent
/// patch searches prevent a pooled error from averaging foreground and floor.
/// This cannot rescue a globally ambiguous or rejected coarse hypothesis.
enum GroundIndependentPatchConsensus {
    typealias Patch = GroundSubpixelRegistration.Patch
    struct Result {
        let dx: Int
        let dy: Int
        let error: CGFloat
        let patches: [Patch]
    }
    private struct Match {
        let patch: Patch
        let dx: Int
        let dy: Int
        let error: CGFloat
    }
    private struct Location: Hashable {
        let x: Int
        let y: Int
    }

    static func refine(patches: [Patch], pixels: [UInt8], width: Int, height: Int,
                       dx: Int, dy: Int, exclusions: [CGRect]) -> Result? {
        guard width >= 16, height >= 12, width <= Int.max / height,
              pixels.count == width * height, patches.count >= 4, patches.count <= 32,
              dx >= -width, dx <= width, dy >= -height, dy <= height else { return nil }
        var matches = [Match]()
        var locations = Set<Location>()
        for patch in patches {
            guard patch.pixels.count == 192, patch.x >= 0, patch.y >= 0,
                  patch.x <= width - 16, patch.y <= height - 12,
                  locations.insert(Location(x: patch.x, y: patch.y)).inserted else { continue }
            var scores = [Match]()
            scores.reserveCapacity(81)
            for candidateY in (dy - 4)...(dy + 4) {
                for candidateX in (dx - 4)...(dx + 4) {
                    let x = patch.x + candidateX, y = patch.y + candidateY
                    let rect = CGRect(x: x, y: y, width: 16, height: 12)
                    guard x >= 0, y >= 0, x <= width - 16, y <= height - 12,
                          !exclusions.contains(where: { $0.intersects(rect) }),
                          let values = GroundPixels.patch(pixels, width: width, height: height,
                              x: x, y: y) else { continue }
                    scores.append(Match(patch: patch, dx: candidateX, dy: candidateY,
                        error: GroundCameraSolver.patchError(patch.pixels, values)))
                }
            }
            guard let best = scores.min(by: { $0.error < $1.error }), best.error <= 12 else { continue }
            let rival = scores.lazy.filter { abs($0.dx - best.dx) > 2 || abs($0.dy - best.dy) > 2 }
                .min(by: { $0.error < $1.error })
            if let rival, rival.error <= best.error * 1.12 + 0.25 { continue }
            matches.append(best)
        }
        guard matches.count >= 4 else { return nil }
        let groups = matches.map { seed in matches.filter {
            abs($0.dx - seed.dx) <= 1 && abs($0.dy - seed.dy) <= 1
        } }
        guard let group = groups.enumerated().min(by: { a, b in
            if a.element.count != b.element.count { return a.element.count > b.element.count }
            let ae = a.element.map(\.error).reduce(0, +) / CGFloat(a.element.count)
            let be = b.element.map(\.error).reduce(0, +) / CGFloat(b.element.count)
            return ae == be ? a.offset < b.offset : ae < be
        })?.element, group.count >= 4, group.count * 2 > matches.count,
            group.map({ $0.patch.x }).max()! - group.map({ $0.patch.x }).min()! >= 32 else { return nil }
        let x = group.map(\.dx).sorted()[group.count / 2]
        let y = group.map(\.dy).sorted()[group.count / 2]
        let inliers = group.filter {
            let px = $0.patch.x + x, py = $0.patch.y + y
            let rect = CGRect(x: px, y: py, width: 16, height: 12)
            return px >= 0 && py >= 0 && px <= width - 16 && py <= height - 12
                && !exclusions.contains(where: { $0.intersects(rect) })
        }
        guard inliers.count >= 4 else { return nil }
        // Individual best shifts can differ by one pixel. Recheck the shared
        // median itself: a thin edge can disappear at that displacement even
        // though it verified at its own preferred shift. It contributes neither
        // support nor confidence unless the common hypothesis verifies it too.
        let verified = inliers.compactMap { value -> (match: Match, error: CGFloat)? in
            guard let target = GroundPixels.patch(pixels, width: width, height: height,
                x: value.patch.x + x, y: value.patch.y + y) else { return nil }
            let error = GroundCameraSolver.patchError(value.patch.pixels, target)
            guard error.isFinite, error <= 12 else { return nil }
            return (value, error)
        }
        guard verified.count >= 4, verified.count * 2 > matches.count,
              verified.map({ $0.match.patch.x }).max()!
                - verified.map({ $0.match.patch.x }).min()! >= 32 else { return nil }
        return Result(dx: x, dy: y,
                      error: verified.map(\.error).reduce(0, +) / CGFloat(verified.count),
                      patches: verified.map { $0.match.patch })
    }
}
