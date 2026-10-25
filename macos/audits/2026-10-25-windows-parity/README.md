# Mac coverage and Windows parity audit — 2026-10-25

## Source and scope

Merged the nine remote commits `a0c8b69` through `f1946e5` into local main at
`799d853`. Local commit `ce85f7b` preserves the earlier Mac startup, Steam privacy
ownership, and cursor/input fixes. No merge conflicts. Nothing was pushed.

This audit compares those incoming Windows changes with the merged Mac source.
The missing features below are identified for implementation; this audit does
not claim they have been ported. Existing Mac changes included in the incoming
commits are already merged. The canonical Mac build installs the merged app in
`/Applications/Hollow Knight Vision.app` and refreshes its existing Dock pin.

## Measured coverage

Command: `swift test --package-path macos --enable-code-coverage --skip MenuStencilTests`.

- XCTest: 822 cases, 812 passed, 10 skipped, zero failures.
- Swift Testing: one additional test passed. Combined: 813 passed, 10 skipped.
- Python: 48 tests passed across coverage reporting, camera diagnostics, input
  analysis, and Dock refresh. The temporary-preference Dock test required a
  sandbox retry; it does not modify the real Dock.
- All nine Metal correctness tests passed with explicit Metal backend assertions;
  the two opt-in GPU benchmarks were skipped.
- The CI profile excludes the entire 60-test `MenuStencilTests` suite. Some tests
  need the private screenshot/calibration corpus; the class also contains
  synthetic tests. Split these so portable tests can run without that corpus.

Production totals include only the 104 instrumented files under
`macos/Sources/HollowKnightVision/`, excluding test sources and generated files.

| Metric | Covered / instrumented | Coverage |
| --- | ---: | ---: |
| Lines | 29,490 / 60,048 | 49.11% |
| Functions | 3,592 / 7,349 | 48.88% |
| Regions | 8,963 / 17,849 | 50.22% |
| Branches | No counters exported | Not available |

| Source | Covered lines / instrumented | Line coverage |
| --- | ---: | ---: |
| GroundMetalProcessing.swift | 532 / 542 | 98.15% |
| GroundCameraSolver.swift | 1,118 / 1,214 | 92.09% |
| GroundHypothesisTracker.swift | 3,691 / 4,139 | 89.18% |
| HUDStencil.swift | 437 / 444 | 98.42% |
| HackerAtlas.swift | 846 / 924 | 91.56% |
| HollowKnightLaunchPolicy.swift | 27 / 27 | 100.00% |
| LiveCaptureRestartPolicy.swift | 48 / 50 | 96.00% |
| LiveAtlasPipeline.swift | 918 / 1,367 | 67.15% |
| LiveTrackingTiming.swift | 152 / 230 | 66.09% |
| LayerSceneViewport.swift | 486 / 1,252 | 38.82% |
| NormalGameKeyForwarder.swift | 111 / 675 | 16.44% |
| LiveDashboardView.swift | 329 / 3,182 | 10.34% |
| GameControlForwarder.swift | 96 / 2,013 | 4.77% |
| LiveCaptureModel.swift | 0 / 5,668 | 0.00% |

These are Swift host-code measurements, not Metal shader instruction coverage.
The tests exercise actual Metal dispatch and compare results with CPU separately.
The trainer executable is outside this instrumented app module. High coverage
of launch policy or HUD matching does not cover their live controller wiring.
No live gameplay, native window/input, or installed Windows UI test was run as
part of this audit. Windows WPF requires Windows; this Mac also has only .NET 10,
while the incoming `global.json` selects .NET 8. Windows coverage is not inferred
from test counts or the source parity inventory.

Full local evidence is saved under ignored
`artifacts/audits/macos-parity-2026-10-25/`: LLVM JSON and profile, annotated HTML,
test log, GPU summary, and build/install log. The adjacent
[coverage-summary.json](coverage-summary.json) retains per-file measured counts
and the source revision without depending on those ignored artifacts.

## Actionable Mac ports

