import CoreGraphics

/// Confirms visible platform texture between consecutive measured views. A
/// platform can supply these inspection boxes before it has a durable atlas ID.
struct GroundPlatformPatches {
    private struct Frame {
        let pixels: [UInt8]
        let floors: [CleanFloorLine]
        let masks: [CGRect]
        let camera: CGPoint
        let size: CGSize
        let index: Int
    }

    private var previous: Frame?

    mutating func observe(pixels: [UInt8], width: Int, height: Int, floors: [CleanFloorLine],
                          masks: [CGRect], camera: CGPoint, frameIndex: Int) -> [CGRect] {
        let current = Frame(pixels: pixels, floors: floors, masks: masks, camera: camera,
                            size: CGSize(width: width, height: height), index: frameIndex)
        defer { previous = current }
        guard let previous, previous.index == frameIndex - 1, previous.size == current.size else { return [] }
        let dx = Int((camera.x - previous.camera.x).rounded())
        let dy = Int((camera.y - previous.camera.y).rounded())
        let patchWidth = GroundPixels.patchWidth, patchHeight = GroundHypothesisTracker.featureHeight
        var matches = [CGRect]()
        for floor in floors {
            let first = max(0, floor.xRange.lowerBound)
            let last = min(width, floor.xRange.upperBound + 1) - patchWidth
            guard first <= last, floor.row >= 0, floor.row + patchHeight <= height else { continue }
            let oldY = floor.row - dy
            let oldFloors = previous.floors.filter { abs($0.row - oldY) <= 3 }
            guard !oldFloors.isEmpty else { continue }

            // Keep samples on a world-space lattice while scrolling. Also try
            // the visible ends so a short ledge can fit its first complete box.
            let worldLeft = Int(camera.x.rounded()) + first
            let phase = ((worldLeft % patchWidth) + patchWidth) % patchWidth
            let aligned = first + (patchWidth - phase) % patchWidth
            var origins = Array(stride(from: aligned, through: last, by: patchWidth))
            if origins.first != first { origins.append(first) }
            if last != first, !origins.contains(last) { origins.append(last) }
            var taken = [Range<Int>]()
            for x in origins {
                let span = x..<(x + patchWidth)
                let oldX = x + dx
                guard !taken.contains(where: { $0.overlaps(span) }),
                      floor.supportsPatch(atX: x, row: floor.row),
                      oldFloors.contains(where: { $0.supportsPatch(atX: oldX, row: oldY) }) else { continue }
                let rect = CGRect(x: x, y: floor.row, width: patchWidth, height: patchHeight)
                let oldRect = CGRect(x: oldX, y: oldY, width: patchWidth, height: patchHeight)
                guard !masks.contains(where: { $0.intersects(rect) }),
                      !previous.masks.contains(where: { $0.intersects(oldRect) }),
                      let old = GroundPixels.patch(previous.pixels, width: width, height: height, x: oldX, y: oldY),
                      let new = GroundPixels.patch(pixels, width: width, height: height, x: x, y: floor.row),
                      (new.max() ?? 0) - (new.min() ?? 0) >= 12,
                      (old.max() ?? 0) - (old.min() ?? 0) >= 12,
                      GroundCameraSolver.patchError(old, new) <= 18 else { continue }
                matches.append(rect)
                taken.append(span)
            }
        }
        return matches
    }
}
