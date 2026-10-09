#!/bin/sh
set -eu

mode=dry-run
game=""
loader=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --install) mode=install ;;
    --restore) mode=restore ;;
    --game) shift; game=${1:?missing game path} ;;
    --loader) shift; loader=${1:?missing loader directory} ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[ -n "$game" ] || { echo "--game /path/to/hollow_knight.app is required" >&2; exit 2; }
managed="$game/Contents/Resources/Data/Managed"
backup_root="$managed/.hkv-loader-backups"
[ -f "$managed/Assembly-CSharp.dll" ] || { echo "Not a Hollow Knight app: $managed/Assembly-CSharp.dll missing" >&2; exit 2; }
version=$(plutil -extract CFBundleShortVersionString raw "$game/Contents/Info.plist")
[ "$version" = "1.5.12620" ] || { echo "Refusing: expected Hollow Knight 1.5.12620, found $version" >&2; exit 1; }

if [ "$mode" = dry-run ]; then
  [ -n "$loader" ] || { echo "--loader /path/to/OutputFinal is required" >&2; exit 2; }
  [ -f "$loader/Assembly-CSharp.dll" ] || { echo "Loader output missing Assembly-CSharp.dll" >&2; exit 2; }
  echo "DRY RUN: would back up and replace loader files in $managed"
  find "$loader" -maxdepth 1 -type f \( -name '*.dll' -o -name 'Assembly-CSharp.xml' -o -name 'TeamCherry.Localization.xml' \) -print | sort
  exit 0
fi

if [ "$mode" = install ]; then
  [ -n "$loader" ] || { echo "--loader /path/to/OutputFinal is required" >&2; exit 2; }
  [ -f "$loader/Assembly-CSharp.dll" ] || { echo "Loader output missing Assembly-CSharp.dll" >&2; exit 2; }
  stamp=$(date +%Y%m%d-%H%M%S)
  backup="$backup_root/$stamp"
  mkdir -p "$backup"
  : > "$backup/added-files.txt"
  find "$loader" -maxdepth 1 -type f \( -name '*.dll' -o -name 'Assembly-CSharp.xml' -o -name 'TeamCherry.Localization.xml' \) -print | sort | while IFS= read -r source; do
    name=$(basename "$source")
    if [ -f "$managed/$name" ]; then cp "$managed/$name" "$backup/$name"; else printf '%s\n' "$name" >> "$backup/added-files.txt"; fi
    cp "$source" "$managed/$name"
  done
  echo "Installed matching loader; backup: $backup"
  exit 0
fi

if [ "$mode" = restore ]; then
  latest=$(find "$backup_root" -mindepth 1 -maxdepth 1 -type d -print 2>/dev/null | sort | tail -n 1 || true)
  [ -n "$latest" ] || { echo "No loader backup to restore" >&2; exit 1; }
  find "$latest" -maxdepth 1 -type f ! -name added-files.txt -print | while IFS= read -r source; do cp "$source" "$managed/$(basename "$source")"; done
  if [ -f "$latest/added-files.txt" ]; then while IFS= read -r name; do [ -n "$name" ] && rm -f "$managed/$name"; done < "$latest/added-files.txt"; fi
  echo "Restored loader backup: $latest"
  exit 0
fi
