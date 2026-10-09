import CoreGraphics
import CoreImage
import Foundation

struct SceneRegions: Equatable {
    let knight: CGRect?
    let health: CGRect
    let geo: CGRect
    let mana: CGRect
    let focusPrompt: CGRect?
    let particles: [CGRect]
    /// Exact frame-matched HUD stencil; nil retains the legacy/offline policy.
    let hudStencilRects: [CGRect]?

    init(
        knight: CGRect?,
        health: CGRect,
        geo: CGRect,
        mana: CGRect,
        focusPrompt: CGRect? = nil,
        particles: [CGRect] = [],
        hudStencilRects: [CGRect]? = nil
    ) {
        self.knight = knight
        self.health = health
        self.geo = geo
        self.mana = mana
        self.focusPrompt = focusPrompt
        self.particles = particles
        self.hudStencilRects = hudStencilRects
    }

    static func startup(in extent: CGRect) -> SceneRegions {
        SceneRegions(
            knight: nil,
            health: .zero,
            geo: .zero,
            mana: .zero
        )
    }

    /// The learned detector owns moving/world objects only. HUD rectangles are
    /// supplied by the fixed-position stencil in `atlasGameplay` below.
    static func fromObjectDetections(
        _ detections: [LiveObjectDetection],
        in extent: CGRect,
        minimumConfidence: Double = 0.5,
        knightMinimumConfidence: Double? = nil
    ) -> SceneRegions {
        let requiredConfidence = knightMinimumConfidence ?? minimumConfidence
        let knight = detections.filter {
                $0.confidence >= requiredConfidence
                    && LabelingClassIdentity.matches(
                        $0.classIdentifier,
                        "game.playable-knight"
                    )
            }.max(by: { $0.confidence < $1.confidence })?.imageRect(
                width: Int(extent.width), height: Int(extent.height)
            )
        return SceneRegions(
            knight: knight,
            health: .zero,
            geo: .zero,
            mana: .zero
        )
    }

    /// Atlas writes use the frame-matched stencil plus a conservative fixed
    /// screen-space HUD shield. The Knight remains detection-only because it
    /// moves through world space.
    static func atlasGameplay(
        fromObjectDetections detections: [LiveObjectDetection],
        in extent: CGRect,
        knightMinimumConfidence: Double? = nil,
        hudStencil: HUDStencilResult? = nil
    ) -> SceneRegions {
        let detected = fromObjectDetections(
            detections,
            in: extent,
            knightMinimumConfidence: knightMinimumConfidence
        )
        let fallback = hud(in: extent, knight: detected.knight)
        let stencilHealth = hudStencil?.healthBounds
        let health = stencilHealth.map { $0.isNull || $0.isEmpty ? fallback.health : $0 }
            ?? fallback.health
        let geo = hudStencil?.geo
            ?? fallback.geo
        let mana = hudStencil?.manaBounds
            ?? fallback.mana
        return SceneRegions(
            knight: detected.knight,
            health: health,
            geo: geo,
            mana: mana,
            hudStencilRects: atlasHUDMaskRects(in: extent, hudStencil: hudStencil)
        )
    }

    /// Atlas evidence uses a stable screen-space shield in addition to exact
    /// stencil matches. The soul ornament extends left of its matched sprites,
    /// and a temporarily incomplete health row must never become world texture.
    /// Later camera overlap can fill the transparent atlas area with real room
    /// pixels, so this deliberately favors omission over persistent HUD ghosts.
    static func atlasHUDMaskRects(
        in extent: CGRect,
        hudStencil: HUDStencilResult?
    ) -> [CGRect] {
        let persistentFootprint = CGRect(
            x: extent.minX,
            y: extent.minY + extent.height * 0.75,
            width: extent.width * 0.40,
            height: extent.height * 0.25
        ).intersection(extent).integral
        return [persistentFootprint] + (hudStencil?.omittedRects ?? [])
    }

    var omittedRects: [CGRect] {
        ([knight, focusPrompt].compactMap { $0 }
            + (hudStencilRects ?? [health, geo, mana]) + particles)
            .filter { !$0.isEmpty && !$0.isNull }
    }

    static func hud(
        in extent: CGRect,
        knight: CGRect?,
        geoDigitCount: Int = 1,
        focusPrompt: CGRect? = nil,
        particles: [CGRect] = []
    ) -> SceneRegions {
        let digits = min(6, max(1, geoDigitCount))
        return SceneRegions(
            knight: knight,
            health: CGRect(
                x: extent.minX + extent.width * 0.122,
                y: extent.minY + extent.height * 0.883,
                width: extent.width * 0.130,
                height: extent.height * 0.047
            ).intersection(extent),
            geo: CGRect(
                x: extent.minX + extent.width * 0.120,
                y: extent.minY + extent.height * 0.813,
                width: extent.width * (0.058 + CGFloat(digits - 1) * 0.016),
                height: extent.height * 0.045
            ).intersection(extent),
            mana: CGRect(
                x: extent.minX + extent.width * 0.062,
                y: extent.minY + extent.height * 0.815,
                width: extent.width * 0.060,
                height: extent.height * 0.120
            ).intersection(extent),
            focusPrompt: focusPrompt,
            particles: particles
        )
    }

    /// Tutorial prompts such as "HOLD A Focus" are screen-space HUD. Detection
    /// is restricted to this fixed box, but the box is only omitted while text
    /// is actually visible (plus a short fade hold).
    static func fixedFocusPrompt(in extent: CGRect) -> CGRect {
        CGRect(
            x: extent.minX + extent.width * 0.405,
            y: extent.minY + extent.height * 0.025,
            width: extent.width * 0.235,
            height: extent.height * 0.105
        ).intersection(extent).integral
    }
}

final class SceneRegionDetector {
    private static let sampleWidth = 320
    private static let particleSampleWidth = 160
    private var previousCenter: CGPoint?
    private var headVelocity = CGVector.zero
    private var missedFrames = 0
    private var previousKnight: CGRect?
    private var intensityHistory = [[UInt8]]()
    private var particleTracks = [ParticleTrack]()
    private var focusPromptHoldFrames = 0

    func reset() {
        previousCenter = nil
        headVelocity = .zero
        previousKnight = nil
        missedFrames = 0
        intensityHistory.removeAll(keepingCapacity: true)
        particleTracks.removeAll(keepingCapacity: true)
        focusPromptHoldFrames = 0
    }

