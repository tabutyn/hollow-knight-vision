#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
scratch_dir=${HKV_BUILD_SCRATCH_PATH:-"$script_dir/.build"}
swift build -c release --package-path "$script_dir" --scratch-path "$scratch_dir"

app_dir="$script_dir/build/Hollow Knight Vision.app"
contents_dir="$app_dir/Contents"
mkdir -p "$contents_dir/MacOS" "$contents_dir/Resources" "$contents_dir/Helpers"
cp "$scratch_dir/release/HollowKnightVision" "$contents_dir/MacOS/HollowKnightVision"
cp "$scratch_dir/release/HollowKnightVisionTrainer" "$contents_dir/Helpers/HollowKnightVisionTrainer"
cp "$script_dir/Sources/HollowKnightVision/Resources/labeling-contracts.json" \
  "$contents_dir/Resources/labeling-contracts.json"
cp "$script_dir/Sources/HollowKnightVision/Resources/hud-stencil-template.json" \
  "$contents_dir/Resources/hud-stencil-template.json"
cp "$script_dir/Sources/HollowKnightVision/Resources/menu-stencil-positions.json" \
  "$contents_dir/Resources/menu-stencil-positions.json"
rm -rf "$contents_dir/Resources/TrainingPython"
cp -R "$script_dir/TrainingPython" "$contents_dir/Resources/TrainingPython"
cp "$script_dir/Info.plist" "$contents_dir/Info.plist"
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
echo "$app_dir"
