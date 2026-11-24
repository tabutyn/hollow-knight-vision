import base64
from collections import Counter
import io
import json
import os
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
import torch

from hacker_gym import EpisodeScore, HackerGym, buttons
from bot_training_ipc import exclusive_file_lock
from gym_authored_route import AuthoredRoute, PitJumpPractice, RoutePractice, Surface, connected_pit_exits
from gym_blockade_response import confirmed_strikes, learn_blockade_response, learn_climb_response, opened_barriers, route_strike_edges, save_wall_strikes, stalled_strikes
from gym_cache_teacher import CachePractice
from gym_combat_rehearsal import CombatRehearsal, nearby_combat, protected_approach, protected_landing, save_combat, sees_opponent
from gym_demonstration_teacher import Decision, DemonstrationPractice
from gym_general_training import EncounterPractice, PracticeStarts, practice_kind
from gym_landing_rehearsal import LandingRehearsal, missed_jump_hold, missed_jump_rearm, missed_ledge_takeoff, nearby_landings, save_landings
from gym_platform_teacher import reachable_platform
from gym_policy_search import SampledFrontier
from gym_route_rehearsal import playable_arrivals, route_examples, route_files, store_route
from hkv_bot_dataset import SCHEMAS
from train_hacker_gym import (adjust, crosses_frontier, enemy_maneuver, exploration_checkpoint, first_crossing_lesson, general_improvement, holds_progress, interrupted_proposal, learn_maneuver, measured_candidate, measured_ppo_lesson, moving_approach_end, ppo_barrier_probe, ppo_wall_rehearsal, save_exploration_branch, sha,
                              advances_learner_past_stall, gym_checkpoint_path, last_measured_ppo_learner, measured_combat_repair, measured_takeoff_lesson,
                              preserves_ppo_combat_repair,
                              practice_another_start, prior_practice_sources, productive_windows, rehearse_crossings, reserve_holdouts, retain_maneuver, retain_measured_ppo_variant, rollout, route_anchors, run, safe_crossing_prefix, verified_ppo_wall_passage, wins)


def frame(x=34.5, y=11.4, **changes):
    values = dict(heroX=x, heroY=y, step=0, sceneName="Tutorial_01", damage=0,
                  health=5, grounded=True, heroAvailable=True, gameState="PLAYING")
    return values | changes


