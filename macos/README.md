# Hollow Knight live camera vision

Native macOS prototype with a deliberately small live baseline. Live capture
normalizes the gameplay surface, estimates camera translation, masks the HUD and
Knight, and stores source-backed 256 px atlas tiles. Sparse keyframes and global
feature descriptors persist across launches. A pose-independent search revisits
saved features at up to 2 Hz; two consistent observations add a loop edge and
rebuild affected tiles at optimized camera poses.

Windows preparation now lives in the sibling
[`windows`](../windows) project. It provides a buildable
Win32 capture/normalization bootstrap and Windows CI; the feature-complete app is
still macOS-only. See [WINDOWS_PORT.md](WINDOWS_PORT.md) for the dependency audit
and ordered port gates.

## Build, capture, and dashboard

After changing code, use the canonical build command:

```bash
macos/build-app.sh
```

It builds and signs the application, atomically replaces
`/Applications/Hollow Knight Vision.app`, unregisters the disposable package
copy, and rebuilds one canonical Dock pin in its original position. Duplicate
Vision pins and stale recent tiles are removed without changing other Dock
items or settings. The package copy is deleted after a successful install.
`macos/build-app.sh --package-only` is reserved for CI and artifact packaging.
Canonical builds use the BallRoller Apple signing certificate, preserving the
same macOS Accessibility identity across source changes. The installer rejects
ad-hoc signatures at `/Applications/Hollow Knight Vision.app` so a rebuild
cannot silently invalidate keyboard and pointer permission again. Set
`HKV_CODE_SIGN_IDENTITY=-` only with `--package-only` for a disposable bundle.

To build, install, and immediately launch the installed copy:

```bash
macos/run-live.sh
```

`run-live.sh` builds the current source, installs it at the stable
`/Applications/Hollow Knight Vision.app` path, and launches that installed
copy. Pin only that application to the Dock. Do not pin the disposable
`build/Hollow Knight Vision.app` packaging output; doing so gives macOS two
paths for the same bundle identifier and can produce two Dock icons.

To add the app to the Dock when it is not already pinned, run once:

```bash
macos/install-app.sh --repair-dock
```

The installer stages and verifies the new bundle before replacing the installed
copy, registers only the canonical path with Launch Services, and terminates an
older running Vision process before the next launch. If that process does not
exit, installation stops before replacing the app. Dock refresh is mandatory
and uses built-in macOS preferences, without requiring `dockutil`. For an isolated
installer test, set `HKV_INSTALL_APP_PATH` to an absolute temporary `.app` path.
Run `python3 macos/test-refresh-dock.py` for Dock regression tests; preference
writes in these tests target temporary plist files, never the live Dock.

On first launch, grant **Privacy & Security → Screen & System Audio Recording**.
The app asks on every ordinary launch whether to use Normal or Modded Hollow Knight. Normal mode
uses capture, visual tracking, and native keyboard and pointer forwarding; Modded mode adds receiver input,
Hacker, and Ops after the required capabilities connect. Agents can bypass the chooser:

```bash
macos/run-live.sh --game-mode normal
macos/run-live.sh --game-mode modded --game-path '/path/to/hollow_knight.app'
```

The chooser disables a mode when Vision cannot find a complete compatible app.
Selecting an available mode gracefully closes a different running copy before
launching the matching app. A Steam shortcut is not treated as a stock install.
Use `--game-path` when the compatible app is outside the scanned locations. The
bottom-bar gear opens settings; blue guidance directs menu labeling;
Reset Stencils and Factory Reset create recoverable backups under
`~/Library/Application Support/HollowKnightVision Backups`.

Automatic title/profile navigation is off by default. Enable it only for an
automated run:

```bash
macos/run-live.sh --auto-navigate-gameplay
```

