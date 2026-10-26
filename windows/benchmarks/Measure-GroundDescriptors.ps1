param([string]$AssemblyDirectory,[string]$Output)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory 'HollowKnightVision.Windows.Core.dll')) | Out-Null
$distance=[HollowKnightVision.Windows.Core.GroundHypothesisTracker].GetMethod('DescriptorDistance',[Reflection.BindingFlags]'Static,NonPublic').CreateDelegate([Func[short[],short[],double]])
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
public static class DescriptorBench {
    public static object Run(Func<short[],short[],double> distance) {
        var random = new Random(817);
        var descriptors = new short[512][];
        for(int d=0;d<descriptors.Length;d++) {
            descriptors[d]=new short[48];
            for(int i=0;i<48;i++) descriptors[d][i]=(short)random.Next(-127,128);
        }
        double RunBatch() {
            double checksum=0;
            for(int a=0;a<512;a++) for(int b=0;b<512;b++) checksum+=distance(descriptors[a],descriptors[b]);
            return checksum;
        }
        for(int n=0;n<20;n++) RunBatch();
        var times=new double[30];double sum=0;
        for(int n=0;n<times.Length;n++) {var watch=Stopwatch.StartNew();sum=RunBatch();times[n]=watch.Elapsed.TotalMilliseconds;}
        Array.Sort(times);
        return new { Comparisons=512*512,MedianMs=times[15],P95Ms=times[28],Checksum=sum,Runtime=Environment.Version.ToString() };
    }
}
'@
$result=[DescriptorBench]::Run($distance)
$result|ConvertTo-Json|Set-Content -LiteralPath $Output
$result|ConvertTo-Json
