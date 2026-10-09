import unittest
from pathlib import Path

import torch

import hkv_incremental_trainer as trainer


class IncrementalTrainerTests(unittest.TestCase):
    def sample(
        self,
        identifier,
        labels=(),
        negative_labels=(),
    ):
        return trainer.Sample(
            example_identifier=identifier,
            image_filename=f"{identifier}.png",
            image_digest=f"digest-{identifier}",
            split="training",
            image_path=Path(f"{identifier}.png"),
            annotations=[
                trainer.Annotation(label, 0.1, 0.1, 0.1, 0.1)
                for label in labels
            ] + [
                trainer.Annotation(label, 0.1, 0.1, 0.1, 0.1, is_negative=True)
                for label in negative_labels
            ],
            known_classes=set(labels) | set(negative_labels),
        )

    def test_legacy_select_decoration_heads_are_removed_from_incremental_runs(self):
        previous_names = {
            "main-title.select-decoration",
            "select-profile.select-decoration",
            "select-profile.back",
            "main-title.options",
            "main-title.achievements",
            "main-title.extras",
            "options.options",
            "options.game",
            "options.audio",
            "options.video",
            "options.controller",
            "options.keyboard",
            "options.mods",
            "quit-to-menu.yes",
            "quit-to-menu.no",
            "pause.quit-to-menu",
            "main-title.quit-game",
            "shared.select-decoration",
            "game.mana",
        }

        retained_names = trainer.retained_previous_class_names({
            "class_names": list(previous_names),
        })

        self.assertEqual(
            retained_names,
            {"shared.select-decoration", "game.mana"},
        )

    def test_dataset_allowlist_retires_old_model_heads(self):
        checkpoint = {
            "class_names": ["game.health", "game.mana", "game.playable-knight"],
        }

        self.assertEqual(
            trainer.training_class_names(
                {"classIdentifiers": ["game.playable-knight"]},
                {"game.playable-knight"},
                checkpoint,
            ),
            ["game.playable-knight"],
        )

    def test_old_dataset_without_allowlist_retains_model_heads(self):
        checkpoint = {"class_names": ["game.mana", "game.playable-knight"]}

        self.assertEqual(
            trainer.training_class_names(
                {}, {"game.playable-knight"}, checkpoint
            ),
            ["game.mana", "game.playable-knight"],
        )

    def test_migrated_class_reuses_legacy_output_head(self):
        old_names = ["main-title.options"]
        old_model = trainer.HKVDetector(len(old_names), pretrained=False)
        checkpoint = {
            "class_names": old_names,
            "state_dict": old_model.state_dict(),
        }

        migrated = trainer.make_model(["shared.options"], checkpoint)

        self.assertTrue(torch.equal(
            migrated.class_head.output.weight,
            old_model.class_head.output.weight,
        ))
        self.assertTrue(torch.equal(
            migrated.class_head.output.bias,
            old_model.class_head.output.bias,
        ))

    def test_every_new_set_identity_has_legacy_head_migration(self):
        self.assertEqual(
            trainer.LEGACY_CLASS_MIGRATIONS["options.game"],
            "game-options.game-options",
        )
        self.assertEqual(
            trainer.LEGACY_CLASS_MIGRATIONS["quit-to-menu.yes"],
            "shared.yes",
        )
        self.assertEqual(
            trainer.LEGACY_CLASS_MIGRATIONS["main-title.achievements"],
            "shared.achievements",
        )

    def test_known_empty_frame_supplies_negative_class_targets(self):
        sample = trainer.Sample(
            example_identifier="negative",
            image_filename="negative.png",
            image_digest="digest",
            split="training",
            image_path=None,
            annotations=[],
            known_classes={"game.mana"},
        )

        heatmap, known, boxes, box_mask, hard_negative_weights = trainer.make_targets(
            [sample], ["game.mana"], 4, 8, torch.device("cpu")
        )

        self.assertEqual(torch.count_nonzero(heatmap).item(), 0)
        self.assertEqual(known[0, 0].item(), 1)
        self.assertEqual(torch.count_nonzero(boxes).item(), 0)
        self.assertEqual(torch.count_nonzero(box_mask).item(), 0)
        self.assertTrue(torch.all(hard_negative_weights == 1))

    def test_explicit_negative_region_upweights_only_target_class_area(self):
        sample = trainer.Sample(
            example_identifier="hard-negative",
            image_filename="hard-negative.png",
            image_digest="digest",
            split="training",
            image_path=None,
            annotations=[trainer.Annotation(
                label="shared.select-decoration",
                x=0.25,
                y=0.25,
                width=0.5,
                height=0.5,
                is_negative=True,
            )],
            known_classes={"shared.select-decoration"},
        )

        heatmap, known, boxes, box_mask, weights = trainer.make_targets(
            [sample], ["shared.select-decoration", "game.mana"],
            4, 8, torch.device("cpu")
        )

        self.assertEqual(torch.count_nonzero(heatmap).item(), 0)
        self.assertEqual(known[0, 0].item(), 1)
        self.assertEqual(known[0, 1].item(), 0)
        self.assertEqual(torch.count_nonzero(boxes).item(), 0)
        self.assertEqual(torch.count_nonzero(box_mask).item(), 0)
        self.assertTrue(torch.all(weights[0, 0, 1:3, 2:6] == trainer.HARD_NEGATIVE_WEIGHT))
        self.assertTrue(torch.all(weights[0, 1] == 1))

    def test_retention_loss_is_zero_for_teacher_and_penalizes_drift(self):
        teacher_classes = torch.zeros((1, 3, 2, 2))
        teacher_classes[:, 1, 0, 0] = 4
        teacher_boxes = torch.zeros((1, 4, 2, 2))

        self.assertEqual(
            trainer.retention_loss(
                teacher_classes.clone(),
                teacher_boxes.clone(),
                teacher_classes,
                teacher_boxes,
                [0, 1],
            ).item(),
            0,
        )
        drifted_classes = teacher_classes.clone()
        drifted_classes[:, 1, 0, 0] = -4
        drifted_boxes = teacher_boxes.clone()
        drifted_boxes[:, 2, :, :] = 1
        self.assertGreater(
            trainer.retention_loss(
                drifted_classes,
                drifted_boxes,
                teacher_classes,
                teacher_boxes,
                [0, 1],
            ).item(),
            0,
        )

    def test_expands_to_one_hundred_classes_without_changing_old_rows(self):
        old_names = [f"object-{index}" for index in range(7)]
        old_model = trainer.HKVDetector(len(old_names), pretrained=False)
        checkpoint = {
            "class_names": old_names,
            "state_dict": old_model.state_dict(),
        }
        expanded_names = old_names + [f"object-{index}" for index in range(7, 100)]

        expanded = trainer.make_model(expanded_names, checkpoint)

        self.assertEqual(expanded.class_head.output.weight.shape[0], 100)
        self.assertTrue(torch.equal(
            expanded.class_head.output.weight[:7],
            old_model.class_head.output.weight,
        ))
        self.assertTrue(torch.equal(
            expanded.class_head.output.bias[:7],
            old_model.class_head.output.bias,
        ))
        self.assertTrue(torch.equal(
            expanded.box_head.output.weight,
            old_model.box_head.output.weight,
        ))

    def test_full_training_set_uses_every_old_and_changed_image(self):
        old = [
            self.sample(f"menu-{index}", ["shared.select-decoration", "shared.back"])
            for index in range(12)
        ] + [
            self.sample(f"title-{index}", ["main-title.start-game"])
            for index in range(3)
        ] + [
            self.sample(f"crawlid-{index}", ["enemies.crawlid"])
            for index in range(3)
        ] + [
            self.sample(f"negative-{index}", negative_labels=["shared.options"])
            for index in range(2)
        ]
        changed = [
            self.sample("new-title", ["main-title.start-game"]),
            self.sample("new-negative", negative_labels=["shared.no"]),
        ]

        selected, new_count, retained_count = trainer.full_training_set(
            old + changed,
            {sample.signature for sample in old},
        )

        self.assertEqual(selected, old + changed)
        self.assertEqual(new_count, 2)
        self.assertEqual(retained_count, len(old))

    def test_full_training_set_does_not_mutate_caller_order(self):
        samples = [
            self.sample(f"sample-{index}", [f"object-{index}"])
            for index in range(5)
        ]
        selected, _, _ = trainer.full_training_set(samples, set())

        self.assertEqual(selected, samples)
        self.assertIsNot(selected, samples)

    def test_cpu_device_is_explicit_and_never_uses_an_accelerator(self):
        self.assertEqual(str(trainer.select_device("cpu")), "cpu")

    def test_windows_export_formats_do_not_require_coremltools(self):
        self.assertEqual(trainer.select_export_format("onnx"), "onnx")
        self.assertEqual(trainer.select_export_format("torchscript"), "torchscript")


if __name__ == "__main__":
    unittest.main()