Normal mode forwards arrows and common keyboard controls (A/D/Z/X/C, I,
Return, Tab, Space, Escape) from the Vision window to the selected unmodified
Hollow Knight process. Pointer events over the displayed live frame are mapped
through the captured content rectangle, including title-bar cropping, and sent
to the same validated process. macOS Accessibility permission is checked when
normal input starts, before a key or click can fail silently. Migrating from an
older ad-hoc build requires removing its stale Accessibility row and approving
the stable installed app once. Later canonical rebuilds retain that approval.
The prompt links to the correct settings pane. Unity ignores background keyboard events, so Normal
uses a nonactivating key panel: Vision receives the keys while the game remains
the active application underneath. The panel stays above the game only while
in use and returns to normal window level when you switch away. Held keys are
released on focus loss, workspace changes, and exit. Labeling, stencil review,
model review, and text fields keep their own keys; Settings retains arrow
forwarding. L and S remain Vision’s labeling and stencil shortcuts. The dashboard
opens at the full usable display size by default. Modded mode keeps its receiver
and ordinary window behavior.

For local development runs that need repeatable game input without a human
holding keys, enable the loopback automation channel explicitly and pulse a
button through the same in-game receiver used by normal input:

```bash
macos/run-live.sh --enable-automation-control
macos/control-game.sh up 2
```

The control channel is disabled on ordinary launches.

Gameplay HUD masking uses bundled icon templates independently of the object
detector's Gameplay/menu classification. Open **Debug View → HUD Stencil** to
review per-frame health counts and the exact health, mana, and fixed Geo cutouts.
See [HUD_STENCIL.md](HUD_STENCIL.md) for reference provenance and matching limits.

Use **Capture** during gameplay, **Fit** to frame the growing atlas, and
**Reset atlas** before a bounded test. **World feature overlay** is the single
review checkbox on the bottom bar; it overlays saved landmarks, keyframes, attempted and
accepted matches, loop edges, and raw-to-optimized corrections directly on the
map. Health has a red box, Geo yellow, Soul blue, and the Knight green. All four
regions are omitted from atlas writes. The status row reports camera coordinates,
display rate, and atlas size. Capture requests native display cadence; Metal presentation targets 60 fps with two drawable slots.
See [GROUND_TRACKING.md](GROUND_TRACKING.md) for the measured limits and replay evidence.
Use [Camera diagnosis](CAMERA_DIAGNOSIS.md) to compare solved poses with Hacker's
exact rendered-frame camera transforms and score identical floor observations
under both placements. These tools run offline and never feed truth into tracking.
Ground texture estimates camera motion on a bounded latest-frame worker;
expensive global recovery runs on a separate bounded snapshot worker. Object
inference runs every fourth capture. See [GROUND_TRACKING.md](GROUND_TRACKING.md)
for the current tracker and [TRACKING_STABILITY.md](TRACKING_STABILITY.md) for the
September 26 implementation and live validation.
Apple-silicon patch search uses Metal with CPU fallback. Ground preprocessing
also has a pixel-exact Metal implementation, but current measurements keep its
automatic path on CPU; [GPU_TRACKING.md](GPU_TRACKING.md) records that decision,
the retained benchmarks, and the expanded recovery-search capacity.
The sparse atlas has no fixed 4096 × 1440 reset boundary. The live world is saved
under `~/Library/Application Support/HollowKnightVision/live-world-v3`; Reset atlas
archives it and starts a fresh world. Set `HKV_LIVE_WORLD_ROOT` to an absolute
scratch directory when running destructive atlas-reset tests.

See [LIVE_BASELINE.md](LIVE_BASELINE.md) for the acceptance tests that must pass
before another reconstruction component returns to the live path.
See [CAPTURE_PIXELS.md](CAPTURE_PIXELS.md) for the retained capture path.
[RETIRED_FEATURES.md](RETIRED_FEATURES.md) records the cleanup and supported commands.

### Saved-route replay gate

The live-world evaluator replays a saved source session as independent visits.
With the reopen flag it commits each observation and graph revision to an
isolated world, then reconstructs that store from disk before each visit:

```sh
HollowKnightVision \
  --world-replay-session /path/to/session \
  --world-replay-report /tmp/live-world-report.json \
  --world-replay-repeats 3 --world-replay-reopen
```

Routes are one-way by default, so one pass ending in Town never creates an
implied connection back to Tutorial_01. Use `--world-replay-returning` only for
a route known to end where it started. Returning-route evaluation requires a
confirmed closure and an optimized endpoint residual of at most 12 px.

