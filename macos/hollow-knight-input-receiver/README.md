# Hollow Knight Vision input receiver

This is a removable input and pause receiver for Hollow Knight Vision. It is
not installed by this repository and it never creates macOS keyboard events.
Vision talks to it over `127.0.0.1:36752`; the receiver feeds a custom
InControl `InputDevice` into Hollow Knight.

## Compatibility gate

This source targets Hollow Knight **1.5.12620 / Unity 6000.0.61f1** and the
matching experimental API branch:

```text
https://github.com/SFGrenade/api/tree/hk-beta-branch-unity-6-update
```

Do not use the ordinary stable Hollow Knight Modding API: it targets a
different game build. Install and smoke-test that loader independently first.
The receiver deliberately uses no `On.*`, `IL.*`, or runtime-detour hooks.
That is important on native Apple Silicon, where the experimental branch has
reported detour limitations.

## Protocol

One UTF-8 JSON object per line, maximum 4096 bytes. A client sends full
`state` snapshots. `heldButtons` is a complete bitset: left=1, right=2,
down=4, up=8, A=16, Z=32, X=64, inventory=128, pause menu=256.

```json
{"version":1,"type":"state","sessionID":"4b6f...","sequence":12,"enabled":true,"heldButtons":18}
```

The game returns an acknowledgement only after applying a snapshot in a game
tick:

```json
{"version":1,"type":"ack","sessionID":"4b6f...","sequence":12,"appliedButtons":18,"enabled":true}
```

The server accepts one client. A new connection clears all input. It accepts
only increasing sequence numbers for the active session. Repeated held-state
heartbeats update freshness without entering the 256-transition edge queue;
overflow inserts neutral state before the latest state.
The receiver clears every button after 250 ms without a valid snapshot, on
disconnect, and on malformed/session-changing input.

Protocol v2 adds an explicit `hello` / `capabilitiesAck` exchange. Live
labeling may start only when the receiver advertises `pause-lease-v1`. The
`menu-shortcuts-v1` capability enables Vision's I inventory and P pause-menu
shortcuts. Each
`pause` or `resume` command receives an acknowledgement after Unity's main
thread applies it. A pause lasts at most two seconds without renewal. Resume,
disconnect, lease expiry, mod unload, and game shutdown restore the time scale
that Vision replaced. The independent 250 ms input watchdog remains unchanged.

While loaded, the receiver also keeps Unity's background tick responsive by
temporarily disabling vSync and requesting 60 fps. It restores the player's
previous vSync, target frame rate, background-update, and InControl suspension
settings when the receiver unloads.

The negotiated `player-checkpoint-v1` capability lets a recorded input path
carry its own deterministic gameplay start. Capture is accepted only while the
Knight is grounded and still. The snapshot includes the GameManager room ID,
Knight position/facing/velocity, and camera/target positions. Restore releases
all input first. When the current room differs, the receiver loads the recorded
start room and waits for the Knight and camera before applying the snapshot on
Unity's main thread. The checkpoint also restores first-region visit flags,
visited-scene history, and semi-persistent scene state before loading the room,
so first-entry transitions and cutscenes can fire on every iteration. It
acknowledges only after the exact start state is ready, before Vision's
one-second settle period and replay.

The development receiver also advertises `player-pose-playback-v1`. Vision
records the Knight's scene-scoped world position, velocity, facing, and grounded
state with every ground-truth sample. Replay streams that trace back at capture
cadence; Unity coalesces any backlog and applies the newest pose before the
camera follows. Recorded buttons remain active for animation, attacks, and room
triggers, but no longer determine the replayed route. Late poses are rejected
when their scene does not match the current room.

For repeatable experiments, receiver v0.9.1 exposes an **Invincibility** toggle
in Vision's Ops context. While enabled, it suppresses Knight health damage and
ignores collision pairs between the Knight's terrain body and active enemy or
`DamageHero` colliders. Disable it to restore normal damage and enemy/hazard
collisions while capturing damaged HUD states. Terrain collision and the
Knight's separate attack hitboxes remain enabled in either mode.

## Build

The receiver depends on the exact API build because it references the loader's
patched `Assembly-CSharp.dll` and the game-managed assemblies.

