"""Fixed, compact typed line geometry; mirrors BotCompactMarkings.swift exactly."""
import math
CAPACITY = 16
KIND_VALUES = {1: .33, 2: .66, 3: 0., 4: 1.}
FIELDS = ['start_dx', 'start_dy', 'end_dx', 'end_dy', 'kind', 'present']
KINDS = ['ground', 'wall', 'spike', 'door']
METADATA_NAMES = [f'marking_{kind}_known' for kind in KINDS] + [f'marking_{kind}_count' for kind in KINDS] + ['marking_overflow']
LANDING_NAMES = ['landing_present', 'landing_left', 'landing_right', 'landing_clearance',
                 'landing_dx', 'landing_dy']
def names_for(version):
    return METADATA_NAMES + [f'marking_{i}_{field}' for i in range(64 if version == 13 else CAPACITY) for field in FIELDS]
NAMES = names_for(14)
LEGACY_NAMES = names_for(13)

def encode(geometry, knight_x, knight_y, version=14):
    capacity = 64 if version == 13 else CAPACITY
    names = names_for(version)
    empty = [0.] * len(names)
    if geometry is None:
        return empty
    w, h = geometry.get('width'), geometry.get('height')
    if not all(isinstance(v, (float, int)) and math.isfinite(v) for v in [w, h, knight_x, knight_y]) or min(w, h) <= 0:
        return empty
    kx, ky = knight_x * w, knight_y * w
    segments = set()
    def add(kind, ax, ay, bx, by):
        if not all(math.isfinite(v) for v in [ax, ay, bx, by]): return
        if ay == by and 0 <= ay <= h:
            left, right = max(0, min(w, min(ax, bx))), max(0, min(w, max(ax, bx)))
            if right > left: segments.add((kind, left, ay, right, ay))
        elif ax == bx and 0 <= ax <= w:
            low, high = max(0, min(h, min(ay, by))), max(0, min(h, max(ay, by)))
            if high > low: segments.add((kind, ax, low, ax, high))
    for s in geometry.get('floors', []): add(1, s['minimumX'], s['y'], s['maximumX'], s['y'])
    for s in geometry.get('walls', []): add(2, s['x'], s['bottomY'], s['x'], s['topY'])
    for kind, key in [(3,'spikes'), (4,'doors')]:
        for s in geometry.get(key) or []: add(kind, s['x1'], s['y1'], s['x2'], s['y2'])
    def key(s):
        kind, ax, ay, bx, by = s
        dx, dy = bx - ax, by - ay
        t = min(1, max(0, ((kx - ax) * dx + (ky - ay) * dy) / (dx * dx + dy * dy)))
        return ((kx - ax - t * dx) ** 2 + (ky - ay - t * dy) ** 2, *s)
    ordered = sorted(segments, key=key)
    values = [1., 1., float(geometry.get('spikes') is not None), float(geometry.get('doors') is not None)]
    values += [min(4, sum(s[0] == kind for s in segments) / capacity) for kind in range(1,5)]
    values += [min(4, max(0, len(ordered) - capacity) / capacity)]
    for kind, ax, ay, bx, by in ordered[:capacity]:
        values += [max(-4, min(4, v)) for v in [(ax-kx)/w,(ay-ky)/w,(bx-kx)/w,(by-ky)/w,(kind/4 if version == 13 else KIND_VALUES[kind]),1.]]
    return values + [0.] * (len(names) - len(values))

def landing_features(markings, height, grounded, previous=None):
    """Local shelf bounds and their movement, normalized by Knight height."""
    absent = [0.] * len(LANDING_NAMES)
    if grounded is not False or not math.isfinite(height) or height <= .005 or len(markings) != len(NAMES):
        return absent
    candidate = None
    for i in range(len(METADATA_NAMES), len(markings), len(FIELDS)):
        x, y, x2, y2, kind, present = markings[i:i+len(FIELDS)]
        if present <= .5 or abs(kind-.33) >= .00001 or y != y2:
            continue
        left, right, clearance = x/height, x2/height, -y/height-1
        gap = max(0, left, -right)
        if not (.35 <= right-left <= 3 and -.07 <= clearance <= 2.75 and gap <= 2.5):
            continue
        rank = clearance + .05*gap
        if candidate is None or rank < candidate[0]:
            candidate = (rank, left, right, clearance)
    if candidate is None:
        return absent
    def bounded(value):
        return max(-4., min(4., value))
    _, left, right, clearance = candidate
    left, right = bounded(left), bounded(right)
    dx = dy = 0.
    if previous is not None and len(previous) == len(LANDING_NAMES) and previous[0] == 1:
        old_left, old_right, old_clearance = previous[1:4]
        if abs((right-left)-(old_right-old_left)) <= .12 and abs(clearance-old_clearance) <= 1.5:
            dx = bounded((old_left+old_right-left-right)/2)
            dy = clearance-old_clearance
    return [1., left, right, clearance, dx, dy]


