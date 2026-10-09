# Retired features — 2026-09-29

The cleanup keeps the current live product: Gameplay, Label, Model/training,
Ops, Hacker, independent atlases, horizontal manual ground labels, recording,
saved-input replay, world persistence, exact-frame camera diagnostics, masks,
room transitions, and input watchdogs.

Removed from the build:

- Legacy dashboard/capture model, standalone ground editor, old atlas compositor,
  unused review windows/navigation, receiver simulator, and their exclusive tests.
- Offline video/depth/parallax reconstruction and `--replay MOVIE`. Saved-input
  replay (`control-game.sh replay`) and `--world-replay-session` remain supported.
- Affine/subpixel registration, wide snap retry and its pair-only audit. Current
  integer texture matching, normal recovery, and global corrections remain.
- Owned-BGRA copy and conversion benchmark, alternative surface architectures,
  and performance comparison flags. Current defaults remain: Core Image,
  native capture cadence, queue depth 3, two Metal drawables, user-initiated
  vision workers, independent raw rows for global search, hysteresis semantics.
- Historical platform/walk probes and their tests, and the parent Python/web lab.
- The old exploded layer view. Live room/world atlas presentation remains.

Retired launch flags fail explicitly rather than silently selecting a different
experiment. Historical saved paths remain readable: removed experiment fields
are ignored during decoding. Historical ground evaluation architecture names
remain readable as strings; new evaluations always use the selected algorithm.

`LiveCaptureModel.swift` now contains the actual live model. The surviving review
data store is `LabelingModelReviewStore.swift`. Shared `GroundPixels`,
`RectangleOverlap`, and `ImageFileIO` remove duplicate primitives. Store-specific
validation, JSON schemas, commit ordering, and camera coordinate systems remain
with their owning components.

Original working source (including uncommitted changes) is archived in
`hkv-code-cleanup-20260929/before.tar.gz` at the outer workspace root. That folder
also contains the per-file change manifest, validation logs, replay evidence,
and before/after line counts. Recordings, trained assets, historical experiment
results, and the user's ground-label document are not deleted.
