import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

import gymnasium as gym
import numpy as np
import torch

from gym_authored_route import AuthoredRoute, Surface
from gym_blockade_response import nearby_wall_strikes
from gym_general_training import (EncounterExplorationPolicy, EncounterPractice, PracticeLessons,
                                  PracticeStarts, idle_navigation_context, practice_temperatures,
                                  soften_action_sampling)
from gym_route_rehearsal import store_route_starts
from hacker_gym import Dwell, EpisodeScore, HackerGym
from hkv_bot_dataset import SCHEMAS


class PracticeRoom(gym.Env):
    version = 7
    max_steps = 80
    usable = True

    def reset(self, *, seed=None, options=None):
        self.last_frame = dict(step=0, heroX=5., heroY=12., grounded=True, damage=0,
                               health=5, heroAvailable=True, sceneName="Tutorial_01")
        self.score = EpisodeScore(self.last_frame)
        self.last_action = None
        self.seed_used = seed
        self.reason = ""
        return self.observation(), {}

    def observation(self):
        row = np.zeros(len(SCHEMAS[self.version]) * 4, dtype=np.float32)
        if self.last_frame["step"] >= 3:
            row[SCHEMAS[self.version].index("enemy_0_present") - len(SCHEMAS[self.version])] = 1
        return row

    def step(self, action):
        self.last_action = list(action)
        self.last_frame["step"] += 1
        if self.last_frame["step"] >= 5:
            self.last_frame["heroY"] = 9.
        reward, _ = self.score.advance(self.last_frame)
        return self.observation(), reward, False, bool(self.reason), dict(reason=self.reason, applied=list(action))


class Starter:
    def predict(self, observation, deterministic):
        return np.array([1, 0, 0, 0]), None


