import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import uuid

from bot_audit import inspect, export
from train_bot_job import recording_domain


class DomainEvidenceTests(unittest.TestCase):
    def test_domain_follows_terrain_and_refuses_mixed_or_misdeclared_evidence(self):
        self.assertEqual(recording_domain({}, [{'terrainSource': 'human-authored'}]), 'hacker')
        self.assertEqual(recording_domain({}, [{'terrainSource': 'detected'}]), 'gameplay')
        with self.assertRaisesRegex(ValueError, 'mixes'):
            recording_domain({}, [{'terrainSource': 'human-authored'}, {'terrainSource': 'detected'}])
        with self.assertRaisesRegex(ValueError, 'disagrees'):
            recording_domain({'botDomain': 'hacker'}, [{'terrainSource': 'detected'}])

    def test_audit_export_preserves_source_hashes_and_marks_unpaired_frames(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            identifier = str(uuid.uuid4())
            clip = root / 'clip'; clip.mkdir()
            replay_root = root / 'replays'; replay_root.mkdir()
            archive = replay_root / ('frames-' + identifier); archive.mkdir()
            (clip / 'manifest.json').write_text(json.dumps({'id': identifier, 'botDomain': 'gameplay'}))
            raw = b'raw-frame-evidence'; name = '00000000.png'
            (archive / name).write_bytes(raw)
            (archive / 'manifest.json').write_text(json.dumps({'frames': [{'fileName': name,
                'sha256': hashlib.sha256(raw).hexdigest(), 'captureTimestamp': 100, 'captureGeneration': 1}],
                'summary': {'complete': True}}))
            (replay_root / ('path-date-' + identifier + '.json')).write_text(json.dumps({'id': identifier}))
            reference = b'{"lines":[]}'
            (clip / 'authored-ground.json').write_bytes(reference)
            (clip / 'audit-context.json').write_text(json.dumps({'domain': 'gameplay', 'kind': 'human-demonstration',
                'replayRoot': str(replay_root), 'authoredGroundSHA256': hashlib.sha256(reference).hexdigest()}))
            (clip / 'terrain-audit.jsonl').write_text(json.dumps({'referenceAvailable': True}) + '\n'
                                                   + json.dumps({'referenceAvailable': False}) + '\n')
            report = export(clip, root / 'export')
            self.assertEqual(report['pairedFrames'], 1)
            self.assertEqual(report['unpairedFrames'], 1)
            self.assertTrue(report['frozenReferenceAvailable'])
            self.assertEqual((root / 'export/source-frames' / name).read_bytes(), raw)
            self.assertTrue((root / 'export/replay.json').exists())
            (clip / 'authored-ground.json').write_bytes(b'modified')
            with self.assertRaisesRegex(ValueError, 'reference hash'):
                inspect(clip)

    def test_paired_visual_clip_resolves_its_shared_replay(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            clip_id, run_id = str(uuid.uuid4()), str(uuid.uuid4())
            clip = root / ('demo-' + clip_id)
            clip.mkdir()
            (clip / 'manifest.json').write_text(json.dumps({'id': clip_id, 'replayID': run_id, 'botDomain': 'gameplay'}))
            (clip / 'audit-context.json').write_text(json.dumps({'pathID': run_id, 'replayRoot': str(root)}))
            archive = root / ('frames-' + run_id)
            archive.mkdir()
            (archive / 'manifest.json').write_text(json.dumps({'summary': {'complete': True}}))
            replay = root / ('path-date-' + run_id + '.json')
            replay.write_text(json.dumps({'id': run_id}))
            result = inspect(clip)
            self.assertEqual(result['id'], clip_id)
            self.assertEqual(result['sourceArchive'], str(archive))
            self.assertEqual(result['replayPath'], str(replay))

    def test_legacy_recording_never_invents_an_authored_reference(self):
        with tempfile.TemporaryDirectory() as temporary:
            clip = Path(temporary)
            (clip / 'manifest.json').write_text(json.dumps({'id': str(uuid.uuid4())}))
            report = inspect(clip)
            self.assertFalse(report['frozenReferenceAvailable'])
            self.assertEqual(report['pairedFrames'], 0)
            self.assertIn('Legacy recording', report['notes'][0])


if __name__ == '__main__':
    unittest.main()
