#!/usr/bin/env python3
"""Account for new and modified XCTest methods in the November deployment."""

import argparse
from functools import cache
import json
from pathlib import Path
import re
import subprocess


TEST_ROOT = "macos/Tests"
TEST_DECLARATION_ERE = r"^[[:space:]]*(@[[:alnum:]_]+[[:space:]]+)*func[[:space:]]+test[[:alnum:]_]+[[:space:]]*\("
TEST_DECLARATION = re.compile(r"^([ \t]*)(?:@\w+[ \t]+)*func[ \t]+(test\w+)[ \t]*\(")
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def git(*args):
    return subprocess.check_output(("git", *args), text=True)


@cache
def source(revision, path):
    return git("show", f"{revision}:{path}")


def test_methods(revision):
    output = git("grep", "-n", "-I", "-E", TEST_DECLARATION_ERE, revision, "--", TEST_ROOT)
    methods = {}
    for row in output.splitlines():
        _, path, number, declaration = row.split(":", 3)
        match = TEST_DECLARATION.match(declaration)
        if path.endswith(".swift") and match:
            name = f"{Path(path).stem}.{match[2]}"
            if name in methods:
                raise ValueError(f"Duplicate XCTest method {name} at {revision}")
            methods[name] = (path, int(number), match[1])
    return methods


@cache
def test_lines(revision, path, start, indent):
    lines = source(revision, path).splitlines()
    if lines[start - 1].rstrip().endswith("}"):
        return start, start
    closing = re.compile(rf"^{re.escape(indent)}}}[ \t]*(?://.*)?$")
    end = next((number for number, line in enumerate(lines[start:], start + 1) if closing.match(line)), start)
    return start, end


def intersects(start, count, span):
    return count > 0 and max(start, span[0]) <= min(start + count - 1, span[1])


def modified_tests(base, deployed, earlier, later):
    changed_files = git("diff", "--name-only", "--diff-filter=M", base, deployed, "--", TEST_ROOT).splitlines()
    hunks = {}
    for path in changed_files:
        diff = git("diff", "--no-ext-diff", "--unified=0", base, deployed, "--", path)
        hunks[path] = [tuple(1 if field is None else int(field) for field in match.groups())
                       for line in diff.splitlines() if (match := HUNK.match(line))]
    modified = set()
    for name in earlier.keys() & later.keys():
        old_path, *_ = earlier[name]
        new_path, *_ = later[name]
        if new_path != old_path or new_path not in hunks:
            continue
        before = test_lines(base, *earlier[name])
        after = test_lines(deployed, *later[name])
        for old, removed, new, added in hunks[new_path]:
            if intersects(old, removed, before) or intersects(new, added, after):
                modified.add(name)
                break
    return modified


def validate_reference(revision, reference):
    path, method = reference.split(":", 1)
    if not re.search(r"\b" + re.escape(method) + r"\s*\(", source(revision, path)):
        raise ValueError(f"Missing Windows test {reference} at {revision}")


def classify(cases, current, mappings, dispositions):
    surviving = cases & current.keys()
    return {
        "originalCases": len(cases),
        "sourceCasesRemovedSinceDeployment": sorted(cases - surviving),
        "survivingCases": len(surviving),
        "mapped": len(surviving & mappings.keys()),
        "dispositions": {name: dispositions[name] for name in sorted(surviving & dispositions.keys())},
        "unresolved": sorted(surviving - mappings.keys() - dispositions.keys()),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="b6f647c")
    parser.add_argument("--deployed", default="8d67fa3")
    parser.add_argument("--target", default="HEAD")
    parser.add_argument("--require-accounted", action="store_true")
    args = parser.parse_args()
    base, deployed, target = (git("rev-parse", f"{rev}^{{commit}}").strip()
                              for rev in (args.base, args.deployed, args.target))
    earlier, later, current = (test_methods(rev) for rev in (base, deployed, target))
    introduced = later.keys() - earlier.keys()
    modified = modified_tests(base, deployed, earlier, later)
    metadata = json.loads(source(target, "windows/mac-parity-audit.json"))
    mappings = {}
    for row in metadata["testMappings"]:
        name = row["mac"]
        if name not in current or name in mappings:
            raise ValueError(f"Unknown or duplicate test mapping {name}")
        validate_reference(target, row["windows"])
        mappings[name] = row["windows"]
    dispositions = {}
    for group in metadata.get("unportedTestDispositions", []):
        if not group["reason"] or not group["windowsEvidence"]:
            raise ValueError("Every disposition requires an explanation and test evidence")
        for evidence in group["windowsEvidence"]:
            validate_reference(target, evidence)
        for name in group["macTests"]:
            if name not in current or name in mappings or name in dispositions:
                raise ValueError(f"Unknown, mapped, or duplicate disposition {name}")
            dispositions[name] = {key: group[key] for key in ("reason", "windowsEvidence")}
    report = {
        "base": base, "deployed": deployed, "target": target,
        "introducedTests": classify(introduced, current, mappings, dispositions),
        "modifiedExistingTests": classify(modified, current, mappings, dispositions),
        "scope": "Source-level mapping and explicitly documented omissions. Native execution and line coverage require separate evidence."
    }
    print(json.dumps(report, indent=2))
    if args.require_accounted and any(report[key]["unresolved"]
                                     for key in ("introducedTests", "modifiedExistingTests")):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
