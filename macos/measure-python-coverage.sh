#!/bin/bash
set -euo pipefail

if [[ $# != 2 ]]; then
  echo "Usage: $0 {diagnostics|bot|object} OUTPUT.json" >&2
  exit 2
fi
cd "$(dirname "$0")/.."
suite=$1
report=$2
python=${HKV_COVERAGE_PYTHON:-python3}
case "$suite" in
  diagnostics) source_dir=macos ;;
  bot) source_dir=macos/BotPython ;;
  object) source_dir=macos/TrainingPython ;;
  *) echo "Unknown test suite: $suite" >&2; exit 2 ;;
esac

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
export COVERAGE_RCFILE="$scratch/coveragerc"
cat > "$COVERAGE_RCFILE" <<EOF
[run]
branch = True
parallel = True
patch = subprocess
data_file = $scratch/.coverage
source = $PWD/$source_dir
[report]
omit =
    */test_*.py
    */test-*.py
EOF

if [[ $suite == diagnostics ]]; then
  for test_file in "$source_dir"/test-*.py; do
    "$python" -m coverage run "$test_file"
  done
else
  "$python" -m coverage run -m unittest discover -s "$source_dir" -p 'test_*.py'
fi

mkdir -p "$(dirname "$report")"
"$python" -m coverage combine "$scratch"
# Name the production files explicitly so modules with no hits still count,
# and nested tools, tests and virtual environments stay outside this suite.
production=()
for file in "$source_dir"/*.py; do
  case ${file##*/} in test_*.py|test-*.py) continue ;; esac
  production+=("$file")
done
"$python" -m coverage json -o "$report" "${production[@]}"