    func detect(
        in image: CGImage,
        cameraPosition: CGPoint = .zero,
        solveWidth: CGFloat? = nil,
        includeParticles: Bool = true
    ) -> SceneRegions {
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let sampleHeight = Self.sampleHeight(for: image)
        guard let pixels = rgbaPixels(image, width: Self.sampleWidth, height: sampleHeight) else {
            return .hud(in: extent, knight: previousKnight)
        }
        let knight = detectKnight(in: image, pixels: pixels, sampleHeight: sampleHeight)
        let geoDigitCount = estimateGeoDigitCount(
            pixels: pixels,
            width: Self.sampleWidth,
            height: sampleHeight
        )
        let focusPrompt = detectFocusPrompt(
            pixels: pixels,
            sampleHeight: sampleHeight,
            imageExtent: extent
        )
        let hud = SceneRegions.hud(
            in: extent,
            knight: knight,
            geoDigitCount: geoDigitCount,
            focusPrompt: focusPrompt
        )
        let scale = CGFloat(image.width) / max(1, solveWidth ?? CGFloat(image.width))
        let particles = includeParticles
            ? detectParticles(
                in: image,
                excluding: hud.omittedRects,
                worldOffset: CGPoint(
                    x: cameraPosition.x * scale,
                    y: cameraPosition.y * scale
                )
            )
            : []
        return .hud(
            in: extent,
            knight: knight,
            geoDigitCount: geoDigitCount,
            focusPrompt: focusPrompt,
            particles: particles
        )
    }

    private static func sampleHeight(for image: CGImage) -> Int {
        max(1, Int(
            (CGFloat(image.height) * CGFloat(Self.sampleWidth) / CGFloat(max(1, image.width))).rounded()
        ))
    }

    private func detectFocusPrompt(
        pixels: [UInt8],
        sampleHeight: Int,
        imageExtent: CGRect
    ) -> CGRect? {
        let prompt = SceneRegions.fixedFocusPrompt(in: imageExtent)
        let scaleX = CGFloat(Self.sampleWidth) / max(1, imageExtent.width)
        let scaleY = CGFloat(sampleHeight) / max(1, imageExtent.height)
        let minX = max(1, Int(((prompt.minX - imageExtent.minX) * scaleX).rounded(.down)))
        let maxX = min(Self.sampleWidth - 1, Int(((prompt.maxX - imageExtent.minX) * scaleX).rounded(.up)))
        // CGContext samples are top-origin while SceneRegions uses Core Image's
        // bottom-origin coordinates.
        let minY = max(1, Int((CGFloat(sampleHeight) - (prompt.maxY - imageExtent.minY) * scaleY).rounded(.down)))
        let maxY = min(sampleHeight - 1, Int((CGFloat(sampleHeight) - (prompt.minY - imageExtent.minY) * scaleY).rounded(.up)))
        guard minX < maxX, minY < maxY else { return nil }

        var textMask = [Bool](repeating: false, count: Self.sampleWidth * sampleHeight)
        for y in minY..<maxY {
            for x in minX..<maxX {
                let color = pixelColor(pixels, width: Self.sampleWidth, x: x, y: y)
                let brightest = max(color.red, color.green, color.blue)
                let darkest = min(color.red, color.green, color.blue)
                guard darkest >= 72, brightest - darkest <= 58 else { continue }
                let luma = (color.red * 54 + color.green * 183 + color.blue * 19) / 256
                let horizontalContrast = max(
                    abs(luma - lumaAt(pixels, width: Self.sampleWidth, x: x - 1, y: y)),
                    abs(luma - lumaAt(pixels, width: Self.sampleWidth, x: x + 1, y: y))
                )
                let verticalContrast = max(
                    abs(luma - lumaAt(pixels, width: Self.sampleWidth, x: x, y: y - 1)),
                    abs(luma - lumaAt(pixels, width: Self.sampleWidth, x: x, y: y + 1))
                )
                textMask[y * Self.sampleWidth + x] = max(horizontalContrast, verticalContrast) >= 12
            }
        }

        var visited = [Bool](repeating: false, count: textMask.count)
        var glyphs = [CGRect]()
        for y in minY..<maxY {
            for x in minX..<maxX {
                let start = y * Self.sampleWidth + x
                guard textMask[start], !visited[start] else { continue }
                var queue = [(x, y)]
                visited[start] = true
                var cursor = 0
                var lowX = x
                var highX = x
                var lowY = y
                var highY = y
                var count = 0
                while cursor < queue.count {
                    let point = queue[cursor]
                    cursor += 1
                    count += 1
                    lowX = min(lowX, point.0)
                    highX = max(highX, point.0)
                    lowY = min(lowY, point.1)
                    highY = max(highY, point.1)
                    for neighborY in max(minY, point.1 - 1)...min(maxY - 1, point.1 + 1) {
                        for neighborX in max(minX, point.0 - 1)...min(maxX - 1, point.0 + 1) {
                            let index = neighborY * Self.sampleWidth + neighborX
                            if textMask[index], !visited[index] {
                                visited[index] = true
                                queue.append((neighborX, neighborY))
                            }
                        }
                    }
                }
                let bounds = CGRect(
                    x: CGFloat(lowX),
                    y: CGFloat(lowY),
                    width: CGFloat(highX - lowX + 1),
                    height: CGFloat(highY - lowY + 1)
                )
                if count >= 2, count <= 150,
                   bounds.width <= 18, bounds.height >= 2, bounds.height <= 14 {
                    glyphs.append(bounds)
                }
            }
        }

        let visible = glyphs.count >= 3
            && (glyphs.map(\.maxX).max() ?? 0) - (glyphs.map(\.minX).min() ?? 0) >= 14
        if visible {
            focusPromptHoldFrames = 2
        } else if focusPromptHoldFrames > 0 {
            focusPromptHoldFrames -= 1
        }
        return focusPromptHoldFrames > 0 ? prompt : nil
    }

    private func lumaAt(_ pixels: [UInt8], width: Int, x: Int, y: Int) -> Int {
        let color = pixelColor(pixels, width: width, x: x, y: y)
        return (color.red * 54 + color.green * 183 + color.blue * 19) / 256
    }

