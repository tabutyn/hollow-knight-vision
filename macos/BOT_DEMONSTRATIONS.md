# Collecting Hollow Knight Bot demonstrations

Gameplay and Hacker each have one independently trained Stable-Baselines3 actor. During gameplay the active actor chooses all four controls from four recent observations. K records the human demonstration, and B plays the selected model.

## Recording and bot controls

**K** records a single manual run for both replay and bot learning. **🤖 B** starts or stops the bot, including its menu navigation. There are no separate Record or Demo buttons.

1. Launch `/Applications/Hollow Knight Vision.app` in Modded mode and enter **Gameplay** or **Hacker**. Finish editing, model training, and replay first.
2. Stand still on the ground and press **K**. Vision captures a replay checkpoint. In either workspace, with Hacker telemetry available, K starts a Hacker clip and a Gameplay clip on the same clock and records every forwarded manual control event in both. A receiver without Hacker telemetry can record a Gameplay clip.
3. Play through Vision with arrow keys, **Z** to jump, and **X** to attack. K turns red and the REC status shows elapsed time and usable learning observations.
4. Press **K** again to save both outputs. Right-click K to reveal the replay or learning recordings. The Hacker clip and replay share an ID. Its Gameplay partner has a separate clip ID. Both manifests link to the same replay through `replayID` and to each other through `pairedClipID`.

Hacker always uses the exact rendered frame's mod-supplied Knight and enemy positions, with your visible green floors and walls projected through that frame's camera. The accompanying Gameplay clip uses the normal visual actor, ground, and wall pipeline at its own sampling cadence. A visual miss is recorded for Gameplay. The REC badge shows separate usable counts for Hacker (H) and Gameplay (G). Missing, stale, malformed, or mismatched mod telemetry releases bot controls.

Start with several manual clips of 30–90 seconds covering both walking directions, released controls, short and long jumps, jumping while moving, enemy avoidance, and attacks. Keep multiple sessions so validation can reserve an entire recording. Bot-generated play and automated replay are not added to human demonstration data. Existing playback commands remain available for K recordings.

For the next improvement round, concentrate on the bot's failed maneuvers: record several complete approaches, jumps, landings, and recoveries near the same pits in both directions. Keep successful recoveries as well as clean traversals. Stop B before starting K and take manual control; bot playback evidence is diagnostic data, not an expert action label. Train from Bot → Summary, then use the new version and compare it on the same route. More epochs on unchanged clips do not supply the missing examples.

Entering an editing workspace, opening settings, losing window focus, changing models or Atlas, or stopping capture finalizes both manual recording outputs. Vision discards interrupted training clips. On a normal stop, it keeps each bot’s clip if it contains a trainable observation, including eligible Gameplay frames with an occluded Knight. A failed learning recorder cancels the paired replay start and reports the problem. Other forwarded controls are retained in the raw evidence; unsupported action windows are excluded during dataset export.

## B in menus and Gameplay

An orange vertical line marks a sustained left or right push with no measured movement in the room. Both modes combine the Knight’s screen position with camera motion so a stationary camera can still show the Knight walking. The line disappears when the Knight passes the contact. Each fresh start and room reset begins with an empty contact map. Clips retain the evidence available when each frame was recorded.

Blockade position, direction and nearby terrain are inputs to the model. The actor can choose any combination of movement, aim, jump, and attack at a blockage. The input lease expires on stale perception or loss of focus.

During recognized **Gameplay**, B starts the policy for the current Gameplay or Hacker workspace without resetting a save. From game menus, B navigates through Main Title → Start Game → Select Profile, archives slot 4, clears its running-version save when occupied, starts a fresh game, alternates X and Z during the opening cutscene until the Gameplay HUD appears, and hands off to the requested bot domain. B stops either navigation or play. Manual gameplay input also takes control.

The stencil routine handles menus until it detects Gameplay. It then releases the controls and starts the selected mode’s general actor. The same forward pass chooses all buttons around enemies, Geo deposits, pits, open ground and mapped blockades.

