#!/usr/bin/env python3
"""Score recorded pixel-only ground detections against exact-frame Hacker truth.

No images are decoded here. The detector audit must already have checked their
hashes. Human geometry and camera truth enter only this independent scorer.
"""
import argparse
import collections
import hashlib
import importlib.util
import json
import math
from pathlib import Path

from camera_diagnostics import distribution, select_truth, truth_index

_spec = importlib.util.spec_from_file_location(
    'ground_geometry_score', Path(__file__).with_name('diagnose-ground-camera.py'))
_geometry = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_geometry)


def terrain_counts(labels, truth, width, height, scale, exclusions, detected, tolerance):
    def projected_y(world_y):
        return height / 2 - (world_y - truth['cameraY']) * scale[1]

    scene = truth['sceneName']
    ground_ids = {line.get(key) for line in labels['lines']
                  if line['kind'] == 'positive' and line['sceneName'] == scene
                  for key in ('startCornerID', 'endCornerID')} - {None}
    spans, junctions = [], {}
    for wall in labels.get('walls', []):
        if wall['sceneName'] != scene:
            continue
        x = width / 2 + (wall['minimumWorldX'] - truth['cameraX']) * scale[0]
        first, last = projected_y(wall['worldY']), projected_y(wall['endWorldY'])
        spans.append((min(first, last), max(first, last), x))
        for key, y in [('startCornerID', first), ('endCornerID', last)]:
            if wall.get(key) in ground_ids:
                junctions[wall[key]] = (x, y)

    # Transposing both spans and masks lets walls use the same length-weighted
    # one-to-one overlap metric as floors.
    transposed = [(y, x, h, w) for x, y, w, h in exclusions]
    expected = _geometry.visible(spans, height, width, transposed)
    predictions = [(top, bottom + 1, x) for x, top, bottom in detected['walls']]
    predicted = _geometry.visible(predictions, height, width, transposed)

    def visible_point(point):
        x, y = point
        return 0 <= x < width and 0 <= y < height and not any(
            rx <= x < rx + w and ry <= y < ry + h for rx, ry, w, h in exclusions)

    references = list(filter(visible_point, junctions.values()))
    points = list(filter(visible_point, detected['corners']))
    pairs = []
    for i, point in enumerate(points):
        for j, reference in enumerate(references):
            distance = math.dist(point, reference)
            if distance <= tolerance * 2:
                pairs.append((distance, i, j))
    used_points, used_references = set(), set()
    for _, i, j in sorted(pairs):
        if i not in used_points and j not in used_references:
            used_points.add(i)
            used_references.add(j)
    matched = len(used_points)
    return {'wallSpans': _geometry.score(expected, predicted, tolerance),
            'corners': [matched, len(points) - matched, len(references) - matched]}


def corner_metrics(counts):
    matched, extra, missed = counts
    return {'matched': matched, 'unmatchedPredictions': extra, 'unmatchedReferences': missed,
            'precision': matched / (matched + extra) if matched + extra else None,
            'recall': matched / (matched + missed) if matched + missed else None}


