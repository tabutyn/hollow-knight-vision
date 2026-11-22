"""Inspect or export a bot clip's frame-matched line-detection evidence.

Old recordings without a frozen authored-line snapshot are explicitly unpaired.
An export includes source frames, recorded masking/tuning inputs, controls,
telemetry when available, policy observations, and terrain comparisons.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import uuid


def inspect(clip):
    clip = Path(clip)
    manifest = json.loads((clip / 'manifest.json').read_text())
    identifier = str(uuid.UUID(manifest['id'])).lower()
    replay_id = str(uuid.UUID(manifest.get('replayID') or identifier)).lower()
    context_path = clip / 'audit-context.json'
    context = json.loads(context_path.read_text()) if context_path.exists() else {}
    paired, missing, total = 0, 0, 0
    audit = clip / 'terrain-audit.jsonl'
    if audit.exists():
        for line in audit.read_text().splitlines():
            record = json.loads(line)
            total += 1
            if record.get('referenceAvailable') is True:
                paired += 1
            else:
                missing += 1
    replay_root = Path(context['replayRoot']) if context.get('replayRoot') else (
        clip.parent.parent.parent / 'input-paths-v1' if not context else None)
    replay = None
    archive = clip / ('frames-' + replay_id)
    if replay_root:
        candidates = list(replay_root.glob('path-*-' + replay_id + '.json'))
        if len(candidates) == 1:
            replay = candidates[0]
        if not archive.exists():
            archive = replay_root / ('frames-' + replay_id)
    frame_manifest = archive / 'manifest.json'
    source = json.loads(frame_manifest.read_text()) if frame_manifest.exists() else None
    reference = clip / 'authored-ground.json'
    if reference.exists() and context.get('authoredGroundSHA256'):
        if hashlib.sha256(reference.read_bytes()).hexdigest() != context['authoredGroundSHA256']:
            raise ValueError('Frozen authored-line reference hash mismatch')
    return {'schemaVersion': 1, 'id': identifier, 'replayID': replay_id, 'kind': context.get('kind', 'legacy-demonstration'),
            'domain': context.get('domain', manifest.get('botDomain')),
            'pairedFrames': paired, 'unpairedFrames': missing, 'auditFrames': total,
            'frozenReferenceAvailable': reference.exists(),
            'sourceArchive': str(archive) if source else None,
            'sourceArchiveComplete': source.get('summary', {}).get('complete', False) if source else False,
            'replayPath': str(replay) if replay else None,
            'notes': [] if reference.exists() else ['Legacy recording: no frozen authored-line reference. Do not assume current labels describe the original recording.']}


def export(clip, output):
    clip, output = Path(clip), Path(output)
    report = inspect(clip)
    if output.exists():
        raise ValueError('Choose a new output directory; existing evidence is preserved')
    output.mkdir(parents=True)
    shutil.copytree(clip, output / 'clip')
    archive = Path(report['sourceArchive']) if report['sourceArchive'] else None
    if archive:
        # Never trust an archive path without validating its captured-frame files.
        manifest = json.loads((archive / 'manifest.json').read_text())
        for frame in manifest['frames']:
            name = frame.get('fileName')
            if not name:
                continue
            if Path(name).name != name:
                raise ValueError('Unsafe source frame filename')
            if hashlib.sha256((archive / name).read_bytes()).hexdigest() != frame.get('sha256'):
                raise ValueError('Corrupt source frame: ' + name)
        # Evaluation archives are already inside clip; avoid a second copy.
        if archive.parent.resolve() != clip.resolve():
            shutil.copytree(archive, output / 'source-frames')
    if report['replayPath']:
        shutil.copy2(report['replayPath'], output / 'replay.json')
    (output / 'audit-report.json').write_text(json.dumps(report, indent=2) + '\n')
    (output / 'README.txt').write_text(
        'Compare terrain-audit.jsonl detected and authored arrays only where referenceAvailable is true.\n'
        'Coordinates are bottom-left pixels in the saved source frame. Match frames by captureTimestamp and captureGeneration.\n'
        'Source frame manifests retain SHA-256 hashes, capture identities, dropped-frame accounting, and recorded ground inputs.\n'
        'Replay JSON, when present, supplies manual controls, checkpoint, and mod telemetry.\n'
        'Bot evaluations are not expert demonstrations and must never be used as expert training labels.\n'
        'See audit-report.json for missing or legacy evidence; unavailable references are not empty ground truth.\n')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['inspect', 'export'])
    parser.add_argument('clip', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if args.command == 'export' and not args.output:
        parser.error('export requires --output')
    result = export(args.clip, args.output) if args.command == 'export' else inspect(args.clip)
    print(json.dumps(result, indent=2))