    private func detectKnight(
        in image: CGImage,
        pixels: [UInt8],
        sampleHeight: Int
    ) -> CGRect? {

        var mask = [Bool](repeating: false, count: Self.sampleWidth * sampleHeight)
        // Leave the HUD out, but search high enough to retain the Knight during
        // full-height jumps. Temporal prediction rejects most pale scenery.
        let excludedTop = Int(CGFloat(sampleHeight) * (previousCenter == nil ? 0.30 : 0.18))
        for y in excludedTop..<sampleHeight {
            for x in 0..<Self.sampleWidth {
                let offset = (y * Self.sampleWidth + x) * 4
                let red = Int(pixels[offset])
                let green = Int(pixels[offset + 1])
                let blue = Int(pixels[offset + 2])
                let alpha = Int(pixels[offset + 3])
                let brightest = max(red, green, blue)
                let darkest = min(red, green, blue)
                mask[y * Self.sampleWidth + x] = alpha > 180
                    && red > 170 && green > 170 && blue > 170
                    && brightest - darkest < 65
            }
        }

        var visited = [Bool](repeating: false, count: mask.count)
        var candidates = [Candidate]()
        for y in excludedTop..<sampleHeight {
            for x in 0..<Self.sampleWidth {
                let start = y * Self.sampleWidth + x
                guard mask[start], !visited[start] else { continue }
                var queue = [(x: Int, y: Int)]()
                queue.append((x, y))
                visited[start] = true
                var cursor = 0
                var minX = x
                var maxX = x
                var minY = y
                var maxY = y
                var count = 0
                while cursor < queue.count {
                    let point = queue[cursor]
                    cursor += 1
                    count += 1
                    minX = min(minX, point.x)
                    maxX = max(maxX, point.x)
                    minY = min(minY, point.y)
                    maxY = max(maxY, point.y)
                    for neighborY in max(excludedTop, point.y - 1)...min(sampleHeight - 1, point.y + 1) {
                        for neighborX in max(0, point.x - 1)...min(Self.sampleWidth - 1, point.x + 1) {
                            let index = neighborY * Self.sampleWidth + neighborX
                            if mask[index], !visited[index] {
                                visited[index] = true
                                queue.append((neighborX, neighborY))
                            }
                        }
                    }
                }
                let bounds = CGRect(
                    x: CGFloat(minX),
                    y: CGFloat(minY),
                    width: CGFloat(maxX - minX + 1),
                    height: CGFloat(maxY - minY + 1)
                )
                guard count >= 8,
                      bounds.width >= 4, bounds.width <= 34,
                      bounds.height >= 7, bounds.height <= 42 else { continue }
                let aspect = bounds.height / max(1, bounds.width)
                guard aspect >= 0.72, aspect <= 2.4 else { continue }
                let appearance = candidateAppearance(
                    around: bounds,
                    pixels: pixels,
                    width: Self.sampleWidth,
                    height: sampleHeight
                )
                // The Knight's pale head sits directly over a dark cloak. Room
                // lights instead retain a bright halo below their white core.
                guard appearance.darkBodyRatio >= 0.22 else { continue }
                candidates.append(Candidate(
                    bounds: bounds,
                    brightPixels: count,
                    darkBodyRatio: appearance.darkBodyRatio,
                    brightHaloRatio: appearance.brightHaloRatio
                ))
            }
        }

        let trackable = candidates.filter(isWithinTrackGate)
        guard let winner = trackable.max(by: {
            score($0, sampleHeight: sampleHeight) < score($1, sampleHeight: sampleHeight)
        }) else {
            missedFrames += 1
            if missedFrames <= 12, let center = previousCenter {
                let predicted = CGPoint(
                    x: center.x + headVelocity.dx,
                    y: center.y + headVelocity.dy
                )
                previousCenter = predicted
                headVelocity.dx *= 0.82
                headVelocity.dy *= 0.82
                let result = knightRect(
                    for: predicted,
                    sampleHeight: sampleHeight,
                    image: image
                )
                previousKnight = result
                return result
            }
            previousCenter = nil
            headVelocity = .zero
            previousKnight = nil
            return nil
        }

        missedFrames = 0
        let measured = CGPoint(x: winner.bounds.midX, y: winner.bounds.midY)
        let filtered: CGPoint
        if let center = previousCenter {
            let stableX = abs(measured.x - center.x) < 1.25
            let stableY = abs(measured.y - center.y) < 1.0
            let predicted = CGPoint(
                x: center.x + headVelocity.dx,
                y: center.y + headVelocity.dy
            )
            let innovation = CGVector(
                dx: measured.x - predicted.x,
                dy: measured.y - predicted.y
            )
            let verticalAlpha: CGFloat = abs(innovation.dy) > 5 ? 0.82 : 0.58
            filtered = CGPoint(
                x: stableX ? center.x : predicted.x + innovation.dx * 0.52,
                y: stableY ? center.y : predicted.y + innovation.dy * verticalAlpha
            )
            headVelocity = CGVector(
                dx: stableX ? 0 : headVelocity.dx * 0.52 + innovation.dx * 0.24,
                dy: stableY ? 0 : headVelocity.dy * 0.48 + innovation.dy * 0.34
            )
        } else {
            filtered = measured
            headVelocity = .zero
        }
        previousCenter = filtered
        let result = knightRect(for: filtered, sampleHeight: sampleHeight, image: image)
        previousKnight = result
        return result
    }

    private func score(_ candidate: Candidate, sampleHeight: Int) -> CGFloat {
        let center = CGPoint(x: candidate.bounds.midX, y: candidate.bounds.midY)
        var value = min(3, CGFloat(candidate.brightPixels) / 20)
            + candidate.darkBodyRatio * 5
            - candidate.brightHaloRatio * 4
        if let previousCenter {
            let predicted = CGPoint(
                x: previousCenter.x + headVelocity.dx,
                y: previousCenter.y + headVelocity.dy
            )
            let dx = center.x - predicted.x
            let dy = (center.y - predicted.y) * 0.72
            value += max(0, 1 - hypot(dx, dy) / 54) * 14
        } else {
            let targetY = CGFloat(sampleHeight) * 0.68
            value += max(0, 1 - abs(center.y - targetY) / CGFloat(sampleHeight)) * 4
            value += max(0, 1 - abs(center.x - CGFloat(Self.sampleWidth) * 0.5)
                / CGFloat(Self.sampleWidth))
        }
        let aspect = candidate.bounds.height / max(1, candidate.bounds.width)
        value += max(0, 1 - abs(aspect - 1.2) / 1.5)
        return value
    }

    private func isWithinTrackGate(_ candidate: Candidate) -> Bool {
        guard let previousCenter else { return true }
        let predicted = CGPoint(
            x: previousCenter.x + headVelocity.dx,
            y: previousCenter.y + headVelocity.dy
        )
        let dx = candidate.bounds.midX - predicted.x
        let dy = (candidate.bounds.midY - predicted.y) * 0.78
        return hypot(dx, dy) <= 54
    }

