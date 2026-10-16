# Hollow Knight Vision Windows lab

This is the native Windows host and desktop lab for Hollow Knight Vision. It
builds without Apple frameworks and keeps capture, atlas, inference, dataset
export, and training on CPU.

Implemented now:

- Win32 Hollow Knight process/window discovery.
- CPU target-window capture via `PrintWindow` into owned BGRA pixels, with a
  validated desktop-GDI fallback for drivers that do not render that path.
- Per-monitor DPI awareness so capture geometry remains physical pixels when
  Windows display scaling is enabled.
- Deterministic center crop and bilinear normalization to the existing 640×360
  vision contract.
- A one-frame latest-only slot so capture cannot create an unbounded queue.
- A portable protocol v1/v2 TCP client for complete input state, capability
  negotiation, acknowledgements, and Hacker camera ground truth.
- Persistent semantic-ground approval, including stable-line evidence and
  segment reassociation parity with the macOS atlas.
- Named atlas save states, immutable restore paths, archive import, disk usage,
  and permanent state cleanup with macOS-compatible metadata.
- Atomic schema-v5 labeling examples with language-specific positive boxes, hard negatives,
  known-empty frames, normalized top-origin coordinates, and PNG capture.
- Leak-safe dataset export: capture groups and duplicate images remain in one
  split, hard-negative groups stay in training, and every class retains
  positive training data.
- A WPF Gameplay/Label/Stencil/Model/Hacker lab for drawing and editing boxes,
  exporting datasets, CPU training, live ONNX review, gameplay startup, and
  atlas recording.
- The mac-calibrated camera accumulator, fresh-atlas admission gate, and
  fade/dark-mask-resistant 64×36 visual translation solver.
- The macOS 32×8 CPU ground kernel, occlusion-aware line theory, semantic
  surface validation, and foreground rejection.
- Persisted visual-route evidence with receiver camera truth and repeatable
  offline error gates.
- Offline route-to-room-graph construction using visual motion only; receiver
  camera truth remains evaluation evidence and never supplies world poses.
- Versioned live-world observations, keyframes, landmarks, motion/loop edges,
  atomic compare-and-swap persistence, and optimized-pose replacement.
- Deterministic mac-parity translation pose-graph solving with exact anchors,
  weighted loop closures, per-room connected components, and atomic revisions.
- Mac-compatible recorded input-path loading and replay with optional receiver
  checkpoint restoration, repeated runs, and unconditional neutral release.
- BMP/PNG export, SHA-256 diagnostics, CPU tests, and self-contained artifacts.

Build on Windows with .NET 8 or newer:

```powershell
./build-windows.ps1
```

The resulting CLI host is
`artifacts/win-x64/HollowKnightVision.Windows.Host.exe`. The desktop lab is
`artifacts/app-win-x64/HollowKnightVision.Windows.exe`. Both are self-contained;
the published executables do not require a separate .NET installation.

The desktop lab contains five workspaces:

- **Gameplay** continuously builds and auto-saves the tiled atlas while showing
  the current game frame. Current/Fit/Free Fly framing, pan/zoom, object,
  stencil, motion, transition, and persistent-ground overlays run on CPU.
- **Label** captures an already-running visible game, adds/moves/resizes/
  reclassifies boxes, converts hard negatives, supports undo and 1–4/R
  shortcuts, and provides a thumbnail-backed Recent Frames review.
- **Model** provides data-backed class counts, object references, screenshot
  review, leak-safe export/validation, and CPU-only ONNX training.
- **Ops** reads and applies the receiver's player test state, randomizes it,
  controls invincibility, and restores enemies.
- **Hacker / Atlas** incrementally updates only dirty atlas tiles, supports
  Current/Fit/Free Fly framing, persistent ground and room editing, atlas
  reset, path recording/replay, and offline room-graph build/optimization.

Every ordinary desktop launch asks for **Normal Hollow Knight** or **Modded
Hollow Knight**. Normal keeps local menu recognition and visual Gameplay atlas building
active without a receiver. Recognized menu frames are excluded from atlas and
ground updates. Modded enables Hacker only after fresh ground-truth
telemetry and Ops only after the player-ops capability arrives. For managed
launches, bypass the chooser and select a dedicated installation:

```powershell
./HollowKnightVision.Windows.exe --game-mode normal --game-path 'D:\Games\Hollow Knight\hollow_knight.exe'
./HollowKnightVision.Windows.exe --game-mode modded --game-path 'D:\Modded\Hollow Knight\hollow_knight.exe'
```

The chooser disables a mode when Vision cannot find a complete compatible
installation. Selecting an available mode gracefully closes a different running
copy before launching the matching executable. Normal requires an unmodified
installation; Modded requires both the Modding API loader and Vision receiver.

The single bottom bar shows blue guidance for language-specific 640×360 menu labels. The gear opens settings. Reset Stencils and Factory Reset move data to
`%LOCALAPPDATA%\HollowKnightVision Backups`; neither action edits the game,
mods, or Hollow Knight saves.

