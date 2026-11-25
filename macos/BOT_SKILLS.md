# Playing policies for a first ending

The long-term goal is to complete the Atlas, including the progression required to beat the game. The policy request always carries the fixed objective code `expandAtlas`.

Enable **Thinking** at launch (or pass `--enable-thinking`). Press **T** to see why the last screenshot was captured, what Vision offered, and which policy and parameters the model returned. Vision dispatches only that action while the bound target is eligible in Modded Gameplay or Hacker. **Automatic policies** pauses future dispatch. **B** stops active playback. [Current contract and policy inputs](../docs/playing-policies/README.md#decision-contract-and-policy-inputs) · [Earlier v3 slides](../docs/playing-policies/playing-policies.pptx)

## Runtime selectors and controls

Gemma selects Explore, Traverse or Interact. Explore follows the Atlas frontier when available. Traverse takes an offered waypoint. Interact takes an observed Inspect or Listen prompt. Menus use a separate select/more script and fresh selector evidence. Fight, Recover and Progress remain available to the legacy motor training and evaluation code.

## Training and evaluation

The recorder and Gym encoder supply up to four frames of numeric observation history to these motor inputs. The Thinking model receives one event screenshot per decision. Interact uses the current screen. A policy selects its own fields by name when it is trained. The exported actor carries the ordered raw column indices, so playback and training read the same positions. Raw clips can be reused across policies. Schema 14 produces these network inputs.

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

| Legacy skill | Starter action before training |
| --- | --- |
| Explore | Follow a route hint, handle a blockade or head toward an Atlas frontier. With no cue or motor, move right. |
| Traverse | Follow the visible route and pulse jump at a blockade. |
| Fight | Approach the nearest observed enemy and pulse the nail within horizontal and vertical range. |
| Recover | Retreat from a close enemy. At two observed masks or fewer, approach a visible Lifeblood cocoon and strike when close. Otherwise hold position. |
| Progress | Approach a visible Geo cache and strike within reach, then prefer an available route when no cache is visible. With no cue or motor, wait. |
| Interact | At INSPECT or LISTEN, tap Up and verify a new passage. Other choices can advance dialogue or operate an eligible menu entry. |

The table above describes the legacy movement and ability-head training path. Runtime playback uses graph selectors, actor bindings and a dedicated Focus controller. The model selects one offered action. The skill head decides which demonstrated ability to apply alongside the shared motor. A head can only learn choices distinguishable from its observations. The legacy learned vector contains health. The deterministic Focus controller reads live SOUL and red masks separately. Quest flags and unlocks still need a reliable source for learned progression. The schema 14–16 ability trainer emits version 2 heads. Earlier full-width heads still load.

The first combined task is to reach the King's Pass tablet, select Inspect when the prompt appears, and recall the passage after reopening the notebook. Score traversal from the ordinary spawn by whether the prompt becomes visible, elapsed decisions, damage and stalls. Score interaction from that prompt by the chosen control, stored text and recalled text. The beside-tablet scenario below tests that second stage independently. Its optional artifacts include the actual `skill-events.jsonl` selection beside the recall result. A completed combined trial requires both stages in the same episode.

To compare the motor view in Gymnasium, run the paired experiment with an existing PPO run directory and the installed modded game executable. Both branches inherit the recorded learner's actor and critic, use fresh optimizers and train on private King's Pass starts. Each receives the same decision budget. The command exports both actors, checks native inference, then evaluates them and the champion on matching seeds. The output directory holds the checkpoints and per-start outcomes.

```sh
macos/.bot-venv/bin/python macos/BotPython/compare_policy_inputs.py \
  --run "$PPO_RUN" --game "$MODDED_GAME_EXECUTABLE" --output ./input-comparison
```

For a runtime under development, use `--stage train` to save both branches, `--stage evaluate` for the matched Gym episodes, and `--stage verify` after installing the native player. Use the same output directory for all three commands. Evaluation can resume completed starts on the same build. On a new build it archives the previous trials and replays the comparison.

1. **Collect expert clips.** In the modded game record with **K**. For each skill collect both successful uses and comparable situations where the player withholds the ability. Keep the raw key edges, chosen direction, screenshots and eventual game outcome. Group clips from the same source run together. Use separate datasets for Gameplay and Hacker. Extended export accepts clips recorded with the new receiver so an unrecorded ability cannot become a false release label.
2. **Fit the ability decisions.** The extended exporter preserves short taps, holds, directional inputs and simultaneous button labels. The trainer fits a 32-unit hidden layer with cross-entropy loss. Each class gets a weight proportional to the inverse square root of its observed frequency, so release frames cannot overwhelm rare casts. The classifier trains on single-ability or release decisions for the chosen skill. It learns when to add Focus, quick cast, dash, crystal dash or Dream Nail to the existing movement actor. Use recovery demonstrations of held Focus when training the shared A key. The interaction policy starts with the tested prompt-and-read routine. Menu and dialogue training needs its own UI-state examples because ordinary gameplay clips require Knight and terrain observations.
3. **Compare on unseen runs.** The trainer splits by whole source replay and writes the held-out confusion matrix, dataset hash, seed and example counts in the candidate JSON. Compare against the initialized policy in paired, save-disabled episodes. Measure the outcomes in the table, including false activations and stalls.
4. **Select a candidate deliberately.** The runtime looks up a learned head by domain, skill and observation schema when a dispatched choice runs a movement policy. To use a tested traversal candidate, place it at `~/Library/Application Support/HollowKnightVision/bot-v1/skill-actors/gameplay/traverse.json`. Keep an experiment output separate while evaluating it. Playback evaluations record the selected action, context, timestamps and end reason in `skill-events.jsonl` beside the actual button events. Compare the selected actions to human ratings and observed outcomes. Weight fitting happens offline in the trainer.

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
