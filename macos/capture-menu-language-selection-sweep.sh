#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
language=${1:-}
advance=${2:-}
typeset -gi failures=0

if [[ -z "$language" ]]; then
  print -u2 "usage: $0 en|es|fr|it|ja|ko|pt-BR|ru|zh-Hans|zh-Hant|de [--advance]"
  exit 64
fi
case "$language" in
  en|es|fr|it|ja|ko|pt-BR|ru|zh-Hans|zh-Hant|de) ;;
  *) print -u2 "unsupported language: $language"; exit 64 ;;
esac
if [[ -n "$advance" && "$advance" != "--advance" ]]; then
  print -u2 "usage: $0 LANGUAGE [--advance]"
  exit 64
fi

press() {
  local attempt
  for attempt in 1 2 3; do
    # Short taps were occasionally missed by Hollow Knight during long
    # sweeps. 250 ms remains below menu key-repeat delay but registers one
    # deterministic navigation step much more reliably.
    if "$script_dir/control-game.sh" "$1" 0.25; then
      sleep "${2:-0.40}"
      return 0
    fi
    sleep 0.5
  done
  return 69
}

press_n() {
  local button=$1
  local count=$2
  local index
  for (( index = 0; index < count; index++ )); do
    press "$button"
  done
}

record() {
  local context=$1
  local selected=$2
  local reply=""
  local attempt
  local recognized=false
  # Full multilingual reacquisition can take several seconds after a scene
  # transition. Keep the guard longer than that slow path before declaring a
  # mismatch and never label a transitional frame as the requested scene.
  for attempt in {1..90}; do
    reply=$("$script_dir/menu-calibration-control.sh" status || true)
    if [[ "$reply" == *'"ok":true'* \
       && "$reply" == *'"contextIdentifier":"'"$context"'"'* \
       && "$reply" == *'"selectedIdentifier":"'"$selected"'"'* ]]; then
      recognized=true
      break
    fi
    sleep 0.15
  done
  if [[ "$reply" != *'"ok":true'* \
     || "$reply" != *'"contextIdentifier":"'"$context"'"'* ]]; then
    print -u2 "context mismatch; aborting before capture expected=$context/$selected actual=$reply"
    return 68
  fi
  if [[ "$recognized" != true ]]; then
    print -u2 "selection mismatch expected=$context/$selected actual=$reply"
    # Never turn an unverified navigation state into permanent calibration.
    # A dropped key and a real stencil miss require visual diagnosis before
    # another input is safe, so stop with the game parked on that frame.
    return 67
  fi
  "$script_dir/menu-calibration-control.sh" capture \
    "$language" "$context" "$selected"
  sleep 0.30
}

step_record() {
  press "$1"
  record "$2" "$3"
}

start_status=$("$script_dir/menu-calibration-control.sh" status)
if [[ "$start_status" != *'"ok":true'* \
   || "$start_status" != *'"contextIdentifier":"game-options"'* \
   || "$start_status" != *'"selectedIdentifier":"game-options.language"'* ]]; then
  print -u2 "sweep must start on Game Options with Language selected: $start_status"
  exit 65
fi

# Game Options and the Options hub.
record game-options game-options.language
step_record down game-options game-options.camera-shake
step_record down game-options game-options.hud-appearance
step_record down game-options game-options.show-achievements
step_record down game-options game-options.backer-credits
step_record down game-options shared.reset-defaults
step_record down game-options shared.back
press_n up 6
press a

record options options.game
step_record down options shared.audio
step_record down options shared.video
step_record down options shared.controller
step_record down options shared.keyboard
step_record down options shared.mods
step_record down options shared.back
press_n up 6

# Audio.
press down
press z 0.8
record audio audio.master-volume
step_record down audio audio.sound-volume
step_record down audio audio.music-volume
step_record down audio shared.reset-defaults
step_record down audio shared.back
press a 0.8

# Video and its three child screens.
press down
press z 0.8
record video video.resolution
step_record down video video.full-screen
step_record down video video.v-sync
step_record down video video.frame-rate-cap
step_record down video video.screen-scale
step_record down video shared.brightness
step_record down video shared.advanced-settings
step_record down video shared.reset-defaults
step_record down video shared.back
press_n up 8

press_n down 4
press z 0.8
record screen-scale screen-scale.scale
step_record down screen-scale shared.back
press a 0.8

press down
press z 0.8
record brightness shared.brightness
step_record down brightness shared.back
press a 0.8

