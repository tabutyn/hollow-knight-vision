import CoreGraphics
import Foundation

/// Refines an already accepted integer ground-texture match by at most 1.25
/// pixels per axis. Fit and validation use disjoint checkerboard samples. This
/// cannot rescue a rejected coarse match or use Hacker telemetry.
enum GroundSubpixelRegistration {
    struct Patch {
        let x: Int
        let y: Int
        let pixels: [UInt8]
    }

    struct Result {
        let offset: CGVector
        let validationError: Double
        let patches: Int
        let iterations: Int
    }

    private struct Evaluation {
        var loss = 0.0
        var count = 0
        var errors = [Double]()
        var h00 = 0.0
        var h01 = 0.0
        var h11 = 0.0
        var g0 = 0.0
        var g1 = 0.0

        var meanError: Double {
            errors.reduce(0, +) / Double(max(1, errors.count))
        }
    }

    static func refine(
        patches: [Patch],
        pixels: [UInt8],
        width: Int,
        height: Int,
        dx: Int,
        dy: Int
    ) -> Result? {
        guard width > 4, height > 4, pixels.count == width * height,
              patches.count >= 4,
              patches.allSatisfy({ $0.pixels.count == 192 })
        else { return nil }

        let baseline = evaluate(
            offsetX: 0, offsetY: 0, patches: patches, pixels: pixels,
            width: width, height: height, dx: dx, dy: dy,
            validation: true, derivatives: false
        )
        guard baseline.errors.count == patches.count,
              baseline.meanError > 0.15 else { return nil }

        var offsetX = 0.0
        var offsetY = 0.0
        var iterations = 0
        for _ in 0..<6 {
            let current = evaluate(
                offsetX: offsetX, offsetY: offsetY, patches: patches,
                pixels: pixels, width: width, height: height, dx: dx, dy: dy,
                validation: false, derivatives: true
            )
            guard current.errors.count == patches.count, current.count > 0 else { break }
            let damping = 0.01 * Double(current.count)
            let h00 = current.h00 + damping
            let h11 = current.h11 + damping
            let determinant = h00 * h11 - current.h01 * current.h01
            guard abs(determinant) > 0.000_001 else { break }
            let stepX = (current.g0 * h11 - current.g1 * current.h01) / determinant
            let stepY = (current.g1 * h00 - current.g0 * current.h01) / determinant
            iterations += 1
            var accepted = false
            for scale in [1.0, 0.5, 0.25] {
                let proposedX = offsetX - stepX * scale
                let proposedY = offsetY - stepY * scale
                guard proposedX.isFinite, proposedY.isFinite,
                      abs(proposedX) <= 1.25, abs(proposedY) <= 1.25 else { continue }
                let candidate = evaluate(
                    offsetX: proposedX, offsetY: proposedY, patches: patches,
                    pixels: pixels, width: width, height: height, dx: dx, dy: dy,
                    validation: false, derivatives: false
                )
                if candidate.errors.count == current.errors.count,
                   candidate.loss + 0.000_1 < current.loss {
                    offsetX = proposedX
                    offsetY = proposedY
                    accepted = true
                    break
                }
            }
            if !accepted || max(abs(stepX), abs(stepY)) < 0.005 { break }
        }

        let refined = evaluate(
            offsetX: offsetX, offsetY: offsetY, patches: patches,
            pixels: pixels, width: width, height: height, dx: dx, dy: dy,
            validation: true, derivatives: false
        )
        guard refined.errors.count == baseline.errors.count,
              refined.meanError + 0.08 < baseline.meanError * 0.98,
              zip(refined.errors, baseline.errors).filter({ $0 <= $1 + 0.1 }).count * 3
                >= baseline.errors.count * 2
        else { return nil }
        return Result(
            offset: CGVector(dx: offsetX, dy: offsetY),
            validationError: refined.meanError,
            patches: patches.count,
            iterations: iterations
        )
    }

    private static func evaluate(
        offsetX: Double,
        offsetY: Double,
        patches: [Patch],
        pixels: [UInt8],
        width: Int,
        height: Int,
        dx: Int,
        dy: Int,
        validation: Bool,
        derivatives: Bool
    ) -> Evaluation {
        var result = Evaluation()
        for patch in patches {
            var samples = [(source: Double, target: Double, gx: Double, gy: Double)]()
            samples.reserveCapacity(96)
            for row in 0..<12 {
                for column in stride(
                    from: (row + (validation ? 1 : 0)) % 2,
                    to: 16,
                    by: 2
                ) {
                    let x = Double(patch.x + column + dx) + offsetX
                    let y = Double(patch.y + row + dy) + offsetY
                    guard x >= 1, y >= 1,
                          x < Double(width - 2), y < Double(height - 2) else { continue }
                    let target = sample(pixels, width: width, x: x, y: y)
                    let gx = derivatives
                        ? (sample(pixels, width: width, x: x + 1, y: y)
                            - sample(pixels, width: width, x: x - 1, y: y)) * 0.5
                        : 0
                    let gy = derivatives
                        ? (sample(pixels, width: width, x: x, y: y + 1)
                            - sample(pixels, width: width, x: x, y: y - 1)) * 0.5
                        : 0
                    samples.append((
                        Double(patch.pixels[row * 16 + column]), target, gx, gy
                    ))
                }
            }
            guard samples.count == 96 else { continue }
            let sourceMean = samples.reduce(0) { $0 + $1.source } / 96
            let targetMean = samples.reduce(0) { $0 + $1.target } / 96
            let sourceEnergy = samples.reduce(0) { $0 + pow($1.source - sourceMean, 2) }
            let targetEnergy = samples.reduce(0) { $0 + pow($1.target - targetMean, 2) }
            guard targetEnergy >= max(96, sourceEnergy * 0.1) else { continue }
            let gain = min(2, max(0.5, sqrt(sourceEnergy / targetEnergy)))
            var patchError = 0.0
            for value in samples {
                let residual = (value.target - targetMean) * gain
                    - (value.source - sourceMean)
                let magnitude = abs(residual)
                let weight = min(1, 12 / max(magnitude, 0.000_1))
                result.loss += magnitude <= 12
                    ? residual * residual : 24 * magnitude - 144
                patchError += magnitude
                if derivatives {
                    let gx = value.gx * gain
                    let gy = value.gy * gain
                    result.g0 += weight * gx * residual
                    result.g1 += weight * gy * residual
                    result.h00 += weight * gx * gx
                    result.h01 += weight * gx * gy
                    result.h11 += weight * gy * gy
                }
            }
            result.count += samples.count
            result.errors.append(patchError / 96)
        }
        result.loss /= Double(max(1, result.count))
        return result
    }

    private static func sample(
        _ pixels: [UInt8], width: Int, x: Double, y: Double
    ) -> Double {
        let ix = Int(x)
        let iy = Int(y)
        let fx = x - Double(ix)
        let fy = y - Double(iy)
        let index = iy * width + ix
        let top = Double(pixels[index]) * (1 - fx)
            + Double(pixels[index + 1]) * fx
        let bottom = Double(pixels[index + width]) * (1 - fx)
            + Double(pixels[index + width + 1]) * fx
        return top * (1 - fy) + bottom * fy
    }
}
