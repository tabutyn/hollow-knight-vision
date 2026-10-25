# GPU tracking audit — 2026-10-25

## Findings and changes

| Finding | Change / evidence |
| --- | --- |
| Windows had only CPU / parallel CPU scoring, while Mac used Metal for production ground hypotheses. | CUDA now runs the same normalized 16×12 patch score on NVIDIA hardware; CPU selection, ambiguity checks, pose continuity and atlas admission are unchanged. |
| Windows' atlas-driving `VisualGameplayTracker` still used CPU and the old 96px maximum radius. | It now uses automatic CUDA selection and the same 192px maximum as the ground-feature tracker. Ordinary predicted searches remain small. |
| Metal accepted unchecked source indices and target offsets before shader dereferences. | Host validation now rejects those samples consistently with CPU. The CPU reference also rejects patches crossing a row boundary. |
| Metal image processing confused an empty valid-row range with no row restriction. | Separate nil sentinel; CPU/GPU row-mask parity test. Invalid sizes and row ranges are rejected before dispatch. |
| Failure to create one of the image compute encoders could still publish output buffers. | Failed encoding now returns nil, preserving the CPU fallback. |
| Mac CI claimed success without publishing coverage. | Run 40141752240 logged `No files were found` for the hidden `.build` artifact path. CI now copies the exact Swift coverage JSON into a visible directory, validates it, and fails on a missing artifact. |

## Verification

- Windows full suite: 969 core tests, 67 UI checks, receiver protocol tests passed.
- GTX 1080 hardware-required run: candidate coordinates/support agree with CPU;
  score error tolerance 0.002; concurrent calls, growing/shrinking buffers,
  bad indices/offsets, low-texture rejection, brightness changes and fallback covered.
- The GPU solver reproduces the CPU motion sequence and recovers a 140px step
  that the former 96px search rejects. These are synthetic correctness checks,
  not a claim that all live tracking failures are resolved.
