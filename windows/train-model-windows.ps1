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
. (Join-Path $windowsRoot 'training-environment-windows.ps1')
Wait-HkvTrainingStartSignal
$trainingEnvironment = Get-HkvTrainingEnvironment -WindowsRoot $windowsRoot
$python = $trainingEnvironment.Python
$torchCache = $trainingEnvironment.TorchCache
$trainer = $trainingEnvironment.Trainer

if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
  [Console]::Error.WriteLine("Training environment missing at '$python'. Run .\setup-training-windows.ps1 or set HKV_TRAINER_PYTHON.")
  exit 1
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
$previousPythonEncoding = $env:PYTHONIOENCODING
$previousPythonBuffering = $env:PYTHONUNBUFFERED
$previousOutputEncoding = [Console]::OutputEncoding
try {
  [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
  $env:PYTHONIOENCODING = "utf-8"
  $env:PYTHONUNBUFFERED = "1"
  $env:CUDA_VISIBLE_DEVICES = "-1"
  $env:TORCH_HOME = $torchCache
  & $python @trainerArguments
  $trainerExitCode = $LASTEXITCODE
} finally {
  $env:CUDA_VISIBLE_DEVICES = $previousCudaDevices
  $env:TORCH_HOME = $previousTorchHome
  $env:PYTHONIOENCODING = $previousPythonEncoding
  $env:PYTHONUNBUFFERED = $previousPythonBuffering
  [Console]::OutputEncoding = $previousOutputEncoding
}
if ($trainerExitCode -ne 0) { exit $trainerExitCode }
