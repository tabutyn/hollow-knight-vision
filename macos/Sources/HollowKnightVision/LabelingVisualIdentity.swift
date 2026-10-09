import CoreImage
import SwiftUI

/// Stable, class-specific presentation shared by labeling, review, and live
/// detection. Canonical identifiers intentionally share one visual identity.
enum LabelingVisualIdentity {
    private static let identifiers = Array(Set(LabelingCatalogPolicy.classIdentifiers.map(
        LabelingClassIdentity.canonicalIdentifier
    ))).sorted()
    private static let indices = Dictionary(uniqueKeysWithValues:
        identifiers.enumerated().map { ($0.element, $0.offset) })
    private static let names: [String: String] = {
        var names = [String: String]()
        for label in LabelingContext.allCases.flatMap(\.labels) {
            let key = LabelingClassIdentity.canonicalIdentifier(label.id)
            if names[key] == nil { names[key] = label.name }
        }
        return names
    }()

    static func color(for classIdentifier: String) -> Color {
        let components = rgb(for: classIdentifier)
        return Color(
            red: components.red,
            green: components.green,
            blue: components.blue
        )
    }

    static func ciColor(for classIdentifier: String) -> CIColor {
        let components = rgb(for: classIdentifier)
        return CIColor(
            red: components.red,
            green: components.green,
            blue: components.blue,
            alpha: 1
        )
    }

    static func displayName(for classIdentifier: String) -> String {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        return names[canonical] ?? canonical
    }

    static func rgb(for classIdentifier: String) -> (red: Double, green: Double, blue: Double) {
        let canonical = LabelingClassIdentity.canonicalIdentifier(classIdentifier)
        let index = indices[canonical] ?? stableFallbackIndex(canonical)
        let count = max(identifiers.count, 1)
        // Equal hue spacing gives every registered object a distinct color.
        let hue = Double(index % count) / Double(count)
        return hsv(hue: hue, saturation: 0.78, value: 1)
    }

    private static func stableFallbackIndex(_ value: String) -> Int {
        value.utf8.reduce(5381) { (($0 << 5) &+ $0) &+ Int($1) } & Int.max
    }

    private static func hsv(
        hue: Double,
        saturation: Double,
        value: Double
    ) -> (red: Double, green: Double, blue: Double) {
        let h = (hue - floor(hue)) * 6
        let sector = Int(floor(h))
        let fraction = h - floor(h)
        let p = value * (1 - saturation)
        let q = value * (1 - saturation * fraction)
        let t = value * (1 - saturation * (1 - fraction))
        switch sector % 6 {
        case 0: return (value, t, p)
        case 1: return (q, value, p)
        case 2: return (p, value, t)
        case 3: return (p, q, value)
        case 4: return (t, p, value)
        default: return (value, p, q)
        }
    }
}

/// Keeps toolbar icons at one readable height while preserving the crop's
/// natural aspect ratio. Wide headings should look wide instead of being
/// squeezed into the same square-ish slot as compact symbols.
struct LabelingToolbarObjectIcon: View {
    let image: CGImage
    let classIdentifier: String
    var height: CGFloat = 28

    private var width: CGFloat {
        guard image.height > 0 else { return height }
        return max(height, height * CGFloat(image.width) / CGFloat(image.height))
    }

    var body: some View {
        Image(decorative: image, scale: 1)
            .resizable()
            .interpolation(.high)
            .scaledToFit()
            .frame(width: width, height: height)
            .fixedSize()
            .accessibilityLabel(
                "Selected object \(LabelingVisualIdentity.displayName(for: classIdentifier))"
            )
    }
}

struct LabelingObjectLegend: View {
    let classIdentifiers: [String]
    var objectAtlas: LabelingObjectAtlasSnapshot? = nil

    var body: some View {
        let canonical = Array(Set(classIdentifiers.map(
            LabelingClassIdentity.canonicalIdentifier
        ))).sorted {
            LabelingVisualIdentity.displayName(for: $0)
                < LabelingVisualIdentity.displayName(for: $1)
        }
        if !canonical.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(canonical, id: \.self) { identifier in
                    HStack(spacing: 6) {
                        ZStack {
                            RoundedRectangle(cornerRadius: 3)
                                .fill(LabelingVisualIdentity.color(for: identifier).opacity(0.35))
                            RoundedRectangle(cornerRadius: 3)
                                .stroke(
                                    LabelingVisualIdentity.color(for: identifier),
                                    lineWidth: 2
                                )
                            if let icon = objectAtlas?.icon(for: identifier) {
                                Image(decorative: icon, scale: 1)
                                    .resizable()
                                    .scaledToFit()
                                    .padding(2)
                            }
                        }
                        .frame(width: 24, height: 18)
                        Text(LabelingVisualIdentity.displayName(for: identifier))
                            .lineLimit(1)
                    }
                }
            }
            .font(.caption2)
            .padding(7)
            .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 6))
            .foregroundStyle(.white)
            .allowsHitTesting(false)
            .accessibilityLabel("Object color legend")
        }
    }
}
