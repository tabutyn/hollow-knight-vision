import Charts
import SwiftUI

struct LabelingCampaignView: View {
    @Environment(\.visionLanguage) private var language
    let progress: [LabelingCampaignProgress]
    let history: [LabelingCampaignPoint]
    let historyUnavailable: Bool

    private func text(_ key: String) -> String { StencilGuidanceText(language: language).text(key) }
    private func name(_ identifier: String) -> String { text(LabelingVisualIdentity.displayName(for: identifier)) }

    var body: some View {
        HStack(alignment: .top, spacing: 24) {
            VStack(alignment: .leading, spacing: 6) {
                Text(text("campaignExamples")).font(.headline)
                Chart(progress) { item in
                    BarMark(x: .value(text("campaignExamples"), item.target), y: .value("", name(item.id)), stacking: .unstacked)
                        .foregroundStyle(.gray.opacity(0.25))
                        .accessibilityLabel(name(item.id))
                        .accessibilityValue("\(item.count) / \(item.target)")
                    BarMark(x: .value(text("campaignExamples"), item.count), y: .value("", name(item.id)), stacking: .unstacked)
                        .foregroundStyle(.blue)
                        .annotation(position: .trailing, alignment: .leading) {
                            Text("\(item.count)/\(item.target)").font(.caption2.monospacedDigit())
                        }
                }
                .chartXScale(domain: 0...(max(progress.map { max($0.target, $0.count) }.max() ?? 10, 10) + 5))
                Text(text("campaignCaptureHelp")).font(.caption).foregroundStyle(.secondary)
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
                    .chartXAxisLabel(text("campaignRun"))
                    .chartYAxis { AxisMarks(values: [0.0, 0.5, 1.0]) { value in
                        AxisGridLine()
                        AxisValueLabel { if let number = value.as(Double.self) { Text(number, format: .percent) } }
                    } }
                }
                Text(text(historyUnavailable ? "campaignMissingHistory" : "campaignValidationHelp"))
                    .font(.caption).foregroundStyle(historyUnavailable ? .orange : .secondary)
            }
        }
        .frame(height: max(210, CGFloat(progress.count * 25 + 55)))
        .padding(14)
        .accessibilityIdentifier("labeling-campaign-progress")
    }
}
