# Gameplay HUD stencil

The fixed-position HUD stencil establishes Gameplay and masks Health, Mana,
and Geo before camera tracking or atlas writes. Those three labels remain in
the labeling catalog as stencil sources but are excluded from object-model
training and runtime inference. The immutable stencil result travels with its
captured image; a newer or older frame is never substituted at atlas submission.

The bundled `Resources/hud-stencil-template.json` contains RGB references and
geometry extracted from labeling example `992a0aa1-243b-4139-9ca9-a04da738dec4`.
The original labeled example can be deleted. It is not loaded at runtime and
the atomic labels are not needed to train the object detector.

- Health uses the measured 16.55 px pitch at 640 px capture width. Starting at
  the first icon, it matches full, empty and blue masks until the first missing
  cell, with a bounded position search and a 32-slot safety limit.
- Matching excludes the first icon's connecting ornament and the transparent
  edge of empty masks. Normalized texture correlation plus colour checks keep
  background changes and blue/white masks from confusing the count.
- Main mana uses fixed-position full and empty Soul references. Matching keeps
  stable neutral edge pixels, follows the verified health-row offset, and checks
  a small local search. The three extra vessel positions use labeled full/empty
  references plus derived quarter, half, and three-quarter fill variants.
- Geo always uses the labeled maximum-width rectangle. No digit-width estimate
  or OCR controls its exclusion area.
- A shorter or missing match holds the previous exclusion geometry for at most
  half a second during HUD animation. The display reports current matches and
  marks held masks; it does not invent current health from the held geometry.
- Menu transitions and capture generations clear the held state. References and
  coordinates scale from the reference capture to the actual image dimensions.

Gameplay state requires two verified health masks plus a verified main Soul
stencil. A coarse fixed-position brightness pair remains as startup fallback;
learned Health/Mana/Geo detections no longer participate.

Open **Debug View → HUD Stencil** to see the exact excluded rectangles on the
live image and current counts. Red covers health cells and connectors, blue
covers mana, and yellow covers fixed Geo. Inner outlines distinguish full
(white), empty (orange), and blue health. This toggle is independent of the
object-detection overlay and does not enable or disable atlas masking.

`HUDStencilTests` includes ten saved HUD crops with independently reviewed
counts, including longer rows, damaged health, blue masks, changed backgrounds,
older HUD offsets, and partially filled mana vessels. Two crops captured during
health animation assert mana only. It also checks menu/generation gating, scaled captures,
temporary mask retention, fixed Geo geometry, transparent atlas pixels, and the
debug overlay. The `hudStencil` telemetry stage measures live matching cost.

Limits: transient icon animations can make a frame's count incomplete; the
short mask hold only protects previously observed slots. A completely new HUD
layout, different icon artwork, or spacing outside the bounded search needs a
new reference. The current stencil uses conservative rectangular cells, not
per-pixel icon alpha masks.
