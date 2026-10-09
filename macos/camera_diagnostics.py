"""Exact rendered-frame camera diagnostics. Never feeds the visual solver.

One origin per camera coordinate epoch; no fitting, time interpolation, or
reanchoring at corrections. Missing/ambiguous telemetry is explicitly excluded.
All error distances are in captured-image pixels (normally 640 x 360).
"""
import collections
import math
import statistics


def percentile(values, percent):
    if not values:
        return None
    values = sorted(values)
    index = (len(values) - 1) * percent / 100
    low, high = math.floor(index), math.ceil(index)
    return values[low] + (values[high] - values[low]) * (index - low)


def distribution(values):
    return {'count': len(values), 'rms': math.sqrt(statistics.mean(x*x for x in values))
            if values else None, 'p50': percentile(values, 50),
            'p95': percentile(values, 95), 'max': max(values) if values else None}


def finite(*values):
    return all(isinstance(x, (int, float)) and math.isfinite(x) for x in values)


def low_resolution_motion_demand(run, width=640, height=360,
                                 grid_width=64, grid_height=36,
                                 minimum_age=.025, maximum_age=.12,
                                 preferred_age=.05,
                                 maximum_x_cells=4, maximum_y_cells=8):
    """Measure Hacker camera travel against the live 64x36 bridge bounds.

    This mirrors the bridge's reference selection, but uses game camera truth
    only for offline diagnosis. Scene/session/projection changes start a new
    epoch so a one-way room transition is never measured as image motion.
    """
    tracking_by_frame = collections.defaultdict(list)
    for tracking in run.get('trackingSamples') or []:
        rendered_frame = tracking.get('renderedGameFrame')
        if isinstance(rendered_frame, int):
            tracking_by_frame[rendered_frame & 0xffffff].append(tracking)

    def ground_reliability(sample):
        unity_frame = sample.get('unityFrame')
        if not isinstance(unity_frame, int):
            return None
        matches = tracking_by_frame.get(unity_frame & 0xffffff, [])
        states = {
            bool(match.get('poseVerified'))
            and bool(match.get('hasConfirmedGround', True))
            for match in matches
        }
        return states.pop() if len(states) == 1 else None

    def subset(rows):
        horizontal = [row['horizontalCells'] for row in rows]
        vertical = [row['verticalCells'] for row in rows]
        representable = sum(
            x <= maximum_x_cells and y <= maximum_y_cells
            for x, y in zip(horizontal, vertical)
        )
        return {
            'samples': len(rows),
            'representablePercent': (
                100*representable/len(rows) if rows else None
            ),
            'horizontalCells': {
                'p50': percentile(horizontal, 50),
                'p95': percentile(horizontal, 95),
                'p99': percentile(horizontal, 99),
                'max': max(horizontal) if horizontal else None,
                'outsideSearchPercent': (
                    100*sum(x > maximum_x_cells for x in horizontal)/len(horizontal)
                    if horizontal else None
                ),
            },
            'verticalCells': {
                'p50': percentile(vertical, 50),
                'p95': percentile(vertical, 95),
                'p99': percentile(vertical, 99),
                'max': max(vertical) if vertical else None,
                'outsideSearchPercent': (
                    100*sum(y > maximum_y_cells for y in vertical)/len(vertical)
                    if vertical else None
                ),
            },
        }

    history = []
    horizontal, vertical, intervals, rows = [], [], [], []
    identity = None
    for recorded in run.get('groundTruthSamples') or []:
        sample = recorded.get('sample') or {}
        values = [recorded.get('offset'), sample.get('cameraX'), sample.get('cameraY'),
                  sample.get('pixelsPerWorldUnitX'), sample.get('pixelsPerWorldUnitY'),
                  sample.get('projectionPixelWidth'), sample.get('projectionPixelHeight')]
        if not sample.get('cameraAvailable') or not finite(*values) or min(values[3:]) <= 0:
            continue
        timestamp, camera_x, camera_y, ppu_x, ppu_y, projection_w, projection_h = values
        current_identity = (sample.get('sessionID'), sample.get('sceneName'),
                            projection_w, projection_h)
        if current_identity != identity:
            history = []
            identity = current_identity
        history = [item for item in history if timestamp-item[0] <= maximum_age]
        candidates = [item for item in history
                      if minimum_age <= timestamp-item[0] <= maximum_age]
        if candidates:
            reference = min(candidates,
                            key=lambda item: abs(timestamp-item[0]-preferred_age))
            age = timestamp-reference[0]
            scale_x = ppu_x * width / projection_w
            scale_y = ppu_y * height / projection_h
            x_cells = abs(camera_x-reference[1]) * scale_x / (width/grid_width)
            y_cells = abs(camera_y-reference[2]) * scale_y / (height/grid_height)
            horizontal.append(x_cells)
            vertical.append(y_cells)
            intervals.append(age)
            rows.append({'offset': timestamp, 'sceneName': sample.get('sceneName'),
                         'intervalSeconds': age, 'horizontalCells': x_cells,
                         'verticalCells': y_cells,
                         'groundTrackingReliable': ground_reliability(sample)})
        history.append((timestamp, camera_x, camera_y))

    representable = sum(x <= maximum_x_cells and y <= maximum_y_cells
                        for x, y in zip(horizontal, vertical))
    peak_x = max(rows, key=lambda row: row['horizontalCells'], default=None)
    peak_y = max(rows, key=lambda row: row['verticalCells'], default=None)
    return {
        'method': 'hacker-camera-short-baseline',
        'gridSize': [grid_width, grid_height],
        'comparisonAgeSeconds': [minimum_age, maximum_age],
        'preferredComparisonAgeSeconds': preferred_age,
        'searchBoundsCells': [maximum_x_cells, maximum_y_cells],
        'samples': len(horizontal),
        'representablePercent': 100*representable/len(horizontal) if horizontal else None,
        'horizontalCells': {
            'p50': percentile(horizontal, 50), 'p95': percentile(horizontal, 95),
            'p99': percentile(horizontal, 99), 'max': max(horizontal) if horizontal else None,
            'outsideSearchPercent': 100*sum(x > maximum_x_cells for x in horizontal)
                / len(horizontal) if horizontal else None,
        },
        'verticalCells': {
            'p50': percentile(vertical, 50), 'p95': percentile(vertical, 95),
            'p99': percentile(vertical, 99), 'max': max(vertical) if vertical else None,
            'outsideSearchPercent': 100*sum(y > maximum_y_cells for y in vertical)
                / len(vertical) if vertical else None,
        },
        'comparisonIntervalMilliseconds': {
            'p50': 1000*percentile(intervals, 50) if intervals else None,
            'p95': 1000*percentile(intervals, 95) if intervals else None,
        },
        'peakHorizontal': peak_x,
        'peakVertical': peak_y,
        'groundReliabilityJoin': {
            'matchedSamples': sum(
                row['groundTrackingReliable'] is not None for row in rows
            ),
            'unmatchedSamples': sum(
                row['groundTrackingReliable'] is None for row in rows
            ),
        },
        'groundReliableDemand': subset([
            row for row in rows if row['groundTrackingReliable'] is True
        ]),
        'groundUnverifiedDemand': subset([
            row for row in rows if row['groundTrackingReliable'] is False
        ]),
    }


