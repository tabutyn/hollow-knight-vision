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
# Vision assigns this launcher to a Windows job before allowing any child,
# including Git or Python, to start. Standalone CLI use has no start gate.
if ($env:HKV_TRAINER_START_SIGNAL) {
  $trainingStartSignal = [Threading.EventWaitHandle]::OpenExisting($env:HKV_TRAINER_START_SIGNAL)
  try {
    if (-not $trainingStartSignal.WaitOne(30000)) { throw "Vision did not release the training start gate." }
  } finally { $trainingStartSignal.Dispose() }
}
$windowsRoot = $PSScriptRoot
. (Join-Path $windowsRoot 'training-environment-windows.ps1')
$trainingEnvironment = Get-HkvTrainingEnvironment -WindowsRoot $windowsRoot
$python = $trainingEnvironment.Python
$torchCache = $trainingEnvironment.TorchCache
$trainer = $trainingEnvironment.Trainer

if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
  throw "Training environment missing at '$python'. Run .\setup-training-windows.ps1 or set HKV_TRAINER_PYTHON."
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
  if ($LASTEXITCODE -ne 0) { throw "Hollow Knight Vision training failed." }
} finally {
  $env:CUDA_VISIBLE_DEVICES = $previousCudaDevices
  $env:TORCH_HOME = $previousTorchHome
  $env:PYTHONIOENCODING = $previousPythonEncoding
  $env:PYTHONUNBUFFERED = $previousPythonBuffering
  [Console]::OutputEncoding = $previousOutputEncoding
}