Press **R** in Gameplay or Hacker, or click **Reset Room** in Settings → System. Stop any active recording, playback, or bot run first. Reset Room returns the Knight to the room's starting position and restores its ordinary enemies, breakable barriers, and Geo deposits. King's Pass uses the verified fresh-game spawn. For other rooms, the receiver remembers the first grounded, controllable entrance observed in the current loaded game; leaving and re-entering through another exit does not replace it. These remembered positions do not survive a game restart. Once the game confirms the reload, Vision clears the Gameplay and Hacker Atlas auto-saves. Named Atlas saves and authored terrain are kept separately.

**Lifeblood Cocoons** appear as cyan rings in **Bot → Hacker**. In King’s Pass, press K before approaching the intact blue cocoon and keep recording as you break it and collect the Lifeseeds. The receiver records the visible cocoon on the same game frame as enemies and Geo deposits. Breaking it clears the ring. Version 10 policies receive the nearest intact cocoon’s offset from the Knight and a presence flag.

**Geo deposits** appear as yellow rings in the clip preview. K records each visible deposit from the object detector in Gameplay and from the game receiver in Hacker. The recorded choices during a fight or at a deposit teach the actor which controls to use there. For a demonstration, start K with the enemy and deposit visible, fight, mine the rock, collect the Geo, then stop K. New policies receive the nearest deposit’s horizontal and vertical offset and a presence flag. Older clips supply zeroes for those new inputs.

To teach combat, start K before approaching an enemy and stop K just after the fight. Record separate encounters from both directions and different distances. Include the pauses between swings, jumps and retreats that keep the Knight healthy. Recording in Hacker saves paired Hacker and Gameplay examples. Bot → Summary → Train learns from the recorded controls and reports how often the actor matched attacks and releases in held-out fights. With two or more fresh combat clips the trainer can reserve one fight for this check. The new version becomes active after training. Try **B** from a different position. A confirmed defeat in live play is a separate outcome from matching an input in a clip.

The original command-line `--auto-navigate-gameplay` routine still loads profile 1. The bot's menu and play controllers share exclusive input ownership, so they cannot compete with K recording or replay.

Before clearing slot 4, the bot copies every slot-4 save version, mod sidecar, and backup into:

```text
~/Library/Application Support/HollowKnightVision/bot-v1/save-archives/slot-4-<uuid>/
```

Archives have SHA-256 manifests and are checked again before confirming Clear Save. The live occupied-slot stencils determine the active save-file version family. Slots 1–3 and shared settings are preserved. An empty slot skips Clear Save. Right-click B → Show save archives to inspect the archive. To recover a save, close the game, preserve current slot-4 files, then copy archived original filenames back to the manifest's source directory.

Unknown menu screens wait and eventually stop with a visible reason. The bot does not send confirmations through unrecognized contexts. Hacker background atlas saves preserve play and recording; replacing the active atlas interrupts controls. Your authored green lines are never changed by the bot. Blue setup guidance disappears after the required stencil setup is complete.

## Local files

Sessions are stored at:

```text
~/Library/Application Support/HollowKnightVision/bot-v1/demonstrations/demo-<uuid>/
    manifest.json
    events.jsonl
    frames/<observation-index>.jpg
```

The recorder streams JSONL to temporary storage while playing and adds each eligible clip to the training library when recording finishes. Collection saves one game screenshot per second for reviewing perception. No files are uploaded. The `bot-v1` directories and training environments are excluded from Git.

The manifest records the game mode, detector description, input transport, schema, ordered feature names, sample period, and counts. `native-posted` means controls were posted to the normal game's process; `receiver-sent` means the modded input bridge sent them. These are forwarded control labels, not claims of per-action simulation acknowledgement.

## Dataset inspection and export

The app provides **Bot** beside Model in the bottom navigation:

1. **Summary** compares Gameplay and Hacker results side by side. Its top-right **Train** starts both eligible training jobs in parallel. Each model has independent versions, activation, and rollback.
   The robot badge counts new usable clips since the last completed training. Summary counts a paired recording once, and each data tab counts its own clips.