- Coverage-report unit tests: measured counts retained; missing GPU file rejected.
- Mac tests passed in [run 40201876662](https://github.com/tabutyn/hollow-knight-vision/actions/runs/40201876662)
  at `f5b348bf46f0099719e74981f4770d1bda0a3b51`. Tests cover explicit GPU and
  automatic selection, absent-device fallback, malformed requests, row masks,
  buffer reuse, and motion/score parity, with explicit Metal backend assertions.
  The full portable suite reported 811 tests, 10 skipped, zero failures;
  all nine GPU correctness tests passed (two opt-in GPU benchmarks skipped).
  Python tests and the canonical Mac release packaging step also passed.
- Windows CI's pre-existing compiler failure was reproduced before this change:
  installing .NET 8 did not select it when newer SDKs were installed. Root
  `global.json` now selects .NET 8; the complete build/test/install step passed
  in run 40202409979. Its next step exposed an obsolete atlas test mapping,
  repaired without weakening the parity gate.
- The complete [Windows CI run 40202807427](https://github.com/tabutyn/hollow-knight-vision/actions/runs/40202807427)
  then passed build/test/install, parity inventory, training interchange and
  publication. Hosted CI has no NVIDIA device, so its CUDA hardware tests skip;
  the local GTX 1080 run required CUDA and logged 13 parity dispatches.
- Installed Windows app, core and ILGPU hashes match the tested publish output.
  App assembly SHA-256: `DEFF5188363A3EF86FA030E73350CD6364DA275521DCE11637948E2489ABA3F6`.

## Measured Mac coverage

Swift LLVM coverage from the passing portable suite; `MenuStencilTests` still
requires the user's local screenshot/calibration corpus and is excluded.
The [coverage artifact](https://github.com/tabutyn/hollow-knight-vision/actions/runs/40201876662/artifacts/12602108064)
contains the raw Swift JSON and the generated GPU summary. A compact copy of
the measured counts is retained in [gpu-coverage-macos-2026-10-25.json](gpu-coverage-macos-2026-10-25.json).

| File | Executed lines | Line coverage | Executed functions | Region coverage |
| --- | ---: | ---: | ---: | ---: |
| GroundMetalProcessing.swift | 532 / 542 | 98.15% | 32 / 34 | 84.72% |
| GroundCameraSolver.swift | 1118 / 1214 | 92.09% | 127 / 165 | 76.50% |
| GroundHypothesisTracker.swift | 3691 / 4139 | 89.18% | 412 / 546 | 75.91% |
| LiveTrackingTiming.swift | 152 / 230 | 66.09% | 23 / 44 | 52.94% |
| LayerSceneViewport.swift | 486 / 1252 | 38.82% | 91 / 199 | 39.38% |
| LiveCaptureModel.swift | 0 / 5576 | 0% | 0 / 743 | 0% |

The 98.15% figure is **host line coverage**, not shader coverage, branch coverage,
or end-to-end gameplay coverage. LLVM exports no branch counters here. Covered
shader-string initialization does not instrument the Metal instructions.

Uncovered GPU host regions include allocation/queue/encoder failure, failed
command completion, malformed source-patch sizes, out-of-Int32 coordinates,
and searches whose samples are all invalid. `makeImageContext()` is unexecuted
by this suite. Device absence is tested, but actual device loss and GPU resource
failure are not injected. These remain concrete coverage gaps.

The live capture controller and much of the renderer are not exercised by the
portable tests. Static review confirms production Mac construction selects the
automatic Metal solver with a 192px maximum, but this suite does not prove live
capture-to-atlas execution.

### Windows live gameplay verification

After the user entered gameplay on 2026-10-25, the installed app (PID 27164)
reported `CameraBackend: Cuda` on the GTX 1080. Between 12:15:14 and 12:15:31
America/St_Johns, the shared CUDA dispatch counter advanced from 3,097 to 3,631
(534 dispatches in approximately 18 seconds). All four distinct diagnostic
snapshots reported a CUDA camera backend and no fallback reason.

Sampled camera searches took 1.1406ms for 231 candidates, 6.8252ms for 5,775,
1.0343ms for 287, and 5.7462ms for 3,080. These are individual search samples,
not frame-time percentiles. The shared counter includes camera and ground-feature
work; the separate camera backend field confirms that camera tracking itself
uses CUDA. This proves live GPU execution, not visual tracking accuracy or
smooth atlas motion. Mac live gameplay remains unverified from this workstation.

## Windows measurements

Ryzen 5 5500 / GTX 1080, Release build. Three runs, nine measurements per median,
after warming CPU, parallel CPU and CUDA 20 times. Includes packing, transfer,
kernel work and readback, not just device execution.

| Run | CPU 96px | Parallel CPU 96px | CUDA 96px | CUDA 192px |
| --- | ---: | ---: | ---: | ---: |
| 1 | 11.856 ms | 2.149 ms | 0.923 ms | 1.268 ms |
| 2 | 9.580 ms | 2.003 ms | 1.859 ms | 1.716 ms |
| 3 | 12.728 ms | 2.224 ms | 3.157 ms | 3.251 ms |

CUDA was 4.03–12.85× faster than the prior serial camera path. Against the
parallel ground-feature path it ranged from 0.70–2.33×: contention/dispatch
variance still matters. Initial cold-JIT CPU timings near 72ms are excluded.
This is patch-search speed, not end-to-end frame rate or tracking accuracy.

## Reproduction and limits

Run `windows/build-windows.ps1` to test, publish, install and verify Windows.
For a hardware-required focused run, set `HKV_REQUIRE_CUDA=1` and
`HKV_WINDOWS_COMPUTE_BENCHMARK=1`, then run:

```
dotnet run --project windows/tests/HollowKnightVision.Windows.Tests -c Release -- --gpu-only
```

CUDA uses pinned ILGPU 1.5.3 with reusable device buffers, one serialized stream,
and CPU fallback after device failure. It requires an NVIDIA driver, not a
toolkit or administrator-installed service. Notices ship with the app.
See [ILGPU's kernel/transfer contract](https://ilgpu.net/docs/02-beginner/03-kernels-and-simple-programs/).

Swift coverage measures host code, including shader string construction; it
does **not** measure execution coverage inside runtime-compiled Metal shaders.
GPU dispatch assertions and CPU/GPU parity tests provide separate shader evidence.
No live Apple GPU timing can be measured from this Windows workstation.
Object-model GPU inference and moving the small edge kernel to GPU are outside
this change; Mac measurements already showed little benefit for the latter.
