"""Warm-start compact PPO experiments from a flat playing checkpoint."""
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.policies import ActorCriticPolicy
from stable_baselines3.common.torch_layers import BaseFeaturesExtractor, FlattenExtractor


class ColumnExtractor(BaseFeaturesExtractor):
    def __init__(self, observation_space, columns):
        width, = observation_space.shape
        if (not columns or list(columns) != sorted(set(columns))
                or any(type(i) is not int or not 0 <= i < width for i in columns)):
            raise ValueError("Expected ordered, unique raw observation columns")
        super().__init__(observation_space, len(columns))
        self.register_buffer("indices", torch.tensor(columns, dtype=torch.long), persistent=False)

    def forward(self, observations):
        return observations.index_select(-1, self.indices)


def warm_start(source, env, columns, seed):
    """Use identical PPO settings and fresh optimizers for the two matched branches."""
    if (source.policy_class != ActorCriticPolicy or type(source.policy.features_extractor) != FlattenExtractor
            or source.observation_space != env.observation_space or source.action_space != env.action_space):
        raise ValueError("Expected an ordinary flat playing policy matching the Gym environment")
    kwargs = dict(source.policy_kwargs)
    if columns is not None:
        kwargs.update(features_extractor_class=ColumnExtractor, features_extractor_kwargs={"columns": columns})
    model = PPO(source.policy_class, env, policy_kwargs=kwargs, seed=seed, device="cpu",
                learning_rate=source.lr_schedule(1.0), n_steps=source.n_steps, n_epochs=source.n_epochs,
                batch_size=source.batch_size, gamma=source.gamma, gae_lambda=source.gae_lambda,
                clip_range=source.clip_range(1.0),
                clip_range_vf=source.clip_range_vf(1.0) if source.clip_range_vf else None,
                ent_coef=source.ent_coef, vf_coef=source.vf_coef, target_kl=source.target_kl,
                max_grad_norm=source.max_grad_norm, normalize_advantage=source.normalize_advantage)
    weights = source.policy.state_dict()
    if columns is not None:
        for network in ("policy_net", "value_net"):
            key = f"mlp_extractor.{network}.0.weight"
            weights[key] = weights[key][:, columns]
    model.policy.load_state_dict(weights)
    return model