2. **Gameplay** lists captures made with detected terrain. **Hacker** lists captures made with authored green terrain. All completed usable clips in each dataset are included automatically; there are no inclusion checkboxes. Train within a data tab updates that model only.
3. Clips use persistent numbers and one-line rows with time and duration on the right. Play Clip and the timeline use the original timestamped capture frames when available. The keyboard highlights the recorded keys held at the displayed time. Older clips with only review images play at their saved image cadence.
4. Use the trash button to permanently delete a bad clip after confirmation. This removes its learning folder. The shared replay JSON and raw frame archive are removed with the last clip from that run. Existing model versions and their training snapshots remain available; deleting a clip does not untrain an existing model. Other clips keep their numbers.
5. Record at least two new clips between updates so comparison uses unseen sessions. The version card shows action agreement, jump balanced accuracy, and own-action agreement when those metrics were recorded. Use these results alongside live play on your target route.
6. Successful training automatically activates and loads the new verified model for its domain. Held-out comparison scores are reported for review. **Use Version** selects an older model manually. **B** selects the Gameplay policy in Gameplay and the Hacker policy in Hacker. **Restore Previous** rolls back that domain independently. A manual choice survives reopening Vision until a newer training run completes.

Each candidate preserves checkpoint, native export, source hashes, split, metrics, and comparison report. Training runs live under `bot-v1/training-runs/<uuid>/`; new models and active selections live under `bot-v1/models/gameplay/` and `bot-v1/models/hacker/`. The previous single-model installation remains readable as a legacy baseline.

The training interpreter remains outside the app bundle. `setup-bot-trainer.sh` prepares the local environment, and `build-app.sh` records its location in the installed app. `HKV_BOT_PYTHON` can override the interpreter; `bot-v1/training-env/bin/python` is also supported. Missing dependencies produce a visible training error and preserve the active models.

## Terrain audit and playback evidence

New K recordings preserve `authored-ground.json` with a SHA-256 digest, `audit-context.json`, and `terrain-audit.jsonl` alongside observations and controls. The paired replay retains raw source PNGs, source hashes, rendered-frame IDs, capture timestamps/generations, mod telemetry, and the masks/tuning used by the detector.

Terrain audit rows record detected and authored spans in the same bottom-left image-pixel coordinates. Authored projections require an exact capture-matched mod camera. `referenceAvailable: false` means unavailable evidence, never empty ground truth. Authored positive spans are partial annotations: do not assume unmarked pixels are negative labels. The frozen document also retains walls and corner metadata supported by its schema.

B play is captured separately under `bot-v1/evaluations/gameplay/` or `bot-v1/evaluations/hacker/`. Each run records model identity/hash, actual forwarded control edges, policy observations, the exact source frames selected for terrain audits (about 10 Hz), processing stages and ground inputs, frozen authored geometry, and paired terrain audits. K replay archives retain the higher-rate source capture. These runs are not expert demonstrations and never enter training automatically.

An agent can inspect or export a portable evidence bundle:

```sh
python3 macos/BotPython/bot_audit.py inspect /path/to/demo-uuid
python3 macos/BotPython/bot_audit.py export /path/to/demo-uuid --output /new/audit-bundle
```

Exports verify source-frame hashes and retain missing-frame accounting. Historical clips without frozen authored references are explicitly marked unpaired; current labels are never presented as if captured historically. Existing K raw replays remain discoverable where available.

The command-line tools remain available for inspection and development:

Inspection uses Python's standard library and requires no training packages:

```sh
python3 macos/BotPython/hkv_bot_dataset.py inspect \
  "$HOME/Library/Application Support/HollowKnightVision/bot-v1/demonstrations"
```

It reports candidate examples, rejected reasons, action presses, session identities, and source hashes. Active, failed, corrupt, and incompatible sessions appear under `skipped`; export refuses a root containing them. Preserve such sessions for diagnosis and select a completed session or a separate directory containing the desired session folders for export.

Set up the separate training environment when ready. This is already installed on the development Mac used for this change; it is not required to record demonstrations:

```sh
zsh macos/setup-bot-trainer.sh
macos/.bot-venv/bin/python macos/BotPython/hkv_bot_dataset.py export \
  "$HOME/Library/Application Support/HollowKnightVision/bot-v1/training-runs/<domain-run-id>/demonstrations" \
  --output /private/tmp/hkv-bot-demonstrations.npz
```

