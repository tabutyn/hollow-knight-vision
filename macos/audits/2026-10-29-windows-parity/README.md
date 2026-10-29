# Windows parity and coverage — 2026-10-29

Merged `origin/main` through **`0306a61`** into local `main`, preserving the
Mac Modded/Steam launch fix in `a55a221`. Initial merge: `238271a`; a further
Windows fix arrived during verification and was merged in `221d5c2`. Mac ports:
`1dca42d`, `ff05f81`, and resource wording correction `730228a`. Nothing was
pushed. Scope is the **15 Windows commits after `ad08a4b`**, including six fetched
today, plus the local Mac fixes.

The user chose **production improvements first**. The experimental **Step 1s**
UI, controlled-frame runner, and its alternate dense camera solver remain
Windows-only. The shared receiver understands that protocol, but Mac does not
expose or enable the experimental tracking mode.

## Production parity

| Windows change | Mac result |
| --- | --- |
| Persistent ground camera, streamed GPU capture, tracking/render separation (`3ceda38`, `5137a8e`, `8b7da27`) | Already present: persistent `GroundHypothesisTracker`, ScreenCaptureKit stream, Metal patch matching, bounded frame handling and separate presentation/integration. CUDA/D3D code is hardware-specific and is not copied to Metal. |
| Bounded descriptor ranking and lower allocation overhead (`7ef78b8`) | Ported stable top-six/alternate/canonical matching without sorting the entire global bank. Added widened integer distance calculation, cached searchable rows, linear winner/rival selection, and worker admission before preparing snapshots. Acceptance thresholds and live solver behavior are preserved. |
| Atlas controls/recovery and save stalls (`f6e24e0`, `51a3d8c`) | Existing Mac Current/Fit controls, persistent recovery, newest-pixel gameplay atlas and asynchronous atlas management already cover these production behaviors. Saving holds the integration transaction, not the tracking queue; destructive world replacement advances the capture epoch. |
| Fast-fall GPU diagnosis (`b242297`) | Reviewed diagnostic evidence; no proven new production algorithm to port from this documentation commit. Metal correctness tests still exercise the Mac backend. |
| Modded Steam launch (`7d601d8`) | Mac launch identity fix preserved. It starts Steam when required, launches the selected app with the correct Steam identity, and validates the returned bundle path. |
| Live receiver replay (`661b5e3`) | Mac already has acknowledged checkpoint restore, isolated replay, pose sampling and completion handling. Incoming optional scene-mask checkpoint fields and round-trip tests were merged. Receiver 0.9.5 built and installed into the dedicated Modded copy only. |
| Replay audit/strict frame steps (`79f47a1`, `82e4d6a`) | Shared checkpoint/receiver improvements merged; experimental Step 1s intentionally deferred as requested. It does not change Mac live camera thresholds or activate broad search every frame. |
| Guided label entry (`a1834d3`) | Explicit/campaign navigation now wins over an incidental Gameplay HUD recognition, including later phases. The next missing class is selected on entry. Normal recognized-context entry resumes after the campaign. |
| XInput naming (`16ebbb4`) | Windows-specific. Mac retains its MFI controller terminology and shared saved-label identifiers. |
| Stencil workflow (`535fd87`) | Clear Save is required in phase three but does not block a fresh save's first phase. One positive box removes it from Add choices. Selectors normalize a Clear Save captured beside any profile row, omit a duplicate generic candidate, ignore stale modal calibration, and use verified text bounds. |
| Shared-model training/guidance (`535fd87`) | Training eligibility uses the whole gameplay model, independently of the selected object. Guidance refreshes after exemptions/deletions. Added all eight new messages to all 11 shared language catalogs, so Windows also picks up those translations. |
| Menu particles (`535fd87`) | Existing Mac menu particles already provide the behavior restored on Windows. |

The late fix (`0306a61`) also reached Mac: repeated Clear Save text is verified
from pixels at each occupied row, fractional crops retain their size, and stale
rows disappear on the next tracked frame. Gameplay choices, restored selections
and contracts exclude retired HUD fields while the separate HUD stencil still
requires two health boxes. Legacy dataset membership stays intact. Gameplay
label instructions remain visible and translated; a local L handler exits Label
when a picker has focus, while text entry and Settings retain their controls.
The Windows UI regression project also cross-builds with zero warnings/errors;
its native WPF key-routing tests still need a Windows session.

A fresh-save screenshot cannot contain Clear Save. Later guidance explicitly
explains deleting that old screenshot in Stencil and capturing an occupied
profile when needed; it does not pretend the missing pixels can be labeled.
No recognition reference images were added to the app.

## Verification and coverage

- Full Mac portable CI profile: **837 XCTest cases, 827 passed, 10 skipped,
  zero failures**, plus **one Swift Testing pass**: **828 passed total**.
- Intermediate affected-test runs passed **74**, then **129** cases after the
  additional Windows commit. Final coverage was recollected with the full suite
  on one final binary; counters from different Swift versions are not mixed.
- **60 Python tests passed**: input analysis 19, camera diagnostics 26, GPU
  coverage reporting 2, Dock refresh 11, replay frame evaluation 2.
- Shared receiver protocol/state tests passed. Receiver release build: zero
  warnings/errors; installed DLL SHA-256 matches the build.