Building does not launch the app or game. Buttons clearly distinguish the one
workflow that launches Hollow Knight from workflows that only attach to it.

Run the complete non-launching readiness audit at any time:

```powershell
./test-readiness-windows.ps1
```

It verifies the installed loader/receiver against local build hashes, both
published executables, CPU-only PyTorch, the smoke ONNX model, blank-frame CPU
inference, and that neither the game nor desktop lab is running.

With Hollow Knight already running, locate its window without reading pixels:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- --probe
```

Capture one normalized reference frame:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- `
  --capture-once artifacts/hollow-knight-640x360.bmp
```

Verify the in-game receiver or print one Hacker camera-transform sample:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --receiver-probe
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --ground-truth-once
```

After the matching receiver is installed, automatically launch Hollow Knight,
select **Start Game**, select profile **1**, and require eight stable gameplay
frames before reporting success:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --auto-navigate-gameplay `
  --game-exe 'D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe'
```

This startup path is CPU-only. It uses bundled 640x360 menu calibration and
sends 85 ms Up/Z pulses through `127.0.0.1:36752`; it never injects desktop
keys. The default deadline is 120 seconds; change it with
`--startup-timeout-seconds N`.

The matching Unity-6 loader and receiver can be inspected, installed with an
automatic per-file backup, or restored with:

```powershell
./install-runtime-windows.ps1 -Mode Plan
./install-runtime-windows.ps1 -Mode Install
./install-runtime-windows.ps1 -Mode Restore
```

Installation refuses unsupported vanilla assembly hashes and a running game.
Backups live under `hollow_knight_Data/Managed/.hkv-windows-backups` and the
installer rolls back automatically if any copied file fails hash verification.

Exercise the latest-frame policy at the current atlas cadence:

```powershell
dotnet run --project src/HollowKnightVision.Windows.Host -c Release -- `
  --frames 7 --interval-ms 133 --capture-once artifacts/latest.bmp
```

While gameplay and the receiver are active, record a world-projected atlas:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --record-atlas C:\path\to\atlas-session `
  --atlas-seconds 60 --atlas-ppu 64 --interval-ms 133
```

The recorder uses camera telemetry, omits the top HUD band, blends repeated
observations, and writes 256x256 BGRA BMP tiles plus `atlas.json`. Output must
be new or empty, so existing atlas evidence is never overwritten silently.

## Label examples and dataset export from the CLI

The WPF Label workspace is the normal authoring path. Equivalent commands are
available for automation. Capture does not launch Hollow Knight:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-capture "$env:LOCALAPPDATA\HollowKnightVision\labeling-v1\examples" `
  --label-context game `
  --label-box "enemies.crawlid,0.10,0.20,0.25,0.30" `
  --label-known-class enemies.crawlid
```

List examples, replace an example's boxes, or delete one example:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --label-list C:\path\to\examples
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-update C:\path\to\examples\EXAMPLE_UUID `
  --label-box "enemies.crawlid,0.10,0.20,0.25,0.30,negative"
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-delete C:\path\to\examples\EXAMPLE_UUID
```

Export one or more repeatable `--label-known-class` identifiers:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --label-export C:\path\to\examples `
  --dataset-root C:\path\to\training-v1\datasets `
  --label-model shared-object-model `
  --label-known-class enemies.crawlid
```

The target-window path continues capturing Hollow Knight while the Vision
dashboard covers it. It establishes pixel ownership, normalization,
diagnostics, and CI without a GPU dependency. Windows Graphics Capture remains
the planned high-frame-rate backend; that backend will normally use a GPU copy
path, while the current analysis, inference, and training code is CPU-only.

Diagnose visual camera motion without receiver telemetry or launching the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --visual-track-seconds 20 --interval-ms 133
```

The diagnostic downsamples captures to 64×36 luma, masks moving dark/fade
regions, estimates sub-cell 2D translation, rejects scene jumps, and reports
the accumulated CPU camera pose and rejection reasons.

Inspect semantic ground on one frame from an already-running visible game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe --ground-detect-once
```

The one-shot command runs the macOS-parity 32×8 ground kernel, occlusion
bridging, row suppression, and dark-platform-body validation on CPU. Developer
sweeps can override `--ground-threshold`, `--ground-minimum-segment`,
`--ground-line-separation`, and `--ground-occlusion-gap`.

Record a visual route together with receiver camera truth, then replay the
saved CPU evidence three times without the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --record-visual-route C:\path\to\route --route-seconds 30
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --replay-visual-route C:\path\to\route --route-repeats 3 `
  --route-report C:\path\to\route\replay-report.json
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --build-route-world C:\path\to\route
```

Recording attaches to active gameplay and never launches it. Replay reopens
the persisted evidence between passes and gates mean and worst-step pixel error.
World construction writes `ROUTE_DIR\world\world.json`, assigns stable rooms by
scene, emits only accepted visual-motion edges, and refuses to overwrite prior
world evidence.

Optimize a persisted graph after motion or loop-closure edges are added:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --optimize-world C:\path\to\route\world
```

