#!/usr/bin/env python3
"""Checkpointed shared-object trainer for Hollow Knight Vision."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import shutil
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as functional
from PIL import Image
from torchvision.models import MobileNet_V3_Small_Weights, mobilenet_v3_small

try:
    import coremltools as ct
except ImportError:
    ct = None


INPUT_WIDTH = 640
INPUT_HEIGHT = 360
CONFIDENCE_FLOOR = 0.05
REVIEW_CONFIDENCE = 0.5
NMS_OVERLAP = 0.45
MAX_PER_CLASS = 12
MAX_DETECTIONS = 100
INCREMENTAL_LEARNING_RATE = 0.0005
INITIAL_LEARNING_RATE = 0.002
RETENTION_WEIGHT = 4.0
HARD_NEGATIVE_WEIGHT = 8.0
ALGORITHM = "pytorch.mobilenet-v3-small.fpn-heatmap-v1"
LEGACY_CLASS_IDENTIFIERS = {
    "main-title.select-decoration",
    "select-profile.select-decoration",
    "select-profile.back",
    "main-title.options",
    "main-title.achievements",
    "main-title.extras",
    "pause.options",
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
}
LEGACY_CLASS_MIGRATIONS = {
    "main-title.select-decoration": "shared.select-decoration",
    "select-profile.select-decoration": "shared.select-decoration",
    "select-profile.back": "shared.back",
    "main-title.options": "shared.options",
    "main-title.achievements": "shared.achievements",
    "main-title.extras": "shared.extras",
    "pause.options": "shared.options",
    "options.options": "shared.options",
    "options.game": "game-options.game-options",
    "options.audio": "shared.audio",
    "options.video": "shared.video",
    "options.controller": "shared.controller",
    "options.keyboard": "shared.keyboard",
    "options.mods": "shared.mods",
    "quit-to-menu.yes": "shared.yes",
    "quit-to-menu.no": "shared.no",
    "pause.quit-to-menu": "quit-to-menu.quit-to-menu",
    "main-title.quit-game": "quit-game.quit-game",
}


@dataclass(frozen=True)
class Annotation:
    label: str
    x: float
    y: float
    width: float
    height: float
    is_negative: bool = False


@dataclass
class Sample:
    example_identifier: str
    image_filename: str
    image_digest: str
    split: str
    image_path: Path
    annotations: list[Annotation]
    known_classes: set[str]
    tensor: torch.Tensor | None = None

    @property
    def signature(self) -> str:
        evidence = [
            (item.label, item.x, item.y, item.width, item.height, item.is_negative)
            for item in self.annotations
        ]
        encoded = json.dumps(
            [self.image_digest, sorted(self.known_classes), evidence],
            separators=(",", ":"),
            sort_keys=True,
        ).encode()
        return hashlib.sha256(encoded).hexdigest()


class DetectionHead(nn.Module):
    def __init__(self, channels: int, outputs: int, bias: float = 0.0) -> None:
        super().__init__()
        self.body = nn.Sequential(
            nn.Conv2d(channels, channels, 3, padding=1),
            nn.ReLU(inplace=False),
        )
        self.output = nn.Conv2d(channels, outputs, 1)
        nn.init.normal_(self.output.weight, std=0.001)
        nn.init.constant_(self.output.bias, bias)

    def forward(self, value: torch.Tensor) -> torch.Tensor:
        return self.output(self.body(value))


class HKVDetector(nn.Module):
    def __init__(self, class_count: int, pretrained: bool) -> None:
        super().__init__()
        weights = MobileNet_V3_Small_Weights.IMAGENET1K_V1 if pretrained else None
        self.backbone = mobilenet_v3_small(weights=weights).features
        self.lateral8 = nn.Conv2d(24, 64, 1)
        self.lateral16 = nn.Conv2d(48, 64, 1)
        self.lateral32 = nn.Conv2d(576, 64, 1)
        self.smooth = nn.Sequential(
            nn.Conv2d(64, 64, 3, padding=1),
            nn.ReLU(inplace=False),
            nn.Conv2d(64, 64, 3, padding=1),
            nn.ReLU(inplace=False),
        )
        self.class_head = DetectionHead(64, class_count, bias=-4.0)
        self.box_head = DetectionHead(64, 4)
        with torch.no_grad():
            self.box_head.output.bias.copy_(torch.tensor([0.0, 0.0, -2.5, -3.5]))

    def raw(self, image: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        mean = image.new_tensor([0.485, 0.456, 0.406]).view(1, 3, 1, 1)
        deviation = image.new_tensor([0.229, 0.224, 0.225]).view(1, 3, 1, 1)
        value = (image - mean) / deviation
        level8 = value
        level16 = value
        level32 = value
        for index, layer in enumerate(self.backbone):
            value = layer(value)
            if index == 3:
                level8 = value
            elif index == 8:
                level16 = value
            elif index == 12:
                level32 = value
        fused = self.lateral8(level8)
        fused = fused + functional.interpolate(
            self.lateral16(level16), size=level8.shape[-2:], mode="nearest"
        )
        fused = fused + functional.interpolate(
            self.lateral32(level32), size=level8.shape[-2:], mode="nearest"
        )
        fused = self.smooth(fused)
        return self.class_head(fused), self.box_head(fused)

    def forward(self, image: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        class_logits, box_logits = self.raw(image)
        scores = torch.sigmoid(class_logits)
        offsets = torch.sigmoid(box_logits[:, 0:2])
        height = box_logits.shape[2]
        width = box_logits.shape[3]
        y_grid = torch.arange(height, device=box_logits.device, dtype=box_logits.dtype)
        x_grid = torch.arange(width, device=box_logits.device, dtype=box_logits.dtype)
        grid_y, grid_x = torch.meshgrid(y_grid, x_grid, indexing="ij")
        center_x = (grid_x[None, None] + offsets[:, 0:1]) / width
        center_y = (grid_y[None, None] + offsets[:, 1:2]) / height
        box_width = torch.exp(box_logits[:, 2:3]).clamp(0.001, 1.0)
        box_height = torch.exp(box_logits[:, 3:4]).clamp(0.001, 1.0)
        x = torch.clamp(center_x - box_width * 0.5, 0.0, 1.0)
        y = torch.clamp(center_y - box_height * 0.5, 0.0, 1.0)
        box_width = torch.minimum(box_width, 1.0 - x)
        box_height = torch.minimum(box_height, 1.0 - y)
        return scores, torch.cat((x, y, box_width, box_height), dim=1)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", required=True)
    parser.add_argument("--output")
    parser.add_argument("--iterations", type=int, default=100)
    parser.add_argument("--grid-size", type=int, default=13)
    parser.add_argument("--base-checkpoint")
    parser.add_argument("--validate-only", action="store_true")
    parser.add_argument("--device", choices=("auto", "cpu", "mps"), default="auto")
    parser.add_argument(
        "--export-format",
        choices=("auto", "coreml", "onnx", "torchscript"),
        default="auto",
    )
    return parser.parse_args()


def select_device(requested: str) -> torch.device:
    if requested == "cpu":
        return torch.device("cpu")
    mps_available = bool(
        getattr(torch.backends, "mps", None)
        and torch.backends.mps.is_available()
    )
    if requested == "mps":
        if not mps_available:
            raise ValueError("MPS training was requested but is unavailable.")
        return torch.device("mps")
    if requested != "auto":
        raise ValueError(f"Unsupported training device: {requested}")
    return torch.device("mps" if mps_available else "cpu")


def select_export_format(requested: str) -> str:
    if requested != "auto":
        if requested == "coreml" and ct is None:
            raise ValueError("Core ML export requires coremltools.")
        return requested
    if sys.platform == "darwin" and ct is not None:
        return "coreml"
    return "onnx"


def load_dataset(root: Path) -> tuple[dict, list[Sample]]:
    manifest = json.loads((root / "dataset.json").read_text())
    annotation_maps: dict[str, dict[str, list[dict]]] = {}
    for split in ("training", "validation"):
        path = root / split / "annotations.json"
        values = json.loads(path.read_text()) if path.exists() else []
        annotation_maps[split] = {value["image"]: value["annotations"] for value in values}

    samples: list[Sample] = []
    for item in manifest["items"]:
        split = item["split"]
        source = annotation_maps[split].get(item["imageFilename"], [])
        annotations = []
        with Image.open(root / split / item["imageFilename"]) as image:
            source_width, source_height = image.size
        for value in source:
            coordinates = value["coordinates"]
            annotations.append(Annotation(
                label=value["label"],
                x=(coordinates["x"] - coordinates["width"] * 0.5) / source_width,
                y=(coordinates["y"] - coordinates["height"] * 0.5) / source_height,
                width=coordinates["width"] / source_width,
                height=coordinates["height"] / source_height,
                is_negative=bool(value.get("isNegative", False)),
            ))
        known = set(item.get("knownClassIdentifiers") or [x.label for x in annotations])
        samples.append(Sample(
            example_identifier=item["exampleIdentifier"],
            image_filename=item["imageFilename"],
            image_digest=item["imageDigest"],
            split=split,
            image_path=root / split / item["imageFilename"],
            annotations=annotations,
            known_classes=known,
        ))
    if not any(sample.split == "training" and sample.annotations for sample in samples):
        raise ValueError("No training annotations.")
    return manifest, samples


def load_image(sample: Sample) -> torch.Tensor:
    if sample.tensor is None:
        with Image.open(sample.image_path) as source:
            image = source.convert("RGB").resize((INPUT_WIDTH, INPUT_HEIGHT), Image.Resampling.BILINEAR)
            array = np.array(image, dtype=np.float32, copy=True) / 255.0
        sample.tensor = torch.from_numpy(array).permute(2, 0, 1).contiguous()
    return sample.tensor


def augment(image: torch.Tensor) -> torch.Tensor:
    contrast = random.uniform(0.90, 1.10)
    brightness = random.uniform(-0.04, 0.04)
    return torch.clamp((image - 0.5) * contrast + 0.5 + brightness, 0.0, 1.0)


def make_targets(
    samples: list[Sample], class_names: list[str], height: int, width: int, device: torch.device
) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor, torch.Tensor]:
    class_index = {name: index for index, name in enumerate(class_names)}
    heatmap = torch.zeros((len(samples), len(class_names), height, width), device=device)
    known = torch.zeros((len(samples), len(class_names), 1, 1), device=device)
    boxes = torch.zeros((len(samples), 4, height, width), device=device)
    box_mask = torch.zeros((len(samples), 1, height, width), device=device)
    hard_negative_weights = torch.ones(
        (len(samples), len(class_names), height, width), device=device
    )

    for batch_index, sample in enumerate(samples):
        for name in sample.known_classes:
            if name in class_index:
                known[batch_index, class_index[name]] = 1
        for annotation in sample.annotations:
            if annotation.label not in class_index:
                continue
            if annotation.is_negative:
                class_id = class_index[annotation.label]
                known[batch_index, class_id] = 1
                left = min(width - 1, max(0, int(math.floor(annotation.x * width))))
                top = min(height - 1, max(0, int(math.floor(annotation.y * height))))
                right = min(width, max(left + 1, int(math.ceil(
                    (annotation.x + annotation.width) * width
                ))))
                bottom = min(height, max(top + 1, int(math.ceil(
                    (annotation.y + annotation.height) * height
                ))))
                hard_negative_weights[batch_index, class_id, top:bottom, left:right] = (
                    HARD_NEGATIVE_WEIGHT
                )
                continue
            center_x = annotation.x + annotation.width * 0.5
            center_y = annotation.y + annotation.height * 0.5
            cell_x = min(width - 1, max(0, int(center_x * width)))
            cell_y = min(height - 1, max(0, int(center_y * height)))
            fractional_x = center_x * width - cell_x
            fractional_y = center_y * height - cell_y
            radius = max(1, min(4, int(min(annotation.width * width, annotation.height * height) / 2)))
            target = heatmap[batch_index, class_index[annotation.label]]
            for offset_y in range(-radius, radius + 1):
                for offset_x in range(-radius, radius + 1):
                    x = cell_x + offset_x
                    y = cell_y + offset_y
                    if 0 <= x < width and 0 <= y < height:
                        value = math.exp(
                            -(offset_x * offset_x + offset_y * offset_y)
                            / (2 * (radius / 2 + 0.5) ** 2)
                        )
                        target[y, x] = max(float(target[y, x]), value)
            target[cell_y, cell_x] = 1
            boxes[batch_index, :, cell_y, cell_x] = torch.tensor(
                [fractional_x, fractional_y, annotation.width, annotation.height], device=device
            )
            box_mask[batch_index, 0, cell_y, cell_x] = 1
    return heatmap, known, boxes, box_mask, hard_negative_weights


def detection_loss(
    class_logits: torch.Tensor,
    box_logits: torch.Tensor,
    heatmap: torch.Tensor,
    known: torch.Tensor,
    box_targets: torch.Tensor,
    box_mask: torch.Tensor,
    hard_negative_weights: torch.Tensor,
) -> torch.Tensor:
    probabilities = torch.sigmoid(class_logits)
    positives = (heatmap == 1).float() * known
    negatives = (heatmap < 1).float() * known
    negative_weights = torch.pow(1 - heatmap, 4) * hard_negative_weights
    positive_loss = (
        -functional.logsigmoid(class_logits) * torch.pow(1 - probabilities, 2) * positives
    )
    negative_loss = (
        -functional.logsigmoid(-class_logits)
        * torch.pow(probabilities, 2)
        * negative_weights
        * negatives
    )
    heatmap_loss = (positive_loss.sum() + negative_loss.sum()) / positives.sum().clamp(min=1)

    predicted_offsets = torch.sigmoid(box_logits[:, 0:2])
    offset_mask = box_mask.expand(-1, 2, -1, -1)
    offset_loss = functional.smooth_l1_loss(
        predicted_offsets * offset_mask,
        box_targets[:, 0:2] * offset_mask,
        reduction="sum",
    ) / box_mask.sum().clamp(min=1)
    size_mask = box_mask.expand(-1, 2, -1, -1)
    log_size_targets = torch.log(box_targets[:, 2:4].clamp(min=0.001))
    size_loss = functional.l1_loss(
        box_logits[:, 2:4] * size_mask,
        log_size_targets * size_mask,
        reduction="sum",
    ) / box_mask.sum().clamp(min=1)
    return heatmap_loss + offset_loss * 5 + size_loss * 2


def retention_loss(
    student_class_logits: torch.Tensor,
    student_box_logits: torch.Tensor,
    teacher_class_logits: torch.Tensor,
    teacher_box_logits: torch.Tensor,
    retained_class_indices: list[int],
) -> torch.Tensor:
    if not retained_class_indices:
        return student_class_logits.sum() * 0
    indices = torch.tensor(
        retained_class_indices,
        dtype=torch.long,
        device=student_class_logits.device,
    )
    student_classes = torch.index_select(student_class_logits, 1, indices)
    teacher_classes = torch.index_select(teacher_class_logits, 1, indices)
    # Preserve old peaks more strongly while also retaining calibrated negative
    # evidence across the map. Boxes are class-agnostic, so replay constrains the
    # complete geometry field rather than only current annotation centers.
    class_weights = 1 + torch.sigmoid(teacher_classes) * 8
    class_difference = functional.smooth_l1_loss(
        student_classes,
        teacher_classes,
        reduction="none",
    )
    class_loss = (class_difference * class_weights).sum() / class_weights.sum()
    box_loss = functional.smooth_l1_loss(
        student_box_logits,
        teacher_box_logits,
    )
    return class_loss + box_loss


def make_model(class_names: list[str], base_checkpoint: dict | None) -> HKVDetector:
    model = HKVDetector(len(class_names), pretrained=base_checkpoint is None)
    if base_checkpoint is None:
        return model
    previous_names = base_checkpoint["class_names"]
    previous_state = base_checkpoint["state_dict"]
    state = model.state_dict()
    for name, value in previous_state.items():
        if name.startswith("class_head.output."):
            continue
        if name in state and state[name].shape == value.shape:
            state[name] = value
    old_index = {name: index for index, name in enumerate(previous_names)}
    for new_index, name in enumerate(class_names):
        source_index = old_index.get(name)
        if source_index is None:
            source_index = next((
                index
                for index, previous_name in enumerate(previous_names)
                if LEGACY_CLASS_MIGRATIONS.get(previous_name) == name
            ), None)
        if source_index is None:
            continue
        state["class_head.output.weight"][new_index] = previous_state[
            "class_head.output.weight"
        ][source_index]
        state["class_head.output.bias"][new_index] = previous_state[
            "class_head.output.bias"
        ][source_index]
    model.load_state_dict(state)
    return model


def retained_previous_class_names(base_checkpoint: dict | None) -> set[str]:
    if base_checkpoint is None:
        return set()
    return set(base_checkpoint["class_names"]) - LEGACY_CLASS_IDENTIFIERS


def training_class_names(
    manifest: dict,
    annotated_names: set[str],
    base_checkpoint: dict | None,
) -> list[str]:
    """Honor an exported model allowlist while retaining old-manifest behavior."""
    configured = manifest.get("classIdentifiers")
    if configured is None:
        return sorted(annotated_names | retained_previous_class_names(base_checkpoint))
    configured_names = set(configured)
    if not configured_names:
        raise ValueError("Dataset classIdentifiers is empty.")
    unexpected = annotated_names - configured_names
    if unexpected:
        raise ValueError(
            "Dataset annotations are outside classIdentifiers: "
            + ", ".join(sorted(unexpected))
        )
    return sorted(configured_names)


def full_training_set(
    training: list[Sample], previous_signatures: set[str]
) -> tuple[list[Sample], int, int]:
    """Use every training image while retaining change counts for diagnostics."""
    changed_count = sum(
        sample.signature not in previous_signatures for sample in training
    )
    retained_count = len(training) - changed_count
    return list(training), changed_count, retained_count


def train_model(
    model: HKVDetector,
    samples: list[Sample],
    class_names: list[str],
    epochs: int,
    device: torch.device,
    teacher: HKVDetector | None = None,
    retained_class_names: list[str] | None = None,
    previous_signatures: set[str] | None = None,
) -> list[float]:
    for parameter in model.backbone.parameters():
        parameter.requires_grad = False
    model.to(device)
    parameters = [parameter for parameter in model.parameters() if parameter.requires_grad]
    learning_rate = INCREMENTAL_LEARNING_RATE if teacher is not None else INITIAL_LEARNING_RATE
    optimizer = torch.optim.AdamW(parameters, lr=learning_rate, weight_decay=0.0001)
    retained_class_indices = [
        class_names.index(name)
        for name in (retained_class_names or [])
        if name in class_names
    ]
    previous_signatures = previous_signatures or set()
    if teacher is not None:
        teacher.to(device).eval()
        for parameter in teacher.parameters():
            parameter.requires_grad = False
    losses: list[float] = []
    batch_size = min(4, len(samples))
    training_started = time.perf_counter()
    for epoch in range(epochs):
        model.train()
        model.backbone.eval()
        random.shuffle(samples)
        epoch_losses = []
        for start in range(0, len(samples), batch_size):
            batch = samples[start:start + batch_size]
            images = torch.stack([augment(load_image(sample)) for sample in batch]).to(device)
            class_logits, box_logits = model.raw(images)
            targets = make_targets(
                batch, class_names, class_logits.shape[2], class_logits.shape[3], device
            )
            loss = detection_loss(class_logits, box_logits, *targets)
            replay_indices = [
                index for index, sample in enumerate(batch)
                if sample.signature in previous_signatures
            ]
            if teacher is not None and replay_indices and retained_class_indices:
                replay_tensor = torch.tensor(replay_indices, dtype=torch.long, device=device)
                with torch.no_grad():
                    teacher_class_logits, teacher_box_logits = teacher.raw(
                        torch.index_select(images, 0, replay_tensor)
                    )
                loss = loss + RETENTION_WEIGHT * retention_loss(
                    torch.index_select(class_logits, 0, replay_tensor),
                    torch.index_select(box_logits, 0, replay_tensor),
                    teacher_class_logits,
                    teacher_box_logits,
                    retained_class_indices,
                )
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(parameters, 5.0)
            optimizer.step()
            epoch_losses.append(float(loss.detach().cpu()))
        mean_loss = sum(epoch_losses) / len(epoch_losses)
        losses.append(mean_loss)
        completed_epochs = epoch + 1
        elapsed = time.perf_counter() - training_started
        eta = (elapsed / completed_epochs) * (epochs - completed_epochs)
        print(
            f"HKV_PROGRESS epoch={completed_epochs} total={epochs} "
            f"elapsed_seconds={elapsed:.1f} eta_seconds={eta:.1f} "
            f"loss={mean_loss:.4f}",
            flush=True,
        )
    return losses


def intersection_over_union(first: dict, second: dict) -> float:
    left = max(first["x"], second["x"])
    top = max(first["y"], second["y"])
    right = min(first["x"] + first["width"], second["x"] + second["width"])
    bottom = min(first["y"] + first["height"], second["y"] + second["height"])
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    union = first["width"] * first["height"] + second["width"] * second["height"] - intersection
    return intersection / union if union > 0 else 0.0


def decode_predictions(
    model: HKVDetector, image: torch.Tensor, class_names: list[str], device: torch.device
) -> list[dict]:
    model.eval()
    with torch.no_grad():
        scores, boxes = model(image[None].to(device))
        local_max = functional.max_pool2d(scores, 3, stride=1, padding=1)
        keep = (scores >= local_max) & (scores >= CONFIDENCE_FLOOR)
        candidates = []
        indices = torch.nonzero(keep[0], as_tuple=False)
        for class_index, y, x in indices.cpu().tolist():
            box = boxes[0, :, y, x].detach().cpu().tolist()
            candidates.append({
                "label": class_names[class_index],
                "x": float(box[0]),
                "y": float(box[1]),
                "width": float(box[2]),
                "height": float(box[3]),
                "confidence": float(scores[0, class_index, y, x].detach().cpu()),
            })
    candidates.sort(key=lambda value: value["confidence"], reverse=True)
    selected = []
    for candidate in candidates:
        same_class = [value for value in selected if value["label"] == candidate["label"]]
        if len(same_class) >= MAX_PER_CLASS:
            continue
        if any(intersection_over_union(candidate, value) >= NMS_OVERLAP for value in same_class):
            continue
        selected.append(candidate)
        if len(selected) == MAX_DETECTIONS:
            break
    return selected


def human_boxes(sample: Sample) -> list[dict]:
    return [{
        "label": value.label,
        "x": value.x,
        "y": value.y,
        "width": value.width,
        "height": value.height,
        "confidence": 1.0,
    } for value in sample.annotations if not value.is_negative]


def review_metrics(examples: list[dict]) -> dict:
    true_positives = 0
    false_positives = 0
    false_negatives = 0
    overlaps = []
    for example in examples:
        unmatched = set(range(len(example["human"])))
        predictions = [
            value for value in example["predictions"]
            if value["confidence"] >= REVIEW_CONFIDENCE
        ]
        for prediction in predictions:
            choices = [
                (index, intersection_over_union(prediction, example["human"][index]))
                for index in unmatched
                if example["human"][index]["label"] == prediction["label"]
            ]
            best = max(choices, key=lambda value: value[1]) if choices else None
            if best and best[1] >= 0.5:
                true_positives += 1
                overlaps.append(best[1])
                unmatched.remove(best[0])
            else:
                false_positives += 1
        false_negatives += len(unmatched)
    precision = true_positives / (true_positives + false_positives) if true_positives + false_positives else 0
    recall = (
        true_positives / (true_positives + false_negatives)
        if true_positives + false_negatives else 0
    )
    return {
        "confidenceThreshold": REVIEW_CONFIDENCE,
        "intersectionOverUnionThreshold": 0.5,
        "truePositives": true_positives,
        "falsePositives": false_positives,
        "falseNegatives": false_negatives,
        "precision": precision,
        "recall": recall,
        "meanIntersectionOverUnion": sum(overlaps) / len(overlaps) if overlaps else 0,
    }


def average_precision(examples: list[dict], class_name: str) -> float:
    truth_by_example = {
        example["exampleIdentifier"]: [
            value for value in example["human"] if value["label"] == class_name
        ] for example in examples
    }
    total_truth = sum(len(values) for values in truth_by_example.values())
    if total_truth == 0:
        return 0.0
    predictions = []
    for example in examples:
        for value in example["predictions"]:
            if value["label"] == class_name:
                predictions.append((value["confidence"], example["exampleIdentifier"], value))
    predictions.sort(reverse=True, key=lambda value: value[0])
    matched = {identifier: set() for identifier in truth_by_example}
    true_positive = []
    false_positive = []
    for _, identifier, prediction in predictions:
        choices = [
            (index, intersection_over_union(prediction, truth))
            for index, truth in enumerate(truth_by_example[identifier])
            if index not in matched[identifier]
        ]
        best = max(choices, key=lambda value: value[1]) if choices else None
        if best and best[1] >= 0.5:
            matched[identifier].add(best[0])
            true_positive.append(1)
            false_positive.append(0)
        else:
            true_positive.append(0)
            false_positive.append(1)
    if not predictions:
        return 0.0
    cumulative_true = np.cumsum(true_positive)
    cumulative_false = np.cumsum(false_positive)
    recalls = cumulative_true / total_truth
    precisions = cumulative_true / np.maximum(cumulative_true + cumulative_false, 1)
    recalls = np.concatenate(([0.0], recalls, [1.0]))
    precisions = np.concatenate(([1.0], precisions, [0.0]))
    for index in range(len(precisions) - 2, -1, -1):
        precisions[index] = max(precisions[index], precisions[index + 1])
    changes = np.where(recalls[1:] != recalls[:-1])[0]
    return float(np.sum((recalls[changes + 1] - recalls[changes]) * precisions[changes + 1]))


def metric_summary(examples: list[dict], class_names: list[str]) -> dict:
    values = {name: average_precision(examples, name) for name in class_names}
    mean = sum(values.values()) / len(values) if values else 0.0
    return {
        "isValid": bool(examples),
        "meanAveragePrecision": mean,
        "meanAveragePrecisionAt50PercentIOU": mean,
        "averagePrecisionByClass": values,
        "averagePrecisionAt50PercentIOUByClass": values,
        "error": None,
    }


def evaluate(
    model: HKVDetector, samples: list[Sample], class_names: list[str], device: torch.device
) -> list[dict]:
    examples = []
    for sample in samples:
        examples.append({
            "exampleIdentifier": sample.example_identifier,
            "imageFilename": sample.image_filename,
            "split": sample.split,
            "human": human_boxes(sample),
            "predictions": decode_predictions(model, load_image(sample), class_names, device),
        })
    return examples


def export_coreml(model: HKVDetector, class_names: list[str], destination: Path) -> None:
    if ct is None:
        raise ValueError("Core ML export requires coremltools.")
    model = model.cpu().eval()
    example = torch.zeros((1, 3, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32)
    with torch.no_grad():
        traced = torch.jit.trace(model, example, strict=False)
    core_model = ct.convert(
        traced,
        convert_to="mlprogram",
        minimum_deployment_target=ct.target.macOS14,
        compute_precision=ct.precision.FLOAT16,
        inputs=[ct.ImageType(
            name="image",
            shape=example.shape,
            scale=1 / 255.0,
            color_layout=ct.colorlayout.RGB,
        )],
        outputs=[ct.TensorType(name="scores"), ct.TensorType(name="boxes")],
    )
    core_model.author = "Hollow Knight Vision"
    core_model.short_description = "Incremental shared object detector"
    core_model.user_defined_metadata["hkv.algorithm"] = ALGORITHM
    core_model.user_defined_metadata["hkv.classNames"] = json.dumps(class_names)
    core_model.user_defined_metadata["hkv.boxFormat"] = "top-left-xywh"
    core_model.save(str(destination))


def export_onnx(model: HKVDetector, destination: Path) -> None:
    model = model.cpu().eval()
    example = torch.zeros((1, 3, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32)
    with torch.no_grad():
        torch.onnx.export(
            model,
            example,
            destination,
            input_names=["image"],
            output_names=["scores", "boxes"],
            opset_version=17,
            do_constant_folding=True,
            dynamo=False,
        )


def export_torchscript(model: HKVDetector, destination: Path) -> None:
    model = model.cpu().eval()
    example = torch.zeros((1, 3, INPUT_HEIGHT, INPUT_WIDTH), dtype=torch.float32)
    with torch.no_grad():
        traced = torch.jit.trace(model, example, strict=False)
    torch.jit.save(traced, destination)


def export_model(
    model: HKVDetector,
    class_names: list[str],
    output: Path,
    requested_format: str,
) -> tuple[str, str]:
    export_format = select_export_format(requested_format)
    if export_format == "coreml":
        filename = "Detector.mlpackage"
        export_coreml(model, class_names, output / filename)
    elif export_format == "onnx":
        filename = "Detector.onnx"
        export_onnx(model, output / filename)
    elif export_format == "torchscript":
        filename = "Detector.torchscript.pt"
        export_torchscript(model, output / filename)
    else:
        raise ValueError(f"Unsupported export format: {export_format}")
    return filename, export_format


def iso_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def run(arguments: argparse.Namespace) -> None:
    dataset_root = Path(arguments.dataset).resolve()
    manifest, samples = load_dataset(dataset_root)
    if arguments.validate_only:
        print(f"Dataset valid: {manifest['classIdentifier']}")
        return
    if not arguments.output:
        raise ValueError("Missing required argument: --output")

    output = Path(arguments.output).resolve()
    if output.exists():
        raise ValueError(f"Training output already exists: {output}")
    staging = output.parent / f".staging-{output.name}"
    if staging.exists():
        raise ValueError(f"Training output already exists: {staging}")
    staging.mkdir(parents=True)
    started_at = iso_now()

    try:
        base_checkpoint = None
        base_path = Path(arguments.base_checkpoint).resolve() if arguments.base_checkpoint else None
        if base_path:
            base_checkpoint = torch.load(base_path, map_location="cpu", weights_only=False)
            if base_checkpoint.get("algorithm") != ALGORITHM:
                raise ValueError("Base checkpoint uses an incompatible trainer.")

        annotated_names = {
            item.label
            for sample in samples
            for item in sample.annotations
            if not item.is_negative
        }
        previous_names = retained_previous_class_names(base_checkpoint)
        class_names = training_class_names(manifest, annotated_names, base_checkpoint)
        if not class_names:
            raise ValueError("Dataset has no classes.")
        training = [sample for sample in samples if sample.split == "training"]
        validation = [sample for sample in samples if sample.split == "validation"]
        if not training:
            raise ValueError("Dataset has no training images.")
        previous_signatures = set(base_checkpoint.get("trained_signatures", [])) if base_checkpoint else set()
        training_set, new_count, retained_count = full_training_set(
            training, previous_signatures
        )
        epochs = max(
            1,
            arguments.iterations
            if base_checkpoint is None
            else min(30, max(10, arguments.iterations // 4)),
        )
        device = select_device(arguments.device)
        batch_size = min(4, len(training_set))
        batches_per_epoch = math.ceil(len(training_set) / batch_size)
        print(
            f"Trainer {ALGORITHM} device={device} classes={len(class_names)} "
            f"training_images={len(training_set)} new={new_count} "
            f"retained={retained_count} batches_per_epoch={batches_per_epoch} "
            f"epochs={epochs}",
            flush=True,
        )

        random.seed(7)
        torch.manual_seed(7)
        model = make_model(class_names, base_checkpoint)
        teacher = None
        review_samples = validation or training
        baseline_examples = None
        baseline_metrics = None
        if base_checkpoint:
            print("HKV_STAGE name=baseline", flush=True)
            model.to(device)
            baseline_examples = evaluate(model, review_samples, class_names, device)
            baseline_metrics = review_metrics(baseline_examples)
            teacher = make_model(class_names, base_checkpoint)
        print("HKV_STAGE name=training", flush=True)
        training_started = time.perf_counter()
        losses = train_model(
            model,
            training_set,
            class_names,
            epochs,
            device,
            teacher=teacher,
            retained_class_names=sorted(previous_names),
            previous_signatures=previous_signatures,
        )
        training_duration = time.perf_counter() - training_started
        print("HKV_STAGE name=evaluating", flush=True)
        training_examples = evaluate(model, training, class_names, device)
        review_examples = evaluate(model, review_samples, class_names, device)
        prediction_review = {
            "examples": review_examples,
            "metrics": review_metrics(review_examples),
            "baselineExamples": baseline_examples,
            "baselineMetrics": baseline_metrics,
        }

        run_identifier = output.name
        checkpoint_filename = "Detector.pt"
        checkpoint = {
            "schema_version": 1,
            "algorithm": ALGORITHM,
            "run_identifier": run_identifier,
            "base_run_identifier": base_checkpoint.get("run_identifier") if base_checkpoint else None,
            "class_names": class_names,
            "state_dict": {
                name: value.detach().cpu() for name, value in model.state_dict().items()
            },
            "trained_signatures": sorted({sample.signature for sample in training}),
            "epochs_this_run": epochs,
            "final_loss": losses[-1],
            "learning_rate": (
                INCREMENTAL_LEARNING_RATE if base_checkpoint else INITIAL_LEARNING_RATE
            ),
            "retention_weight": RETENTION_WEIGHT if base_checkpoint else 0,
        }
        torch.save(checkpoint, staging / checkpoint_filename)
        print("HKV_STAGE name=exporting", flush=True)
        model_filename, model_format = export_model(
            model,
            class_names,
            staging,
            arguments.export_format,
        )

        (staging / "predictions.json").write_text(json.dumps(
            prediction_review, indent=2, sort_keys=True
        ))
        # review_samples is exactly validation whenever a holdout exists, so
        # reuse those predictions instead of evaluating the holdout twice.
        validation_examples = review_examples if validation else []
        run_manifest = {
            "schemaVersion": 2,
            "id": run_identifier,
            "datasetIdentifier": manifest["id"],
            "classIdentifier": manifest["classIdentifier"],
            "startedAt": started_at,
            "completedAt": iso_now(),
            "algorithm": ALGORITHM,
            "maximumIterations": arguments.iterations,
            "gridSize": 8,
            "trainingAnnotationCount": sum(len(sample.annotations) for sample in training),
            "validationAnnotationCount": sum(len(sample.annotations) for sample in validation),
            "isPreliminary": manifest.get("isPreliminary", True),
            "modelFilename": model_filename,
            "modelFormat": model_format,
            "checkpointFilename": checkpoint_filename,
            "baseRunIdentifier": checkpoint["base_run_identifier"],
            "newTrainingImageCount": new_count,
            # Retained for compatibility with older app builds. Every retained
            # image is now replayed, so this equals retainedTrainingImageCount.
            "replayTrainingImageCount": retained_count,
            "fullTrainingSet": True,
            "trainingImageCount": len(training_set),
            "changedTrainingImageCount": new_count,
            "retainedTrainingImageCount": retained_count,
            "trainingEpochCount": epochs,
            "trainingBatchSize": batch_size,
            "trainingBatchCountPerEpoch": batches_per_epoch,
            "optimizerStepCount": batches_per_epoch * epochs,
            "trainingDevice": str(device),
            "trainingDurationSeconds": training_duration,
            "epochLosses": losses,
            "classIdentifiers": class_names,
            "predictionsFilename": "predictions.json",
            "trainingMetrics": metric_summary(training_examples, class_names),
            "validationMetrics": (
                metric_summary(validation_examples, class_names) if validation else None
            ),
            "reviewMetrics": prediction_review["metrics"],
            "baselineReviewMetrics": baseline_metrics,
            "learningRate": checkpoint["learning_rate"],
            "retentionWeight": checkpoint["retention_weight"],
        }
        (staging / "training.json").write_text(json.dumps(
            run_manifest, indent=2, sort_keys=True
        ))
        shutil.move(str(staging), str(output))
        print("Training complete", flush=True)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise


if __name__ == "__main__":
    try:
        run(parse_arguments())
    except Exception as error:
        print(f"Training failed: {error}", file=sys.stderr, flush=True)
        raise SystemExit(1)
