import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class RuntimeBuildInfoTests(unittest.TestCase):
    def test_linked_build_keeps_primary_trainer_and_uses_its_own_sources(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            original, linked, resources = root / "main checkout", root / "linked checkout", root / "resources"
            subprocess.run(["git", "init", "-q", str(original)], check=True)
            swift = Path("macos/Sources/App.swift")
            (original / swift).parent.mkdir(parents=True)
            (original / swift).write_text("original")
            subprocess.run(["git", "add", str(swift)], cwd=original, check=True)
            subprocess.run(["git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                            "-c", "commit.gpgsign=false", "commit", "-qm", "fixture"], cwd=original, check=True)
            subprocess.run(["git", "worktree", "add", "-q", "--detach", str(linked)], cwd=original, check=True)
            (linked / swift).write_text("linked")
            resources.mkdir()
            script = Path(__file__).with_name("write-runtime-build-info.py")
            primary_python = original / "macos/.bot-venv/bin/python"
            local_python = linked / "macos/.bot-venv/bin/python"

            def bundled_python(checkout):
                subprocess.run([sys.executable, script, checkout / "macos", resources], check=True)
                return Path((resources / "bot-training-environment.txt").read_text().strip())

            self.assertEqual(bundled_python(linked), primary_python)
            local_python.parent.mkdir(parents=True)
            local_python.symlink_to(sys.executable)
            self.assertEqual(bundled_python(linked), local_python)
            primary_python.parent.mkdir(parents=True)
            primary_python.symlink_to(sys.executable)
            self.assertEqual(bundled_python(linked), primary_python)
            revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=linked, text=True).strip()
            provenance = json.loads((resources / "runtime-build-info.json").read_text())
            self.assertEqual(provenance, {"gitRevision": revision,
                                         "swiftSourceSHA256": hashlib.sha256(b"Sources/App.swiftlinked").hexdigest()})
            self.assertEqual(bundled_python(original), primary_python)


if __name__ == "__main__":
    unittest.main()
