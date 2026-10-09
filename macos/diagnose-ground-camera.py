#!/usr/bin/env python3
"""Compare identical recorded floor observations under Hacker and solved poses.

Uses captureTimestamp to associate audits with tracking, then the rendered Unity
frame marker for camera truth. No image decoding or timestamp-nearest fallback.
Scores continuous horizontal length, one-to-one within a vertical tolerance.
The paired observation maps deliberately have no persistence/fusion heuristic.
The third score measures the existing persistent atlas in its fixed origin.
"""
import argparse
import collections
import hashlib
import html
import json
import math
from pathlib import Path

from camera_diagnostics import exact_frames, summarize_exact


def subtract(interval, holes):
    result = [interval]
    for a, b in holes:
        remaining = []
        for x0, x1 in result:
            if b <= x0 or a >= x1:
                remaining.append((x0, x1))
            else:
                if x0 < a:
                    remaining.append((x0, a))
                if b < x1:
                    remaining.append((b, x1))
        result = remaining
    return result


def visible(lines, width, height, exclusions=()):
    result = []
    for x0, x1, y in lines:
        if not 0 <= y < height:
            continue
        x0, x1 = max(0, x0), min(width, x1)
        if x0 >= x1:
            continue
        holes = [(x, x+w) for x, top, w, h in exclusions if top <= y < top+h]
        result.extend((a, b, y) for a, b in subtract((x0, x1), holes))
    return result


def match_counts(truth_y, predicted_y, tolerance):
    # Union exact overlapping fragments, but retain nearby duplicate rows as
    # false positives. Sorted interval matching maximizes one-to-one matches.
    truth_y, predicted_y = sorted(set(truth_y)), sorted(set(predicted_y))
    i = j = matched = 0
    while i < len(truth_y) and j < len(predicted_y):
        if abs(truth_y[i]-predicted_y[j]) <= tolerance:
            matched += 1
            i += 1
            j += 1
        elif predicted_y[j] < truth_y[i]:
            j += 1
        else:
            i += 1
    return matched, len(predicted_y)-matched, len(truth_y)-matched


def score(truth, predicted, tolerance=4):
    endpoints = sorted({x for line in truth+predicted for x in line[:2]})
    total = [0., 0., 0.]
    for a, b in zip(endpoints, endpoints[1:]):
        x = (a+b)/2
        counts = match_counts([y for x0, x1, y in truth if x0 <= x < x1],
                              [y for x0, x1, y in predicted if x0 <= x < x1], tolerance)
        total = [t+(b-a)*c for t, c in zip(total, counts)]
    return total


def metrics(counts):
    tp, fp, fn = counts
    return {'truePositivePixelLength': tp, 'falsePositivePixelLength': fp,
            'falseNegativePixelLength': fn,
            'precision': tp/(tp+fp) if tp+fp else None,
            'recall': tp/(tp+fn) if tp+fn else None,
            'f1': 2*tp/(2*tp+fp+fn) if 2*tp+fp+fn else None}


def truth_lines(labels, truth, width, height, scale):
    sx, sy = scale
    return [(width/2+(line['minimumWorldX']-truth['cameraX'])*sx,
             width/2+(line['maximumWorldX']-truth['cameraX'])*sx,
             height/2-(line['worldY']-truth['cameraY'])*sy)
            for line in labels if line['kind'] == 'positive'
            and line['sceneName'] == truth['sceneName']]


def paired_observations(lines, frame):
    ex, ey = frame['error']
    return [(x0+ex, x1+ex, row-ey) for x0, x1, row in lines]


def atlas_projection(lines, frame, height):
    # Atlas is bottom-left and shares the camera pixel coordinate basis.
    # Use the expected true pose in that basis, never a per-line birth origin.
    cx, cy = frame['expected']
    return [(line['x0']-cx, line['x1']-cx, height-line['y']+cy) for line in lines]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_svg(path, panels, labels, scene, title):
    labels = [(l['minimumWorldX'], l['maximumWorldX'], l['worldY']) for l in labels
              if l['kind'] == 'positive' and l['sceneName'] == scene]
    all_lines = labels + [line for _, lines in panels for line in lines]
    if not all_lines:
        return
    x0, x1 = min(l[0] for l in all_lines), max(l[1] for l in all_lines)
    y0, y1 = min(l[2] for l in all_lines), max(l[2] for l in all_lines)
    scale = min(1400/max(1, x1-x0), 280/max(1, y1-y0))
    panel_height = 340
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="1500" '
           f'height="{60+panel_height*len(panels)}" viewBox="0 0 1500 {60+panel_height*len(panels)}">',
           '<rect width="100%" height="100%" fill="#101722"/>',
           f'<text x="25" y="28" fill="white" font-family="sans-serif" font-size="18">{html.escape(title)}</text>',
           '<text x="25" y="49" fill="#b8c8dc" font-family="sans-serif" font-size="13">Green: human floor. Blue: observations. Same detections and frames; no line fusion.</text>']
    for i, (name, lines) in enumerate(panels):
        top = 60+i*panel_height
        out.append(f'<text x="25" y="{top+20}" fill="white" font-family="sans-serif" font-size="16">{html.escape(name)}</text>')
        for color, opacity, width, group in [('#68aaff', .08, .8, lines), ('#62ec9d', 1, 1.5, labels)]:
            # Draw identical spatial samples, retaining motion smear and duplicates.
            for a, b, y in group:
                px0, px1, py = 35+(a-x0)*scale, 35+(b-x0)*scale, top+40+(y1-y)*scale
                out.append(f'<path d="M{px0:.2f},{py:.2f}H{px1:.2f}" stroke="{color}" '
                           f'stroke-width="{width}" opacity="{opacity}"/>')
    out.append('</svg>')
    path.write_text('\n'.join(out))


