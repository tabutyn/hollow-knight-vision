#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
install_app=${HKV_INSTALL_APP_PATH:-"/Applications/Hollow Knight Vision.app"}
"$script_dir/install-app.sh"
open "$install_app" --args "$@"