```sh
export HK_MANAGED_DIR="/Volumes/Expansion/Relocated/thomasbutyn/steam/steamapps/common/Hollow Knight/hollow_knight.app/Contents/Resources/Data/Managed"
dotnet build src/HollowKnightVisionInputReceiver.csproj \
  -p:HK_MANAGED_DIR="$HK_MANAGED_DIR" \
  -p:HK_LOADER_DIR=/path/to/matching/api/OutputFinal
```

The resulting `HollowKnightVisionInputReceiver.dll` is only valid after the
matching loader itself passes its own start-to-menu test. Build errors against
`InControl`, `UnityEngine`, or `Modding` mean the installed loader/API revision
does not match this source; do not copy the DLL into the game.

Build the matching loader into an `OutputFinal` directory first, then inspect
its installation without changing the game:

```sh
sh scripts/install_loader.sh --game "$HK_GAME_APP" --loader /path/to/api/OutputFinal
```

## Test before Vision integration

Start Hollow Knight with the compatible loader installed, then from another
terminal run:

```sh
python3 test-client/vision_input_client.py --right --seconds 2
python3 test-client/vision_input_client.py --cycle 100
python3 test-client/vision_input_client.py --pause-seconds 5
python3 test-client/vision_input_client.py --pause-seconds 1 --pause-drop
```

Keep another app selected while testing. The game should move while backgrounded,
stop on release, and never capture or reposition the mouse. `--drop` proves the
250 ms watchdog releases a deliberately abandoned held state. Successful runs
report validated ACK counts and send-to-game-tick p50/p95/max latency.

Before that environment exists, run the dependency-free protocol test:

```sh
mcs -out:/tmp/hkv-protocol-tests.exe -r:"$HK_MANAGED_DIR/netstandard.dll" -r:"$HK_MANAGED_DIR/Newtonsoft.Json.dll" \
  tests/ApiStubs.cs src/Protocol.cs src/LocalInputServer.cs src/VisionInputDevice.cs tests/ProtocolTests.cs
cp "$HK_MANAGED_DIR/Newtonsoft.Json.dll" /tmp/Newtonsoft.Json.dll
mono /tmp/hkv-protocol-tests.exe
```

The public API surface can also be syntax-checked without the game:

```sh
mcs -target:library -out:/tmp/hkv-receiver-check.dll -r:"$HK_MANAGED_DIR/netstandard.dll" -r:"$HK_MANAGED_DIR/Newtonsoft.Json.dll" \
  src/*.cs tests/ApiStubs.cs
```

## Install and restore

Both installers are **dry-run by default**. `install_loader.sh` snapshots every
loader file it would replace and refuses a game version other than 1.5.12620.
`install.sh` copies only this receiver into its own `Mods/HollowKnightVisionInputReceiver`
directory and refuses to run without the loader marker.

```sh
# inspect the exact target and planned copy
sh scripts/install.sh --game "$HK_GAME_APP" --receiver /path/to/HollowKnightVisionInputReceiver.dll

# install the matching loader after its dry run and build have passed
sh scripts/install_loader.sh --install --game "$HK_GAME_APP" --loader /path/to/api/OutputFinal

# make a timestamped backup of only a prior receiver, then install
sh scripts/install.sh --install --game "$HK_GAME_APP" --receiver /path/to/HollowKnightVisionInputReceiver.dll

# restore the latest backed-up receiver, or remove the receiver if none existed
sh scripts/install.sh --restore --game "$HK_GAME_APP"
sh scripts/install_loader.sh --restore --game "$HK_GAME_APP"
```

The script does not install or remove the experimental loader. Save data and
Steam configuration are outside its scope.

## Current limits

The receiver supports Vision's Arrow/A/Z/X controls, I inventory, P pause menu,
and one localhost client. The live game acknowledges applied states, but actual
movement and action behavior must still be checked manually in a playable room
after each game or input-binding update.
Pause must likewise be checked while falling and attacking: animation and
physics stop after `pauseAck`, normal play resumes after `resumeAck`, and a
`--pause-drop` run resumes automatically within two seconds.
