import CoreGraphics
import Foundation

/// Pixel references extracted once from the intentionally atomic labeling example.
/// No dependency on the labeling database remains after extraction.
struct HUDStencilTemplate: Decodable {
    struct Atom: Decodable {
        let kind: String
        let rect: [Double] // top-origin reference pixels
        let rgb: Data
        var bounds: CGRect { CGRect(x: rect[0], y: rect[1], width: rect[2], height: rect[3]) }
    }
    let schemaVersion: Int
    let sourceExampleID: String
    let referenceWidth: Int
    let referenceHeight: Int
    let healthPitch: Double
    let health: [Atom]
    let manaMain: [Atom]
    let manaContainer: [Double]
    let manaReserves: [Atom]
    let geo: [Double]

    static func captured(examples: [SavedLabelingExample], languageIdentifier: String) -> HUDStencilTemplate? {
        guard let example = StencilInitializationPolicy.representativeExample(
            examples: examples, context: .gameplayHUD, languageIdentifier: languageIdentifier, mode: .normal
        ), let image = try? LabelingExampleStore().loadImage(for: example),
        let pixels = ImageStencilPixels(image, referenceWidth: 640, referenceHeight: 360, band: 0..<80)
        else { return nil }
        let boxes = example.manifest.annotations.filter {
            !$0.isHardNegative && $0.classIdentifier == "game.health"
        }.map { annotation in
            let box = annotation.normalizedRect
            return CGRect(x: box.minX * 640, y: box.minY * 360,
                          width: box.width * 640, height: box.height * 360).integral
        }.sorted { $0.minX < $1.minX }
        guard boxes.count == 2, boxes[0].maxX <= boxes[1].minX,
              abs(boxes[0].midY - boxes[1].midY) < max(boxes[0].height, boxes[1].height) / 2,
              boxes.allSatisfy({ $0.minY >= 0 && $0.maxY <= 80 && $0.maxX <= 320 }) else { return nil }
        let atoms = boxes.compactMap { box -> Atom? in
            guard let reference = pixels.reference(in: box) else { return nil }
            return Atom(kind: "full", rect: [box.minX, box.minY, box.width, box.height], rgb: reference.rgb)
        }
        let result = Self(schemaVersion: 1, sourceExampleID: example.id.uuidString,
            referenceWidth: 640, referenceHeight: 360, healthPitch: boxes[1].midX - boxes[0].midX,
            health: atoms, manaMain: [], manaContainer: [0, 0, 0, 0], manaReserves: [], geo: [0, 0, 0, 0])
        guard atoms.count == 2 && result.isValid,
              HUDStencilTracker(template: result).observe(image, gameplay: true, timestamp: 0, generation: 0, allowRowRecovery: false)?.establishesGameplay == true
        else { return nil }
        return result
    }

    var isValid: Bool {
        schemaVersion == 1 && referenceWidth > 0 && referenceHeight > 0
            && healthPitch.isFinite && healthPitch > 4 && !health.isEmpty
            && manaContainer.count == 4 && geo.count == 4
            && (health + manaMain + manaReserves).allSatisfy {
                $0.rect.count == 4 && $0.rect.allSatisfy(\.isFinite)
                    && $0.rect[2] > 0 && $0.rect[3] > 0
                    && $0.rgb.count == Int($0.rect[2]) * Int($0.rect[3]) * 3
            }
    }
}

struct HUDStencilMatch: Equatable {
    let index: Int
    let kind: String
    let rect: CGRect // Core Image bottom-origin pixels
    let confidence: Double
}

struct HUDStencilResult: Equatable {
    let health: [HUDStencilMatch]
    let manaMain: HUDStencilMatch?
    let manaReserves: [HUDStencilMatch]
    let healthMasks: [CGRect]
    let manaMasks: [CGRect]
    let geo: CGRect
    let holdsHealthMask: Bool
    let sourceTimestamp: Double

