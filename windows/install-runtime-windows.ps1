[CmdletBinding()]
param(
    [ValidateSet('Plan', 'Install', 'Restore')]
    [string]$Mode = 'Plan',
    [string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\Hollow Knight',
    [string]$LoaderDirectory,
    [string]$ReceiverDll
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedAssemblyHash = 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD'
$expectedLocalizationHash = '8D932B8CE77668BC59CFA651F1C8634B825300FA4E7DD2B7B23C81B71257EF18'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$commonGitDirectory = (& git -C $scriptRoot rev-parse --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not locate repository root.' }
if (-not [IO.Path]::IsPathRooted($commonGitDirectory)) {
    $commonGitDirectory = [IO.Path]::GetFullPath((Join-Path $scriptRoot $commonGitDirectory))
}
$repositoryRoot = Split-Path -Parent $commonGitDirectory

if ([string]::IsNullOrWhiteSpace($LoaderDirectory)) {
    $LoaderDirectory = Join-Path $repositoryRoot '.tools\hollow-knight-modding-api-unity6\OutputFinal'
}
$buildReceiver = [string]::IsNullOrWhiteSpace($ReceiverDll)
$receiverProject = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\macos\hollow-knight-input-receiver\src\HollowKnightVisionInputReceiver.csproj'))
$receiver = if ($buildReceiver) {
    Join-Path (Split-Path -Parent $receiverProject) 'bin\Release\net472\HollowKnightVisionInputReceiver.dll'
} else {
    [IO.Path]::GetFullPath($ReceiverDll)
}

$game = [IO.Path]::GetFullPath($GameDirectory)
$managed = Join-Path $game 'hollow_knight_Data\Managed'
$gameExecutable = Join-Path $game 'hollow_knight.exe'
$assembly = Join-Path $managed 'Assembly-CSharp.dll'
$localization = Join-Path $managed 'TeamCherry.Localization.dll'
$receiverTarget = Join-Path $managed 'Mods\HollowKnightVisionInputReceiver\HollowKnightVisionInputReceiver.dll'
$backupRoot = Join-Path $managed '.hkv-windows-backups'

if (-not (Test-Path -LiteralPath $gameExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Not a Hollow Knight Windows installation: $game"
}
function Assert-GameStopped {
    if (Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) {
        throw 'Close Hollow Knight before installing or restoring runtime files.'
    }
}
Assert-GameStopped

function Get-Hash([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Build-CurrentReceiver {
    $localDotnet = Join-Path $repositoryRoot '.tools\dotnet-8\dotnet.exe'
    $dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
        $localDotnet
    } else {
        (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    }
    $build = Join-Path ([IO.Path]::GetTempPath()) ('hkv-receiver-build-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $build | Out-Null
    try {
        Push-Location $scriptRoot
        try {
            # Isolate both compiler outputs and intermediates from the previous build.
            $buildArguments = @('build', $receiverProject, '--configuration', 'Release',
                '--artifacts-path', $build, "-p:HK_MANAGED_DIR=$managed", "-p:HK_LOADER_DIR=$loader")
            $PSNativeCommandUseErrorActionPreference = $false
            & $dotnet @buildArguments
            if ($LASTEXITCODE -ne 0) { throw "Receiver build failed with exit code $LASTEXITCODE." }
        } finally {
            Pop-Location
        }

        $output = Join-Path $build 'bin'
        $binaries = @(Get-ChildItem -LiteralPath $output -Recurse -File -Filter 'HollowKnightVisionInputReceiver.dll')
        if ($binaries.Count -ne 1) {
            throw 'Receiver build did not produce a single HollowKnightVisionInputReceiver.dll.'
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $receiver) | Out-Null
        Copy-Item -LiteralPath $binaries[0].FullName -Destination $receiver -Force
    } finally {
        Remove-Item -LiteralPath $build -Recurse -Force
    }
}

function Restore-Backup([string]$Backup) {
    $manifestPath = Join-Path $Backup 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Invalid runtime backup: $Backup"
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    foreach ($relative in @($manifest.addedFiles)) {
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
            throw "Unsafe backup relative path: $relative"
        }
        $target = [IO.Path]::GetFullPath((Join-Path $managed $relative))
        if (-not $target.StartsWith($managed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Backup target escapes Managed: $target"
        }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Backup 'files') -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $Backup 'files'), $file.FullName)
        $target = Join-Path $managed $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

if ($Mode -eq 'Restore') {
    if (-not (Test-Path -LiteralPath $backupRoot -PathType Container)) {
        throw "No runtime backups exist at $backupRoot"
    }
    $latest = Get-ChildItem -LiteralPath $backupRoot -Directory | Sort-Object Name | Select-Object -Last 1
    if ($null -eq $latest) { throw "No runtime backups exist at $backupRoot" }
    Restore-Backup $latest.FullName
    Write-Host "Restored runtime backup: $($latest.FullName)"
    exit 0
}

$loader = [IO.Path]::GetFullPath($LoaderDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $loader 'Assembly-CSharp.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $loader 'MMHOOK_Assembly-CSharp.dll') -PathType Leaf)) {
    throw "Loader output is incomplete: $loader"
}
if (-not $buildReceiver -and -not (Test-Path -LiteralPath $receiver -PathType Leaf)) {
    throw "Receiver DLL missing: $receiver"
}

$loaderFiles = @(Get-ChildItem -LiteralPath $loader -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -in @('Assembly-CSharp.xml', 'TeamCherry.Localization.xml')
} | Sort-Object Name)
if ($loaderFiles.Count -lt 20) { throw "Loader output unexpectedly contains only $($loaderFiles.Count) installable files." }

$currentAssemblyHash = Get-Hash $assembly
$currentLocalizationHash = Get-Hash $localization
$loaderAlreadyPresent = Test-Path -LiteralPath (Join-Path $managed 'MMHOOK_Assembly-CSharp.dll') -PathType Leaf
if (-not $loaderAlreadyPresent -and
    ($currentAssemblyHash -ne $expectedAssemblyHash -or $currentLocalizationHash -ne $expectedLocalizationHash)) {
    throw 'Vanilla game assembly hashes do not match supported Hollow Knight 1.5.12620 / Unity 6000.0.61f1 files.'
}

if ($Mode -eq 'Install' -and $buildReceiver) {
    Build-CurrentReceiver
    Assert-GameStopped
}
$deferredReceiver = $Mode -eq 'Plan' -and $buildReceiver

$plan = @($loaderFiles | ForEach-Object {
    $target = Join-Path $managed $_.Name
    [pscustomobject]@{
        Source = $_.FullName
        Destination = $target
        Action = if (Test-Path -LiteralPath $target) { 'replace' } else { 'add' }
        Sha256 = Get-Hash $_.FullName
    }
}) + [pscustomobject]@{
    Source = if ($deferredReceiver) { $receiverProject } else { $receiver }
    Destination = $receiverTarget
    Action = if (Test-Path -LiteralPath $receiverTarget) { 'replace' } else { 'add' }
    Sha256 = if ($deferredReceiver) { $null } else { Get-Hash $receiver }
}

if ($Mode -eq 'Plan') {
    [pscustomobject]@{
        Mode = 'Plan'
        Game = $game
        Managed = $managed
        VanillaAssemblySha256 = $currentAssemblyHash
        LoaderAlreadyPresent = $loaderAlreadyPresent
        ReceiverBuildRequired = $deferredReceiver
        FileCount = $plan.Count
        Files = $plan
    } | ConvertTo-Json -Depth 5
    exit 0
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'
$backup = Join-Path $backupRoot $stamp
$backupFiles = Join-Path $backup 'files'
New-Item -ItemType Directory -Force -Path $backupFiles | Out-Null
$addedFiles = [Collections.Generic.List[string]]::new()
foreach ($item in $plan) {
    $relative = [IO.Path]::GetRelativePath($managed, $item.Destination)
    if (Test-Path -LiteralPath $item.Destination -PathType Leaf) {
        $backupPath = Join-Path $backupFiles $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
        Copy-Item -LiteralPath $item.Destination -Destination $backupPath
    } else {
        $addedFiles.Add($relative)
    }
}

$manifest = [pscustomobject]@{
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    game = $game
    supportedVersion = '1.5.12620 / Unity 6000.0.61f1'
    vanillaAssemblySha256 = $currentAssemblyHash
    addedFiles = $addedFiles.ToArray()
    files = $plan
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding utf8

try {
    foreach ($file in $loaderFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $managed $file.Name) -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $receiverTarget) | Out-Null
    Copy-Item -LiteralPath $receiver -Destination $receiverTarget -Force
    foreach ($item in $plan) {
        if ((Get-Hash $item.Destination) -ne $item.Sha256) {
            throw "Installed hash mismatch: $($item.Destination)"
        }
    }
} catch {
    Restore-Backup $backup
    throw
}

Write-Host "Installed loader and Vision receiver. Backup: $backup"
