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
