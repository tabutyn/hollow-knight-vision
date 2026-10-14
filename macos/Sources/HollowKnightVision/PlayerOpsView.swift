import SwiftUI

struct PlayerOpsView: View {
    @Environment(\.visionLanguage) private var visionLanguage
    private func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
        StencilGuidanceText(language: visionLanguage).text(key, values)
    }

    @ObservedObject var model: LiveCaptureModel

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 12) {
                integerRow("Max Health", value: maxHealth, range: 1...9)
                integerRow("Health", value: health, range: 1...max(1, model.playerTestDraft.maxHealth))
                integerRow("Lifeblood Seed", value: lifeblood, range: 0...9)
                integerRow("Extra Mana Slots", value: extraManaSlots, range: 0...3)
                integerRow(
                    "Mana",
                    value: mana,
                    range: 0...(99 + model.playerTestDraft.extraManaSlots * 33)
                )
                integerRow("Geo", value: geo, range: 0...9_999_999)
            }

            Toggle(
                visionText("Invincibility — ignore damage and phase through enemies"),
                isOn: invincibility
            )
            .toggleStyle(.checkbox)
            .disabled(model.playerOpsBusy)

            HStack(spacing: 10) {
                Button(visionText("Randomize Now")) { model.randomizePlayerTestState() }
                Button(visionText("Restore Enemies")) { model.restoreEnemies() }
                Spacer()
            }
            .disabled(model.playerOpsBusy)

            Divider()
            Toggle(
                visionText("Randomize health, lifeblood, mana, and geo after each Label session"),
                isOn: $model.randomizePlayerStateAfterLabel
            )
            .toggleStyle(.checkbox)

            if !model.playerOpsStatus.isEmpty {
                Text(visionText(model.playerOpsStatus))
                    .font(.caption.monospaced())
                    .foregroundStyle(model.playerOpsStatus.contains("failed")
                        || model.playerOpsStatus.contains("not ready") ? .red : .secondary)
            }
            HStack {
                Button(visionText("Reload")) { model.resetPlayerTestState() }
                    .disabled(model.playerOpsBusy)
                if model.playerOpsBusy {
                    ProgressView().controlSize(.small)
                }
                Spacer()
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear { model.refreshPlayerTestState() }
    }

    private func integerRow(
        _ title: String,
        value: Binding<Int>,
        range: ClosedRange<Int>
    ) -> some View {
        GridRow {
            Text(visionText(title))
            Stepper(value: value, in: range) {
                Text("\(value.wrappedValue)")
                    .monospacedDigit()
                    .frame(width: 72, alignment: .trailing)
            }
            .frame(width: 190)
        }
    }

    private var maxHealth: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.maxHealth },
            set: {
                model.playerTestDraft.maxHealth = $0
                model.playerTestDraft.health = min(model.playerTestDraft.health, $0)
                model.applyPlayerTestState()
            }
        )
    }

    private var health: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.health },
            set: {
                model.playerTestDraft.health = $0
                model.applyPlayerTestState()
            }
        )
    }

    private var lifeblood: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.lifebloodSeed },
            set: {
                model.playerTestDraft.lifebloodSeed = $0
                model.applyPlayerTestState()
            }
        )
    }

    private var extraManaSlots: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.extraManaSlots },
            set: {
                model.playerTestDraft.extraManaSlots = $0
                model.playerTestDraft.mana = min(
                    model.playerTestDraft.mana,
                    99 + $0 * 33
                )
                model.applyPlayerTestState()
            }
        )
    }

    private var mana: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.mana },
            set: {
                model.playerTestDraft.mana = $0
                model.applyPlayerTestState()
            }
        )
    }

    private var geo: Binding<Int> {
        Binding(
            get: { model.playerTestDraft.geo },
            set: {
                model.playerTestDraft.geo = $0
                model.applyPlayerTestState()
            }
        )
    }

    private var invincibility: Binding<Bool> {
        Binding(
            get: { model.playerTestDraft.invincible },
            set: {
                model.playerTestDraft.invincible = $0
                model.applyPlayerTestState()
            }
        )
    }
}