Use a snapshot from one domain for low-level exports; the combined demonstrations root contains both model datasets. The export contains float32 observation history, integer MultiDiscrete targets, monotonic timestamps, session identities, and temporal segment IDs. A JSON sidecar records source hashes and the dataset hash. Short jump and attack taps are retained within their 100 ms action window. Conflicting directions and unsupported controls invalidate that window.

Action labels start when an observation becomes available to the agent, using the same host monotonic clock as forwarded input. The capture presentation timestamp is separate provenance. `--action-delay` can label a later control window for reaction-delay experiments without changing the raw evidence. The default is zero; assess alignment using recorded control edges and review frames before selecting a different value.

## Observation contract

Schema 2 contains **155 values per observation**, with four observations stacked oldest to newest into **620 values**. Schema 1 policies continue to receive the first 144 values of each observation. All image coordinates use a bottom-left origin and frame width as the length scale. Unused entries are zero with explicit presence or validity fields.

| Indices | Meaning |
| --- | --- |
| 0 to 4 | Knight center X/Y, width/height, and position confidence. Hacker uses mod-supplied body sizes, or zero for unknown dimensions. |
| 5 to 7 | Detection age divided by 0.2 seconds, terrain validity, capture processing age divided by 0.35 seconds. Ages are bounded from zero to one. |
| 8 | New temporal segment flag, assigned during export. |
| 9 to 12 | Previous horizontal direction, aim, jump, and attack, assigned during export from the control interval ending at the current observation. Future control events never enter these fields, including when label windows overlap or use a reaction delay. Horizontal is -1 left / +1 right; aim is +1 up / -1 down. |
| 13 to 15 | Legacy recording placeholders. Schema 4 and newer policies use these positions for blockade evidence. Schema 8 appends road guidance after the Atlas grid. |
| 16 to 95 | Up to 16 nearby terrain spans: relative start X, relative end X, relative Y, confidence, presence. |
| 96 to 143 | Up to 8 nearby enemies: relative center X/Y, width/height, confidence, presence. |
| 144 | Wall analysis available. An analyzed frame can have zero detected walls. |
| 145 to 149 | Nearest wall left of the Knight. Relative X, lower Y, upper Y, solid side (−1 left, +1 right, 0 unspecified), presence. |
| 150 to 154 | Nearest wall right of the Knight, using the same fields. |

K records the wall fields in both modes. Gameplay includes visually detected walls and contact-supported walls when available. Hacker projects the authored wall endpoints through the matched camera. Capture review draws every visible authored wall and floor in green. New policies retain the two precise nearest-wall inputs and also receive every visible floor and wall through a screen-space map. Schema 11 adds a separate door map; Hacker uses projected authored doors while Gameplay uses visual evidence. Existing policies retain their original inputs. B supplies the same fields to the selected schema 2 policy. The ground overlay can be hidden.

Training in Vision recovers visual walls for older Gameplay demonstrations from matching, hash-verified source pixels, using the recorded masks and semantic tuning. Older Hacker clips retain their recorded floors and controls. Their historical pixel-detected wall slots are omitted from training and review. Frames without matching evidence keep the wall availability flag at zero. The original demonstrations remain intact. Summary shows training and held-out wall coverage and how many held-out decisions changed when wall geometry was removed from the four-frame history.

Gameplay terrain and actors use visual detections. Hacker always uses mod-supplied Knight and enemy centers and body sizes from the capture's exact rendered Unity frame, together with visible positive green floor lines from the same scene. The visual ground solver, object detector, atlas compositor, and display throttling do not gate this Hacker path. Off-screen Knight state, unavailable or malformed actors, old receiver telemetry, capture-generation mismatch, stale captures, and missing authored terrain are unusable. Enemies are active, living hostile actors in the camera view, including actors hidden by foreground scenery. An empty enemy array is valid evidence of no nearby enemies; an absent actor payload is not.

Gameplay retains a short visual Knight track through missed detections. Recent measured motion, verified visual camera movement, observed floors, and changes in bot controls bound its estimate. The maximum time since the last real detection is 0.6 seconds with a verified visual camera, or 0.35 seconds without one; processing time consumes that budget. Estimated frames never renew it. Reacquisition replaces the estimate immediately. Room/capture changes, missing terrain analysis, stale captures, excessive uncertainty, and off-screen predictions prevent continued control. Hacker never substitutes this estimate for mod telemetry.

