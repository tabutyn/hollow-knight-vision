param(
  [Parameter(Mandatory = $true)]
  [string]$Dataset,
  [string]$Output,
  [ValidateRange(1, 10000)]
  [int]$Iterations = 100,
  [string]$BaseCheckpoint,
  [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$windowsRoot = $PSScriptRoot
$gitCommonDirectory = & git -C $windowsRoot rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -eq 0 -and $gitCommonDirectory) {
  $repositoryRoot = Split-Path -Parent $gitCommonDirectory.Trim()
} else {
  $repositoryRoot = (Resolve-Path (Join-Path $windowsRoot "..\..\..")).Path
}
$python = Join-Path $repositoryRoot ".tools\hkv-training-windows\Scripts\python.exe"
$torchCache = Join-Path $repositoryRoot ".tools\hkv-training-cache"
$trainer = Join-Path $windowsRoot "..\macos\TrainingPython\hkv_incremental_trainer.py"

if (-not (Test-Path -LiteralPath $python)) {
  throw "Training environment missing. Run .\setup-training-windows.ps1 first."
}
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
$trainerArguments = @(
  $trainer,
  "--dataset", $datasetPath,
  "--device", "cpu",
  "--export-format", "onnx"
)

if ($ValidateOnly) {
  $trainerArguments += "--validate-only"
} else {
  if (-not $Output) { throw "-Output is required unless -ValidateOnly is set." }
  $trainerArguments += @("--output", $Output, "--iterations", [string]$Iterations)
  if ($BaseCheckpoint) {
    $checkpointPath = (Resolve-Path -LiteralPath $BaseCheckpoint).Path
    $trainerArguments += @("--base-checkpoint", $checkpointPath)
  }
}

$previousCudaDevices = $env:CUDA_VISIBLE_DEVICES
$previousTorchHome = $env:TORCH_HOME
try {
  $env:CUDA_VISIBLE_DEVICES = "-1"
  $env:TORCH_HOME = $torchCache
  & $python @trainerArguments
  if ($LASTEXITCODE -ne 0) { throw "Hollow Knight Vision training failed." }
} finally {
  $env:CUDA_VISIBLE_DEVICES = $previousCudaDevices
  $env:TORCH_HOME = $previousTorchHome
}
