param(
  [string]$SourceDirectory = (Join-Path $PSScriptRoot 'artifacts\app-win-x64'),
  [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Hollow Knight Vision'),
  [string]$TrainingPython = $env:HKV_TRAINER_PYTHON
)

$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$sourceExecutable = Join-Path $source 'HollowKnightVision.Windows.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
  throw "Published Hollow Knight Vision executable not found at $sourceExecutable."
}

$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$install = [IO.Path]::GetFullPath($InstallDirectory)
$programsPrefix = $programsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $install.StartsWith($programsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to install outside $programsRoot."
}
if (Test-Path -LiteralPath $install) {
  $installItem = Get-Item -LiteralPath $install -Force
  if (($installItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing to update reparse-point installation directory $install."
  }
}

$installedExecutable = Join-Path $install 'HollowKnightVision.Windows.exe'
$running = @(Get-CimInstance Win32_Process -Filter "Name='HollowKnightVision.Windows.exe'" |
  Where-Object {
    $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
      $installedExecutable,
      [StringComparison]::OrdinalIgnoreCase)
  })
if ($running.Count -gt 0) {
  $processIDs = ($running.ProcessId | Sort-Object) -join ', '
  throw "Close the installed Hollow Knight Vision before building (process $processIDs). The build will not leave the Start Menu installation stale."
}

New-Item -ItemType Directory -Path $install -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath $source -File -Recurse)
foreach ($file in $sourceFiles) {
  $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
  $destination = Join-Path $install $relative
  $destinationParent = Split-Path -Parent $destination
  New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
  Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}

$startMenu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$shortcutPath = Join-Path $startMenu 'Hollow Knight Vision.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
  $shortcut = $shell.CreateShortcut($shortcutPath)
  $shortcut.TargetPath = $installedExecutable
  $shortcut.WorkingDirectory = $install
  $shortcut.IconLocation = "$installedExecutable,0"
  $shortcut.Save()
} finally {
  if ($null -ne $shell) {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
  }
}

foreach ($file in $sourceFiles) {
  $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
  $destination = Join-Path $install $relative
  if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
    throw "Installed file is missing after deployment: $destination"
  }
  $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
  $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
  if ($sourceHash -ne $installedHash) {
    throw "Installed file does not match the published build: $relative"
  }
}

$shortcut = $shell = New-Object -ComObject WScript.Shell
try {
  $installedShortcut = $shortcut.CreateShortcut($shortcutPath)
  if (-not [IO.Path]::GetFullPath($installedShortcut.TargetPath).Equals(
      $installedExecutable,
      [StringComparison]::OrdinalIgnoreCase)) {
    throw "Start Menu shortcut does not target the installed build."
  }
} finally {
  if ($null -ne $shortcut) {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
  }
}

# Keep workstation interpreter paths in user settings, never in distributable artifacts.
# Future installs preserve this registration when no explicit interpreter is supplied.
if ($TrainingPython) {
  if (-not (Test-Path -LiteralPath $TrainingPython -PathType Leaf)) { throw "Training Python not found: $TrainingPython" }
  $trainingSettings = Join-Path $env:LOCALAPPDATA 'HollowKnightVision\training-v1\runtime-settings.json'
  New-Item -ItemType Directory -Path (Split-Path -Parent $trainingSettings) -Force | Out-Null
  @{ pythonPath = [IO.Path]::GetFullPath($TrainingPython) } | ConvertTo-Json | Set-Content -LiteralPath $trainingSettings -Encoding UTF8
}

$appAssembly = Join-Path $install 'HollowKnightVision.Windows.dll'
$appHash = (Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash
Write-Host "Installed Hollow Knight Vision: $installedExecutable"
Write-Host "Installed app assembly SHA-256: $appHash"
