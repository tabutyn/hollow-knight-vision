#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
source_app="$script_dir/build/Hollow Knight Vision.app"
canonical_app="/Applications/Hollow Knight Vision.app"
install_app=${HKV_INSTALL_APP_PATH:-"$canonical_app"}
build_first=true
repair_dock=false

usage() {
  cat <<'EOF'
Usage: install-app.sh [--no-build] [--repair-dock]

Builds and installs Hollow Knight Vision at its stable application path.
Refreshes and deduplicates Vision Dock entries using macOS preferences.

  --no-build      Install and consume the already packaged application.
  --repair-dock   Also pin the installed app if it is not already pinned.

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
  "$script_dir/build-app.sh" --package-only
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
    && /usr/bin/killall -0 HollowKnightVision 2>/dev/null; then
  /usr/bin/killall -TERM HollowKnightVision
  for _ in {1..50}; do
    if ! /usr/bin/killall -0 HollowKnightVision 2>/dev/null; then
      break
    fi
    sleep 0.1
  done
  if /usr/bin/killall -0 HollowKnightVision 2>/dev/null; then
    print -u2 "Vision is still running; installation stopped before replacing the app."
    exit 75
  fi
fi

if [[ -e "$install_app" ]]; then
  /bin/mv -- "$install_app" "$previous_app"
fi
/bin/mv -- "$stage_app" "$install_app"
/usr/bin/codesign --verify --deep --strict "$install_app"

# ditto preserves the bundle directory's old timestamp. Refresh it so macOS
# invalidates its cached Finder/Dock icon, including while the app is closed.
/usr/bin/touch "$install_app/Contents/Info.plist" "$install_app"
launch_services_register="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
if [[ "$install_app" == "$canonical_app" ]]; then
  # The package in build/ is disposable and must never compete with the stable
  # installed copy when Launch Services resolves this bundle identifier.
  "$launch_services_register" -u "$source_app" 2>/dev/null || true
fi
"$launch_services_register" -f "$install_app"

if [[ -e "$previous_app" ]]; then
  /bin/rm -rf -- "$previous_app"
fi

# Recreate all Vision pins against the new bundle, remove stale recent tiles,
# and refresh the Dock. Failure must fail installation, never silently skip it.
if [[ "$install_app" == "$canonical_app" ]]; then
  dock_arguments=()
  if $repair_dock; then
    dock_arguments+=(--pin)
  fi
  python3 "$script_dir/refresh-dock.py" "${dock_arguments[@]}"
  # A normal build leaves one launchable bundle. --package-only still retains
  # its artifact because it never invokes this installer.
  /bin/rm -rf -- "$source_app"
fi

print "Installed $install_app"
