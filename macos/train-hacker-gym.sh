#!/bin/zsh
set -euo pipefail
script_dir=${0:A:h}
python=${HKV_BOT_PYTHON:-"$script_dir/.bot-venv/bin/python"}
if [[ ! -x "$python" ]]; then
  print -u2 "Set up the bot environment with macos/setup-bot-trainer.sh."
  exit 69
fi
game_bundle=${HKV_GAME_APP:-}
if [[ -z "$game_bundle" ]]; then
  candidates=("$HOME/Applications/Hollow Knight.app" "/Applications/Hollow Knight.app"
    "$HOME/Library/Application Support/Steam/steamapps/common/Hollow Knight/hollow_knight.app"
    /Volumes/*/Relocated/$USER/Applications/Hollow\ Knight.app(N)
    /Volumes/*/SteamLibrary/steamapps/common/Hollow\ Knight/hollow_knight.app(N))
  for bundle in "${candidates[@]}"; do
    if [[ -d "$bundle" ]]; then game_bundle=$bundle; break; fi
  done
fi
if [[ ! -d "$game_bundle/Contents/MacOS" ]]; then
  print -u2 "Set HKV_GAME_APP to the installed Hollow Knight app bundle."
  exit 69
fi
binary=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$game_bundle/Contents/Info.plist")
exec /usr/bin/caffeinate -i "$python" "$script_dir/BotPython/train_hacker_gym.py" --game "$game_bundle/Contents/MacOS/$binary" "$@"
