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

```bash
macos/run-live.sh
```

`run-live.sh` builds the current source, installs it at the stable
`/Applications/Hollow Knight Vision.app` path, and launches that installed
copy. Pin only that application to the Dock. Do not pin the disposable
`build/Hollow Knight Vision.app` packaging output; doing so gives macOS two
paths for the same bundle identifier and can produce two Dock icons.

To repair an older Dock item while preserving its position, run once:

```bash
macos/install-app.sh --repair-dock
```

The installer stages and verifies the new bundle before replacing the installed
copy, registers only the canonical path with Launch Services, and terminates an
older running Vision process before the next launch. For an isolated installer
test, set `HKV_INSTALL_APP_PATH` to an absolute temporary `.app` path.

On first launch, grant **Privacy & Security → Screen & System Audio Recording**.
The app can launch and position Hollow Knight, then keeps Vision as the active
application while relaying Arrow, A, X, and Z input to the game.

Automatic title/profile navigation is off by default. Enable it only for an
automated run:

```bash
macos/run-live.sh --auto-navigate-gameplay
```

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

Gameplay, Label, Model, Ops, and Hacker remain the supported workspaces. The
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
