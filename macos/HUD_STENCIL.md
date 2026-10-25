# Gameplay HUD stencil

Vision ships no recognition reference images. Both apps build HUD references
from the user's saved Gameplay HUD screenshot and its two health labels in the
selected language. Fresh installs, Factory Reset, missing or invalid labels,
and a language without a completed HUD stencil leave recognition unavailable.
The first stencil campaign must be completed before dependent features unlock.

The two labeled patches provide health appearance, position, and horizontal
pitch. No stock full, empty, or lifeblood artwork is appended. Recognition needs
two verified health matches; Soul is not a prerequisite. A new icon appearance
or layout needs a new captured reference. Damage can temporarily remove fresh
HUD evidence; the existing gameplay-state latch survives this dropout until a
recognized menu or session reset. Deleting the stencil clears that latch.

Matching uses the original capture before ground intensity correction, with
bounded top-left searches and a 32-slot limit. A shorter or missing match holds
previous exclusion geometry for at most half a second, without inventing health
matches. Menu transitions and capture generations clear held masks. Coordinates
scale from the reference image to the actual capture dimensions.

The immutable result travels with its image for Gameplay atlas masking. Hacker
atlas workers receive the same captured template and match their own frames.
Both keep the existing geometric HUD exclusion shield; that rectangle contains
no reference pixels and does not recognize game state. Camera matching, atlas
composition, timing, and GPU backend selection are unchanged.

Use the HUD stencil debug setting to inspect detected health and excluded
rectangles. The overlay does not enable or disable atlas masking. HUD labels
remain excluded from object-model training and runtime inference.

Photographic images and reference pixels used by matcher regression tests live
only under `Tests/HollowKnightVisionTests`, with explicit test resource entries
on Mac and Windows. They are not included in either app. Tests cover default
trackers without references, captured-label setup, language isolation, deletion,
matching counts, scaling, animation holds, and actual atlas pixel exclusion.
