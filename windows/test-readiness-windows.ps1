param(
  [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Hollow Knight',
  [string]$ModelRun
)

$ErrorActionPreference = 'Stop'
$windowsRoot = $PSScriptRoot
$gitCommonDirectory = & git -C $windowsRoot rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -eq 0 -and $gitCommonDirectory) {
  $repositoryRoot = Split-Path -Parent $gitCommonDirectory.Trim()
} else {
  $repositoryRoot = (Resolve-Path (Join-Path $windowsRoot '..\..\..')).Path
}
$managed = Join-Path $GameRoot 'hollow_knight_Data\Managed'
$receiverSource = Join-Path $windowsRoot '..\macos\hollow-knight-input-receiver\src\bin\Release\net472\HollowKnightVisionInputReceiver.dll'
$receiverTarget = Join-Path $managed 'Mods\HollowKnightVisionInputReceiver\HollowKnightVisionInputReceiver.dll'
$loaderSource = Join-Path $repositoryRoot '.tools\hollow-knight-modding-api-unity6\OutputFinal\MMHOOK_Assembly-CSharp.dll'
$loaderTarget = Join-Path $managed 'MMHOOK_Assembly-CSharp.dll'
$python = Join-Path $repositoryRoot '.tools\hkv-training-windows\Scripts\python.exe'
$cliExecutable = Join-Path $windowsRoot 'artifacts\win-x64\HollowKnightVision.Windows.Host.exe'
$app = Join-Path $windowsRoot 'artifacts\app-win-x64\HollowKnightVision.Windows.exe'
if (-not $ModelRun) {
  $smokeRun = Join-Path $repositoryRoot '.tools\hkv-training-smoke-run-20261008'
  if (Test-Path -LiteralPath $smokeRun -PathType Container) { $ModelRun = $smokeRun }
}

$checks = [Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Ok, [string]$Detail) {
  $checks.Add([ordered]@{ name = $Name; ok = $Ok; detail = $Detail })
}
function File-Hash([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
  return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Add-MatchingFileCheck([string]$Name, [string]$Source, [string]$Target) {
  $sourceHash = File-Hash $Source
  $targetHash = File-Hash $Target
  $ok = $null -ne $sourceHash -and $sourceHash -eq $targetHash
  $detail = if (-not $sourceHash) {
    "source missing: $Source"
  } elseif (-not $targetHash) {
    "installed file missing: $Target"
  } elseif ($ok) {
    "sha256=$targetHash"
  } else {
    "hash mismatch: source=$sourceHash installed=$targetHash"
  }
  Add-Check $Name $ok $detail
}

$gameExecutable = Join-Path $GameRoot 'hollow_knight.exe'
Add-Check 'game executable' (Test-Path -LiteralPath $gameExecutable -PathType Leaf) $gameExecutable
Add-MatchingFileCheck 'modding api loader' $loaderSource $loaderTarget
Add-MatchingFileCheck 'vision input receiver' $receiverSource $receiverTarget
Add-Check 'published cli host' (Test-Path -LiteralPath $cliExecutable -PathType Leaf) $cliExecutable
Add-Check 'published desktop lab' (Test-Path -LiteralPath $app -PathType Leaf) $app
Add-Check 'cpu training python' (Test-Path -LiteralPath $python -PathType Leaf) $python

if (Test-Path -LiteralPath $cliExecutable -PathType Leaf) {
  $helpOutput = & $cliExecutable --help
  $helpText = $helpOutput -join "`n"
  $requiredModes = @(
    '--auto-navigate-gameplay',
    '--label-capture',
    '--label-export',
    '--infer-once',
    '--record-atlas',
    '--record-visual-route',
    '--build-route-world',
    '--optimize-world',
    '--replay-input-path'
  )
  $missingModes = @($requiredModes | Where-Object { -not $helpText.Contains($_) })
  $surfaceOk = $LASTEXITCODE -eq 0 -and $missingModes.Count -eq 0
  $surfaceDetail = if ($surfaceOk) {
    'navigation, labels, inference, atlas, route/world, and path replay present'
  } else {
    'missing: ' + ($missingModes -join ', ')
  }
  Add-Check 'published workflow surface' $surfaceOk $surfaceDetail
}

if (Test-Path -LiteralPath $python -PathType Leaf) {
  $pythonStatus = & $python -c "import json, onnx, torch, torchvision; print(json.dumps({'torch': torch.__version__, 'torchvision': torchvision.__version__, 'onnx': onnx.__version__, 'cuda_build': torch.version.cuda, 'cuda_available': torch.cuda.is_available()}))"
  $pythonOk = $LASTEXITCODE -eq 0
  if ($pythonOk) {
    $parsed = $pythonStatus | ConvertFrom-Json
    $pythonOk = $null -eq $parsed.cuda_build -and -not $parsed.cuda_available
  }
  Add-Check 'cpu-only pytorch' $pythonOk ($pythonStatus -join "`n")
}

if ($ModelRun) {
  $model = Join-Path $ModelRun 'Detector.onnx'
  $manifest = Join-Path $ModelRun 'training.json'
  $modelReady = (
    (Test-Path -LiteralPath $model -PathType Leaf) -and
    (Test-Path -LiteralPath $manifest -PathType Leaf)
  )
  Add-Check 'onnx smoke model' $modelReady $ModelRun
  if ($modelReady -and (Test-Path -LiteralPath $cliExecutable -PathType Leaf)) {
    $probe = & $cliExecutable --model-probe $ModelRun
    Add-Check 'published cpu onnx inference' ($LASTEXITCODE -eq 0) ($probe -join "`n")
  }
}

$running = @(Get-Process -Name 'hollow_knight', 'HollowKnightVision.Windows' -ErrorAction SilentlyContinue)
$launchDetail = if ($running.Count -eq 0) {
  'game and desktop lab are not running'
} else {
  'running: ' + (($running | Select-Object -ExpandProperty ProcessName) -join ', ')
}
Add-Check 'launch restriction respected' ($running.Count -eq 0) $launchDetail

$ok = ($checks | Where-Object { -not $_.ok }).Count -eq 0
[ordered]@{
  ok = $ok
  mode = 'readiness'
  checkedAt = [DateTimeOffset]::UtcNow.ToString('o')
  checks = $checks
} | ConvertTo-Json -Depth 5
if (-not $ok) { exit 1 }
