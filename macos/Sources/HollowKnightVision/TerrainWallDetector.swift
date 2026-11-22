import Foundation

/// All coordinates use capture pixels with the origin at the top left.
struct DetectedWall: Equatable {
    var x: Int
    var top: Int
    var bottom: Int
    let solidOnRight: Bool
}

struct DetectedTerrainCorner: Equatable {
    let wallIndex: Int
    let floorIndex: Int
    let x: Int
    let y: Int
    let floorEndpoint: Int
}

struct TerrainWallDetection {
    var walls: [DetectedWall] = []
    var corners: [DetectedTerrainCorner] = []
    /// The measured walked footprint is recorded even when a visual edge covers it.
    var walkedFloor: CleanFloorLine?
    var supportedFloor: CleanFloorLine?

    mutating func apply(_ contact: KnightTerrainSupport, floors: [CleanFloorLine], analysis: GroundEdgeAnalysis) {
        var visible = floors
        let scale = CGFloat(analysis.width) / 640
        let reach = max(2, Int(6 * scale))
        if let foot = contact.floor {
            let near = floors.filter { abs($0.row - foot.row) <= max(1, Int(2 * scale))
                && $0.xRange.overlaps(foot.xRange)
            }.min { abs($0.row - foot.row) < abs($1.row - foot.row) }
            // Fill only the unsupported footprint. If a measured platform is present,
            // use its elevation to avoid a parallel stroke under the Knight.
            let row = near?.row ?? foot.row
            let span = foot.xRange
            let walked = CleanFloorLine(row: row, xRange: span)
            walkedFloor = walked
            if !floors.contains(where: { abs($0.row - row) <= reach && $0.xRange.overlaps(span)
                && $0.xRange.lowerBound <= span.lowerBound && $0.xRange.upperBound >= span.upperBound }) {
                supportedFloor = walked
                visible.append(walked)
            }
        }
        if let face = contact.wall {
            if let i = walls.indices.filter({ walls[$0].solidOnRight == face.solidOnRight
                && abs(walls[$0].x - face.x) <= reach && walls[$0].top <= face.bottom + reach
                && walls[$0].bottom >= face.top - reach }).min(by: { abs(walls[$0].x - face.x) < abs(walls[$1].x - face.x) }) {
                walls[i].top = min(walls[i].top, face.top)
                walls[i].bottom = max(walls[i].bottom, face.bottom)
            } else { walls.append(face) }
        }
        if let mask = analysis.validSourcePixels, contact.wall != nil || supportedFloor != nil {
            let result = TerrainWallDetector.join(walls: walls, floors: visible, tolerance: max(4, analysis.width / 64)) {
                mask[$1 * analysis.width + $0] != 0
            }
            walls = result.walls
            corners = result.corners
        }
    }
}

enum TerrainWallDetector {
    private struct Candidate {
        let wall: DetectedWall
        let quality: Int
        var standalone = false
    }

    // A convex platform top and a concave landing at its foot have opposite
    // floor directions for the *same* solid side of a wall.
    private struct Junction {
        let floor: Int
        let wall: Int
        let floorStart: Bool
        let wallTop: Bool
        let distance: Int
    }

