param([string]$Output)
$ErrorActionPreference='Stop'
$installed=Join-Path $env:LOCALAPPDATA 'Programs\Hollow Knight Vision'
[Runtime.InteropServices.NativeLibrary]::Load((Join-Path $installed 'onnxruntime.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $installed 'Microsoft.ML.OnnxRuntime.dll')) | Out-Null
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
$references+=Join-Path $installed 'Microsoft.ML.OnnxRuntime.dll'
Add-Type -ReferencedAssemblies $references -CompilerOptions '/nowarn:1701' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
public static class OnnxSpinBench {
    public static object Run(string model, bool spin) {
        using var options = new SessionOptions {
            ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads=Math.Max(1,Environment.ProcessorCount/2),InterOpNumThreads=1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning",spin?"1":"0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning",spin?"1":"0");
        using var session = new InferenceSession(model,options);
        var input=new float[3*360*640];var random=new Random(817);
        for(int i=0;i<input.Length;i++)input[i]=(float)random.NextDouble();
        using var tensor=OrtValue.CreateTensorValueFromMemory(input,new long[]{1,3,360,640});
        var inputs=new Dictionary<string,OrtValue>{{"image",tensor}};
        using var runOptions=new RunOptions();
        string RunFrame() {
            using var outputs=session.Run(runOptions,inputs,new[]{"scores","boxes"});
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach(var output in outputs) hash.AppendData(MemoryMarshal.AsBytes(output.GetTensorDataAsSpan<float>()));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        for(int i=0;i<10;i++){RunFrame();Thread.Sleep(20);}
        var process=Process.GetCurrentProcess();var startCpu=process.TotalProcessorTime.TotalSeconds;
        var elapsed=Stopwatch.StartNew();var times=new double[80];string checksum=null;
        for(int i=0;i<times.Length;i++) {
            var watch=Stopwatch.StartNew();var current=RunFrame();times[i]=watch.Elapsed.TotalMilliseconds;
            if(checksum!=null&&checksum!=current)throw new Exception("Inference changed output between identical frames.");
            checksum=current;
            Thread.Sleep(Math.Max(0,33-(int)watch.ElapsedMilliseconds));
        }
        var cores=(process.TotalProcessorTime.TotalSeconds-startCpu)/elapsed.Elapsed.TotalSeconds;
        Array.Sort(times);
        return new {Spin=spin,MedianMs=times[40],P95Ms=times[76],AverageCpuCores=cores,OutputSha256=checksum,Runtime=Environment.Version.ToString()};
    }
}
'@
$model=Join-Path $installed 'models\shared-object-model\Detector.onnx'
$results=@([OnnxSpinBench]::Run($model,$true),[OnnxSpinBench]::Run($model,$false))
$results|ConvertTo-Json|Set-Content -LiteralPath $Output
$results|ConvertTo-Json