    init(
        health: [HUDStencilMatch],
        manaMain: HUDStencilMatch? = nil,
        manaReserves: [HUDStencilMatch],
        healthMasks: [CGRect],
        manaMasks: [CGRect],
        geo: CGRect,
        holdsHealthMask: Bool,
        sourceTimestamp: Double
    ) {
        self.health = health
        self.manaMain = manaMain
        self.manaReserves = manaReserves
        self.healthMasks = healthMasks
        self.manaMasks = manaMasks
        self.geo = geo
        self.holdsHealthMask = holdsHealthMask
        self.sourceTimestamp = sourceTimestamp
    }
    var omittedRects: [CGRect] { healthMasks + manaMasks + [geo] }
    var healthBounds: CGRect { healthMasks.reduce(CGRect.null) { $0.union($1) } }
    var manaBounds: CGRect { manaMasks.reduce(CGRect.null) { $0.union($1) } }
    /// Two independently matched masks provide the HUD side of gameplay
    /// entry evidence. The state gates also require an object-model Knight
    /// detection. Matching enforces the row's bounded top-left position,
    /// texture, colour, and horizontal lattice. Soul is not a prerequisite.
    var establishesGameplay: Bool { health.count >= 2 }
}

/// All matching uses the original capture, before ground intensity correction.
/// The bounded top-left HUD search never consults gameplay/player-state telemetry.
final class HUDStencilTracker {
    private static let localRowRadius = 4
    private static let maximumRowOffset = 32
    private let template: HUDStencilTemplate?
    private let healthKernels: [HUDStencilKernel]
    private let manaMainKernels: [HUDStencilKernel]
    private let manaKernels: [HUDStencilKernel]
    private var generation: UInt64?
    private var previousHealthMasks = [CGRect]()
    private var previousManaMasks = [CGRect]()
    private var confirmedViewportScale: CGFloat = 1
    private var confirmedRowOffsetY: CGFloat = 0
    private var lastRowSearch: Double = -.infinity
    private var lastHealthSeen: Double = -.infinity
    private var lastManaSeen: Double = -.infinity

    init(template: HUDStencilTemplate? = nil) {
        self.template = template?.isValid == true ? template : nil
        healthKernels = Self.healthVariants(self.template?.health ?? []).map { HUDStencilKernel($0) }
        manaMainKernels = self.template?.manaMain.map {
            HUDStencilKernel($0, mainManaOnly: true)
        } ?? []
        manaKernels = Self.reserveVariants(self.template?.manaReserves ?? []).flatMap {
            [HUDStencilKernel($0), HUDStencilKernel($0, opaqueManaOnly: true)]
        }
    }

    /// The user labels filled white masks. Lifeblood keeps that same face
    /// shape under a blue tint; derive its colour variant from those pixels.
    private static func healthVariants(_ atoms: [HUDStencilTemplate.Atom]) -> [HUDStencilTemplate.Atom] {
        guard !atoms.contains(where: { $0.kind == "lifeblood" }) else { return atoms }
        return atoms + atoms.filter { $0.kind == "full" }.map { atom in
            var rgb = [UInt8](atom.rgb)
            for index in stride(from: 0, to: rgb.count, by: 3) {
                rgb[index] = UInt8(Double(rgb[index]) * 0.45)
                rgb[index + 1] = UInt8(Double(rgb[index + 1]) * 0.75)
            }
            return .init(kind: "lifeblood", rect: atom.rect, rgb: Data(rgb))
        }
    }

    // Vessel fill changes its interior while its position stays fixed. Build
    // quarter/half/three-quarter references from the labeled empty and full
    // atoms, so an obtained but partially filled vessel is still masked.
    private static func reserveVariants(_ atoms: [HUDStencilTemplate.Atom]) -> [HUDStencilTemplate.Atom] {
        guard let full = atoms.first(where: { $0.kind == "full" }) else { return atoms }
        var variants = atoms
        for empty in atoms where empty.kind == "empty" {
            let w = Int(empty.bounds.width), h = Int(empty.bounds.height)
            let fw = Int(full.bounds.width), fh = Int(full.bounds.height)
            let dark = [UInt8](empty.rgb), light = [UInt8](full.rgb)
            for fraction in [0.25, 0.5, 0.75] {
                var rgb = dark
                let boundary = (1 - fraction) * Double(h)
                for y in 0..<h { for x in 0..<w {
                    let fx = min(fw - 1, Int((Double(x) + 0.5) * Double(fw) / Double(w)))
                    let fy = min(fh - 1, Int((Double(y) + 0.5) * Double(fh) / Double(h)))
                    let weight = max(0, min(1, Double(y) + 1 - boundary))
                    for c in 0..<3 {
                        let i = (y * w + x) * 3 + c, j = (fy * fw + fx) * 3 + c
                        rgb[i] = UInt8((Double(dark[i]) * (1 - weight) + Double(light[j]) * weight).rounded())
                    }
                } }
                variants.append(.init(kind: "partial", rect: empty.rect, rgb: Data(rgb)))
            }
        }
        return variants
    }