Estimated observations use `knightSource: "visual-predicted"`, decaying confidence, and `knightPrediction` metadata containing the last real detection time, age, uncertainty, camera-compensation flag, and original detection issues. Both K learning data and B evaluation evidence retain this distinction. Missing enemy detections remain unavailable rather than being invented. Training export independently checks the prediction bounds and a genuine visual anchor in the same recording, room, and capture generation before accepting estimated inputs.

Gameplay can learn movement and jumps through Knight occlusion from clips that include those conditions. During a fresh, eligible occlusion, the same actor receives a masked Knight box and the available terrain and history. Stale captures, room transitions and loss of game control release held inputs.

Schema 3 policies append a 24 by 14 screen map for floors and a second map for walls to the 155 existing measurements at each of four time steps. Every visible segment marks the cells it crosses, including segments beyond the nearest-wall and floor slots. The clip stores the exact screen endpoints. Hacker uses authored segments projected through the matched camera. Gameplay uses visual detections. The same map is used in training and live play. Existing policies retain their original input dimensions.

Optional `terrainSource`, `humanTerrainCount`, `knightSource`, and `enemySource` fields preserve actor and floor provenance. Hacker actor sources are `hacker-frame`; Gameplay sources are `visual`. Confidence is one for exact mod positions. Unknown body dimensions are zero. Actor IDs and names are diagnostic only and never become policy class labels. Positions are relative to the Knight except its own frame position. Values are clipped to [-4, 4]. Each policy receives the four-frame history for its schema.

Actions are `MultiDiscrete([3, 3, 2, 2])`: horizontal `[neutral, left, right]`, aim `[neutral, up, down]`, jump `[released, held]`, and attack `[released, held]`. Export clears history at session boundaries, room or capture-generation changes, invalid observations, unsupported actions, and gaps longer than 0.35 seconds. Missing early history is zero-padded.

Hacker observations are built directly from the matched rendered capture and mod actor packet. Local `bot-demo-control.sh status` diagnostics report exact issues, processing age, actor provenance, enemy count, green-floor decisions, and non-neutral decisions.

### Atlas paths and objectives

**Settings → Debug View → Atlas paths** shows walking paths one Knight height above the ground. Gameplay combines detected ground with the ground confirmed beneath the Knight as he walks. Hacker uses authored ground, walls, spikes, and doors. Walls constrain movement, spikes exclude unsafe landings, and a door at a floor edge prevents the planner from inventing a fall through that exit. Yellow shelves connect platforms within jumping reach. Orange lines with downward arrows show falls. Branches show lower platforms the Knight can reach by steering in the air. A pair with a jump connection uses that two-way route.

Hacker opens with **Objective** selected. Click any point on the room’s drawn path, including beyond the captured tiles. Authored ground, walls, doors, and spikes remain visible, and the reachable route from the Knight to the target turns white. Click the target again to resume automatic exploration. Automatic objectives use registered camera coverage. Selecting **Path** or **Objective** turns on Atlas paths. Turning that setting off returns to the previous editing tool. In Gameplay, click a visible path to set the objective.

In **Hacker → Path**, choose **Jump** and click two platforms to add a single-jump connection. The first selected platform turns purple. **2 Jump** creates a differently colored double-jump connection. **Delete** removes a manual connection. Generated platform paths follow the ground. Connections are stored in `~/Library/Application Support/HollowKnightVision/atlas-path-connections-v1.json`.

Schema 8 carries six values in each of four observations. They describe whether a route is available, the next horizontal and vertical direction, Knight-height clearance, remaining path distance, and whether the objective was selected automatically. Positive Y points up. **K** records the measurements alongside your actions. For older clips, training reconstructs ground paths from recorded terrain and camera evidence where available. Earlier brightness-based road hints are omitted because those hints had a different meaning. A newly trained policy learns how to use the ground-path inputs.

### How exploration and combat affect the weights

