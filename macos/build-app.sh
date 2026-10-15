#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
scratch_dir=${HKV_BUILD_SCRATCH_PATH:-"$script_dir/.build"}
install_after_build=true

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
cp "$script_dir/Sources/HollowKnightVision/Resources/hud-stencil-template.json" \
  "$contents_dir/Resources/hud-stencil-template.json"
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
codesign --force --sign - \
  --identifier "com.ballroller.hollow-knight-vision.trainer" \
  --requirements '=designated => identifier "com.ballroller.hollow-knight-vision.trainer"' \
  "$contents_dir/Helpers/HollowKnightVisionTrainer"
codesign --force --sign - \
  --requirements '=designated => identifier "com.ballroller.hollow-knight-vision"' \
  "$app_dir"

if $install_after_build; then
  "$script_dir/install-app.sh" --no-build
else
  print "Packaged $app_dir"
fi
