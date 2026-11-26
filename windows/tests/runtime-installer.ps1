$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path ([IO.Path]::GetTempPath()) ('hkv runtime install ' + [Guid]::NewGuid().ToString('N'))
$repository = Join-Path $root 'fixture repo'
$fixtureWindows = Join-Path $repository 'windows'
$projectDirectory = Join-Path $repository 'macos/hollow-knight-input-receiver/src'
$game = Join-Path $root 'fixture game'
$managed = Join-Path $game 'hollow_knight_Data/Managed'
$loader = Join-Path $root 'fixture loader'
$installer = Join-Path $fixtureWindows 'install-runtime-windows.ps1'
$receiverName = 'HollowKnightVisionInputReceiver.dll'
$project = Join-Path $projectDirectory 'HollowKnightVisionInputReceiver.csproj'
$source = Join-Path $projectDirectory 'Marker.cs'
$built = Join-Path $projectDirectory "bin/Release/net472/$receiverName"
$installed = Join-Path $managed "Mods/HollowKnightVisionInputReceiver/$receiverName"
$backups = Join-Path $managed '.hkv-windows-backups'
$shell = (Get-Process -Id $PID).Path

function Assert-Case([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Installer([string]$Mode, [string]$Receiver = '') {
    $arguments = @('-NoProfile', '-NonInteractive', '-File', $installer,
        '-Mode', $Mode, '-GameDirectory', $game, '-LoaderDirectory', $loader)
    if ($Receiver) { $arguments += @('-ReceiverDll', $Receiver) }
    $PSNativeCommandUseErrorActionPreference = $false
    $output = & $shell @arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Read-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

try {
    $directories = @($fixtureWindows, $projectDirectory, $managed, $loader,
        (Split-Path -Parent $built), (Split-Path -Parent $installed))
    New-Item -ItemType Directory -Force -Path $directories | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../install-runtime-windows.ps1') -Destination $installer
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../global.json') -Destination $repository
    & git init --quiet $repository
    Assert-Case ($LASTEXITCODE -eq 0) 'Could not initialize the temporary installer checkout.'

    [IO.File]::WriteAllText((Join-Path $game 'hollow_knight.exe'), 'fixture')
    foreach ($name in @('Assembly-CSharp.dll', 'TeamCherry.Localization.dll', 'MMHOOK_Assembly-CSharp.dll')) {
        [IO.File]::WriteAllText((Join-Path $managed $name), "previous $name")
        [IO.File]::WriteAllText((Join-Path $loader $name), "fresh $name")
    }
    foreach ($index in 1..17) {
        [IO.File]::WriteAllText((Join-Path $loader "loader-$index.dll"), "loader fixture $index")
    }
    [IO.File]::WriteAllText($built, 'stale build')
    [IO.File]::WriteAllText($installed, 'previous installation')
    [IO.File]::WriteAllText($project, 'Plan must not compile this project')
    $previousInstalled = Read-Hash $installed
    $staleBuild = Read-Hash $built

    $planRun = Invoke-Installer Plan
    Assert-Case ($planRun.ExitCode -eq 0) "Receiver plan failed. $($planRun.Output)"
    $plan = $planRun.Output | ConvertFrom-Json
    $receiverPlan = $plan.Files[-1]
    Assert-Case ($plan.ReceiverBuildRequired -and $receiverPlan.Source -eq $project -and
        $null -eq $receiverPlan.Sha256) 'Default Plan must identify the receiver source and defer its hash until compilation.'
    Assert-Case ((Read-Hash $installed) -eq $previousInstalled -and (Read-Hash $built) -eq $staleBuild -and
        -not (Test-Path -LiteralPath $backups)) 'Plan must leave the checkout and game fixture untouched.'

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>HollowKnightVisionInputReceiver</AssemblyName>
    <AssemblyVersion>9.17.0.0</AssemblyVersion>
  </PropertyGroup>
  <Target Name="CheckReceiverReferencePaths" BeforeTargets="CoreCompile">
    <Error Condition="!Exists('$(HK_MANAGED_DIR)/TeamCherry.Localization.dll')" Text="Missing game-managed references." />
    <Error Condition="!Exists('$(HK_LOADER_DIR)/MMHOOK_Assembly-CSharp.dll')" Text="Missing loader references." />
  </Target>
</Project>
'@ | Set-Content -LiteralPath $project -Encoding utf8
    [IO.File]::WriteAllText($source, 'public sealed class FreshReceiverFixture { }')

    $installRun = Invoke-Installer Install
    Assert-Case ($installRun.ExitCode -eq 0) "Fresh receiver installation failed. $($installRun.Output)"
    $freshHash = Read-Hash $built
    $freshAssembly = [Reflection.AssemblyName]::GetAssemblyName($installed)
    Assert-Case ($freshAssembly.Version.ToString() -eq '9.17.0.0' -and $freshHash -eq (Read-Hash $installed) -and
        $freshHash -ne $staleBuild) 'Install must copy the current compiled receiver instead of the stale DLL.'

    [IO.File]::WriteAllText($source, 'This source cannot compile')
    $failedRun = Invoke-Installer Install
    Assert-Case ($failedRun.ExitCode -ne 0 -and
        $failedRun.Output -match 'Receiver build failed') "An unsuccessful rebuild must stop before installation. $($failedRun.Output)"
    Assert-Case ((Read-Hash $installed) -eq $freshHash -and (Read-Hash $built) -eq $freshHash -and
        @(Get-ChildItem -LiteralPath $backups -Directory).Count -eq 1) 'A failed build must preserve the previous install and backup.'

    $restoreRun = Invoke-Installer Restore
    Assert-Case ($restoreRun.ExitCode -eq 0) "Receiver restore failed. $($restoreRun.Output)"
    Assert-Case ((Read-Hash $installed) -eq $previousInstalled -and
        -not (Test-Path -LiteralPath (Join-Path $managed 'loader-1.dll'))) 'Restore must replace the receiver and remove added loader files.'

    $explicit = Join-Path $root 'requested receiver.dll'
    Copy-Item -LiteralPath $built -Destination $explicit
    $explicitPlanRun = Invoke-Installer Plan $explicit
    Assert-Case ($explicitPlanRun.ExitCode -eq 0) "Prebuilt receiver plan failed. $($explicitPlanRun.Output)"
    $explicitPlan = $explicitPlanRun.Output | ConvertFrom-Json
    Assert-Case (-not $explicitPlan.ReceiverBuildRequired -and $explicitPlan.Files[-1].Source -eq $explicit -and
        $explicitPlan.Files[-1].Sha256 -eq $freshHash) 'A prebuilt receiver plan must report the requested file and hash.'
    $explicitRun = Invoke-Installer Install $explicit
    Assert-Case ($explicitRun.ExitCode -eq 0 -and
        (Read-Hash $installed) -eq $freshHash) "An explicit DLL install must work without compiling the invalid checkout. $($explicitRun.Output)"

    Write-Host 'PASS Windows runtime installer builds the current receiver, retains explicit DLL mode, and restores the previous runtime'
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
