"""Local policy-gradient practice on real actions taken at a stalled frontier."""
import copy

import numpy as np
import torch


class SampledFrontier:
    def __init__(self, seed):
        self.rng = torch.Generator().manual_seed(seed)
        self.start = -100
        self.steps, self.inputs, self.actions, self.rewards = [], [], [], []

    def choose(self, model, observation, step, stall):
        if stall >= 7 and step - self.start >= 25:
            self.start = step
        if not 0 <= step - self.start < 14:
            return None
        with torch.no_grad():
            row = torch.as_tensor(observation).unsqueeze(0)
            distributions = model.policy.get_distribution(row).distribution
            action = np.array([torch.multinomial(head.probs[0], 1, generator=self.rng).item()
                               for head in distributions], dtype=np.int64)
        self.steps.append(step)
        self.inputs.append(observation.copy())
        self.actions.append(action)
        return action

    def experience(self):
        if not self.steps:
            return None
        # Assign the next three seconds of actual ground, health, combat and resource
        # outcomes to the sampled control. Each state precedes its applied action.
        returns = [sum(reward * 0.96**i for i, reward in enumerate(self.rewards[step:step+30]))
                   for step in self.steps]
        return np.stack(self.inputs), np.stack(self.actions), np.asarray(returns, dtype=np.float32)


def learn_from_returns(parent, sample, work, champion, seed, expert_inputs, approach):
    """A clipped policy-gradient update, rehearsing the actor's original decisions."""
    inputs, actions, returns = sample
    if len(inputs) < 8 or np.ptp(returns) < 0.3:
        return None
    bank = work / "policy-samples" / champion
    bank.mkdir(parents=True, exist_ok=True)
    path = bank / f"{seed}.npz"
    # Retain just the controlled trials of this exact parent policy.
    selected = np.linspace(0, len(inputs)-1, min(96, len(inputs)), dtype=int)
    temporary = path.with_suffix(".tmp")
    with temporary.open("wb") as stream:
        np.savez_compressed(stream, inputs=inputs[selected], actions=actions[selected], returns=returns[selected])
    temporary.replace(path)
    batches = []
    for peer in sorted(bank.glob("*.npz"), key=lambda p: int(p.stem), reverse=True)[:4]:
        with np.load(peer, allow_pickle=False) as stored:
            gain = stored["returns"]
            # A long failed episode should not dominate a shorter successful crossing.
            gain = np.clip((gain - gain.mean()) / max(0.4, float(gain.std())), -3, 3)
            batches.append((stored["inputs"], stored["actions"], gain))
    states, applied, advantages = (torch.as_tensor(np.concatenate(column)) for column in zip(*batches))
    protected = torch.as_tensor(np.concatenate(([np.stack(approach)] if approach else []) + [expert_inputs]))
    child = copy.deepcopy(parent)
    policy = child.policy
    optimizer = torch.optim.Adam([*policy.mlp_extractor.policy_net.parameters(), *policy.action_net.parameters()], lr=0.00012)
    with torch.no_grad():
        old = parent.policy.get_distribution(states).log_prob(applied)
        reference = ([head.probs.detach() for head in parent.policy.get_distribution(protected).distribution]
                     if len(protected) else [])
    for _ in range(32):
        distribution = policy.get_distribution(states)
        ratio = (distribution.log_prob(applied) - old).exp()
        objective = torch.minimum(ratio * advantages, ratio.clamp(0.85, 1.15) * advantages).mean()
        retention = states.new_zeros(())
        if reference:
            on_anchors = policy.get_distribution(protected).distribution
            retention = sum(torch.nn.functional.kl_div(head.logits, prior, reduction="batchmean")
                            for head, prior in zip(on_anchors, reference))
        loss = -objective + 3 * retention
        optimizer.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(policy.parameters(), 0.5)
        optimizer.step()
        if float(retention.detach()) > 0.035:
            break
    with torch.no_grad():
        if not torch.isfinite(loss) or torch.equal(policy.get_distribution(states).mode(),
                                                   parent.policy.get_distribution(states).mode()):
            return None
    return child
