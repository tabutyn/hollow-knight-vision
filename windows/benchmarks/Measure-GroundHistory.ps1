param([string]$AssemblyDirectory, [string]$Output, [int]$HistoryCount=20000)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory 'ILGPU.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory 'HollowKnightVision.Windows.Core.dll')) | Out-Null
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$tracker=[HollowKnightVision.Windows.Core.GroundHypothesisTracker]::new(0,$true,0,$false)
$type=$tracker.GetType()
$worker=$type.GetField('globalSearchWorker',$flags).GetValue($tracker)
# Model a running worker deterministically; no wall-clock race or queued work.
$worker.GetType().GetField('busy',$flags).SetValue($worker,$true)
$luma=[byte[]]::new(320*100)
$random=[Random]::new(8371);$random.NextBytes($luma)
$lines=[HollowKnightVision.Windows.Core.DetectedFloorLine[]]@([HollowKnightVision.Windows.Core.DetectedFloorLine]::new(40,[HollowKnightVision.Windows.Core.PixelSpan]::new(0,319)))
for($i=0;$i -lt 10;$i++) {$null=$tracker.Update($luma,320,100,$lines,40,300,$i/60.0,$null,$null)}
$bank=$type.GetField('retiredGlobalFeatures',$flags).GetValue($tracker)
$live=$type.GetField('globalFeatures',$flags).GetValue($tracker)
$seed=@($live.Values)[0]
if(-not $seed){throw 'Fixture did not promote ground features.'}
$featureType=$seed.GetType()
for($i=0;$i -lt $HistoryCount;$i++) {
    $feature=$featureType.GetMethod('Copy').Invoke($seed,@())
    $id=$i+100000
    $featureType.GetProperty('Id').SetValue($feature,$id)
    $featureType.GetProperty('WorldX').SetValue($feature,[double]($id*16))
    $bank.Add($id,$feature)
}
$cache=$type.GetField('cachedRelocalizationFeatures',$flags)
if($cache){$cache.SetValue($tracker,$null)}
$samples=for($i=0;$i -lt 110;$i++) {
    $allocated=[GC]::GetAllocatedBytesForCurrentThread()
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $result=$tracker.Update($luma,320,100,$lines,40,300,1+$i/60.0,$null,$null)
    $watch.Stop()
    if($i -ge 10){[pscustomobject]@{Ms=$watch.Elapsed.TotalMilliseconds;Bytes=[GC]::GetAllocatedBytesForCurrentThread()-$allocated;X=$result.CameraPosition.X;Y=$result.CameraPosition.Y;Verified=$result.PoseVerified;History=$result.GlobalFeatureCount;Features=$result.Features.Count}}
}
$times=@($samples.Ms|Sort-Object)
$summary=[pscustomobject]@{HistoryCount=$HistoryCount;Runtime=[Environment]::Version.ToString();MedianMs=$times[50];P95Ms=$times[95];AverageAllocatedBytes=($samples.Bytes|Measure-Object -Average).Average;Verified=($samples|Where-Object Verified).Count;MaxX=($samples.X|Measure-Object -Maximum).Maximum;MaxY=($samples.Y|Measure-Object -Maximum).Maximum;FinalHistory=$samples[-1].History}
@{Summary=$summary;Samples=$samples}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $Output
$summary|ConvertTo-Json
