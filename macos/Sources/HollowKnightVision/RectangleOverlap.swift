import CoreGraphics

/// Detection, tracking, and human-label review use the same overlap measure.
enum RectangleOverlap {
    static func intersectionOverUnion(_ first: CGRect, _ second: CGRect) -> CGFloat {
        let intersection = first.intersection(second)
        guard !intersection.isNull, !intersection.isEmpty else { return 0 }
        let area = intersection.width * intersection.height
        let union = first.width * first.height + second.width * second.height - area
        return union > 0 ? area / union : 0
    }
}