Each capture carries a local view of the Atlas. The two frontier values describe how close unmapped ground is beyond the left and right edges. New captures also contain a 5-by-3 grid of nearby map coverage, including gaps above and below the current view. Separate flags identify missing map and health measurements. Gameplay uses the visual Atlas. In Hacker, the bot samples covered pixels in the room’s committed Atlas tiles, including restored tiles, at the frame-matched mod camera position. Training can recover a missing Atlas view from the other half of the same K capture when that measurement was already available within 200 ms. Model versions trained entirely on older clips learn the frontier inputs first. The extra grid connections begin learning when demonstrations contain the grid.

The trainer looks one second ahead within each continuous stretch of a human demonstration. Positive progress along the selected path, measured in Knight heights, strengthens the preceding examples. The planner keeps a reachable exploration objective through small floor corrections. When a newly visible shelf extends the route at that objective, the planner can advance the objective to the next platform as the Knight approaches takeoff. The direction arrow follows the chosen shelf through the jump and resumes walking guidance on landing. Once it reaches an objective, the planner chooses the farthest reachable ground that still needs walking. Progress is scored against the objective in force when the action was taken. The next objective starts with a fresh baseline. Returning to a point already reached for that objective earns no second credit. Automatic exploration also remembers credited travel on each stretch in each direction. When visual tracking splits an area into pieces whose relative positions are unknown, the planner carries its heading to reachable ground in the next piece. Until pieces can be placed on the same map, only the highest measured total among those pieces contributes to exploration reward. A sustained loss of one health mask costs 32. Remaining within 80 solve pixels for two seconds costs 0.12 per subsequent decision. These measurements scale the strength of the demonstrated action when fitting the actor.

Receiver 0.9.8 measures nail damage on the struck enemy and listens for its death confirmation. One enemy HP removed earns one point and a confirmed kill adds 32. A swing that deals no damage earns none. Matching observations can transfer this training signal from a paired Hacker recording to Gameplay. Standalone visual recordings currently supply combat examples through the player’s buttons. A disappearing detector box supplies no kill credit. Damage and kill counters are training labels only.

Live B evaluations keep their own controls, model identity, and Atlas score. Playing with B leaves the actor’s weights fixed. Recording with K and training a new version updates them.

In Gameplay the two horizontal frontier distances come from the active visual Atlas. Coverage samples the unmasked interior of committed screenshots at 64-solve-pixel spacing, per room and within its composition bounds. Existing coverage, placement-only corrections and the first observation of a disconnected island earn no points. Stable decreases in visually recognized full or lifeblood HUD masks supply the live damage estimate. An unreadable HUD provides no health measurement. Evaluation recordings contain the visual map inputs and an `atlas-score.json` summary. K demonstrations remain separate. B runs use fixed weights.

## Initial behavior cloning

After collecting and inspecting demonstrations:

```sh
macos/.bot-venv/bin/python macos/BotPython/train_behavior_cloning.py \
  /private/tmp/hkv-bot-demonstrations.npz \
  --output /private/tmp/hkv-bot-policy-first
```

This fits the action-producing part of an SB3 PPO model to the demonstrated controls, with training weights derived from measured outcomes. Gymnasium describes the observation and action shapes for SB3. The training job reads recordings and makes no game-step calls. The trainer also rehearses the first deliberate control in each clip’s opening segment, both with its recorded history and with the empty history used when B starts. An initial jump or stationary swing is a valid example. Training discounts a short hands-off setup at the beginning of a K clip when the Knight is tracked and stationary with no enemy or blockade. The original recording, later pauses, and tactical waits remain available. Whole clips are held out to evaluate four-button agreement, both with recorded button history and with the actor’s own preceding choices. Native export verifies that the checkpoint and the exported actor choose identical buttons on held-out observations.

## Playing the trained policy

1. Enter **Modded → Gameplay** or **Modded → Hacker** with the receiver connected. Hacker uses the exact-frame mod actors and authored terrain. Gameplay uses visual detections.
2. Press **B** or click **🤖 B** to start. The bottom bar shows the current action or **BOT · waiting**. Press **B** again to stop and release the controls.
3. Press any manual gameplay key to take over immediately. Changing workspace, losing window focus, opening settings or labeling, pausing, changing the detector or Atlas, and losing the receiver stop the bot. Gameplay briefly bridges missing Knight detections as described above, displaying **estimating Knight**. Expired estimates or other unusable perception release held buttons and clear history while the bot waits for fresh observations.
4. Record more manual demonstrations with **K** while B and replay are stopped. K produces both replay and learning data.

