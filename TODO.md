# Localization verification

- [x] Update the shared Mac/Windows catalog for all 11 supported languages: English, Español, Français, Italiano, 日本語, 한국어, Português (Brasil), Русский, 简体中文, 繁體中文, Deutsch.
- [x] Route setup, navigation, stencil guidance, object choices, editing buttons, settings, Ops controls, reset instructions, and recent-frame instructions through the selected language. Keep internal context identifiers independent of displayed translations.
- [x] Add automated checks for matching catalog keys/placeholders and translated campaign/settings captions. Cover partial-stencil guidance, saved Back labels, and first-model readiness with regression tests.
- [x] Update languages before merging the runtime-mode/stencil campaign release into main.
- [ ] Have fluent speakers verify menu/object names against each localized game, including left/right decorations, profile slots, and health-icon instructions.
- [ ] Complete native Windows visual testing: long captions, three campaign phases, Normal/Modded gating, mouse camera transitions, settings arrow forwarding, and reset flows. Cross-compilation and portable tests do not replace this check.

Technical diagnostics from the game, operating system, or training subprocess may retain their original language. User-defined object names and saved-state names are preserved verbatim.
