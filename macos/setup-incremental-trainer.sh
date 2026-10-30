#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
python=/opt/homebrew/bin/python3.12
training_root="$HOME/Library/Application Support/HollowKnightVision/training-v1"
environment="$training_root/runtime/.training-venv"
# Reuse an existing development environment without relocating a virtualenv.
if [[ -x "$script_dir/.training-venv/bin/python" ]]; then
  environment="$script_dir/.training-venv"
fi

if [[ ! -x "$python" ]]; then
  echo "Python 3.12 is required at $python" >&2
  exit 1
fi

if [[ ! -x "$environment/bin/python" ]]; then
  "$python" -m venv "$environment"
fi

"$environment/bin/python" -m pip install --upgrade pip
"$environment/bin/python" -m pip install -r "$script_dir/TrainingPython/requirements.txt"
"$environment/bin/python" -c 'from torchvision.models import MobileNet_V3_Small_Weights, mobilenet_v3_small; mobilenet_v3_small(weights=MobileNet_V3_Small_Weights.IMAGENET1K_V1); print("Incremental trainer ready")'
"$environment/bin/python" "$script_dir/TrainingPython/hkv_incremental_trainer.py" --help >/dev/null
# Finder launches do not inherit terminal environment variables or a checkout path.
"$environment/bin/python" - "$training_root" "$environment/bin/python" <<'PY'
import json, os, pathlib, sys, tempfile
root = pathlib.Path(sys.argv[1])
root.mkdir(parents=True, exist_ok=True)
with tempfile.NamedTemporaryFile(mode="w", dir=root, delete=False) as pending:
    json.dump({"pythonPath": str(pathlib.Path(sys.argv[2]).absolute())}, pending)
try:
    os.replace(pending.name, root / "runtime-settings.json")
finally:
    pathlib.Path(pending.name).unlink(missing_ok=True)
PY