def score(reference_path, audit_path, labels_path, tolerance=4):
    reference_path, audit_path, labels_path = map(Path, (reference_path, audit_path, labels_path))
    reference_bytes, audit_bytes, label_bytes = [p.read_bytes() for p in
                                                (reference_path, audit_path, labels_path)]
    reference, audit, label_document = map(json.loads, (reference_bytes, audit_bytes, label_bytes))
    reference_hash = hashlib.sha256(reference_bytes).hexdigest()
    if audit['sourcePathID'].lower() != reference['id'].lower() \
            or audit['recordingSHA256'] != reference_hash:
        raise ValueError('Detector audit does not belong to this recording')
    manifest_path = reference_path.parent / reference['visualFrameArchive']['directoryName'] / 'manifest.json'
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    if manifest['pathID'].lower() != reference['id'].lower():
        raise ValueError('Frame manifest does not belong to this recording')
    images = {frame['index']: frame for frame in manifest['frames']}
    telemetry = truth_index(reference)
    labels = label_document['lines']
    totals = {name: [0., 0., 0.] for name in
              ('semanticSpans', 'semanticEvidence', 'trackingSpans')}
    timeline = collections.defaultdict(lambda: {key: [0., 0., 0.] for key in totals})
    terrain_totals = collections.defaultdict(lambda: [0, 0, 0])
    rejected = collections.Counter()
    rows, seen = [], set()
    first = manifest['frames'][0]
    coarse = reference.get('coarseMotionSamples') or []
    start = first['capturedAt'] - (coarse[0]['offset'] if coarse else 0)
    for detected in audit['frames']:
        index = detected['index']
        image = images.get(index)
        if index in seen:
            raise ValueError('Duplicate detector audit frame')
        seen.add(index)
        if image is None or image.get('fileName') is None \
                or image.get('sha256') != detected['imageSHA256'] \
                or image['captureTimestamp'] != detected['captureTimestamp'] \
                or image.get('renderedGameFrame') != detected.get('renderedGameFrame') \
                or (image['width'], image['height']) != (detected['width'], detected['height']):
            raise ValueError('Detector audit frame/image association changed')
        truth, reason = select_truth(telemetry, image.get('renderedGameFrame'))
        if reason:
            rejected[reason] += 1
            continue
        if not truth.get('cameraAvailable'):
            rejected['cameraUnavailable'] += 1
            continue
        if not any(line['sceneName'] == truth['sceneName'] and line['kind'] == 'positive'
                   for line in labels):
            rejected['unlabeledScene'] += 1
            continue
        width, height = image['width'], image['height']
        scale = [truth['pixelsPerWorldUnitX'] * width / truth['projectionPixelWidth'],
                 truth['pixelsPerWorldUnitY'] * height / truth['projectionPixelHeight']]
        exclusions = detected['exclusions']
        expected = _geometry.visible(_geometry.truth_lines(labels, truth, width, height, scale),
                                     width, height, exclusions)

        def spans(key, evidence=False):
            lines = []
            for line in detected[key]:
                ranges = line['evidence'] if evidence else [[line['x0'], line['x1']]]
                lines.extend((a, b + 1, line['row']) for a, b in ranges)
            return _geometry.visible(lines, width, height, exclusions)

        predictions = {'semanticSpans': spans('semanticLines'),
                       'semanticEvidence': spans('semanticLines', evidence=True),
                       'trackingSpans': spans('trackingLines')}
        offset = image['capturedAt'] - start
        row = {'index': index, 'offset': offset, 'unityFrame': image['renderedGameFrame'],
               'referenceSpans': len(expected),
               'referencePixelLength': sum(b-a for a, b, _ in expected)}
        for name, prediction in predictions.items():
            counts = _geometry.score(expected, prediction, tolerance)
            totals[name] = [a+b for a, b in zip(totals[name], counts)]
            timeline[int(offset)][name] = [a+b for a, b in zip(timeline[int(offset)][name], counts)]
            row[name] = _geometry.metrics(counts)
        if 'walls' in detected and 'walls' in label_document:
            for key, counts in terrain_counts(label_document, truth, width, height, scale, exclusions,
                                               detected, tolerance).items():
                terrain_totals[key] = [a+b for a, b in zip(terrain_totals[key], counts)]
                row[key] = (corner_metrics if key == 'corners' else _geometry.metrics)(counts)
        rows.append(row)
    if len(audit['frames']) != audit['processedFrames']:
        raise ValueError('Detector audit processed count differs from its frames')
    return {
        'schemaVersion': 1, 'sourcePathID': reference['id'],
        'provenance': {'recordingSHA256': reference_hash,
                       'auditSHA256': hashlib.sha256(audit_bytes).hexdigest(),
                       'labelsSHA256': hashlib.sha256(label_bytes).hexdigest(),
                       'manifestSHA256': hashlib.sha256(manifest_bytes).hexdigest()},
        'coverage': {'archiveFrames': len(images), 'selectedFrames': audit['selectedFrames'],
                     'processedFrames': audit['processedFrames'], 'scoredFrames': len(rows),
                     'rejected': dict(rejected), 'missingFreshMaskInputs': audit['missingFreshMaskInputs'],
                     'exactMaskInputs': audit['exactMaskInputs'], 'frameStride': audit['frameStride']},
        'verticalTolerancePixels': tolerance,
        'terrain': {key: (corner_metrics if key == 'corners' else _geometry.metrics)(counts)
                    for key, counts in terrain_totals.items()},
        'cornerRadiusPixels': tolerance * 2,
        'buildConfiguration': audit.get('buildConfiguration', 'unspecified'),
        'scores': {key: _geometry.metrics(value) for key, value in totals.items()},
        'detectorMilliseconds': distribution([frame['detectorMilliseconds'] for frame in audit['frames']]),
        'timeline': [{'second': second, **{key: _geometry.metrics(value)
                                         for key, value in scores.items()}}
                     for second, scores in sorted(timeline.items())],
        'frames': rows,
        'limitations': [
            'Exact rendered-frame camera projection only; no published visual pose enters the detector score.',
            'Length weighted within the capture viewport; archived runtime masks excluded.',
            'Nearby duplicate rows compete one-to-one for reference floor and count as false positives.',
            'Unlabeled scenes are skipped. Positive labels are assumed complete in a labeled scene.',
            'Physical foreground occlusion is not annotated: reference floor may exist behind opaque scenery.',
            'Semantic spans include inferred occlusion bridges; semantic evidence scores only measured edge ranges.',
            'Detector timings describe this audit build and exclude decoding; debug timings are not live-app latency.',
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--audit', type=Path, required=True)
    parser.add_argument('--labels', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--vertical-tolerance', type=float, default=4)
    args = parser.parse_args()
    report = score(args.reference, args.audit, args.labels, args.vertical_tolerance)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'output': str(args.output), 'coverage': report['coverage'],
                      'scores': report['scores'], 'detectorMilliseconds': report['detectorMilliseconds']}, indent=2))


if __name__ == '__main__':
    main()
