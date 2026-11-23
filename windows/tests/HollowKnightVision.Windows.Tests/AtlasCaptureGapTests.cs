using HollowKnightVision.Windows.Core;

internal static class AtlasCaptureGapTests
{
    public static void TestOneMissCanReconnectOnlyToTheRetainedCapture()
    {
        var finder = new AtlasCaptureGap();
        var reference = Frame(0, 1);
        var missed = Frame(0, 2);
        var arriving = Frame(-10, 3);
        var origin = new AtlasPoint(24, 31);
        void StartGap()
        {
            finder.Reset();
            Check(finder.Complete(reference, [], null, 1, "room", 1, origin,
                currentReferenceAccepted: true, continuedAcrossGap: false, atlasFrameHeld: false)
                == AtlasCaptureGapState.None, "The measured capture should be stored.");
            Check(finder.Complete(missed, [], null, 1, "room", 1.03, origin,
                currentReferenceAccepted: false, continuedAcrossGap: false, atlasFrameHeld: true)
                == AtlasCaptureGapState.AwaitingRegistration, "One held frame should keep the exact reference.");
        }

        StartGap();
        Check(finder.Match(arriving, [], null, 2, "room", 1.06) is null, "The old capture generation leaked.");
        Check(finder.Match(arriving, [], null, 1, "other", 1.06) is null, "The old room reference leaked.");
        Check(finder.Match(arriving, [], null, 1, "room", 1.16) is null, "The old image exceeded its age limit.");
        var match = finder.Match(arriving, [], null, 1, "room", 1.06);
        Check(match is { ReferenceTimestamp: 1, Confidence: >= .8f }
            && Math.Abs(match.CameraPosition.X - origin.X - 10) < 1.5
            && Math.Abs(match.CameraPosition.Y - origin.Y) < 1.5,
            $"Expected the skip-frame displacement to use the accepted pose, received {match}.");
        Check(finder.Complete(arriving, [], null, 1, "room", 1.06, match!.CameraPosition,
            currentReferenceAccepted: true, continuedAcrossGap: true, atlasFrameHeld: false)
            == AtlasCaptureGapState.None, "The proven registration should keep the placed world.");

        StartGap();
        Check(finder.Complete(arriving, [], null, 1, "room", 1.06, origin,
            currentReferenceAccepted: true, continuedAcrossGap: false, atlasFrameHeld: false)
            == AtlasCaptureGapState.RequiresRecovery, "A relative pose after a lost capture must prove its reference.");
        StartGap();
        Check(finder.Complete(missed, [], null, 1, "room", 1.06, origin,
            currentReferenceAccepted: false, continuedAcrossGap: false, atlasFrameHeld: true)
            == AtlasCaptureGapState.RequiresRecovery, "A second consecutive miss must release the old placement.");
        Check(finder.Match(arriving, [], null, 1, "room", 1.08) is null, "Released placement left a usable reference.");
    }

    private static BgraFrame Frame(int offset, long id)
    {
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var col = (x - offset + 30) / 10;
            var row = y / 10;
            var sample = (byte)(25 + (col * 37 + row * 61 + col * row * 3) % 190);
            var index = (y * width + x) * 4;
            pixels[index] = sample;
            pixels[index + 1] = sample;
            pixels[index + 2] = sample;
            pixels[index + 3] = 255;
        }
        return new(width, height, width * 4, pixels, id, DateTimeOffset.UnixEpoch);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
