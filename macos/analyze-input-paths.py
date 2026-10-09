#!/usr/bin/env python3
"""Compare one human input path with replay traces produced from it."""

import argparse
import base64
import bisect
import binascii
import json
import math
import statistics
from pathlib import Path

from camera_diagnostics import low_resolution_motion_demand, summarize_exact


def percentile(values, percent):
    if not values:
        return None
    ordered = sorted(values)
    index = (len(ordered) - 1) * percent / 100
    lower = math.floor(index)
    upper = math.ceil(index)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] * (upper - index) + ordered[upper] * (index - lower)


def rounded(value, digits=3):
    return None if value is None else round(value, digits)


def load(path):
    with path.open() as stream:
        return json.load(stream)


def pose_samples(path, verified_only=False, published=False, capture_time=False):
    truth = path.get("groundTruthSamples") or []
    origin = (truth[0]["receivedTimestamp"] - truth[0]["offset"]
              if truth and "receivedTimestamp" in truth[0] else None)
    result = []
    for sample in path["trackingSamples"]:
        x = sample.get("publishedCameraX") if published else sample.get("cameraX")
        y = sample.get("publishedCameraY") if published else sample.get("cameraY")
        if published and (x is None or y is None):
            x, y = sample.get("cameraX"), sample.get("cameraY")
        if x is None or y is None:
            continue
        if verified_only and not sample["poseVerified"]:
            continue
        timestamp = sample["offset"]
        if capture_time:
            timestamp = sample.get("captureOffset")
            if timestamp is None:
                timestamp = (sample["captureTimestamp"] - origin
                             if origin is not None else sample["offset"])
        result.append((timestamp, x, y))
    return result


def coarse_motion_proxy(path):
    """Adapt capture-rate coarse samples to exact-frame camera diagnostics."""
    samples = path.get("coarseMotionSamples") or []
    if not samples:
        return None
    proxy = dict(path)
    proxy["trackingSamples"] = [{
        "offset": sample["offset"],
        "captureOffset": sample["offset"],
        "captureTimestamp": sample["captureTimestamp"],
        "renderedGameFrame": sample.get("renderedGameFrame"),
        "publishedCameraX": sample["presentedCameraX"],
        "publishedCameraY": sample["presentedCameraY"],
        "poseSource": "coarseCaptureRate" if sample.get("isControlling") else "presentation",
        "poseVerified": False,
        "roomID": sample.get("roomID"),
        "atlasLineIDs": [],
    } for sample in samples]
    return proxy


def low_resolution_trace_summary(path):
    frames = path.get("lowResolutionFrames") or []
    if not frames:
        return None
    intervals = [
        (current["captureTimestamp"] - previous["captureTimestamp"]) * 1000
        for previous, current in zip(frames, frames[1:])
        if current["captureTimestamp"] > previous["captureTimestamp"]
    ]
    valid = 0
    evidence_bytes = 0
    for frame in frames:
        try:
            payload = base64.b64decode(frame.get("luma", ""), validate=True)
        except (binascii.Error, ValueError, TypeError):
            continue
        expected = frame.get("width", 0) * frame.get("height", 0)
        if expected > 0 and len(payload) == expected:
            valid += 1
            evidence_bytes += len(payload)
    duration = frames[-1]["offset"] - frames[0]["offset"]
    return {
        "frames": len(frames),
        "validFrames": valid,
        "evidenceBytes": evidence_bytes,
        "frameRate": rounded((len(frames) - 1) / duration if duration > 0 else None),
        "captureIntervalP50Milliseconds": rounded(percentile(intervals, 50)),
        "captureIntervalP95Milliseconds": rounded(percentile(intervals, 95)),
        "captureIntervalsOver100Milliseconds": sum(value > 100 for value in intervals),
        "groundLossFrames": sum(
            not frame.get("groundTrackingReliable", False) for frame in frames
        ),
        "roomIDs": sorted({frame.get("roomID") for frame in frames
                           if frame.get("roomID") is not None}),
    }


