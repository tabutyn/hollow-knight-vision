param([string]$Game, [string]$Python = $env:HKV_BOT_PYTHON)

$ErrorActionPreference = 'Stop'
if (-not $Game) { throw 'Pass -Game with the modded Hollow Knight executable path.' }
$Game = (Resolve-Path -LiteralPath $Game).Path
if (-not $Python) {
  $Python = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\bot-v1\runtime\.bot-venv\Scripts\python.exe'
}
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
  throw 'Run setup-bot-trainer-windows.ps1 to set up the Windows bot training environment.'
}
$trainer = Join-Path $PSScriptRoot 'BotPython\train_hacker_gym.py'
if (-not (Test-Path -LiteralPath $trainer -PathType Leaf)) {
  $trainer = Join-Path $PSScriptRoot '..\macos\BotPython\train_hacker_gym.py'
}
$vision = Join-Path $env:LOCALAPPDATA 'Programs\Hollow Knight Vision\HollowKnightVision.Windows.Host.exe'
if (-not (Test-Path -LiteralPath $vision -PathType Leaf)) { throw 'Install the current Windows build of Hollow Knight Vision.' }
& $Python -B $trainer --game $Game @args
exit $LASTEXITCODE
