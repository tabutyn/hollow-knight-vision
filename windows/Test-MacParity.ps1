param([switch]$Json, [switch]$RequireComplete)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$audit = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mac-parity-audit.json') -Raw | ConvertFrom-Json
function RelativePath([string]$path) { [IO.Path]::GetRelativePath($repoRoot, $path).Replace('\','/') }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'macos/Sources') -Filter '*.swift' -Recurse -File | Sort-Object FullName)
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'macos/Tests') -Filter '*.swift' -Recurse -File | Sort-Object FullName)
$tests = @($testFiles | ForEach-Object {
    $testFile = $_
    $content = Get-Content -LiteralPath $testFile.FullName -Raw
    [regex]::Matches($content, '(?m)^\s*(?:@\w+\s+)*func\s+(test\w+)\s*\(') | ForEach-Object {
        [pscustomobject]@{ name=($testFile.BaseName + '.' + $_.Groups[1].Value); source=(RelativePath $testFile.FullName) }
    }
})
$reviews = @{}
foreach ($review in $audit.sourceReviews) {
    if ($reviews.ContainsKey($review.source)) { throw "Duplicate source review: $($review.source)" }
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $review.source))) { throw "Missing reviewed source: $($review.source)" }
    $reviews[$review.source] = $review
}
$mapped = @{}
$windowsFiles = @{}
$pairedWindows = @{}
foreach ($mapping in $audit.testMappings) {
    if ($mapping.mac -notin $tests.name) { throw "Unknown Mac test mapping: $($mapping.mac)" }
    $parts = $mapping.windows.Split(':', 2)
    $target = Join-Path $repoRoot $parts[0]
    if (-not (Test-Path -LiteralPath $target)) { throw "Missing Windows regression: $target" }
    if (-not $windowsFiles.ContainsKey($target)) { $windowsFiles[$target] = Get-Content -LiteralPath $target -Raw }
    if ($parts.Length -ne 2 -or $windowsFiles[$target] -notmatch ('\b' + [regex]::Escape($parts[1]) + '\s*\(')) {
        throw "Missing Windows test method: $($mapping.windows)"
    }
    if ($mapped.ContainsKey($mapping.mac)) { throw "Duplicate Mac test mapping: $($mapping.mac)" }
    $mapped[$mapping.mac] = $mapping.windows
    $pairedWindows[$mapping.windows] = $true
}
$inventory = @($sources | ForEach-Object {
    $relative = RelativePath $_.FullName
    $review = $reviews[$relative]
    [pscustomobject]@{ source=$relative; status=$(if ($review) { $review.status } else { 'unreviewed' }); notes=$(if ($review) { $review.notes } else { 'No semantic parity review recorded.' }) }
})
$unmapped = @($tests | Where-Object { -not $mapped.ContainsKey($_.name) })
# Count explicitly named Windows test methods. Some UI regressions and helper
# groups use other entry points, so this inventory is separate from runner totals.
$windowsTests = @(foreach ($suite in 'HollowKnightVision.Windows.Tests','HollowKnightVision.Windows.UiTests') {
    $dir = Join-Path $PSScriptRoot "tests/$suite"
    foreach ($file in Get-ChildItem -LiteralPath $dir -Filter '*.cs' -File | Sort-Object Name) {
        $source = RelativePath $file.FullName
        $content = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($method in [regex]::Matches($content, '(?m)^\s*(?:(?:public|private|internal)\s+)?static\s+(?:async\s+)?(?:void|Task)\s+(Test\w+)\s*\(')) {
            "${source}:$($method.Groups[1].Value)"
        }
    }
})
$windowsTests = @($windowsTests | Sort-Object -Unique)
$unmappedWindows = @($windowsTests | Where-Object { -not $pairedWindows.ContainsKey($_) })
$incompleteSources = @($inventory | Where-Object status -ne 'verified')
$complete = $audit.completion -eq 'complete' -and $incompleteSources.Count -eq 0 -and $unmapped.Count -eq 0 -and $unmappedWindows.Count -eq 0
$report = [ordered]@{
    completion=$(if ($complete) { 'complete' } else { 'incomplete' })
    coverageMeaning=$audit.coverageMeaning
    macRuntimeCoverage=$audit.macRuntimeCoverage
    summary=[ordered]@{ sourceFiles=$sources.Count; testFiles=$testFiles.Count; testMethods=$tests.Count; sourcesNeedingVerification=$incompleteSources.Count; explicitlyMappedTests=$mapped.Count; unmappedTests=$unmapped.Count; namedWindowsTestMethods=$windowsTests.Count; windowsMethodsWithMacLinks=($windowsTests.Count - $unmappedWindows.Count); windowsMethodsWithoutMacLinks=$unmappedWindows.Count }
    sources=$inventory
    windowsInventoryScope='Named static Test* methods returning void or Task in top-level Core and UI test project sources. A missing direct link is a review candidate. Other entry points and platform-only checks are included in native runner totals separately.'
    windowsMethodsWithoutMacLinks=$unmappedWindows
    unmappedMacTests=$unmapped
    verifiedRequirements=$audit.verifiedRequirements
    nextAuditAreas=$audit.nextAuditAreas
}
if ($Json) { $report | ConvertTo-Json -Depth 12 }
else {
    Write-Output "Mac/Windows functional parity: $($report.completion)"
    [pscustomobject]$report.summary | Format-List
    Write-Output $report.coverageMeaning
    Write-Output $report.macRuntimeCoverage
    $inventory | Group-Object status | Select-Object Name,Count | Format-Table -AutoSize
}
if ($RequireComplete -and -not $complete) { throw 'Full parity is not proven: source reviews and cross-platform regression mappings remain incomplete.' }
