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

Windows 10/11 x64 with .NET 8 SDK and PowerShell:

```powershell
cd windows
.\build-windows.ps1
```

See [macOS instructions](macos/README.md) and [Windows instructions](windows/README.md) for capture permissions, the optional telemetry receiver, training setup, and readiness checks.

## Local initialization

Menu text stencils are initialized from labels made on the player's own game installation. In **Label**, capture a menu or gameplay screen, choose its contract object, and draw a tight rectangle. Those examples create the local stencils and stay in the operating system's application-data directory.

Gameplay objects use the learned detector. In **Label**, press **+ Object**, choose Gameplay, Enemy, or World, enter a name, then draw boxes for it. The app stores the new identifier in `object-catalog-v1.json`; dataset export and subsequent training include it without a source-code change. The catalog JSON is deliberately the same schema on both platforms, so it can be copied with the user's private labels when moving a training workspace.

The repository model is inference-only. Incremental training runs keep their PyTorch checkpoints in local application data; they are not committed. A fresh machine can train from its local labels, while an existing Mac or Windows training workspace can continue from its last checkpoint.

## Data locations

- macOS: `~/Library/Application Support/HollowKnightVision`
- Windows: `%LOCALAPPDATA%\HollowKnightVision`

Do not commit those directories. They contain screenshots and other evidence captured from the user's game.

## License

Source code is available under [MIT](LICENSE). The model is covered by the same project license subject to the third-party components described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and its [model card](models/shared-object-model/MODEL_CARD.md).
