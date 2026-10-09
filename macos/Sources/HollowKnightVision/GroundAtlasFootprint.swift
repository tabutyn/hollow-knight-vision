import CoreGraphics
import Foundation

/// Diagnostic scan of painted pixels, not allocated 256px tile bounds.
/// All rows count: vertical-only movement may extend Y but must not extend X.
struct GroundAtlasFootprint {
    let minimumX: Int
    let maximumX: Int // exclusive
    let leftPixels: Int
    let rightPixels: Int
    let paintedPixels: Int

    static func measure(_ tiles: [LiveTiledAtlasTile], viewX: CGFloat,
                        viewWidth: CGFloat) -> GroundAtlasFootprint {
        var minimum = Int.max, maximum = Int.min
        var left = 0, right = 0, painted = 0
        let viewLeft = Int(floor(viewX)), viewRight = Int(ceil(viewX + viewWidth))
        for tile in tiles {
            // LiveTiledAtlas publishes RGBA8, premultiplied-last tiles.
            guard let data = tile.image.dataProvider?.data,
                  let bytes = CFDataGetBytePtr(data), tile.image.bitsPerPixel == 32,
                  tile.image.alphaInfo == .premultipliedLast else { continue }
            let stride = tile.image.bytesPerRow
            for x in 0..<tile.image.width {
                var count = 0
                for y in 0..<tile.image.height where bytes[y * stride + x * 4 + 3] > 0 {
                    count += 1
                }
                guard count > 0 else { continue }
                let worldX = Int(tile.worldBounds.minX) + x
                minimum = min(minimum, worldX)
                maximum = max(maximum, worldX + 1)
                painted += count
                if worldX < viewLeft { left += count }
                if worldX >= viewRight { right += count }
            }
        }
        return GroundAtlasFootprint(minimumX: painted > 0 ? minimum : 0,
            maximumX: painted > 0 ? maximum : 0, leftPixels: left,
            rightPixels: right, paintedPixels: painted)
    }
}
