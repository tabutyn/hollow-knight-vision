"""Write build provenance and the local bot trainer location into the Mac bundle."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


def write_build_info(source, resources):
    digest = hashlib.sha256()
    for path in sorted((source / "Sources").rglob("*.swift")):
        digest.update(str(path.relative_to(source)).encode())
        digest.update(path.read_bytes())
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    (resources / "runtime-build-info.json").write_text(json.dumps({
        "gitRevision": revision, "swiftSourceSHA256": digest.hexdigest(),
    }))

    # The first worktree is the primary checkout. Its trainer survives the
    # disposable checkout used to build an installed app.
    listing = subprocess.check_output(["git", "worktree", "list", "--porcelain", "-z"], cwd=source)
    primary = Path(os.fsdecode(listing.split(b"\0", 1)[0][len(b"worktree "):]))
    primary_env = primary / "macos" / ".bot-venv"
    local_env = source / ".bot-venv"
    trainer = primary_env
    if not os.access(primary_env / "bin/python", os.X_OK) and os.access(local_env / "bin/python", os.X_OK):
        trainer = local_env
    # Resolve the environment directory only. Resolving bin/python itself can
    # select the base Python installation outside the virtual environment.
    (resources / "bot-training-environment.txt").write_text(str(trainer.resolve() / "bin/python") + "\n")


if __name__ == "__main__":
    write_build_info(Path(sys.argv[1]), Path(sys.argv[2]))