The solver keeps raw capture poses immutable, holds each connected component's
anchor exact, distributes loop drift deterministically, and commits exactly one
new map revision.

Replay a schema-v1 input path recorded by the macOS app while gameplay is
already running:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --replay-input-path C:\path\to\path.json --path-repeats 3
```

When the file has a start checkpoint, Windows restores it through
`player-checkpoint-v1` before each pass. `--skip-path-checkpoint` preserves the
current game state. Playback drains receiver telemetry and always sends a
neutral state on completion, cancellation, or failure.

## CPU model training on Windows

The shared incremental detector can train on Windows without Core ML and export
an ONNX model. Setup installs CPU-only PyTorch into the repository's ignored
`.tools/hkv-training-windows` environment and caches the pretrained MobileNet
backbone under `.tools/hkv-training-cache`:

```powershell
./setup-training-windows.ps1
```

Validate an exported labeling dataset without training:

```powershell
./train-model-windows.ps1 -Dataset C:\path\to\dataset -ValidateOnly
```

Train and export `Detector.onnx`, `Detector.pt`, predictions, metrics, and the
run manifest:

```powershell
./train-model-windows.ps1 `
  -Dataset C:\path\to\dataset `
  -Output C:\path\to\runs\first-windows-run `
  -Iterations 100
```

Run the exported detector against one live frame with CPU-only ONNX Runtime:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --infer-once C:\path\to\runs\first-windows-run
```

Validate model loading and native runtime packaging without launching the game:

```powershell
./artifacts/win-x64/HollowKnightVision.Windows.Host.exe `
  --model-probe C:\path\to\runs\first-windows-run
```

The host reads `training.json`, converts BGRA to the trainer's RGB NCHW tensor,
and applies the same 3x3 local-maximum filter, 0.05 confidence floor, per-class
nonmaximum suppression, and detection limits as training review.

Pass `-BaseCheckpoint C:\path\to\Detector.pt` for an incremental run. The
wrapper forces `--device cpu` and hides CUDA devices. The WPF Model workspace
drives the same validator and trainer.

The interactive Mac parity surfaces are wired on Windows, including continuous
atlas persistence, state management, ground/room editing, input-path recording,
model review, and player operations. Remaining production work is the recorded
real-game route acceptance gate and an optional high-frame-rate Windows Graphics
Capture backend. The current atlas uses authoritative receiver camera telemetry
and target-window pixels. All current Windows analysis, training, tests, and
published readiness probes are CPU-only.

See [WINDOWS_PORT.md](../macos/WINDOWS_PORT.md) for the dependency
audit and ordered port plan.

### Stencil walkthrough and model workspace

The gear opens a centered, scrollable Settings dialog with atlas actions, Debug View, Game language, and Factory Reset. Language names use their native spelling. Navigate to the screen named by the blue guidance in the single bottom row, then press **L** or **Label**. Game State is an optional overlay under Debug View. Free Fly, Fit, and Current appear only after Phase 1 while Gameplay is recognized in the Gameplay workspace; Ops also requires Modded mode and a recognized gameplay state. Required menu labels follow menu order and disappear from Add after their required boxes are present (two for selection decorations); Modify/Delete keep them editable. Gameplay objects remain repeatable and alone expose **+ Object**.

**Stencil** derives its checklist from the contracts: 26 required screens in Modded, 25 in Normal (excluding Mods). Phase 1 is Main Title, Select Profile without Clear Save, and a separately captured Gameplay HUD reference (two adjacent full health icons). It unlocks Model and Stencil Detection. Phase 2 contains the other menu/game screens; Phase 3 contains Clear Save, which requires an existing save and is dismissed with No. Credits has no required boxes. Each stencil category keeps one reference; gameplay model objects remain repeatable. Reset Stencils below the sidebar archives stencil references and calibration while preserving gameplay training examples. Factory Reset also removes stencils. The gear opens a centered, scrolling dialog with Save State, New, Delete, Load, all debug controls, language, and Factory Reset. Object Detection stays disabled until an active model has completed training and evaluation on current gameplay labels; bundled models and models from a cleared dataset do not unlock it. Framing controls require both Phase 1 readiness and recognized Gameplay in the Gameplay workspace.

Localization coverage and remaining native-language/Windows UI checks are tracked in [TODO](../TODO.md).

Existing partial references show **Click Stencil and Edit Labels** guidance. Each reference has **Edit Labels** and **Delete Screen Shot** actions, and label boxes show their localized class names without requiring captured icons. **L** selects Gameplay when gameplay is recognized; explicit Edit Labels still opens the selected stencil. The wider object selector sits above the editing controls to avoid overlap at small window sizes.

Hacker is offered only for recognized gameplay with mod telemetry. Ops controls live in Settings and require recognized gameplay plus the player-ops capability. Clicking outside Settings closes it; arrow keys continue forwarding to the modded game. Dragging in Current camera mode switches to Free Fly while ordinary clicks retain game input.