    func reset() {
        generation = nil
        previousHealthMasks = []
        confirmedViewportScale = 1
        confirmedRowOffsetY = 0
        lastRowSearch = -.infinity
        previousManaMasks = []
        lastHealthSeen = -.infinity
        lastManaSeen = -.infinity
    }

    func observe(_ image: CGImage, gameplay: Bool, timestamp: Double,
                 generation: UInt64, allowRowRecovery: Bool = true) -> HUDStencilResult? {
        guard gameplay, let template, template.isValid else { reset(); return nil }
        if self.generation != generation { reset(); self.generation = generation }
        guard let band = ImageStencilPixels(
            image,
            referenceWidth: template.referenceWidth,
            referenceHeight: template.referenceHeight,
            band: 0..<min(template.referenceHeight, 80)
        ) else { return nil }
        var pixels = HUDPixels(band: band, referenceHeight: CGFloat(template.referenceHeight),
                               scale: confirmedViewportScale)
        let scaleX = CGFloat(image.width) / CGFloat(template.referenceWidth)
        let scaleY = CGFloat(image.height) / CGFloat(template.referenceHeight)
        let extent = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        func outputRect(_ rect: CGRect, margin: CGFloat = 0) -> CGRect {
            let expanded = pixels.frameRect(rect.insetBy(dx: -margin, dy: -margin))
            return CGRect(x: expanded.minX * scaleX,
                          y: CGFloat(image.height) - expanded.maxY * scaleY,
                          width: expanded.width * scaleX, height: expanded.height * scaleY)
                .intersection(extent)
        }
        func rect(_ values: [Double]) -> CGRect {
            CGRect(x: values[0], y: values[1], width: values[2], height: values[3])
        }
        let localLimit = CGFloat(Self.maximumRowOffset - Self.localRowRadius)
        let localCenter = min(localLimit, max(-localLimit, confirmedRowOffsetY))
        var row = matchHealthRow(in: pixels, template: template, offsetY: localCenter,
                                 verticalRadius: Self.localRowRadius, outputRect: outputRect)
        if allowRowRecovery, row.health.count < 2, timestamp - lastRowSearch >= 0.25 {
            lastRowSearch = timestamp
            // A title bar inside a wide capture scales the game about the
            // bottom center of the presentation. Fit both axes and icon pitch
            // together. The inverse layouts also handle a windowed reference.
            for scale: CGFloat in [1, 0.95, 1 / 0.95, 0.90, 1 / 0.90] {
                pixels.scale = scale
                // Returning from a windowed reference to a tall display can
                // also remove rows from the top of the presentation.
                let radius = scale >= 1 ? Self.maximumRowOffset : Self.localRowRadius * 2
                let candidate = matchHealthRow(in: pixels, template: template, offsetY: 0,
                                               verticalRadius: radius, outputRect: outputRect)
                guard candidate.health.count >= 2 else { continue }
                // After a resize the second mask can land where the first
                // used to be. Reject that alignment before caching the row.
                let precedingCenter = template.health[0].bounds.midX + candidate.offset.x - template.healthPitch
                if let preceding = bestHealth(in: pixels, center: precedingCenter, offsetY: candidate.offset.y,
                                              radius: 2, verticalRadius: 1),
                   preceding.score >= threshold(for: preceding.atom.kind) { continue }
                row = candidate
                if confirmedViewportScale != scale {
                    previousHealthMasks = []
                    previousManaMasks = []
                }
                confirmedViewportScale = scale
                break
            }
            pixels.scale = confirmedViewportScale
        }
        let health = row.health
        var healthMasks = row.masks
        let offset = row.offset
        if health.count >= 2 { confirmedRowOffsetY = offset.y }
        let holdingHealth = healthMasks.count < previousHealthMasks.count
            && timestamp >= lastHealthSeen && timestamp - lastHealthSeen <= 0.5
        if holdingHealth { healthMasks = previousHealthMasks }
        else {
            previousHealthMasks = healthMasks
            lastHealthSeen = timestamp
        }
        var manaMain: HUDStencilMatch?
        if let firstManaMain = template.manaMain.first {
            let expected = firstManaMain.bounds.offsetBy(dx: offset.x, dy: offset.y)
            var best: (Double, CGRect, String)?
            for dy in -2...2 { for dx in -2...2 {
                let candidate = expected.offsetBy(dx: CGFloat(dx), dy: CGFloat(dy))
                for kernel in manaMainKernels {
                    let candidateScore = score(kernel, in: pixels, at: candidate)
                    if candidateScore > (best?.0 ?? 0) {
                        best = (candidateScore, candidate, kernel.kind)
                    }
                }
            } }
            if let best, best.0 >= 0.70 {
                manaMain = HUDStencilMatch(
                    index: 0,
                    kind: best.2,
                    rect: outputRect(best.1),
                    confidence: best.0
                )
            }
        }
        var mana = [HUDStencilMatch]()
        var reserveMasks = [CGRect]()
        for (index, position) in template.manaReserves.enumerated() {
            let expected = position.bounds.offsetBy(dx: offset.x, dy: offset.y)
            var best: (Double, CGRect, String)?
            // Vessel sprites also pulse slightly while their fill changes.
            for scale in [0.8, 1.0, 1.2] {
              let w = (expected.width * scale).rounded(), h = (expected.height * scale).rounded()
              let scaled = CGRect(x: (expected.midX - w / 2).rounded(),
                                  y: (expected.midY - h / 2).rounded(), width: w, height: h)
              for dy in -2...2 { for dx in -2...2 {
                let candidate = scaled.offsetBy(dx: CGFloat(dx), dy: CGFloat(dy))
                for atom in manaKernels {
                    let score = score(atom, in: pixels, at: candidate)
                    if score > (best?.0 ?? 0) { best = (score, candidate, atom.kind) }
                }
              } }
            }
            if let best, best.0 >= 0.72 {
                mana.append(HUDStencilMatch(index: index, kind: best.2,
                    rect: outputRect(best.1), confidence: best.0))
                reserveMasks.append(outputRect(expected.union(best.1), margin: 2))
            }
        }
        if reserveMasks.count < previousManaMasks.count,
           timestamp >= lastManaSeen, timestamp - lastManaSeen <= 0.5 {
            reserveMasks = previousManaMasks
        } else {
            previousManaMasks = reserveMasks
            lastManaSeen = timestamp
        }
        return HUDStencilResult(health: health, manaMain: manaMain, manaReserves: mana,
            healthMasks: healthMasks,
            manaMasks: (rect(template.manaContainer).isEmpty ? []
                : [outputRect(rect(template.manaContainer).offsetBy(dx: offset.x, dy: offset.y), margin: 2)]) + reserveMasks,
            geo: rect(template.geo).isEmpty ? .zero
                : outputRect(rect(template.geo).offsetBy(dx: offset.x, dy: offset.y), margin: 1),
            holdsHealthMask: holdingHealth, sourceTimestamp: timestamp)
    }