    private func candidateAppearance(
        around bounds: CGRect,
        pixels: [UInt8],
        width: Int,
        height: Int
    ) -> (darkBodyRatio: CGFloat, brightHaloRatio: CGFloat) {
        let centerX = Int(bounds.midX.rounded())
        let bodyHalfWidth = max(3, Int((bounds.width * 0.58).rounded()))
        let bodyTop = min(height, Int(bounds.maxY.rounded(.up)))
        let bodyBottom = min(height, bodyTop + max(8, Int((bounds.height * 0.95).rounded())))
        var darkBodyPixels = 0
        var bodyPixels = 0
        if bodyTop < bodyBottom {
            for y in bodyTop..<bodyBottom {
                for x in max(0, centerX - bodyHalfWidth)..<min(width, centerX + bodyHalfWidth + 1) {
                    let color = pixelColor(pixels, width: width, x: x, y: y)
                    let luma = (color.red * 54 + color.green * 183 + color.blue * 19) / 256
                    bodyPixels += 1
                    if luma < 74 { darkBodyPixels += 1 }
                }
            }
        }

        let padding = max(4, Int((max(bounds.width, bounds.height) * 0.55).rounded()))
        let minX = max(0, Int(bounds.minX) - padding)
        let maxX = min(width, Int(bounds.maxX.rounded(.up)) + padding)
        let minY = max(0, Int(bounds.minY) - padding)
        let maxY = min(height, Int(bounds.maxY.rounded(.up)) + padding)
        var brightHaloPixels = 0
        var haloPixels = 0
        for y in minY..<maxY {
            for x in minX..<maxX where !bounds.contains(CGPoint(x: x, y: y)) {
                let color = pixelColor(pixels, width: width, x: x, y: y)
                let luma = (color.red * 54 + color.green * 183 + color.blue * 19) / 256
                haloPixels += 1
                if luma > 112 { brightHaloPixels += 1 }
            }
        }
        return (
            darkBodyRatio: CGFloat(darkBodyPixels) / CGFloat(max(1, bodyPixels)),
            brightHaloRatio: CGFloat(brightHaloPixels) / CGFloat(max(1, haloPixels))
        )
    }

    private func estimateGeoDigitCount(pixels: [UInt8], width: Int, height: Int) -> Int {
        let minX = max(0, Int(CGFloat(width) * 0.15))
        let maxX = min(width, Int(CGFloat(width) * 0.27))
        let minY = max(0, Int(CGFloat(height) * 0.118))
        let maxY = min(height, Int(CGFloat(height) * 0.19))
        var rightmost = -1
        guard minX < maxX, minY < maxY else { return 1 }
        for y in minY..<maxY {
            for x in minX..<maxX {
                let color = pixelColor(pixels, width: width, x: x, y: y)
                let brightest = max(color.red, color.green, color.blue)
                let darkest = min(color.red, color.green, color.blue)
                if darkest > 145, brightest - darkest < 55 {
                    rightmost = max(rightmost, x)
                }
            }
        }
        guard rightmost >= minX else { return 1 }
        let rightEdge = CGFloat(rightmost + 1) / CGFloat(width)
        return min(6, max(1, Int(ceil((rightEdge - 0.158) / 0.016))))
    }

    private func pixelColor(
        _ pixels: [UInt8],
        width: Int,
        x: Int,
        y: Int
    ) -> (red: Int, green: Int, blue: Int) {
        let offset = (y * width + x) * 4
        return (Int(pixels[offset]), Int(pixels[offset + 1]), Int(pixels[offset + 2]))
    }

    private func knightRect(for headCenter: CGPoint, sampleHeight: Int, image: CGImage) -> CGRect {
        let scaleX = CGFloat(image.width) / CGFloat(Self.sampleWidth)
        let scaleY = CGFloat(image.height) / CGFloat(sampleHeight)
        let size = CGSize(
            width: (CGFloat(image.width) * 0.036).rounded(),
            height: (CGFloat(image.height) * 0.125).rounded()
        )
        let centerX = headCenter.x * scaleX + CGFloat(image.width) * 0.004
        let top = CGFloat(image.height) - headCenter.y * scaleY + CGFloat(image.height) * 0.0585
        let maxX = max(0, CGFloat(image.width) - size.width)
        let maxY = max(0, CGFloat(image.height) - size.height)
        return CGRect(
            x: min(max(0, centerX - size.width / 2), maxX).rounded(),
            y: min(max(0, top - size.height), maxY).rounded(),
            width: size.width,
            height: size.height
        )
    }

