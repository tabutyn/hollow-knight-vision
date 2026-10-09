$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$configuration = "Release"
$gitCommonDirectory = & git -C $root rev-parse --path-format=absolute --git-common-dir
if ($LASTEXITCODE -eq 0 -and $gitCommonDirectory) {
  $repositoryRoot = Split-Path -Parent $gitCommonDirectory.Trim()
} else {
  $repositoryRoot = (Resolve-Path (Join-Path $root "..\..\..")).Path
}
$localDotnet = Join-Path $repositoryRoot ".tools\dotnet-8\dotnet.exe"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
  $localDotnet
} elseif ($dotnetCommand) {
  $dotnetCommand.Source
} else {
  throw "Install .NET 8 or place it at $localDotnet."
}

function Invoke-Dotnet {
  & $dotnet @args
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet failed with exit code $LASTEXITCODE"
  }
}

Invoke-Dotnet build "$root/src/HollowKnightVision.Windows.Host/HollowKnightVision.Windows.Host.csproj" -c $configuration
Invoke-Dotnet build "$root/src/HollowKnightVision.Windows.App/HollowKnightVision.Windows.App.csproj" -c $configuration
Invoke-Dotnet run --project "$root/tests/HollowKnightVision.Windows.Tests/HollowKnightVision.Windows.Tests.csproj" -c $configuration
Invoke-Dotnet run --project "$root/../macos/hollow-knight-input-receiver/tests/HollowKnightVisionInputReceiver.ProtocolTests.csproj" -c $configuration --disable-build-servers
Invoke-Dotnet publish "$root/src/HollowKnightVision.Windows.Host/HollowKnightVision.Windows.Host.csproj" `
  -c $configuration -r win-x64 --self-contained true -o "$root/artifacts/win-x64"
Invoke-Dotnet publish "$root/src/HollowKnightVision.Windows.App/HollowKnightVision.Windows.App.csproj" `
  -c $configuration -r win-x64 --self-contained true -o "$root/artifacts/app-win-x64"

Write-Host "Windows host: $root/artifacts/win-x64/HollowKnightVision.Windows.Host.exe"
Write-Host "Windows lab:  $root/artifacts/app-win-x64/HollowKnightVision.Windows.exe"
