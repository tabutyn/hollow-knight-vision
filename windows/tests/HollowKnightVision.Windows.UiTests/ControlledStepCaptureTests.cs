using System.Reflection;
using HollowKnightVision.Windows.App;
using HollowKnightVision.Windows.Core;

internal static class ControlledStepCaptureTests
{
    internal static void Run() => Task.Run(async () => {
        var type = typeof(MainWindow).GetNestedType("StepCapture", BindingFlags.NonPublic)!;
        using var stop = new CancellationTokenSource();
        var capture = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { 0, 1, stop }, null)!;
        object? Call(string name, params object?[] args) => type.GetMethod(name)!.Invoke(capture, args);
        TrackingStepEvidence Evidence(int id) => new(new(new BgraFrame(64,36,256,new byte[64*36*4],id,DateTimeOffset.UnixEpoch),
            id/30.0,"room",[],[],null,false),id,new(0,0));
        Task Demand(CancellationToken token) => (Task)Call("WaitForCaptureRequestAsync", token)!;
        await Demand(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        for (var i=0; i<20; i++) Call("Observe", Evidence(i), null);
        var preparing = Demand(stop.Token);
        if (preparing.IsCompleted) throw new Exception("Capture kept allocating after enough seed frames arrived.");
        var processor = new TrackingStepProcessor(); _ = processor.Process(Evidence(99).Input);
        Call("Ready", processor, new AtlasPoint(0,0)); Call("Begin", 1.0);
        var command = Guid.NewGuid();
        var tick = new ControlledReplayFrame(0,1.0/30,1.0/30,InputButtons.None,null!);
        var ack = new ReceiverControlledFrameAcknowledgement(2,"controlledFrameAck",Guid.NewGuid(),command,true,100,1.0/30,null);
        var scoring = (Task)Call("ScoreAsync", tick, ack, stop.Token)!;
        await preparing.WaitAsync(TimeSpan.FromSeconds(2));
        Call("Observe", Evidence(100), Guid.NewGuid());
        if (scoring.IsCompleted) throw new Exception("A foreign frame completed the controlled tick.");
        Call("Observe", Evidence(100), command);
        await scoring.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var idle = Demand(cancellation.Token);
        if (idle.IsCompleted) throw new Exception("Capture continued without another controlled frame request.");
        cancellation.Cancel();
        try { await idle; throw new Exception("Capture wait ignored shutdown."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS controlled capture stops during reconstruction, joins the requested tick and cancels idle waits");
    }).GetAwaiter().GetResult();
}
