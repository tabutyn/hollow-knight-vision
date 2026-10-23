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
foreach ($mapping in $audit.testMappings) {
    if ($mapping.mac -notin $tests.name) { throw "Unknown Mac test mapping: $($mapping.mac)" }
    $parts = $mapping.windows.Split(':', 2)
    $target = Join-Path $repoRoot $parts[0]
    if (-not (Test-Path -LiteralPath $target)) { throw "Missing Windows regression: $target" }
    if ($parts.Length -ne 2 -or (Get-Content -LiteralPath $target -Raw) -notmatch ('\b' + [regex]::Escape($parts[1]) + '\s*\(')) {
        throw "Missing Windows test method: $($mapping.windows)"
    }
    $mapped[$mapping.mac] = $mapping.windows
}
$inventory = @($sources | ForEach-Object {
    $relative = RelativePath $_.FullName
    $review = $reviews[$relative]
    [pscustomobject]@{ source=$relative; status=$(if ($review) { $review.status } else { 'unreviewed' }); notes=$(if ($review) { $review.notes } else { 'No semantic parity review recorded.' }) }
})
$unmapped = @($tests | Where-Object { -not $mapped.ContainsKey($_.name) })
$incompleteSources = @($inventory | Where-Object status -ne 'verified')
$complete = $audit.completion -eq 'complete' -and $incompleteSources.Count -eq 0 -and $unmapped.Count -eq 0
$report = [ordered]@{
    completion=$(if ($complete) { 'complete' } else { 'incomplete' })
    coverageMeaning=$audit.coverageMeaning
    macRuntimeCoverage=$audit.macRuntimeCoverage
    summary=[ordered]@{ sourceFiles=$sources.Count; testFiles=$testFiles.Count; testMethods=$tests.Count; sourcesNeedingVerification=$incompleteSources.Count; explicitlyMappedTests=$mapped.Count; unmappedTests=$unmapped.Count }
    sources=$inventory
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
if ($RequireComplete -and -not $complete) { throw 'Full parity is not proven: source reviews and Mac regression mappings remain incomplete.' }
