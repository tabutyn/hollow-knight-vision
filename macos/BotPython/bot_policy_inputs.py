"""Select policy inputs from the oldest-to-newest raw observation history."""
import json
from pathlib import Path

from hkv_bot_dataset import HISTORY_LENGTH, SCHEMAS


def _load_views():
    root = Path(__file__).resolve().parent.parent
    spec = root / "playing-policy-inputs.json"
    if not spec.is_file():
        spec = root / "Sources/HollowKnightVision/Resources" / spec.name
    return {role: (tuple(view["current"]), tuple(view["history"]))
            for role, view in json.loads(spec.read_text()).items()}


VIEWS = _load_views()


def policy_columns(version, role):
    """Return ordered raw column indices. The saved indices are the inference contract."""
    if version not in (14, 15, 16):
        raise ValueError(f"Compact policy views require observation schema 14, 15 or 16, got {version}")
    current, history = VIEWS[role]
    names = SCHEMAS[version]
    return [frame * len(names) + i for frame in range(HISTORY_LENGTH)
            for i, name in enumerate(names)
            if name.startswith(current if frame == HISTORY_LENGTH - 1 else history)]