def ground_truth_camera_samples(path, output_width=640, output_height=360):
    """Place game-camera truth in the same connected atlas space as Hacker.

    Unity camera coordinates restart in every scene, so subtracting one raw
    origin makes every later room look like a huge camera error. Hacker keeps
    one anchor per playable scene. On first entry it joins the old and new
    camera coordinate systems at the Knight's projected doorway position, and
    on return it reuses the original scene anchor. Mirror that policy here so
    the stability report measures Vision against the atlas the user actually
    sees.

    A new scene whose Knight projection is still offscreen has no defensible
    placement yet. Hacker deliberately does not add those frames to its atlas;
    this diagnostic likewise leaves them unavailable instead of inventing a
    room offset.
    """
    anchors = {}
    last_playable = None
    result = []
    for recorded in path.get("groundTruthSamples") or []:
        sample = recorded["sample"]
        if not sample.get("cameraAvailable"):
            continue
        projection_width = sample.get("projectionPixelWidth") or 0
        projection_height = sample.get("projectionPixelHeight") or 0
        scale_x = sample.get("pixelsPerWorldUnitX") or 0
        scale_y = sample.get("pixelsPerWorldUnitY") or 0
        if projection_width <= 0 or projection_height <= 0 or scale_x <= 0 or scale_y <= 0:
            continue
        pixel_scale_x = scale_x * output_width / projection_width
        pixel_scale_y = scale_y * output_height / projection_height
        position = (
            sample["cameraX"] * pixel_scale_x,
            sample["cameraY"] * pixel_scale_y,
        )
        scene = sample.get("sceneName") or ""
        hero_point = None
        hero_x = sample.get("heroScreenX")
        hero_y = sample.get("heroScreenY")
        supplies_projection = hero_x is not None and hero_y is not None
        if sample.get("heroAvailable") and supplies_projection:
            candidate = (
                hero_x * output_width / projection_width,
                hero_y * output_height / projection_height,
            )
            if 0 <= candidate[0] <= output_width and 0 <= candidate[1] <= output_height:
                hero_point = candidate

        if scene in anchors:
            anchor = anchors[scene]
        else:
            can_initialize = sample.get("heroAvailable") and (
                hero_point is not None or not supplies_projection
            )
            if not can_initialize:
                continue
            if last_playable is None:
                origin = (0.0, 0.0)
            elif last_playable[1] is not None and hero_point is not None:
                previous_origin, previous_hero, _, _ = last_playable
                origin = (
                    previous_origin[0] + previous_hero[0] - hero_point[0],
                    previous_origin[1] + previous_hero[1] - hero_point[1],
                )
            else:
                previous_origin, _, velocity, facing_right = last_playable
                overlap_x = min(32, round(output_width * 0.1))
                overlap_y = min(24, round(output_height * 0.1))
                if abs(velocity[1]) > abs(velocity[0]) and abs(velocity[1]) > 0.05:
                    origin = (
                        previous_origin[0],
                        previous_origin[1] + (
                            output_height - overlap_y if velocity[1] > 0
                            else -output_height + overlap_y
                        ),
                    )
                else:
                    exits_right = velocity[0] > 0 if abs(velocity[0]) > 0.05 else facing_right
                    origin = (
                        previous_origin[0] + (
                            output_width - overlap_x if exits_right
                            else -output_width + overlap_x
                        ),
                        previous_origin[1],
                    )
            anchor = (position[0] - origin[0], position[1] - origin[1])
            anchors[scene] = anchor

        origin = (position[0] - anchor[0], position[1] - anchor[1])
        result.append((recorded["offset"], origin[0], origin[1]))
        if sample.get("heroAvailable") and (
            hero_point is not None or not supplies_projection
        ):
            last_playable = (
                origin,
                hero_point,
                (sample.get("velocityX") or 0, sample.get("velocityY") or 0),
                bool(sample.get("facingRight")),
            )
    return result


def state_spans(path, predicate):
    samples = path["trackingSamples"]
    if not samples:
        return []
    spans = []
    active_start = None
    for index, sample in enumerate(samples):
        end = samples[index + 1]["offset"] if index + 1 < len(samples) else path["duration"]
        active = predicate(sample)
        if active and active_start is None:
            active_start = sample["offset"]
        if active_start is not None and (not active or index + 1 == len(samples)):
            interval_end = sample["offset"] if not active else end
            spans.append((active_start, interval_end))
            active_start = None
    return spans


def nearest_pose(samples, timestamp, tolerance=0.075):
    if not samples:
        return None
    times = [sample[0] for sample in samples]
    index = bisect.bisect_left(times, timestamp)
    candidates = []
    if index < len(samples):
        candidates.append(samples[index])
    if index > 0:
        candidates.append(samples[index - 1])
    selected = min(candidates, key=lambda sample: abs(sample[0] - timestamp))
    return selected[1:] if abs(selected[0] - timestamp) <= tolerance else None


