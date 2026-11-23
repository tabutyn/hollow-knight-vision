param(
  [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
  [string]$CoverageDirectory,
  [string]$Coverlet = 'coverlet'
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
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
if ($CoverageDirectory) {
  $CoverageDirectory = (New-Item -ItemType Directory -Path $CoverageDirectory -Force).FullName
  $Coverlet = (Get-Command $Coverlet -ErrorAction Stop).Source
}

function Invoke-TestProject($project, $framework, $report, [switch]$NoBuildServers, [switch]$WpfCoverage) {
  $serverArgs = @()
  if ($NoBuildServers) { $serverArgs += '--disable-build-servers' }
  if (-not $CoverageDirectory) {
    Invoke-Dotnet run --project $project -c $configuration @serverArgs
    return
  }

  $assemblyName = [IO.Path]::GetFileNameWithoutExtension($project)
  $coveragePath = Join-Path $CoverageDirectory "$report.info"
  $staging = $null
  try {
    if ($WpfCoverage) {
      # Cecil resolves the managed assemblies next to the instrumented DLL.
      # Self-contained test staging supplies the WPF framework at that location.
      $staging = Join-Path ([IO.Path]::GetTempPath()) ("hkv-wpf-coverage-" + [Guid]::NewGuid().ToString('N'))
      Invoke-Dotnet publish $project -c $configuration -r win-x64 --self-contained true -o $staging @serverArgs
      $assembly = Join-Path $staging "$assemblyName.dll"
      $testTarget = Join-Path $staging "$assemblyName.exe"
      $targetArgs = @()
    } else {
      Invoke-Dotnet build $project -c $configuration @serverArgs
      $assembly = Join-Path (Split-Path -Parent $project) "bin/$configuration/$framework/$assemblyName.dll"
      $testTarget = $dotnet
      $targetArgs = @('--targetargs', ('"{0}"' -f $assembly))
    }
    & $Coverlet $assembly --target $testTarget @targetArgs `
      --include '[HollowKnightVision*]*' --include-test-assembly `
      --format lcov --output $coveragePath | Tee-Object -Variable testOutput
    if ($LASTEXITCODE -ne 0) {
      throw "Coverage run for $assemblyName failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $coveragePath) -or
        -not (Select-String -LiteralPath $coveragePath -Pattern '^DA:[0-9]+,[1-9][0-9]*' -Quiet)) {
      throw "No executable coverage hits recorded for $assemblyName."
    }
    if ($report -eq 'desktop-core' -and -not ($testOutput -match '^Windows core tests passed: [0-9]+$')) {
      throw 'The Windows core test executable did not finish.'
    }
    if ($WpfCoverage) {
      $records = (Get-Content -LiteralPath $coveragePath -Raw) -split '(?m)^end_of_record\r?$'
      $renderer = $records | Where-Object {
        $_ -match '(?m)^SF:[^\r\n]*[\\/]HollowKnightVision\.Windows\.App[\\/]GroundFeatureOverlayPresentation\.cs\r?$'
      }
      if (-not ($renderer -match '(?m)^DA:[0-9]+,[1-9][0-9]*')) {
        throw "The WPF app was not instrumented or its offscreen renderer did not produce any hits."
      }
    }
  } finally {
    if ($staging -and (Test-Path -LiteralPath $staging)) {
      Remove-Item -LiteralPath $staging -Recurse -Force
    }
  }
}

Invoke-TestProject "$root/tests/HollowKnightVision.Windows.Tests/HollowKnightVision.Windows.Tests.csproj" net8.0 desktop-core
Invoke-TestProject "$root/tests/HollowKnightVision.Windows.UiTests/HollowKnightVision.Windows.UiTests.csproj" net8.0-windows10.0.19041.0 desktop-ui -WpfCoverage
Invoke-TestProject "$root/../macos/hollow-knight-input-receiver/tests/HollowKnightVisionInputReceiver.ProtocolTests.csproj" net8.0 receiver -NoBuildServers
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
