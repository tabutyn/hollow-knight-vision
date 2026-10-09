# Shared gameplay object detector

`Detector.onnx` is the default cross-platform runtime model.

- Architecture: frozen TorchVision MobileNet V3 Small feature backbone with project-specific heatmap and box heads.
- Input: float32 RGB tensor named `image`, shape `[1, 3, 360, 640]`, values normalized to `[0, 1]`.
- Outputs: `scores` and `boxes`; boxes use normalized top-left `x, y, width, height` coordinates.
- Classes: Crawlid, Shade, Vengfly, playable Knight, Geo Deposit, Lifeblood Cocoon, and Sign. Exact identifiers are in `training.json`.
- Source run: `431f6b98-718a-448a-ba45-a4655191a073`, promoted as version 17.0 on 2026-10-03.
- Validation at the source run's 0.5 confidence/IoU settings: precision 0.9524, recall 0.6452, mean IoU 0.7688. These figures describe the private validation split used during development and are not a general benchmark.

The private screenshots and annotations used for training are not distributed. The exported weights contain learned parameters, not source images. Users should validate accuracy on their resolution, language, display settings, and game version. New named classes can be added through the Label workspace and trained locally.
