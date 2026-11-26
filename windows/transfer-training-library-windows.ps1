param(
  [Parameter(Mandatory = $true)][ValidateSet('export', 'import')][string]$Operation,
  [Parameter(Mandatory = $true)][string]$Support,
  [Parameter(Mandatory = $true)][string]$Bundle
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'training-environment-windows.ps1')
Wait-HkvTrainingStartSignal
$environment = Get-HkvTrainingEnvironment -WindowsRoot $PSScriptRoot
if (-not (Test-Path -LiteralPath $environment.Python -PathType Leaf)) {
  & (Join-Path $PSScriptRoot 'setup-training-windows.ps1')
  $environment = Get-HkvTrainingEnvironment -WindowsRoot $PSScriptRoot
}
if (-not (Test-Path -LiteralPath $environment.Python -PathType Leaf)) {
  throw 'Vision needs its Python training environment to transfer models.'
}
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$env:PYTHONIOENCODING = 'utf-8'
$script = Join-Path (Split-Path -Parent $environment.Trainer) 'hkv_library_transfer.py'
& $environment.Python -B $script $Operation $Support $Bundle --platform windows
exit $LASTEXITCODE
