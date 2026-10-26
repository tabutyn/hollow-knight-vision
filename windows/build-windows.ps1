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
Invoke-Dotnet run --project "$root/tests/HollowKnightVision.Windows.UiTests/HollowKnightVision.Windows.UiTests.csproj" -c $configuration
Invoke-Dotnet run --project "$root/../macos/hollow-knight-input-receiver/tests/HollowKnightVisionInputReceiver.ProtocolTests.csproj" -c $configuration --disable-build-servers
# A dependency can be newer by assembly version but older by file timestamp
# than an existing runtime DLL. PreserveNewest publication otherwise leaves
# stale files (for example System.Text.Json 8 beside a version 9 deps manifest).
# Clean only these generated publish directories before producing the install.
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
foreach ($leaf in @('win-x64', 'app-win-x64')) {
  $target = [IO.Path]::GetFullPath((Join-Path $publishRoot $leaf))
  if (-not $target.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish directory escaped the artifact root: $target"
  }
  if (Test-Path -LiteralPath $target) {
    if (((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Refusing to clean a redirected publish directory: $target"
    }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
      try {
        if (Test-Path -LiteralPath $target) {
          Remove-Item -LiteralPath $target -Recurse -Force
        }
        break
      } catch [IO.IOException] {
        # Windows can briefly retain a directory after its last file is deleted.
        if ($attempt -eq 4) { throw }
        Start-Sleep -Milliseconds 200
      }
    }
  }
}

Invoke-Dotnet publish "$root/src/HollowKnightVision.Windows.Host/HollowKnightVision.Windows.Host.csproj" `
  -c $configuration -r win-x64 --self-contained true -o "$root/artifacts/win-x64"
Invoke-Dotnet publish "$root/src/HollowKnightVision.Windows.App/HollowKnightVision.Windows.App.csproj" `
  -c $configuration -r win-x64 --self-contained true -o "$root/artifacts/app-win-x64"

& "$root/install-app-windows.ps1" -SourceDirectory "$root/artifacts/app-win-x64"
if ($LASTEXITCODE -ne 0) {
  throw "Hollow Knight Vision installation failed with exit code $LASTEXITCODE"
}

Write-Host "Windows host: $root/artifacts/win-x64/HollowKnightVision.Windows.Host.exe"
Write-Host "Windows lab:  $root/artifacts/app-win-x64/HollowKnightVision.Windows.exe"
Write-Host "Start Menu installation updated and verified."