    private func detectParticles(
        in image: CGImage,
        excluding excludedRects: [CGRect],
        worldOffset: CGPoint
    ) -> [CGRect] {
        let sampleHeight = max(1, Int(
            (CGFloat(image.height) * CGFloat(Self.particleSampleWidth)
                / CGFloat(max(1, image.width))).rounded()
        ))
        guard let pixels = rgbaPixels(
            image,
            width: Self.particleSampleWidth,
            height: sampleHeight
        ) else { return updateParticleTracks(with: [], worldOffset: worldOffset) }

        var intensity = [UInt8](repeating: 0, count: Self.particleSampleWidth * sampleHeight)
        var palePeak = [Bool](repeating: false, count: intensity.count)
        for index in intensity.indices {
            let offset = index * 4
            let red = CGFloat(pixels[offset])
            let green = CGFloat(pixels[offset + 1])
            let blue = CGFloat(pixels[offset + 2])
            intensity[index] = UInt8(min(255, red * 0.2126 + green * 0.7152 + blue * 0.0722))
            palePeak[index] = max(red, green, blue) >= 108
                && max(red, green, blue) - min(red, green, blue) <= 105
        }
        intensityHistory.append(intensity)
        if intensityHistory.count > 6 {
            intensityHistory.removeFirst(intensityHistory.count - 6)
        }
        guard intensityHistory.count >= 3 else {
            return updateParticleTracks(with: [], worldOffset: worldOffset)
        }

        let scaleX = CGFloat(image.width) / CGFloat(Self.particleSampleWidth)
        let scaleY = CGFloat(image.height) / CGFloat(sampleHeight)
        let currentSample = intensityHistory[intensityHistory.count - 1]
        var peaks = [ParticlePeak]()
        for y in 2..<(sampleHeight - 2) {
            for x in 2..<(Self.particleSampleWidth - 2) {
                let index = y * Self.particleSampleWidth + x
                let point = CGPoint(
                    x: (CGFloat(x) + 0.5) * scaleX,
                    y: CGFloat(image.height) - (CGFloat(y) + 0.5) * scaleY
                )
                guard !excludedRects.contains(where: { $0.contains(point) }) else { continue }
                var low = 255
                var high = 0
                for sample in intensityHistory {
                    let value = Int(sample[index])
                    low = min(low, value)
                    high = max(high, value)
                }
                let current = Int(currentSample[index])
                let previous = Int(intensityHistory[intensityHistory.count - 2][index])
                guard palePeak[index],
                      current >= 108,
                      high - low >= 18,
                      abs(current - previous) >= 4 || high - low >= 28 else { continue }

                var immediate = [Int]()
                var ring = [Int]()
                for dy in -2...2 {
                    for dx in -2...2 where dx != 0 || dy != 0 {
                        let value = Int(currentSample[(y + dy) * Self.particleSampleWidth + x + dx])
                        if max(abs(dx), abs(dy)) == 1 {
                            immediate.append(value)
                        } else if max(abs(dx), abs(dy)) == 2 {
                            ring.append(value)
                        }
                    }
                }
                let ringAverage = ring.reduce(0, +) / max(1, ring.count)
                let lowerNeighbors = immediate.filter { $0 <= current - 3 }.count
                let localMaximum = immediate.max().map { current >= $0 } ?? false
                let centerSurround = current - ringAverage
                let left = Int(currentSample[y * Self.particleSampleWidth + x - 1])
                let right = Int(currentSample[y * Self.particleSampleWidth + x + 1])
                let up = Int(currentSample[(y - 1) * Self.particleSampleWidth + x])
                let down = Int(currentSample[(y + 1) * Self.particleSampleWidth + x])
                let axialAsymmetry = abs(left - right) + abs(up - down)
                guard localMaximum,
                      lowerNeighbors >= 5,
                      centerSurround >= 14,
                      axialAsymmetry <= 100 else { continue }
                peaks.append(ParticlePeak(
                    x: x,
                    y: y,
                    score: centerSurround + high - low,
                    intensity: current
                ))
            }
        }

        var selected = [ParticlePeak]()
        for peak in peaks.sorted(by: { $0.score > $1.score }) {
            guard selected.count < 32 else { break }
            guard !selected.contains(where: {
                hypot(CGFloat($0.x - peak.x), CGFloat($0.y - peak.y)) < 3
            }) else { continue }
            selected.append(peak)
        }
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let detections = selected.compactMap { peak -> ParticleDetection? in
            let radius: CGFloat = 3.5
            let rect = CGRect(
                x: (CGFloat(peak.x) + 0.5 - radius) * scaleX,
                y: (CGFloat(sampleHeight - peak.y) - 0.5 - radius) * scaleY,
                width: radius * 2 * scaleX,
                height: radius * 2 * scaleY
            ).intersection(extent).integral
            return rect.isNull || rect.isEmpty
                ? nil
                : ParticleDetection(rect: rect, intensity: peak.intensity)
        }
        return updateParticleTracks(with: detections, worldOffset: worldOffset)
    }

    private func updateParticleTracks(
        with detections: [ParticleDetection],
        worldOffset: CGPoint
    ) -> [CGRect] {
        var matched = Set<Int>()
        for detection in detections {
            let worldDetection = detection.rect.offsetBy(dx: worldOffset.x, dy: worldOffset.y)
            let nearest = particleTracks.indices
                .filter { !matched.contains($0) }
                .min {
                    predictedDistance(particleTracks[$0], worldDetection)
                        < predictedDistance(particleTracks[$1], worldDetection)
                }
            if let nearest, predictedDistance(particleTracks[nearest], worldDetection) < 48 {
                var track = particleTracks[nearest]
                let motion = CGVector(
                    dx: worldDetection.midX - track.worldRect.midX,
                    dy: worldDetection.midY - track.worldRect.midY
                )
                track.velocity = CGVector(
                    dx: track.velocity.dx * 0.55 + motion.dx * 0.45,
                    dy: track.velocity.dy * 0.55 + motion.dy * 0.45
                )
                track.rect = detection.rect
                track.worldRect = worldDetection
                track.hits += 1
                if hypot(motion.dx, motion.dy) >= 0.75 {
                    track.movingHits += 1
                }
                if abs(detection.intensity - track.lastIntensity) >= 14 {
                    track.brightnessChangeHits += 1
                }
                track.lastIntensity = detection.intensity
                track.misses = 0
                particleTracks[nearest] = track
                matched.insert(nearest)
            } else if particleTracks.count < 64 {
                particleTracks.append(ParticleTrack(
                    rect: detection.rect,
                    worldRect: worldDetection,
                    velocity: .zero,
                    hits: 1,
                    movingHits: 0,
                    brightnessChangeHits: 0,
                    lastIntensity: detection.intensity,
                    misses: 0
                ))
                matched.insert(particleTracks.count - 1)
            }
        }
        for index in particleTracks.indices where !matched.contains(index) {
            particleTracks[index].worldRect = particleTracks[index].worldRect.offsetBy(
                dx: particleTracks[index].velocity.dx,
                dy: particleTracks[index].velocity.dy
            )
            particleTracks[index].rect = particleTracks[index].worldRect.offsetBy(
                dx: -worldOffset.x,
                dy: -worldOffset.y
            )
            particleTracks[index].velocity.dx *= 0.75
            particleTracks[index].velocity.dy *= 0.75
            particleTracks[index].misses += 1
        }
        particleTracks.removeAll { $0.misses > 2 }
        return particleTracks
            .filter {
                // A raw peak already has temporal and Gaussian-shape evidence,
                // so mask its first appearance before that pixel reaches the
                // map. Keep it through one missed frame only after its motion
                // or brightness lifecycle confirms the track.
                ($0.hits == 1 && $0.misses == 0)
                    || ($0.hits >= 2
                        && ($0.movingHits >= 1 || $0.brightnessChangeHits >= 1)
                        && $0.misses <= 1)
            }
            .map(\.rect)
    }

    private func predictedDistance(_ track: ParticleTrack, _ worldDetection: CGRect) -> CGFloat {
        hypot(
            track.worldRect.midX + track.velocity.dx - worldDetection.midX,
            track.worldRect.midY + track.velocity.dy - worldDetection.midY
        )
    }

    private func rgbaPixels(_ image: CGImage, width: Int, height: Int) -> [UInt8]? {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let created = pixels.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(
                data: bytes.baseAddress,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                    | CGBitmapInfo.byteOrder32Big.rawValue
            ) else { return false }
            context.interpolationQuality = .low
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        return created ? pixels : nil
    }

    private struct Candidate {
        let bounds: CGRect
        let brightPixels: Int
        let darkBodyRatio: CGFloat
        let brightHaloRatio: CGFloat
    }

    private struct ParticleTrack {
        var rect: CGRect
        var worldRect: CGRect
        var velocity: CGVector
        var hits: Int
        var movingHits: Int
        var brightnessChangeHits: Int
        var lastIntensity: Int
        var misses: Int
    }

    private struct ParticlePeak {
        let x: Int
        let y: Int
        let score: Int
        let intensity: Int
    }

