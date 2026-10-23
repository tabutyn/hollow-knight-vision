using HollowKnightVision.Windows.Core;

internal static class RenderedFrameMarkerTests
{
    private const int Width = 640;
    private const int Height = 360;

    private static void Check(bool value, string message = "Rendered frame marker parity assertion failed.")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    private static byte[] MarkerPixels(int frame)
    {
        var checksum = 0x5A
            ^ (frame & 255)
            ^ ((frame >> 8) & 255)
            ^ ((frame >> 16) & 255);
        var word = 0xD3UL | ((ulong)frame << 8) | ((ulong)checksum << 32);
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)127);
        for (var bit = 0; bit < 40; bit++)
        {
            var value = (word & (1UL << bit)) == 0 ? (byte)0 : (byte)255;
            for (var y = 0; y < 6; y++)
            {
                for (var x = bit * 3; x < bit * 3 + 3; x++)
                {
                    pixels[y * Width + x] = y < 3 ? value : (byte)(255 - value);
                }
            }
        }
        return pixels;
    }

    public static void TestPixelMarkerRoundTripAndRejectsCorruption()
    {
        foreach (var frame in new[] { 0, 1, 255, 256, 65535, 123456, 0xFFFFFF })
        {
            var pixels = MarkerPixels(frame);
            Check(RenderedFrameMarker.Decode(pixels, Width, Height) == frame);
            pixels[Width + 1] = 127;
            Check(RenderedFrameMarker.Decode(pixels, Width, Height) is null);
        }
        Check(RenderedFrameMarker.Decode(new byte[Width * Height], Width, Height) is null);
    }

    public static void TestHelloMarkerIsExplicitlyOptInAndOldHelloStillDecodes()
    {
        var id = Guid.NewGuid();
        var request = ReceiverCapabilityRequest.Create(id);
        Check(request.RenderFrameMarker is null);
        var old = $"{{\"version\":2,\"type\":\"hello\",\"sessionID\":\"{id}\"}}";
        Check(ReceiverWireCodec.DecodeLine<ReceiverCapabilityRequest>(old) == request);
        Check(ReceiverCapabilityRequest.Create(id, renderFrameMarker: true).RenderFrameMarker == true);
    }

    public static void TestMarkerDecodesDirectlyFromCapturedImage()
    {
        const int frame = 123_456;
        var luma = MarkerPixels(frame);
        var bgra = new byte[Width * Height * BgraFrame.BytesPerPixel];
        for (var index = 0; index < luma.Length; index++)
        {
            var destination = index * BgraFrame.BytesPerPixel;
            bgra[destination] = luma[index];
            bgra[destination + 1] = luma[index];
            bgra[destination + 2] = luma[index];
            bgra[destination + 3] = 255;
        }
        var image = new BgraFrame(
            Width,
            Height,
            Width * BgraFrame.BytesPerPixel,
            bgra,
            captureId: 1,
            observedAt: DateTimeOffset.UnixEpoch);
        Check(RenderedFrameMarker.Decode(image) == frame);
    }
}
