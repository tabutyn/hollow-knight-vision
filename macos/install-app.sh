#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
source_app="$script_dir/build/Hollow Knight Vision.app"
canonical_app="/Applications/Hollow Knight Vision.app"
install_app=${HKV_INSTALL_APP_PATH:-"$canonical_app"}
bundle_id="com.ballroller.hollow-knight-vision"
dock_label="Hollow Knight Vision"
build_first=true
repair_dock=false

usage() {
  cat <<'EOF'
Usage: install-app.sh [--no-build] [--repair-dock]

Builds and installs Hollow Knight Vision at its stable application path.

  --no-build      Install the already packaged build/Hollow Knight Vision.app.
  --repair-dock   Replace any pinned copy with the stable installed app.

Set HKV_INSTALL_APP_PATH to exercise the installer outside /Applications.
EOF
}

while (( $# > 0 )); do
  case "$1" in
    --no-build)
      build_first=false
      ;;
    --repair-dock)
      repair_dock=true
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      print -u2 "Unknown argument: $1"
      usage >&2
      exit 64
      ;;
  esac
  shift
done

if [[ "$install_app" != /* || "$install_app" != *.app ]]; then
  print -u2 "HKV_INSTALL_APP_PATH must be an absolute .app path: $install_app"
  exit 64
fi
if $repair_dock && [[ "$install_app" != "$canonical_app" ]]; then
  print -u2 "--repair-dock requires the canonical install path: $canonical_app"
  exit 64
fi

if $build_first; then
  "$script_dir/build-app.sh"
fi

if [[ ! -x "$source_app/Contents/MacOS/HollowKnightVision" ]]; then
  print -u2 "Packaged application is missing: $source_app"
  exit 66
fi

install_parent=${install_app:h}
install_name=${install_app:t}
stage_app="$install_parent/.${install_name}.installing.$$"
previous_app="$install_parent/.${install_name}.previous.$$"

mkdir -p "$install_parent"

cleanup() {
  if [[ -e "$stage_app" ]]; then
    /bin/rm -rf -- "$stage_app"
  fi
  if [[ -e "$previous_app" ]]; then
    if [[ ! -e "$install_app" ]]; then
      /bin/mv -- "$previous_app" "$install_app"
    else
      /bin/rm -rf -- "$previous_app"
    fi
  fi
}
trap cleanup EXIT

/usr/bin/ditto --rsrc --extattr "$source_app" "$stage_app"
/usr/bin/codesign --verify --deep --strict "$stage_app"

# A normal update must not leave an older process owning the Dock icon. SIGTERM
# gives the app its ordinary termination path before the canonical bundle moves.
if [[ "$install_app" == "$canonical_app" ]] \
    && /usr/bin/killall -TERM HollowKnightVision 2>/dev/null; then
  for _ in {1..50}; do
    if ! /usr/bin/killall -0 HollowKnightVision 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
fi

if [[ -e "$install_app" ]]; then
  /bin/mv -- "$install_app" "$previous_app"
fi
/bin/mv -- "$stage_app" "$install_app"
/usr/bin/codesign --verify --deep --strict "$install_app"

launch_services_register="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
"$launch_services_register" -f "$install_app"

if [[ -e "$previous_app" ]]; then
  /bin/rm -rf -- "$previous_app"
fi

if $repair_dock; then
  if ! command -v dockutil >/dev/null 2>&1; then
    print -u2 "dockutil is required only for --repair-dock. The app was installed successfully."
    exit 69
  fi
  dockutil --add "$install_app" --replacing "$dock_label"

  persistent_matches=0
  canonical_matches=0
  while IFS=$'\t' read -r item_label item_url section _plist item_bundle_id; do
    if [[ "$section" == "persistentApps" \
          && ( "$item_label" == "$dock_label" || "$item_bundle_id" == "$bundle_id" ) ]]; then
      (( persistent_matches += 1 ))
      if [[ "$item_url" == "$canonical_app" \
            || "$item_url" == "file:///Applications/Hollow%20Knight%20Vision.app/" ]]; then
        (( canonical_matches += 1 ))
      fi
    fi
  done < <(dockutil --list)
  if (( persistent_matches != 1 || canonical_matches != 1 )); then
    print -u2 "Dock repair did not produce one canonical $dock_label entry."
    exit 70
  fi
fi

print "Installed $install_app"
