import Foundation
import SwiftUI

struct StencilGuidanceText {
    let language: HollowKnightMenuLanguage

    static let catalog: [String: [String: String]] = {
        let url = Bundle.main.url(forResource: "stencil-guidance", withExtension: "json")
            ?? Bundle.module.url(forResource: "stencil-guidance", withExtension: "json")
        guard let url, let data = try? Data(contentsOf: url),
              let values = try? JSONDecoder().decode([String: [String: String]].self, from: data)
        else { preconditionFailure("Missing stencil-guidance.json") }
        return values
    }()

    func text(_ key: String, _ values: [String: String] = [:]) -> String {
        var result = Self.catalog[language.rawValue]?[key] ?? Self.catalog["en"]?[key] ?? key
        for (name, value) in values {
            result = result.replacingOccurrences(of: "{\(name)}", with: value)
        }
        return result
    }

    func screenName(_ context: LabelingContext) -> String { text(context.rawValue) }

    func instruction(_ step: StencilInitializationStep) -> String {
        if step.context == .gameplayHUD {
            return text(step.occurrence == 1 ? "drawHealthFirst" : "drawHealthSecond")
        }
        if step.classIdentifier == LabelingClassIdentity.selectDecoration {
            return text("draw", ["object": text(step.occurrence == 1 ? "leftDecoration" : "rightDecoration")])
        }
        // Slot labels already contain their period; don't print "1..".
        let name = text(step.className).trimmingCharacters(in: CharacterSet(charactersIn: "."))
        return text(step.requiredCount == 1 ? "draw" : "drawRepeated", [
            "object": name, "count": String(step.occurrence), "total": String(step.requiredCount)
        ])
    }
}

extension HollowKnightMenuLanguage {
    var nativeName: String { StencilGuidanceText(language: self).text("nativeName") }
}

func visionText(_ key: String, _ values: [String: String] = [:]) -> String {
    let language = UserDefaults.standard.string(forKey: "HollowKnightVision.initialization.language.v1")
        .flatMap(HollowKnightMenuLanguage.init(rawValue:)) ?? .english
    return StencilGuidanceText(language: language).text(key, values)
}

private struct VisionLanguageKey: EnvironmentKey {
    static let defaultValue = HollowKnightMenuLanguage.english
}
extension EnvironmentValues {
    var visionLanguage: HollowKnightMenuLanguage {
        get { self[VisionLanguageKey.self] }
        set { self[VisionLanguageKey.self] = newValue }
    }
}