- New synthetic menu tests require no private screenshot corpus. New descriptor
  tests compare exact candidate order against the former full-sort algorithm,
  including ties, thresholds, canonical segments and signed integer extremes.
- The full CI profile still excludes `MenuStencilTests` (60 cases, including
  private-corpus cases). The two new portable menu tests run independently.
- The Windows test project cross-built using .NET 10 with its intended C# 12
  language version. This is not a native Windows installation or UI test.

| Mac LLVM production metric | Covered / instrumented | Coverage |
| --- | ---: | ---: |
| Lines | 34,206 / 60,465 | **56.57%** |
| Functions | 4,001 / 7,420 | 53.92% |
| Regions | 9,826 / 17,999 | 54.59% |

The new descriptor matcher has **100% LLVM line coverage**; stencil campaign
logic has **90.63%**, and menu recognition has **65.25%** after the new synthetic
end-to-end pixel tests. Whole-app coverage remains limited by native GUI, input,
capture and orchestration. Metal host-code coverage is not shader instruction
coverage. Trainer coverage is outside this app harness.

Changed-line coverage below uses unique executable line entries from LCOV,
intersected with added/modified lines since `ad08a4b`. LLVM summaries and unique
LCOV line counts have different denominators. **Uninstrumented files are listed
separately, not assumed covered.** Comments, resources, removals and XAML are not
executable line counters.

| Source group | Covered changed executable lines | Coverage | Changed files without counters |
| --- | ---: | ---: | ---: |
| Mac app | 232 / 367 | 63.22% | 0 |
| Windows core | 1,097 / 1,457 | 75.29% | 1 assembly-visibility metadata file |
| Linked Windows app policies | 101 / 269 | 37.55% | 14 |
| Windows host | 0 / 19 | 0% | 0 |
| Shared receiver | 64 / 84 | 76.19% | 5 Unity runtime files |

Windows core overall line coverage is **87.46%**. The portable Windows audit
attempted 1,019 registered checks: **1,000 passed, 14 explicitly skipped, five
failed on macOS filesystem semantics**. The ordinary runner stops at its first
failure; an ignored audit runner retained all assertions and continued after
exceptions. Correcting its assembly locations and using a physical temp root
resolved 29 initial harness/environment failures. After the late merge, a
shared English wording assertion was corrected and its focused rerun passed.
The five remaining failures
expect Windows sharing locks to prevent deletion/replacement of an open file:

1. Live-world manifest rollback/orphan retry.
2. Live-world rollback failure preserving the recovery archive.
3. Scene-session manifest rollback.
4. Calibration write failure preserving old data.
5. Ground-truth document atomic replacement failure.

Run these on Windows before treating Windows validation as green. The 14 skips
include missing CUDA hardware, private recordings, junction-specific checks and
opt-in saved-atlas evidence. WPF event wiring, native capture and the Unity
runtime hooks are not fully covered. Experimental Windows sources are included
in the coverage inventory but not claimed as Mac ports.

## Performance evidence

Paired optimized Swift benchmark, 20,020 stored descriptors × 192 values,
20 iterations, identical selected candidates/checksum:

- Former full-sort median: **2.736 ms**.
- Bounded selection median: **1.554 ms**.
- **1.76× faster**, about **43% less time**, for this descriptor-search benchmark.

This is not an end-to-end gameplay FPS claim. The source benchmark is included
for reproduction (`swiftc -O` with `GroundDescriptorMatcher.swift` and
`descriptor-benchmark.swift`); instrumented/debug timing is not a production speed estimate.

## Installation and reproduction

`./macos/build-app.sh` built, signed and installed
`/Applications/Hollow Knight Vision.app`. The installer verified one canonical
Dock pin and removed its disposable build app. Installed Swift fingerprint and
translated resource bytes match source `730228a`; strict signature verification
passes with access to the Mac's signing trust settings.

Installed-app smoke check: Vision displayed the live Modded title screen in a
1470 × 840 point window. One canonical Vision process and one dedicated Modded
game process were present; the loopback receiver connection was established,
and the mod log confirmed receiver 0.9.5. This checks launch/capture/receiver
wiring, not a live gameplay performance benchmark or a full replay.

Receiver 0.9.5 was installed at
`~/Applications/Hollow Knight.app/Contents/Resources/Data/Managed/Mods/HollowKnightVisionInputReceiver/`.
Its prior DLL was backed up by the receiver installer. The normal Steam copy
was not modified.

Commands used:

```sh
swift test --package-path macos --enable-code-coverage --skip MenuStencilTests
swift test --package-path macos --enable-code-coverage \
  --filter 'MenuSelectorParityTests|Labeling|StencilGuidanceTests|HUDStencilTests|LiveDashboardViewTests'
./macos/build-app.sh
```

The final full Mac run supplies one consistent LLVM profile. Coverlet collected
the Windows core/linked-policy and receiver protocol runs;
its [custom-runner workflow](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/GlobalTool.md)
does not rebuild the instrumented assemblies during execution.

[coverage-summary.json](coverage-summary.json) inventories every changed Swift
and C# production file, measured/unmeasured status, and uncovered changed lines.
[summarize-coverage.py](summarize-coverage.py) regenerates that summary from the
raw exports. Full local logs, profiles, LLVM HTML, Coverlet exports and the
ignored Windows audit runner live under
`artifacts/audits/macos-parity-2026-10-29/`.