    private func matchHealthRow(in pixels: HUDPixels, template: HUDStencilTemplate,
                                offsetY: CGFloat, verticalRadius: Int,
                                outputRect: (CGRect, CGFloat) -> CGRect)
        -> (health: [HUDStencilMatch], masks: [CGRect], offset: CGPoint) {
        let firstCenter = template.health[0].bounds.midX
        var offset = CGPoint.zero
        var pitch = template.healthPitch
        var health = [HUDStencilMatch]()
        var healthMasks = [CGRect]()
        var misses = 0
        // Empty masks are not part of a two-filled-mask capture. Keep the
        // fixed lattice through gaps to find the blue masks after them. A gap
        // is never counted as a recognized health icon.
        for index in 0..<32 {
            let center = firstCenter + CGFloat(index) * pitch + offset.x
            guard center < CGFloat(template.referenceWidth) - 8 else { break }
            let found = bestHealth(in: pixels, center: center, offsetY: index == 0 ? offsetY : offset.y,
                                   radius: index == 0 ? 7 : 2, verticalRadius: index == 0 ? verticalRadius : 1)
            guard let found, found.score >= threshold(for: found.atom.kind) else {
                misses += 1
                if health.isEmpty || misses > (health.count < 2 ? 9 : 1) { break }
                continue
            }
            misses = 0
            if health.isEmpty {
                offset = CGPoint(x: found.rect.midX - firstCenter,
                                 y: found.rect.minY - found.atom.bounds.minY)
            } else if pixels.scale != 1 {
                // A pair labeled on integer pixels can round its spacing by
                // one pixel. Average over the verified slots to avoid
                // accumulating that error toward the far end of a long row.
                let observedPitch = (found.rect.midX - firstCenter - offset.x) / CGFloat(index)
                pitch = min(template.healthPitch + 1, max(template.healthPitch - 1, observedPitch))
            }
            let previousIndex = health.last?.index
            health.append(HUDStencilMatch(index: index, kind: found.atom.kind,
                rect: outputRect(found.rect, 0), confidence: found.score))
            // The gap contains the connector between masks. Cover the whole
            // lattice cell plus glow, rather than just the bright icon core.
            let cellCenter = index == 0 ? found.rect.midX : center
            let cell = CGRect(x: cellCenter - pitch / 2,
                              y: found.rect.minY - 1,
                              width: pitch, height: found.rect.height + 2)
            // Include unclassified cells only in the exclusion mask, never in
            // the health count, once a later icon confirms their lattice.
            if let previous = healthMasks.last, let previousIndex, index - previousIndex > 1 {
                let last = outputRect(cell, 2)
                healthMasks.append(previous.union(last))
            } else { healthMasks.append(outputRect(cell, 2)) }
        }
        return (health, healthMasks, offset)
    }

