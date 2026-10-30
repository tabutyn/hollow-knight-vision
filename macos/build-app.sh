#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
scratch_dir=${HKV_BUILD_SCRATCH_PATH:-"$script_dir/.build"}
install_after_build=true
default_signing_identity="Apple Development: THOMAS ANTHONY BUTYN (S64U9B558U)"

usage() {
  cat <<'EOF'
Usage: build-app.sh [--package-only]

Builds Hollow Knight Vision and, by default, installs the finished application
at its stable path so macOS and the Dock cannot keep launching an older build.

  --package-only  Leave the signed bundle in build/ without installing it.
EOF
}

while (( $# > 0 )); do
  case "$1" in
    --package-only)
      install_after_build=false
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

swift build -c release --package-path "$script_dir" --scratch-path "$scratch_dir"

signing_identity=${HKV_CODE_SIGN_IDENTITY:-}
if [[ -z "$signing_identity" ]]; then
  available_identities=$(/usr/bin/security find-identity -v -p codesigning 2>/dev/null || true)
  if [[ "$available_identities" == *"\"$default_signing_identity\""* ]]; then
    signing_identity="$default_signing_identity"
  elif $install_after_build; then
    print -u2 "Stable code-signing identity is unavailable: $default_signing_identity"
    print -u2 "Canonical builds require a stable signature so macOS Accessibility approval survives rebuilds."
    exit 69
  else
    signing_identity="-"
    print -u2 "Warning: packaging with an ad-hoc signature; do not install this bundle at the canonical path."
  fi
fi

if $install_after_build && [[ "$signing_identity" == "-" ]]; then
  print -u2 "Canonical builds cannot use an ad-hoc signature."
  print -u2 "Use --package-only for a disposable ad-hoc bundle."
  exit 69
fi

app_dir="$script_dir/build/Hollow Knight Vision.app"
contents_dir="$app_dir/Contents"
mkdir -p "$contents_dir/MacOS" "$contents_dir/Resources" "$contents_dir/Helpers"
build_version=$(git -C "$script_dir" rev-list --count HEAD 2>/dev/null || print 1)
if [[ ! "$build_version" =~ '^[0-9]+$' ]]; then
  build_version=1
fi
# Refresh the icon resource even when artwork changes before the next commit.
icon_digest=$(shasum -a 256 "$script_dir/Resources/HollowKnightVision.icns")
icon_digest=${icon_digest%% *}
icon_filename="HollowKnightVision-$build_version-${icon_digest[1,12]}.icns"
cp "$scratch_dir/release/HollowKnightVision" "$contents_dir/MacOS/HollowKnightVision"
cp "$scratch_dir/release/HollowKnightVisionTrainer" "$contents_dir/Helpers/HollowKnightVisionTrainer"
cp "$script_dir/Sources/HollowKnightVision/Resources/labeling-contracts.json" \
  "$contents_dir/Resources/labeling-contracts.json"
# Remove reference pixels left by older incremental packages.
rm -f "$contents_dir/Resources/hud-stencil-template.json"
cp "$script_dir/Sources/HollowKnightVision/Resources/menu-stencil-positions.json" \
  "$contents_dir/Resources/menu-stencil-positions.json"
cp "$script_dir/Sources/HollowKnightVision/Resources/labeling-contexts.json" \
  "$contents_dir/Resources/labeling-contexts.json"
cp "$script_dir/Sources/HollowKnightVision/Resources/stencil-guidance.json" \
  "$contents_dir/Resources/stencil-guidance.json"
rm -f "$contents_dir/Resources"/HollowKnightVision*.icns(N)
cp "$script_dir/Resources/HollowKnightVision.icns" \
  "$contents_dir/Resources/$icon_filename"
rm -rf "$contents_dir/Resources/TrainingPython"
cp -R "$script_dir/TrainingPython" "$contents_dir/Resources/TrainingPython"
cp "$script_dir/setup-incremental-trainer.sh" "$contents_dir/Resources/setup-incremental-trainer.sh"
cp "$script_dir/Info.plist" "$contents_dir/Info.plist"
plutil -replace CFBundleVersion -string "$build_version" "$contents_dir/Info.plist"
plutil -replace CFBundleIconFile -string "$icon_filename" "$contents_dir/Info.plist"
python3 - "$script_dir" "$contents_dir/Resources/runtime-build-info.json" <<'PY'
import hashlib, json, pathlib, subprocess, sys
root = pathlib.Path(sys.argv[1])
digest = hashlib.sha256()
for path in sorted((root / "Sources").rglob("*.swift")):
    digest.update(str(path.relative_to(root)).encode())
    digest.update(path.read_bytes())
revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
pathlib.Path(sys.argv[2]).write_text(json.dumps({"gitRevision": revision, "swiftSourceSHA256": digest.hexdigest()}))
PY
sign_item() {
  local identifier=$1
  local item=$2
  local signing_arguments=(--force --sign "$signing_identity" --identifier "$identifier")
  if [[ "$signing_identity" == "-" ]]; then
    signing_arguments+=(--requirements "=designated => identifier \"$identifier\"")
  else
    # Local canonical builds do not need a network timestamp. codesign still
    # creates a certificate-anchored designated requirement with a stable team.
    signing_arguments+=(--timestamp=none)
  fi
  /usr/bin/codesign "${signing_arguments[@]}" "$item"
}

sign_item \
  "com.ballroller.hollow-knight-vision.trainer" \
  "$contents_dir/Helpers/HollowKnightVisionTrainer"
sign_item "com.ballroller.hollow-knight-vision" "$app_dir"

if $install_after_build; then
  "$script_dir/install-app.sh" --no-build
else
  print "Packaged $app_dir"
fi
