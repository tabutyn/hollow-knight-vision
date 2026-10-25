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
- Mac tests now cover explicit GPU and automatic selection, absent-device fallback,
  malformed requests, row masks, buffer reuse, and existing motion/score parity.
  Measured Mac coverage will be recorded after CI; do not substitute test counts
  or Windows results for it.

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
