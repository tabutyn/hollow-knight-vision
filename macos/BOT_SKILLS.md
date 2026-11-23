# Playing policies for a first ending

The long-term goal is to complete the Atlas, including the progression required to beat the game. With no other objective, explore unmapped territory and measure newly mapped ground or rooms. **Use Atlas goal** clears a previously saved objective.

Press **T** to inspect the latest timed capture, the exact context, the response schema and the latest JSON proposal. Once a minute, the selected Ollama model receives a captured frame, the current goal and the options Vision has prepared for that screen. Atlas observations, OCR, earlier readings and saved quest notes help the model rank those options. In a ready Gameplay or Hacker workspace Vision immediately runs the highest eligible choice from each fresh reply. The inspector displays the result. **Automatic policies** pauses future dispatch, and **B** can stop an active session.

The [Gemma context design](../docs/playing-policies/README.md#how-gemma-uses-the-notebook) traces the playthrough notebook, evidence-backed quest notes and the separate JSON decision request.

[Architecture slides](../docs/playing-policies/playing-policies.pptx) · [Selection diagram](../docs/playing-policies/policy-selection.png) · [Control diagram](../docs/playing-policies/policy-controls.png)

## Initialized policies

These starting points guide the planner. Each proposal supplies its own scores from 1 through 100. At execution the agent checks the capture generation, room or menu, required literal text and receiver capabilities. It picks the highest-scoring eligible choice. Vision discards an expired plan. During ordinary playback an English INSPECT or LISTEN prompt can also start the reading routine.

| Policy | Starting points | First training task | Evidence for scoring the outcome |
| --- | ---: | --- | --- |
| Recover | 95 | Hold Focus in a safe opening with missing health and enough SOUL. Include interrupted and unnecessary heals. | A mask gained, SOUL spent, damage taken, or death. |
| Fight | 85 | Dodge one telegraph, then attack or quick-cast in the opening. Include enemies above and below. | Damage dealt and taken, SOUL spent, and whether the encounter ended. |
| Interact | 80 | Inspect a tablet, read each NPC page, navigate an inventory or pause menu. | A stable new passage saved, a changed page or the expected selected menu entry. |
| Progress | 65 | Choose a reachable prerequisite from a map marker or remembered clue. Revisit the passage after gaining an ability. | A verified pickup, quest flag or newly usable connection. |
| Traverse | 55 | Cross one named obstacle using the available jump, dash, wall jump, pogo or crystal dash. | Landing on the intended platform, elapsed time, damage and stalls. |
| Explore | 40 | Take a reachable unexplored exit, then choose another exit when the first is blocked. | A new room or connection, repeat visits, damage and stalls. |

Vision maps a movement option to its corresponding policy and gives it up to two seconds. With a compatible motor and learned skill head, the motor supplies the base action and the head adds a demonstrated ability. Without the head, the policy runs its deterministic starter. Without the motor, the starter supplies the base action and any compatible head can still add an ability. An ordinary bot session uses the domain motor when available or the Explore starter otherwise. Direct maneuvers own the same input lease and release their buttons on completion, stale capture, interruption or loss of focus. A traversal choice uses existing route and geometry inputs. Reaching a new area needs further decisions and verified route progress. The sign routine explicitly verifies storage.

## Controls and shopping

In modded gameplay the recording controls include arrows, **Z** jump, **X** attack, **A** Focus/cast, **F** quick cast, **C** dash, **E** crystal dash, **D** Dream Nail, **I** inventory and **P** pause. Hold Focus or charge crystal dash. Aim up or down with quick cast to select the corresponding unlocked spell. Jump twice for Monarch Wings and jump against a wall for Mantis Claw. Casting and movement upgrades require the corresponding in-game resources or unlocks.

Vision offers `readPrompt` on a recognized visible INSPECT or LISTEN prompt and `resumeGame` when Continue is selected in the pause menu. Vision binds the exact OCR cue or selected menu identifier, then rechecks it before execution. For an inspected tablet, the reader waits for two matching transcriptions of the new page and writes the joined passage to the local notebook. Thinking can retrieve it in another room after restart.

A shopping wish records an item, desire, maximum price and minimum Geo to keep. The reply accepts wishes and an optional price snapshot. A tested Swift helper can check OCR, price, wallet and reserve, but the production selector does not call it. No current Gemma option buys an item. A future Buy executor would need a bound offer and verification of both inventory and Geo after a purchase.

A minimal example of the policy output follows.

```json
{
  "choices": [
    {"option": "readPrompt", "points": 85},
    {"option": "explore", "points": 30}
  ],
  "shopping": []
}
```

The installed contract accepts only IDs from the current request. Gemma can
return an empty `choices` array. A future destination option needs a route that
Vision can resolve and a target identifier it can verify before execution.

## Training and evaluation

The recorder and Gym encoder supply four chronological frames to movement policies. Interact uses the current screen. A policy selects its own fields by name when it is trained. The exported actor carries the ordered raw column indices, so playback and training read the same positions. Raw clips can be reused across policies. Schema 14 produces these network inputs.

| Policy | Inputs | Information available to the decision |
| --- | ---: | --- |
| Shared motor candidate | 337 | All 205 fields now, plus 44 fields on each older frame covering Knight state, last controls, blockades, two enemy slots, two nearby geometry segments and geometry availability. |
| Explore | 123 | Atlas coverage and routes now, plus Knight, control and blockade history. |
| Traverse | 274 | All current geometry, route and nearby enemies, plus local motion and obstacle history. |
| Fight | 130 | Four enemy slots, health and local geometry now, plus recent Knight state, controls and two enemy slots. |
| Recover | 139 | Health and enemy history, nearby geometry and the current lifeblood cocoon cue. |
| Progress | 153 | Current route, atlas, cache and cocoon cues, plus recent Knight state, controls and enemies. |
| Interact | 6 flags per candidate | In gameplay, in an in-game menu, cue required, literal cue visible, selected item matches, and extended controls available. |

The [shared input contract](Sources/HollowKnightVision/Resources/playing-policy-inputs.json) defines current and historical feature names. Python and Swift resolve them in raw recording order. Interact’s six flags are computed for each proposed control. The reader retains the full text, timestamp, generation, menu selection and passage for freshness checks and verification.

| Policy | Action available before training |
| --- | --- |
| Explore | Follow a route hint, handle a blockade or head toward an Atlas frontier. With no cue or motor, move right. |
| Traverse | Follow the visible route and pulse jump at a blockade. |
| Fight | Approach the nearest observed enemy and pulse the nail within horizontal and vertical range. |
| Recover | Retreat from a close enemy. At two observed masks or fewer, approach a visible Lifeblood cocoon and strike when close. Otherwise hold position. |
| Progress | Approach a visible Geo cache and strike within reach, then prefer an available route when no cache is visible. With no cue or motor, wait. |
| Interact | At INSPECT or LISTEN, tap Up and verify a new passage. Other choices can advance dialogue or operate an eligible menu entry. |

Priority selects a skill. The skill head decides which demonstrated ability to apply alongside the shared motor. A head can only learn choices distinguishable from its observations. In particular the current vector has health but lacks SOUL, ability unlocks, inventory and quest flags. A recovery or progression head needs those observations to reliably learn resource spending or prerequisites. The schema 14–16 ability trainer emits version 2 heads. Earlier full-width heads still load.

The first combined task is to reach the King's Pass tablet, select Inspect when the prompt appears, and recall the passage after reopening the notebook. Score traversal from the ordinary spawn by whether the prompt becomes visible, elapsed decisions, damage and stalls. Score interaction from that prompt by the chosen control, stored text and recalled text. The beside-tablet scenario below tests that second stage independently. Its optional artifacts include the actual `skill-events.jsonl` selection beside the recall result. A completed combined trial requires both stages in the same episode.

To compare the motor view in Gymnasium, run the paired experiment with an existing PPO run directory and the installed modded game executable. Both branches inherit the recorded learner's actor and critic, use fresh optimizers and train on private King's Pass starts. Each receives the same decision budget. The command exports both actors, checks native inference, then evaluates them and the champion on matching seeds. The output directory holds the checkpoints and per-start outcomes.

```sh
macos/.bot-venv/bin/python macos/BotPython/compare_policy_inputs.py \
  --run "$PPO_RUN" --game "$MODDED_GAME_EXECUTABLE" --output ./input-comparison
```

For a runtime under development, use `--stage train` to save both branches, `--stage evaluate` for the matched Gym episodes, and `--stage verify` after installing the native player. Use the same output directory for all three commands. Evaluation can resume completed starts on the same build. On a new build it archives the previous trials and replays the comparison.

1. **Collect expert clips.** In the modded game record with **K**. For each skill collect both successful uses and comparable situations where the player withholds the ability. Keep the raw key edges, chosen direction, screenshots and eventual game outcome. Group clips from the same source run together. Use separate datasets for Gameplay and Hacker. Extended export accepts clips recorded with the new receiver so an unrecorded ability cannot become a false release label.
2. **Fit the ability decisions.** The extended exporter preserves short taps, holds, directional inputs and simultaneous button labels. The trainer fits a 32-unit hidden layer with cross-entropy loss. Each class gets a weight proportional to the inverse square root of its observed frequency, so release frames cannot overwhelm rare casts. The classifier trains on single-ability or release decisions for the chosen skill. It learns when to add Focus, quick cast, dash, crystal dash or Dream Nail to the existing movement actor. Use recovery demonstrations of held Focus when training the shared A key. The interaction policy starts with the tested prompt-and-read routine. Menu and dialogue training needs its own UI-state examples because ordinary gameplay clips require Knight and terrain observations.
3. **Compare on unseen runs.** The trainer splits by whole source replay and writes the held-out confusion matrix, dataset hash, seed and example counts in the candidate JSON. Compare against the initialized policy in paired, save-disabled episodes. Measure the outcomes in the table, including false activations and stalls. A model assigning a large number of points is not evidence of success.
4. **Select a candidate deliberately.** The runtime looks up a learned head by domain, skill and observation schema when a dispatched choice runs a movement policy. To use a tested traversal candidate, place it at `~/Library/Application Support/HollowKnightVision/bot-v1/skill-actors/gameplay/traverse.json`. Keep an experiment output separate while evaluating it. Playback evaluations record the proposed choices, winning points, context, timestamps and end reason in `skill-events.jsonl` beside the actual button events. They can be compared to human ratings and verified outcomes when calibrating planner points. The planner sets decision priorities at runtime. Weight fitting happens offline in the trainer.

From the repository root, export a curated directory of **K** clips, then fit a candidate. Replace the validation clip ID with one of the held-out recordings. Match the observation version to the playing actor.

```sh
macos/.bot-venv/bin/python macos/BotPython/hkv_bot_dataset.py export ./traversal-clips \
  --policy-version 14 --skill-controls --output ./traversal.npz
macos/.bot-venv/bin/python macos/BotPython/train_skill_controls.py ./traversal.npz \
  --domain gameplay --skill traverse --validation-clip "$VALIDATION_CLIP_ID" \
  --output ./candidates/traverse.json
```

The opt-in integration test launches a private, save-disabled King’s Pass instance, places the Knight at a real tablet, lets the player select Inspect from the rendered prompt, and checks the stored passage through a fresh reader. It isolates interacting and remembering from the separate challenge of navigating to the tablet.

```sh
HKV_TABLET_TEST_GAME="$MODDED_GAME_EXECUTABLE" swift test --package-path macos \
  --filter BotSkillTests/testPrivateGameInspectsTabletAndRecallsPassageAfterReopen
```
