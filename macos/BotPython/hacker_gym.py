"""Gymnasium environment backed by one private, no-save Hollow Knight process."""
from __future__ import annotations

import base64
from collections import Counter
from contextlib import suppress
import json
import logging
import math
import os
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

import gymnasium as gym
import numpy as np
from gymnasium import spaces

from bot_training_ipc import EncoderReplies
from hkv_bot_dataset import ACTION_SIZES, HELD_ATTACK_COST, HISTORY_LENGTH, SCHEMAS

START = "Tutorial_01"
SUPPORTED_SCENES = {START, "Town", ""}
NEUTRAL = [0, 0, 0, 0]
SPAWN_ATTEMPTS = 3


def buttons(action):
    horizontal, aim, jump, attack = action
    return (0, 1, 2)[horizontal] | (0, 8, 4)[aim] | jump * 32 | attack * 64


def save_json(path, data):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, allow_nan=False) + "\n")
    temporary.replace(path)


class Dwell:
    """Count consecutive decisions within a small area without a productive interaction."""
    def __init__(self, x, y):
        self.x, self.y = x, y
        self.steps = 0
        self.longest = 0

    def advance(self, x, y, productive):
        if productive or math.hypot(x - self.x, y - self.y) >= 1.5:
            self.steps = 0
            self.x, self.y = x, y
        else:
            self.steps += 1
            self.longest = max(self.longest, self.steps)
        return self.steps