    private struct HUDPixels {
        let band: ImageStencilPixels
        let referenceHeight: CGFloat
        var scale: CGFloat

        func frameRect(_ rect: CGRect) -> CGRect {
            guard scale != 1 else { return rect }
            return CGRect(x: rect.minX * scale + CGFloat(band.width) * (1 - scale) / 2,
                          y: rect.minY * scale + referenceHeight * (1 - scale),
                          width: rect.width * scale, height: rect.height * scale)
        }

        func compare(_ kernel: ImageStencilKernel, at rect: CGRect) -> ImageStencilComparison? {
            guard scale != 1 else { return band.compare(kernel, at: rect) }
            let target = frameRect(rect)
            return band.compare(kernel, at: CGRect(x: target.minX.rounded(), y: target.minY.rounded(),
                                                   width: target.width.rounded(), height: target.height.rounded()))
        }
    }

    private struct Candidate {
        let atom: HUDStencilKernel
        let rect: CGRect
        let score: Double
    }
    private func threshold(for kind: String) -> Double { kind == "empty" ? 0.78 : 0.75 }

    private func bestHealth(in pixels: HUDPixels, center: CGFloat, offsetY: CGFloat,
                            radius: Int, verticalRadius: Int) -> Candidate? {
        var best: Candidate?
        for atom in healthKernels {
            for dy in -verticalRadius...verticalRadius { for dx in -radius...radius {
                let bounds = CGRect(x: (center - atom.bounds.width / 2).rounded() + CGFloat(dx),
                    y: atom.bounds.minY + offsetY + CGFloat(dy),
                    width: atom.bounds.width, height: atom.bounds.height)
                let score = score(atom, in: pixels, at: bounds)
                if score > (best?.score ?? 0) { best = Candidate(atom: atom, rect: bounds, score: score) }
            } }
        }
        return best
    }

    private func score(
        _ atom: HUDStencilKernel,
        in pixels: HUDPixels,
        at rect: CGRect
    ) -> Double {
        guard let comparison = pixels.compare(atom.stencil, at: rect) else { return 0 }
        if atom.kind == "lifeblood",
           comparison.meanBlue - comparison.meanRed < 30 { return 0 }
        if atom.kind == "full",
           comparison.meanBlue - comparison.meanRed > 24 { return 0 }
        guard comparison.normalizedCorrelation >= 0.6,
              comparison.meanAbsoluteColorError <= (atom.kind == "empty" ? 18 : 58) else {
            return 0
        }
        return comparison.confidence(correlationWeight: 0.75, colorErrorScale: 80)
    }
}

