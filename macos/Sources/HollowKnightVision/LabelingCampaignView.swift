import Charts
import SwiftUI

struct LabelingCampaignView: View {
    @Environment(\.visionLanguage) private var language
    let progress: [LabelingCampaignProgress]
    let history: [LabelingCampaignPoint]

    private func text(_ key: String) -> String { StencilGuidanceText(language: language).text(key) }
    private func name(_ identifier: String) -> String { text(LabelingVisualIdentity.displayName(for: identifier)) }

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            VStack(alignment: .leading, spacing: 6) {
                Chart(progress) { item in
                    BarMark(x: .value(text("campaignExamples"), item.count), y: .value("", name(item.id)), stacking: .unstacked)
                        .foregroundStyle(.blue)
                        .accessibilityLabel(name(item.id))
                        .accessibilityValue("\(item.count)")
                        .annotation(position: .trailing, alignment: .leading) {
                            Text("\(item.count)").font(.caption2.monospacedDigit())
                        }
                }
                .chartXScale(domain: 0...(max(progress.map(\.count).max() ?? 10, 10) + 5))
            }
            VStack(alignment: .leading, spacing: 6) {
                Text(text("campaignQuality")).font(.headline)
                if history.isEmpty {
                    Text(text("campaignNoValidation")).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
                } else {
                    Chart(history) { point in
                        LineMark(x: .value(text("campaignRun"), point.run), y: .value("mAP@50", point.accuracy))
                            .foregroundStyle(.green)
                        PointMark(x: .value(text("campaignRun"), point.run), y: .value("mAP@50", point.accuracy))
                            .foregroundStyle(.green)
                            .annotation(position: .top) {
                                Text(point.accuracy, format: .percent.precision(.fractionLength(0))).font(.caption2)
                            }
                    }
                    .chartYScale(domain: 0...1)
                    .chartXScale(domain: 0...max(2, history.map(\.run).max() ?? 2))
                    .chartYAxis { AxisMarks(values: [0.0, 0.5, 1.0]) { value in
                        AxisGridLine()
                        AxisValueLabel { if let number = value.as(Double.self) { Text(number, format: .percent) } }
                    } }
                }
            }
        }
        .frame(height: max(210, CGFloat(progress.count * 25 + 55)))
        .padding(14)
        .accessibilityIdentifier("labeling-campaign-progress")
    }
}