    private struct ParticleDetection {
        let rect: CGRect
        let intensity: Int
    }

}

struct LowResolutionMotionGrid: Equatable, Sendable {
    let width: Int
    let height: Int
    let luma: [UInt8]

    init(width: Int, height: Int, luma: [UInt8]) {
        precondition(width > 0 && height > 0 && luma.count == width * height)
        self.width = width
        self.height = height
        self.luma = luma
    }
}

enum LowResolutionMotionDiagnosticPresentation {
    static func text(
        estimate: LowResolutionRoomMotionEstimate?,
        isControlling: Bool,
        solveWidth: CGFloat,
        gridWidth: Int
    ) -> String {
        guard let estimate, solveWidth.isFinite, solveWidth > 0,
              gridWidth > 0 else { return "Coarse Motion · ready · no reliable shift" }
        let worldStep = abs(CGFloat(estimate.screenShift))
            * solveWidth / CGFloat(gridWidth)
        let mode = isControlling ? "controlling" : "ready"
        let confidence = Int((estimate.confidence * 100).rounded())
        return "Coarse Motion · \(mode) · \(estimate.direction.label) \(Int(worldStep.rounded())) px · \(confidence)%"
    }
}

enum LowResolutionMotionDiagnosticRenderer {
    static func image(
        grid: LowResolutionMotionGrid,
        estimate: LowResolutionRoomMotionEstimate?,
        isControlling: Bool
    ) -> CGImage? {
        guard grid.width > 0, grid.height > 0,
              grid.luma.count == grid.width * grid.height else { return nil }
        let mean = Double(grid.luma.reduce(0) { $0 + Int($1) })
            / Double(grid.luma.count)
        var pixels = [UInt8](repeating: 0, count: grid.width * grid.height * 4)
        for index in grid.luma.indices {
            let contrast = min(255, max(0,
                Int((128 + (Double(grid.luma[index]) - mean) * 2.2).rounded())
            ))
            let output = index * 4
            pixels[output] = UInt8(contrast)
            pixels[output + 1] = UInt8(contrast)
            pixels[output + 2] = UInt8(contrast)
            pixels[output + 3] = 255
        }

        let accent: (UInt8, UInt8, UInt8) = isControlling
            ? (255, 132, 31) : estimate == nil
                ? (150, 150, 150) : (30, 230, 255)
        func paint(_ x: Int, _ y: Int, color: (UInt8, UInt8, UInt8)) {
            guard (0..<grid.width).contains(x), (0..<grid.height).contains(y) else { return }
            let index = (y * grid.width + x) * 4
            pixels[index] = color.0
            pixels[index + 1] = color.1
            pixels[index + 2] = color.2
            pixels[index + 3] = 255
        }
        for x in 0..<grid.width {
            paint(x, 0, color: accent)
            paint(x, grid.height - 1, color: accent)
        }
        for y in 0..<grid.height {
            paint(0, y, color: accent)
            paint(grid.width - 1, y, color: accent)
        }
        if let estimate {
            let centerX = grid.width / 2
            let centerY = grid.height / 2
            let length = min(
                max(2, abs(estimate.screenShift) * 2),
                max(2, grid.width / 2 - 2)
            )
            let sign = estimate.direction.rawValue
            let endX = min(grid.width - 2, max(1, centerX + sign * length))
            let range = min(centerX, endX)...max(centerX, endX)
            for x in range { paint(x, centerY, color: accent) }
            paint(endX, centerY - 1, color: accent)
            paint(endX, centerY + 1, color: accent)
        }

        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data) else { return nil }
        return CGImage(
            width: grid.width,
            height: grid.height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: grid.width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedLast.rawValue
            ),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }
}

struct FrameEdgeBlackBand: Equatable, Sendable {
    /// Width measured inward from the corresponding image edge.
    let widthFraction: CGFloat
    /// Content span immediately inside the black band, in image coordinates.
    let contentYRangeFraction: ClosedRange<CGFloat>
}

struct FrameEdgeBlackBands: Equatable, Sendable {
    let left: FrameEdgeBlackBand?
    let right: FrameEdgeBlackBand?

    func band(at direction: VisualRoomDirection) -> FrameEdgeBlackBand? {
        direction == .left ? left : right
    }

    /// Converts detected loading-matte bands into a few straight atlas-mask
    /// rectangles. Keeping the interior intact avoids the ragged alpha holes
    /// produced by clearing every individually dark pixel.
    func rectangularOmissions(frameSize: CGSize) -> [CGRect] {
        guard frameSize.width.isFinite, frameSize.height.isFinite,
              frameSize.width > 0, frameSize.height > 0 else { return [] }
        let padding = max(1, (frameSize.width / 640).rounded(.up))
        let leftWidth = left.map {
            min(frameSize.width, max(0, $0.widthFraction * frameSize.width + padding))
        } ?? 0
        let rightWidth = right.map {
            min(frameSize.width, max(0, $0.widthFraction * frameSize.width + padding))
        } ?? 0
        guard leftWidth + rightWidth <= frameSize.width * 0.90 else { return [] }
        var result = [CGRect]()
        if leftWidth > 0 {
            result.append(CGRect(
                x: 0, y: 0,
                width: leftWidth, height: frameSize.height
            ))
        }
        if rightWidth > 0 {
            result.append(CGRect(
                x: frameSize.width - rightWidth, y: 0,
                width: rightWidth, height: frameSize.height
            ))
        }
        return result
    }
}

struct FrameSignalProfile: Equatable {
    let sampleCount: Int
    let signaledCount: Int
    let visibleCount: Int
    let meanPeak: Double
    let motionGrid: LowResolutionMotionGrid?
    let edgeBlackBands: FrameEdgeBlackBands?

    init(
        sampleCount: Int,
        signaledCount: Int,
        visibleCount: Int,
        meanPeak: Double,
        motionGrid: LowResolutionMotionGrid? = nil,
        edgeBlackBands: FrameEdgeBlackBands? = nil
    ) {
        self.sampleCount = sampleCount
        self.signaledCount = signaledCount
        self.visibleCount = visibleCount
        self.meanPeak = meanPeak
        self.motionGrid = motionGrid
        self.edgeBlackBands = edgeBlackBands
    }

    var signaledFraction: Double {
        guard sampleCount > 0 else { return 0 }
        return Double(signaledCount) / Double(sampleCount)
    }

    var visibleFraction: Double {
        guard sampleCount > 0 else { return 0 }
        return Double(visibleCount) / Double(sampleCount)
    }

    var hasGameplaySignal: Bool {
        signaledCount >= max(8, sampleCount / 20)
    }
}

