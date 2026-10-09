#!/bin/sh
set -eu

mode=dry-run
game=""
receiver=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --install) mode=install ;;
    --restore) mode=restore ;;
    --game) shift; game=${1:?missing game path} ;;
    --receiver) shift; receiver=${1:?missing receiver dll path} ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[ -n "$game" ] || { echo "--game /path/to/hollow_knight.app is required" >&2; exit 2; }
managed="$game/Contents/Resources/Data/Managed"
mods="$managed/Mods"
mod_dir="$mods/HollowKnightVisionInputReceiver"
target="$mod_dir/HollowKnightVisionInputReceiver.dll"
backup_root="$mods/.hkv-input-receiver-backups"
[ -f "$managed/Assembly-CSharp.dll" ] || { echo "Not a Hollow Knight app: $managed/Assembly-CSharp.dll missing" >&2; exit 2; }

if [ "$mode" = dry-run ]; then
  echo "DRY RUN: would install $receiver to $target"
  echo "DRY RUN: requires the matching loader marker at $managed/MMHOOK_Assembly-CSharp.dll"
  exit 0
fi

if [ "$mode" = install ]; then
  [ -f "$managed/MMHOOK_Assembly-CSharp.dll" ] || { echo "Refusing: matching loader marker missing at $managed/MMHOOK_Assembly-CSharp.dll" >&2; exit 1; }
  [ -n "$receiver" ] && [ -f "$receiver" ] || { echo "--receiver existing.dll is required for --install" >&2; exit 2; }
  mkdir -p "$mod_dir" "$backup_root"
  stamp=$(date +%Y%m%d-%H%M%S)
  if [ -f "$target" ]; then
    mkdir -p "$backup_root/$stamp"
    cp "$target" "$backup_root/$stamp/HollowKnightVisionInputReceiver.dll"
    echo "Backed up previous receiver to $backup_root/$stamp"
  fi
  cp "$receiver" "$target"
  echo "Installed receiver only: $target"
  exit 0
fi

if [ "$mode" = restore ]; then
  latest=$(find "$backup_root" -mindepth 2 -maxdepth 2 -name HollowKnightVisionInputReceiver.dll -print 2>/dev/null | sort | tail -n 1 || true)
  if [ -n "$latest" ]; then
    cp "$latest" "$target"
    echo "Restored $latest"
  else
    [ -f "$target" ] && rm "$target"
    echo "Removed receiver; no prior receiver backup existed"
  fi
  exit 0
fi
