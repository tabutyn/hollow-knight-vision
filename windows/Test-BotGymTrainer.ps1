$ErrorActionPreference = 'Stop'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\Hollow Knight Vision'
$training = Join-Path $installed 'training'
& (Join-Path $training 'setup-bot-trainer-windows.ps1')
if ($LASTEXITCODE -ne 0) { throw 'The installed Windows bot environment failed setup.' }
$botPython = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\bot-v1\runtime\.bot-venv\Scripts\python.exe'
$source = Join-Path $PSScriptRoot '..\macos\BotPython'
$detector = Join-Path $PSScriptRoot '..\models\shared-object-model'
& $botPython -B -m unittest discover -s $source -p 'test_*.py'
if ($LASTEXITCODE -ne 0) { throw 'Shared bot tests failed on Windows.' }
& $botPython -B (Join-Path $PSScriptRoot 'tests\gym_trainer_interop.py') $installed $detector
if ($LASTEXITCODE -ne 0) { throw 'Installed Windows Gym runtime or PPO publication failed.' }
$nativeTests = Join-Path $PSScriptRoot 'tests\HollowKnightVision.Windows.Tests\bin\Release\net8.0\HollowKnightVision.Windows.Tests.dll'
& dotnet $nativeTests --train-bot-probe $installed
if ($LASTEXITCODE -ne 0) { throw 'Installed Windows Bot training from captures failed.' }