## Manual ground-edge labels

Open **Hacker** or press `H` to build and edit a second atlas that is independent from Gameplay. Hacker joins each capture to the mod camera transform from the exact Unity frame, places it without the visual pose solver, seeds horizontal ground truth from the current green lines, and keeps edits fixed in world space while the live camera moves. Add/Modify/Delete/Negative, undo, and automatic saves share the compact Label-style workflow. **Debug View → Mark Ground…** opens the same workspace. See [Ground labeling](GROUND_LABELING.md).

## Saved-input replay and checks

Gameplay, Label, Stencil, Model, and Hacker are the workspaces; Ops is part of Settings. The
old video reconstruction command is retired; recorded game input still replays
through the actual game and receiver:

```sh
./run-live.sh --enable-automation-control --allow-background-path-playback --render-frame-marker
./control-game.sh replay path-RECORDING.json 1
swift test -c release
python3 test-camera-diagnostics.py
python3 test-analyze-input-paths.py
./build/Hollow\ Knight\ Vision.app/Contents/MacOS/HollowKnightVision \
  --evaluate-low-resolution-trace /path/to/path-REPLAY.json \
  --low-resolution-report /tmp/low-resolution-report.json
```

Use `HKV_LIVE_WORLD_ROOT` with an absolute scratch directory for replay tests.
Frame truth is diagnostic only. The independent Hacker atlas and manual ground
labels never supply poses or edges to Gameplay tracking.

New recordings retain the matcher input at about 20 Hz. The low-resolution
trace evaluator reuses the production matcher, joins Hacker truth only by exact
rendered Unity frame, measures per-step and integrated ground-loss drift, and
audits room-local place recognition. Recordings made before this trace was
added report `evaluable: false` rather than an apparent zero-error result.

### Stencil walkthrough and model workspace

The gear opens a centered, scrollable Settings dialog with atlas actions, Debug View, Game language, and Factory Reset. Language names use their native spelling. Navigate to the screen named by the blue guidance in the single bottom row, then press **L** or **Label**. Game State is an optional overlay under Debug View. Free Fly, Fit, and Current appear only after Phase 1 while Gameplay is recognized in the Gameplay workspace; Ops also requires Modded mode and a recognized gameplay state. Required menu labels follow menu order and disappear from Add after their required boxes are present (two for selection decorations); Modify/Delete keep them editable. Gameplay objects remain repeatable and alone expose **+ Object**.

**Stencil** derives its checklist from the contracts: 26 required screens in Modded, 25 in Normal (excluding Mods). Phase 1 is Main Title, Select Profile without Clear Save, and a separately captured Gameplay HUD reference (two adjacent full health icons). It unlocks Model and Stencil Detection. Phase 2 contains the other menu/game screens; Phase 3 contains Clear Save, which requires an existing save and is dismissed with No. Credits has no required boxes. Each stencil category keeps one reference; gameplay model objects remain repeatable. Reset Stencils below the sidebar archives stencil references and calibration while preserving gameplay training examples. Factory Reset also removes stencils. The gear opens a centered, scrolling dialog with Save State, New, Delete, Load, all debug controls, language, and Factory Reset. Object Detection stays disabled until an active model has completed training and evaluation on current gameplay labels; bundled models and models from a cleared dataset do not unlock it. Framing controls require both Phase 1 readiness and recognized Gameplay in the Gameplay workspace.

Localization coverage and remaining native-language/Windows UI checks are tracked in [TODO](../TODO.md).

Existing partial references show **Click Stencil and Edit Labels** guidance. Each reference has **Edit Labels** and **Delete Screen Shot** actions, and label boxes show their localized class names without requiring captured icons. **L** selects Gameplay when gameplay is recognized; explicit Edit Labels still opens the selected stencil. The wider object selector sits above the editing controls to avoid overlap at small window sizes.

Hacker is offered only for recognized gameplay with mod telemetry. Ops controls live in Settings and require recognized gameplay plus the player-ops capability. Clicking outside Settings closes it; arrow keys continue forwarding to the modded game. Dragging in Current camera mode switches to Free Fly while ordinary clicks retain game input.
