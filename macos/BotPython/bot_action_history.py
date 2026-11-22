"""Recorded-state evaluation with the policy's own previous controls."""
from collections import deque


def sequential_inputs(model, observations, sessions, segments):
    """Positions follow expert recordings; this is not a simulated game rollout."""
    import numpy as np
    history, previous, key = deque(maxlen=4), np.zeros(4, dtype=np.float32), None
    frame_width = observations.shape[1] // 4
    rebuilt = []
    for observation, session, segment in zip(observations, sessions, segments):
        current_key = (session, segment)
        if current_key != key:
            history.clear(); previous = np.zeros(4, dtype=np.float32); key = current_key
        frame = observation[-frame_width:].copy()
        frame[9:13] = previous
        history.append(frame)
        stack = np.concatenate([np.zeros(frame_width, dtype=np.float32)] * (4 - len(history)) + list(history))
        action, _ = model.predict(stack, deterministic=True)
        previous = np.asarray([0 if action[0] == 0 else -1 if action[0] == 1 else 1,
                               0 if action[1] == 0 else 1 if action[1] == 1 else -1,
                               action[2], action[3]], dtype=np.float32)
        rebuilt.append(stack)
    return np.asarray(rebuilt, dtype=np.float32)