def summarize(source, run):
    samples = run["trackingSamples"]
    # The atlas consumes the published pose. The private ground pose can be a
    # rejected proposal and must not be mistaken for visible camera output.
    poses = pose_samples(run, published=True)
    verified_poses = pose_samples(run, verified_only=True, published=True)
    capture_poses = pose_samples(run, published=True, capture_time=True)
    capture_verified = pose_samples(run, verified_only=True, published=True, capture_time=True)
    first = poses[0][1:] if poses else (None, None)
    last = poses[-1][1:] if poses else (None, None)
    path_displacement = (
        math.hypot(last[0] - first[0], last[1] - first[1])
        if None not in first + last else None
    )
    original_distance = (
        math.hypot(last[0], last[1]) if None not in last else None
    )

    loss_spans = state_spans(run, lambda sample: not sample["poseVerified"])
    loss_intervals = [end - start for start, end in loss_spans]
    match_spans = state_spans(run, lambda sample: sample["globalMatchCount"] > 0)
    match_intervals = [end - start for start, end in match_spans]
    longest_loss = max(loss_spans, key=lambda span: span[1] - span[0], default=None)
    pose_steps = []
    for previous, current in zip(poses, poses[1:]):
        if current[0] - previous[0] <= 0.2:
            pose_steps.append((
                math.hypot(current[1] - previous[1], current[2] - previous[2]),
                current[0],
            ))
    largest_step = max(pose_steps, default=(0, None))
    corrections = [
        math.hypot(sample.get("globalCorrectionX") or 0, sample.get("globalCorrectionY") or 0)
        for sample in samples
        if sample.get("globalCorrectionX") is not None
        or sample.get("globalCorrectionY") is not None
    ]
    capture_intervals = [
        (current["captureTimestamp"] - previous["captureTimestamp"]) * 1000
        for previous, current in zip(samples, samples[1:])
        if 0 < current["captureTimestamp"] - previous["captureTimestamp"] < 1
    ]
    ground_tracking_times = [
        sample["groundTrackingMilliseconds"]
        for sample in samples
        if sample.get("groundTrackingMilliseconds") is not None
    ]

    timing_errors = []
    sequence_matches = len(source["events"]) == len(run["events"])
    if sequence_matches:
        for expected, actual in zip(source["events"], run["events"]):
            if (expected["button"], expected["transition"]) != (
                actual["button"], actual["transition"]
            ):
                sequence_matches = False
                break
            timing_errors.append(actual["offset"] - expected["offset"])

    source_verified = pose_samples(source, verified_only=True, published=True)
    source_start = pose_samples(source, published=True)[0][1:]
    run_start = poses[0][1:] if poses else (0, 0)
    trajectory_errors = []
    ground_truth = ground_truth_camera_samples(run)
    ground_truth_errors = []
    ground_truth_error_times = []
    ground_truth_x_errors = []
    ground_truth_y_errors = []
    verified_truth_errors = []
    truth_available = 0
    comparable = 0
    total_grid = 0
    timestamp = 0.0
    while timestamp <= run["duration"]:
        total_grid += 1
        expected = nearest_pose(source_verified, timestamp)
        actual = nearest_pose(verified_poses, timestamp)
        if expected is not None and actual is not None:
            comparable += 1
            expected_x = expected[0] - source_start[0]
            expected_y = expected[1] - source_start[1]
            actual_x = actual[0] - run_start[0]
            actual_y = actual[1] - run_start[1]
            trajectory_errors.append(math.hypot(actual_x - expected_x, actual_y - expected_y))
        truth = nearest_pose(ground_truth, timestamp)
        visual = nearest_pose(capture_poses, timestamp)
        if truth is not None:
            truth_available += 1
        verified_visual = nearest_pose(capture_verified, timestamp)
        if truth is not None and verified_visual is not None:
            verified_truth_errors.append(math.hypot(
                verified_visual[0] - run_start[0] - truth[0],
                verified_visual[1] - run_start[1] - truth[1]))
        if truth is not None and visual is not None:
            visual_x = visual[0] - run_start[0]
            visual_y = visual[1] - run_start[1]
            error_x = visual_x - truth[0]
            error_y = visual_y - truth[1]
            ground_truth_x_errors.append(error_x)
            ground_truth_y_errors.append(error_y)
            ground_truth_errors.append(math.hypot(error_x, error_y))
            ground_truth_error_times.append(timestamp)
        timestamp += 0.1

    truth_end = ground_truth[-1][1:] if ground_truth else (None, None)
    visual_end = (
        (last[0] - run_start[0], last[1] - run_start[1])
        if None not in last else (None, None)
    )
    end_error = (
        (visual_end[0] - truth_end[0], visual_end[1] - truth_end[1])
        if None not in truth_end + visual_end else (None, None)
    )
    threshold_crossings = {
        str(threshold): next((rounded(timestamp) for error, timestamp in
            zip(ground_truth_errors, ground_truth_error_times) if error > threshold), None)
        for threshold in (8, 16, 32, 64, 128, 256)
    }
    maximum_error_time = (
        rounded(ground_truth_error_times[max(
            range(len(ground_truth_errors)), key=ground_truth_errors.__getitem__
        )]) if ground_truth_errors else None
    )
    coarse_proxy = coarse_motion_proxy(run)

    return {
        "iteration": run.get("replayIteration"),
        "primaryCameraAccuracy": "exactFrameCamera",
        "exactFrameCamera": summarize_exact(run),
        "captureRateCoarseCamera": (
            summarize_exact(coarse_proxy) if coarse_proxy is not None else None
        ),
        "lowResolutionMotionDemand": low_resolution_motion_demand(run),
        "lowResolutionFrameTrace": low_resolution_trace_summary(run),
        "coarsePlaceMatches": sum(
            sample.get("placeMatchKeyframeID") is not None
            for sample in run.get("coarseMotionSamples") or []
        ),
        "poseCoordinateSource": "publishedCamera",
        "groundTruthPoseSelection": "hacker-connected-scenes-published-capture-time",
        "groundTruthTimestampCaveat": "Receiver receipt time; transport/display delay not calibrated",
        "groundTruthComparableSamples": len(ground_truth_errors),
        "groundTruthAvailableSamples": truth_available,
        "groundTruthUnavailablePoseSamples": truth_available - len(ground_truth_errors),
        "verifiedPoseGroundTruthP95ErrorPixels": rounded(percentile(verified_truth_errors, 95)),
        "verifiedPoseErrorOver16Percent": rounded(
            100 * sum(error > 16 for error in verified_truth_errors) / len(verified_truth_errors)
            if verified_truth_errors else None),
        "samples": len(samples),
        "trackingSamplesPerSecond": rounded(
            len(samples) / run["duration"] if run["duration"] > 0 else None
        ),
        "captureIntervalP50Milliseconds": rounded(percentile(capture_intervals, 50)),
        "captureIntervalP95Milliseconds": rounded(percentile(capture_intervals, 95)),
        "captureIntervalsOver25Milliseconds": sum(
            interval > 25 for interval in capture_intervals
        ),
        "groundTrackingP50Milliseconds": rounded(percentile(ground_tracking_times, 50)),
        "groundTrackingP95Milliseconds": rounded(percentile(ground_tracking_times, 95)),
        "groundTrackingMaxMilliseconds": rounded(max(ground_tracking_times, default=0)),
        "verifiedPercent": rounded(100 * sum(s["poseVerified"] for s in samples) / len(samples), 2),
        "lossEpisodes": len(loss_intervals),
        "lossSeconds": rounded(sum(loss_intervals)),
        "longestLossSeconds": rounded(max(loss_intervals, default=0)),
        "longestLossStartSeconds": rounded(longest_loss[0] if longest_loss else None),
        "largestPoseStepPixels": rounded(largest_step[0]),
        "largestPoseStepAtSeconds": rounded(largest_step[1]),
        "poseStepsOver64Pixels": sum(step[0] > 64 for step in pose_steps),
        "startPose": [rounded(first[0]), rounded(first[1])],
        "endPose": [rounded(last[0]), rounded(last[1])],
        "pathDisplacementPixels": rounded(path_displacement),
        "distanceFromLocalOriginPixels": rounded(original_distance),
        "globalMatchEpisodes": len(match_intervals),
        "globalMatchSeconds": rounded(sum(match_intervals)),
        "globalCorrectionCount": len(corrections),
        "globalCorrectionP95Pixels": rounded(percentile(corrections, 95)),
        "acceptedAtlasClosures": len(run.get("loopClosures") or []),
        "inputSequenceExact": sequence_matches,
        "inputTimingMeanErrorMilliseconds": rounded(
            1000 * statistics.mean(timing_errors) if timing_errors else None
        ),
        "inputTimingP95AbsoluteMilliseconds": rounded(
            1000 * percentile([abs(value) for value in timing_errors], 95)
            if timing_errors else None
        ),
        "inputTimingMaxAbsoluteMilliseconds": rounded(
            1000 * max([abs(value) for value in timing_errors], default=0)
            if timing_errors else None
        ),
        "trajectoryComparablePercent": rounded(100 * comparable / total_grid, 2),
        "trajectoryRMSErrorPixels": rounded(
            math.sqrt(statistics.mean([value * value for value in trajectory_errors]))
            if trajectory_errors else None
        ),
        "trajectoryP95ErrorPixels": rounded(percentile(trajectory_errors, 95)),
        "groundTruthEndPosePixels": [rounded(truth_end[0]), rounded(truth_end[1])],
        "groundTruthEndErrorPixels": [rounded(end_error[0]), rounded(end_error[1])],
        "groundTruthRMSErrorPixels": rounded(
            math.sqrt(statistics.mean([value * value for value in ground_truth_errors]))
            if ground_truth_errors else None
        ),
        "groundTruthP95ErrorPixels": rounded(percentile(ground_truth_errors, 95)),
        "groundTruthMaxErrorPixels": rounded(max(ground_truth_errors, default=0)),
        "groundTruthMaxErrorAtSeconds": maximum_error_time,
        "groundTruthFirstErrorOverPixelsAtSeconds": threshold_crossings,
        "groundTruthXErrorSDPixels": rounded(
            statistics.pstdev(ground_truth_x_errors) if ground_truth_x_errors else None
        ),
        "groundTruthYErrorSDPixels": rounded(
            statistics.pstdev(ground_truth_y_errors) if ground_truth_y_errors else None
        ),
    }