press down
press z 0.8
record video-advanced-settings video-advanced.particle-effects
step_record down video-advanced-settings video-advanced.blur-quality
step_record down video-advanced-settings video-advanced.dithering
step_record down video-advanced-settings video-advanced.film-grain
step_record down video-advanced-settings shared.reset-defaults
step_record down video-advanced-settings shared.back
press a 0.8
press_n up 6
press a 0.8

# Controller, Remap Controller, and Controller Advanced Settings.
press down
press z 0.8
record controller controller.remap-controls
step_record down controller shared.advanced-settings
step_record down controller shared.back
press_n up 2
press z 0.8

record remap-controller remap-controller.done
step_record up remap-controller shared.reset-defaults
step_record up remap-controller shared.focus-cast
step_record up remap-controller shared.dash
step_record up remap-controller shared.attack
step_record up remap-controller shared.jump
step_record left remap-controller shared.quick-map
step_record down remap-controller shared.super-dash
step_record down remap-controller shared.dream-nail
step_record down remap-controller shared.quick-cast
press right
press down
press down
press a 0.8

press down
press z 0.8
record controller-advanced-settings controller-advanced.vibration
step_record down controller-advanced-settings controller-advanced.native-input
step_record down controller-advanced-settings shared.reset-defaults
step_record down controller-advanced-settings shared.back
press a 0.8
press up
press a 0.8

# Keyboard and Mods.
press down
press z 0.8
record keyboard shared.back
step_record up keyboard shared.reset-defaults
step_record up keyboard inventory.inventory
step_record up keyboard shared.focus-cast
step_record up keyboard shared.dash
step_record up keyboard shared.attack
step_record up keyboard shared.jump
step_record up keyboard keyboard.down
step_record up keyboard keyboard.up
step_record left keyboard keyboard.left
step_record down keyboard keyboard.right
step_record down keyboard shared.quick-map
step_record down keyboard shared.super-dash
step_record down keyboard shared.dream-nail
step_record down keyboard shared.quick-cast
press right
press down
press down
press down
press a 0.8

press down
press z 0.8
record mods shared.back
press a 0.8
press_n up 5
press a 1.0

# Main Title, Achievements, Extras, and the four Extras pages.
record main-title shared.options
step_record up main-title main-title.start-game
step_record down main-title shared.options
step_record down main-title shared.achievements
step_record down main-title shared.extras
step_record down main-title quit-game.quit-game
press_n up 3

press down
press z 0.8
record achievements shared.back
press a 0.8

press down
press z 0.8
record extras extras.menu-style
step_record down extras extras.credits
step_record down extras extras.hidden-dreams
step_record down extras extras.the-grimm-troupe
step_record down extras extras.lifeblood
step_record down extras extras.godmaster
step_record down extras shared.back
press_n up 6

press_n down 2
press z 0.8
record hidden-dreams shared.back
press a 0.8
press down
press z 0.8
record the-grimm-troupe shared.back
press a 0.8
press down
press z 0.8
record lifeblood shared.back
press a 0.8
press down
press z 0.8
record godmaster shared.back
press a 0.8
press_n up 5
press a 1.0

# Profiles, all Clear Save buttons, and the Clear Save dialog.
press_n up 3
press z 1.0
record select-profile select-profile.slot-1
step_record right select-profile select-profile.clear-save-1
press z 0.8
record clear-save shared.no
step_record left clear-save shared.yes
press right
press a 0.8
press left

step_record down select-profile select-profile.slot-2
step_record right select-profile select-profile.clear-save-2
press left
step_record down select-profile select-profile.slot-3
step_record right select-profile select-profile.clear-save-3
press left
step_record down select-profile select-profile.slot-4
step_record right select-profile select-profile.clear-save-4
press left
step_record down select-profile shared.back
press_n up 4
press a 1.0

# Quit Game confirmation, then restore the deterministic language position.
press_n down 4
press z 0.8
record quit-game shared.no
step_record up quit-game shared.yes
press down
press a 0.8
press_n up 3
press z 0.8
press z 0.8

if [[ "$advance" == "--advance" ]]; then
  press right 1.2
fi

end_status=$("$script_dir/menu-calibration-control.sh" status)
if [[ "$end_status" != *'"ok":true'* \
   || "$end_status" != *'"contextIdentifier":"game-options"'* \
   || "$end_status" != *'"selectedIdentifier":"game-options.language"'* ]]; then
  print -u2 "sweep did not return to Game Options Language: $end_status"
  exit 66
fi
print "selection-sweep language=$language mismatches=$failures $end_status"
if (( failures > 0 )); then
  exit 67
fi
