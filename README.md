# Hollow Knight Vision

Hollow Knight Vision is an MIT-licensed desktop vision and mapping tool for macOS and Windows. It captures a local Hollow Knight window, recognizes gameplay objects, builds room atlases, edits ground truth, records repeatable routes, and trains updated object detectors from labels made inside the app.

The repository contains source code, contracts, calibration metadata, and a seven-class ONNX detector. It contains no gameplay screenshots, recordings, labels, or training dataset. Hollow Knight and its visual assets belong to Team Cherry and are not distributed here.

## Layout

- `macos/` — SwiftUI app, Core ML training workflow, telemetry mod, and tests.
- `windows/` — WPF app, Win32 capture, ONNX Runtime inference, CPU training, and tests.
- `models/shared-object-model/` — distributable ONNX detector and model card.

## Build

macOS 14 or newer with Xcode command-line tools:

```sh
cd macos
swift test
./build-app.sh
```

`build-app.sh` also replaces `/Applications/Hollow Knight Vision.app`, refreshes
its Launch Services registration, and updates an existing Dock pin. Use
`./build-app.sh --package-only` only when producing an artifact that must not be
installed, such as in CI.

Windows 10/11 x64 with .NET 8 SDK and PowerShell:

```powershell
cd windows
.\build-windows.ps1
```

See [macOS instructions](macos/README.md) and [Windows instructions](windows/README.md) for capture permissions, the optional telemetry receiver, training setup, and readiness checks.

For repeatable modded-game experiments, see the [recording and replay audit](MODDED_REPLAY_AUDIT.md).
It documents current Windows replay integration gaps, Mac Auto Save reset behavior,
and how to compare the same recorded route across builds without pooling unrelated runs.

## Local initialization

On every ordinary launch, choose **Normal Hollow Knight** or **Modded Hollow Knight**. Normal mode keeps menu recognition and the visual Gameplay atlas available without a game mod. Modded mode also connects to the local receiver; Hacker and Ops remain disabled until their advertised receiver capabilities arrive. Automated launches can use `--game-mode normal` or `--game-mode modded` to bypass the chooser. Pass `--game-path PATH` to select a separate stock or modded installation explicitly; explicit paths are checked against the selected mode.

Vision scans for complete compatible installations before showing the chooser;
an unavailable mode is disabled. Selecting an available mode gracefully closes
a different running Hollow Knight and launches the matching installation.
Application shortcuts that only redirect to Steam do not count as installations.

Setup is a three-phase stencil campaign with persistent blue guidance in the bottom bar. Phase 1 captures Main Title, the available Select Profile labels, and two adjacent full health icons in Gameplay HUD. Clear Save is deferred until a save exists; it never blocks first-time setup. Completing Phase 1 unlocks Model and the stencil debug viewer. Free Fly, Fit, and Current appear only while the Gameplay workspace recognizes Gameplay. Phase 2 covers the remaining menu/game screens; Phase 3 covers save-management dialogs. Reset Stencils lives in the Stencil sidebar and preserves gameplay training data. Factory Reset, in the centered scrolling Settings dialog, archives Vision data including stencils without changing Hollow Knight saves, game files, or mods.

Gameplay objects use the learned detector. In **Label**, press **+ Object**, choose Gameplay, Enemy, or World, enter a name, then draw boxes for it. The app stores the new identifier in `object-catalog-v1.json`; dataset export and subsequent training include it without a source-code change. The catalog JSON is deliberately the same schema on both platforms, so it can be copied with the user's private labels when moving a training workspace.

The repository model is inference-only. Incremental training runs keep their PyTorch checkpoints in local application data; they are not committed. A fresh machine can train from its local labels, while an existing Mac or Windows training workspace can continue from its last checkpoint.

## Data locations

- macOS: `~/Library/Application Support/HollowKnightVision`
- Windows: `%LOCALAPPDATA%\HollowKnightVision`

Do not commit those directories. They contain screenshots and other evidence captured from the user's game.

## License

Source code is available under [MIT](LICENSE). The model is covered by the same project license subject to the third-party components described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and its [model card](models/shared-object-model/MODEL_CARD.md).
