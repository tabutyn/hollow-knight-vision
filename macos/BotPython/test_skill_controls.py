import hashlib
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import numpy as np

from hkv_bot_dataset import HISTORY_LENGTH, SCHEMAS, SKILL_BUTTONS
from bot_policy_inputs import policy_columns, VIEWS
from train_skill_controls import fit


class SkillTrainingTests(unittest.TestCase):
    def test_column_contract_preserves_order_across_history_and_schema_extensions(self):
        for version in (14, 15, 16):
            names = SCHEMAS[version]
            width = len(names)
            newest = width * (HISTORY_LENGTH - 1)
            motor = policy_columns(version, "motor")
            self.assertEqual(motor[-width:], list(range(newest, newest + width)))
            for role in VIEWS:
                columns = policy_columns(version, role)
                self.assertEqual(columns, sorted(set(columns)))
                self.assertLess(len(columns), width * HISTORY_LENGTH)
                self.assertIn(names.index("room_changed"), columns)
                self.assertIn(newest + names.index("previous_attack"), columns)
            self.assertIn(names.index("marking_1_present"), motor)
            self.assertNotIn(names.index("marking_2_present"), motor)
        self.assertEqual(len(policy_columns(14, "motor")), 337)

    def test_fits_native_layers_without_splitting_a_replay(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "practice.npz"
            clips = np.repeat(["first", "second", "third"], 4)
            width = len(SCHEMAS[14]) * HISTORY_LENGTH
            x = np.zeros((len(clips), width), dtype=np.float32)
            columns = policy_columns(14, "traverse")
            x[1::2, columns[-1]] = 1
            actions = np.zeros((len(clips), 4 + len(SKILL_BUTTONS)), dtype=np.int64)
            actions[1::2, 4 + SKILL_BUTTONS.index("dash")] = 1
            np.savez(path, observations=x, actions=actions, session_ids=clips)
            report = dict(schemaVersion=14, skillButtons=list(SKILL_BUTTONS), historyLength=HISTORY_LENGTH,
                          datasetSHA256=hashlib.sha256(path.read_bytes()).hexdigest(),
                          sessions=[dict(id=name, botDomain="gameplay", sourceRun=run) for name, run in
                                    [("first", "train"), ("second", "test"), ("third", "test")]])
            path.with_suffix(".json").write_text(json.dumps(report))
            actor = fit(path, "gameplay", "traverse", ["second"], epochs=2)
            self.assertEqual(actor["labels"], ["none", "dash"])
            self.assertEqual(actor["training"]["validationExamples"], 8)
            self.assertEqual(actor["version"], 2)
            self.assertEqual(actor["inputColumns"], columns)
            self.assertEqual(actor["layers"][0]["inputSize"], len(columns))
            with self.assertRaises(ValueError):
                fit(path, "hacker", "traverse", ["second"])
