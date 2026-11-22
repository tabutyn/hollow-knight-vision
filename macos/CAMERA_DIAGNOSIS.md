> Historical design/evidence. Experimental commands described here were retired on 2026-09-29; use the current README and RETIRED_FEATURES.md.

# Camera diagnosis with Hacker telemetry

`analyze-input-paths.py` now includes `exactFrameCamera` for each replay. This is
the primary camera accuracy measurement. The older `groundTruth*` fields remain
timestamp-nearest estimates for compatibility; do not mix them with exact-frame
results. The analysis runs offline and does not change the app or add GUI text.

Record Gameplay with `--render-frame-marker`. The existing recorder writes the
decoded Unity frame number with each solved capture and records the mod's camera
telemetry separately. `camera_diagnostics.py` joins those frame numbers. Missing
markers, missing telemetry, unavailable published poses, and ambiguous 24-bit
frame collisions are reported and excluded. There is no time-nearest fallback.

Each reference scene/receiver-session/projection gets one origin at its first
exact match, reused on every return to that scene and projection. Visual room
and island IDs never define reference origins. Corrections and recoveries do
**not** reset an origin. Thus this
measures relative drift, not the unknown absolute error already present at the
start. A projection scale change larger than 0.1% opens an explicit new epoch;
minor floating point projection noise does not. Distances use 640-by-360 capture
pixels. No trajectory fitting or best-lag optimization is performed.

```sh
python3 analyze-input-paths.py source.json replay-*.json > comparison.json
python3 test-camera-diagnostics.py
python3 test-analyze-input-paths.py
```

Reports include all-published and verified-ground errors, error spans above 16px,
per-second signed drift, processed-capture gaps, source-specific error, correction
steps, and atlas line ID churn. The recorded `poseVerified` flag belongs to the
ground tracker: the app can still reject its proposal and publish a motion bridge.
Use `verifiedGroundOutputErrorPixels` when specifically assessing accepted ground
poses. ID churn alone does not prove a duplicate or mistaken deletion.

For a controlled floor comparison, record with the existing optional
`--ground-audit-directory=/absolute/path` flag. Then run:

```sh
python3 diagnose-ground-camera.py \
  --audit /path/to/audit --replay /path/to/replay.json \
  --labels /path/to/world-ground-lines-v2.json \
  --output /path/to/paired.json
```

Audits join tracking by the exact capture timestamp, then join mod telemetry by
Unity frame ID. The same detected semantic spans are projected with Hacker's
camera and with the published visual camera. JSON reports precision, recall and
F1 against human horizontal lines; SVGs show both sets of observations in world
coordinates. These are diagnostic observation maps without line fusion. They do
not substitute for either production atlas.

The third score projects the actual persistent Gameplay lines using the same
fixed origin and scores the true current viewport. It does not independently
reanchor each line at birth; doing that would hide part of the camera error.

Horizontal overlap is continuous, with one-to-one row matching within 4px by
default (`--vertical-tolerance` overrides it). Duplicate rows cannot both match
one floor. Runtime masks are excluded. Both placements are scored within the
same true viewport; offscreen predictions may overlap legitimate unseen floor
and are excluded from this visible-view score. Scores sum visible length over
frames, not unique floor length. Positive labels are assumed complete in labeled
scenes; unmarked visible space is treated as negative. Entirely unlabeled scenes
are skipped. Provenance includes hashes of recordings, labels and matched audits.

Keep Hacker camera data strictly in diagnostics and labeling. Any production
acceptance/recovery change must use visual evidence and be tested on new replay
runs, with camera accuracy, map accuracy, recovery duration and throughput all
reported. A high `poseVerified` percentage alone is not evidence of stability.

## Placement recovery evidence

Recordings with runtime metadata `groundPlacementRecovery` set to
`deferred-ground-corrections-v1` validate discontinuous ground corrections before
committing ground lines, active feature patches, or local pixel references. A
plausible correction is retained privately for up to 120 ms, allowing the next
capture to verify the original reference rather than a fallback-biased origin.
Acceptance requires coherent evidence from two distinct captures or a verified
global match. Repeated captures do not supply a second vote. Provisional startup
strips remain free to seed; only the current tracker's ground/feature history can
activate this policy, independently of an older atlas still being purged.

Ordinary dropout/fallback behavior is retained. Freezing all reference updates
after any miss was tested and rejected: it could starve recovery when the old
reference left view. This version addresses viable rejected corrections, not
general proof of world placement after every fallback or gradual horizontal drift.

`placementRecoveryState` records `trusted`, `unlocated`, or `verifying`.
`atlasWriteAllowed` records the final admission for this exact capture, including
bootstrap and placement checks. It describes eligibility to submit an atlas
observation, not a completed pixel commit or a visible GUI blackout. An absent
field means unavailable evidence, not acceptance or rejection. Existing atlas
evidence and the live capture have separate lifetimes from new atlas writes.

Use `poseSource`, the recovery state, and exact-frame error together when auditing
recovery. The `poseVerified` flag includes this correction policy, so unverified
percentages are not directly equivalent to earlier recordings. A `trusted` policy
state is not an independent guarantee of absolute world accuracy. The policy uses
no Hacker camera transforms.

## Adjacent-row camera wobble

Saved purple floor estimates and the live green floor review are separate
overlays. Consolidating saved strokes does not change the camera measurement.
The production ground tracker now runs the existing fractional texture fit on
every accepted capture, rather than only when promoting a pixel reference.
The fit remains bounded to 1.25 pixels per axis and must improve held-out
checkerboard pixels; it cannot rescue a rejected coarse match.

On three short pixel-only sequences from recording `26799f3b` (7.85 seconds
total), an isolated solver comparison reduced the horizontal clip's largest
false vertical step from 0.998 to 0.143 solve pixels. The other two clips' largest
stationary-Y steps were 0 and 0.460 before, and 0 and 0.038 after. Acceptance
counts were unchanged. These clips include substantial tracking gaps, so they
do not establish full camera accuracy, map placement, recovery, or live speed.
A separate four-second stationary capture from recording `61a46d0f` accepted
240/240 exact-joined frames in both variants, with vertical spans of 0.036 and
0.041 pixels respectively.

A stricter sparse-match rejection candidate was discarded: it reduced accepted
frames in the horizontal clip from 47 to 17 and worsened horizontal error.
Lower motion obtained by holding more frames is not a tracking improvement.
Large published-coordinate jumps can also come from rigid island corrections
while the raw local Y estimate stays constant; assess those separately.

For bounded continuous ground-solver comparisons, set
`HKV_GROUND_PAIR_RECORDING`, `HKV_GROUND_PAIR_OUTPUT`, and
`HKV_GROUND_SEQUENCE_RANGES` (a JSON array of `{"start": ..., "end": ...}` capture
timestamps), then run `GroundPairReplayAuditTests` in release configuration.
Each range is limited to ten seconds. This uses archived pixels and masking,
with no camera telemetry supplied to the solver. Score exact-frame telemetry
separately and report rejected frames as well as the accepted-frame errors.
