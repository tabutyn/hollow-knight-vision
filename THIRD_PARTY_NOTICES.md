# Third-party notices

Hollow Knight Vision depends on third-party packages. Their licenses apply to those packages and, where applicable, to components represented in the distributed model.

- PyTorch and TorchVision are distributed under BSD-style licenses. The detector uses a TorchVision MobileNet V3 Small backbone initialized from ImageNet weights before project-specific training. The exact notices shipped with the training environment are in [PYTORCH-LICENSE](THIRD_PARTY_LICENSES/PYTORCH-LICENSE) and [TORCHVISION-LICENSE](THIRD_PARTY_LICENSES/TORCHVISION-LICENSE).
- ONNX is distributed under Apache License 2.0. See [ONNX-LICENSE](THIRD_PARTY_LICENSES/ONNX-LICENSE).
- ONNX Runtime is distributed under the MIT License and includes its own third-party notices. See [ONNXRUNTIME-LICENSE](THIRD_PARTY_LICENSES/ONNXRUNTIME-LICENSE) and [its complete notices](THIRD_PARTY_LICENSES/ONNXRUNTIME-THIRD-PARTY-NOTICES).
- The optional bot training environment uses Stable-Baselines3 2.9.2 and Gymnasium under MIT licenses. Their packages are installed separately from the desktop app. See [STABLE-BASELINES3-LICENSE](THIRD_PARTY_LICENSES/STABLE-BASELINES3-LICENSE), [STABLE-BASELINES3-NOTICE](THIRD_PARTY_LICENSES/STABLE-BASELINES3-NOTICE), and [GYMNASIUM-LICENSE](THIRD_PARTY_LICENSES/GYMNASIUM-LICENSE). The bot training environment also installs PyTorch and its dependencies with their package notices; the exact installed versions are recorded in `.bot-venv/installed-requirements.txt`.

Hollow Knight is a trademark of Team Cherry. This independent fan project is not affiliated with or endorsed by Team Cherry. No Hollow Knight screenshots, recordings, executable files, or game resources are included.
