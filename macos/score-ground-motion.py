#!/usr/bin/env python3
"""Diagnose accepted ground motion separately from provisional map layout.

This is a secondary relative-motion score. It excludes fallback frames and never
replaces the all-published camera, map-placement, or coverage measurements.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path

from camera_diagnostics import exact_frames, finite, summarize_exact, truth_index, select_truth


def accepted_ground_trace(output, reference):
    accepted, excluded = [], {}
    for sample in output['trackingSamples']:
        reason = None
        if sample.get('poseSource') != 'ground':
            reason = 'notAcceptedGroundOutput'
        elif not (sample.get('poseVerified') and sample.get('hasConfirmedGround')
                  and any(sample.get(k, 0) > 0 for k in
                          ('localTextureSupport', 'inlierCount', 'globalMatchCount'))):
            reason = 'unmeasuredGround'
        elif not finite(sample.get('cameraX'), sample.get('cameraY')):
            reason = 'rawGroundPoseUnavailable'
        if reason:
            excluded[reason] = excluded.get(reason, 0)+1
        else:
            accepted.append(dict(sample, publishedCameraX=sample['cameraX'],
                                 publishedCameraY=sample['cameraY']))
    return dict(output, trackingSamples=accepted,
                groundTruthSamples=reference.get('groundTruthSamples', [])), dict(
        totalPublishedSamples=len(output['trackingSamples']), acceptedGroundSamples=len(accepted),
        excluded=excluded)


def identity(truth):
    return [truth.get('sessionID'), truth.get('sceneName'),
            truth.get('projectionPixelWidth'), truth.get('projectionPixelHeight')]


def same_capture_origins(frames, baseline_epochs, reference):
    """Use one identical observed capture per scene, never a fitted trajectory.

    Only the unknown initial translation changes with a run. Missing anchor
    captures remove that scene from this secondary score, with explicit coverage;
    a later convenient ground sample cannot silently become a replacement origin.
    """
    by_time = {frame['timestamp']: frame for frame in frames}
    index = truth_index(reference)
    origins, missing = [], []
    for epoch in baseline_epochs:
        truth, reason = select_truth(index, epoch['unityFrame'])
        frame = by_time.get(epoch['captureTimestamp'])
        if reason or frame is None or frame['unityFrame'] != epoch['unityFrame'] \
                or identity(frame['truth']) != identity(truth):
            missing.append(dict(scene=epoch['sceneName'], captureTimestamp=epoch['captureTimestamp']))
            continue
        origins.append(dict(epoch, identity=identity(truth), publishedOrigin=frame['published']))
    return origins, missing


def relative_frames(frames, coverage, origins):
    result, rejected = [], dict(coverage['rejected'])
    for frame in frames:
        origin = next((o for o in origins if o['identity'] == identity(frame['truth'])
                       and all(abs(frame['scale'][i]/o['scale'][i]-1) <= .001 for i in (0, 1))), None)
        if origin is None:
            rejected['missingSameCaptureGroundOrigin'] = rejected.get('missingSameCaptureGroundOrigin', 0)+1
            continue
        truth = frame['truth']
        expected = [origin['publishedOrigin'][i]
                    +(truth['cameraX' if i == 0 else 'cameraY']-origin['worldOrigin'][i])*frame['scale'][i]
                    for i in (0, 1)]
        error = [frame['published'][i]-expected[i] for i in (0, 1)]
        result.append(dict(frame, expected=expected, error=error, errorPixels=math.hypot(*error),
                           epoch=origin['epoch']))
    return result, dict(coverage, matchedSamples=len(result), rejected=rejected, epochs=origins)


def score(reference_path, replay_dir, baseline_path=None):
    reference_path, replay_dir = Path(reference_path), Path(replay_dir)
    reference_bytes = reference_path.read_bytes()
    reference = json.loads(reference_bytes)
    reference_hash = hashlib.sha256(reference_bytes).hexdigest()
    report = json.loads((replay_dir/'report.json').read_text())
    if report['sourcePathID'].lower() != reference['id'].lower():
        raise ValueError('Reference recording does not match replay')
    manifest = json.loads((reference_path.parent/reference['visualFrameArchive']['directoryName']/'manifest.json').read_text())
    first = manifest['frames'][0]
    if any(f['width'] != first['width'] or f['height'] != first['height']
           or f['solveWidth'] != f['width'] for f in manifest['frames']):
        raise ValueError('Raw ground units require one matching capture/solve scale')
    output = json.loads(Path(report['outputPath']).read_text())
    if output.get('groundTruthSamples'):
        raise ValueError('Replay output unexpectedly contains reference telemetry')
    trace, selection = accepted_ground_trace(output, reference)
    frames, coverage = exact_frames(trace, first['width'], first['height'])
    missing = []
    if baseline_path:
        baseline = json.loads(Path(baseline_path).read_text())
        if baseline['sourcePathID'].lower() != reference['id'].lower() \
                or baseline['referencePathSHA256'] != reference_hash:
            raise ValueError('Ground origins belong to a different recording')
        origins, missing = same_capture_origins(frames, baseline['coverage']['epochs'], reference)
        frames, coverage = relative_frames(frames, coverage, origins)
    else:
        by_index = {frame['trackingIndex']: frame for frame in frames}
        coverage['epochs'] = [dict(epoch, captureTimestamp=by_index[epoch['trackingIndex']]['timestamp'])
                              for epoch in coverage['epochs']]
    return dict(schemaVersion=1, sourcePathID=report['sourcePathID'], referencePathSHA256=reference_hash,
                selection=selection, coverage=coverage, missingAnchorCaptures=missing,
                ground=summarize_exact(trace, first['width'], first['height'],
                                       frames_and_coverage=(frames, coverage)),
                alignment='One translation from the same ground-supported capture per independent scene/projection; no trajectory fit or reanchoring on visual room/island changes or return trips.',
                limits='Accepted raw ground output only. Excludes fallback/held frames and provisional map translations. Read with full published-camera/map errors and explicit coverage.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference', type=Path, required=True)
    parser.add_argument('--replay', type=Path, required=True)
    parser.add_argument('--origins', type=Path)
    args = parser.parse_args()
    result = score(args.reference, args.replay, args.origins)
    target = args.replay/'ground-motion-score.json'
    target.write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(dict(score=str(target), selection=result['selection'],
                         missingAnchorCaptures=result['missingAnchorCaptures'],
                         groundError=result['ground']['errorPixels']), indent=2))
