# Windows live ground tracking alignment — 2026-10-25

Baseline: merged Mac/main `ad08a4b`.

## Fixed integration gap

Windows ran `GroundHypothesisTracker` for overlays while a separate texture-only
`GroundCameraSolver` drove the actual camera. Persistent recovery and global
corrections therefore did not reach the displayed view or atlas.

One production ground tracker now owns the live ground pose. The app passes the
same cleaned floor lines, foreground exclusions and monotonic capture timestamps
used by overlays to `VisualGameplayTracker`. Placement recovery validates full
texture/inlier/global-match proposals inside the update transaction. Camera and
atlas use the verified, confirmed result. Recordings retain actual inlier counts,
corrections, line IDs and provisional state; GPU diagnostics report that solver.

Mac settings retained: 0.2s line confirmation, 3s confirmed-loss grace, background
global search, calibrated subpixel texture solving and a 192px maximum local
search radius. Windows scores large patch batches with CUDA; Mac uses Metal.

Fades retain persistent ground evidence. Measured fallback feeds back into the
same tracker without replacing a pending placement or independent provisional
trajectory. Only a bridge with confidence >=0.8 can enable provisional promotion;
it still requires >=12 strong matches over >=0.20s. Atlas writes also require the
placement gate, in addition to verified ground/bridge and bootstrap admission.

Canonical recovery now challenges drifted data with the oldest four line IDs
and frozen first-view feature population. It searches all X phases without the
local predicted-Y bound, verifies independent strip pixels, and revalidates
background results on current pixels before timed two-observation confirmation.
Repeated measured handoffs preserve this search/confirmation cadence.

## Verification and limits

`VisualGroundIntegrationTests` exercises the actual camera/atlas adapter with
persistent background recovery after a fade, checks published X/Y against the
tracker result, and covers trusted/untrusted provisional promotion and clearing
stale texture at an unmeasured handoff. Existing recovery, masks, immutable
first-view, unrelated-view rejection and CUDA/CPU parity regressions run in the
canonical Windows build.

Validated locally: 973 core tests, 67 UI regressions and receiver protocol/state
tests passed. CUDA was required on the GTX 1080; parity probe reported 13 GPU
dispatches. `Test-MacParity.ps1` passed inventory validation. The canonical build
installed and hash-verified the Start Menu copy; app assembly SHA-256:
`DDA27C1661D28A738B3682C63142A6C7A40D400395F7CE16CDB977BB3AFF1BDF`.

These are algorithm and integration regressions, not a measured identical-run
comparison of Mac and Windows gameplay. The floorless registration backend still
differs: Mac uses Vision image registration and live coarse/place arbitration;
Windows uses its union-masked 64x36 estimator. Native capture pixels and backend
numerics also need recorded-run differential validation. The parity inventory
therefore remains partial. Atlas composition retains the user's newest-pixel
policy; this change does not restore temporal or median blending.
