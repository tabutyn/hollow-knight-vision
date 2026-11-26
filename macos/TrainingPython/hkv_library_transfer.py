"""Private, versioned transfer of the Vision training library."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from zipfile import BadZipFile, ZIP_STORED, ZipFile


STATE = "training-transfer-v1/latest.json"
MANIFEST = "transfer.json"
SCHEMA = 1
BUFFER = 1024 * 1024
MAX_MANIFEST = 32 * BUFFER
HISTORY_LIMIT = 8
PORTABLE_MODEL = "Detector.portable"
CHECKPOINT = "Detector.pt"
NATIVE_MODELS = {"Detector.onnx", "Detector.mlpackage"}
MODEL_DOCUMENTS = {"training.json", "restore-point.json"}
TREES = (
    "labeling-v1/examples", "training-v1/datasets", "training-v1/runs",
    "models-v1", "bot-v1/demonstrations", "bot-v1/models",
)
FILES = {
    "labeling-v1/menu-stencil-positions.json",
    "training-v1/split-assignments.json",
}
FORBIDDEN = {"authored-ground.json", "path-connections.json", "audit-context.json"}
WINDOWS_DEVICE = re.compile(r"(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", re.IGNORECASE)
SHA = re.compile(r"[a-f0-9]{64}")


def allowed(name: str) -> bool:
    parts = name.split("/")
    if not all(
        part and not part.startswith(".") and not part.endswith((" ", "."))
        and not any(char in '<>:"\\|?*' or ord(char) < 32 for char in part)
        and not WINDOWS_DEVICE.match(part)
        and "atlas" not in part.casefold() and part.casefold() not in FORBIDDEN
        for part in parts
    ):
        return False
    return name in FILES or any(name.startswith(tree + "/") for tree in TREES)


def model_scope(name: str) -> bool:
    return name.startswith(("training-v1/runs/", "models-v1/"))


def document(data) -> bytes:
    return (json.dumps(data, sort_keys=True, separators=(",", ":"), ensure_ascii=False) + "\n").encode()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def json_file(path: Path, payload) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(document(payload))
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def registry_model_names(registry, select) -> bytes:
    for item in registry["objects"].values():
        for point in item["restorePoints"]:
            if point.get("checkpointFilename") == CHECKPOINT:
                point["modelFilename"] = select(point)
    return document(registry)


def model_metadata(payload, native_name: str, model_format: str) -> bytes:
    payload["modelFilename"] = native_name
    if "modelFormat" in payload:
        payload["modelFormat"] = model_format
    return document(payload)


def safe_location(root: Path, name: str) -> Path:
    current = root
    for part in name.split("/"):
        current = current / part
        if current.is_symlink() or getattr(current.lstat() if current.exists() else None,
                                          "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError(f"Transfer encountered a linked path: {name}")
    return current


def snapshot(root: Path) -> dict[str, Path | bytes]:
    result: dict[str, Path | bytes] = {}

    def visit(location: Path, name: str) -> None:
        if not allowed(name):
            return
        safe_location(root, name)
        portable_parent = model_scope(name) and (location.parent / CHECKPOINT).is_file()
        if portable_parent and location.name in NATIVE_MODELS:
            return
        if location.is_dir():
            for child in sorted(location.iterdir()):
                visit(child, f"{name}/{child.name}")
        elif location.is_file():
            if name == "models-v1/registry.json":
                result[name] = registry_model_names(read_json(location), lambda _: PORTABLE_MODEL)
            elif portable_parent and location.name in MODEL_DOCUMENTS:
                if location.name == "training.json" and (location.parent / "restore-point.json").is_file():
                    return  # Windows derives this inference receipt from the portable checkpoint.
                result[name] = model_metadata(read_json(location), PORTABLE_MODEL, "portable")
            else:
                result[name] = location

    for name in TREES:
        location = safe_location(root, name)
        if location.is_dir():
            for child in sorted(location.iterdir()):
                visit(child, f"{name}/{child.name}")
    for name in FILES:
        visit(safe_location(root, name), name)
    validate_names(result)
    return result


def validate_names(names) -> None:
    seen = set()
    directories = {}
    for name in names:
        spelling = name.casefold()
        if not allowed(name) or spelling in seen:
            raise ValueError(f"Unsupported or duplicate bundle entry: {name}")
        seen.add(spelling)
        for prefix in prefixes(name):
            if directories.setdefault(prefix.casefold(), prefix) != prefix:
                raise ValueError(f"Directory names differ only by case: {prefix}")
    if any(prefix in seen for name in seen for prefix in prefixes(name)):
        raise ValueError("The bundle uses a file as a directory.")


def prefixes(name: str):
    while "/" in name:
        name = name.rsplit("/", 1)[0]
        yield name


def transfer_stream(source, destination=None) -> str:
    digest = hashlib.sha256()
    while block := source.read(BUFFER):
        digest.update(block)
        if destination is not None:
            destination.write(block)
    return digest.hexdigest()


def fingerprint(value: Path | bytes) -> str:
    if isinstance(value, bytes):
        return hashlib.sha256(value).hexdigest()
    with value.open("rb") as stream:
        return transfer_stream(stream)


def read_history(root: Path) -> list[dict]:
    path = safe_location(root, STATE)
    return read_json(path)["history"] if path.exists() else []


def remember(*records: dict) -> list[dict]:
    unique = {}
    for record in records:
        unique.setdefault(record["id"], record)
    return list(unique.values())[:HISTORY_LIMIT]


def export_library(root: Path, bundle: Path) -> str:
    if bundle.exists():
        raise ValueError("Choose a new filename for this version of your training library.")
    if bundle.is_relative_to(root):
        raise ValueError("Save the bundle outside the Vision data folder.")
    history = read_history(root)
    version = str(uuid.uuid4())
    contents = snapshot(root)
    files = {}
    modified = {}
    with tempfile.NamedTemporaryFile(dir=bundle.parent, delete=False) as stream:
        staged = Path(stream.name)
    try:
        with ZipFile(staged, "w", compression=ZIP_STORED, allowZip64=True) as archive:
            for name, source in contents.items():
                if isinstance(source, bytes):
                    archive.writestr(name, source)
                    files[name] = fingerprint(source)
                else:
                    with source.open("rb") as data, archive.open(name, "w", force_zip64=True) as entry:
                        before = os.fstat(data.fileno())
                        files[name] = transfer_stream(data, entry)
                        after = os.fstat(data.fileno())
                    if before.st_mtime_ns != after.st_mtime_ns or before.st_size != after.st_size:
                        raise ValueError(f"A file changed during the export: {name}")
                    modified[name] = before.st_mtime_ns
            record = {"id": version, "files": files}
            payload = document({"schemaVersion": SCHEMA, "snapshot": record, "parents": history,
                                "modified": modified,
                                "createdAt": datetime.now(timezone.utc).isoformat()})
            while len(payload) > MAX_MANIFEST and history:
                history.pop()
                payload = document({"schemaVersion": SCHEMA, "snapshot": record, "parents": history,
                                    "modified": modified,
                                    "createdAt": datetime.now(timezone.utc).isoformat()})
            if len(payload) > MAX_MANIFEST:
                raise ValueError("The training library contains too many files for this bundle format.")
            archive.writestr(MANIFEST, payload)
        if bundle.exists():
            raise ValueError("A file appeared at the selected export destination.")
        os.replace(staged, bundle)
        json_file(safe_location(root, STATE), {"history": remember(record, *history)})
    finally:
        staged.unlink(missing_ok=True)
    return f"Exported version {version[:8]} with {len(files)} files."


def verify_record(record: dict) -> None:
    uuid.UUID(record["id"])
    files = record["files"]
    if not isinstance(files, dict) or any(not isinstance(val, str) or not SHA.fullmatch(val)
                                          for val in files.values()):
        raise ValueError("Invalid bundle fingerprints.")
    validate_names(files)


def open_manifest(archive: ZipFile) -> dict:
    names = archive.namelist()
    if len(names) != len(set(names)) or archive.getinfo(MANIFEST).file_size > MAX_MANIFEST:
        raise ValueError("Invalid bundle directory.")
    header = json.loads(archive.read(MANIFEST))
    if header.get("schemaVersion") != SCHEMA:
        raise ValueError("This training bundle requires a different version of Vision.")
    verify_record(header["snapshot"])
    if not isinstance(header["parents"], list) or len(header["parents"]) > HISTORY_LIMIT:
        raise ValueError("Invalid bundle version history.")
    for parent in header["parents"]:
        verify_record(parent)
    if set(names) != set(header["snapshot"]["files"]) | {MANIFEST}:
        raise ValueError("The bundle contents do not match its manifest.")
    if not isinstance(header["modified"], dict) or any(
        name not in header["snapshot"]["files"] or type(value) is not int or not 0 <= value < 2**63
        for name, value in header["modified"].items()
    ):
        raise ValueError("Invalid file timestamps.")
    for entry in archive.infolist():
        kind = stat.S_IFMT(entry.external_attr >> 16)
        if entry.is_dir() or kind not in (0, stat.S_IFREG):
            raise ValueError("The bundle contains an unsupported file type.")
    return header


def changes(base: dict, local: dict, incoming: dict) -> set[str]:
    apply = set()
    conflicts = []
    validate_names(set(base) | set(local) | set(incoming))
    for name in sorted(set(base) | set(incoming)):
        old, here, there = base.get(name), local.get(name), incoming.get(name)
        if there == here or there == old:
            continue
        if here == old:
            apply.add(name)
        else:
            conflicts.append(name)
    if conflicts:
        examples = ", ".join(conflicts[:4])
        raise ValueError(f"{len(conflicts)} files changed on both machines: {examples}. "
                         "Your library was preserved. Use a bundle based on the last shared version.")
    return apply


def stage_payload(archive: ZipFile, files: dict, modified: dict, apply: set[str],
                  stage: Path) -> dict[str, Path | None]:
    pending: dict[str, Path | None] = {name: None for name in apply if name not in files}
    for name, expected in files.items():
        with archive.open(name) as entry:
            if name in apply:
                path = stage / name
                path.parent.mkdir(parents=True, exist_ok=True)
                with path.open("xb") as destination:
                    checksum = transfer_stream(entry, destination)
                if name in modified:
                    os.utime(path, ns=(modified[name], modified[name]))
                pending[name] = path
            else:
                checksum = transfer_stream(entry)
        if checksum != expected:
            raise ValueError(f"Bundle checksum failed for {name}.")
    return pending


def materialize_models(root: Path, stage: Path, pending: dict, merged: dict, platform: str) -> None:
    native_name, model_format = ("Detector.mlpackage", "coreml") if platform == "mac" else ("Detector.onnx", "onnx")
    artifact_by_hash = {}
    for name in sorted(merged):
        if not model_scope(name) or name.rsplit("/", 1)[-1] not in MODEL_DOCUMENTS:
            continue
        receipt = pending.get(name, root / name)
        manifest = read_json(receipt)
        if manifest.get("checkpointFilename") != CHECKPOINT:
            continue
        checkpoint_name = f"{name.rsplit('/', 1)[0]}/{CHECKPOINT}"
        if checkpoint_name not in merged:
            raise ValueError(f"The saved detector checkpoint is missing for {name}.")
        target_name = f"{name.rsplit('/', 1)[0]}/{native_name}"
        target = safe_location(root, target_name)
        inference_receipt = f"{name.rsplit('/', 1)[0]}/training.json"
        wants_receipt = platform == "windows" and name.endswith("/restore-point.json") and (
            checkpoint_name in pending or not (root / inference_receipt).is_file()
        )
        if target_name not in pending and (checkpoint_name in pending or not target.exists() or wants_receipt):
            checkpoint_hash = merged[checkpoint_name]
            artifact = artifact_by_hash.get(checkpoint_hash)
            if artifact is None:
                from hkv_incremental_trainer import export_checkpoint
                cache = stage / "native" / checkpoint_hash
                cache.mkdir(parents=True)
                checkpoint = pending.get(checkpoint_name, root / checkpoint_name)
                artifact = export_checkpoint(checkpoint, cache, model_format)
                artifact_by_hash[checkpoint_hash] = artifact
            source, metadata = artifact
            destination = stage / target_name
            destination.parent.mkdir(parents=True, exist_ok=True)
            if source.is_dir():
                shutil.copytree(source, destination)
            else:
                shutil.copyfile(source, destination)
            pending[target_name] = destination
            if wants_receipt:
                result = stage / inference_receipt
                result.write_bytes(document(metadata))
                pending[inference_receipt] = result
        if manifest.get("modelFilename") != native_name:
            result = stage / name
            result.parent.mkdir(parents=True, exist_ok=True)
            result.write_bytes(model_metadata(manifest, native_name, model_format))
            pending[name] = result
    registry = "models-v1/registry.json"
    if pending.get(registry) is not None:
        saved = pending[registry]
        saved.write_bytes(registry_model_names(read_json(saved), lambda _: native_name))


def recover(root: Path, transaction: Path) -> None:
    journal = transaction / "journal.json"
    if journal.exists():
        for name, had_original in reversed(list(read_json(journal).items())):
            target = safe_location(root, name)
            backup = transaction / "before" / name
            if backup.exists() or not had_original:
                if target.is_dir():
                    shutil.rmtree(target)
                else:
                    target.unlink(missing_ok=True)
            if backup.exists():
                os.replace(backup, target)
    shutil.rmtree(transaction)


def publish(root: Path, transaction: Path, pending: dict, history: list[dict]) -> None:
    state = transaction / "new-state.json"
    state.write_bytes(document({"history": history}))
    pending[STATE] = state
    # Publish models and captures before their selection records.
    ordered = sorted(pending, key=lambda name: name == STATE or name.endswith(("registry.json", "active.json")))
    journal = {}
    for name in ordered:
        target = safe_location(root, name)
        target.parent.mkdir(parents=True, exist_ok=True)
        journal[name] = target.exists()
        (transaction / "before" / name).parent.mkdir(parents=True, exist_ok=True)
    json_file(transaction / "journal.json", journal)
    for name in ordered:
        target = safe_location(root, name)
        if journal[name]:
            os.replace(target, transaction / "before" / name)
        if pending[name] is not None:
            os.replace(pending[name], target)
    (transaction / "journal.json").unlink()


def import_library(root: Path, bundle: Path, platform: str) -> str:
    transaction = safe_location(root, "training-transfer-v1/pending")
    if transaction.exists():
        recover(root, transaction)
    stage = transaction / "incoming"
    stage.mkdir(parents=True)
    try:
        with ZipFile(bundle, "r") as archive:
            header = open_manifest(archive)
            incoming = header["snapshot"]
            local = {name: fingerprint(source) for name, source in snapshot(root).items()}
            history = read_history(root)
            known = {record["id"]: record["files"] for record in history}
            base = next((parent["files"] for parent in [incoming, *header["parents"]]
                         if known.get(parent["id"]) == parent["files"]), {})
            apply = changes(base, local, incoming["files"])
            pending = stage_payload(archive, incoming["files"], header["modified"], apply, stage)
        merged = {name: checksum for name, checksum in local.items() if name not in apply}
        merged.update({name: incoming["files"][name] for name in apply if name in incoming["files"]})
        materialize_models(root, stage, pending, merged, platform)
        publish(root, transaction, pending, remember(incoming, *history, *header["parents"]))
    finally:
        recover(root, transaction)
    return f"Imported version {incoming['id'][:8]} with {len(apply)} file changes."


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("export", "import"))
    parser.add_argument("root", type=Path)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--platform", choices=("mac", "windows"), default="mac" if sys.platform == "darwin" else "windows")
    args = parser.parse_args()
    root, bundle = args.root.resolve(), args.bundle.resolve()
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "BotPython"))
        from bot_training_ipc import exclusive_file_lock
        lock = safe_location(root, "training-transfer-v1/transfer.lock")
        lock.parent.mkdir(parents=True, exist_ok=True)
        with exclusive_file_lock(lock, wait=False):
            transaction = safe_location(root, "training-transfer-v1/pending")
            if transaction.exists():
                recover(root, transaction)
            message = (export_library(root, bundle) if args.operation == "export"
                       else import_library(root, bundle, args.platform))
    except (OSError, ValueError, RuntimeError, KeyError, ImportError, BadZipFile, TypeError, AttributeError) as error:
        print(f"Training library transfer failed. {error}", file=sys.stderr)
        raise SystemExit(1) from None
    print(message)


if __name__ == "__main__":
    main()
