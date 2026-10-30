# Installed training parity and coverage — 2026-10-29

Pulled and fast-forwarded `main` from `2c62adc` to `6aeedf5`. The new Windows
change is `ba47ca9`, restoring training from the installed app. Mac production
ports are in `c388f8d`. This audit covers that delta; the earlier tracking,
stencil, replay and debug-default work remains in `main`. The experimental
Windows Step 1s tool remains deferred under the user's production-first scope.

## Behavior reviewed and ported

| Windows improvement | Mac result |
| --- | --- |
| Train uses the shared model instead of an empty hidden field | Already correct: `selectedModelIdentifier`, export, configuration, checkpoint selection and training notifications use the shared gameplay model. Existing dataset-export tests pass. |
| Installed app carries its own training tools | Trainer and requirements were already bundled. Added the executable setup script to the signed bundle, so setup is available without a checkout. |
| Installed launches retain their Python environment | Fixed: the helper resolves an explicit `HKV_TRAINER_PYTHON`, then the registered interpreter, then the environment under user Application Support. Setup registers its verified environment; installers preserve existing registration and can register a legacy development environment. Workstation paths stay outside distributed app resources. |
| New runs use canonical UUIDs | Already correct on Mac. |
| Recover completed runs with compact Python UUIDs | Ported strict acceptance of canonical UUIDs or 32 hexadecimal digits for run IDs. Dataset IDs keep their normal decoding. Restored models retain checkpoints after the source run is removed, and durable restore metadata uses canonical UUIDs. |
| Preserve checkpoint lineage | Mac now recognizes a compact base-run ID while reconciling successive development models. Existing regression protection still rejects a child that loses prior knowledge. |

The installed helper no longer derives Python from the app bundle's parent
directories. Setup uses `~/Library/Application Support/HollowKnightVision/`
`training-v1/runtime/.training-venv`; `training-v1/runtime-settings.json` records
the interpreter. Explicit installer registration is checked before replacing
the app, so a Python error cannot interrupt Dock refresh after replacement.
Metadata compatibility does not convert Windows ONNX models to Core ML.

## Verification

- Full Mac CI profile: **845 XCTest cases: 835 passed, 10 skipped, no failures**,
  plus **one Swift Testing pass** — **836 passed total**.
- Focused training regression run: **24 passed**. Eight added cases cover
  runtime resolution, durable registration and overrides, malformed settings,
  missing interpreters, compact identifiers, durable promotion and lineage.
- Shared Python trainer: **13 passed** using the registered Python 3.12
  environment. Dock refresh tests: **11 passed**. Shell syntax checks passed.
- Windows portable audit: **1,020 registered checks: 1,001 passed, 14 skipped,
  five Windows filesystem-semantics failures on macOS**. The new compact-run
  recovery/promotion/checkpoint test passes. Existing assertions were retained;
  the ignored audit runner continues after exceptions and fixes fixture paths.
- Windows core and native WPF test projects cross-build with zero warnings or
  errors. An initial concurrent build hit an artifact-file lock; its sequential
  rerun passed. This is not native Windows UI or PowerShell execution.

The five Windows failures are unchanged from the earlier audit: live-world
manifest rollback, rollback recovery-archive preservation, scene-session
manifest rollback, calibration-write rollback, and ground-truth atomic-replace
rollback. They expect Windows open-file sharing behavior. Native Windows checks
remain necessary. Skips cover CUDA, private captures and Windows junctions.
Mac's CI profile excludes the private `MenuStencilTests` screenshot corpus;
the portable synthetic menu tests still run.

## Coverage

| Instrumented production scope | Covered / total | Line coverage |
| --- | ---: | ---: |
| Mac app, LLVM whole-source summary | 34,234 / 60,490 | **56.59%** |
| New Mac training runtime, LLVM summary | 35 / 35 | **100%** |
| Changed Mac app executable lines, unique LCOV | 18 / 18 | **100%** |
| Changed training-runtime executable lines, unique LCOV | 33 / 33 | **100%** |
| Changed Windows core executable lines, unique LCOV | 6 / 6 | **100%** |
| Windows core overall, unique LCOV | 20,436 / 23,365 | **87.46%** |

The Mac helper's launch/exec entrypoint, three changed Windows MainWindow files,
XAML and installation/setup scripts have no unit-test coverage counters here.
They are **not assumed covered**. Installed-helper verification is separate from
instrumented coverage. Whole-app counts include native GUI/capture orchestration;
Metal shader instructions are outside LLVM host-code coverage. LLVM summaries
and unique LCOV line counts have different denominators.

`coverage-summary.json` lists every changed Swift/C# production file, uncovered
executable lines and files without counters. Regenerate it with:

```sh
python3 macos/audits/2026-10-29-training-parity/summarize-coverage.py \
  "$PWD" artifacts/audits/macos-training-parity-2026-10-29 2c62adc
```

The full Mac coverage profile is from one final debug binary. Raw exports,
validation logs, the Windows audit runner and installed-training smoke script
are retained in ignored `artifacts/audits/macos-training-parity-2026-10-29/`.

## Installed application

`./macos/build-app.sh` built, signed and installed source `c388f8d` at
`/Applications/Hollow Knight Vision.app`; its Swift fingerprint and bundled
training resources match the checkout. Strict signature verification passes.
The installer verified one canonical Dock pin and consumed the disposable build
app. The registered Python environment remained available after installation.

The installed helper passed a `--help` probe with an empty PATH and no
`HKV_TRAINER_PYTHON`. A second smoke test used two temporary synthetic images
from a temporary working directory, trained one CPU epoch, evaluated predictions,
and successfully exported a Core ML package, checkpoint and canonical training
manifest. The smoke test removed its temporary output; it did not alter user
labels or activate a model. This validates installed training wiring, not model
quality or gameplay tracking speed.
