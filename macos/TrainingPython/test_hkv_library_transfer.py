from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from hkv_library_transfer import export_library, import_library


class LibraryTransferTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.mac, self.windows = self.root / "mac", self.root / "windows"
        self.mac.mkdir()
        self.windows.mkdir()

    @staticmethod
    def write(root, name, value):
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value)

    def round_trip(self, source, destination, name):
        bundle = self.root / f"{name}.hkvtraining"
        export_library(source, bundle)
        import_library(destination, bundle, "windows" if destination == self.windows else "mac")
        return bundle

    def test_skipped_versions_and_independent_edits_survive_round_trip(self):
        example = "labeling-v1/examples/first/image.png"
        self.write(self.mac, example, "first")
        self.write(self.mac, "atlas-states-v1/state.json", "atlas")
        self.write(self.mac, "bot-v1/demonstrations/clip/authored-ground.json", "atlas")
        self.write(self.mac, "bot-v1/demonstrations/clip/frames/image.png", "clip")
        self.write(self.mac, "training-v1/runtime-settings.json", "local runtime")
        initial = self.round_trip(self.mac, self.windows, "first")
        self.assertEqual((self.windows / example).stat().st_mtime_ns,
                         (self.mac / example).stat().st_mtime_ns)
        with ZipFile(initial) as archive:
            self.assertEqual(set(archive.namelist()), {example, "bot-v1/demonstrations/clip/frames/image.png",
                                                     "transfer.json"})
        self.write(self.mac, example, "second")
        export_library(self.mac, self.root / "skipped.hkvtraining")
        self.write(self.windows, "labeling-v1/examples/other/image.png", "windows")
        self.round_trip(self.mac, self.windows, "third")
        self.assertEqual((self.windows / example).read_text(), "second")
        self.round_trip(self.windows, self.mac, "fourth")
        self.assertEqual((self.mac / "labeling-v1/examples/other/image.png").read_text(), "windows")
        (self.windows / example).unlink()
        self.round_trip(self.windows, self.mac, "fifth")
        self.assertFalse((self.mac / example).exists())
        self.assertEqual((self.mac / "atlas-states-v1/state.json").read_text(), "atlas")

    def test_conflicts_and_checksum_failures_leave_the_library_unchanged(self):
        example = "labeling-v1/examples/first/manifest.json"
        added = "labeling-v1/examples/later/image.png"
        self.write(self.mac, example, "first")
        self.round_trip(self.mac, self.windows, "first")
        self.write(self.mac, example, "mac")
        self.write(self.windows, example, "windows")
        self.write(self.mac, added, "new screenshot")
        bundle = self.root / "conflict.hkvtraining"
        export_library(self.mac, bundle)
        with self.assertRaisesRegex(ValueError, "changed on both machines"):
            import_library(self.windows, bundle, "windows")
        self.assertEqual((self.windows / example).read_text(), "windows")
        self.assertFalse((self.windows / added).exists())

        corrupt = self.root / "corrupt.hkvtraining"
        with ZipFile(bundle) as original, ZipFile(corrupt, "w") as replacement:
            for item in original.infolist():
                replacement.writestr(item.filename, b"corrupt" if item.filename == added else original.read(item))
        target = self.root / "empty"
        target.mkdir()
        with self.assertRaisesRegex(ValueError, "checksum"):
            import_library(target, corrupt, "windows")
        self.assertFalse((target / "labeling-v1").exists())

    def test_failed_commit_restores_replaced_files(self):
        example = "labeling-v1/examples/first/image.png"
        self.write(self.mac, example, "first")
        self.round_trip(self.mac, self.windows, "first")
        self.write(self.mac, example, "updated")
        bundle = self.root / "later.hkvtraining"
        export_library(self.mac, bundle)
        from os import replace

        def interrupted(source, target):
            if Path(source).name == "new-state.json":
                raise OSError("simulated publication failure")
            replace(source, target)

        with patch("hkv_library_transfer.os.replace", side_effect=interrupted):
            with self.assertRaisesRegex(OSError, "simulated"):
                import_library(self.windows, bundle, "windows")
        self.assertEqual((self.windows / example).read_text(), "first")
        import_library(self.windows, bundle, "windows")
        self.assertEqual((self.windows / example).read_text(), "updated")
