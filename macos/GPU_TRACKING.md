# Apple GPU tracking

The live macOS tracker uses Metal for its largest bounded, data-parallel ground
tracking workload:

- normalized 16-by-12 texture scoring across camera displacement candidates.

Core Image, that compute pipeline, and the presentation renderer use the default
Apple GPU. The search path retains reusable unified-memory buffers, compiles
before capture begins, and falls back to the CPU algorithm when Metal is
unavailable. Match selection, ambiguity rejection, pose continuity, reference
promotion, and atlas admission remain on the CPU.

A pixel-exact Metal implementation also measures vertical edges and the fixed
32-by-8 ground kernel. Five consecutive final-build runs put that smaller job
between 0.995x and 1.10x of CPU speed, so production `.automatic` deliberately
uses CPU there. Explicit Metal remains available for parity tests and future
profiling. This avoids adding GPU synchronization where it does not pay for
itself.

Production local search keeps the ordinary motion-dependent radius and raises
its maximum horizontal radius from 96 to 192 pixels. The larger domain matters
after a capture gap or sudden camera movement; routine 60 Hz frames still search
the small predicted neighborhood.

## M4 measurements

Measured on the development MacBook Air (Apple M4, 8-core GPU) using a release
build on 2026-10-22. The ranges below cover six consecutive benchmark runs;
each endpoint is a median after a warm-up dispatch at the production 640-by-360
resolution.

| Work | CPU median range | Metal median range | Result |
| --- | ---: | ---: | --- |
| Texture search, 96px radius | 3.254-3.290 ms | 0.939-1.027 ms | 3.18-3.47x faster |
| Texture search, 192px radius | — | 1.281-1.336 ms | 1.995x candidates in 39-41% of the old 96px CPU time |
| Edge plus ground kernel | 0.809-0.853 ms | 0.761-0.831 ms | 0.995-1.10x; keep CPU automatic path |

The production edge-and-kernel benchmark still improved from 1.727 ms to
1.011 ms (1.71x) because the refactor computes and carries the paired result
once. That gain comes from removing duplicate CPU work rather than choosing the
Metal image path.

The wider-search test also verifies behavior rather than throughput alone: a
140-pixel displacement is outside the former local radius and returns no CPU
solution, while the production Metal search recovers the exact -140-pixel
translation. CPU and Metal preprocessing are pixel-exact. Accepted matcher
candidate coordinates and support counts are exact; floating scores agree
within 0.002.

Run the retained measurements with:

```sh
HKV_GPU_BENCHMARK=1 swift test -c release \
  --filter GroundMetalProcessingTests
```

Ordinary performance logs include `groundPatchSearch` timing. Launch with
`--trace-ground-tracking` to also log the selected backend, candidate count,
patch-comparison count, and elapsed time for each search.

## Object model

Core ML object inference continues to use `MLComputeUnits.all`, allowing Core
ML to choose the CPU, GPU, or Neural Engine for each operation. This development
machine currently has no promoted Core ML gameplay model under
`models-v1`; the repository's shared detector is ONNX for the cross-platform
runtime. No honest `.all` versus `.cpuAndNeuralEngine` result can be reported
until a Core ML artifact is trained or installed. Ground-tracking measurements
above do not depend on the object model.
