#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
python=/opt/homebrew/bin/python3.12
environment="$script_dir/.training-venv"

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
