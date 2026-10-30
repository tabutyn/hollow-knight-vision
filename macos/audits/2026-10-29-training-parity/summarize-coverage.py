#!/usr/bin/env python3
"""Usage: summarize-coverage.py REPOSITORY EVIDENCE_DIRECTORY [BASE=2c62adc]."""
import json
from pathlib import Path
import re
import subprocess
import sys

repo = Path(sys.argv[1]).resolve()
evidence = Path(sys.argv[2]).resolve()
base = sys.argv[3] if len(sys.argv) > 3 else '2c62adc'

def git(*args):
    return subprocess.check_output(['git', '-C', str(repo), *args], text=True).strip()

def relative(filename):
    try:
        return str(Path(filename).relative_to(repo))
    except ValueError:
        return filename

def counts(lines):
    covered = sum(value > 0 for value in lines.values())
    return {'covered': covered, 'count': len(lines),
            'percent': round(covered * 100 / len(lines), 2) if lines else None}

lines = {}
for path in [evidence / 'macos-coverage.info', evidence / 'windows-coverage/coverage.info']:
    current = None
    for line in path.read_text().splitlines():
        if line.startswith('SF:'):
            current = lines.setdefault(relative(line[3:]), {})
        elif line.startswith('DA:') and current is not None:
            number, hits, *_ = line[3:].split(',')
            number = int(number)
            current[number] = current.get(number, 0) + int(hits)

changes = {}
current = None
for line in git('diff', '--unified=0', base, 'HEAD', '--', 'macos/Sources', 'windows/src').splitlines():
    if line.startswith('+++ b/'):
        current = line[6:] if line.endswith(('.swift', '.cs')) else None
    elif current and (match := re.match(r'@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@', line)):
        start, count = int(match[1]), int(match[2] or 1)
        changes.setdefault(current, set()).update(range(start, start + count))

changed = {}
for name, added in changes.items():
    if not added:
        continue
    measured = {number: hits for number, hits in lines.get(name, {}).items() if number in added}
    changed[name] = {'measured': name in lines, 'added_or_modified_lines': len(added),
        'changed_executable_lines': counts(measured),
        'uncovered_changed_executable_lines': [number for number, hits in sorted(measured.items()) if hits == 0]}

prefixes = {'macos_app': 'macos/Sources/HollowKnightVision/',
    'macos_training_runtime': 'macos/Sources/HollowKnightVisionTrainingRuntime/',
    'macos_trainer_launcher': 'macos/Sources/HollowKnightVisionTrainer/',
    'windows_core': 'windows/src/HollowKnightVision.Windows.Core/',
    'windows_app': 'windows/src/HollowKnightVision.Windows.App/'}
groups = {}
for group, prefix in prefixes.items():
    files = {name: counts(values) for name, values in lines.items() if name.startswith(prefix)}
    group_changes = {name: value for name, value in changed.items() if name.startswith(prefix)}
    all_lines = {(name, number): hits for name, values in lines.items() if name.startswith(prefix) for number, hits in values.items()}
    changed_lines = {(name, number): hits for (name, number), hits in all_lines.items() if number in changes.get(name, set())}
    groups[group] = {'unique_executable_line_coverage': counts(all_lines),
        'changed_executable_line_coverage': counts(changed_lines),
        'unmeasured_changed_files': [name for name, value in group_changes.items() if not value['measured']], 'files': files}

llvm_files = json.loads((evidence / 'macos-coverage.json').read_text())['data'][0]['files']
llvm = {}
for group in ['macos_app', 'macos_training_runtime']:
    files = [file for file in llvm_files if relative(file['filename']).startswith(prefixes[group])]
    llvm[group] = {}
    for metric in ['lines', 'functions', 'regions']:
        covered = sum(file['summary'][metric]['covered'] for file in files)
        total = sum(file['summary'][metric]['count'] for file in files)
        llvm[group][metric] = {'covered': covered, 'count': total, 'percent': round(100 * covered / total, 2) if total else None}

mac_log = (evidence / 'macos-full-tests.log').read_text()
cases, skipped, failed = map(int, re.findall(r'Executed (\d+) tests, with (\d+) tests skipped and (\d+) failures', mac_log)[-1])
swift_testing = int(re.findall(r'Test run with (\d+) tests? in', mac_log)[-1])
win_log = (evidence / 'windows-coverage-tests.log').read_text()
win_total, win_failed = map(int, re.findall(r'AUDIT total=(\d+) failures=(\d+)', win_log)[-1])
win_skipped = len(re.findall(r'^SKIP ', win_log, re.M))
result = {'base': git('rev-parse', base), 'source_revision': git('rev-parse', 'HEAD'),
    'scope': 'Windows installed-training fixes in ba47ca9 and their Mac ports after 2c62adc.',
    'macos_llvm': llvm, 'groups': groups, 'changed_files': changed,
    'tests': {'macos_xctest_passed': cases - skipped - failed, 'macos_xctest_skipped': skipped,
        'macos_failures': failed, 'macos_swift_testing_passed': swift_testing,
        'windows_registered': win_total, 'windows_passed': win_total - win_skipped - win_failed,
        'windows_skipped': win_skipped, 'windows_failed': win_failed,
        'windows_failure_names': re.findall(r'^FAIL (.*?): ', win_log, re.M),
        'python_trainer_passed': int(re.findall(r'Ran (\d+) tests', (evidence / 'python-training-tests.log').read_text())[-1]),
        'python_dock_passed': int(re.findall(r'Ran (\d+) tests', (evidence / 'python-dock-tests.log').read_text())[-1]),
        'installed_training_smoke_passed': 'PASS: installed helper' in (evidence / 'installed-training-smoke.log').read_text()},
    'limitations': ['Native Windows WPF and PowerShell installation tests require Windows; cross-build is not runtime verification.',
        'The full Mac CI profile excludes the private screenshot corpus in MenuStencilTests.',
        'Uninstrumented GUI/launcher files and shell/PowerShell scripts are not assumed covered.',
        'Installed-helper training is a separate smoke test; its release executable has no coverage counters.',
        'LLVM line counts and unique LCOV executable lines have different denominators. Metal shader instructions are not measured.']}
(evidence / 'coverage-summary.json').write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps({'tests': result['tests'], 'macos_llvm': llvm,
    'changed_line_coverage': {name: value['changed_executable_line_coverage'] for name, value in groups.items()}}, indent=2))