/// Precompute the opaque interior and reference statistics once. In particular,
/// the first mask's ornament and an empty mask's transparent rim are not evidence:
/// including those pixels makes the score depend on the scenery behind the HUD.
private struct HUDStencilKernel {
    let stencil: ImageStencilKernel
    var kind: String { stencil.kind }
    var bounds: CGRect { stencil.bounds }

    init(
        _ atom: HUDStencilTemplate.Atom,
        opaqueManaOnly: Bool = false,
        mainManaOnly: Bool = false
    ) {
        let kind = atom.kind
        let bounds = atom.bounds
        let reference = [UInt8](atom.rgb)
        stencil = ImageStencilKernel(
            kind: kind,
            bounds: bounds,
            rgb: atom.rgb
        ) { x, y, width, height in
            if mainManaOnly {
                let index = (y * width + x) * 3
                guard index + 2 < reference.count else { return false }
                let channels = [
                    Int(reference[index]),
                    Int(reference[index + 1]),
                    Int(reference[index + 2]),
                ]
                let edge = x < 15 || x >= width - 15 || y < 10 || y >= height - 10
                return edge
                    && (channels.min() ?? 0) >= 70
                    && (channels.max() ?? 0) - (channels.min() ?? 0) <= 60
            }
            if height >= 14 {
                if kind == "empty" {
                    let nx = (Double(x) - Double(width) * 0.5) / (Double(width) * 0.27)
                    let ny = (Double(y) - Double(height) * 0.44) / (Double(height) * 0.30)
                    if nx * nx + ny * ny > 1 { return false }
                } else if x < 2 || x >= width - 2 || y < 3 || y >= height - 2 {
                    return false
                }
            }
            if height < 14 && opaqueManaOnly {
                let nx = (Double(x) + 0.5 - Double(width) * 0.5) / (Double(width) * 0.40)
                let ny = (Double(y) + 0.5 - Double(height) * 0.5) / (Double(height) * 0.40)
                if nx * nx + ny * ny > 1 { return false }
            }
            return true
        }
    }
}

/// Transparent overlay of the exact rectangles excluded from atlas/tracking.
/// Rendered only when explicitly enabled in Debug View.
enum HUDStencilRenderer {
    static func overlay(_ stencil: HUDStencilResult, width: Int, height: Int) -> CGImage? {
        guard let context = CGContext(data: nil, width: width, height: height,
            bitsPerComponent: 8, bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        func mark(_ rect: CGRect, red: CGFloat, green: CGFloat, blue: CGFloat) {
            context.setFillColor(CGColor(red: red, green: green, blue: blue, alpha: 0.35))
            context.fill(rect)
            context.setStrokeColor(CGColor(red: red, green: green, blue: blue, alpha: 1))
            context.setLineWidth(1)
            context.stroke(rect.insetBy(dx: 0.5, dy: 0.5))
        }
        for rect in stencil.healthMasks { mark(rect, red: 1, green: 0.25, blue: 0.25) }
        for rect in stencil.manaMasks { mark(rect, red: 0.25, green: 0.8, blue: 1) }
        if let main = stencil.manaMain {
            context.setStrokeColor(CGColor(red: 0.25, green: 0.8, blue: 1, alpha: 1))
            context.setLineWidth(2)
            context.stroke(main.rect.insetBy(dx: 1, dy: 1))
        }
        mark(stencil.geo, red: 1, green: 0.85, blue: 0.1)
        for match in stencil.health {
            context.setStrokeColor(match.kind == "lifeblood"
                ? CGColor(red: 0, green: 0.9, blue: 1, alpha: 1)
                : match.kind == "empty"
                    ? CGColor(red: 1, green: 0.65, blue: 0, alpha: 1)
                    : CGColor(gray: 1, alpha: 1))
            context.stroke(match.rect)
        }
        return context.makeImage()
    }
}
