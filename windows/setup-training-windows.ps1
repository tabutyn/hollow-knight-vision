param(
  [string]$Python
)

$ErrorActionPreference = "Stop"
$windowsRoot = $PSScriptRoot
$gitCommonDirectory = & git -C $windowsRoot rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -eq 0 -and $gitCommonDirectory) {
  $repositoryRoot = Split-Path -Parent $gitCommonDirectory.Trim()
} else {
  $repositoryRoot = (Resolve-Path (Join-Path $windowsRoot "..\..\..")).Path
}
$environmentRoot = Join-Path $repositoryRoot ".tools\hkv-training-windows"
$environmentPython = Join-Path $environmentRoot "Scripts\python.exe"
$virtualenvBootstrap = Join-Path $repositoryRoot ".tools\hkv-virtualenv-bootstrap"
$torchCache = Join-Path $repositoryRoot ".tools\hkv-training-cache"
$requirements = Join-Path $windowsRoot "TrainingPython\requirements-windows.txt"
$trainer = Join-Path $windowsRoot "..\macos\TrainingPython\hkv_incremental_trainer.py"

if (-not $Python) {
  $repositoryPython = Join-Path $repositoryRoot ".tools\python311\python.exe"
  $candidatePaths = @(
    $repositoryPython,
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python312\python.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe")
  )
  $command = Get-Command python -ErrorAction SilentlyContinue
  if ($command) { $candidatePaths += $command.Source }
  foreach ($candidatePath in $candidatePaths | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $candidatePath)) { continue }
    & $candidatePath -c "import pip, sys, venv; sys.exit(0 if hasattr(venv, 'EnvBuilder') else 1)"
    if ($LASTEXITCODE -eq 0) {
      $Python = $candidatePath
      break
    }
  }
  if (-not $Python) {
    throw "Complete Python 3.11 or 3.12 is required. Pass -Python C:\path\to\python.exe."
  }
}

if (-not (Test-Path -LiteralPath $environmentPython)) {
  & $Python -c "import sys, venv; sys.exit(0 if hasattr(venv, 'EnvBuilder') else 1)"
  if ($LASTEXITCODE -eq 0) {
    & $Python -m venv $environmentRoot
    if ($LASTEXITCODE -ne 0) { throw "Failed to create training environment." }
  } else {
    if (-not (Test-Path -LiteralPath (Join-Path $virtualenvBootstrap "virtualenv"))) {
      $pipExecutable = Join-Path (Split-Path -Parent $Python) "Scripts\pip.exe"
      if (Test-Path -LiteralPath $pipExecutable) {
        & $pipExecutable install --target $virtualenvBootstrap "virtualenv==20.34.0"
      } else {
        & $Python -m pip install --target $virtualenvBootstrap "virtualenv==20.34.0"
      }
      if ($LASTEXITCODE -ne 0) { throw "Failed to install local virtualenv bootstrap." }
    }
    $previousPythonPath = $env:PYTHONPATH
    $env:PYTHONPATH = if ($previousPythonPath) {
      "$virtualenvBootstrap;$previousPythonPath"
    } else {
      $virtualenvBootstrap
    }
    try {
      & $Python -m virtualenv $environmentRoot
      if ($LASTEXITCODE -ne 0) { throw "Failed to create training environment." }
    } finally {
      $env:PYTHONPATH = $previousPythonPath
    }
  }
}

& $environmentPython -m pip install `
  --index-url https://download.pytorch.org/whl/cpu `
  "torch==2.7.0+cpu" "torchvision==0.22.0+cpu"
if ($LASTEXITCODE -ne 0) { throw "Failed to install CPU-only PyTorch." }

& $environmentPython -m pip install -r $requirements
if ($LASTEXITCODE -ne 0) { throw "Failed to install Windows training dependencies." }

& $environmentPython -c "import onnx, torch, torchvision; assert not torch.cuda.is_available(); print(f'HKV trainer ready: torch={torch.__version__} torchvision={torchvision.__version__} onnx={onnx.__version__} device=cpu')"
if ($LASTEXITCODE -ne 0) { throw "Windows training environment verification failed." }

$previousTorchHome = $env:TORCH_HOME
try {
  $env:TORCH_HOME = $torchCache
  & $environmentPython -c "from torchvision.models import MobileNet_V3_Small_Weights, mobilenet_v3_small; model=mobilenet_v3_small(weights=MobileNet_V3_Small_Weights.IMAGENET1K_V1); print(f'HKV pretrained backbone cached: parameters={sum(value.numel() for value in model.parameters())}')"
  if ($LASTEXITCODE -ne 0) { throw "Failed to cache the pretrained detector backbone." }
} finally {
  $env:TORCH_HOME = $previousTorchHome
}

& $environmentPython $trainer --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Hollow Knight Vision trainer cannot start." }

Write-Host "CPU training Python: $environmentPython"