enum FrameRegionRenderer {
    /// Reject only whole loading/transition frames. Black pixels inside a valid
    /// gameplay frame remain opaque because silhouettes are legitimate artwork.
    static func containsGameplaySignal(in frame: CGImage) -> Bool {
        signalProfile(in: frame).hasGameplaySignal
    }

    static func signalProfile(in frame: CGImage) -> FrameSignalProfile {
        let width = 64
        let height = max(16, Int((CGFloat(width) * CGFloat(frame.height)
            / max(1, CGFloat(frame.width))).rounded()))
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        guard let context = CGContext(
            data: &pixels,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return FrameSignalProfile(
                sampleCount: 1,
                signaledCount: 1,
                visibleCount: 1,
                meanPeak: 255
            )
        }
        context.interpolationQuality = .low
        context.draw(frame, in: CGRect(x: 0, y: 0, width: width, height: height))

        // Persistent HUD marks occupy less than this fraction, while even a
        // very dark room has low-level gray or colored signal across the frame.
        let sampledPixels = width * height
        var signaled = 0
        var visible = 0
        var peakTotal = 0
        for y in 0..<height {
            for x in 0..<width {
                let index = (y * width + x) * 4
                let peak = max(pixels[index], max(pixels[index + 1], pixels[index + 2]))
                if peak >= 5 { signaled += 1 }
                if peak >= 16 { visible += 1 }
                peakTotal += Int(peak)
            }
        }
        // Keep the complete 64x36 signal render for floorless odometry. The
        // previous second 2x2 average reduced this to 32x18: one accepted cell
        // then meant 20 camera pixels at the 640-wide solve size. In the dark
        // rooms from the recorded route that erased the remaining wall detail
        // and quantized ordinary 5-10 px movement into long freezes. This uses
        // no additional image render or capture copy; it only retains the luma
        // already resident in this tiny signal buffer.
        let motionWidth = width
        let motionHeight = height
        var motionLuma = [UInt8](repeating: 0, count: motionWidth * motionHeight)
        for y in 0..<motionHeight {
            for x in 0..<motionWidth {
                let index = (y * width + x) * 4
                // Integer BT.601 luma. This grid is reused by room transition
                // voting; no second image render is needed on capture path.
                let red = 77 * Int(pixels[index])
                let green = 150 * Int(pixels[index + 1])
                let blue = 29 * Int(pixels[index + 2])
                motionLuma[y * motionWidth + x] = UInt8((red + green + blue) >> 8)
            }
        }
        return FrameSignalProfile(
            sampleCount: sampledPixels,
            signaledCount: signaled,
            visibleCount: visible,
            meanPeak: Double(peakTotal) / Double(max(1, sampledPixels)),
            motionGrid: LowResolutionMotionGrid(
                width: motionWidth,
                height: motionHeight,
                luma: motionLuma
            ),
            edgeBlackBands: edgeBlackBands(
                pixels: pixels,
                width: width,
                height: height
            )
        )
    }

    private static func edgeBlackBands(
        pixels: [UInt8],
        width: Int,
        height: Int
    ) -> FrameEdgeBlackBands {
        func peak(x: Int, y: Int) -> UInt8 {
            let index = (y * width + x) * 4
            return max(pixels[index], max(pixels[index + 1], pixels[index + 2]))
        }

        func measure(fromLeft: Bool) -> FrameEdgeBlackBand? {
            var lastAcceptedDepth = -1
            var brightColumnRun = 0
            for depth in 0..<min(width * 3 / 4, width - 1) {
                let x = fromLeft ? depth : width - 1 - depth
                var signaledRows = 0
                for y in 0..<height where peak(x: x, y: y) >= 6 {
                    signaledRows += 1
                }
                if signaledRows <= max(2, height / 9) {
                    lastAcceptedDepth = depth
                    brightColumnRun = 0
                } else {
                    brightColumnRun += 1
                    if brightColumnRun >= 2 { break }
                }
            }
            let bandColumns = lastAcceptedDepth + 1
            guard bandColumns >= 2 else { return nil }

            let boundaryX = fromLeft ? bandColumns : width - bandColumns - 1
            let strip = (0..<6).compactMap { offset -> Int? in
                let x = boundaryX + (fromLeft ? offset : -offset)
                return (0..<width).contains(x) ? x : nil
            }
            var supportedRows = [Bool](repeating: false, count: height)
            for y in 0..<height {
                supportedRows[y] = strip.contains { peak(x: $0, y: y) >= 6 }
            }
            let range = longestSupportedRun(supportedRows)
            let defaultRange = CGFloat(0.38)...CGFloat(0.68)
            let normalizedRange: ClosedRange<CGFloat>
            if let range, range.count >= 3 {
                var lower = CGFloat(max(0, range.lowerBound - 1)) / CGFloat(height)
                var upper = CGFloat(min(height, range.upperBound + 2)) / CGFloat(height)
                let maximumHeight: CGFloat = 0.46
                if upper - lower > maximumHeight {
                    let center = (lower + upper) * 0.5
                    lower = max(0, center - maximumHeight * 0.5)
                    upper = min(1, lower + maximumHeight)
                }
                normalizedRange = lower...upper
            } else {
                normalizedRange = defaultRange
            }
            return FrameEdgeBlackBand(
                widthFraction: CGFloat(bandColumns) / CGFloat(width),
                contentYRangeFraction: normalizedRange
            )
        }

        return FrameEdgeBlackBands(
            left: measure(fromLeft: true),
            right: measure(fromLeft: false)
        )
    }

    private static func longestSupportedRun(
        _ rows: [Bool]
    ) -> Range<Int>? {
        var best: Range<Int>?
        var start: Int?
        var gap = 0
        for index in rows.indices {
            if rows[index] {
                if start == nil { start = index }
                gap = 0
            } else if start != nil {
                gap += 1
                if gap > 1 {
                    let candidate = start!..<(index - gap + 1)
                    if best.map({ candidate.count > $0.count }) ?? true { best = candidate }
                    start = nil
                    gap = 0
                }
            }
        }
        if let start {
            let candidate = start..<max(start, rows.count - gap)
            if best.map({ candidate.count > $0.count }) ?? true { best = candidate }
        }
        return best
    }