def _wall_slots(frames, names):
    start = names.index('marking_0_start_dx')
    capacity = 64 if 'marking_63_present' in names else CAPACITY
    return frames[..., start:start+capacity*len(FIELDS)].reshape(*frames.shape[:-1], capacity, len(FIELDS)), capacity


def wall_present(frames, names):
    """Array of observations with a typed wall, for training coverage reports."""
    import numpy as np
    slots, capacity = _wall_slots(frames, names)
    return ((abs(slots[...,4] - (.5 if capacity == 64 else .66)) < 1e-6) & (slots[...,5] == 1)).any(axis=-1)

def mask_walls(frames, names):
    slots, capacity = _wall_slots(frames, names)
    slots[(abs(slots[...,4] - (.5 if capacity == 64 else .66)) < 1e-6) & (slots[...,5] == 1)] = 0
    frames[...,names.index('marking_wall_count')] = 0

def recover_recorded_spikes(directory, manifest, observations):
    """Recover only frozen authored spikes joined to exact archived Hacker frames.

    Source recordings are unchanged. Missing/ambiguous camera joins remain
    unknown (no spikes key), not falsely observed empty spike channels.
    """
    from pathlib import Path
    import hashlib, json
    from collections import defaultdict
    if manifest.get('botDomain') != 'hacker' or not any(
            'spikes' not in (row.get('authoredTerrain') or {}) for row in observations):
        return {}
    directory = Path(directory)
    context_file, labels_file = directory/'audit-context.json', directory/'authored-ground.json'
    if not context_file.is_file() or not labels_file.is_file(): return {'matchedFrames': 0}
    context = json.loads(context_file.read_text())
    labels_raw = labels_file.read_bytes()
    digest = hashlib.sha256(labels_raw).hexdigest()
    if context.get('authoredGroundSHA256') != digest:
        raise ValueError('Frozen spike reference hash mismatch')
    from bot_audit import inspect
    audit = inspect(directory)
    if not audit.get('sourceArchive') or not audit.get('replayPath'): return {'matchedFrames': 0}
    archive_file = Path(audit['sourceArchive'])/'manifest.json'
    replay_file = Path(audit['replayPath'])
    archive_raw, replay_raw = archive_file.read_bytes(), replay_file.read_bytes()
    archive, replay = json.loads(archive_raw), json.loads(replay_raw)
    rid = audit['replayID'].lower()
    if str(archive['pathID']).lower() != rid or str(replay['id']).lower() != rid:
        raise ValueError('Spike recovery has a mismatched replay identity')
    labels = json.loads(labels_raw).get('spikes')
    if labels is None: return {'matchedFrames': 0, 'frozenLabelsSHA256': digest}
    frames, samples = defaultdict(list), defaultdict(list)
    for frame in archive.get('frames',[]):
        frames[(frame['captureTimestamp'], frame['captureGeneration'])].append(frame)
    for entry in replay.get('groundTruthSamples',[]):
        samples[int(entry['sample']['unityFrame']) & 0xffffff].append(entry)
    matched = total = 0
    for row in observations:
        geometry = row.get('authoredTerrain')
        if geometry is None or 'spikes' in geometry: continue
        options = frames[(row['captureTimestamp'],row['generation'])]
        if len(options) != 1 or options[0].get('renderedGameFrame') is None: continue
        frame = options[0]
        entries = [s for s in samples[frame['renderedGameFrame']] if abs(s['receivedTimestamp']-row['capturedAt']) <= .25]
        if len(entries) != 1: continue
        sample = entries[0]['sample']
        if not sample.get('cameraAvailable') or sample.get('projectionPixelWidth',0) <= 0 or sample.get('projectionPixelHeight',0) <= 0: continue
        w,h = geometry['width'],geometry['height']
        sy = sample.get('pixelsPerWorldUnitY') or (sample['projectionPixelHeight'] / (2*sample['orthographicSize']) if sample.get('orthographicSize',0)>0 else 0)
        sx = sample.get('pixelsPerWorldUnitX') or sy
        sx *= w/sample['projectionPixelWidth']; sy *= h/sample['projectionPixelHeight']
        if not all(math.isfinite(v) for v in [sx,sy,sample['cameraX'],sample['cameraY']]) or min(sx,sy)<=0: continue
        spikes=[]
        for line in labels:
            if line.get('seededFromDetector') or line.get('kind')!='spike' or line['sceneName']!=sample['sceneName']: continue
            y=h/2+(line['worldY']-sample['cameraY'])*sy
            x1=w/2+(line['minimumWorldX']-sample['cameraX'])*sx
            x2=w/2+(line['maximumWorldX']-sample['cameraX'])*sx
            left,right=max(0,min(x1,x2)),min(w,max(x1,x2))
            if 0<=y<=h and right>left:spikes.append(dict(x1=left,y1=y,x2=right,y2=y))
        geometry['spikes']=spikes
        matched+=1;total+=len(spikes)
    return {'matchedFrames':matched,'visibleSpikes':total,'frozenLabelsSHA256':digest,
            'archiveManifestSHA256':hashlib.sha256(archive_raw).hexdigest(),
            'replaySHA256':hashlib.sha256(replay_raw).hexdigest()}
