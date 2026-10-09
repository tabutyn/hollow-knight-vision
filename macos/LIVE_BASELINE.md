# Live atlas baseline (historical)

This describes the September 6 baseline and its original acceptance ladder.
The current live path uses native capture cadence, 60 Hz presentation, and ground texture camera tracking,
with bounded asynchronous global recovery. See [GROUND_TRACKING.md](GROUND_TRACKING.md)
and [TRACKING_STABILITY.md](TRACKING_STABILITY.md) for the current implementation.
`captureToPublish` now names main-queue publication latency; actual drawable
presentation is logged separately as `captureToVisible`.

The September 6 baseline used seven runtime pieces:

1. ScreenCaptureKit captures the Hollow Knight window at 30 fps without the cursor.
2. Vision estimates camera translation independently of the 30 Hz presentation path.
3. The region detector finds the HUD and Knight. Particle and depth work is disabled live.
4. A persistent one-room world stores masked observations, sparse global feature
   keyframes, motion edges, confirmed loop edges, and optimized camera poses.
5. Pose-independent global retrieval runs at no more than 2 Hz. Two consistent
   observations are required before a loop edge can move the live camera origin.
6. The source-backed composer writes timestamp-matched observations to sparse
   256 px tiles and rebuilds history after an accepted graph correction.
7. Metal submits the newest boxed capture over the persistent atlas tiles on
   its own 30 Hz clock, independently of capture, vision, and atlas processing.

The `presentation` log reports `metalSubmitFPS`. The `performance` log reports
`modelPublishFPS` plus p50/p95 capture-to-stage, vision, render, and
capture-to-present timing.

Layer extraction, planar depth, contour ownership, the phase-five multi-layer
texture selector, and portal tracking remain offline experiments. Live session
writes, global feature retrieval, graph correction, and tiled source provenance
now run in the app.

## Small test ladder

Run each test alone. Save a short screen recording and record pass/fail before adding
another component.

### A. Static pose and boxes

- Stand still for 10 seconds.
- Pass: health has a red box, Geo has a yellow box, Soul has a blue box, Knight
  has a green box, cursor stays visible, camera coordinates stay within 3 px,
    and displayed rate stays at or above 28 fps.

### B. One camera pan

- Reset the atlas. Walk right until the camera pans, stop, then walk back to the
  starting view.
- Pass: camera X changes smoothly, atlas width grows, the current window remains
  attached to its reported pose, and the return error is at most 12 px.

### C. Dynamic-pixel masking

- Reset the atlas. Move and jump in one camera view for 20 seconds.
- Pass: the live green Knight box follows the character and no Knight trail is
  retained in the atlas outside the current frame. HUD pixels also leave no trail.

### D. Two-minute performance hold

- Traverse the same hall for two minutes without resetting.
- Pass: displayed rate stays at or above 28 fps, controls remain responsive, and
  the sparse atlas continues expanding without a fixed-size reset.

### E. Loop-back and reopen

- Reset the atlas. Walk right, climb and look up, descend, then return through
  the original hall. Repeat after quitting and reopening Vision.
- Check **World feature overlay** only while inspecting evidence.
- Pass: the return produces a confirmed loop edge, old tiles move with the pose
  graph, the current view stays aligned with saved texture, and endpoint residual
  is at most 12 px. Unchecking the overlay restores the ordinary map view.

### F. First isolated depth experiment

After A-D pass, test one extra foreground texture layer offline. Use a hand-authored
mask for one large foreground object and one fixed parallax factor. Compare its
reprojection against the flat atlas on held-out frames. Accept it only if median
contour error improves and runtime cost stays outside the live camera loop. This
tests whether a layer helps before restoring automatic depth ownership.
