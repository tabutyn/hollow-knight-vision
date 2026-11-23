param([string]$Python, [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $Python) {
  $Python = if ($env:HKV_TRAINER_PYTHON) { $env:HKV_TRAINER_PYTHON } else {
    Join-Path $repositoryRoot '.tools\hkv-training-windows\Scripts\python.exe'
  }
}
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) { throw 'Run setup-training-windows.ps1 or supply -Python.' }
$tests = Join-Path $PSScriptRoot 'tests\HollowKnightVision.Windows.Tests\bin\Release\net8.0\HollowKnightVision.Windows.Tests.dll'
if (-not (Test-Path -LiteralPath $tests -PathType Leaf)) { throw 'Run build-windows.ps1 before the trainer interchange check.' }
$trainerRoot = Join-Path $repositoryRoot 'macos\TrainingPython'
$temporaryParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$fixtureRoot = Join-Path $temporaryParent ('hkv-dataset-probe-' + [Guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($name in @('CUDA_VISIBLE_DEVICES', 'OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'PYTHONDONTWRITEBYTECODE', 'HKV_TRAINER_PYTHON')) {
  $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
  $env:CUDA_VISIBLE_DEVICES = '-1'
  $env:OMP_NUM_THREADS = '1'
  $env:MKL_NUM_THREADS = '1'
  $env:PYTHONDONTWRITEBYTECODE = '1'
  $env:HKV_TRAINER_PYTHON = $Python
  & $Python -B -m unittest discover -s $trainerRoot -p test_incremental_trainer.py
  if ($LASTEXITCODE -ne 0) { throw 'Shared trainer unit tests failed.' }
  $null = New-Item -ItemType Directory -Path $fixtureRoot
  $dataset = & $Dotnet $tests --export-dataset-probe $fixtureRoot
  if ($LASTEXITCODE -ne 0) { throw 'Synthetic Windows export failed.' }
  $dataset = [IO.Path]::GetFullPath(($dataset | Out-String).Trim())
  if (-not $dataset.StartsWith((Join-Path $fixtureRoot 'datasets') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Dataset probe returned a path outside its disposable fixture.'
  }
  & (Join-Path $PSScriptRoot 'train-model-windows.ps1') -Dataset $dataset -ValidateOnly
  if ($LASTEXITCODE -ne 0) { throw 'Windows trainer launcher rejected the synthetic export.' }
  $validation = @'
import hashlib
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import hkv_incremental_trainer as trainer
import torch
manifest, samples = trainer.load_dataset(Path(sys.argv[2]))
assert len(samples) == 3, 'Menu frame leaked into gameplay dataset'
assert len(manifest['classIdentifiers']) == 7
assert sum(not box.is_negative for sample in samples for box in sample.annotations) == 3
assert sum(box.is_negative for sample in samples for box in sample.annotations) == 1
empty_index = next(index for index, sample in enumerate(samples) if not sample.annotations)
assert samples[empty_index].known_classes == {'game.playable-knight', 'enemies.crawlid'}
corrected = next(sample for sample in samples if any(box.is_negative for box in sample.annotations))
assert corrected.split == 'training'
negative = next(box for box in corrected.annotations if box.is_negative)
assert abs(negative.x - .7) < 1e-9 and abs(negative.y - .5) < 1e-9
assert abs(negative.width - .2) < 1e-9 and abs(negative.height - .2) < 1e-9
for sample in samples:
    assert hashlib.sha256(sample.image_path.read_bytes()).hexdigest() == sample.image_digest
    image = trainer.load_image(sample)
    assert image.device.type == 'cpu' and tuple(image.shape) == (3, 360, 640)
assert abs(trainer.load_image(corrected)[0, 0, 0].item() - .2) < 1e-6
heatmap, known, boxes, box_mask, weights = trainer.make_targets(samples, manifest['classIdentifiers'], 4, 8, torch.device('cpu'))
assert torch.count_nonzero(heatmap[empty_index]).item() == 0
assert known[empty_index].sum().item() == 2
assert torch.max(weights).item() == trainer.HARD_NEGATIVE_WEIGHT
assert str(trainer.select_device('cpu')) == 'cpu'
print('PASS Windows PNG/JSON -> shared Python reader, normalized coordinates, known-empty targets, hard negatives and CPU tensors')
'@
  & $Python -B -c $validation $trainerRoot $dataset
  if ($LASTEXITCODE -ne 0) { throw 'Windows/Python dataset interchange assertions failed.' }
  $botFixture = Join-Path $fixtureRoot 'paired-captures'
  $botSource = Join-Path $repositoryRoot 'macos\BotPython'
  & $Dotnet $tests --export-bot-capture-probe $botFixture
  if ($LASTEXITCODE -ne 0) { throw 'Windows paired capture export failed.' }
  $botValidation = @'
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import hkv_bot_dataset as bot
sessions = {manifest["botDomain"]: (manifest, observations, inputs)
            for manifest, observations, inputs in
            (bot.read_session(folder) for folder in Path(sys.argv[2]).glob("demo-*"))}
assert set(sessions) == {"hacker", "gameplay"}
hm, hacker, controls = sessions["hacker"]
gm, gameplay, visual_controls = sessions["gameplay"]
assert hm["replayID"] == gm["replayID"] and gm["pairedClipID"] == hm["id"] and hm["pairedClipID"] == gm["id"]
assert controls == visual_controls
assert [f["captureTimestamp"] for f in hacker] == [f["captureTimestamp"] for f in gameplay]
assert all("detectedTerrain" not in f and f["authoredTerrain"]["walls"][0]["solidSide"] == 0
           and f["authoredTerrain"]["doors"] and f["authoredTerrain"]["spikes"] and f["geoCaches"]
           for f in hacker)
assert all("authoredTerrain" not in f and f["detectedTerrain"]["walls"][0]["solidSide"] == 1
           and f["geoCaches"] == [] for f in gameplay)
bot.paired_measurements(gm, gameplay, hm, hacker)
assert all(f["enemyOutcomes"] == other["enemyOutcomes"] for f, other in zip(gameplay, hacker))
for manifest, observations, inputs in sessions.values():
    for version in (bot.SCHEMA_VERSION, bot.GROUND_CONTACT_POLICY_VERSION, bot.LANDING_GEOMETRY_POLICY_VERSION):
        rows, rejected = bot.examples(manifest, observations, inputs, policy_version=version)
        assert len(rows) == 6 and not rejected
        assert all(len(row["observation"]) == bot.HISTORY_LENGTH * len(bot.SCHEMAS[version]) for row in rows)
        assert sum(row["blueHealthGained"] for row in rows) == 2
        assert [row["continuedAttack"] for row in rows] == [False, True, True, False, True, True]
print("PASS Windows paired PNG and event capture -> shared bot exporter, typed terrain, held attacks and blue health")

import hashlib
import json
import torch
root = Path(sys.argv[2]) / "policy-fixtures"
for version in (bot.SCHEMA_VERSION, bot.GROUND_CONTACT_POLICY_VERSION, bot.LANDING_GEOMETRY_POLICY_VERSION):
    names = bot.SCHEMAS[version]
    folder = root / str(version)
    folder.mkdir(parents=True)
    fake_checkpoint = b"native parity test"
    (folder / "model.zip").write_bytes(fake_checkpoint)
    digest = hashlib.sha256(fake_checkpoint).hexdigest()
    width = len(names) * bot.HISTORY_LENGTH
    generator = torch.Generator().manual_seed(version)
    samples = torch.rand((8, width), generator=generator) - .5
    weights = torch.rand((7, width), generator=generator) / width
    hidden_bias = torch.rand((7,), generator=generator)
    heads = torch.rand((sum(bot.ACTION_SIZES), 7), generator=generator)
    biases = torch.rand((sum(bot.ACTION_SIZES),), generator=generator)
    logits = torch.nn.functional.linear(torch.tanh(torch.nn.functional.linear(samples, weights, hidden_bias)), heads, biases)
    actions = torch.stack([torch.argmax(head, dim=1) for head in torch.split(logits, bot.ACTION_SIZES, dim=1)], dim=1)
    layers = []
    for matrix, bias, activation in ((weights, hidden_bias, "tanh"), (heads, biases, "linear")):
        layers.append(dict(inputSize=matrix.shape[1], outputSize=matrix.shape[0], activation=activation,
                           weights=matrix.reshape(-1).tolist(), biases=bias.tolist()))
    raw = json.dumps(dict(schemaVersion=1, observationSchemaVersion=version, modelName="CPU fixture",
                          featureNames=names, historyLength=bot.HISTORY_LENGTH, actionSizes=bot.ACTION_SIZES,
                          sourceCheckpointSHA256=digest, layers=layers)).encode()
    (folder / "runtime-policy.json").write_bytes(raw)
    (folder / "runtime-manifest.json").write_text(json.dumps(dict(schemaVersion=1, policyFile="runtime-policy.json",
        policySHA256=hashlib.sha256(raw).hexdigest(), sourceCheckpointSHA256=digest)))
    (folder / "fixture.json").write_text(json.dumps(dict(inputs=samples.tolist(), logits=logits.tolist(), actions=actions.tolist())))

'@
  & $Python -B -c $botValidation $botSource $botFixture
  if ($LASTEXITCODE -ne 0) { throw 'Windows bot capture interoperability failed.' }
  $nativeVerifier = Join-Path $PSScriptRoot 'artifacts\win-x64\HollowKnightVision.Windows.Host.exe'
  $policyFixtures = Get-ChildItem -LiteralPath (Join-Path $botFixture 'policy-fixtures') -Directory
  foreach ($entry in $policyFixtures) {
    & $nativeVerifier --hacker-gym-observations verify $entry.FullName (Join-Path $entry.FullName 'fixture.json')
    if ($LASTEXITCODE -ne 0) { throw "Native bot policy verification failed for schema $($entry.Name)." }
  }
  Write-Host 'PASS Windows native PPO action heads match CPU PyTorch for compact, grounded, and landing policies.'

} finally {
  foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
  if (Test-Path -LiteralPath $fixtureRoot) {
    $target = Get-Item -LiteralPath $fixtureRoot -Force
    if ($target.FullName -ne $fixtureRoot -or $target.Parent.FullName -ne $temporaryParent -or
        -not $target.Name.StartsWith('hkv-dataset-probe-', [StringComparison]::Ordinal) -or
        ($target.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing cleanup of an unexpected fixture path.' }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
  }
}