class GeneralPracticeTests(unittest.TestCase):
    def test_finishing_training_skips_unused_practice_reset_and_keeps_real_terminals(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            action = np.array([2, 0, 0, 0])
            for finishes_naturally in (False, True):
                room = PracticeRoom()
                practice = EncounterPractice(room, Starter(), 0, work / "practice.jsonl")
                practice.batch_step_limit = 4
                practice.reset()
                practice.horizon = 2
                self.assertFalse(practice.step(action)[3])
                self.assertEqual(practice.step(action)[2:4], (False, True))
                practice.reset()
                practice.horizon = 2
                self.assertFalse(practice.step(action)[3])
                if finishes_naturally:
                    room.reason = "stalled"
                _, _, done, cut, _ = practice.step(action)
                self.assertEqual((done, cut), (finishes_naturally, False))
                self.assertEqual(practice.batch_steps, 4)
                self.assertEqual(len(practice.seeds), 2)
                if done:
                    practice.reset()
                    self.assertEqual(room.last_frame["step"], 0)
                    self.assertEqual(len(practice.seeds), 2)

    def test_retained_route_starts_survive_newer_recordings_and_prefer_unused_seeds(self):
        import random

        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            recent = work / "route-starts"
            recent.mkdir()

            def site(seed, actions, y):
                return dict(seed=seed, actions=[[2, 0, 0, 0]] * actions, x=90.0, y=y, health=4, grounded=True)

            retained = site(190007, 60, 62.0)
            for index in range(9):
                (recent / f"{index}.json").write_text(json.dumps([site(190011, 40 + index, 17.0)]))
            newest = max(recent.iterdir(), key=lambda path: (path.stat().st_mtime_ns, path.name))
            newest.write_text(json.dumps([site(190011, 40, 17.0), retained]))
            (work / "retained-route-starts.json").write_text(json.dumps([retained]))

            starts = PracticeStarts(work / "practice-starts.json")
            self.assertEqual(starts.route.count(retained), 1)
            self.assertEqual(starts.choose("route", random.Random(1), 65, [190011]), retained)
            self.assertEqual(starts.choose("route", random.Random(1), 50, [190011])["seed"], 190011)
            newest.write_text(json.dumps([site(190011, 40, 17.0)]))
            starts = PracticeStarts(work / "practice-starts.json")
            self.assertEqual(starts.choose("route", random.Random(1), 65, [190011]), retained)

    def test_completed_route_supplies_diverse_replay_prefixes_with_playable_time_left(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            fields = ("step", "x", "y", "grounded", "health", "buttons", "scene", "gameState", "heroAvailable")
            samples = [(i, 34.5 + i, 11.4 if i < 250 else 61.4, True, 5 if i < 350 else 3,
                        2 if i else 0, "Tutorial_01", "PLAYING", True) for i in range(601)]
            samples[-1] = (600, 4.3, 44.4, True, 3, 0, "Town", "PLAYING", True)
            report = dict(seed=190_007, completed=True, reason="exit", steps=600,
                          end=dict(scene="Town", gameState="PLAYING", heroAvailable=True))
            self.assertEqual(store_route_starts(work, report, fields, samples, 650), 14)
            routes = PracticeStarts(work / "practice-starts.json")
            self.assertEqual(len(routes.route), 14)
            self.assertTrue(any(r["y"] > 60 for r in routes.route))
            self.assertTrue(any(r["y"] < 12 for r in routes.route))
            self.assertLessEqual(max(len(r["actions"]) for r in routes.route), 650 - 256)
            self.assertTrue(all(r["actions"][0] == [2, 0, 0, 0] for r in routes.route))
            samples[-1] = samples[-2]
            self.assertEqual(store_route_starts(work, report, fields, samples, 650), 0)

            samples[-1] = (600, 170., 61.4, False, 0, 0, "Tutorial_01", "PLAYING", True)
            partial = dict(seed=190_011, completed=False, reason="death", steps=600, renderHz=60,
                           end=dict(scene="Tutorial_01"), authoredRouteStartStep=180, authoredRouteLandings=5)
            self.assertEqual(store_route_starts(work, partial, fields, samples, 650), 14)
            prepared = json.loads((work / "route-starts/190011.json").read_text())
            self.assertTrue(all(r["health"] >= 3 and r["renderHz"] == 60 and len(r["actions"]) >= 180 for r in prepared))
            self.assertTrue(any(r["y"] > 60 for r in prepared))
            self.assertEqual(store_route_starts(work, dict(partial, authoredRouteLandings=2), fields, samples, 650), 0)

            samples[360:366] = [(i, 680 + i, 61.4, True, 2, 2, "Tutorial_01", "PLAYING", True)
                                for i in range(360, 366)]
            for i in range(366, 600):
                samples[i] = tuple(2 if col == 4 else v for col, v in enumerate(samples[i]))
            learner = dict(seed=980_015, completed=False, reason="death", steps=600, renderHz=60,
                           end=dict(scene="Tutorial_01"), pathProgress=160.)
            incumbent = dict(seed=learner["seed"], pathProgress=60.,
                             groundedCells=sorted({(int(row[1] // 4), int(row[2] // 4)) for row in samples[:250]}))
            self.assertEqual(store_route_starts(work, learner, fields, samples, 650), 0)
            self.assertEqual(store_route_starts(work, learner, fields, samples, 650,
                                               baseline=dict(incumbent, seed=2_000_015)), 0)
            self.assertEqual(store_route_starts(work, learner, fields, samples, 650,
                                               baseline=incumbent), 14)
            stored = json.loads((work / "route-starts/980015.json").read_text())
            self.assertTrue(all(250 <= len(r["actions"]) < 354 and r["health"] >= 3 for r in stored))

    def test_route_practice_balances_training_seeds_and_can_focus_on_later_landings(self):
        starts = PracticeStarts(None)
        starts.route = [dict(seed=seed, actions=[[2, 0, 0, 0]] * i)
                        for seed, count in [(190_007, 8), (190_011, 4)] for i in range(1, count + 1)]
        selected = starts.choose("route", SimpleNamespace(choice=lambda rows: rows[-1], random=lambda: .1), 3)
        self.assertEqual((selected["seed"], len(selected["actions"])), (190_011, 3))
        early = starts.choose("route", SimpleNamespace(choice=lambda rows: rows[0], random=lambda: .9), 3)
        self.assertEqual((early["seed"], len(early["actions"])), (190_007, 1))

    def test_route_replay_hands_off_at_the_first_large_divergence(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            class ReferenceRateRoom(PracticeRoom):
                def reset(self, *, seed=None, options=None):
                    self.reset_options = options
                    return super().reset(seed=seed, options=options)

            room = ReferenceRateRoom()
            room.max_steps = 300
            starts = PracticeStarts(None)
            starts.route = [dict(seed=190_007, actions=[[2, 0, 0, 0]] * 4, x=50, y=9, health=5,
                                 grounded=True, checkpoints=[[1, 30, 12, 5]], renderHz=60)]
            env = EncounterPractice(room, Starter(), 5, work / "log", starts)
            env.reset()
            record = json.loads((work / "log").read_text())
            self.assertEqual(record["requested"], "route")
            self.assertEqual(record["setupSteps"], 1)
            self.assertEqual(room.reset_options, {"render_hz": 60})
            self.assertEqual(record["replayInterrupted"]["reason"], "diverged")
            self.assertFalse(record["replayed"])
            self.assertEqual(env.episode_metrics()["steps"], 0)

    def test_route_repair_uses_live_actions_before_the_policy_handoff(self):
        class MovingRoom(PracticeRoom):
            def reset(self, *, seed=None, options=None):
                self.ended = False
                return super().reset(seed=seed, options=options)

            def step(self, action):
                result = super().step(action)
                if action[0] == 2:
                    self.last_frame["heroX"] += 2
                return result

        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            room = MovingRoom()
            room.max_steps = 300
            starts = PracticeStarts(None)
            starts.route = [dict(seed=190_007, actions=[[2, 0, 0, 0]] * 4, x=14, y=12, health=5,
                                 grounded=True, checkpoints=[[1, 30, 12, 5]])]
            graph = AuthoredRoute([Surface(0, 30, 10.65)], [])
            env = EncounterPractice(room, Starter(), 5, work / "log", starts, route_graph=graph)
            env.reset()
            record = json.loads((work / "log").read_text())
            self.assertTrue(record["routeRecovered"])
            self.assertTrue(record["reached"])
            self.assertFalse(record["replayed"])
            self.assertGreater(record["setupSteps"], record["replayInterrupted"]["step"])
            self.assertEqual(env.episode_metrics()["steps"], 0)

    def test_replay_starts_a_full_stall_window_without_resetting_outcomes(self):
        class ReplayRoom(HackerGym):
            def reset(self, *, seed=None, options=None):
                self.episode = "fixture"
                self.last_frame = dict(step=0, heroX=5., heroY=12., grounded=True, damage=0, health=5,
                                       heroAvailable=True, sceneName="Tutorial_01", gameState="PLAYING")
                self.score = EpisodeScore(self.last_frame)
                self.ended, self.usable, self.issues = False, True, []
                self.last_control_gap = self.missing_steps = 0
                return np.zeros(self.observation_space.shape, np.float32), {}

            def _receive(self, step):
                self.last_frame = dict(self.last_frame, step=step, damage=1, health=4)
                return self.last_frame

            def _encode(self, frame, applied):
                self.path_progress = 1. if frame["step"] == 1 else 0.
                return np.zeros(self.observation_space.shape, np.float32)

        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            room = ReplayRoom(game="game", vision="vision", labels="labels", version=7,
                              max_steps=300, stall_steps=8)
            room.directory = work
            attack = [0, 0, 0, 1]
            starts = PracticeStarts(None)
            starts.route = [dict(seed=190_007, actions=[attack] * 6, x=5, y=12, health=4, grounded=True)]
            env = EncounterPractice(room, Starter(), 5, work / "log", starts)
            env.reset()
            self.assertEqual(room.last_frame["step"], 6)
            self.assertEqual(room.score.damage, 1)
            self.assertEqual(room.score.path_progress, 1.)
            self.assertEqual(room.score.dwell.steps, 0)
            self.assertEqual(env.episode_metrics()["heldAttackDecisions"], 0)
            for decision in range(1, 9):
                _, reward, stopped, truncated, info = env.step(attack)
                self.assertAlmostEqual(reward, -.065)
                self.assertEqual(stopped, decision == 8)
                self.assertFalse(truncated)
                self.assertEqual(info["reason"], "stalled" if decision == 8 else "")
            self.assertEqual(env.episode_metrics()["heldAttackDecisions"], 8)
            self.assertEqual(env.episode_metrics()["damage"], 0)

    def test_practice_restores_exploration_for_a_saturated_head(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            shelf = root / "training-observations"
            shelf.mkdir()
            schema = SCHEMAS[14]
            observations = np.zeros((8, len(schema) * 4), dtype=np.float32)
            for key in ("previous_attack", "blockade_direction"):
                observations[:, schema.index(key) - len(schema)] = 1
            np.savez(shelf / "6_300000.npz", observations=observations)
            heads = [None, None, SimpleNamespace(probs=torch.tensor([[.9, .1]] * 8)),
                     SimpleNamespace(probs=torch.tensor([[.01, .99]] * 8))]
            policy = SimpleNamespace(get_distribution=lambda _: SimpleNamespace(distribution=heads))
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (1.0, 1.0))
            heads[2].probs[:, 1] = .01
            heads[3].probs[:, 0] = .2
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (1.0, 4))

            idx = {name: schema.index(name) - len(schema) for name in
                   ("blockade_direction", "enemy_0_present", "enemy_0_x", "enemy_0_y")}
            observations[:, idx["blockade_direction"]] = 0
            observations[:, idx["enemy_0_present"]] = 1
            observations[:2, idx["enemy_0_x"]] = .4
            observations[2, idx["enemy_0_y"]] = -.3
            heads[3].probs[:, 0] = torch.tensor([.01, .01, .01, .02, .03, .02, .02, .7])
            np.savez(shelf / "6_300000.npz", observations=observations)
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (1.0, 1.0))
            observations[2, idx["enemy_0_y"]] = -.1
            np.savez(shelf / "6_300000.npz", observations=observations)
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (3, 1.0))

            observations[:, schema.index("previous_attack") - len(schema)] = 0
            heads[3].probs[:, 1] = .01
            np.savez(shelf / "6_300000.npz", observations=observations)
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (3, 1.0))
            heads[3].probs[:, 1] = .4
            self.assertEqual(practice_temperatures(policy, root, 7, schema, 3, 4), (1.0, 1.0))

    def test_attack_exploration_preserves_walls_and_uses_on_policy_probabilities(self):
        schema = SCHEMAS[14]
        shape = (len(schema) * 4,)
        policy = EncounterExplorationPolicy(gym.spaces.Box(-4., 4., shape, dtype=np.float32),
                                            gym.spaces.MultiDiscrete([3, 3, 2, 2]), lambda _: .001)
        rows = torch.zeros(4, *shape)
        def column(name):
            return schema.index(name) - len(schema)
        rows[:, column("enemy_0_present")] = 1
        rows[:, column("previous_attack")] = 1
        rows[1, column("blockade_direction")] = 1
        rows[2, column("enemy_0_x")] = .5
        rows[3, column("previous_attack")] = 0
        with torch.no_grad():
            policy.action_net.bias[-1] = 5
            before = [head.probs.clone() for head in policy.get_distribution(rows).distribution]
            policy.observation_schema = schema
            policy.attack_temperature = 3.0
            after = [head.probs.clone() for head in policy.get_distribution(rows).distribution]
            self.assertGreater(after[-1][0, 0], before[-1][0, 0])
            self.assertGreater(after[-1][3, 0], before[-1][3, 0])
            torch.testing.assert_close(after[-1][1:3], before[-1][1:3], rtol=0, atol=0)
            for left, right in zip(before, after):
                torch.testing.assert_close(left.argmax(1), right.argmax(1))
            actions, values, log_prob = policy(rows)
            checked_value, checked_prob, _ = policy.evaluate_actions(rows, actions)
            torch.testing.assert_close(log_prob, checked_prob)
            torch.testing.assert_close(values, checked_value)

    def test_blocked_recovery_samples_retreat_and_release_after_repeated_contact(self):
        schema = SCHEMAS[14]
        shape = (len(schema) * 4,)
        policy = EncounterExplorationPolicy(gym.spaces.Box(-4., 4., shape, dtype=np.float32),
                                            gym.spaces.MultiDiscrete([3, 3, 2, 2]), lambda _: .001)
        history = torch.zeros(8, 4, len(schema))
        contact, horizontal, jump, road_dx, road_present = (schema.index(name) for name in
            ("blockade_direction", "previous_horizontal", "previous_jump", "road_dx", "road_present"))
        history[0, 2:, [contact, horizontal, jump]] = 1
        history[1, :2, [contact, horizontal, jump]] = 1
        history[2, 3, [contact, horizontal, jump]] = 1
        history[3, :, [contact, jump]] = 1
        history[3, :, horizontal] = -1
        history[4, :, [contact, horizontal]] = 1
        history[5:, 2:, [contact, horizontal]] = 1
        history[5, 2:, road_present] = 1
        history[5:7, 2:, road_dx] = -1
        history[7, 2:, [road_present, road_dx]] = 1
        eligible = torch.tensor([True, True, False, False, False, True, False, False])
        rows = history.flatten(1)
        with torch.no_grad():
            policy.action_net.weight.zero_()
            policy.action_net.bias[[2, 7, 9]] = 7
            before = [head.probs.clone() for head in policy.get_distribution(rows).distribution]
            policy.observation_schema = schema
            policy.blocked_recovery_temperature = 4.0
            after = [head.probs.clone() for head in policy.get_distribution(rows).distribution]
            for head, recovery_action in ((0, 1), (2, 0), (3, 0)):
                self.assertTrue(torch.all(after[head][eligible, recovery_action] > before[head][eligible, recovery_action]))
                torch.testing.assert_close(after[head][~eligible], before[head][~eligible], rtol=0, atol=0)
                torch.testing.assert_close(after[head].argmax(1), before[head].argmax(1))
            torch.testing.assert_close(after[1], before[1], rtol=0, atol=0)
            actions, values, log_prob = policy(rows)
            checked_value, checked_prob, _ = policy.evaluate_actions(rows, actions)
            torch.testing.assert_close(log_prob, checked_prob)
            torch.testing.assert_close(values, checked_value)

    def test_idle_navigation_explores_movement_when_the_road_is_clear(self):
        schema = SCHEMAS[14]
        field = {name: index for index, name in enumerate(schema)}
        history = torch.zeros(10, 4, len(schema))
        history[:, -3:, [field["knight_confidence"], field["road_present"]]] = 1
        history[:, -3:, field["road_dx"]] = -1
        history[0, -3:, field["previous_attack"]] = 1
        history[1, -3:, field["road_dx"]] = 0
        history[1, -3:, field["road_dy"]] = 1
        history[1, -3:, field["enemy_0_present"]] = 1
        history[1, -3:, field["enemy_0_x"]] = .3
        history[2, -1, field["enemy_0_present"]] = 1
        history[3, -1, field["road_present"]] = 0
        history[4, -1, field["knight_y"]] = .03
        history[5, -2, field["previous_jump"]] = 1
        history[6, -1, field["room_changed"]] = 1
        history[7, -3, field["previous_horizontal"]] = -1
        history[8, -1, field["knight_confidence"]] = 0
        history[9, -3:, field["road_dx"]] = 0
        rows = history.flatten(1)
        eligible = torch.tensor([True, True] + [False] * 8)
        torch.testing.assert_close(idle_navigation_context(rows, schema), eligible)
        self.assertFalse(idle_navigation_context(torch.zeros(1, 4 * len(SCHEMAS[7])), SCHEMAS[7]).item())

        policy = EncounterExplorationPolicy(gym.spaces.Box(-4., 4., (rows.shape[1],), dtype=np.float32),
                                            gym.spaces.MultiDiscrete([3, 3, 2, 2]), lambda _: .001)
        with torch.no_grad():
            policy.action_net.weight.zero_()
            policy.action_net.bias[[0, 6, 9]] = 7
            before = [head.probs.clone() for head in policy.get_distribution(rows).distribution]
            policy.observation_schema = schema
            policy.blocked_recovery_temperature = 4
            after = [head.probs for head in policy.get_distribution(rows).distribution]
        for head, action in ((0, 1), (2, 1), (3, 0)):
            self.assertTrue(torch.all(after[head][eligible, action] > before[head][eligible, action]))
            torch.testing.assert_close(after[head][~eligible], before[head][~eligible], rtol=0, atol=0)
            torch.testing.assert_close(after[head].argmax(1), before[head].argmax(1))

    def test_frontier_keeps_deeper_living_starts_when_the_archive_is_full(self):
        starts = PracticeStarts(None)
        frame = dict(sceneName="Tutorial_01", grounded=True, heroY=11.4, health=5)
        action = [[2, 0, 0, 0]]
        for i in range(16):
            starts.remember("frontier", 300_100 + i, action, dict(frame, heroX=i * 4), i)
        starts.remember("frontier", 300_116, action, dict(frame, heroX=64, health=3), 16)
        starts.remember("frontier", 300_117, action, dict(frame, heroX=68, health=2), 17)
        self.assertIn("16,3", starts.starts["frontier"])
        self.assertNotIn("0,3", starts.starts["frontier"])
        self.assertNotIn("17,3", starts.starts["frontier"])
        chosen = starts.choose("frontier", SimpleNamespace(random=lambda: .1, choice=lambda rows: rows[0]), 12)
        self.assertEqual(chosen["seed"], 300_116)

    def test_retains_applied_press_and_release_only_for_a_safe_crossing(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            initial = dict(step=90, heroX=82.8, heroY=11.4, velocityX=0, velocityY=0,
                           grounded=True, health=5, damage=0)
            for seed, damage in ((300_101, 0), (300_102, 1)):
                before = initial
                lesson = PracticeLessons(work, seed, before, [2, 0, 0, 1], 0, 0)
                for i in range(7):
                    cut = i >= 1
                    hit = damage if i >= 3 else 0
                    after = dict(initial, step=91 + i, heroX=82.8 + .8 * i,
                                 velocityX=8 if cut else 0, health=5 - hit, damage=hit)
                    row = np.zeros(len(SCHEMAS[14]), dtype=np.float32)
                    lesson.record(row.copy(), row, [2, 0, 0, int(i == 1)], before, after, 0, 0)
                    before = after
                lesson.save()
            examples = nearby_wall_strikes(work, {"x": 82.9, "y": 11.41})
            self.assertEqual({action[3] for _, action in examples}, {0, 1})
            self.assertEqual(len(list(work.rglob("*.npz"))), 1)

    def test_exploration_only_changes_sampling_for_the_selected_head(self):
        head = torch.nn.Linear(2, 10)
        with torch.no_grad():
            head.weight.zero_()
            head.bias.copy_(torch.arange(10, dtype=torch.float))
            head.bias[7] = 3
            head.bias[9] = 12
        policy = SimpleNamespace(action_net=head)
        x = torch.ones((1, 2))
        before = head(x).detach()
        soften_action_sampling(policy, 3, 3)
        after = head(x).detach()
        torch.testing.assert_close(after[:, :-2], before[:, :-2], rtol=0, atol=0)
        self.assertEqual(after[:, -2:].argmax().item(), before[:, -2:].argmax().item())
        self.assertGreater(after[:, -2:].softmax(-1)[0, 0].item(), before[:, -2:].softmax(-1)[0, 0].item())
        soften_action_sampling(policy, 2, 4)
        after_jump = head(x).detach()
        torch.testing.assert_close(after_jump[:, :6], after[:, :6], rtol=0, atol=0)
        torch.testing.assert_close(after_jump[:, 8:], after[:, 8:], rtol=0, atol=0)
        self.assertEqual(after_jump[:, 6:8].argmax().item(), after[:, 6:8].argmax().item())
        self.assertGreater(after_jump[:, 6:8].softmax(-1)[0, 1].item(), after[:, 6:8].softmax(-1)[0, 1].item())

    def test_setup_hands_control_to_the_learner_and_keeps_seeds_out_of_evaluation(self):
        with tempfile.TemporaryDirectory() as temporary:
            for batch, expected_step in [(0, 0), (1, 13), (2, 3), (3, 5)]:
                log = Path(temporary) / str(batch)
                room = PracticeRoom()
                env = EncounterPractice(room, Starter(), batch, log)
                env.reset(seed=2_000_004)
                self.assertTrue(100_000 <= room.seed_used < 1_000_000)
                self.assertEqual(room.last_frame["step"], expected_step)
                self.assertEqual(env.learner_steps, 0)
                self.assertTrue(json.loads(log.read_text())["reached"])
                action = [2, 1, 1, 1]
                env.step(action)
                self.assertEqual(room.last_action, action)

    def test_stationary_work_and_horizontal_or_vertical_progress_reset_dwell(self):
        dwell = Dwell(2., 2.)
        for _ in range(14):
            dwell.advance(2.1, 2., False)
        self.assertEqual(dwell.longest, 14)
        self.assertEqual(dwell.advance(2., 2., True), 0)
        self.assertEqual(dwell.advance(2., 4., False), 0)
        self.assertEqual(dwell.advance(4., 4., False), 0)

    def test_replay_starts_at_a_real_enemy_with_previous_controls_and_fresh_learner_metrics(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            room = PracticeRoom()
            room.max_steps = 300
            seed = 350_000
            room.reset(seed=seed)
            prefix = [[2, 0, 0, 0], [2, 0, 0, 1], [1, 0, 0, 0]]
            for action in prefix:
                room.step(action)
            starts = PracticeStarts(path / "starts.json")
            starts.remember("enemy", seed, prefix, room.last_frame, 3)
            starts.save()
            env = EncounterPractice(room, Starter(), 2, path / "log", PracticeStarts(starts.file))
            env.reset()
            report = json.loads((path / "log").read_text())
            self.assertTrue(report["reached"])
            self.assertTrue(report["replayed"])
            self.assertEqual(report["setupSteps"], len(prefix))
            self.assertEqual(room.last_action, prefix[-1])
            self.assertEqual(room.seed_used, seed)
            self.assertEqual(env.episode_metrics()["steps"], 0)
            env.step([2, 0, 0, 1])
            self.assertEqual(env.episode_metrics()["combatSteps"], 1)

    def test_only_a_stall_is_a_terminal_failure_for_ppo(self):
        with tempfile.TemporaryDirectory() as temporary:
            env = EncounterPractice(PracticeRoom(), Starter(), 0, Path(temporary) / "rows")
            for reason, expected in [("stalled", (True, False)), ("time-limit", (False, True))]:
                env.reset()
                env.env.reason = reason
                self.assertEqual(env.step([0, 0, 0, 0])[2:4], expected)