    static func detect(_ analysis: GroundEdgeAnalysis, floors: [CleanFloorLine]) -> TerrainWallDetection {
        let width = analysis.width, height = analysis.height
        guard width >= 32, height >= 24, width <= Int.max / height, let luma = analysis.sourceLuma,
              let valid = analysis.validSourcePixels, luma.count == width * height,
              valid.count == luma.count else { return .init() }
        let tolerance = max(4, Int((Double(width) / 64).rounded()))
        var candidates = [Candidate]()
        for x in stride(from: 10, to: width - 10, by: 2) {
            var sign = 0, start = 0, last = 0, votes = 0, contrast = 0
            func finish() {
                let length = last - start + 1
                guard votes >= 5, votes * 2 * 100 >= length * 60 else { return }
                let wall = DetectedWall(x: x, top: start, bottom: last, solidOnRight: sign > 0)
                var nearest = tolerance + 1
                var anchors = 0
                for line in floors where line.row >= start - tolerance && line.row <= last + tolerance {
                    for left in [true, false] {
                        let end = left ? line.xRange.lowerBound : line.xRange.upperBound
                        let tip = (left == wall.solidOnRight) ? start : last
                        let distance = abs(end - x) + abs(tip - line.row) / 2
                        if distance <= tolerance {
                            anchors += 1
                            nearest = min(nearest, distance)
                        }
                    }
                }
                let mean = contrast / votes
                guard anchors > 0 || (length >= 72 && votes * 2 * 100 >= length * 92 && mean >= 54) else { return }
                let quality = min(2, anchors) * 65 - nearest * 2 + min(55, length) + min(70, mean)
                candidates.append(Candidate(wall: wall, quality: quality, standalone: anchors == 0))
            }

            for y in stride(from: 2, to: height - 2, by: 2) {
                let i = y * width + x
                guard valid[i - 9] != 0, valid[i] != 0, valid[i + 9] != 0 else {
                    if votes > 0 { finish(); votes = 0 }
                    continue
                }
                let near = Int(luma[i - 2]) - Int(luma[i + 2])
                let left = Int(luma[i - 5]) + Int(luma[i - 9])
                let right = Int(luma[i + 5]) + Int(luma[i + 9])
                let broad = (left - right) / 2
                let side = broad > 0 ? 1 : -1
                let strength = side * (near + broad) / 2
                if strength >= 7 && side * broad >= 6 && side * near >= 0 && min(left, right) <= 112 {
                    if votes > 0 && (side != sign || y - last > 6) { finish(); votes = 0 }
                    if votes == 0 { sign = side; start = y; contrast = 0 }
                    last = y; votes += 1; contrast += strength
                } else if votes > 0 && y - last > 6 {
                    finish(); votes = 0
                }
            }
            if votes > 0 { finish() }
        }
        // A slab of carved rock often has bright highlights on its dark face.
        // Trace the change in texture from rock to open space in short strips.
        // This keeps the wall continuous when the sign of point contrast flips.
        for floor in floors {
            for left in [true, false] {
                let endpoint = left ? floor.xRange.lowerBound : floor.xRange.upperBound
                for down in [true, false] {
                    let interior = (left == down) ? 1 : -1
                    let step = down ? 1 : -1
                    for x in stride(from: endpoint - tolerance, through: endpoint + tolerance, by: 2)
                    where x >= 11 && x < width - 11 {
                        var quality = 0, blocks = 0, gaps = 0, tip = floor.row, textured = 0
                        for distance in stride(from: 4, to: height, by: 6) {
                            let y = floor.row + step * distance
                            guard y >= 3, y < height - 3 else { break }
                            var inner = 0, outer = 0, insideTexture = 0, outsideTexture = 0
                            var validBlock = true
                            for row in [y - 2, y, y + 2] {
                                let i = row * width + x
                                guard valid[i] != 0, valid[i - 10] != 0, valid[i + 10] != 0 else {
                                    validBlock = false; break
                                }
                                let a = Int(luma[i + 2 * interior]), b = Int(luma[i + 6 * interior])
                                let c = Int(luma[i - 2 * interior]), d = Int(luma[i - 6 * interior])
                                inner += a + b; outer += c + d
                                insideTexture += abs(a - b) + abs(b - Int(luma[i + 10 * interior]))
                                outsideTexture += abs(c - d) + abs(d - Int(luma[i - 10 * interior]))
                            }
                            guard validBlock else { break }
                            let contrast = (outer - inner) / 6
                            let texture = (insideTexture - outsideTexture) / 6
                            if contrast >= 9 || (texture >= 8 && outsideTexture < 75 && outer >= 90) {
                                quality += min(38, max(0, contrast) + max(0, texture))
                                if texture >= 8 && outsideTexture < 75 { textured += 1 }
                                blocks += 1; gaps = 0; tip = y + step * 2
                            } else {
                                gaps += 1
                                if gaps >= 2 { break }
                            }
                        }
                        guard blocks >= 2, textured * 2 >= blocks, abs(tip - floor.row) >= 10 else { continue }
                        let rank = 65 + (down ? 24 : 0) + min(50, blocks * 6) + quality / blocks - abs(x - endpoint) * 3
                        candidates.append(.init(wall: .init(x: x, top: min(tip, floor.row),
                            bottom: max(tip, floor.row), solidOnRight: interior > 0), quality: rank))
                    }
                }
            }
        }
        candidates.sort {
            if $0.quality != $1.quality { return $0.quality > $1.quality }
            return ($0.wall.x, $0.wall.top) < ($1.wall.x, $1.wall.top)
        }
        var kept = [DetectedWall]()
        var freeWalls = Set<Int>()
        for candidate in candidates {
            let wall = candidate.wall
            if kept.contains(where: {
                let overlap = min(wall.bottom, $0.bottom) - max(wall.top, $0.top) + 1
                return abs(wall.x - $0.x) <= tolerance
                    && overlap * 2 >= min(wall.bottom - wall.top + 1, $0.bottom - $0.top + 1)
            }) { continue }
            // Locate the contour inside the several-pixel contrast band.
            var localized = wall, best = -1
            for column in (wall.x - 5)...(wall.x + 5) where column >= 5 && column < width - 5 {
                var energy = 0, count = 0
                for y in stride(from: wall.top + 2, to: wall.bottom - 1, by: 2) {
                    let i = y * width + column
                    guard valid[i - 4] != 0, valid[i + 4] != 0 else { continue }
                    let edge = abs(Int(luma[i - 1]) - Int(luma[i + 1]))
                    let broad = Int(luma[i - 4]) - Int(luma[i + 4])
                    energy += min(55, edge) + min(40, max(0, wall.solidOnRight ? broad : -broad))
                    count += 1
                }
                let score = energy / max(1, count) - abs(column - wall.x) * 2
                if score > best { best = score; localized.x = column }
            }
            if candidate.standalone { freeWalls.insert(kept.count) }
            kept.append(localized)
            if kept.count == 48 { break }
        }

        let joined = join(walls: kept, floors: floors, tolerance: tolerance) { x, y in
            valid[y * width + x] != 0
        }
        let accepted = Set(joined.corners.map(\.wallIndex)).union(freeWalls)
        let acceptedWalls = kept.enumerated().compactMap { accepted.contains($0.offset) ? $0.element : nil }
        return join(walls: acceptedWalls, floors: floors, tolerance: tolerance) { x, y in valid[y * width + x] != 0 }
    }