class EpisodeScore:
    """Score measured progress on the Atlas path and gameplay outcomes."""
    def __init__(self, start):
        self.dwell = Dwell(start["heroX"], start["heroY"])
        self.start = start["heroX"]
        self.cells = {self.cell(start)}
        self.best_x = self.start
        self.damage = 0
        self.geo = start.get("geo", 0)
        self.geo_earned = self.deposit_hits = self.deposits_mined = 0
        self.enemy_outcome = (start.get("actors") or {}).get("enemyOutcomes")
        self.blue_outcome = start.get("blueHealthOutcomes")
        self.blue_gained = 0
        self.enemy_damage = self.enemy_kills = 0
        self.last_progress = 0
        self.path_progress = 0.0
        self.path_samples = 0
        self.path_alignment = 0.0
        self.path_alignment_samples = 0
        self.last_activity = 0
        # Historical report field; arriving in a scene does not mark completion.
        self.completed = False
        self.attack_down = False
        self.held_attack_decisions = 0

    @staticmethod
    def cell(frame):
        return math.floor(frame["heroX"] / 4), math.floor(frame["heroY"] / 4)

    def advance(self, frame, path_progress=None, path_alignment=None, *, attack_down=False):
        reward = -0.015
        if attack_down and self.attack_down:
            reward -= HELD_ATTACK_COST
            self.held_attack_decisions += 1
        self.attack_down = attack_down
        if path_progress is not None:
            if isinstance(path_progress, bool) or not isinstance(path_progress, (float, int)) or not math.isfinite(path_progress) or not 0 <= path_progress <= 100:
                raise ValueError("Invalid Atlas path progress")
            self.path_samples += 1
            self.path_progress += path_progress
            if path_progress > 0:
                self.last_progress = frame["step"]
                reward += path_progress
        if path_alignment is not None:
            if isinstance(path_alignment, bool) or not isinstance(path_alignment, (float, int)) or not math.isfinite(path_alignment) or not -3 <= path_alignment <= 3:
                raise ValueError("Invalid Atlas path alignment")
            self.path_alignment += path_alignment
            self.path_alignment_samples += 1
            reward += 0.5 * path_alignment
        damage = frame["damage"] - self.damage
        if damage < 0:
            raise ValueError("Damage counter regressed")
        self.damage += damage
        reward -= 5 * damage
        geo = frame.get("geo", self.geo)
        earned = max(0, geo - self.geo)
        self.geo, self.geo_earned = geo, self.geo_earned + earned
        hits, mined = frame.get("depositHits", self.deposit_hits), frame.get("depositsMined", self.deposits_mined)
        if hits < self.deposit_hits or mined < self.deposits_mined:
            raise ValueError("Deposit outcome counter regressed")
        productive = hits > self.deposit_hits or earned > 0
        if productive:
            self.last_activity = frame["step"]
        reward += 0.25 * (hits - self.deposit_hits) + 1.5 * (mined - self.deposits_mined) + 0.04 * earned
        self.deposit_hits, self.deposits_mined = hits, mined
        blue = frame.get("blueHealthOutcomes")
        if blue is not None and (not isinstance(blue.get("epoch"), str) or not blue["epoch"]
                                 or type(blue.get("gained")) is not int or blue["gained"] < 0):
            raise ValueError("Invalid blue health outcome counter")
        if blue and self.blue_outcome and blue["epoch"] == self.blue_outcome["epoch"]:
            if blue["gained"] < self.blue_outcome["gained"]:
                raise ValueError("Blue health outcome counter regressed")
            gained = blue["gained"] - self.blue_outcome["gained"]
            self.blue_gained += gained
            reward += 1.0 * gained
            productive = productive or gained > 0
        self.blue_outcome = blue
        combat = (frame.get("actors") or {}).get("enemyOutcomes")
        if combat and self.enemy_outcome and combat["epoch"] == self.enemy_outcome["epoch"]:
            dealt = max(0, combat["damage"] - self.enemy_outcome["damage"])
            kills = max(0, combat["kills"] - self.enemy_outcome["kills"])
            self.enemy_damage += dealt
            self.enemy_kills += kills
            reward += 0.03 * dealt + 1.25 * kills
            productive = productive or dealt > 0 or kills > 0
            if kills:
                self.last_activity = frame["step"]
        self.enemy_outcome = combat
        scene = frame["sceneName"]
        reason = ""
        if frame["health"] <= 0:
            reason, reward = "death", reward - 10
        elif scene == START and frame["heroAvailable"] and frame["grounded"] and not damage:
            cell = self.cell(frame)
            if cell not in self.cells:
                self.cells.add(cell)
            self.best_x = max(self.best_x, frame["heroX"])
        elif scene not in SUPPORTED_SCENES:
            reason = "unexpected-scene"
        if scene == START and frame["heroAvailable"]:
            self.dwell.advance(frame["heroX"], frame["heroY"], productive or (path_progress is not None and path_progress > 0))
        return reward, reason

    def metrics(self):
        return dict(pathProgress=round(self.path_progress, 6), pathSamples=self.path_samples,
                    pathAlignment=round(self.path_alignment, 6), pathAlignmentSamples=self.path_alignment_samples,
                    new_cells=len(self.cells) - 1, longestDwellDecisions=self.dwell.longest, damage=self.damage, completed=self.completed,
                    geoEarned=self.geo_earned, depositHits=self.deposit_hits, depositsMined=self.deposits_mined,
                    enemyKills=self.enemy_kills, enemyDamage=self.enemy_damage, blueHealthGained=self.blue_gained,
                    heldAttackDecisions=self.held_attack_decisions,
                    rightward_distance=round(self.best_x - self.start, 2))