class HackerGymTests(unittest.TestCase):
    def test_resume_resolves_domain_private_and_legacy_model_paths(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            models = root / "models" / "hacker"
            work = root / "gym"
            legacy = models.parent / "bot-start"
            private = work / "winners" / "bot-private"
            legacy.mkdir(parents=True)
            private.mkdir(parents=True)
            self.assertEqual(gym_checkpoint_path(models, legacy.name), legacy)
            self.assertEqual(gym_checkpoint_path(models, private.name, work), private)
            current = models / legacy.name
            current.mkdir(parents=True)
            self.assertEqual(gym_checkpoint_path(models, legacy.name), current)
            with self.assertRaisesRegex(ValueError, "checkpoint name"):
                gym_checkpoint_path(models, "../gameplay")

    def test_alternating_batch_starts_route_before_the_rollout_budget_runs_out(self):
        for batch in range(0, 30, 2):
            first_two = [practice_kind(batch, episode, has_routes=True) for episode in range(2)]
            self.assertEqual(first_two, ["route", "spawn"])
            self.assertCountEqual(
                [practice_kind(batch, episode, has_routes=True) for episode in range(2, 6)],
                ["blocked", "enemy", "lower landing", "frontier"],
            )
        self.assertEqual([practice_kind(23, episode, has_routes=False) for episode in range(5)],
                         ["lower landing", "frontier", "spawn", "blocked", "enemy"])

    def test_ending_a_replay_at_the_wrong_pose_recovers_toward_the_selected_floor(self):
        import gymnasium as gym

        class Replay(gym.Env):
            version, max_steps, usable, ended = 14, 1200, True, False
            observation_space = gym.spaces.Box(-4, 4, (820,), dtype=np.float32)
            action_space = gym.spaces.MultiDiscrete([3, 3, 2, 2])

            def reset(self, **kwargs):
                self.last_frame = frame(x=3, y=5.35, velocityX=0)
                self.score = EpisodeScore(self.last_frame)
                self.applied = []
                return np.zeros(820, dtype=np.float32), {}

            def step(self, action):
                self.applied.append(list(action))
                step = len(self.applied)
                x = self.last_frame["heroX"] + (1 if action[0] == 2 else 0)
                self.last_frame = frame(x=x, y=5.35, step=step, velocityX=0)
                return np.zeros(820, dtype=np.float32), 0, False, False, dict(applied=action)

        with TemporaryDirectory() as temporary:
            log = Path(temporary) / "practice.jsonl"
            starts = PracticeStarts(None)
            starts.route.append(dict(seed=100_001, actions=[[0, 0, 0, 0]], x=12., y=5.35, health=5, grounded=True))
            env = Replay()
            idle = SimpleNamespace(predict=lambda *_args, **_kwargs: (np.zeros(4, dtype=np.int8), None))
            practice = EncounterPractice(env, idle, 0, log, starts, route_graph=AuthoredRoute([Surface(0, 20, 4)], []))
            practice.reset()
            record = json.loads(log.read_text())
            self.assertEqual(record["replayInterrupted"]["step"], 1)
            self.assertTrue(record["routeRecovered"])
            self.assertEqual(env.applied[1], [2, 0, 0, 0])
            self.assertLessEqual(abs(env.last_frame["heroX"] - 12), 5)

    def test_course_benchmark_uses_playable_arrival_without_gym_termination(self):
        fields = ("step", "scene", "gameState", "heroAvailable", "health")
        trace = [(0, "Tutorial_01", "PLAYING", True, 5),
                 (1, "Town", "ENTERING_LEVEL", True, 4),
                 (2, "Town", "PLAYING", False, 4),
                 (3, "Town", "PLAYING", True, 4)]
        self.assertEqual(playable_arrivals(fields, trace[:3]), [])
        arrivals = playable_arrivals(fields, trace)
        self.assertEqual(arrivals, [dict(departure="Tutorial_01", scene="Town", step=3)])
        prior = dict(completed=False, damage=0, reason="stalled", pathProgress=80, enemyKills=1)
        reached = dict(completed=False, damage=1, reason="stalled", pathProgress=70, enemyKills=1,
                       kingPassBenchmark=True, playableArrivals=arrivals)
        self.assertTrue(general_improvement(reached, prior))
        self.assertFalse(general_improvement(dict(reached, kingPassBenchmark=False), prior))

    def test_ppo_barrier_probe_targets_contact_and_avoids_recent_repeats(self):
        with TemporaryDirectory() as directory:
            work = Path(directory)
            prior = dict(reason="stalled", end=dict(x=82.9, y=11.4), obstructionStep=70, frontier_step=71,
                         frontierActions=dict(blockedRight=100, nailPresses=0))
            self.assertEqual(ppo_barrier_probe(work, prior, SCHEMAS[14]),
                             (70, 16, "strike marked barriers", (82.9, 11.4)))
            self.assertIsNone(ppo_barrier_probe(work, dict(prior, reason="exit"), SCHEMAS[14]))
            swinging = dict(prior, frontierActions=dict(blockedRight=100, nailPresses=8))
            self.assertIsNone(ppo_barrier_probe(work, swinging, SCHEMAS[14]))
            (work / "comparison-0006.json").write_text(json.dumps(dict(barrierProbe=dict(target=[82.88, 11.41]))))
            self.assertIsNone(ppo_barrier_probe(work, prior, SCHEMAS[14]))

    def test_late_airborne_jump_rehearses_the_shelf_lost_before_damage(self):
        fields = ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")
        def pose(step, x, y, vy, grounded, buttons):
            return step, "Tutorial_01", "PLAYING", True, x, y, vy, grounded, buttons, 5
        trace = [pose(9, 86.7, 43.7, 0, True, 1), pose(10, 85.8, 43.7, 0, True, 1),
                 pose(11, 84.9, 43.6, -1, False, 1), pose(12, 84.1, 43.1, -5, False, 33),
                 pose(13, 83.3, 42.1, -9, False, 33), pose(14, 82.5, 38.2, 0, True, 1),
                 pose(15, 81.7, 38.2, 0, True, 1)]
        missed = missed_ledge_takeoff(fields, trace, frontier_step=12)
        self.assertEqual(missed, dict(step=10, x=85.8, y=43.7, direction=-1,
                                      jumpStep=12, landingStep=14, landingY=38.2))
        self.assertIsNone(missed_ledge_takeoff(fields, trace, frontier_step=24))
        without_jump = trace[:3] + [pose(12, 84.1, 43.1, -5, False, 1),
                                    pose(13, 83.3, 42.1, -9, False, 1)] + trace[5:]
        self.assertIsNone(missed_ledge_takeoff(fields, without_jump, frontier_step=12))
        self.assertIsNone(missed_ledge_takeoff(fields, trace + [pose(16, 82, 43.7, 0, True, 1)], 12))
        with TemporaryDirectory() as directory:
            work = Path(directory)
            observed = np.zeros(3, dtype=np.float32)
            save_landings(work, 190_007, [(85.8, 43.7, 82.3, 47.4, [(observed, [1, 0, 1, 0])])])
            save_landings(work, 190_011, [(85.8, 43.7, 88.1, 47.4, [(observed, [2, 0, 1, 0])])])
            report = dict(missedTakeoff=missed, end=dict(x=82, y=38.2),
                          first_damage=dict(step=21, x=82, y=38.2))
            skill, examples, heads = measured_ppo_lesson(work, report)
            self.assertEqual((skill, heads), ("landing", (0, 2)))
            self.assertEqual([action for _, action in examples], [[1, 0, 1, 0]])
            self.assertIsNone(measured_ppo_lesson(work, dict(report, first_damage=dict(step=8, x=82, y=38.2))))

    def test_short_jump_is_diagnosed_only_at_an_unrecovered_marked_landing(self):
        fields = ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")

        def pose(step, x, y, vy, grounded, buttons, health=5):
            return step, "Tutorial_01", "PLAYING", True, x, y, vy, grounded, buttons, health

        graph = AuthoredRoute([Surface(0, 10, 10), Surface(10, 14, 6), Surface(14, 19, 11)], [])
        points = [pose(0, 9.8, 11.35, 0, True, 2),
                  pose(1, 10.6, 12.8, 15.7, False, 34),
                  pose(2, 11.4, 12.74, 0, False, 2),
                  pose(3, 12.2, 12.04, -9.5, False, 2),
                  pose(4, 13.0, 9.5, -19, False, 2),
                  pose(5, 13.8, 7.35, 0, True, 2)]
        missed = missed_jump_hold(fields, points, graph)
        self.assertEqual(missed, dict(step=0, x=9.8, y=11.35, direction=1, jumpStep=1,
                                      releaseStep=2, landingStep=5, landingY=7.35))
        recovered = points + [pose(6, 14.7, 12.35, 0, True, 2)]
        self.assertIsNone(missed_jump_hold(fields, recovered, graph))
        self.assertIsNone(missed_jump_hold(fields, points[:-1] + [pose(5, 13.8, 7.35, 0, True, 2, 4)], graph))
        holding = points[:2] + [pose(2, 11.4, 14.3, 15.7, False, 34)] + points[3:]
        self.assertIsNone(missed_jump_hold(fields, holding, graph))
        low_floor = AuthoredRoute([Surface(0, 10, 10), Surface(10, 14, 6)], [])
        self.assertIsNone(missed_jump_hold(fields, points, low_floor))

    def test_missed_takeoff_uses_measured_jumps_to_the_reachable_higher_shelf(self):
        graph = AuthoredRoute([Surface(146, 148.1, 13.1), Surface(149.58, 151.5, 17.01)], [])
        takeoff = dict(step=260, x=146.14, y=14.37, direction=-1)
        with TemporaryDirectory() as directory:
            work = Path(directory)
            observed = np.zeros(3, dtype=np.float32)
            left = [1, 0, 1, 0]
            right = [2, 0, 1, 0]
            save_landings(work, 190_007, [(146.5, 14.4, 142, 18.3, [(observed, left)])])
            save_landings(work, 190_011, [(146.5, 14.4, 153, 16.2, [(observed, right)])])
            rehearsal, shelves = measured_takeoff_lesson(work, takeoff, graph)
            self.assertEqual((rehearsal, shelves), ([], (0, 1)))
            save_landings(work, 190_012, [(146.5, 14.4, 150.4, 18.3, [(observed, right)])])
            rehearsal, shelves = measured_takeoff_lesson(work, takeoff, graph)
            self.assertEqual(shelves, (0, 1))
            self.assertEqual([action for _, action in rehearsal], [right])
            rehearsal, shelves = measured_takeoff_lesson(work, takeoff)
            self.assertIsNone(shelves)
            self.assertEqual([action for _, action in rehearsal], [left])

    def test_held_jump_through_touchdown_rehearses_release_before_the_next_jump(self):
        fields = ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")

        def pose(step, x, y, vy, grounded, buttons=34, health=5):
            return step, "Tutorial_01", "PLAYING", True, x, y, vy, grounded, buttons, health

        graph = AuthoredRoute([Surface(0, 18, 5), Surface(8.55, 10.52, 15), Surface(12.32, 14, 18.17)], [])
        trace = [pose(20, 8.62, 17, -12.7, False), pose(21, 9.45, 16.35, 0, True),
                 pose(22, 10.28, 16.35, 0, True), pose(23, 10.41, 16.35, 0, True),
                 pose(24, 11.11, 16.28, -2.8, False), pose(25, 11.94, 15.78, -7.6, False),
                 pose(31, 13.73, 6.35, 0, True)]
        miss = missed_jump_rearm(fields, trace, graph, frontier_step=20)
        self.assertEqual(miss, dict(step=21, x=9.45, y=16.35, direction=1,
                                    lostSupportStep=24, landingStep=31, landingY=6.35))
        released = trace[:2] + [pose(22, 10.28, 16.35, 0, True, buttons=2)] + trace[3:]
        self.assertIsNone(missed_jump_rearm(fields, released, graph, 20))
        hurt = trace[:5] + [pose(25, 11.94, 15.78, -7.6, False, health=4)] + trace[6:]
        self.assertIsNone(missed_jump_rearm(fields, hurt, graph, 20))
        recovered = trace + [pose(50, 12.9, 19.52, 0, True)]
        self.assertIsNone(missed_jump_rearm(fields, recovered, graph, 20))
        with TemporaryDirectory() as directory:
            work = Path(directory)
            observed = np.zeros(3, dtype=np.float32)
            save_landings(work, 190_012, [(9.5, 16.35, 12.9, 19.52,
                                         [(observed, [0, 0, 0, 0]), (observed, [2, 0, 1, 0])])])
            lesson = measured_ppo_lesson(work, dict(missedJumpRearm=miss, end=dict(x=13.73, y=6.35)), graph)
            self.assertEqual(lesson[0], "landing")
            self.assertEqual(lesson[2], (0, 2))
            self.assertEqual([a for _, a in lesson[1]], [[0, 0, 0, 0], [2, 0, 1, 0]])

    def test_ppo_wall_rehearsal_uses_the_candidates_confirmed_contact(self):
        target = (58.8, 11.4, 1)
        incumbent = dict(pathProgress=60, wallPassages=[(*target, 34)])
        candidate = dict(reason="stalled", pathProgress=19, end=dict(x=58.75, y=11.41),
                         frontierActions=dict(nailPresses=0, blockedRight=90))
        obs = np.zeros(len(SCHEMAS[14]) * 4, np.float32)
        contacting = (58.75, 11.41, 1, obs, [2, 0, 0, 0], 0)
        with TemporaryDirectory() as directory:
            work = Path(directory)
            save_wall_strikes(work, 190_007, [(target, [(obs, [2, 0, 0, 0]), (obs, [2, 0, 0, 1])])])
            self.assertIsNone(ppo_wall_rehearsal(work, candidate, [], incumbent))
            self.assertIsNone(ppo_wall_rehearsal(work, candidate, [contacting], dict(incumbent, wallPassages=[])))
            self.assertIsNone(ppo_wall_rehearsal(work, dict(candidate, pathProgress=59), [contacting], incumbent))
            opening, examples = ppo_wall_rehearsal(work, candidate, [contacting], incumbent)
            self.assertEqual(opening, target)
            self.assertEqual(examples[-1][1], [2, 0, 0, 1])
            self.assertEqual(contacting[-2], [2, 0, 0, 0])
        retest = dict(reason="stalled", wallPassages=[(*target, 39)], first_damage=dict(step=70))
        self.assertTrue(verified_ppo_wall_passage(retest, target))
        self.assertFalse(verified_ppo_wall_passage(dict(retest, first_damage=dict(step=42)), target))
        self.assertTrue(verified_ppo_wall_passage(dict(retest, reason="death"), target))
        self.assertFalse(verified_ppo_wall_passage(dict(retest, reason="observation-gap"), target))
        self.assertFalse(verified_ppo_wall_passage(retest, (58.8, 11.4, -1)))

    def test_one_mask_trade_requires_a_kill_and_substantial_live_progress(self):
        stalled = dict(completed=False, damage=0, reason="stalled", pathProgress=30, pathSamples=130,
                       enemyDamage=0, enemyKills=0, longestDwellDecisions=100, reward=20)
        candidate = dict(stalled, damage=1, pathProgress=42, enemyDamage=8, enemyKills=1, reward=30)
        self.assertTrue(general_improvement(candidate, stalled))
        self.assertFalse(general_improvement(dict(candidate, enemyKills=0), stalled))
        self.assertFalse(general_improvement(dict(candidate, pathProgress=35), stalled))
        self.assertFalse(general_improvement(dict(candidate, damage=2), stalled))

    def test_reaching_dirtmouth_takes_priority_over_stalling_with_full_health(self):
        stalled = dict(completed=False, damage=0, reason="stalled", pathProgress=80)
        cleared = dict(completed=True, damage=1, reason="exit", pathProgress=70)
        self.assertTrue(general_improvement(cleared, stalled))
        self.assertFalse(general_improvement(stalled, cleared))

    def test_safer_shorter_run_preserves_path_coverage_and_combat(self):
        prior = dict(damage=2, reason="stalled", pathProgress=87.714286, pathSamples=374, steps=374,
                     enemyDamage=16, enemyKills=2, longestDwellDecisions=100, reward=117.969)
        candidate = dict(prior, damage=1, pathProgress=87.607143, pathSamples=270, steps=270,
                         longestDwellDecisions=42, reward=126.798)
        self.assertTrue(general_improvement(candidate, prior))
        self.assertFalse(general_improvement(dict(candidate, pathSamples=215), prior))
        self.assertFalse(general_improvement(dict(candidate, pathProgress=87.4), prior))
        self.assertFalse(general_improvement(dict(candidate, enemyKills=1), prior))

    def test_measured_ppo_variant_remains_available_for_training_after_a_holdout_loss(self):
        with TemporaryDirectory() as directory:
            work = Path(directory)
            raw = work / "learner-0038.zip"
            raw.write_bytes(b"raw PPO update")
            variant = SimpleNamespace(save=lambda path: path.write_bytes(b"measured policy"))
            evidence = {"selectedUpdate": "rehearsal", "matched": [[{"damage": 0}, {"damage": 1}]]}
            path = retain_measured_ppo_variant(variant, work, 38, raw, evidence)
            self.assertEqual(path.name, "learner-rehearsed-0038.zip")
            self.assertEqual(path.read_bytes(), b"measured policy")
            self.assertEqual(evidence["continuationLearner"], {"filename": path.name, "sha256": sha(path)})
            self.assertEqual(raw.read_bytes(), b"raw PPO update")

            quarter = {"selectedUpdate": "rehearsal", "selectedUpdateFraction": .25}
            path = retain_measured_ppo_variant(variant, work, 38, raw, quarter)
            self.assertEqual(path.name, "learner-compared-0038.zip")
            self.assertEqual(path.read_bytes(), b"measured policy")
            self.assertEqual(retain_measured_ppo_variant(variant, work, 38, raw, {}), raw)

    def test_regressed_ppo_can_retest_the_latest_verified_learner_in_the_same_experiment(self):
        with TemporaryDirectory() as directory:
            work = Path(directory)
            identity = {"visionSource": "same-encoder", "labels": "same-room"}
            earlier = work / "learner-rehearsed-0043.zip"
            later = work / "learner-compared-0044.zip"
            earlier.write_bytes(b"prior measured learner")
            later.write_bytes(b"later measured learner")

            def evidence(number, file, environment):
                (work / f"comparison-{number:04d}.json").write_text(json.dumps({
                    "identity": environment,
                    "continuationLearner": {"filename": file.name, "sha256": sha(file)},
                }))

            evidence(43, earlier, identity)
            evidence(44, later, identity)
            evidence(45, later, dict(identity, labels="another-room"))
            self.assertEqual(last_measured_ppo_learner(work, 46, identity), later)
            self.assertEqual(last_measured_ppo_learner(work, 44, identity), earlier)
            later.write_bytes(b"changed")
            self.assertEqual(last_measured_ppo_learner(work, 46, identity), earlier)
            earlier.unlink()
            self.assertIsNone(last_measured_ppo_learner(work, 46, identity))

    def test_safer_farther_stall_continues_training_without_qualifying_for_playback(self):
        prior = dict(damage=2, reason="stalled", pathProgress=87.46, pathSamples=300, steps=310,
                     enemyDamage=16, enemyKills=2, longestDwellDecisions=69, reward=100, unharmedCells=23)
        candidate = dict(prior, damage=1, pathProgress=152.71, pathSamples=450, steps=453,
                         enemyDamage=24, enemyKills=3, longestDwellDecisions=85, reward=236, unharmedCells=50)
        self.assertTrue(advances_learner_past_stall(candidate, prior))
        self.assertFalse(general_improvement(candidate, prior))
        self.assertFalse(advances_learner_past_stall(dict(candidate, damage=3), prior))
        self.assertFalse(advances_learner_past_stall(dict(candidate, enemyKills=1), prior))
        self.assertFalse(advances_learner_past_stall(dict(candidate, pathProgress=92), prior))
        self.assertFalse(advances_learner_past_stall(dict(candidate, unharmedCells=23), prior))
        self.assertFalse(advances_learner_past_stall(dict(candidate, reason="death"), prior))
        with TemporaryDirectory() as directory:
            work = Path(directory)
            raw = work / "learner-0042.zip"
            raw.write_bytes(b"raw PPO update")
            variant = SimpleNamespace(save=lambda path: path.write_bytes(b"private rehearsal"))
            evidence = {"privateRehearsal": candidate, "matched": []}
            path = retain_measured_ppo_variant(variant, work, 42, raw, evidence)
            self.assertEqual(path.name, "learner-rehearsed-0042.zip")
            self.assertEqual(path.read_bytes(), b"private rehearsal")
            self.assertEqual(evidence["continuationLearner"]["sha256"], sha(path))

    def test_combat_repair_uses_measured_training_kills_and_preserves_route_progress(self):
        incumbent = dict(enemyKills=2, damage=1)
        candidate = dict(enemyKills=0, enemyDamage=0, damage=3, reason="stalled",
                         pathProgress=78., pathSamples=220, steps=230, longestDwellDecisions=90,
                         reward=40, first_damage=dict(step=87, x=93.8, y=11.4))
        observed = np.zeros(len(SCHEMAS[14]) * 4, np.float32)
        move = (2, 0, 0, 1)
        with TemporaryDirectory() as directory:
            work = Path(directory)
            save_combat(work, 2_000_001, [(94, 11, [(observed, move)])])
            self.assertEqual(measured_combat_repair(work, candidate, incumbent), [])
            save_combat(work, 980_040, [(94, 11, [(observed, move)])])
            lesson = measured_combat_repair(work, candidate, incumbent)
            self.assertEqual([tuple(action) for _, action in lesson], [move])
            self.assertEqual(measured_combat_repair(work, dict(candidate, enemyKills=2, damage=1), incumbent), [])
            self.assertEqual(len(measured_combat_repair(work, dict(candidate, enemyKills=2), incumbent)), 1)
            self.assertEqual(measured_combat_repair(work, dict(candidate, first_damage=None), incumbent), [])
        repaired = dict(candidate, enemyKills=1, enemyDamage=8, damage=2, pathProgress=83.)
        self.assertTrue(preserves_ppo_combat_repair(repaired, candidate))
        self.assertTrue(preserves_ppo_combat_repair(dict(candidate, damage=2), candidate))
        self.assertFalse(preserves_ppo_combat_repair(dict(repaired, pathProgress=65.), candidate))
        self.assertFalse(preserves_ppo_combat_repair(dict(repaired, damage=5), candidate))

    def test_only_applied_consecutive_attacks_cost_reward_in_both_domains(self):
        for domain in ("hacker", "gameplay"):
            with self.subTest(domain=domain), TemporaryDirectory() as directory:
                env = HackerGym(game="game", vision="vision", labels="labels", version=7, domain=domain)
                env.directory, env.episode = Path(directory), "fight"
                env.last_frame = frame()
                env.score = EpisodeScore(env.last_frame)
                env.ended, env.missing_steps = False, 0
                env.path_progress, env.path_alignment, env.issues = None, None, []
                def receive(step):
                    env.last_frame = frame(step=step, ticks=6 * step)
                    return env.last_frame
                env._receive = receive
                env._encode = lambda *_: np.zeros(1)
                for usable, move, attack, repeats, cost in [
                    (True, 2, 1, 0, 0), (True, 2, 1, 1, .05), (True, 1, 1, 2, .05),
                    (True, 1, 0, 2, 0), (True, 1, 1, 2, 0), (False, 1, 1, 2, 0), (True, 1, 1, 2, 0)
                ]:
                    env.usable = usable
                    _, reward, _, _, info = env.step(np.array([move, 0, 1, attack]))
                    self.assertAlmostEqual(reward, -.015 - cost)
                    self.assertEqual(info["heldAttackDecisions"], repeats)
                    self.assertEqual(info["applied"], [move, 0, 1, attack] if usable else [0, 0, 0, 0])
                self.assertAlmostEqual(EpisodeScore(frame()).advance(frame(step=1), attack_down=True)[0], -.015)

    def test_exit_gate_lesson_requires_blocked_rightward_movement_on_the_last_floor(self):
        guide = RoutePractice(AuthoredRoute([Surface(0, 10, 5)], []))
        at_gate = frame(x=8.4, y=6.4, velocityX=0)
        self.assertFalse(guide.blocked_at_exit(at_gate, 2, 8))
        guide.controls(at_gate, [2, 0, 0, 0])
        self.assertTrue(guide.blocked_at_exit(at_gate, 2, 3))
        self.assertFalse(guide.blocked_at_exit(at_gate, 1, 3))
        self.assertFalse(guide.blocked_at_exit(at_gate, 2, 2))
        self.assertFalse(guide.blocked_at_exit(dict(at_gate, heroX=2), 2, 8))
        self.assertFalse(guide.blocked_at_exit(dict(at_gate, heroY=2), 2, 8))
        self.assertFalse(guide.blocked_at_exit(dict(at_gate, velocityX=8.3), 2, 8))

    def test_pit_jump_requires_connected_rims_and_six_safe_steps_after_landing(self):
        floors = [dict(minimumWorldX=0, maximumWorldX=10, worldY=4, endCornerID="left-rim"),
                  dict(minimumWorldX=10, maximumWorldX=14, worldY=2, startCornerID="left", endCornerID="right"),
                  dict(minimumWorldX=14, maximumWorldX=20, worldY=5, startCornerID="right-rim")]
        walls = [dict(worldY=4, endWorldY=2, startCornerID="left-rim", endCornerID="left"),
                 dict(worldY=2, endWorldY=5, startCornerID="right", endCornerID="right-rim")]
        self.assertEqual(connected_pit_exits(floors, walls[:1]), [])
        left, right = connected_pit_exits(floors, walls)
        self.assertEqual((left.direction, right.direction), (-1, 1))
        bottom = frame(x=14.05, y=3.4, velocityX=0)
        rim = frame(x=15, y=6.4, velocityX=8)
        press = [2, 0, 1, 0]
        practice = PitJumpPractice(right)
        self.assertIsNone(practice.control(rim, press, press))
        self.assertIsNone(practice.control(bottom, [1, 0, 0, 0], press))
        self.assertEqual(practice.control(bottom, press, press), 0)
        self.assertEqual(practice.control(dict(bottom, step=1), press, [2, 0, 0, 0]), 1)
        practice.control(dict(rim, step=10, grounded=False), press, press)
        self.assertIsNone(practice.landing)
        practice.control(dict(rim, step=11), press, press)
        practice.control(dict(bottom, step=12), press, press)
        self.assertIsNone(practice.landing)
        for step in range(13, 20):
            practice.control(dict(rim, step=step), press, press)
        self.assertEqual((practice.landing, practice.verified), (13, 19))
        hurt = PitJumpPractice(right)
        hurt.control(bottom, press, press)
        for step in range(10, 18):
            hurt.control(dict(rim, step=step, health=5 if step < 14 else 4), press, press)
        self.assertIsNone(hurt.verified)

    def test_route_wall_lessons_require_grounded_passage_after_real_press_at_multiple_doors(self):
        with TemporaryDirectory() as folder:
            path = Path(folder) / "100003.npz"
            states = np.array([[i, float(i), 11.] for i in range(18)])
            actions = np.array([[2, 0, 0, int(i in (3, 12))] for i in range(18)])
            features = np.array([[1., int(i in (4, 13))] for i in range(18)], np.float32)
            schema = ["blockade_direction", "previous_attack"]
            def save():
                np.savez(path, states=states, observations=features, actions=actions)
            save()
            openings = [(3., 11., 1), (12., 11., 1)]
            examples = route_strike_edges([path], openings, schema)
            self.assertEqual(sum(a[3] for _, a in examples), 2)
            self.assertTrue(any(o[1] and not a[3] for o, a in examples))
            states[14, 2] += 2
            save()
            self.assertEqual(route_strike_edges([path], openings, schema), [])

    def test_worker_queue_respects_the_time_budget_and_reads_the_checkpoint_after_acquisition(self):
        with TemporaryDirectory() as directory, patch("train_hacker_gym.train_with_worker") as train:
            root = Path(directory)
            arena = root / "bot-v1/gym"
            arena.mkdir(parents=True)
            args = SimpleNamespace(support=root, hours=.00004, wait_for_worker=True)
            with exclusive_file_lock(arena / "worker.lock"):
                run(args)
                train.assert_not_called()
                args.wait_for_worker = False
                with self.assertRaises(BlockingIOError):
                    run(args)
            run(args)
            train.assert_called_once()

    def test_completed_route_rehearsal_excludes_evaluation_loading_damage_and_other_floors(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            report = dict(seed=2000000, reason="exit", completed=True, authoredRouteLandings=5,
                          end=dict(scene="Town", gameState="PLAYING", heroAvailable=True))
            rows = [((i, i % 40, 10 if i < 80 else 22, int(i == 26)),
                     np.array([i], dtype=np.float32), [2, 0, i % 4 == 0, 0]) for i in range(120)]
            store_route(root, report, rows)
            self.assertFalse(route_files(root))
            report["seed"] = 100002
            report["end"]["gameState"] = "ENTERING_LEVEL"
            store_route(root, report, rows)
            self.assertFalse(route_files(root))
            report["end"]["gameState"] = "PLAYING"
            store_route(root, report, rows)
            paths = route_files(root)
            self.assertEqual(len(paths), 1)
            full = route_examples(paths, per_route=40)
            self.assertEqual(len(full), 40)
            self.assertFalse(any(16 <= obs[0] <= 34 for obs, _ in full))
            high = route_examples(paths, {"x": 20, "y": 22})
            self.assertTrue(high)
            self.assertTrue(all(obs[0] >= 80 for obs, _ in high))
            self.assertEqual(route_examples(paths, {"x": 30, "y": 99}), [])

    def test_town_continues_controls_and_uses_normal_episode_limits(self):
        for domain in ("hacker", "gameplay"):
            with self.subTest(domain=domain), TemporaryDirectory() as directory:
                env = HackerGym(game="game", vision="vision", labels="labels", version=7,
                                domain=domain, max_steps=8, stall_steps=100)
                env.directory, env.episode = Path(directory), "run"
                env.last_frame = frame(step=4, ticks=24)
                env.score = EpisodeScore(env.last_frame)
                env.ended, env.usable, env.missing_steps, env.issues = False, True, 0, []
                observations = iter([frame(step=5, ticks=30, sceneName="Town", gameState="ENTERING_LEVEL"),
                                     frame(step=6, ticks=36, sceneName="Town", heroAvailable=False),
                                     frame(step=7, ticks=42, sceneName="Town"),
                                     frame(step=8, ticks=48, sceneName="Town")])
                def receive(step):
                    env.last_frame = next(observations)
                    return env.last_frame
                def encode(current, previous):
                    env.usable = current["heroAvailable"] and current["gameState"] == "PLAYING"
                    return np.zeros(1)
                env._receive, env._encode = receive, encode
                action = np.array([2, 0, 1, 0])
                for index in range(4):
                    _, reward, done, truncated, info = env.step(action)
                    self.assertAlmostEqual(reward, -.015)
                    self.assertFalse(done)
                    self.assertFalse(info["completed"])
                    self.assertEqual(truncated, index == 3)
                    self.assertEqual(info["reason"], "time-limit" if index == 3 else "")
                    self.assertEqual(info["applied"], [2, 0, 1, 0] if index in (0, 3) else [0, 0, 0, 0])

    def test_loading_does_not_use_the_playable_stall_or_observation_gap_budget(self):
        with TemporaryDirectory() as directory:
            env = HackerGym(game="game", vision="vision", labels="labels", version=7,
                            max_steps=20, stall_steps=4)
            env.directory, env.episode = Path(directory), "run"
            env.last_frame = frame(step=3, ticks=18)
            env.score = EpisodeScore(env.last_frame)
            env.ended, env.usable, env.missing_steps, env.issues = False, True, 0, []

            def receive(step):
                current = frame(step=step, ticks=6 * step, sceneName="Town" if step >= 5 else "Tutorial_01")
                if step == 4:
                    current["gameState"] = "EXITING_LEVEL"
                elif step < 8:
                    current["gameState"] = "ENTERING_LEVEL"
                elif step == 8:
                    current["heroAvailable"] = False
                env.last_frame = current
                return current

            def encode(current, previous):
                env.usable = current["gameState"] == "PLAYING" and current["heroAvailable"]
                return np.zeros(1)

            env._receive, env._encode = receive, encode
            for step in range(4, 13):
                _, reward, done, truncated, info = env.step(np.array([2, 0, 0, 0]))
                self.assertAlmostEqual(reward, -.015)
                self.assertFalse(done)
                self.assertFalse(info["completed"])
                self.assertEqual(truncated, step == 12)
                self.assertEqual(info["reason"], "stalled" if step == 12 else "")

    def test_unusable_frame_cannot_supply_a_path_reward(self):
        with TemporaryDirectory() as directory:
            env = HackerGym(game="game", vision="vision", labels="labels", version=7)
            env.directory, env.episode = Path(directory), "run"
            env.last_frame = frame(step=0, ticks=0)
            env.score = EpisodeScore(env.last_frame)
            env.ended, env.usable, env.missing_steps, env.issues = False, True, 0, []
            def receive(step):
                env.last_frame = frame(step=step, ticks=step * 6)
                return env.last_frame
            def encode(frame, previous):
                env.path_progress = 4.0
                env.usable = False
                return np.zeros(1)
            env._receive, env._encode = receive, encode
            _, reward, _, _, info = env.step(np.array([2, 0, 0, 0]))
            self.assertLess(reward, 0)
            self.assertEqual(info["pathProgress"], 0)
            self.assertEqual(info["pathSamples"], 0)

    def test_legacy_receiver_uses_reference_rate_and_a_reported_mismatch_remains_an_error(self):
        with TemporaryDirectory() as directory:
            env = HackerGym(game="game", vision="vision", labels="labels", version=7, training_render_hz=120)
            env.directory, env.episode = Path(directory), "fresh"
            reply = env.directory / "reply.json"
            initial = dict(step=0, episode=env.episode, noSave=True, invincible=False, ticks=0)
            env.render_hz = 120
            reply.write_text(json.dumps(initial))
            env._receive(0)
            self.assertEqual(env.render_hz, 60)
            env.render_hz = 120
            reply.write_text(json.dumps(dict(initial, simulationHz=60, renderHz=60)))
            with self.assertRaisesRegex(RuntimeError, "simulation timing"):
                env._receive(0)

    def test_reset_relaunches_an_invalid_spawn_with_the_same_seed_and_render_rate(self):
        env = HackerGym(game="game", vision="vision", labels="labels", version=7)
        attempts = []
        read_reply = env._receive

        def receive(step):
            if attempts:
                self.assertFalse(attempts[-1][0].exists())
            config = json.loads((env.directory / "episode.json").read_text())
            reply = frame(x=39.0 if not attempts else 34.5, ticks=0, episode=env.episode,
                          noSave=True, invincible=False, renderHz=120, simulationHz=60)
            attempts.append((env.directory, config))
            (env.directory / "reply.json").write_text(json.dumps(reply))
            return read_reply(step)

        self.addCleanup(env.close)
        with patch("hacker_gym.subprocess.Popen") as launch, patch.object(env, "_receive", side_effect=receive), \
                patch.object(env, "_encode", return_value=np.zeros(env.observation_space.shape)) as encode, \
                self.assertLogs("hacker_gym", level="WARNING") as messages:
            launch.return_value.poll.return_value = None
            env.usable = True
            env.reset(seed=755698, options={"render_hz": 120})
            self.assertEqual(launch.return_value.terminate.call_count, 2)
            self.assertEqual(encode.call_count, 1)

        configs = [config for _, config in attempts]
        self.assertEqual([(config["seed"], config["renderHz"]) for config in configs], [(755698, 120)] * 2)
        self.assertNotEqual(configs[0]["episode"], configs[1]["episode"])
        self.assertEqual(env.last_frame["heroX"], 34.5)
        self.assertIn("'heroX': 39.0", messages.output[0])

    def test_reset_bounds_spawn_retries_and_propagates_protocol_failures(self):
        for no_save, launches, message in ((True, 6, r"attempt 3/3.*'health': 4"),
                                            (False, 2, "Invalid worker, sequence, save or damage state")):
            with self.subTest(no_save=no_save):
                env = HackerGym(game="game", vision="vision", labels="labels", version=7)
                read_reply = env._receive

                def receive(step):
                    reply = frame(health=4, ticks=0, episode=env.episode, noSave=no_save, invincible=False)
                    (env.directory / "reply.json").write_text(json.dumps(reply))
                    return read_reply(step)

                with patch("hacker_gym.subprocess.Popen") as launch, \
                        patch.object(env, "_receive", side_effect=receive), patch.object(env, "_encode") as encode, \
                        patch("hacker_gym.logging.getLogger"):
                    with self.assertRaisesRegex(RuntimeError, message):
                        env.reset(seed=755698)
                    self.assertEqual(launch.call_count, launches)
                    encode.assert_not_called()
                self.assertFalse(env.directory.exists())
                self.assertIsNone(env.last_frame)

    def test_practice_bank_uses_only_compatible_observations_and_unique_training_starts(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            identity = dict(domain="gameplay", base="base", checkpoint="weights", visualDetectorAndHUD="perception")
            current, prior, other = (root / name for name in ("current", "prior", "other"))
            for path, info in ((current, identity), (prior, identity), (other, dict(identity, visualDetectorAndHUD="old"))):
                path.mkdir()
                (path / "state.json").write_text(json.dumps({"identity": info}))
            sources = prior_practice_sources(current, identity)
            self.assertEqual(sources, [prior])
            contact = dict(x=94, y=11)
            rows = [(np.array([1.]), (2, 0, 0, 1))]
            for work in (current, prior, other):
                save_combat(work, 100001, [(94, 11, rows)])
            save_combat(prior, 100002, [(94, 11, rows)])
            self.assertEqual(len(nearby_combat(current, contact, sources)), 2)

    def test_horizontal_strike_requires_the_approaching_enemy_in_the_observation_and_a_safe_kill(self):
        sample = frame(actors={"hero": {"screenX": 320, "screenY": 180},
                               "enemies": [{"screenX": 360, "screenY": 177}, {"screenX": 265, "screenY": 180}]},
                       projectionPixelWidth=640, pixelsPerWorldUnitX=10, pixelsPerWorldUnitY=10)
        row = np.zeros(144)
        row[101] = 1
        row[96] = -55 / 640
        self.assertFalse(sees_opponent(row, sample, approaching=1))
        row[96] = 40 / 640
        self.assertTrue(sees_opponent(row, sample, approaching=1))
        self.assertFalse(sees_opponent(row, sample, approaching=-1))
        self.assertFalse(sees_opponent(row, dict(sample, grounded=False), approaching=1))
        later_enemy = dict(sample, actors={"hero": sample["actors"]["hero"], "enemies": [
            {"screenX": 385, "screenY": 177}
        ]})
        row[96] = 65 / 640
        self.assertFalse(sees_opponent(row, later_enemy, approaching=1))
        self.assertTrue(sees_opponent(row, later_enemy, approaching=1, approach_distance=8))
        samples = [[86 + n, 11, 5, int(n >= 6)] for n in range(18)]
        hit = dict(x=94, y=11, approachingVisibleEnemy=True)
        fields = ["x", "y", "health", "enemyKills"]
        self.assertEqual(protected_approach(samples, fields, [(4, 7)], hit), (4, 7, 6))
        samples[10][2] = 4
        self.assertIsNone(protected_approach(samples, fields, [(4, 7)], hit))

    def test_unharmed_branch_survives_farther_costly_route_and_can_be_retested_after_build(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            old, current = root / "old", root / "current"
            old.mkdir()
            current.mkdir()
            identity = dict(domain="hacker", base="demo", checkpoint="original", max_steps=1200, stall_steps=100)
            (old / "state.json").write_text(json.dumps({"identity": identity}))
            prior = dict(rightward_distance=70, damage=0)
            candidate = SimpleNamespace(save=lambda path: path.write_bytes(b"long route"))
            save_exploration_branch(candidate, dict(reason="stalled", damage=2, rightward_distance=132, new_cells=33),
                                    prior, "parent", old)
            safe = SimpleNamespace(save=lambda path: path.write_bytes(b"safe route"))
            save_exploration_branch(safe, dict(reason="stalled", damage=0, rightward_distance=124, new_cells=30),
                                    prior, "parent", old, needs_generalization=True)
            path, _ = exploration_checkpoint(current, "parent", identity, prefer_unharmed=True)
            self.assertEqual(path.read_bytes(), b"safe route")
            path, _ = exploration_checkpoint(current, "parent", identity, prefer_unharmed=False)
            self.assertEqual(path.read_bytes(), b"long route")
            self.assertIsNone(exploration_checkpoint(current, "other parent", identity, prefer_unharmed=True))
            self.assertIsNone(exploration_checkpoint(current, "parent", dict(identity, domain="gameplay"), True))

    def test_barrier_lesson_requires_grounded_unharmed_progress_after_a_stationary_nail_edge(self):
        fields = ["x", "y", "vx", "grounded", "health", "buttons"]
        samples = [[70.87, 11.4, 0, True, 5, 2]]
        samples.extend([70.87 + .75 * i, 11.4, 8.3, True, 5, 66 if i == 1 else 2] for i in range(1, 6))
        trail = {"fields": fields, "samples": samples}
        openings = set(opened_barriers(trail))
        self.assertEqual(openings, {(70.9, 11.4, 1)})
        timed = dict(fields=["step", *fields], samples=[[i, *row] for i, row in enumerate(samples)])
        native = [(i, np.array([i], dtype=np.float32), [2, 0, 0, int(i == 0)]) for i in range(4)]
        verified = confirmed_strikes(timed, native)
        self.assertEqual([contact for contact, _ in verified], list(openings))
        self.assertEqual([a[3] for _, a in verified[0][1]], [1, 0, 0, 0])
        with TemporaryDirectory() as directory:
            work = Path(directory)
            save_wall_strikes(work, 2000001, verified)
            self.assertEqual(list(work.iterdir()), [])
            save_wall_strikes(work, 100001, verified)
            with np.load(next(work.rglob("*.npz"))) as bank:
                self.assertEqual(bank["actions"][:, 3].tolist(), [1, 0, 0, 0])
        obs = np.zeros(3, dtype=np.float32)
        contact = (70.87, 11.4, 1, obs, [2, 0, 0, 0])
        examples = stalled_strikes([(*contact, 0), (*contact, 1), (82.89, *contact[1:], 0)], openings)
        self.assertEqual([action[3] for _, action in examples], [1, 0])
        samples[3][-1] = 0
        self.assertEqual(set(opened_barriers(trail)), openings)
        samples[3][-1] = 1
        self.assertFalse(list(opened_barriers(trail)))
        samples[3][-1] = 0
        samples[3][3] = False
        self.assertFalse(list(opened_barriers(trail)))
        samples[3][3], samples[3][4] = True, 4
        self.assertFalse(list(opened_barriers(trail)))

    def test_landing_strike_requires_sustained_health_and_landing_past_the_same_hazard(self):
        fields = ["x", "y", "vx", "health", "grounded"]
        rows = [[110 + i, 15 if i < 9 else 11, 8, 5, i >= 9] for i in range(20)]
        hit = {"x": 115, "y": 11, "descendingOnVisibleEnemy": True}
        self.assertEqual(protected_landing(rows, fields, [(4, 7)], hit), (4, 7, 9))
        self.assertIsNone(protected_landing(rows, fields, [(4, 7)], dict(hit, x=125)))
        rows[12][3] = 4
        self.assertIsNone(protected_landing(rows, fields, [(4, 7)], hit))

    def test_evaluation_seeds_are_reserved_across_restarts_and_builds(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            old = root / "previous-build"
            old.mkdir()
            historical = [dict(role="incumbent", seed=100000),
                          dict(role="held-out incumbent", seed=2000005),
                          dict(domain="gameplay", role="held-out candidate", seed=2000013)]
            (old / "episodes.jsonl").write_text("".join(json.dumps(row) + "\n" for row in historical))
            self.assertEqual(list(reserve_holdouts(root, "gameplay")), [2000014, 2000015, 2000016])
            self.assertEqual(list(reserve_holdouts(root, "gameplay")), [2000017, 2000018, 2000019])
            self.assertEqual(list(reserve_holdouts(root, "hacker")), [2000006, 2000007, 2000008])

    def test_interrupted_round_retests_only_integral_weights_from_its_training_seed_and_parent(self):
        with TemporaryDirectory() as directory:
            work = Path(directory)
            weights = work / "unharmed-exploration.zip"
            weights.write_bytes(b"trained candidate")
            prior = dict(seed=100008, reason="stalled", new_cells=9, rightward_distance=36,
                         completed=False, damage=0, reward=7)
            candidate = prior | dict(new_cells=12, rightward_distance=48, reward=10)
            (work / "unharmed-exploration.json").write_text(json.dumps(
                dict(parent="model", report=candidate, sha256=sha(weights))))
            self.assertEqual(interrupted_proposal(work, "model", prior), weights)
            self.assertIsNone(interrupted_proposal(work, "other", prior))
            self.assertIsNone(interrupted_proposal(work, "model", prior | dict(seed=100009)))
            weights.write_bytes(b"other weights")
            self.assertIsNone(interrupted_proposal(work, "model", prior))

    def test_supplied_candidate_requires_matching_observations_and_training_only_seeds(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            checkpoint = root / "parent"
            checkpoint.mkdir()
            (checkpoint / "model.zip").write_bytes(b"original")
            weights = root / "student.zip"
            weights.write_bytes(b"student")
            identity = dict(domain="gameplay", receiver="receiver", visualDetectorAndHUD="actors", visionSource="encoder")
            receipt = dict(parent=checkpoint.name, parentSHA256=sha(checkpoint / "model.zip"), modelFilename=weights.name,
                           sha256=sha(weights), trainingSeeds=[865101], environment=identity)
            path = root / "receipt.json"
            path.write_text(json.dumps(receipt))
            self.assertEqual(measured_candidate(path, checkpoint, identity), weights)
            with self.assertRaises(ValueError):
                measured_candidate(path, checkpoint, identity | dict(visualDetectorAndHUD="other"))
            path.write_text(json.dumps(receipt | dict(trainingSeeds=[2_000_000])))
            with self.assertRaises(ValueError):
                measured_candidate(path, checkpoint, identity)

    def test_local_nail_fit_preserves_all_other_controls_and_survives_checkpoint_reload(self):
        import gymnasium as gym
        from stable_baselines3 import PPO
        schema = ["other", "blockade_direction", "previous_attack", "previous_horizontal"]
        class Spec(gym.Env):
            observation_space = gym.spaces.Box(-4, 4, (16,), dtype=np.float32)
            action_space = gym.spaces.MultiDiscrete([3, 3, 2, 2])
        parent = PPO("MlpPolicy", Spec(), seed=17, device="cpu", policy_kwargs={"net_arch": [8, 8]})
        rng = np.random.default_rng(33)
        clear = rng.uniform(-1, 1, (12, 16)).astype(np.float32)
        clear[:, 1::4] = 0
        clear[:, 2::4] = rng.integers(0, 2, (len(clear), 4))
        examples = []
        for direction in (-1, 1):
            for previous in (0, 1):
                for basis in clear[:2]:
                    row = basis.copy()
                    row[-3:] = [direction, previous, direction]
                    examples.append((row, [2 if direction == 1 else 1, 0, 0, 1 - previous]))
        child = learn_blockade_response(parent, examples, schema, clear)
        batch = torch.as_tensor(np.concatenate((clear, np.stack([o for o, _ in examples]))))
        def logits(model):
            return model.policy.action_net(model.policy.mlp_extractor.policy_net(batch)).detach()
        torch.testing.assert_close(logits(child)[:, :-2], logits(parent)[:, :-2], atol=1e-6, rtol=0)
        torch.testing.assert_close(logits(child)[:len(clear)], logits(parent)[:len(clear)], atol=1e-6, rtol=0)
        actual = logits(child)[len(clear):, -2:].argmax(1).tolist()
        self.assertEqual(actual, [act[3] for _, act in examples])
        with TemporaryDirectory() as folder:
            path = Path(folder) / "learned.zip"
            child.save(path)
            torch.testing.assert_close(logits(PPO.load(path, device="cpu")), logits(child))

    def test_contact_climb_fit_changes_only_jump_near_an_upward_obstruction(self):
        import gymnasium as gym
        from stable_baselines3 import PPO
        schema = ["blockade_direction", "previous_horizontal", "previous_jump", "road_dy"]

        class Spec(gym.Env):
            observation_space = gym.spaces.Box(-4, 4, (16,), dtype=np.float32)
            action_space = gym.spaces.MultiDiscrete([3, 3, 2, 2])

        parent = PPO("MlpPolicy", Spec(), seed=17, device="cpu", policy_kwargs={"net_arch": [8, 8]})
        examples = []
        for direction in (-1, 1):
            for current in (0, 1):
                for previous in (0, 1):
                    row = np.zeros(16, dtype=np.float32)
                    row[-4:] = [direction, direction, current, 1.]
                    row[-6] = previous
                    examples.append((row, [2 if direction == 1 else 1, 0, 1 - current, 1]))
        upward = np.stack([row for row, _ in examples])
        flat, clear = upward.copy(), upward.copy()
        flat[:, -1] = 0
        clear[:, -4] = 0
        child = learn_climb_response(parent, examples, schema, flat)
        self.assertIsNotNone(child)
        inputs = torch.from_numpy(np.concatenate((flat, clear, upward)))

        def logits(model):
            policy = model.policy
            return policy.action_net(policy.mlp_extractor.policy_net(inputs)).detach()

        old, trained = logits(parent), logits(child)
        torch.testing.assert_close(trained[:, :6], old[:, :6], atol=1e-6, rtol=0)
        torch.testing.assert_close(trained[:, -2:], old[:, -2:], atol=1e-6, rtol=0)
        torch.testing.assert_close(trained[:16], old[:16], atol=1e-4, rtol=0)
        self.assertEqual(trained[16:, 6:8].argmax(1).tolist(), [action[2] for _, action in examples])

    def test_clean_climb_can_follow_an_earlier_hit_and_survives_a_later_failed_episode(self):
        row = np.zeros(155, dtype=np.float32)
        def climb(hurt):
            history = LandingRehearsal()
            states = [frame(x=111 + min(i, 6), y=9.4 if i <= 3 else 13 if i <= 6 else 11.4,
                            grounded=i <= 3 or i >= 7, damage=1 + int(i >= hurt), velocityY=10 if i == 4 else 0)
                      for i in range(20)]
            for i, (start, end) in enumerate(zip(states, states[1:])):
                history.record(i, row, [2, 0, int(3 <= i <= 5), 0], start, end)
            return history.confirmed
        valid = climb(17)
        self.assertEqual(len(valid), 1)
        self.assertFalse(climb(10))
        with TemporaryDirectory() as directory:
            root = Path(directory)
            save_landings(root, 2000001, valid)
            self.assertFalse((root / "clean-landings").exists())
            save_landings(root, 100002, valid)
            self.assertTrue(nearby_landings(root, dict(x=114, y=9.4)))
            self.assertFalse(nearby_landings(root, dict(x=114, y=13.4)))

    def test_authored_climb_reverses_from_a_dead_end_to_reach_a_higher_route(self):
        surfaces = [Surface(10, 23, 4), Surface(6, 9, 7), Surface(1, 4, 10)]
        graph = AuthoredRoute(surfaces, [])
        first = graph.route(0, 22, set())
        self.assertEqual((first.dest, first.direction), (1, -1))
        practice = RoutePractice(graph)
        self.assertEqual(practice.controls(frame(x=22, y=5.4), [2, 0, 0, 0]), (1, 0))
        self.assertEqual(practice.controls(frame(x=first.takeoff, y=5.4, step=12), [1, 0, 0, 0]), (1, 1))
        practice.controls(frame(x=first.landing, y=8.4, step=23), [1, 0, 0, 0])
        self.assertEqual(len(practice.landings), 1)
        wall = AuthoredRoute(surfaces, [(9.5, 3, 28)])
        self.assertIsNone(wall.route(0, 22, set()))

    def test_replay_route_can_target_a_lower_fork_instead_of_the_highest_shelf(self):
        graph = AuthoredRoute([Surface(10, 20, 4), Surface(6, 9, 7), Surface(21, 25, 6)], [])
        self.assertEqual(graph.route(0, 12, set()).dest, 1)
        self.assertEqual(graph.route(0, 12, set(), destination=2).dest, 2)
        guide = RoutePractice(graph, destination=2)
        self.assertEqual(guide.controls(frame(x=12, y=5.35), [0, 0, 0, 0]), (2, 0))
        self.assertIsNone(graph.route(2, 22, set(), destination=2))

    def test_climb_probes_an_early_ceiling_once_per_jump_and_releases_a_held_nail(self):
        graph = AuthoredRoute([Surface(0, 8, 0), Surface(6, 12, 4.9)], [])
        hop = graph.route(0, 3, set())
        practice = RoutePractice(graph)
        start = frame(x=hop.takeoff, y=1.4, step=10, velocityY=0)
        practice.controls(start, [2, 0, 0, 0])
        practice.controls(dict(start, heroY=2.7, step=11, grounded=False, velocityY=14), [2, 0, 1, 0])
        self.assertIsNone(practice.upward_controls)
        contact = dict(start, heroY=4.15, step=12, grounded=False, velocityY=0)
        practice.controls(contact, [2, 0, 1, 1])
        self.assertEqual(practice.upward_controls, (1, 0))
        practice.controls(dict(contact, step=13), [2, 1, 1, 0])
        self.assertEqual(practice.upward_controls, (1, 1))
        practice.controls(dict(contact, step=14), [2, 1, 1, 1])
        self.assertEqual(practice.upward_controls, (1, 0))
        self.assertEqual(practice.upward_strikes, 1)

    def test_tall_adjacent_step_can_be_cleared_by_jumping_before_steering(self):
        graph = AuthoredRoute([Surface(0, 2.88, 4), Surface(2.88, 5.88, 9.04)], [(2.88, 4, 9.04)])
        hop = graph.route(0, 1, set())
        self.assertEqual(hop.dest, 1)
        self.assertGreater(hop.steer_delay, 0)
        practice = RoutePractice(graph)
        self.assertEqual(practice.controls(frame(x=hop.takeoff, y=5.4), [0, 0, 0, 0]), (0, 1))
        self.assertEqual(practice.controls(frame(x=hop.takeoff, y=7.4, step=hop.steer_delay, grounded=False),
                                           [0, 0, 1, 0]), (2, 1))

    def test_route_continues_to_exit_trigger_just_past_the_final_authored_ledge(self):
        practice = RoutePractice(AuthoredRoute([Surface(10, 20, 4)], []))
        self.assertEqual(practice.controls(frame(x=19.5, y=5.4), [2, 0, 0, 0]), (2, 0))
        self.assertEqual(practice.controls(frame(x=22, y=5.4, step=3), [2, 0, 0, 0]), (2, 0))
        self.assertIsNone(practice.controls(frame(x=29, y=5.4, step=10), [2, 0, 0, 0]))

    def test_confirmed_combat_needs_a_visible_opponent_and_survival_after_the_kill(self):
        sample = frame(x=94, pixelsPerWorldUnitX=20, pixelsPerWorldUnitY=20, projectionPixelWidth=600,
                       actors=dict(hero={"screenX": 200, "screenY": 120}, enemies=[{"screenX": 230, "screenY": 120}]))
        row = np.zeros(155, dtype=np.float32)
        row[96], row[101] = .05, 1
        def encounter(hurt=None):
            practice = CombatRehearsal()
            for step in range(22):
                practice.record(step, row, row, [2, 0, 0, int(step == 7)], sample, step == hurt, step == 9)
            return practice.confirmed
        confirmed = encounter()
        self.assertEqual(len(confirmed), 1)
        self.assertEqual(encounter(hurt=13), [])
        row[96] = -.2
        self.assertEqual(encounter(), [])
        with TemporaryDirectory() as directory:
            work = Path(directory)
            save_combat(work, 2000000, confirmed)
            self.assertFalse((work / "confirmed-combat").exists())
            save_combat(work, 100001, confirmed)
            self.assertEqual(len(nearby_combat(work, dict(x=93, y=11.4))), 10)
            self.assertFalse(nearby_combat(work, dict(x=160, y=6)))

    def test_rehearsal_keeps_different_training_seeds_at_the_same_obstacle(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            prior = dict(end=dict(x=114, y=9.4))
            teacher = dict(seed=100002, damage=2, unharmedDistance=87., rightward_distance=120.)
            first = [(np.array([1.]), [2, 0, 1, 0])]
            self.assertEqual(len(rehearse_crossings(root, prior, teacher, first, safe_prefix=True)), 1)
            next_rows = [(np.array([2.]), [2, 0, 0, 1])]
            result = rehearse_crossings(root, prior, teacher | dict(seed=100003, damage=0), next_rows)
            self.assertEqual([int(row[0]) for row, _ in result], [2, 1])
            held = rehearse_crossings(root, prior, teacher | dict(seed=2000001), next_rows)
            self.assertIs(held, next_rows)
            self.assertEqual(len(list(root.rglob("*.npz"))), 2)

    def test_human_recovery_can_backtrack_and_stops_at_a_world_state_mismatch(self):
        route = [Decision(113 - n * .8, 9.4, -8, True, (1, 0, 1, 0)) for n in range(13)]
        practice = DemonstrationPractice([("training-clip", route)])
        sample = frame(x=113, y=9.4, velocityX=0, step=12)
        visited = {(28, 2)}
        self.assertIsNone(practice.controls(sample, visited, 2, [0, 0, 1, 0]))
        self.assertEqual(practice.controls(sample, visited, 10, [0, 0, 1, 0]).tolist(), [1, 0, 0, 0])
        self.assertEqual(practice.controls(sample | dict(step=13, heroX=112.2), visited, 11, [1, 0, 0, 0]).tolist(),
                         [1, 0, 1, 0])
        self.assertIsNone(practice.controls(sample | dict(step=14, heroY=15), visited, 12, [1, 0, 1, 0]))
        self.assertEqual(practice.windows, [(12, 14)])
        self.assertEqual(practice.clips, ["training-clip"])

    def test_sampled_frontier_associates_returns_with_applied_decision(self):
        heads = [torch.distributions.Categorical(probs=torch.as_tensor([p], dtype=torch.float32))
                 for p in ([0, 0, 1], [1, 0, 0], [1, 0], [0, 1])]
        policy = SimpleNamespace(get_distribution=lambda row: SimpleNamespace(distribution=heads))
        model = SimpleNamespace(policy=policy)
        search = SampledFrontier(42)
        row = np.array([1, 2], dtype=np.float32)
        self.assertIsNone(search.choose(model, row, 0, 0))
        self.assertEqual(search.choose(model, row, 7, 7).tolist(), [2, 0, 0, 1])
        search.rewards = [9.] * 7 + [1., 2.]
        states, actions, returns = search.experience()
        np.testing.assert_array_equal(states, [row])
        np.testing.assert_array_equal(actions, [[2, 0, 0, 1]])
        self.assertAlmostEqual(float(returns[0]), 1 + 0.96 * 2, places=5)

    def test_second_start_rehearses_the_childs_collision_and_requires_a_safe_unassisted_retest(self):
        control = dict(seed=100011, damage=0, reason="stalled", completed=False, new_cells=20, rightward_distance=79)
        candidate = control | dict(damage=2, new_cells=30, rightward_distance=132,
                                   first_damage=dict(step=148, x=125, y=11, approachingVisibleEnemy=True))
        trace = SimpleNamespace(anchors=[], maneuver=[("observed", [2, 0, 0, 1])])
        evidence = {}
        def trial(actor, seed, role, intervention=None, reference=None):
            if role == "second training incumbent":
                return control, trace
            if role == "second training correction":
                self.assertIs(reference, candidate)
                return candidate | dict(protectedApproach=dict(strikeStep=146, killStep=148)), trace
            return candidate, trace
        with patch("train_hacker_gym.learn_maneuver", return_value="repaired") as fitted:
            _, _, accepted = practice_another_start(trial, "prior", "child", control, candidate, trace,
                                                    11, [], True, evidence)
        fitted.assert_called_once()
        self.assertIn("secondTrainingRecovery", evidence)
        self.assertFalse(accepted)

    def test_second_training_start_uses_a_separate_seed_and_preserves_a_tie(self):
        base = dict(seed=100002, damage=0, reason="stalled", completed=False, new_cells=9, rightward_distance=36)
        calls = []
        def trial(model, seed, role):
            calls.append((seed, role))
            return base | dict(seed=seed), None
        evidence = {}
        child, result, qualified = practice_another_start(trial, "parent", "child", base, base, None,
                                                         2, None, True, evidence)
        self.assertEqual((child, result, qualified), ("child", base, True))
        self.assertEqual([seed for seed, _ in calls], [500002, 500002])
        self.assertNotIn("matched", evidence)

    def test_alternate_route_requires_two_new_connected_landing_cells(self):
        prior = dict(rightward_distance=8, new_cells=2, groundedCells=[[1, 1], [2, 1], [3, 1]])
        def branch(extra):
            return prior | dict(new_cells=prior["new_cells"] + len(extra), groundedCells=prior["groundedCells"] + extra)
        self.assertTrue(crosses_frontier(branch([[2, 2], [3, 2]]), prior))
        self.assertTrue(crosses_frontier(branch([[-1, 1], [0, 1]]), prior))
        self.assertFalse(crosses_frontier(branch([[2, 2], [8, 4]]), prior))
        self.assertFalse(crosses_frontier(branch([[2, 2]]), prior))

    def test_waiting_against_a_wall_is_excluded_from_the_protected_approach(self):
        path = [[i, 6*i, min(i, 6), 11] for i in range(21)]
        self.assertEqual(moving_approach_end(path, 18), 5)

    def test_cache_practice_turns_toward_a_deposit_and_yields_to_nearby_enemies(self):
        schema = SCHEMAS[7]
        row = np.zeros(len(schema), dtype=np.float32)
        row[4] = row[schema.index("geo_cache_present")] = 1
        row[schema.index("geo_cache_dx")] = -0.12
        sample = frame(x=166, y=6.4, facingRight=True, pixelsPerWorldUnitX=20, pixelsPerWorldUnitY=20,
                       projectionPixelWidth=600)
        work = CachePractice(schema)
        actor = np.array([2, 0, 0, 0])
        focused = CachePractice(schema, destination=(162.5, 6.4))
        self.assertIsNone(focused.controls(row, dict(sample, heroX=112), actor, [2, 0, 0, 0]))
        self.assertIsNone(focused.controls(row, dict(sample, heroY=14), actor, [2, 0, 0, 0]))
        self.assertFalse(focused.reached_destination)
        self.assertIsNotNone(focused.controls(row, sample, actor, [2, 0, 0, 0]))
        self.assertTrue(focused.reached_destination)
        self.assertEqual(work.controls(row, sample, actor, [2, 0, 0, 0]).tolist(), [1, 0, 0, 0])
        row[schema.index("geo_cache_dx")] = -0.045
        self.assertEqual(work.controls(row, sample | dict(step=1), actor, [1, 0, 0, 0]).tolist(), [1, 0, 0, 1])
        row[schema.index("enemy_0_present")] = 1
        row[schema.index("enemy_0_x")] = 0.04
        self.assertIsNone(work.target(row, sample))
        self.assertIsNone(work.controls(row, sample | dict(step=2), actor, [1, 0, 0, 1]))

    def test_blue_health_gain_is_scored_once_and_counter_boundaries_are_safe(self):
        start = frame(blueHealthOutcomes=dict(epoch="a", gained=0))
        score = EpisodeScore(start)
        gained = start | dict(step=1, blueHealthOutcomes=dict(epoch="a", gained=2))
        self.assertAlmostEqual(score.advance(gained)[0], 1.985)
        self.assertAlmostEqual(score.advance(gained | dict(step=2))[0], -.015)
        self.assertEqual(score.metrics()["blueHealthGained"], 2)
        self.assertAlmostEqual(score.advance(gained | dict(step=3, blueHealthOutcomes=dict(epoch="b", gained=10)))[0], -.015)
        self.assertAlmostEqual(score.advance(gained | dict(step=4, blueHealthOutcomes=None))[0], -.015)
        self.assertAlmostEqual(score.advance(gained | dict(step=5, blueHealthOutcomes=dict(epoch="b", gained=11)))[0], -.015)

    def test_verified_resource_outcomes_score_once_and_preserve_health_gate(self):
        initial = frame(geo=10, actors=dict(enemyOutcomes=dict(epoch="A", damage=0, kills=0)))
        score = EpisodeScore(initial)
        mined = initial | dict(step=6, geo=13, depositHits=2, depositsMined=1,
                               actors=dict(enemyOutcomes=dict(epoch="A", damage=8, kills=1)))
        self.assertGreater(score.advance(mined)[0], 3)
        self.assertLess(score.advance(mined | dict(step=7))[0], 0)
        self.assertEqual(score.last_activity, 6)
        self.assertEqual((score.geo_earned, score.deposit_hits, score.deposits_mined, score.enemy_kills), (3, 2, 1, 1))
        prior = dict(new_cells=2, rightward_distance=8, damage=0, completed=False, reason="stalled", reward=1)
        improvement = prior | dict(depositsMined=1, reward=3)
        self.assertTrue(wins(improvement, prior))
        self.assertFalse(wins(improvement | dict(damage=1), prior))

    def test_first_crossing_is_local_and_requires_a_clean_landing(self):
        trajectory = [[i, i*6, 35+i, 11.4, 8.3, 0, True, 5] for i in range(65)]
        windows = [(6, 15), (45, 53)]
        examples = [(i, i, [2, 0, 0, i%2]) for start, end in windows for i in range(start, end+1)]
        route = [(i, i, (2, 0, 0, 0)) for i in range(65)]
        lesson, anchors = first_crossing_lesson(dict(rightward_distance=10), trajectory, windows, examples, route)
        self.assertEqual([i for i, _ in lesson], list(range(6, 16)))
        self.assertIn(0, anchors)
        trajectory[13][7] = 4
        self.assertIsNone(first_crossing_lesson(dict(rightward_distance=10), trajectory, windows, examples, route))

    def test_marked_barrier_lesson_keeps_steering_and_records_only_productive_controls(self):
        schema = SCHEMAS[7]
        env = SimpleNamespace(version=7, domain="hacker", episode="test", usable=True, close=lambda: None)
        inputs = []
        def observation(blocked):
            row = np.zeros(len(schema) * 4, np.float32)
            row[schema.index("blockade_direction") - len(schema)] = blocked
            return row
        def reset(seed):
            env.last_frame = frame(ticks=0, velocityX=0, velocityY=0)
            env.score = EpisodeScore(env.last_frame)
            return observation(False), {}
        def step(action):
            inputs.append(action.tolist())
            opened = any(a[3] for a in inputs)
            index, x = len(inputs), env.last_frame["heroX"]
            env.last_frame = frame(x=x + int(opened), step=index, ticks=index * 6,
                                   velocityX=8.3 if opened else 0, velocityY=0)
            reward, _ = env.score.advance(env.last_frame, path_progress=0.25 if opened else 0)
            return (observation(not opened and index >= 4), reward, False, index >= 22,
                    dict(applied=inputs[-1], reason="time-limit" if index >= 22 else ""))
        env.reset, env.step = reset, step
        driver = SimpleNamespace(predict=lambda obs, deterministic: (np.array([2, 0, 0, 0]), None))
        report, trace, trajectory = rollout(env, driver, 12, (0, 0, "strike marked barriers"))
        self.assertEqual([i for i, act in enumerate(inputs) if act[3]], [4])
        self.assertTrue(all(act[:3] == [2, 0, 0] for act in inputs))
        self.assertEqual((report["markedBarrierAttempts"], report["teachingWindows"]), (1, 1))
        self.assertGreater(report["new_cells"], 2)
        self.assertTrue(any(act[3] for _, act in trace.maneuver))
        self.assertEqual(len(trajectory), len(inputs) + 1)

    def test_platform_teacher_uses_exposed_edge_and_can_climb_left(self):
        sample = frame(x=112, y=9.4, pixelsPerWorldUnitX=20, pixelsPerWorldUnitY=20, projectionPixelWidth=600)
        row = np.zeros(155, dtype=np.float32)
        row[3], row[4], row[6] = 1.3 / 30, 1, 1
        segments = row[16:96].reshape(16, 5)
        segments[0] = [-3 / 30, 1.65 / 30, -1.4 / 30, 1, 1]
        segments[1] = [1.65 / 30, 12 / 30, 0.6 / 30, 1, 1]
        segments[2] = [-12 / 30, -2.9 / 30, 0.6 / 30, 1, 1]
        plan = reachable_platform(row, sample, set())
        self.assertEqual(plan.direction, 1)
        self.assertEqual(plan.controls(sample, [2, 0, 1, 0]), (0, 0))
        self.assertEqual(plan.controls(sample | dict(step=1), [0, 0, 0, 0]), (2, 1))
        on_ledge = sample | dict(step=12, heroX=plan.landing, heroY=plan.top + 1.4)
        self.assertEqual(plan.controls(on_ledge, [0, 0, 0, 0]), (2, 0))
        self.assertIs(plan.landed, True)
        self.assertGreater(plan.walk_to, plan.landing + 3)
        self.assertLess(plan.walk_to, 124)
        self.assertIsNone(plan.controls(on_ledge | dict(step=20, heroX=plan.walk_to), [2, 0, 0, 0]))
        misses = [(plan.landing, plan.top), (plan.landing + 0.2, plan.top)]
        self.assertEqual(reachable_platform(row, sample, set(), misses).direction, -1)
        segments[1, 2] = 9 / 30  # Above the Knight's jump.
        self.assertEqual(reachable_platform(row, sample, set()).direction, -1)
        segments[2, 2] = -0.55 / 30  # Duplicated floor below the standing Knight.
        self.assertIsNone(reachable_platform(row, sample, set()))
        row[4] = 0
        self.assertIsNone(reachable_platform(row, sample, set()))

    def test_platform_teacher_reads_compact_markings_with_appended_policy_inputs(self):
        sample = frame(x=112, y=9.4, pixelsPerWorldUnitX=20, pixelsPerWorldUnitY=20, projectionPixelWidth=600)
        for version in (13, 14, 15, 16):
            with self.subTest(version=version):
                schema = SCHEMAS[version]
                row = np.zeros(len(schema), dtype=np.float32)
                row[3], row[4], row[6] = 1.3 / 30, 1, 1
                start = schema.index("marking_0_start_dx")
                ground = .25 if version == 13 else .33
                row[start:start + 12] = [-3 / 30, -1.4 / 30, 1.65 / 30, -1.4 / 30, ground, 1,
                                         1.65 / 30, .6 / 30, 12 / 30, .6 / 30, ground, 1]
                plan = reachable_platform(row, sample, set())
                self.assertIsNotNone(plan)
                self.assertAlmostEqual(plan.landing, 115.15, places=4)

    def test_enemy_rehearsal_reads_compact_slots_with_appended_policy_inputs(self):
        sample = frame(actors={"hero": {"screenX": 320, "screenY": 180},
                               "enemies": [{"screenX": 360, "screenY": 177}]},
                       projectionPixelWidth=640, pixelsPerWorldUnitX=10, pixelsPerWorldUnitY=10)
        for version in (13, 14, 15, 16):
            with self.subTest(version=version):
                schema = SCHEMAS[version]
                row = np.zeros(len(schema), dtype=np.float32)
                slot = schema.index("enemy_0_x")
                row[slot:slot + 6] = [40 / 640, -3 / 640, 0, 0, 1, 1]
                self.assertTrue(sees_opponent(row, sample, approaching=1))

    def test_terminal_stall_and_post_damage_actions_are_excluded_from_lesson(self):
        windows = [(20, 30), (100, 110), (160, 180), (190, 210)]
        outcomes = [1, 18, 36, 104, 189]
        self.assertEqual(productive_windows(windows, outcomes, 120), [(20, 30), (100, 110)])
        self.assertEqual(productive_windows(windows, [20, 60], 220), [])

    def test_safe_lesson_requires_landings_and_applied_actions_before_damage(self):
        prior = dict(new_cells=4, rightward_distance=12)
        actions = [(step, None, []) for step in range(10, 20)]
        hit = dict(step=30)
        self.assertEqual(safe_crossing_prefix(prior, (14, 6, 20), hit, actions), 14)
        self.assertIsNone(safe_crossing_prefix(prior, (13, 6, 20), hit, actions))
        self.assertIsNone(safe_crossing_prefix(prior, (14, 5, 20), hit, actions))
        self.assertIsNone(safe_crossing_prefix(prior, (14, 6, 19), hit, actions))

    def test_enemy_crossing_uses_direction_height_and_actor_edges(self):
        knight = dict(screenX=100, screenY=80, width=10, height=28)
        enemy = dict(screenX=190, screenY=72, width=32, height=20)
        sample = dict(grounded=True, pixelsPerWorldUnitX=20, pixelsPerWorldUnitY=20,
                      actors=dict(hero=knight, enemies=[enemy]))
        self.assertEqual(enemy_maneuver(sample, 2), "jump")
        self.assertIsNone(enemy_maneuver(sample, 1))
        self.assertIsNone(enemy_maneuver(sample | dict(grounded=False), 2))
        enemy.update(screenX=10)
        self.assertEqual(enemy_maneuver(sample, 1), "jump")
        enemy.update(screenY=20)
        self.assertIsNone(enemy_maneuver(sample, 1))
        enemy.update(screenX=120)
        falling = sample | dict(grounded=False, velocityY=-14)
        self.assertEqual(enemy_maneuver(falling, 2), "downward nail")
        self.assertIsNone(enemy_maneuver(falling | dict(velocityY=2), 2))

    def test_visual_encoder_uses_pixels_and_keeps_eligible_occluded_input(self):
        env = HackerGym(game="game", vision="vision", labels="unused", version=7, domain="gameplay")
        source = io.BytesIO()
        reader, writer = os.pipe()
        env.perception = Counter()
        observation = np.zeros(env.observation_space.shape, dtype="<f4")
        response = dict(input=base64.b64encode(observation.tobytes()).decode(), issues=["missing-knight"],
                        visibleMasks=4, healthKnown=True)
        with os.fdopen(reader, "rb") as output, os.fdopen(writer, "wb") as reply:
            env.encoder = SimpleNamespace(stdin=source, stdout=output)
            # The encoder writes one line per request; avoid pipe buffer limits in this test.
            import threading
            def answer():
                reply.write(json.dumps(response).encode() + b"\n")
                reply.flush()
            worker = threading.Thread(target=answer)
            worker.start()
            values = env._encode(dict(step=1, ticks=6, pixelWidth=640, pixelHeight=360, health=4,
                                      heroX=34.5, actors={"enemies": []}, sceneName="Tutorial_01"), [2, 0, 0, 0])
            worker.join()
            env.encoder_replies.close()
        self.assertTrue(env.usable)
        self.assertEqual(values.shape, env.observation_space.shape)
        self.assertEqual(env.perception["correctHealth"], 1)
        message = json.loads(source.getvalue())
        self.assertEqual(message["previous"], [2, 0, 0, 0])
        self.assertEqual(message["sample"]["heroX"], 34.5)

    def test_fresh_ground_only_and_damage_always_counts(self):
        score = EpisodeScore(frame())
        for state in (frame(x=40, grounded=False), frame(x=40, grounded=False, y=16)):
            reward, _ = score.advance(state)
            self.assertLess(reward, 0)
        self.assertGreater(score.advance(frame(x=40, step=3), path_progress=0.25)[0], 0)
        self.assertLess(score.advance(frame(x=40, step=4))[0], 0)
        self.assertLess(score.advance(frame(x=34.5, step=5))[0], 0)
        self.assertLess(score.advance(frame(x=48, damage=2, health=3))[0], -9)
        self.assertEqual(score.metrics()["new_cells"], 1)
        self.assertEqual(EpisodeScore(frame()).metrics()["new_cells"], 0)

    def test_matching_exit_objective_uses_verified_training_snapshots(self):
        from train_hacker_gym import checkpoint_path_inputs
        with TemporaryDirectory() as directory:
            run = Path(directory)
            inputs, evidence = run / "exit-path-inputs", run / "exit-path-evidence"
            inputs.mkdir(); evidence.mkdir()
            labels, connections = inputs / "labels.json", inputs / "connections.json"
            labels.write_text("labels"); connections.write_text("connections")
            item = dict(objective=dict(x=192.181, y=63.408), labelsSHA256=sha(labels),
                        connectionsSHA256=sha(connections))
            (evidence / "demo.json").write_text(json.dumps(item))
            self.assertEqual(checkpoint_path_inputs(run, [192.181, 63.408]), (labels, connections))
            self.assertIsNone(checkpoint_path_inputs(run, [1, 2]))
            labels.write_text("changed")
            with self.assertRaisesRegex(ValueError, "do not match"):
                checkpoint_path_inputs(run, [192.181, 63.408])

    def test_signed_alignment_rewards_physical_motion(self):
        score = EpisodeScore(frame())
        self.assertGreater(score.advance(frame(step=1), path_progress=0, path_alignment=.25)[0], 0)
        self.assertLess(score.advance(frame(step=2), path_progress=0, path_alignment=-.25)[0], -.125)
        self.assertLess(score.advance(frame(step=3), path_progress=0, path_alignment=0)[0], 0)
        self.assertEqual(score.metrics()["pathAlignment"], 0)
        self.assertEqual(score.metrics()["pathAlignmentSamples"], 3)
        with self.assertRaisesRegex(ValueError, "path alignment"):
            score.advance(frame(step=4), path_alignment=float("nan"))

    def test_path_score_and_comparison_ignore_map_cell_growth(self):
        score = EpisodeScore(frame())
        self.assertLess(score.advance(frame(x=48, step=1), path_progress=0)[0], 0)
        self.assertGreater(score.advance(frame(x=48, step=2), path_progress=.5)[0], 0)
        self.assertEqual(score.metrics()["pathProgress"], .5)
        self.assertEqual(score.metrics()["pathSamples"], 2)
        self.assertEqual(score.last_progress, 2)
        with self.assertRaisesRegex(ValueError, "path progress"):
            score.advance(frame(step=3), path_progress=-1)
        from train_hacker_gym import general_improvement, preserves_general_outcomes
        old = dict(damage=0, reason="stalled", enemyDamage=0, enemyKills=0, reward=1,
                   longestDwellDecisions=10, pathProgress=2, pathSamples=40,
                   new_cells=5, rightward_distance=20)
        more_ground = old | dict(new_cells=100, rightward_distance=500, reward=2)
        self.assertFalse(general_improvement(more_ground, old))
        advanced = old | dict(pathProgress=2.5)
        self.assertTrue(general_improvement(advanced, old))
        self.assertFalse(preserves_general_outcomes(advanced | dict(pathSamples=0), old))
        self.assertFalse(preserves_general_outcomes(advanced | dict(damage=1), old))

    def test_town_has_no_arrival_trigger_or_loading_reward_adjustment(self):
        score = EpisodeScore(frame())
        self.assertEqual(score.advance(frame(sceneName="Town", gameState="ENTERING_LEVEL")), (-.015, ""))
        self.assertEqual(score.advance(frame(sceneName="Town")), (-.015, ""))
        self.assertFalse(score.completed)
        self.assertEqual(score.advance(frame(sceneName="Town", health=0))[1], "death")
        self.assertEqual(score.advance(frame(sceneName="Menu_Title"))[1], "unexpected-scene")

    def test_late_correction_preserves_the_approach(self):
        layer = torch.nn.Linear(3, 10)
        torch.nn.init.zeros_(layer.weight)
        torch.nn.init.zeros_(layer.bias)
        model = SimpleNamespace(policy=SimpleNamespace(action_net=layer, pi_features_extractor=None,
            mlp_extractor=SimpleNamespace(forward_actor=lambda tensor: tensor),
            extract_features=lambda tensor, _: tensor))
        approach = np.array([1., 0., 0.], dtype=np.float32)
        obstacle = np.array([1., 2., 0.], dtype=np.float32)
        adjust(model, [obstacle], 0, 2, 2., protected=[approach])
        self.assertLess(float(layer(torch.from_numpy(approach)).detach().abs().max()), 1e-5)
        self.assertEqual(int(layer(torch.from_numpy(obstacle))[:3].argmax()), 2)
        released = np.array([1., 0., 2.], dtype=np.float32)
        learned = learn_maneuver(model, [(obstacle, [2, 0, 0, 1]), (released, [2, 0, 0, 0])], [approach])
        outputs = learned.policy.action_net(torch.as_tensor(np.stack([approach, obstacle, released]))).detach()
        self.assertLess(float(outputs[0].abs().max()), 1e-5)
        self.assertEqual(outputs[1:, 8:].argmax(dim=1).tolist(), [1, 0])
        jumping = learn_maneuver(model, [(obstacle, [2, 0, 1, 0]), (released, [2, 0, 0, 0])],
                                 [approach], heads=(0, 2, 3))
        jump_scores = jumping.policy.action_net(torch.as_tensor(np.stack([obstacle, released])))
        self.assertEqual(jump_scores[:, 6:8].argmax(dim=1).tolist(), [1, 0])

        combat = learn_maneuver(model, [(obstacle, [2, 0, 0, 1]), (released, [2, 0, 0, 0])],
                                [approach], np.stack([approach, released]), heads=(1, 3))
        retained = [0, 1, 2, 6, 7]
        self.assertTrue(torch.equal(combat.policy.action_net.weight[retained], layer.weight[retained]))
        self.assertTrue(torch.equal(combat.policy.action_net.bias[retained], layer.bias[retained]))
        self.assertFalse(torch.equal(combat.policy.action_net.weight[8:], layer.weight[8:]))

    def test_long_maneuver_keeps_turn_and_button_edges(self):
        maneuver = [(i, [1 if i < 35 else 2, 0, int(40 <= i < 43), 0]) for i in range(150)]
        selected = retain_maneuver(maneuver, limit=30)
        steps = [step for step, _ in selected]
        self.assertEqual(len(set(steps)), 30)
        self.assertTrue({0, 34, 35, 39, 40, 42, 43, 149}.issubset(steps))
        route = [(i, i, tuple(action)) for i, action in maneuver]
        anchors = route_anchors(route, 24)
        self.assertTrue({0, 1, 2, 3, 35, 40, 43}.issubset(anchors))

    def test_gate_and_buttons(self):
        prior = dict(new_cells=2, completed=False, damage=1, reward=-3, reason="stalled", rightward_distance=5)
        better = prior | dict(new_cells=4, reward=-1)
        self.assertTrue(wins(better, prior))
        self.assertTrue(wins(better | dict(openingLeftDistance=12, returnedFromLeft=True), prior))
        self.assertFalse(wins(better, prior, require_crossing=True))
        self.assertTrue(wins(better | dict(rightward_distance=10), prior, require_crossing=True))
        for regression in (dict(damage=2), dict(reason="death"), dict(rightward_distance=0)):
            self.assertFalse(wins(better | regression, prior))
        self.assertFalse(wins(prior, prior))
        finished = prior | dict(completed=True, reason="exit")
        self.assertFalse(wins(finished, prior))
        self.assertFalse(holds_progress(finished | dict(new_cells=1), prior))
        self.assertTrue(wins(better, finished))
        self.assertTrue(holds_progress(prior, prior))
        self.assertFalse(holds_progress(prior | dict(damage=2), prior))
        self.assertFalse(holds_progress(prior | dict(new_cells=1), prior))
        self.assertEqual(buttons([2, 1, 1, 0]), 2 | 8 | 32)
        self.assertEqual(buttons([1, 2, 0, 1]), 1 | 4 | 64)