def truth_index(run):
    index = collections.defaultdict(list)
    for recorded in run.get('groundTruthSamples') or []:
        sample = recorded['sample']
        if isinstance(sample.get('unityFrame'), int):
            index[sample['unityFrame'] & 0xffffff].append(sample)
    return index


def select_truth(index, marker):
    values = index.get(marker, [])
    if not values:
        return None, 'missingTelemetry'
    # Repeated receipt of the same Unity sample is harmless; a wraparound,
    # new receiver session, or conflicting camera sample is not.
    keys = ('sessionID', 'unityFrame', 'sceneName', 'cameraAvailable', 'cameraX',
            'cameraY', 'projectionPixelWidth', 'projectionPixelHeight',
            'pixelsPerWorldUnitX', 'pixelsPerWorldUnitY')
    signatures = {tuple(value.get(key) for key in keys) for value in values}
    if len(signatures) != 1:
        return None, 'ambiguousTelemetry'
    return values[0], None


def exact_frames(run, width=640, height=360):
    index = truth_index(run)
    rejected = collections.Counter()
    frames, epochs = [], []
    anchor = None
    for tracking_index, tracking in enumerate(run.get('trackingSamples', [])):
        marker = tracking.get('renderedGameFrame')
        if marker is None:
            rejected['missingMarker'] += 1
            continue
        truth, reason = select_truth(index, marker)
        if reason:
            rejected[reason] += 1
            continue
        if not truth.get('cameraAvailable'):
            rejected['cameraUnavailable'] += 1
            continue
        x, y = tracking.get('publishedCameraX'), tracking.get('publishedCameraY')
        # An older private candidate may have been rejected. Do not silently
        # substitute it for the camera the player actually saw.
        if not finite(x, y):
            rejected['publishedPoseUnavailable'] += 1
            continue
        raw = [truth.get(key) for key in ('cameraX', 'cameraY', 'pixelsPerWorldUnitX',
                'pixelsPerWorldUnitY', 'projectionPixelWidth', 'projectionPixelHeight')]
        if not finite(*raw) or min(raw[2:]) <= 0:
            rejected['invalidProjection'] += 1
            continue
        cx, cy, rx, ry, pw, ph = raw
        sx, sy = rx * width / pw, ry * height / ph
        identity = (truth.get('sessionID'), truth.get('sceneName'), tracking.get('roomID'), pw, ph)
        if (anchor is None or identity != anchor['identity']
                or abs(sx / anchor['sx'] - 1) > .001
                or abs(sy / anchor['sy'] - 1) > .001):
            anchor = dict(identity=identity, x=x, y=y, cx=cx, cy=cy, sx=sx, sy=sy)
            epochs.append({'epoch': len(epochs), 'trackingIndex': tracking_index,
                           'unityFrame': marker, 'sceneName': truth.get('sceneName'),
                           'publishedOrigin': [x, y], 'worldOrigin': [cx, cy],
                           'scale': [sx, sy]})
        expected_x = anchor['x'] + (cx - anchor['cx']) * sx
        expected_y = anchor['y'] + (cy - anchor['cy']) * sy
        error_x, error_y = x - expected_x, y - expected_y
        frames.append({'trackingIndex': tracking_index, 'unityFrame': marker,
                       'epoch': len(epochs)-1, 'timestamp': tracking['captureTimestamp'],
                       'offset': tracking.get('captureOffset', tracking['offset']),
                       'published': [x, y], 'expected': [expected_x, expected_y],
                       'error': [error_x, error_y], 'errorPixels': math.hypot(error_x, error_y),
                       'scale': [sx, sy], 'truth': truth, 'tracking': tracking})
    return frames, {'totalTrackingSamples': len(run.get('trackingSamples', [])),
                    'matchedSamples': len(frames), 'rejected': dict(rejected), 'epochs': epochs}


