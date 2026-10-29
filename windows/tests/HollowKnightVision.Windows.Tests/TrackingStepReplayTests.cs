using System.Text.Json;
using HollowKnightVision.Windows.Core;
internal static class TrackingStepReplayTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static TrackingStepEvidence Evidence(int frame, double truth = 0) => new(new(
        new BgraFrame(64,36,256,new byte[64*36*4],frame,DateTimeOffset.UnixEpoch.AddSeconds(frame/30.0)),
        frame/30.0,"room",[],[],null,false),frame,new(truth,0));
    public static void MaskCheckpointRoundTrip()
    {
        const string json = """{"sceneName":"room","heroX":1,"heroY":2,"sceneMasks":[{"objectPath":"Secret[0]","fsmName":"unmasker","stateName":"Fade","enabled":true,"active":true,"booleans":{"Activated":true},"floats":{"Timer":0.2},"sprites":[{"objectPath":"Secret[0]","alpha":0.25,"enabled":true,"active":true}]}]}""";
        var checkpoint = ReceiverWireCodec.DecodeLine<RecordedGameCheckpoint>(json);
        var restored = ReceiverWireCodec.DecodeLine<RecordedGameCheckpoint>(ReceiverWireCodec.EncodeLine(checkpoint));
        Check(restored.SceneMasks is { Count: 1 } && restored.SceneMasks[0].Sprites[0].Alpha == .25
            && restored.SceneMasks[0].StateName == "Fade" && restored.SceneMasks[0].Booleans["Activated"]
            && restored.SceneMasks[0].Floats["Timer"] == .2, "Checkpoint lost secret-room reveal state.");
        Check(ReceiverWireCodec.DecodeLine<RecordedGameCheckpoint>("""{"sceneName":"room"}""").SceneMasks is null,
            "Legacy checkpoint unexpectedly claimed room-state coverage.");
    }
    public static void ControlledSampling()
    {
        var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>("""{"version":2,"type":"groundTruth","sceneName":"room","heroAvailable":true,"heroX":0,"heroY":10,"grounded":true,"cameraAvailable":true} """);
        var source = new RecordedInputPath(1, Guid.NewGuid(), DateTimeOffset.UtcNow, 2.1, null, null, null,
            [new(.5, RecordedGameButton.Right, RecordedInputTransition.Pressed), new(2.05, RecordedGameButton.Right, RecordedInputTransition.Released)], [],
            [new(0, 0, sample), new(2.1, 2.1, sample with { HeroX = 63 })]);
        var first = ControlledFrameReplay.Frames(source, 0);
        var next = ControlledFrameReplay.Frames(source, 1);
        var tail = ControlledFrameReplay.Frames(source, 2);
        Check(first.Length == 30 && next.Length == 30 && tail.Length == 3, "Controlled cadence lost a simulation tick.");
        Check(Math.Abs(first.Sum(f => f.DeltaSeconds) - 1) < 1e-9 && Math.Abs(tail.Sum(f => f.DeltaSeconds) - .1) < 1e-9, "Simulation duration changed.");
        Check(Math.Abs(first[0].Pose.HeroX - 1) < 1e-8 && Math.Abs(first[^1].Pose.HeroX - 30) < 1e-8, "Endpoint interpolation is incorrect.");
        Check(first[14].Buttons == InputButtons.None && first[15].Buttons == InputButtons.Right && next[0].Buttons == InputButtons.Right,
            "Tick input timing or boundary-held keys changed.");
        var id = Guid.NewGuid(); var session = Guid.NewGuid();
        var command = new ReceiverControlledFrameCommand(2, "controlledFrame", session, id, 1, "advance", 1.0/30, first[0].Buttons,
            ReceiverPlayerPoseCommand.Create(session, 1, first[0].Pose));
        var wire = ReceiverWireCodec.EncodeLine(command);
        Check(!wire.Contains("cameraX") && !wire.Contains("cameraY"), "Controlled replay supplied camera answers to the game.");
        Check(ReceiverWireCodec.DecodeLine<ReceiverControlledFrameCommand>(wire).CommandId == id, "Controlled frame identity lost.");
    }
    public static void FeatureErrorBoundPreservesWinners()
    {
        var method = typeof(GroundHypothesisTracker).GetMethod("AbsolutePatchError",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var random = new Random(173);
        for (var fixture = 0; fixture < 100; fixture++)
        {
            var startX = fixture % 3 == 0 ? 48 : 11; var startY = fixture % 3 == 0 ? 20 : 7;
            var image = new byte[64 * 32]; random.NextBytes(image);
            var patch = new byte[192]; random.NextBytes(patch);
            if (fixture % 2 == 0)
                for (var y = 0; y < 12; y++)
                    image.AsSpan((y + startY) * 64 + startX, 16).CopyTo(patch.AsSpan(y * 16));
            patch[0] = (byte)Math.Min(255, patch[0] + fixture % 13);
            double exact = 0;
            for (var y = 0; y < 12; y++) for (var x = 0; x < 16; x++)
                exact += Math.Abs(patch[y * 16 + x] - image[(y + startY) * 64 + x + startX]);
            exact /= 192;
            foreach (var bound in new[] { 0, .05, 6, 18, exact, exact + .000001, double.PositiveInfinity })
            {
                var result = (double)method.Invoke(null, [patch, image, 64, startX, startY, bound])!;
                Check(exact >= bound ? result >= bound : result == exact,
                    "Bounded feature scoring changed a winning score or rejected a better candidate.");
            }
        }
    }

    public static void SearchCandidateParity()
    {
        var random = new Random(157);
        var solver = new GroundCameraSolver();
        for (var fixture = 0; fixture < 100; fixture++)
        {
            const int width = 257, height = 113;
            var patches = Enumerable.Range(0, 32).Select(_ => new GroundCameraSolver.Patch(
                random.Next(width - 15), random.Next(height - 11), new byte[192])).ToArray();
            var lines = Enumerable.Range(0, 7).Select(_ => new GroundTextureLine(random.Next(height), 0, width - 1)).ToArray();
            StencilRect[] masks = [new(-10.25, 30.5, 50.75, 60.25), new(0, 0, 16, 12),
                new(160.5, -20, 70, 50), new(double.NaN, 0, 12, 12), new(-1e100, 80, 1e100, 30),
                new(random.Next(width), random.Next(height), random.Next(100), random.Next(40))];
            var prediction = random.Next(-350, 350); var radius = random.Next(1, 100);
            int[] dys = [0, -12, 12, random.Next(-150, 150)];
            var actual = solver.BuildSearchCandidates(patches, lines, masks, width, height, dys, prediction, radius);
            var candidate = 0;
            foreach (var dy in dys)
            for (var dx = prediction - radius; dx <= prediction + radius; dx++)
            {
                var expected = new List<GroundPatchSearchSample>();
                for (var sourceIndex = 0; sourceIndex < patches.Length; sourceIndex++)
                {
                    var source = patches[sourceIndex]; var x = source.X + dx; var y = source.Y + dy;
                    if (x < 0 || x + 16 > width || y < 0 || y + 12 > height || masks.Any(r =>
                        r.IsFinite && !r.IsEmpty && r.X < x + 16 && r.Right > x && r.Y < y + 12 && r.Bottom > y)) continue;
                    expected.Add(new(sourceIndex, lines.Any(line => Math.Abs(line.Row - y) <= 3) ? y * width + x : null));
                }
                var value = actual[candidate++];
                Check(value.Dx == dx && value.Dy == dy && value.Samples.SequenceEqual(expected),
                    $"Candidate selection changed at fixture {fixture}, dx {dx}, dy {dy}.");
            }
            Check(candidate == actual.Count, "Candidate count changed.");
        }
    }

    public static void SearchCandidateAllocation()
    {
        var solver = new GroundCameraSolver(broadSearchEveryFrame: true, maximumLocalSearchRadius: 192);
        var patches = Enumerable.Range(0, 32).Select(i => new GroundCameraSolver.Patch(i * 16, 100, new byte[192])).ToArray();
        GroundTextureLine[] lines = [new(100, 0, 639)];
        int[] dys = [-3, -2, -1, 0, 1, 2, 3];
        for (var i = 0; i < 4; i++) solver.BuildSearchCandidates(patches, lines, [], 640, 360, dys, 0, 192);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++) solver.BuildSearchCandidates(patches, lines, [], 640, 360, dys, i, 192);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(allocated < 4096, $"Broad candidate preparation allocated {allocated} bytes after reservation.");
    }

    public static void StrictGate()
    {
        var gate = new TrackingStepGate(0);
        Check(gate.Observe(0,Evidence(1),new(5,0),new(0,0),1,"ground"),"Exactly five pixels must pass.");
        Check(!gate.Observe(1,Evidence(2),new(5.001,0),new(0,0),1,"ground"),"First frame beyond threshold must fail.");
        Check(!gate.Observe(2,Evidence(3),new(0,0),new(0,0),1,"ground"),"Later good frames cannot erase failure.");
        Check(gate.Finish().Scores.Length == 2 && !gate.Finish().Passed,"Stop-at-first-failure lost.");
        var missing = new TrackingStepGate(0);
        Check(!missing.Observe(1,Evidence(1),new(0,0),new(0,0),1,"ground"),"Missing first frame passed.");
        var repeated = new TrackingStepGate(0);
        repeated.Observe(0,Evidence(1),new(0,0),new(0,0),1,"ground");
        Check(!repeated.Observe(1,Evidence(1),new(0,0),new(0,0),1,"ground"),"Repeated image passed.");
        var slow = new TrackingStepGate(0);
        Check(!slow.Observe(0,Evidence(1),new(0,0),new(0,0),34,"ground"),"Over-budget processing passed.");
        var restore = new TrackingStepGate(0, 2, enforceFrameBudget: false);
        Check(restore.Observe(0,Evidence(1),new(0,0),new(0,0),100,"ground"),"Untimed reconstruction acquired a live deadline.");
        Check(!restore.Observe(1,Evidence(2),new(6,0),new(0,0),1,"ground"),"Untimed reconstruction skipped accuracy validation.");
        var drift = new TrackingStepGate(6);
        Check(!drift.Observe(0,Evidence(180,6),new(0,0),new(0,0),1,"ground"),"New boundary erased accumulated error.");
    }
    public static void DurableStash()
    {
        var root=Path.Combine(Path.GetTempPath(),"hkv-step-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new TrackingStepStore(root);
            var cp=JsonSerializer.Deserialize<RecordedGameCheckpoint>("{\"SceneName\":\"room\"}")!;
            var m=store.Initialize("source","conditions",cp,[Evidence(0)]);
            var restored=store.Rehydrate(m);
            Check(restored.Alignment==new AtlasPoint(0,0),"Initial alignment incorrect.");
            var bad=Evidence(1,9);
            // Simulates a previously accepted prefix under a different algorithm.
            var old=new TrackingStepAttempt(0,1,[bad],[new(0,1,0,1,"ground",new(9,0))],null);
            m=store.Accept(m,old,cp);
            var invalidated=false;
            try { store.Rehydrate(m); } catch(InvalidDataException) { invalidated=true; }
            Check(invalidated && store.Load("source","conditions").Accepted.Length==0,"Changed algorithm did not invalidate bad prefix.");
            var incompatible=false;
            try { store.Load("different-source","conditions"); } catch(InvalidDataException) { incompatible=true; }
            Check(incompatible,"Wrong recording reused a stash.");
            var nonfinite = new TrackingStepGate(0);
            nonfinite.Observe(0, Evidence(2), new(double.NaN, 0), new(0,0), 1, "lost");
            store.SaveAttempt(nonfinite.Finish());
            Check(File.ReadAllText(Path.Combine(root,"last-attempt.json")).Contains("NaN"), "Non-finite failure lost its evidence.");
            var file=Path.Combine(root,m.SeedFile);File.AppendAllText(file,"corrupt");
            var corrupt=false;
            try { store.ReadEvidence(m.SeedFile,m.SeedSha256); } catch(InvalidDataException) { corrupt=true; }
            Check(corrupt,"Corrupted frame journal restored.");
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    public static void HeldKeysAcrossBoundary()
    {
        var cp=JsonSerializer.Deserialize<RecordedGameCheckpoint>("{\"SceneName\":\"room\"}")!;
        var source=new RecordedInputPath(1,Guid.NewGuid(),DateTimeOffset.UtcNow,3,null,null,cp,
            [new(.5,RecordedGameButton.Right,RecordedInputTransition.Pressed),new(1.5,RecordedGameButton.Right,RecordedInputTransition.Released)],null);
        var slice=TrackingStepPath.Slice(source,1,cp);
        Check(slice.Events.Count==2 && slice.Events[0].Offset==0 && slice.Events[1].Offset==.5,"Held input did not cross the one-second boundary.");
        Check(slice.Duration==1 && slice.StartCheckpoint==cp,"Slice lost duration/checkpoint.");
        var longer=new RecordedInputPath(1,Guid.NewGuid(),DateTimeOffset.UtcNow,3,null,null,cp,
            [new(.5,RecordedGameButton.Right,RecordedInputTransition.Pressed),new(2.5,RecordedGameButton.Right,RecordedInputTransition.Released)],null);
        var held=TrackingStepPath.Slice(longer,1,cp); held.Validate();
        Check(held.Events.Count==2 && held.Events[^1].Offset==1 && held.Events[^1].Transition==RecordedInputTransition.Released,
            "Step boundary did not release a still-held source key.");
    }
}
