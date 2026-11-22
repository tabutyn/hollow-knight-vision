#!/bin/zsh
set -euo pipefail
script_dir=${0:A:h}
bot_python=${HKV_BOT_PYTHON:-}
if [[ -z "$bot_python" ]]; then
  for candidate in python3.13 python3.12 python3.11 python3.10; do
    if command -v "$candidate" >/dev/null; then
      bot_python=$(command -v "$candidate")
      break
    fi
  done
fi
if [[ -z "$bot_python" ]]; then
  print -u2 "Install Python 3.10–3.13, or set HKV_BOT_PYTHON to its executable."
  exit 69
fi
"$bot_python" -m venv "$script_dir/.bot-venv"
"$script_dir/.bot-venv/bin/python" -m pip install -r "$script_dir/BotPython/requirements.txt"
"$script_dir/.bot-venv/bin/python" -m pip freeze > "$script_dir/.bot-venv/installed-requirements.txt"
print "Bot training environment ready: $script_dir/.bot-venv/bin/python"