    static func mapFrame(
        from frame: CGImage,
        omitting regions: SceneRegions,
        additionalOmittedRects: [CGRect] = [],
        context: CIContext
    ) -> CGImage? {
        let extent = CGRect(x: 0, y: 0, width: frame.width, height: frame.height)
        var mask = CIImage(color: .black).cropped(to: extent)
        for rect in regions.omittedRects + additionalOmittedRects {
            let clipped = rect.intersection(extent)
            guard !clipped.isNull, !clipped.isEmpty else { continue }
            mask = CIImage(color: .white).cropped(to: clipped).composited(over: mask)
        }
        let cleared = CIImage(color: .clear).cropped(to: extent).applyingFilter(
            "CIBlendWithMask",
            parameters: [
                kCIInputBackgroundImageKey: CIImage(cgImage: frame),
                kCIInputMaskImageKey: mask,
            ]
        ).cropped(to: extent)
        return context.createCGImage(cleared, from: extent)
    }

    static func annotatedFrame(
        from frame: CGImage,
        regions: SceneRegions,
        objectDetections: [LiveObjectDetection] = [],
        objectIcons: [String: CGImage] = [:],
        featureTracking: FeatureTrackingResult = .empty,
        showFeatures: Bool = false,
        context: CIContext
    ) -> CGImage? {
        let extent = CGRect(x: 0, y: 0, width: frame.width, height: frame.height)
        var image = CIImage(cgImage: frame)
        if showFeatures {
            let markerSize = max(8, extent.width / 64)
            for feature in featureTracking.features {
                let color: CIColor
                switch feature.kind {
                case .candidate:
                    color = CIColor(red: 1.00, green: 0.76, blue: 0.16)
                case .landmark:
                    color = CIColor(red: 0.12, green: 0.92, blue: 1.00)
                case .relocalized:
                    color = CIColor(red: 0.82, green: 0.32, blue: 1.00)
                }
                let marker = CGRect(
                    x: feature.point.x - markerSize / 2,
                    y: feature.point.y - markerSize / 2,
                    width: markerSize,
                    height: markerSize
                ).intersection(extent)
                if let knight = regions.knight,
                   marker.intersects(knight.insetBy(dx: -2, dy: -2)) {
                    continue
                }
                for edge in borderRects(for: marker, lineWidth: max(1.5, extent.width / 480)) {
                    image = CIImage(color: color).cropped(to: edge).composited(over: image)
                }
                let center = CGRect(x: feature.point.x - 1.5, y: feature.point.y - 1.5, width: 3, height: 3)
                    .intersection(extent)
                image = CIImage(color: color).cropped(to: center).composited(over: image)
            }
        }
        var outlines: [(CGRect?, CIColor)] = objectDetections.map { detection in
            (
                detection.imageRect(width: frame.width, height: frame.height),
                LabelingVisualIdentity.ciColor(for: detection.classIdentifier)
            )
        }
        if showFeatures {
            outlines += regions.particles.map {
                ($0, CIColor(red: 1.00, green: 0.18, blue: 0.72))
            }
        }
        let lineWidth = max(2, extent.width / 320)
        for (optionalRect, color) in outlines {
            guard let rect = optionalRect?.intersection(extent), !rect.isNull, !rect.isEmpty else { continue }
            for edge in borderRects(for: rect, lineWidth: lineWidth) {
                image = CIImage(color: color).cropped(to: edge).composited(over: image)
            }
        }
        for detection in objectDetections {
            let canonical = LabelingClassIdentity.canonicalIdentifier(
                detection.classIdentifier
            )
            guard let icon = objectIcons[canonical],
                  let rect = detection.imageRect(width: frame.width, height: frame.height),
                  icon.width > 0, icon.height > 0,
                  let destination = objectIconDestination(
                    box: rect,
                    iconSize: CGSize(width: icon.width, height: icon.height),
                    extent: extent
                  ) else { continue }
            let color = LabelingVisualIdentity.ciColor(for: canonical)
            image = CIImage(color: CIColor(red: 0, green: 0, blue: 0, alpha: 0.82))
                .cropped(to: destination)
                .composited(over: image)
            for edge in borderRects(for: destination, lineWidth: 1) {
                image = CIImage(color: color).cropped(to: edge).composited(over: image)
            }
            let content = destination.insetBy(dx: 2, dy: 2)
            let scale = min(
                content.width / CGFloat(icon.width),
                content.height / CGFloat(icon.height)
            )
            let iconDestination = CGRect(
                x: content.midX - CGFloat(icon.width) * scale / 2,
                y: content.midY - CGFloat(icon.height) * scale / 2,
                width: CGFloat(icon.width) * scale,
                height: CGFloat(icon.height) * scale
            )
            let source = CIImage(cgImage: icon)
            let transformed = source.transformed(by: CGAffineTransform(
                translationX: -source.extent.minX,
                y: -source.extent.minY
            )).transformed(by: CGAffineTransform(scaleX: scale, y: scale))
                .transformed(by: CGAffineTransform(
                    translationX: iconDestination.minX,
                    y: iconDestination.minY
                ))
            image = transformed.cropped(to: iconDestination).composited(over: image)
        }
        return context.createCGImage(image.cropped(to: extent), from: extent)
    }

    /// Keeps the icon attached to a detection without covering pixels inside
    /// its rectangle. Core Image coordinates have their origin at bottom-left.
    static func objectIconDestination(
        box: CGRect,
        iconSize: CGSize,
        extent: CGRect
    ) -> CGRect? {
        guard iconSize.width > 0, iconSize.height > 0,
              !box.isEmpty, !extent.isEmpty else { return nil }
        let height = min(20, extent.height)
        let width = min(80, max(height, height * iconSize.width / iconSize.height))
        let gap: CGFloat = 4
        let clampedX = min(max(box.minX, extent.minX), extent.maxX - width)

        if box.maxY + gap + height <= extent.maxY {
            return CGRect(x: clampedX, y: box.maxY + gap, width: width, height: height)
        }
        if box.minY - gap - height >= extent.minY {
            return CGRect(x: clampedX, y: box.minY - gap - height, width: width, height: height)
        }

        let clampedY = min(max(box.midY - height / 2, extent.minY), extent.maxY - height)
        if box.maxX + gap + width <= extent.maxX {
            return CGRect(x: box.maxX + gap, y: clampedY, width: width, height: height)
        }
        if box.minX - gap - width >= extent.minX {
            return CGRect(x: box.minX - gap - width, y: clampedY, width: width, height: height)
        }
        return nil
    }

    private static func borderRects(for rect: CGRect, lineWidth: CGFloat) -> [CGRect] {
        [
            CGRect(x: rect.minX, y: rect.minY, width: rect.width, height: lineWidth),
            CGRect(x: rect.minX, y: rect.maxY - lineWidth, width: rect.width, height: lineWidth),
            CGRect(x: rect.minX, y: rect.minY, width: lineWidth, height: rect.height),
            CGRect(x: rect.maxX - lineWidth, y: rect.minY, width: lineWidth, height: rect.height),
        ]
    }
}