The Train button exports each completed model automatically. For a command-line export of an existing checkpoint use its model directory.

```sh
macos/.bot-venv/bin/python macos/BotPython/export_bot_policy.py \
  "$HOME/Library/Application Support/HollowKnightVision/bot-v1/models/v1"
```

The original installed model directory is `~/Library/Application Support/HollowKnightVision/bot-v1/models/v1/`. It contains `model.zip`, its training `manifest.json`, `runtime-policy.json`, and `runtime-manifest.json`. The app verifies the actor and source checkpoint hashes and the observation/action contract. It evaluates the exported Linear/Tanh actor with Accelerate, so Python is required for export and training only. Use **Bot → Summary → Use Version** to select a verified model for each domain without overwriting the baselines. The robot button's **Bot details → Reload model** also reloads the selected version.

## Verification

```sh
swift test --package-path macos --filter 'BotDemo|BotDemonstrationTests|BotPlayback'
macos/.bot-venv/bin/python -m unittest discover -s macos/BotPython -p 'test_*.py' -v
macos/build-app.sh
```

The tests cover native actor/action conversion, model hash verification, history reset and input lease expiry, coordinate conversion, evidence freshness, airborne frames, temporal segmentation, short taps, input exclusions, streamed files, review images, and SB3 export/train/save/reload with a session holdout. Synthetic fixtures verify the pipeline; real demonstrations are needed to assess learned gameplay.

## Local bot diagnostics

For a repeatable Gameplay trial, use `macos/bot-demo-control.sh restore-start path-….json` while the app is in Gameplay and the bot is stopped. Check the reported evaluation pose against the recording's starting checkpoint, then press B. This restores only the checkpoint. B supplies all subsequent controls, and K remains available for separate expert recordings.

Launch the installed app with `--game-mode modded --enable-automation-control` to enable the existing local control channel. `macos/bot-demo-control.sh status` reports the active phase, last recognized menu, archive path, bot decisions, and terrain provenance; `macos/bot-demo-control.sh stop` immediately stops menu navigation and bot play. The status command does not start a game or bypass the focus checks. Start failures are visible beside B. Clicking B activates the Vision window before acquiring controls. Yes/No navigation follows the matched stencil anchors, including vertically arranged confirmations.

## Overnight bot training in King’s Pass

Each completed episode has a compressed JSON trace in its experiment’s `trajectories` directory. The episode log links to it. It records the applied buttons, Knight position, velocity, ground contact, health, nearest enemy offset and selected policy inputs at each decision. Comparing ground contact on the pit floor with a later landing on its far rim distinguishes an escape from a jump that clears the pit entirely.

The default trainer uses PPO to update the selected policy from game rewards. Its practice starts include the room entrance and situations reached by the selected policy. Every promotion trial gives the policy sole control from a fresh start. The game reports actual enemy damage, kills, remaining deposit hits and collected geo for scoring.

Use `--wait-for-worker` when queuing a run behind another domain or a replay check. An interrupted round can retest its saved proposal from the same parent and training seed. To evaluate weights from a separate training experiment, pass `--rounds 1 --candidate receipt.json`. The receipt identifies the parent directory and its model SHA-256, the proposed model filename and SHA-256, the training seeds and the matching observation environment. These weights enter the normal autonomous comparison and promotion process.

Run `macos/train-hacker-gym.sh` from the checkout with Steam open, the current Vision app installed, and the updated input receiver in the game. The launcher uses the bot environment from `macos/setup-bot-trainer.sh`, keeps the Mac awake while plugged in and defaults to eight hours. Use `--domain gameplay` to train the visual bot or `--domain hacker` for the authored map and mod actors. Use `--baseline-only` for one run of the selected bot or `--rounds 1` for one training comparison. Press Control-C to stop. Running the same command again resumes from the last completed comparison for that checkpoint, build and set of ground labels.

Each episode launches a private headless game at the King's Pass spawn with five masks, damage enabled and saving disabled. The Gym rewards measured forward progress along the platform path in Knight heights. Damage costs five points per mask and death costs ten more. Confirmed enemy damage, kills, deposits and collected Geo have their own rewards. A small decision cost and an inactivity limit discourage stalls. Newly occupied ground cells and rightward distance remain in diagnostic reports.

