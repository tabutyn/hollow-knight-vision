#!/usr/bin/env python3
"""Score a production image replay after tracking has finished.

Reference telemetry is read only here, never by the image-replay tracker.
Origins follow independent scene/projection identities and survive visual resets.
"""
import argparse
import bisect
import datetime
import hashlib
import json
import math
from pathlib import Path

from camera_diagnostics import distribution, exact_frames, summarize_exact, truth_index, select_truth


def read_json(path):
    return json.loads(Path(path).read_text())


def fingerprint(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def archive_coverage(report, manifest):
    summary = manifest['summary']
    selected = report.get('selectedFrameCount', report['frames'])
    return {'archiveFrames': len(manifest['frames']),
            'archiveSavedImages': summary['savedFrameCount'],
            'archiveComplete': summary['complete'],
            'selectedFrames': selected, 'replayedImages': report['frames'],
            'missingSelectedFrameIndices': report.get('missingFrameIndices', []),
            'selectedImageCoverage': report['frames'] / selected if selected else None,
            'note': 'Unavailable images are explicit gaps. They supply neither predicted poses nor successful tracking samples.'}


def local_measurement_availability(samples, selected_count, ground_only=False):
    """Local image support is availability, never proof of global accuracy."""
    def supported(sample):
        if ground_only:
            return sample.get('poseVerified') is True and sample.get('hasConfirmedGround') is True
        return sample.get('island', {}).get('measured') is True

    measured = sum(supported(sample) for sample in samples)
    spans, start, last, first = [], None, None, None
    capture_gaps = []
    for sample in samples:
        timestamp = sample.get('captureTimestamp', sample.get('offset'))
        if not isinstance(timestamp, (int, float)) or not math.isfinite(timestamp):
            continue
        if first is None:
            first = timestamp
        if last is not None and timestamp-last > .1:
            capture_gaps.append(timestamp-last)
        if supported(sample):
            if start is not None:
                spans.append(dict(startSeconds=start-first, endSeconds=timestamp-first,
                                  durationSeconds=timestamp-start, recovered=True))
                start = None
        elif start is None:
            start = timestamp
        last = timestamp
    if start is not None and last is not None:
        spans.append(dict(startSeconds=start-first, endSeconds=last-first,
                          durationSeconds=last-start, recovered=False))
    return dict(publishedFrames=len(samples), selectedFrames=selected_count,
                locallyMeasuredFrames=measured,
                locallyMeasuredSelectedFramePercent=100*measured/selected_count if selected_count else None,
                unavailableImageFrames=selected_count-len(samples),
                unmeasuredOutputSpans=len(spans),
                spanDurationSeconds=distribution([span['durationSeconds'] for span in spans]),
                longestSpans=sorted(spans, key=lambda span: span['durationSeconds'], reverse=True)[:20],
                captureIntervalsOver100ms=distribution(capture_gaps),
                limits='Local ground or calibrated two-axis scenery support only; global placement and independent error remain separate. Spans include blackouts and time without captures until a measured output returns; long capture intervals are explicit.')


def motion_diagnostics(frames):
    """Displacement error on continuous exact-reference spans, without refitting.

    Constant unknown room placement cancels in a displacement difference. The
    fixed-origin camera score remains unchanged; large reference steps remain
    included here as well, with a separate count to make entry transients visible.
    """
    scenes, steps, spans = {}, [], []
    span = []
    for frame in frames:
        scene = frame['truth'].get('sceneName', 'unknown')
        scenes.setdefault(scene, []).append(frame)
        if span and (frame['epoch'] != span[-1]['epoch']
                     or not 0 < frame['timestamp']-span[-1]['timestamp'] <= .1):
            spans.append(span)
            span = []
        if span:
            prior = span[-1]
            reference = math.dist(prior['expected'], frame['expected'])
            residual = math.dist(prior['error'], frame['error'])
            steps.append(dict(offset=frame['offset'], scene=scene,
                              intervalSeconds=frame['timestamp']-prior['timestamp'],
                              referenceStepPixels=reference, residualPixels=residual,
                              poseSource=frame['tracking'].get('poseSource')))
        span.append(frame)
    if span:
        spans.append(span)
    windows = {}
    for duration in (.25, 1., 5.):
        residuals = []
        for values in spans:
            times = [f['timestamp'] for f in values]
            for index, frame in enumerate(values):
                target = frame['timestamp']-duration
                i = bisect.bisect_left(times, target, 0, index)
                candidates = values[max(0, i-1):min(index, i+1)]
                prior = min(candidates, key=lambda f: abs(f['timestamp']-target), default=None)
                if prior is not None and abs(prior['timestamp']-target) <= .025:
                    residuals.append(math.dist(prior['error'], frame['error']))
        windows[str(duration)] = distribution(residuals)
    return dict(continuousSpanCount=len(spans), adjacentPairs=len(steps),
                adjacentDisplacementErrorPixels=distribution([s['residualPixels'] for s in steps]),
                largeReferenceStepsOver64Pixels=sum(s['referenceStepPixels'] > 64 for s in steps),
                largestDisplacementErrors=sorted(steps, key=lambda s: s['residualPixels'], reverse=True)[:20],
                windowDisplacementErrorPixels=windows,
                scenes={name: dict(samples=len(values),
                    fixedOriginErrorPixels=distribution([f['errorPixels'] for f in values]),
                    adjacentDisplacementErrorPixels=distribution([
                        s['residualPixels'] for s in steps if s['scene'] == name]))
                    for name, values in scenes.items()},
                note='No new reference origins or trajectory fit. Windows never cross reference epochs or exact-reference gaps over 100 ms. Overall camera errors retain room placement and entry transients.')


def point_coordinates(point):
    """Windows persists AtlasPoint as an object, while Swift encodes CGPoint as an array."""
    return (point['x'], point['y']) if isinstance(point, dict) else point


def screenshot_errors(snapshot, frames, capture_origin, world_origin, world_uses_capture_uptime=False):
    """Join persisted observations to the exact capture time, with no refitting."""
    ordered = sorted(frames, key=lambda f: f['timestamp'])
    times = [f['timestamp'] for f in ordered]
    rows, missing = [], 0
    ownership = {member['observationID']: (island, member)
                 for island in snapshot.get('trackingIslands', []) for member in island['members']}
    for observation in snapshot['observations']:
        capture_time = observation.get('captureTimestamp')
        if capture_time is None:
            capture_time = observation['timestamp'] if world_uses_capture_uptime else (
                observation['timestamp'] - world_origin + capture_origin)
        i = bisect.bisect_left(times, capture_time)
        candidates = ordered[max(0, i-1):min(len(ordered), i+1)]
        match = min(candidates, key=lambda f: abs(f['timestamp']-capture_time), default=None)
        # Date reference seconds have lower float precision than capture uptime.
        if match is None or abs(match['timestamp']-capture_time) > .000002:
            missing += 1
            continue
        pose = point_coordinates(observation['optimizedPose'])
        error = [pose[axis]-match['expected'][axis] for axis in (0, 1)]
        island, member = ownership.get(observation['id'], ({}, {}))
        rows.append({'observationID': observation['id'], 'offset': match['offset'],
                     'roomID': observation.get('roomID', observation.get('roomId')), 'errorXY': error,
                     'rawErrorPixels': math.dist(point_coordinates(observation['rawPose']), match['expected']) if 'rawPose' in observation else None,
                     'errorPixels': math.hypot(*error), 'islandID': island.get('id'),
                     'placement': island.get('placement', 'legacyUnknown'),
                     'expectedPose': match['expected'], 'referenceEpoch': match.get('epoch'),
                     'sceneName': match.get('truth', {}).get('sceneName'),
                     'measured': member.get('measured', False)})
    return {'total': len(snapshot['observations']), 'matched': len(rows),
            'missingExactReference': missing,
            'errorPixels': distribution([r['errorPixels'] for r in rows]),
            'globallyPlacedMeasuredErrorPixels': distribution([r['errorPixels'] for r in rows
                if r['measured'] and r['placement'] in ('reference', 'connected')]),
            'estimatedOrUnmeasuredErrorPixels': distribution([r['errorPixels'] for r in rows
                if not r['measured'] or r['placement'] not in ('reference', 'connected')]),
            'rows': rows}


def loop_constraint_errors(snapshot, screenshots):
    """Score accepted edges against independent poses, with no island realignment."""
    rows = {row['observationID']: row for row in screenshots['rows']}
    keys = {key['id']: key.get('observationID', key.get('observationId')) for key in snapshot.get('keyframes', [])}
    edges = []
    for edge in snapshot.get('loopClosureEdges', []):
        first, second = rows.get(keys.get(edge.get('fromKeyframeID', edge.get('fromKeyframeId')))), rows.get(keys.get(edge.get('toKeyframeID', edge.get('toKeyframeId'))))
        result = {'id': edge['id'], 'support': edge['support'], 'errorPixels': None}
        if not first or not second or first['referenceEpoch'] is None or second['referenceEpoch'] is None:
            result['status'] = 'missing-reference'
        elif first['referenceEpoch'] != second['referenceEpoch']:
            result.update(status='different-reference-frames', fromScene=first['sceneName'],
                          toScene=second['sceneName'])
        else:
            expected = [second['expectedPose'][axis]-first['expectedPose'][axis] for axis in (0, 1)]
            error = [edge['deltaX']-expected[0], edge['deltaY']-expected[1]]
            result.update(status='evaluated', errorXY=error, errorPixels=math.hypot(*error),
                          fromSeconds=first['offset'], toSeconds=second['offset'])
        edges.append(result)
    return {'edges': edges,
            'errorPixels': distribution([e['errorPixels'] for e in edges if e['errorPixels'] is not None]),
            'missingReference': sum(e['status'] == 'missing-reference' for e in edges),
            'differentReferenceFrames': sum(e['status'] == 'different-reference-frames' for e in edges)}


def validate_corrected_world(baseline, corrected):
    fields = ('id', 'sourceObservationID', 'sourceObservationId', 'timestamp', 'captureTimestamp', 'rawPose', 'roomID', 'roomId',
              'sourceWidth', 'sourceHeight', 'solveWidth', 'groundGeometry', 'observedFloorCandidates',
              'observedPlayerBounds')
    immutable = lambda world: [{key: o.get(key) for key in fields} for o in world['observations']]
    if immutable(baseline) != immutable(corrected):
        raise ValueError('Corrected world changed immutable input observations')


def island_diagnostics(frames, snapshot):
    lifecycle, placed, estimated, missing = {}, [], [], 0
    for frame in frames:
        island = frame['tracking'].get('island')
        if not island:
            missing += 1
            estimated.append(frame['errorPixels'])
            continue
        known = island['measured'] and island['placement'] in ('reference', 'connected')
        (placed if known else estimated).append(frame['errorPixels'])
        identity = island['id']
        if identity not in lifecycle:
            lifecycle[identity] = {'id': identity, 'roomID': island['roomID'],
                'firstCaptureSeconds': frame['offset'], 'lastCaptureSeconds': frame['offset'],
                'captures': 0, 'placement': island['placement'],
                'originUncertainty': island['uncertainty'], 'breakReason': island['breakReason']}
        lifecycle[identity]['lastCaptureSeconds'] = frame['offset']
        lifecycle[identity]['captures'] += 1
    return {'recordedIslands': list(lifecycle.values()),
            'persistedIslands': len(snapshot.get('trackingIslands', [])),
            'missingFrameOwnership': missing,
            'globallyPlacedMeasuredCameraErrorPixels': distribution(placed),
            'estimatedOrUnmeasuredCameraErrorPixels': distribution(estimated),
            'note': 'Island origin uncertainty is stored separately from local pose confidence; disconnected errors remain reported and are never counted as successful global localization.'}


def fixed_origin_frames(frames, coverage, origins):
    """Keep origins fixed even when a new tracker reports previously missing frames."""
    adjusted, rejected = [], dict(coverage['rejected'])
    for frame in frames:
        truth = frame['truth']
        identity = [truth.get('sessionID'), truth.get('sceneName'),
                    truth.get('projectionPixelWidth'), truth.get('projectionPixelHeight')]
        origin = next((o for o in origins if o['identity'] == identity
                       and all(abs(frame['scale'][axis]/o['scale'][axis]-1) <= .001
                               for axis in (0, 1))), None)
        if origin is None:
            rejected['missingFixedReferenceOrigin'] = rejected.get('missingFixedReferenceOrigin', 0)+1
            continue
        expected = [origin['publishedOrigin'][axis]
                    +(truth['cameraX' if axis == 0 else 'cameraY']-origin['worldOrigin'][axis])*frame['scale'][axis]
                    for axis in (0, 1)]
        error = [frame['published'][axis]-expected[axis] for axis in (0, 1)]
        adjusted.append(dict(frame, expected=expected, error=error, errorPixels=math.hypot(*error),
                             epoch=origin['epoch']))
    return adjusted, dict(coverage, matchedSamples=len(adjusted), rejected=rejected, epochs=origins)


def load_fixed_origins(path, reference, reference_hash):
    baseline = read_json(path)
    if baseline['sourcePathID'].lower() != reference['id'].lower() or baseline['referencePathSHA256'] != reference_hash:
        raise ValueError('Fixed origins belong to a different reference recording')
    index, origins = truth_index(reference), []
    for epoch in baseline['coverage']['epochs']:
        truth, reason = select_truth(index, epoch['unityFrame'])
        if reason or truth is None:
            raise ValueError('Fixed origin lacks an unambiguous reference frame')
        origins.append(dict(epoch, identity=[truth.get('sessionID'), truth.get('sceneName'),
                            truth.get('projectionPixelWidth'), truth.get('projectionPixelHeight')]))
    return origins


def score(reference_path, replay_dir, corrected_world=None, reference_origins=None):
    reference_path, replay_dir = Path(reference_path), Path(replay_dir)
    report = read_json(replay_dir/'report.json')
    reference = read_json(reference_path)
    if reference['id'].lower() != report['sourcePathID'].lower():
        raise ValueError('Reference recording does not match replay sourcePathID')
    output_path, snapshot_path = Path(report['outputPath']), Path(report['worldSnapshotPath'])
    output, snapshot = read_json(output_path), read_json(snapshot_path)
    baseline_snapshot_path = snapshot_path
    if corrected_world is not None:
        corrected = read_json(corrected_world)
        validate_corrected_world(snapshot, corrected)
        snapshot, snapshot_path = corrected, Path(corrected_world)
    if output.get('groundTruthSamples'):
        raise ValueError('Visual tracker output unexpectedly contains reference telemetry')
    manifest_path = reference_path.parent/reference['visualFrameArchive']['directoryName']/'manifest.json'
    manifest = read_json(manifest_path)
    first = manifest['frames'][0]
    if any(f['width'] != first['width'] or f['height'] != first['height']
           or f['solveWidth'] != first['width'] for f in manifest['frames']):
        raise ValueError('This scorer requires one image/solve scale throughout the recording')
    # Only the post-hoc scorer receives this reference array.
    joined = dict(output, groundTruthSamples=reference.get('groundTruthSamples', []))
    frames, coverage = exact_frames(joined, first['width'], first['height'])
    if reference_origins is not None:
        origins = load_fixed_origins(reference_origins, reference, fingerprint(reference_path))
        frames, coverage = fixed_origin_frames(frames, coverage, origins)
    camera = summarize_exact(joined, first['width'], first['height'], frames_and_coverage=(frames, coverage))
    world_uses_capture_uptime = report.get('worldSnapshotTimestampDomain') == 'capture-uptime'
    capture_origin = world_origin = 0
    if not world_uses_capture_uptime:
        capture_origin = first['capturedAt'] - reference['coarseMotionSamples'][0]['offset']
        world_origin = datetime.datetime.fromisoformat(reference['createdAt'].replace('Z', '+00:00')).timestamp()-978307200
    screenshots = screenshot_errors(snapshot, frames, capture_origin, world_origin, world_uses_capture_uptime)
    result = {'schemaVersion': 1, 'sourcePathID': report['sourcePathID'],
              'pixels': [first['width'], first['height']],
              'referencePathSHA256': fingerprint(reference_path),
              'manifestSHA256': fingerprint(manifest_path),
              'outputSHA256': fingerprint(output_path), 'worldSHA256': fingerprint(snapshot_path),
              'baselineWorldSHA256': fingerprint(baseline_snapshot_path),
              'referenceOriginsSHA256': fingerprint(reference_origins) if reference_origins else None,
              'visualDigest': report['visualDigest'], 'coverage': coverage,
              'archiveCoverage': archive_coverage(report, manifest),
              'localMeasurementAvailability': local_measurement_availability(
                  output['trackingSamples'], report.get('selectedFrameCount', report['frames']),
                  ground_only=world_uses_capture_uptime),
              'camera': camera, 'persistedScreenshots': screenshots,
              'motion': motion_diagnostics(frames),
              'processing': {'offlineFramesPerSecond': report['framesPerSecond'],
                  'groundMilliseconds': distribution([f['tracking']['groundTrackingMilliseconds'] for f in frames
                      if f['tracking'].get('groundTrackingMilliseconds') is not None]),
                  'sceneMilliseconds': distribution([f['tracking']['sceneMotionMilliseconds'] for f in frames
                      if f['tracking'].get('sceneMotionMilliseconds') is not None]),
                  'fallPredictionSamples': sum(f['tracking'].get('poseSource') == 'fallPrediction' for f in frames)},
              'islands': island_diagnostics(frames, snapshot),
              'acceptedLoopClosures': len(snapshot['loopClosureEdges']),
              'loopConstraints': loop_constraint_errors(snapshot, screenshots),
              'notes': ['Scene/projection reference origins are fixed across recoveries and revisits.',
                        'Initial placement between different game scenes is unobservable in this camera score; doorway floors require a separate measurement.',
                        'poseVerified describes local ground matching, not proven global island placement.',
                        'All exactly joined camera/screenshot errors remain in the overall score; estimated placement is also reported separately.']}
    return result, frames


def plot(result, frames, target):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    figure, axes = plt.subplots(5, 1, figsize=(13, 12), sharex=True, constrained_layout=True)
    t = [f['offset'] for f in frames]
    axes[0].plot(t, [f['errorPixels'] for f in frames], lw=.7, label='Published camera error')
    axes[0].axhline(6, color='green', ls='--', lw=.8, label='6 px target for confident segments')
    axes[0].set_yscale('symlog', linthresh=6)
    axes[0].set_ylim(bottom=0)
    axes[0].set_ylabel('Error (px)'); axes[0].legend(loc='upper left')
    source_names = sorted({f['tracking'].get('poseSource', 'unknown') for f in frames})
    indices = {name: i for i, name in enumerate(source_names)}
    axes[1].scatter(t, [indices[f['tracking'].get('poseSource', 'unknown')] for f in frames], s=1)
    axes[1].set_yticks(range(len(source_names)), source_names); axes[1].set_ylabel('Pose source')
    island_frames = [f for f in frames if f['tracking'].get('island')]
    for placement, color in [('reference', 'tab:blue'), ('connected', 'tab:green'), ('estimated', 'tab:orange')]:
        selected = [f for f in island_frames if f['tracking']['island']['placement'] == placement]
        axes[2].scatter([f['offset'] for f in selected],
            [f['tracking']['island']['id'] for f in selected], s=2, color=color, label=placement)
    axes[2].set_ylabel('Island ID / placement')
    if island_frames: axes[2].legend(loc='upper left')
    # Origin uncertainty is distinct from local fit quality. Unknown stays
    # unbounded; do not silently plot absent bounds at zero.
    for axis_name, color in [('x', 'tab:blue'), ('y', 'tab:orange')]:
        values = [f['tracking'].get('island', {}).get('uncertainty', {}).get(axis_name) for f in frames]
        finite = [(time, value) for time, value in zip(t, values) if value is not None]
        unknown = [time for time, value in zip(t, values) if value is None]
        if finite:
            axes[3].scatter([v[0] for v in finite], [v[1] for v in finite], s=1,
                            color=color, label=f'{axis_name.upper()} origin tolerance')
        axes[3].scatter(unknown, [1 if axis_name == 'x' else 0.9] * len(unknown),
                        transform=axes[3].get_xaxis_transform(), marker='|', s=3, color=color,
                        label=f'{axis_name.upper()} unknown / unbounded')
    axes[3].set_ylabel('Origin uncertainty\n(px; unknown at top)')
    axes[3].set_ylim(bottom=0)
    axes[3].legend(loc='center left', fontsize=7, ncol=2)
    rows = result['persistedScreenshots']['rows']
    raw = [r for r in rows if r.get('rawErrorPixels') is not None]
    if raw and any(abs(r['rawErrorPixels']-r['errorPixels']) > .001 for r in raw):
        axes[4].scatter([r['offset'] for r in raw], [r['rawErrorPixels'] for r in raw],
                        s=2, color='gray', alpha=.55, label='Original placement')
    axes[4].scatter([r['offset'] for r in rows], [r['errorPixels'] for r in rows], s=2,
                    label='Retained placement')
    for index, edge in enumerate(result.get('loopConstraints', {}).get('edges', [])):
        if 'toSeconds' in edge:
            axes[4].axvline(edge['toSeconds'], color='purple', lw=.6, alpha=.6,
                            label='Accepted visual loop' if index == 0 else None)
    axes[4].legend(loc='upper left')
    axes[4].axhline(12, color='green', ls='--', lw=.8)
    axes[4].set_ylabel('Saved screenshot\nerror (px)'); axes[4].set_xlabel('Recording time (seconds)')
    for epoch in result['coverage']['epochs'][1:]:
        match = next((f for f in frames if f['epoch'] == epoch['epoch']), None)
        if match:
            for axis in axes: axis.axvline(match['offset'], color='gray', ls=':', lw=.8)
    figure.suptitle('Fixed-reference image replay — all matched camera and screenshot errors retained')
    target = Path(target); target.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(target, dpi=160); plt.close(figure)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--replay', type=Path, required=True)
    parser.add_argument('--plot', type=Path)
    parser.add_argument('--corrected-world', type=Path)
    parser.add_argument('--reference-origins', type=Path,
                        help='Frozen baseline score.json; required for comparisons when capture coverage changes')
    args = parser.parse_args()
    result, frames = score(args.reference, args.replay, args.corrected_world, args.reference_origins)
    destination = (args.corrected_world.parent/'score.json' if args.corrected_world else args.replay/'score.json')
    destination.write_text(json.dumps(result, indent=2)+'\n')
    if args.plot: plot(result, frames, args.plot)
    print(json.dumps({'score': str(destination), 'coverage': result['coverage']['matchedSamples'],
                      'camera': result['camera']['errorPixels'],
                      'persistedScreenshots': result['persistedScreenshots']['errorPixels']}, indent=2))


if __name__ == '__main__':
    main()