def diagnose(audit_directory, replay_path, labels_path, tolerance=4):
    run = json.loads(replay_path.read_text())
    labels = json.loads(labels_path.read_text())['lines']
    frames, coverage = exact_frames(run)
    by_timestamp = collections.defaultdict(list)
    for frame in frames:
        by_timestamp[frame['timestamp']].append(frame)
    totals = {key: [0., 0., 0.] for key in ('hackerObservations', 'gameplayObservations', 'persistentGameplay')}
    rejected = collections.Counter()
    rows, panels = [], collections.defaultdict(lambda: [[], []])
    signatures = collections.Counter()
    # Raw documents affect reproducibility as well as the frozen path/labels.
    audit_hash = hashlib.sha256()
    for path in sorted(audit_directory.glob('*.json')):
        raw = path.read_bytes()
        audit = json.loads(raw)
        matches = by_timestamp.get(audit['timestamp'], [])
        if len(matches) != 1:
            rejected['auditWithoutUniqueExactTrackingSample'] += 1
            continue
        frame = matches[0]
        width, height = audit['width'], audit['height']
        if (width, height) != (640, 360):
            rejected['unsupportedCaptureSize'] += 1
            continue
        audit_hash.update(path.name.encode()+raw)
        exclusions = audit.get('exclusions', [])
        expected = visible(truth_lines(labels, frame['truth'], width, height, frame['scale']),
                           width, height, exclusions)
        # A scene without positive labels is not implicitly fully reviewed.
        if not any(l['kind'] == 'positive' and l['sceneName'] == frame['truth']['sceneName'] for l in labels):
            rejected['unlabeledScene'] += 1
            continue
        detected = visible([(l['x0'], l['x1']+1, l['row']) for l in audit['lines']],
                           width, height, exclusions)
        # Score both placements in the same true viewport. A displaced span
        # outside it may overlap legitimate offscreen floor, so it cannot be
        # called a false positive against clipped visible annotations.
        unbounded_paired = paired_observations(detected, frame)
        paired = visible(unbounded_paired, width, height, exclusions)
        persistent = visible(atlas_projection(audit['atlasLines'], frame, height),
                             width, height, exclusions)
        predictions = {'hackerObservations': detected, 'gameplayObservations': paired,
                       'persistentGameplay': persistent}
        row = {'audit': path.name, 'unityFrame': frame['unityFrame'], 'offset': frame['offset'],
               'epoch': frame['epoch'], 'cameraErrorXY': frame['error'],
               'poseVerified': frame['tracking']['poseVerified'],
               'atlasLineIDs': [l['id'] for l in audit['atlasLines']]}
        for name, lines in predictions.items():
            counts = score(expected, lines, tolerance)
            totals[name] = [x+y for x, y in zip(totals[name], counts)]
            row[name] = metrics(counts)
        rows.append(row)
        signature = (tuple(audit.get('tuning', [])), audit.get('surfaceArchitecture', 'legacy'))
        signatures[str(signature)] += 1
        sx, sy = frame['scale']
        cx, cy = frame['truth']['cameraX'], frame['truth']['cameraY']
        for panel, lines in zip(panels[frame['truth']['sceneName']], (detected, unbounded_paired)):
            panel.extend((cx+(a-width/2)/sx, cx+(b-width/2)/sx, cy+(height/2-y)/sy)
                         for a, b, y in lines)
    return {'schemaVersion': 1, 'replay': str(replay_path), 'audit': str(audit_directory),
            'provenance': {'replaySHA256': digest(replay_path), 'labelsSHA256': digest(labels_path),
                           'matchedAuditSHA256': audit_hash.hexdigest(),
                           'detectorSignatures': dict(signatures)},
            'method': 'Identical recorded semantic spans; only the camera transform changes.',
            'limitations': ['Positive labels assumed complete within the labeled scene; unmarked floor counts as negative.',
                           'Both placements scored in the same true viewport; runtime masks excluded before and after projection.',
                           'Persistent score is current true viewport only, with a single fixed origin per epoch.',
                           'Paired observations are diagnostic maps without fusion, not a replacement persistent mapper.',
                           'Pixel lengths summed across frames, not unique room-wide floor length.'],
            'verticalTolerancePixels': tolerance, 'coverage': coverage,
            'auditRejected': dict(rejected), 'scoredFrames': len(rows),
            'scores': {key: metrics(value) for key, value in totals.items()},
            'camera': summarize_exact(run), 'frames': rows}, panels, labels


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audit', type=Path, required=True)
    parser.add_argument('--replay', type=Path, required=True)
    parser.add_argument('--labels', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--vertical-tolerance', type=float, default=4)
    args = parser.parse_args()
    if not math.isfinite(args.vertical_tolerance) or args.vertical_tolerance < 0:
        parser.error('vertical tolerance must be finite and nonnegative')
    report, panels, labels = diagnose(args.audit, args.replay, args.labels, args.vertical_tolerance)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2)+'\n')
    for scene, (hacker, gameplay) in panels.items():
        safe_scene = ''.join(c if c.isalnum() or c in '-_' else '_' for c in scene)
        write_svg(args.output.with_name(args.output.stem+'-'+safe_scene+'.svg'),
                  [('Hacker camera', hacker), ('Gameplay camera', gameplay)], labels, scene, args.replay.name)
    print(json.dumps({key: report[key] for key in ('scoredFrames', 'scores', 'auditRejected')}, indent=2))


if __name__ == '__main__':
    main()
