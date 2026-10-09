#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
language=${1:-}
advance=${2:-}
capture_count=${HKV_MENU_CAPTURE_COUNT:-3}

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
if (( capture_count < 1 || capture_count > 8 )); then
  print -u2 "HKV_MENU_CAPTURE_COUNT must be between 1 and 8"
  exit 64
fi

press() {
  local attempt
  for attempt in 1 2 3; do
    if "$script_dir/control-game.sh" "$1" 0.1; then
      break
    fi
    if (( attempt == 3 )); then
      return 69
    fi
    sleep 0.5
  done
  sleep "${2:-0.45}"
}

press_n() {
  local button=$1
  local count=$2
  local index
  for (( index = 0; index < count; index++ )); do
    press "$button"
  done
}

capture_scene() {
  local context=$1
  local selected=$2
  local index
  for (( index = 0; index < capture_count; index++ )); do
    "$script_dir/menu-calibration-control.sh" capture \
      "$language" "$context" "$selected"
    sleep 0.65
  done
}

route_status=$($script_dir/menu-calibration-control.sh status)
if [[ "$route_status" != *'"ok":true'* \
   || "$route_status" != *'"contextIdentifier":"game-options"'* \
   || "$route_status" != *'"selectedIdentifier":"game-options.language"'* ]]; then
  print -u2 "route must start on Game Options with Language selected: $route_status"
  exit 65
fi

# Game Options -> Options -> Audio.
capture_scene game-options game-options.language
press a 1.1
capture_scene options options.game
press down
press z 1.1
capture_scene audio audio.master-volume
press a 1.1

# Video starts and ends on Resolution.
press down
press z 1.1
capture_scene video video.resolution
press_n down 4
press z 1.1
capture_scene screen-scale screen-scale.scale
press a 1.1
press down
press z 1.1
capture_scene brightness shared.brightness
press a 1.1
press down
press z 1.1
capture_scene video-advanced-settings video-advanced.particle-effects
press a 1.1
press_n up 6
press a 1.1

# Controller starts and ends on Remap Controls. Keyboard and Mods use Back.
press down
press z 1.1
capture_scene controller controller.remap-controls
press z 1.1
capture_scene remap-controller remap-controller.done
press a 1.1
press down
press z 1.1
capture_scene controller-advanced-settings controller-advanced.vibration
press a 1.1
press up
press a 1.1
press down
press z 1.1
capture_scene keyboard shared.back
press a 1.1
press down
press z 1.1
capture_scene mods shared.back
press a 1.1
press_n up 5
press a 1.5

# Main title, Achievements, and Extras. Extras starts and ends on Menu Style.
capture_scene main-title shared.options
press down
press z 1.5
capture_scene achievements shared.back
press a 1.1
press down
press z 1.5
capture_scene extras extras.menu-style
press_n down 2
press z 1.1
capture_scene hidden-dreams shared.back
press a 1.1
press down
press z 1.1
capture_scene the-grimm-troupe shared.back
press a 1.1
press down
press z 1.1
capture_scene lifeblood shared.back
press a 1.1
press down
press z 1.1
capture_scene godmaster shared.back
press a 1.1
press_n up 5
press a 1.5

# Profile and confirmation dialogs. Both routes retain their safe selections.
press_n up 3
press z 1.5
capture_scene select-profile select-profile.slot-1
press right
press z 1.1
capture_scene clear-save shared.no
press a 1.1
press left
press a 1.5
press_n down 4
press z 1.1
capture_scene quit-game shared.no
press a 1.5

# Restore the deterministic start position for the next language.
press_n up 3
press z 1.1
press z 1.1
if [[ "$advance" == "--advance" ]]; then
  press right 1.5
fi

route_status=$($script_dir/menu-calibration-control.sh status)
if [[ "$route_status" != *'"ok":true'* \
   || "$route_status" != *'"contextIdentifier":"game-options"'* \
   || "$route_status" != *'"selectedIdentifier":"game-options.language"'* ]]; then
  print -u2 "route did not return to Game Options Language: $route_status"
  exit 66
fi
print "$route_status"
