function Get-HkvTrainingEnvironment {
  param([Parameter(Mandatory = $true)][string]$WindowsRoot)

  $packagedTrainer = Join-Path $WindowsRoot 'TrainingPython\hkv_incremental_trainer.py'
  $isPackaged = Test-Path -LiteralPath $packagedTrainer -PathType Leaf
  $settingsPath = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\training-v1\runtime-settings.json'
  $registeredPython = $null
  if ($isPackaged) {
    # Installed apps need neither the source checkout nor Git to launch training.
    $runtimeRoot = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\training-v1\runtime'
    $trainer = $packagedTrainer
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
      $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
      $registeredPython = $settings.pythonPath
    }
  } else {
    $gitDirectory = & git -C $WindowsRoot rev-parse --path-format=absolute --git-common-dir
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory) { throw "Could not find the training source repository." }
    $runtimeRoot = Split-Path -Parent $gitDirectory.Trim()
    $trainer = Join-Path $WindowsRoot '..\macos\TrainingPython\hkv_incremental_trainer.py'
  }
  $defaultPython = Join-Path $runtimeRoot '.tools\hkv-training-windows\Scripts\python.exe'
  $python = if (-not [string]::IsNullOrWhiteSpace($env:HKV_TRAINER_PYTHON)) { $env:HKV_TRAINER_PYTHON }
    elseif (-not [string]::IsNullOrWhiteSpace($registeredPython)) { $registeredPython }
    else { $defaultPython }
  [pscustomobject]@{
    Root = $runtimeRoot
    Python = $python
    TorchCache = (Join-Path $runtimeRoot '.tools\hkv-training-cache')
    Trainer = $trainer
    IsPackaged = $isPackaged
    SettingsPath = $settingsPath
  }
}