    static func join(walls: [DetectedWall], floors: [CleanFloorLine], tolerance: Int,
                     allowed: (Int, Int) -> Bool) -> TerrainWallDetection {
        var pairs = [Junction]()
        for (w, wall) in walls.enumerated() {
            for (f, line) in floors.enumerated() {
                for left in [true, false] {
                    let end = left ? line.xRange.lowerBound : line.xRange.upperBound
                    let top = left == wall.solidOnRight
                    let tip = top ? wall.top : wall.bottom
                    let dx = abs(end - wall.x), dy = abs(tip - line.row)
                    guard dx <= tolerance, dy <= tolerance, dx * dx + dy * dy <= tolerance * tolerance,
                          (min(end, wall.x)...max(end, wall.x)).allSatisfy({ allowed($0, line.row) }),
                          (min(tip, line.row)...max(tip, line.row)).allSatisfy({ allowed(wall.x, $0) })
                    else { continue }
                    pairs.append(.init(floor: f, wall: w, floorStart: left, wallTop: top, distance: dx * dx + dy * dy))
                }
            }
        }
        var result = TerrainWallDetection(walls: walls)
        var usedFloors = Set<Int>(), usedWalls = Set<Int>()
        for pair in pairs.sorted(by: { ($0.wall, $0.distance, $0.floor) < ($1.wall, $1.distance, $1.floor) }) {
            let floorKey = pair.floor * 2 + (pair.floorStart ? 0 : 1)
            let wallKey = pair.wall * 2 + (pair.wallTop ? 0 : 1)
            guard !usedFloors.contains(floorKey), !usedWalls.contains(wallKey) else { continue }
            let line = floors[pair.floor], wall = result.walls[pair.wall]
            guard pair.wallTop ? line.row < wall.bottom : line.row > wall.top else { continue }
            usedFloors.insert(floorKey); usedWalls.insert(wallKey)
            if pair.wallTop { result.walls[pair.wall].top = line.row }
            else { result.walls[pair.wall].bottom = line.row }
            result.corners.append(.init(wallIndex: pair.wall, floorIndex: pair.floor,
                x: wall.x, y: line.row, floorEndpoint: pair.floorStart ? line.xRange.lowerBound : line.xRange.upperBound))
        }
        return result
    }
}