class HackerGym(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, *, game, vision, labels, version, domain="hacker", supports_masked=False, max_steps=1200, stall_steps=100, deadline=math.inf,
                 visual_confirmation=None, training_render_hz=60, knight_confirmation=None, deposit_confirmation=None,
                 path_connections=None, exit_path_objective=None, visual_support_root=None):
        if training_render_hz not in (60, 120):
            raise ValueError("Training render rate must be 60 or 120 Hz")
        self.training_render_hz = training_render_hz
        self.game, self.vision, self.labels = map(Path, (game, vision, labels))
        self.version, self.max_steps, self.stall_steps = version, max_steps, stall_steps
        if domain not in ("gameplay", "hacker"):
            raise ValueError("Unknown bot domain")
        self.domain, self.supports_masked = domain, supports_masked
        self.visual_confirmation = visual_confirmation
        self.knight_confirmation = knight_confirmation
        self.deposit_confirmation = deposit_confirmation
        self.visual_support_root = visual_support_root
        self.path_connections = Path(path_connections) if path_connections is not None else None
        if exit_path_objective is not None and (len(exit_path_objective) != 2 or not all(math.isfinite(v) for v in exit_path_objective)):
            raise ValueError("Invalid fixed exit objective")
        self.exit_path_objective = exit_path_objective
        self.deadline = deadline
        self.action_space = spaces.MultiDiscrete(ACTION_SIZES)
        self.observation_space = spaces.Box(-4, 4, (len(SCHEMAS[version]) * HISTORY_LENGTH,), np.float32)
        self.worker = self.encoder = self.temp = None
        self.encoder_replies = EncoderReplies()
        self.last_frame = None
        self.last_control_gap = 0
        self.path_progress = None
        self.path_alignment = None

    def _receive(self, step):
        path = self.directory / "reply.json"
        failed = self.directory / "error.txt"
        limit = min(self.deadline, time.monotonic() + (130 if step == 0 else 12))
        while not path.exists():
            if failed.exists():
                raise RuntimeError(failed.read_text()[:1500])
            if self.worker.poll() is not None or time.monotonic() >= limit:
                tail = self.directory / "unity.log"
                debug = tail.read_bytes()[-800:].decode(errors="replace") if tail.exists() else ""
                raise TimeoutError(f"Game worker stopped responding at step {step}. {debug}")
            time.sleep(0.006 if step else 0.05)
        frame = json.loads(path.read_bytes())
        path.unlink()
        if (frame["step"] != step or frame["episode"] != self.episode
                or frame.get("noSave") is not True or frame.get("invincible") is not False):
            raise RuntimeError("Invalid worker, sequence, save or damage state")
        if step == 0 and "renderHz" not in frame and "simulationHz" not in frame:
            # Earlier receivers always use 60 Hz and ignore the optional display-rate request.
            self.render_hz = 60
        if (frame.get("renderHz", 60) != self.render_hz or frame.get("simulationHz", 60) != 60):
            raise RuntimeError("The installed Gym receiver did not preserve the requested simulation timing")
        if step and not self.last_frame["ticks"] < frame["ticks"] <= self.last_frame["ticks"] + 6:
            raise RuntimeError("Simulation clock lost synchronization")
        self.last_frame = frame
        return frame

    def _encode(self, frame, previous):
        if self.domain == "gameplay":
            message = dict(ticks=frame["ticks"], previous=previous, width=frame["pixelWidth"], height=frame["pixelHeight"], sample=frame)
        else:
            message = dict(sample=frame, ticks=frame["ticks"], health=frame["health"], previous=previous)
        try:
            line = self.encoder_replies.request(self.encoder, json.dumps(message).encode() + b"\n",
                                                timeout=45 if frame["step"] == 0 else 12)
        except TimeoutError:
            raise TimeoutError(f"{self.domain} observation encoder timed out") from None
        if not line:
            raise RuntimeError(f"Installed Vision exited before encoding the {self.domain} frame")
        result = json.loads(line)
        self.path_progress = result.get("pathProgress")
        self.path_alignment = result.get("pathAlignment")
        self.path_reachable = result.get("pathReachable", False)
        self.issues = result["issues"]
        self.usable = "input" in result
        if self.domain == "gameplay":
            self.perception["frames"] += 1
            self.perception["usable"] += self.usable
            self.perception["floorFrames"] += result.get("floors", 0) > 0
            self.perception["wallFrames"] += result.get("walls", 0) > 0
            self.perception["atlasFrames"] += bool(result.get("atlas"))
            self.perception["weakKnightCorrections"] += bool(result.get("weakKnightCorrection"))
            self.perception["trainedCrawlidFrames"] += bool(result.get("crawlidLocalizerActive"))
            self.perception["trainedKnightFrames"] += bool(result.get("knightLocalizerActive"))
            self.perception["trainedDepositFrames"] += bool(result.get("depositLocalizerActive"))
            self.perception["healthFrames"] += bool(result.get("healthKnown"))
            if "visibleMasks" in result:
                self.perception["correctHealth"] += result["visibleMasks"] == frame["health"]
            if result.get("knight", "missing") == "missing" and "knightConfidence" in result:
                confidence = result["knightConfidence"]
                self.perception["weakKnightCandidate"] += 0.2 <= confidence < 0.5
            self.perception[result.get("knight", "missing")] += 1
            self.perception.update(self.issues)
        if "input" not in result:
            self.issues = self.issues or ["history-unavailable"]
            return np.zeros(self.observation_space.shape, np.float32)
        values = np.frombuffer(base64.b64decode(result["input"], validate=True), dtype="<f4").copy()
        if self.version == 1 and values.size == len(SCHEMAS[2]) * HISTORY_LENGTH:
            values = values.reshape(HISTORY_LENGTH, -1)[:, :len(SCHEMAS[1])].ravel()
        if not self.observation_space.contains(values):
            raise ValueError("Native encoder returned incompatible features")
        if self.domain == "gameplay" and result.get("knight") in ("visual", "visual-predicted"):
            # Audit the finished native observation against the mod. These counters
            # never enter the policy's inputs or change an action.
            hero = (frame.get("actors") or {}).get("hero")
            if hero and hero.get("height", 0) > 0:
                width, height = frame["pixelWidth"], frame["pixelHeight"]
                x, y = values[-len(SCHEMAS[self.version]):][:2] * width
                within = math.hypot(x - hero["screenX"], y - hero["screenY"]) <= hero["height"]
                source = "measuredKnight" if result["knight"] == "visual" else "predictedKnight"
                self.perception[source + ("WithinHeight" if within else "OutsideHeight")] += 1
                if not within and 0 <= x <= width * .27 and height * .8 <= y <= height:
                    self.perception["farKnightInHUDCorner"] += 1
        return values

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self.ended = False
        self.missing_steps = 0
        self.last_control_gap = 0
        self.perception = Counter()
        self.close()
        episode_seed = int(self.np_random.integers(0, 2**31 - 1) if seed is None else seed % (2**31))
        preferred_render_hz = (options or {}).get("render_hz", self.training_render_hz)
        if preferred_render_hz not in (60, 120):
            raise ValueError("Training render rate must be 60 or 120 Hz")
        # Verification starts always use the reference display rate.
        render_hz = preferred_render_hz if 100_000 <= episode_seed < 1_000_000 else 60
        try:
            for attempt in range(1, SPAWN_ATTEMPTS + 1):
                self._launch_episode(episode_seed, render_hz)
                frame = self._receive(0)
                if (frame["sceneName"] == START and frame["gameState"] == "PLAYING"
                        and frame["heroAvailable"] and frame["grounded"] and frame["ticks"] == 0
                        and frame["damage"] == 0 and frame["health"] == 5
                        and abs(frame["heroX"] - 34.5) <= 0.25 and abs(frame["heroY"] - 11.41) <= 0.5):
                    break
                fields = ("sceneName", "gameState", "heroAvailable", "grounded", "heroX", "heroY",
                          "health", "damage", "ticks")
                details = {key: frame[key] for key in fields}
                message = (f"Worker did not reach a fresh King's Pass spawn for seed {episode_seed} "
                           f"at {self.render_hz} Hz (attempt {attempt}/{SPAWN_ATTEMPTS}). Observed {details}.")
                if attempt == SPAWN_ATTEMPTS or time.monotonic() >= self.deadline:
                    raise RuntimeError(message)
                logging.getLogger(__name__).warning("%s Relaunching with the same seed.", message)
                self.close()

            self.score = EpisodeScore(frame)
            obs = self._encode(frame, NEUTRAL)
            if self.exit_path_objective is not None and not self.path_reachable:
                raise RuntimeError("The fixed King’s Pass exit objective is not reachable on the authored Atlas path")
            if not self.usable and self.domain == "hacker":
                raise RuntimeError(f"Opening Hacker observation unusable: {self.issues}")
            return obs, self.score.metrics()
        except BaseException:
            self.close()
            raise

    def _launch_episode(self, seed, render_hz):
        self.temp = tempfile.TemporaryDirectory(prefix="hkv-gym-")
        self.directory = Path(self.temp.name)
        self.episode = str(uuid.uuid4())
        self.render_hz = render_hz
        # Gameplay needs RGB frames. Hacker uses authored terrain and mod positions.
        visual = self.domain == "gameplay"
        save_json(self.directory / "episode.json", dict(episode=self.episode, domain=self.domain,
                  policyDomain=self.domain, seed=seed, renderHz=render_hz))
        encoder_args = ([self.vision, "--gameplay-gym-observations", str(self.version), self.directory / "frame.rgb",
                         str(self.supports_masked).lower()] if visual else
                        [self.vision, "--hacker-gym-observations", str(self.version), self.labels])
        if visual:
            overrides = [self.visual_confirmation, self.knight_confirmation, self.deposit_confirmation]
            while overrides and overrides[-1] is None:
                overrides.pop()
            encoder_args.extend(path or "-" for path in overrides)
        encoder_temp = self.directory / "encoder-tmp"
        encoder_temp.mkdir()
        encoder_env = dict(os.environ, TMPDIR=str(encoder_temp), HKV_GYM_LABELS=str(self.labels))
        if self.visual_support_root is not None:
            encoder_env["HKV_GYM_VISUAL_SUPPORT_ROOT"] = str(self.visual_support_root)
        if self.path_connections is not None:
            encoder_env["HKV_ATLAS_PATH_CONNECTIONS"] = str(self.path_connections)
        if self.exit_path_objective is not None:
            encoder_env.update(HKV_GYM_EXIT_X=str(self.exit_path_objective[0]), HKV_GYM_EXIT_Y=str(self.exit_path_objective[1]))
        self.encoder = subprocess.Popen(encoder_args, env=encoder_env, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.worker = subprocess.Popen([self.game, "-batchmode", *([] if visual else ["-nographics"]), "-screen-width", "960",
            "-screen-height", "540", "-logFile", self.directory / "unity.log"],
            cwd=self.game.parent if os.name == "nt" else self.directory,
            env=dict(os.environ, HKV_GYM_DIRECTORY=str(self.directory), SteamAppId="367520", SteamGameId="367520"),
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def step(self, action):
        if self.last_frame is None or self.ended or not self.action_space.contains(action):
            raise ValueError("Expected a reset environment and a valid bot action")
        applied = list(map(int, action)) if self.usable else NEUTRAL
        step = self.last_frame["step"] + 1
        save_json(self.directory / "request.json",
                  dict(episode=self.episode, step=step, ticks=6, buttons=buttons(applied)))
        frame = self._receive(step)
        obs = self._encode(frame, applied)
        reward, reason = self.score.advance(frame, self.path_progress if self.usable else None,
                                            self.path_alignment if self.usable else None, attack_down=bool(applied[3]))
        playable = frame["gameState"] == "PLAYING" and frame["heroAvailable"]
        if not playable:
            self.last_control_gap = step
        self.missing_steps = self.missing_steps + 1 if playable and not self.usable else 0
        if not reason:
            if self.missing_steps >= self.stall_steps:
                reason = "observation-gap"
            elif playable and step - max(self.score.last_progress, self.score.last_activity,
                                         self.last_control_gap) >= self.stall_steps:
                reason = "stalled"
            elif step >= self.max_steps:
                reason = "time-limit"
        self.ended = bool(reason)
        info = dict(**self.score.metrics(), reason=reason, applied=applied, issues=self.issues)
        return obs, reward, reason == "death", bool(reason and reason != "death"), info

    def close(self):
        for process in (self.encoder, self.worker):
            if process is None:
                continue
            if process.stdin:
                with suppress(BrokenPipeError):
                    process.stdin.close()
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            if process.stdout:
                process.stdout.close()
        self.worker = self.encoder = self.last_frame = None
        self.encoder_replies.close()
        if self.temp is not None:
            self.temp.cleanup()
            self.temp = None