def cross_run_summary(runs):
    verified = [pose_samples(run, verified_only=True, published=True) for run in runs]
    starts = [pose_samples(run, published=True)[0][1:] for run in runs]
    duration = min(run["duration"] for run in runs)
    spreads = []
    common = 0
    total = 0
    timestamp = 0.0
    while timestamp <= duration:
        total += 1
        poses = [nearest_pose(samples, timestamp) for samples in verified]
        if all(pose is not None for pose in poses):
            common += 1
            normalized = [
                (pose[0] - start[0], pose[1] - start[1])
                for pose, start in zip(poses, starts)
            ]
            center_x = statistics.mean(point[0] for point in normalized)
            center_y = statistics.mean(point[1] for point in normalized)
            spreads.append(math.sqrt(statistics.mean([
                (point[0] - center_x) ** 2 + (point[1] - center_y) ** 2
                for point in normalized
            ])))
        timestamp += 0.1
    return {
        "allRunsVerifiedAtGridPercent": rounded(100 * common / total, 2),
        "meanTrajectorySpreadPixels": rounded(statistics.mean(spreads) if spreads else None),
        "p95TrajectorySpreadPixels": rounded(percentile(spreads, 95)),
        "maxTrajectorySpreadPixels": rounded(max(spreads, default=0) if spreads else None),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("replays", nargs="+", type=Path)
    arguments = parser.parse_args()
    source = load(arguments.source)
    runs = sorted((load(path) for path in arguments.replays), key=lambda item: item["replayIteration"])
    summaries = [summarize(source, run) for run in runs]
    print(json.dumps({
        "source": {
            "duration": rounded(source["duration"]),
            "events": len(source["events"]),
            "samples": len(source["trackingSamples"]),
            "verifiedPercent": rounded(
                100 * sum(s["poseVerified"] for s in source["trackingSamples"])
                / len(source["trackingSamples"]),
                2,
            ),
        },
        "replays": summaries,
        "aggregate": {
            "verifiedPercentMean": rounded(statistics.mean(item["verifiedPercent"] for item in summaries), 2),
            "verifiedPercentSD": rounded(statistics.pstdev(item["verifiedPercent"] for item in summaries), 2),
            "pathDisplacementMeanPixels": rounded(statistics.mean(
                item["pathDisplacementPixels"] for item in summaries)),
            "pathDisplacementSDPixels": rounded(statistics.pstdev(
                item["pathDisplacementPixels"] for item in summaries)),
            "acceptedAtlasClosures": sum(item["acceptedAtlasClosures"] for item in summaries),
            "trackingSamplesPerSecondMean": rounded(statistics.mean(
                item["trackingSamplesPerSecond"] for item in summaries
            )),
            "captureIntervalP95MillisecondsMean": rounded(statistics.mean(
                item["captureIntervalP95Milliseconds"] for item in summaries
            )),
            "groundTrackingP50MillisecondsMean": rounded(statistics.mean(
                item["groundTrackingP50Milliseconds"] for item in summaries
            )),
            "groundTrackingP95MillisecondsMean": rounded(statistics.mean(
                item["groundTrackingP95Milliseconds"] for item in summaries
            )),
            **cross_run_summary(runs),
        },
    }, indent=2))


if __name__ == "__main__":
    main()
