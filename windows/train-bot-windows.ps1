param(
  [Parameter(Mandatory = $true)][string]$BotRoot,
  [Parameter(Mandatory = $true)][guid]$RunId,
  [Parameter(Mandatory = $true)][string]$Selection,
  [Parameter(Mandatory = $true)][ValidateSet('gameplay', 'hacker')][string]$Domain,
  [Parameter(Mandatory = $true)][string]$Vision,
  [ValidateRange(1, 10000)][int]$Epochs = 160,
  [switch]$UpgradeObservations
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'training-environment-windows.ps1')
Wait-HkvTrainingStartSignal
$trainer = Join-Path $PSScriptRoot 'BotPython\train_bot_job.py'
if (-not (Test-Path -LiteralPath $trainer -PathType Leaf)) {
  $trainer = Join-Path $PSScriptRoot '..\macos\BotPython\train_bot_job.py'
}
$python = $env:HKV_BOT_PYTHON
if (-not $python) {
  $python = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\bot-v1\runtime\.bot-venv\Scripts\python.exe'
  $ready = $false
  if (Test-Path -LiteralPath $python -PathType Leaf) {
    & $python -B -c 'import stable_baselines3'
    $ready = $LASTEXITCODE -eq 0
  }
  if (-not $ready) {
    Write-Output 'Setting up the CPU bot trainer...'
    & (Join-Path $PSScriptRoot 'setup-bot-trainer-windows.ps1')
  }
}
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "Bot training Python was not found at '$python'." }
$trainerArguments = @('-X', 'utf8', '-u', '-B', $trainer,
  '--root', $BotRoot, '--selection', $Selection, '--run-id', $RunId.ToString('D'),
  '--domain', $Domain, '--epochs', [string]$Epochs, '--follow-playback', '--native-verifier', $Vision)
if ($UpgradeObservations) { $trainerArguments += '--upgrade-observations' }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:OMP_NUM_THREADS = '2'
$env:MKL_NUM_THREADS = '2'
$env:MPLCONFIGDIR = Join-Path $BotRoot ('training-runs\' + $RunId.ToString('D') + '\matplotlib')
& $python @trainerArguments
exit $LASTEXITCODE