In both Gameplay and Hacker, keeping Attack down costs 0.05 per decision after the initial press. Releasing Attack makes the next press eligible for a fresh swing. Gym and live B runs count the controls actually applied, including releases when the bot waits for perception. The desktop trainer reads the original X key events. A continued hold trains against repeating Attack while preserving the recorded movement, aim and jump. A fresh press still teaches a swing.

Hacker's native encoder builds its graph from authored ground, walls and frame-matched mod position. Gameplay renders RGB frames and uses the active detector, detected ground, Knight tracking and visual Atlas. Each domain supplies its own path-progress measurement to the Gym, including when its selected policy uses an older observation schema. Hacker Gym freezes the authored ground and Jump connections for each training experiment.

The default trainer runs PPO updates from the selected checkpoint. It compares candidates with the incumbent on an initial trial and separate held-out starts, considering path progress, damage, enemy outcomes and dwell. Successful candidates are exported to the normal version selector. The scoring identity changes with this path reward, so a previous map-coverage experiment starts a separate training history. Older guided experiments remain available explicitly with `--method guided`.

Lifeblood collection is scored from the receiver's cumulative observed blue-mask increases, without adding privileged data to policy inputs. Gym and the B-run score award +1 per gained mask; demonstration export adds +1 per gained mask to outcome points within one second and converts combined points to training weights using the existing `2 ** (points / 32)` rule. The receiver re-baselines on unavailable/dead heroes, scene transitions, scene reloads, player/checkpoint restoration, bench visits, profile changes, and charm changes. Missing telemetry, counter epochs, and recording segment boundaries never create gains. Existing recordings without the signal remain usable without this reward.

### Compact marking policy (schema 13)

New training uses 493 values per observation and four observations (1,972 inputs).
The 100 scalar fields retain Knight/enemy measurements, previous controls,
blockades, Geo/cocoon targets, Atlas/health, timing and route guidance. The old
16-floor list, two-wall list, three screen raster maps and unused Joni fields
are excluded from this policy. Historical checkpoints retain their old contracts.

Geometry uses 64 shared slots across Ground, Wall, Spike and Door. Each slot is
`[start_dx, start_dy, end_dx, end_dy, kind, present]`, with endpoints relative to
the Knight and normalized by frame width; kind is 0.25/0.5/0.75/1 respectively.
There is no per-type allocation: any type may use all 64 slots. Exact duplicate
segments are removed, endpoints are clipped to the capture, and distance to the
nearest point on each segment determines ordering with deterministic tie breaks.
Nine metadata fields report four channel-known flags, four total counts divided
by 64, and the count beyond capacity divided by 64 (count values capped at 4).
A missing historical spike/door channel stays unknown rather than becoming an
empty observation. Current Hacker projection records all four authored kinds.
Older Hacker spikes are recovered only from frozen, hash-verified labels and an
exact archived capture/Unity-frame/camera join. Source recordings remain intact.

The trainer migrates actor and critic columns by feature name, starts new
geometry columns at zero, and learns the compact encoding from the selected
demonstrations. An upgraded critic requires fresh PPO calibration. Historical
raster-only Gym rehearsals are excluded rather than fabricating precise geometry.

### Nearest marking policy (schema 14)

Geometry now retains the 16 closest shared segments across all four types.
Type values are exactly Spike=0.0, Ground=0.33, Wall=0.66, Door=1.0.
Presence distinguishes a real zero-coded spike from an empty slot.
The six fields per slot and nine metadata fields remain: 105 geometry values,
plus the other 100 fields = 205 values/frame, four frames = 820 inputs.
Counts and overflow are divided by 16 and capped at 4. Schema 13 checkpoints
remain readable with their original 64 slots and original type values.
When migrating from schema 13, changed type and overflow weights start at zero;
count weights are rescaled to account for the changed normalization.

PPO scoring version 8 removes the Town arrival completion trigger and the loading
cost cancellation. Town uses the normal decision cost and normal death, stall,
observation-gap and time limits; controls resume whenever perception is usable.
