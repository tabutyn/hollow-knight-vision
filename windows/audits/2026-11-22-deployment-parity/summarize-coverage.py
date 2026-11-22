#!/usr/bin/env python3
"""Intersect deployed production source changes with executable line counters."""

import argparse
from collections import defaultdict
from itertools import groupby
import json
from pathlib import Path
import re
import subprocess


def git(*args):
    return subprocess.check_output(["git", *args], text=True)


def production_group(path):
    if path.startswith(("macos/Tests/", "windows/tests/")) or "/tests/" in path:
        return None
    name = Path(path).name
    suffix = Path(path).suffix
    if name.startswith(("test_", "test-")):
        return None
    if path.startswith("macos/Sources/") and suffix == ".swift":
        return "macOS Swift"
    if path.startswith("windows/src/") and suffix == ".cs":
        return "Windows C#"
    if path.startswith("macos/hollow-knight-input-receiver/src/") and suffix == ".cs":
        return "Shared receiver C#"
    if path.startswith("macos/BotPython/") and suffix == ".py":
        return "Bot Python"
    if path.startswith("macos/TrainingPython/") and suffix == ".py":
        return "Object trainer Python"
    if path.startswith("macos/") and suffix == ".py":
        return "Diagnostics Python"
    if suffix in {".json", ".xaml", ".sh", ".ps1"}:
        return "Resources and scripts without line counters"
    return None


def changed_lines(base, head):
    changes = {}
    current = None
    for line in git("diff", "--no-ext-diff", "--unified=0", base, head, "--", "macos", "windows").splitlines():
        if line.startswith("diff --git "):
            current = None
        elif line.startswith("+++ b/"):
            path = line[6:]
            current = path if production_group(path) else None
        elif current and (hunk := re.match(r"@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", line)):
            first, count = int(hunk[1]), int(hunk[2] or 1)
            if count:
                changes.setdefault(current, set()).update(range(first, first + count))
    return changes


def repository_path(filename, paths):
    filename = filename.replace("\\", "/").lstrip("./")
    if filename in paths:
        return filename
    for match in re.finditer(r"/(?:macos|windows)/", filename):
        path = filename[match.start() + 1:]
        if path in paths:
            return path
    return None


def swift_lines(segments):
    # LLVM's LCOV export counts a line once, including regions that wrap from
    # an earlier line. Its aggregate JSON summary can count a line more than once.
    result = {}
    if not segments:
        return result
    grouped = {line: list(values) for line, values in groupby(segments, key=lambda s: s[0])}
    wrapped = None
    for line in range(segments[0][0], segments[-1][0] + 1):
        row = grouped.get(line, [])
        entries = [segment for segment in row if segment[3] and segment[4]]
        skipped = row and row[0][4] and not row[0][3]
        if entries or (not skipped and wrapped and wrapped[3]):
            counts = [segment[2] for segment in entries if not segment[5]]
            counts.append(wrapped[2] if wrapped else 0)
            result[line] = max(counts)
        if row:
            wrapped = row[-1]
    return result


def read_lcov(report):
    filename, lines = None, None
    for line in report.read_text().splitlines():
        if line.startswith("SF:"):
            filename, lines = line[3:], {}
        elif line.startswith("DA:") and lines is not None:
            number, hits, *_ = line[3:].split(",")
            lines[int(number)] = int(hits)
        elif line == "end_of_record" and lines is not None:
            yield filename, lines
            filename, lines = None, None


def coverage(covered, total):
    return {"covered": covered, "executable": total, "percent": round(100 * covered / total, 2) if total else None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base")
    parser.add_argument("head")
    parser.add_argument("--swift-json", type=Path)
    parser.add_argument("--python-json", type=Path, action="append", default=[])
    parser.add_argument("--lcov", type=Path, action="append", default=[])
    args = parser.parse_args()
    changes = changed_lines(args.base, args.head)
    counters = {}

    def merge(filename, lines):
        path = repository_path(filename, changes)
        if path is None:
            return
        known = counters.setdefault(path, {})
        for line, hits in lines.items():
            known[line] = max(known.get(line, 0), hits)

    if args.swift_json:
        for entry in json.loads(args.swift_json.read_text())["data"]:
            for file in entry["files"]:
                if repository_path(file["filename"], changes):
                    merge(file["filename"], swift_lines(file["segments"]))
    for report in args.python_json:
        for name, file in json.loads(report.read_text())["files"].items():
            merge(name, dict.fromkeys(file["missing_lines"], 0) | dict.fromkeys(file["executed_lines"], 1))
    for report in args.lcov:
        for filename, lines in read_lcov(report):
            merge(filename, lines)

    groups = defaultdict(lambda: {"files": {}, "unmeasured_files": []})
    for path, lines in sorted(changes.items()):
        group = groups[production_group(path)]
        counts = counters.get(path)
        info = {"added_or_replaced_lines": len(lines), "measured_file": counts is not None}
        if counts is None:
            group["unmeasured_files"].append(path)
        else:
            executable = sorted(lines & counts.keys())
            uncovered = [line for line in executable if counts[line] == 0]
            info["changed_executable_lines"] = coverage(len(executable) - len(uncovered), len(executable))
            info["uncovered_changed_lines"] = uncovered
        group["files"][path] = info

    for group in groups.values():
        measurements = [info["changed_executable_lines"] for info in group["files"].values() if info["measured_file"]]
        group["changed_executable_lines"] = coverage(sum(row["covered"] for row in measurements),
                                                     sum(row["executable"] for row in measurements))
    print(json.dumps({
        "base": git("rev-parse", args.base).strip(),
        "revision": git("rev-parse", args.head).strip(),
        "scope": "Added or replaced production lines, intersected with the supplied revision-matched counters.",
        "groups": dict(groups),
    }, indent=2))


if __name__ == "__main__":
    main()