def error_spans(frames, threshold=16, maximum_gap=.1):
    spans, active = [], []
    def finish():
        if not active:
            return
        peak = max(active, key=lambda f: f['errorPixels'])
        spans.append({'startSeconds': active[0]['offset'], 'endSeconds': active[-1]['offset'],
                      'observedSpanSeconds': active[-1]['timestamp']-active[0]['timestamp'],
                      'samples': len(active), 'maxErrorPixels': peak['errorPixels'],
                      'peakSeconds': peak['offset'], 'peakUnityFrame': peak['unityFrame'],
                      'peakErrorXY': peak['error'],
                      'verifiedPercent': 100*sum(f['tracking'].get('poseVerified', False)
                                                for f in active)/len(active)})
    for frame in frames:
        if active and (frame['epoch'] != active[-1]['epoch']
                       or frame['timestamp']-active[-1]['timestamp'] > maximum_gap):
            finish()
            active = []
        if frame['errorPixels'] > threshold:
            active.append(frame)
        elif active:
            finish()
            active = []
    finish()
    return spans


def summarize_exact(run, width=640, height=360):
    frames, coverage = exact_frames(run, width, height)
    verified = [f for f in frames if f['tracking'].get('poseVerified')]
    steps, gaps, stationary, moving = [], [], [], []
    for before, after in zip(frames, frames[1:]):
        dt = after['timestamp'] - before['timestamp']
        if after['epoch'] != before['epoch'] or dt <= 0:
            continue
        delta = [after['error'][i]-before['error'][i] for i in range(2)]
        true_step = math.dist(after['expected'], before['expected'])
        row = {'offset': after['offset'], 'unityFrame': after['unityFrame'],
               'intervalSeconds': dt,
               'errorStepPixels': math.hypot(*delta), 'errorDeltaXY': delta,
               'truthStepPixels': true_step, 'poseSource': after['tracking'].get('poseSource'),
               'verified': after['tracking'].get('poseVerified')}
        if dt > .1:
            gaps.append(row)
            continue
        steps.append(row)
        (stationary if true_step < .1 else moving).append(after['errorPixels'])
    peak = max(frames, key=lambda f: f['errorPixels'], default=None)
    bins = collections.defaultdict(list)
    for frame in frames:
        bins[(frame['epoch'], math.floor(frame['offset']))].append(frame)
    timeline = []
    for (epoch, second), values in sorted(bins.items()):
        timeline.append({'epoch': epoch, 'second': second, 'samples': len(values),
                         'errorXMedian': statistics.median(f['error'][0] for f in values),
                         'errorYMedian': statistics.median(f['error'][1] for f in values),
                         'errorP95': percentile([f['errorPixels'] for f in values], 95),
                         'verifiedPercent': 100*sum(f['tracking'].get('poseVerified', False)
                                                   for f in values)/len(values)})
    sources = collections.defaultdict(list)
    for frame in frames:
        sources[frame['tracking'].get('poseSource', 'unknown')].append(frame['errorPixels'])
    ground_verified = [f for f in verified if f['tracking'].get('poseSource') == 'ground']
    previous_ids, seen_ids = set(), set()
    id_births = id_removals = id_returns = peak_ids = 0
    for sample in run.get('trackingSamples', []):
        ids = set(sample.get('atlasLineIDs', []))
        additions = ids-previous_ids
        id_births += len(additions-seen_ids)
        id_returns += len(additions & seen_ids)
        id_removals += len(previous_ids-ids)
        peak_ids = max(peak_ids, len(ids))
        seen_ids |= ids
        previous_ids = ids
    return {
        'method': 'exact-rendered-frame', 'coordinateSource': 'publishedCamera',
        'alignment': 'first exact matched sample per scene/room/session/projection epoch; no fit',
        'caveat': 'Relative drift after first matched frame; initial absolute offset is unobservable.',
        **coverage,
        'errorPixels': distribution([f['errorPixels'] for f in frames]),
        'absoluteXErrorPixels': distribution([abs(f['error'][0]) for f in frames]),
        'absoluteYErrorPixels': distribution([abs(f['error'][1]) for f in frames]),
        'verifiedErrorPixels': distribution([f['errorPixels'] for f in verified]),
        'verifiedGroundOutputErrorPixels': distribution([f['errorPixels'] for f in ground_verified]),
        'verifiedErrorOver16Percent': 100*sum(f['errorPixels'] > 16 for f in verified)/len(verified)
            if verified else None,
        'errorStepPixels': distribution([s['errorStepPixels'] for s in steps]),
        'processedCaptureGapsOver100ms': sorted(gaps, key=lambda s: s['intervalSeconds'], reverse=True),
        'poseSources': {source: distribution(errors) for source, errors in sources.items()},
        'atlasLineIDs': {'distinct': id_births, 'removals': id_removals,
                         'reappearances': id_returns, 'peakCount': peak_ids,
                         'finalCount': len(previous_ids),
                         'caveat': 'ID churn alone does not establish a duplicate or mistaken deletion.'},
        'stationaryCameraErrorPixels': distribution(stationary),
        'movingCameraErrorPixels': distribution(moving),
        'peak': ({'offset': peak['offset'], 'unityFrame': peak['unityFrame'],
                  'errorXY': peak['error'], 'tracking': peak['tracking']} if peak else None),
        'endErrorXY': frames[-1]['error'] if frames else None,
        'errorOver16Spans': error_spans(frames),
        'largestErrorSteps': sorted(steps, key=lambda s: s['errorStepPixels'], reverse=True)[:10],
        'timeline': timeline,
    }