1. **P1 — Preserve held game keys while using the mouse.** Windows now maintains
   game keyboard focus under a clickable overlay and releases game-owned cursor
   confinement ([WindowsNormalGameInput.cs](../../../windows/src/HollowKnightVision.Windows.App/WindowsNormalGameInput.cs#L117)).
   Mac's [claimPointerForVision](../../Sources/HollowKnightVision/NormalGameKeyForwarder.swift#L325)
   calls `releaseAll()`, activates Vision, and requires a fresh press for keys
   that were held. The earlier Mac fix frees the pointer, but still interrupts
   movement/chords when the pointer moves. Implement the same user-visible
   behavior with supported macOS input/window APIs; Windows `ClipCursor` cannot
   be copied. Validate holding Right + Jump while moving the pointer and using
   camera controls, then switching to Label/Model and another application.

2. **Resolved — Require user-captured HUD stencils on both platforms.**
   The proposed Windows fallback port was rejected: reference images must not
   ship in Vision. Mac's captured-stencil requirement is the intended behavior.
   Both platforms now require two valid health labels in the selected language;
   no built-in full/empty/lifeblood patches supplement them. Factory Reset or
   deletion of the HUD stencil clears recognition until setup is completed.
   Hacker atlas masking and the Windows automation host also use captured
   references. Photographic matcher fixtures live only in test targets.

3. **P2 — Support remapped Normal-mode game controls.** Windows allows native
   game keys beyond the stock bindings while reserving Vision shortcuts
   ([NormalGameInputPolicy.cs](../../../windows/src/HollowKnightVision.Windows.App/NormalGameInputPolicy.cs#L21)).
   Mac's [fixed supported-key list](../../Sources/HollowKnightVision/NormalGameKeyForwarder.swift#L73)
   omits keys such as Q/W/E. When Vision owns keyboard focus, remapped controls
   can be lost. Define reserved Vision shortcuts and forward other supported
   game bindings, preserving editor/text-field and system-shortcut handling.
   Test remapped movement/jump, held chords, repeat/release, and workspace exits.

4. **P2 — Align Hacker atlas overlap policy with Windows.** Windows changed its
   [ground-truth mosaic](../../../windows/src/HollowKnightVision.Windows.Core/GroundTruthAtlasMosaic.cs#L143)
   to newest opaque pixels, including black. Mac Gameplay already received that
   default, but [Hacker RoomState](../../Sources/HollowKnightVision/HackerAtlas.swift#L620)
   still selects `.newestQuilted`, whose seam can retain old opaque pixels at
   overlap edges. Use `.newestOpaque` for equivalent Hacker behavior, retaining
   quilting only as an explicit diagnostic option if desired. Add Hacker-level
   regressions for changed edge pixels, black, transparent HUD holes, room
   revisits, and restored scenes; lower-level atlas tests alone do not verify
   the policy chosen by the live Hacker pipeline.

5. **P2 — Expose persistent Metal diagnostics and fallback reasons.** Windows
   writes [tracking-gpu.json](../../../windows/src/HollowKnightVision.Windows.App/MainWindow.xaml.cs#L3777)
   with device, actual camera backend, completed dispatch count, candidate count,
   search duration, and fallback reason. Mac has per-search backend/timing logs
   only when [ground tracing is enabled](../../Sources/HollowKnightVision/LiveCaptureModel.swift#L3469);
   [GroundPatchMatcher](../../Sources/HollowKnightVision/GroundMetalProcessing.swift#L132)
   reports availability/results but no persistent dispatch counter or failure
   reason. Add a throttled diagnostic snapshot for Metal and CPU fallback.
   Verify live camera use separately from renderer GPU use, and report actual
   dispatch/fallback results rather than assuming device availability means use.

## Already present; do not duplicate

- **Newest Gameplay pixels:** incoming commit `366c6bf` changes Mac
  `LiveAtlasPipeline` to `.newestOpaque` and adds a direct regression.
- **Tracking that continues when analysis trails capture:** Mac already publishes
  ordered tracking state independently (`LiveCaptureModel.swift:3926`), reads
  that state for fresh sample-queue presentation (`:2844`, `:3103`), and submits
  slow atlas observations separately. `LiveAtlasPipelineTests` cover delayed
  observations and a slow atlas worker. Windows' new `GameplayPresentationState`
  fixes its previous equality-to-newest-capture gate; that gate is absent on Mac.
- **HUD animation/dropout latch:** Mac already uses established gameplay state
  plus current fade/transition evidence for tracking (`LiveCaptureModel.swift:3111`).
- **Ground-feature colors/default visibility:** Windows ports the existing Mac
  review palette; the incoming Mac view change enables the overlay by default.
- **Gameplay versus Hacker camera controls:** Mac already renders mutually
  exclusive button sets (`LiveDashboardView.swift:1291`).
- **GPU matching and 192-pixel recovery:** Mac already constructs the production
  solver with automatic Metal selection and that radius
  (`GroundHypothesisTracker.swift:603`). CUDA itself is Windows-specific.
  Incoming `f5b348b` adds the relevant Metal bounds, row-mask, and encoder guards.
- **Build/install refresh:** Mac already has the canonical build/install/Dock
  workflow; Windows added its corresponding install/Start Menu workflow.
- **Coverage publication:** the incoming Mac CI now copies the exact coverage
  JSON to a visible artifact directory and fails if it is missing.

## Differences to measure or preserve

- Windows uses CPU for automatic patch searches below 64 candidates; Mac uses
  Metal whenever available. Benchmark small Apple GPU searches before adopting
  a threshold. The CUDA cutoff is not evidence for the correct Metal cutoff.
  Mac already keeps the smaller image preprocessing kernel on CPU automatically.
- Keep the requested Mac behavior that arrows still reach the game while
  Settings is open. Windows currently exits game input in Settings; copying
  that rule would conflict with the earlier requirement.
- Prioritize tests through live capture, launch, focus, and UI coordination.
  Their low coverage explains why passing pure policy/geometry tests cannot
  establish that native launch, cursor behavior, and workflow wiring work.

Suggested implementation order: held-key/mouse behavior and its native test,
HUD fallback with campaign isolation tests, remapped bindings, Hacker pixel
policy, then persistent GPU diagnostics. Native verification should use the
installed Mac app and keep user captures/stencils intact.
