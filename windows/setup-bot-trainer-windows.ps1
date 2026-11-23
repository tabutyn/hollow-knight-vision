param([string]$Python = $env:HKV_BOT_PYTHON)

$ErrorActionPreference = 'Stop'
if (-not $Python) {
  $command = Get-Command python.exe -ErrorAction SilentlyContinue
  if (-not $command) { throw 'Install Python 3.11 or 3.12, or pass -Python with its executable path.' }
  $Python = $command.Source
}
$botSource = Join-Path $PSScriptRoot 'BotPython'
if (-not (Test-Path -LiteralPath $botSource -PathType Container)) {
  $botSource = Join-Path $PSScriptRoot '..\macos\BotPython'
}
$runtime = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\bot-v1\runtime\.bot-venv'
$botPython = Join-Path $runtime 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $botPython -PathType Leaf)) {
  & $Python -m venv $runtime
  if ($LASTEXITCODE -ne 0) { throw 'Could not create the Windows bot training environment.' }
}
& $botPython -m pip install --index-url https://download.pytorch.org/whl/cpu 'torch==2.8.0+cpu'
if ($LASTEXITCODE -ne 0) { throw 'Could not install CPU PyTorch for PPO.' }
& $botPython -m pip install -r (Join-Path $botSource 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Could not install the bot training dependencies.' }
& $botPython -m pip check
if ($LASTEXITCODE -ne 0) { throw 'The bot training environment has incompatible dependencies.' }
& $botPython -B (Join-Path $botSource 'train_hacker_gym.py') --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The Windows Gym trainer cannot start.' }
Write-Host "Windows CPU bot training Python: $botPython"
