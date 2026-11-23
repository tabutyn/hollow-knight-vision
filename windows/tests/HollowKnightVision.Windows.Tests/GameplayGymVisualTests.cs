using HollowKnightVision.Windows.Core;

internal static class GameplayGymVisualTests
{
    private const int Width = 640, Height = 360;
    internal static void TestGamePlayGymUsesVisualBlockadeEvidence()
    {
        var source = new BotVisualGymObservationSource();
        var pixels = Scene();
        BotCaptureObservation? latest = null;
        for (var i = 0; i < 12; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.Right, frame => Detect(frame, true), hud: null).Observation;
        if (latest is not { BlockadeVersion: 1, Blockade.Mark.Direction: 1, BlockadePose.Space: var space }
            || !space.StartsWith("visual/", StringComparison.Ordinal))
            throw new InvalidOperationException("The Game Play Gym observation should recognize a measured stationary push.");
        var history = new BotObservationHistory(BotPolicyContract.LandingVersion);
        var vector = history.Append(latest, new(2, 0, 0, 0), latest.AvailableAt + .01)!;
        var names = BotPolicyContract.Names(BotPolicyContract.LandingVersion)!.ToList();
        if (vector[3 * names.Count + names.IndexOf("blockade_direction")] != 1)
            throw new InvalidOperationException("The visual blockade did not reach the policy input.");
    }

    internal static void TestVisualFootprintAndMaskedKnightRecovery()
    {
        var source = new BotVisualGymObservationSource();
        var pixels = Scene();
        BotVisualGymReading? latest = null;
        for (var i = 0; i < 10; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, true), hud: null);
        if (latest?.Observation is not { } visible || latest.Camera?.GroundVerified != true
            || visible.AtlasView is not { CoveredCells: > 0, Health: null }
            || visible.DetectedTerrain?.Floors.Count is not > 0 || visible.KnightSource != "visual"
            || visible.LifebloodCocoons is not { Count: > 0 })
            throw new InvalidOperationException("Pixel-derived actor and terrain evidence must establish the episode Atlas.");

        var masked = source.Observe(Frame(pixels, 10), 1, InputButtons.None, frame => Detect(frame, false), hud: null);
        if (masked.Observation is not { HasMaskedKnightEvidence: true })
            throw new InvalidOperationException("An occluded Knight lost its fresh floor evidence.");

        var blackout = source.Observe(Frame(new byte[pixels.Length], 11), 1.1, InputButtons.None,
            _ => throw new InvalidOperationException("Inference must skip a complete blackout."), hud: null);
        if (blackout.Observation is not null || blackout.Issues is not ["transition-blackout"])
            throw new InvalidOperationException("A black frame entered policy history.");

        for (var i = 12; i < 21; i++)
            latest = source.Observe(Frame(pixels, i), i / 10d, InputButtons.None, frame => Detect(frame, true), hud: null);
        if (latest?.Observation?.AtlasView is not { CoveredCells: > 0 } || latest.Observation.RoomId == visible.RoomId)
            throw new InvalidOperationException("A recovered room must establish fresh coverage after the blackout.");
    }

    private static BgraFrame Frame(byte[] pixels, int index) =>
        new(Width, Height, Width * 4, pixels, index, DateTimeOffset.UnixEpoch.AddSeconds(index / 10d));

    private static IReadOnlyList<ObjectDetection> Detect(BgraFrame frame, bool knight)
    {
        var result = new List<ObjectDetection>
        {
            new("enemies.crawlid", new(.61, .58, .06, .11), .91f) { SourceFrameIdentifier = frame.CaptureId },
            new("world.geo-deposit", new(.80, .57, .045, .11), .88f) { SourceFrameIdentifier = frame.CaptureId },
            new("world.lifeblood-cacoon", new(.48, .57, .045, .11), .93f) { SourceFrameIdentifier = frame.CaptureId }
        };
        if (knight)
            result.Add(new("game.playable-knight", new(.26, .53, .045, .14), .96f) { SourceFrameIdentifier = frame.CaptureId });
        return result;
    }

    private static byte[] Scene()
    {
        var bytes = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var noise = (x * 23 + y * 37 + x / 13 * 17) % 33;
            var shade = y < 239 ? 20 + noise / 4 : y < 241 ? 120 + noise : 10 + noise;
            if (x is >= 168 and < 194 && y is >= 191 and < 239) shade = 229;
            if (x is >= 394 and < 429 && y is >= 209 and < 240) shade = 159;
            if (x is >= 514 and < 537 && y is >= 211 and < 240) shade = 171;
            var offset = (y * Width + x) * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)shade;
            bytes[offset + 3] = 255;
        }
        return bytes;
    }
}
